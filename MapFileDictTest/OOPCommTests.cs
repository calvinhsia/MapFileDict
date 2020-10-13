using MapFileDict;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MapFileDictTest
{
    [TestClass]
    public class OOPCommTests : MyTestBase
    {
        [TestMethod]
        public async Task OOPSendAndQuery()
        {
            await Task.Yield();
            try
            {
                //var pidClient = Process.GetCurrentProcess().Id;
                //var procServer = OutOfProc.CreateServer(pidClient);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.ToString());
                throw;
            }
        }

        [TestMethod]
        public async Task OOPTest()
        {
            try
            {
                await Task.Yield();
                var outputLogFile = TestContext.Properties[ContextPropertyLogFile] as string;
                Trace.WriteLine($"Log = {outputLogFile}");
                MyClassThatRunsIn32and64bit.CreateAndRun(outputLogFile);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.ToString());
                throw;
            }
        }


        [TestMethod]
        public async Task OOPTestConsoleApp()
        {
            var consapp = "ConsoleAppTest.exe";
            var pidClient = Process.GetCurrentProcess().Id;

            var procServer = Process.Start(consapp, $"{pidClient}");
            Trace.WriteLine($"Client: started server {procServer.Id}");
            await DoTheGenericServerStuffAsync(pidClient, procServer);

            VerifyLogStrings(@"
IntPtr.Size = 4 Shared Memory region address
IntPtr.Size = 8 Shared Memory region address
");
        }

        [TestMethod]
        public async Task OOPTestGenAsm()
        {
            var pidClient = Process.GetCurrentProcess().Id;

            var procServer = OutOfProc.CreateServer(pidClient);
            Trace.WriteLine($"Client: started server PidClient={pidClient} PidServer={procServer.Id}");
            await DoTheGenericServerStuffAsync(pidClient, procServer);

            VerifyLogStrings(@"
IntPtr.Size = 4 Shared Memory region address
IntPtr.Size = 8 Shared Memory region address
");
        }

        private async Task DoTheGenericServerStuffAsync(int pidClient, Process procServer)
        {
            await DoServerStuff(procServer, pidClient, async (pipeClient, oop) =>
            {
                //                    await Task.Delay(5000);
                var verb = new byte[2] { 1, 1 };

                for (int i = 0; i < 5; i++)
                {
                    verb[0] = (byte)Verbs.verbRequestData;
                    await pipeClient.WriteAsync(verb, 0, 1);
                    var bufReq = new byte[100];
                    var buflen = await pipeClient.ReadAsync(bufReq, 0, bufReq.Length);
                    var readStr = Encoding.ASCII.GetString(bufReq, 0, buflen);
                    Trace.WriteLine($"Client req data from server: {readStr}");
                }

                Trace.WriteLine($"Client: GetLog");
                verb[0] = (byte)Verbs.verbGetLog;
                await pipeClient.WriteAsync(verb, 0, 1);
                await pipeClient.GetAckAsync();
                var logstrs = Marshal.PtrToStringAnsi(oop.mappedSection);
                Trace.WriteLine($"Got log from server\r\n" + logstrs);


                Trace.WriteLine($"Client: sending quit");
                verb[0] = (byte)Verbs.verbQuit;
                await pipeClient.WriteAsync(verb, 0, 1);
                await pipeClient.GetAckAsync();
            });
        }

        private async Task DoServerStuff(Process procServer, int pidClient, Func<NamedPipeClientStream, OutOfProc, Task> func)
        {
            var sw = Stopwatch.StartNew();
            var cts = new CancellationTokenSource();

            using (var oop = new OutOfProc(pidClient, cts.Token))
            {
                using (var pipeClient = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: oop.pipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous))
                {
                    Trace.WriteLine($"Client: starting to connect");
                    await pipeClient.ConnectAsync(cts.Token);
                    Trace.WriteLine($"Client: connected");
                    await func(pipeClient, oop);
                }
            }
            while (!procServer.HasExited)
            {
                Trace.WriteLine($"Waiting for cons app to exit");
                await Task.Delay(TimeSpan.FromMilliseconds(1000));
                if (!Debugger.IsAttached && sw.Elapsed.TotalSeconds > 10)
                {
                    Trace.WriteLine($"Killing server process");
                    procServer.Kill();
                    break;
                }
            }
            Trace.WriteLine($"Done in {sw.Elapsed.TotalSeconds:n2}");
        }

        [TestMethod]
        public async Task OOPTestInProc()
        {
            var cts = new CancellationTokenSource();
            using (var oop = new OutOfProc(Process.GetCurrentProcess().Id, cts.Token))
            {
                oop.SetChunkSize(1024 * 1024 * 1024);
                Trace.WriteLine($"Mapped Section {oop.mappedSection} 0x{oop.mappedSection.ToInt32():x8}");

                var taskServer = oop.DoServerLoopAsync();

                var taskClient = DoTestClientAsync(oop);
                var tskDelay = Task.Delay(TimeSpan.FromSeconds(Debugger.IsAttached ? 3000 : 13));
                await Task.WhenAny(new[] { tskDelay, taskClient });
                if (tskDelay.IsCompleted)
                {
                    Trace.WriteLine($"Delay completed: cancelling server");
                    cts.Cancel();
                }
                await taskServer;
                Trace.WriteLine($"Done");
                Assert.IsTrue(taskServer.IsCompleted);
            }
            VerifyLogStrings(@"
Server: SharedMemStr StrSharedMem
Client: sending quit
Server got quit message
sent message..requesting data
");
        }

        private async Task DoTestClientAsync(OutOfProc oop)
        {
            Trace.WriteLine("Starting Client");
            var cts = new CancellationTokenSource();
            using (var pipeClient = new NamedPipeClientStream(
                serverName: ".",
                pipeName: oop.pipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous))
            {
                await pipeClient.ConnectAsync(cts.Token);
                var verb = new byte[2] { 1, 1 };
                for (int i = 0; i < 5; i++)
                {
                    {
                        var strBuf = Encoding.ASCII.GetBytes($"MessageString {i}");
                        var buf = new byte[strBuf.Length + 1];
                        buf[0] = (byte)Verbs.verbString;
                        Array.Copy(strBuf, 0, buf, 1, strBuf.Length);
                        Trace.WriteLine("Client: sending message");
                        await pipeClient.WriteAsync(buf, 0, buf.Length);
                        Trace.WriteLine($"Client: sent message..requesting data");
                    }
                    {
                        var strBuf = Encoding.ASCII.GetBytes($"StrSharedMem {i}");
                        verb[0] = (byte)Verbs.verbStringSharedMem;
                        Marshal.WriteInt32(oop.mappedSection, strBuf.Length);
                        Marshal.Copy(strBuf, 0, oop.mappedSection + IntPtr.Size, strBuf.Length);
                        await pipeClient.WriteAsync(verb, 0, 1);
                    }
                    {
                        verb[0] = (byte)Verbs.verbRequestData;
                        await pipeClient.WriteAsync(verb, 0, 1);
                        var bufReq = new byte[100];
                        var buflen = await pipeClient.ReadAsync(bufReq, 0, bufReq.Length);
                        var readStr = Encoding.ASCII.GetString(bufReq, 0, buflen);
                        Trace.WriteLine($"Client req data from server: {readStr}");
                    }
                }
                {
                    // speedtest
                    var nIter = 10;
                    var bufSpeed = new byte[oop.chunkSize];
                    bufSpeed[0] = (byte)Verbs.verbSpeedTest;
                    var sw = Stopwatch.StartNew();
                    for (int iter = 0; iter < nIter; iter++)
                    {
                        Trace.WriteLine($"Sending chunk {iter}");
                        await pipeClient.WriteAsync(bufSpeed, 0, bufSpeed.Length);
                    }
                    var bps = (double)oop.chunkSize * nIter / sw.Elapsed.TotalSeconds;
                    Trace.WriteLine($"BytesPerSec = {bps:n0}"); // 1.4 G/Sec
                }
//                if (oop.option == OOPOption.InProcTestLogging)
                {
                    Trace.WriteLine($"getting log from server");
                    verb[0] = (byte)Verbs.verbGetLog;
                    await pipeClient.WriteAsync(verb, 0, 1);
                    await pipeClient.GetAckAsync();
                    var logstrs = Marshal.PtrToStringAnsi(oop.mappedSection);
                    Trace.WriteLine($"Got log from server\r\n" + logstrs);

                }
                Trace.WriteLine($"Client: sending quit");
                verb[0] = (byte)Verbs.verbQuit;
                await pipeClient.WriteAsync(verb, 0, 1);
                await pipeClient.GetAckAsync();
            }
        }
    }
}
