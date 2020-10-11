using MapFileDict;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MapFileDictTest
{
    [TestClass]
    public class OOPComm : MyTestBase
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
        public async Task PipeTesting()
        {
            await Task.Yield();
            var tcs = new TaskCompletionSource<object>();
            var cts = new CancellationTokenSource();
            var pipeName = "MyTestPipe";
            var taskServer = Task.Run(async () =>
                {
                    try
                    {
                        Trace.WriteLine("Server: Starting ");
                        var pipeServer = new NamedPipeServerStream(
                            pipeName: pipeName,
                            direction: PipeDirection.InOut,
                            maxNumberOfServerInstances: 1,
                            transmissionMode: PipeTransmissionMode.Message,
                            options: PipeOptions.Asynchronous
                            );
                        await pipeServer.WaitForConnectionAsync(cts.Token);

                        var buff = new byte[100];
                        var receivedQuit = false;
                        while (!receivedQuit)
                        {
                            if (cts.IsCancellationRequested)
                            {
                                Trace.WriteLine("server: got cancl");
                                receivedQuit = true;
                            }
                            var nBytesRead = await pipeServer.ReadAsync(buff, 0, 10, cts.Token);
                            switch ((Verbs)buff[0])
                            {
                                case Verbs.verbQuit:
                                    Trace.WriteLine($"Server got quit message");
                                    receivedQuit = true;
                                    break;
                                case Verbs.verbString:
                                    var bufLen = buff[1];
                                    var strbuf = new byte[bufLen];
                                    await pipeServer.ReadAsync(strbuf, 0, bufLen, cts.Token);
                                    var strRead = Encoding.ASCII.GetString(strbuf);
                                    Trace.WriteLine($"Server Got str {strRead}");
                                    break;
                                case Verbs.verbRequestData:
                                    var strB = Encoding.ASCII.GetBytes($"Server: {DateTime.Now}");
                                    await pipeServer.WriteAsync(strB, 0, strB.Length);
                                    break;
                            }

                            Trace.WriteLine($"Server: got bytes = {nBytesRead}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Trace.WriteLine("Server: cancelled");
                    }
                    Trace.WriteLine("Server: exiting servertask");

                }, cts.Token);

            var taskClient = Task.Run(async () =>
            {
                Trace.WriteLine("Starting Client");
                var pipeClient = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: pipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous);
                await pipeClient.ConnectAsync(cts.Token);
                var verb = new byte[2] { 1, 1 };
                for (int i = 0; i < 10; i++)
                {
                    var strBuf = Encoding.ASCII.GetBytes($"MessageString {i}");
                    Trace.WriteLine("Client: sending message");
                    verb[0] = (byte)Verbs.verbString;
                    verb[1] = (byte)strBuf.Length;
                    await pipeClient.WriteAsync(verb, 0, verb.Length);
                    await pipeClient.WriteAsync(strBuf, 0, strBuf.Length);

                    verb[0] = (byte)Verbs.verbRequestData;
                    await pipeClient.WriteAsync(verb, 0, verb.Length);
                    var bufReq = new byte[100];
                    var buflen = await pipeClient.ReadAsync(bufReq, 0, bufReq.Length);
                    var readStr = Encoding.ASCII.GetString(bufReq, 0, buflen);
                    Trace.WriteLine($"Client req data from server: {readStr}");
                }
                Trace.WriteLine($"Client: sending quit");
                verb[0] = (byte)Verbs.verbQuit;
                await pipeClient.WriteAsync(verb, 0, verb.Length);

            });

            var tskDelay = Task.Delay(TimeSpan.FromSeconds(10));
            await Task.WhenAny(new[] { tskDelay, taskServer });
            if (tskDelay.IsCompleted)
            {
                Trace.WriteLine($"Delay completed: cancelling server");
                cts.Cancel();
                await taskServer;
            }
            Trace.WriteLine($"Done");


        }
        public enum Verbs
        {
            verbQuit,
            verbString,
            verbRequestData
        }
    }
}
