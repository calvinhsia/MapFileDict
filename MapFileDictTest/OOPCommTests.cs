using MapFileDict;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
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
        public async Task OOPTest()
        {
            Trace.WriteLine($"Start {nameof(OOPTest)}");
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
        public async Task OOPPipeTesting()
        {
            var sharedFileMapName = $"MapFileDict{Process.GetCurrentProcess().Id}\0";
            var cts = new CancellationTokenSource();
            var pipeName = "MyTestPipe";
            var sharedMapSize = 65536U;
            using (var mmf = MemoryMappedFile.CreateNew(
                mapName: sharedFileMapName,
                capacity: sharedMapSize,
                access: MemoryMappedFileAccess.ReadWrite,
                options: MemoryMappedFileOptions.None,
                inheritability: HandleInheritability.None
                ))
            using (var mmfView = mmf.CreateViewAccessor(
                                offset: 0,
                                size: 0,
                                access: MemoryMappedFileAccess.ReadWrite))
            {
                var mappedSection = mmfView.SafeMemoryMappedViewHandle.DangerousGetHandle();
                var oop = new OutOfProc();
                await oop.CreateServer(pipeName, mappedSection, cts.Token);
            }
        }

        [TestMethod]
        public async Task PipeTesting()
        {
            var oop = new OutOfProc(chunkSize: 1024 * 1024 * 1024);
            Trace.WriteLine($"Mapped Section {oop.mappedSection} 0x{oop.mappedSection.ToInt32():x8}");

            var taskServer = oop.CreateServer(oop.pipeName, oop.mappedSection, oop.cts.Token);

            var taskClient = Task.Run(async () =>
            {
                Trace.WriteLine("Starting Client");
                using (var pipeClient = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: oop.pipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous))
                {
                    await pipeClient.ConnectAsync(oop.cts.Token);
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

                    Trace.WriteLine($"Client: sending quit");
                    verb[0] = (byte)Verbs.verbQuit;
                    await pipeClient.WriteAsync(verb, 0, 1);
                }
            });

            var tskDelay = Task.Delay(TimeSpan.FromSeconds(Debugger.IsAttached ? 3000 : 13));
            await Task.WhenAny(new[] { tskDelay, taskClient});
            if (tskDelay.IsCompleted)
            {
                Trace.WriteLine($"Delay completed: cancelling server");
                oop.cts.Cancel();
                await taskServer;
            }
            oop.mmfView.Dispose();
            oop.mmf.Dispose();
            Trace.WriteLine($"Done");
        }
    }
}
