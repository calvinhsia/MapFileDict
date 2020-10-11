﻿using MapFileDict;
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
            var sharedFileMapName = $"MapFileDict{Process.GetCurrentProcess().Id}\0";
            var sharedMapSize = 65536U;
            var mmf = MemoryMappedFile.CreateNew(
                mapName: sharedFileMapName,
                capacity: sharedMapSize,
                access: MemoryMappedFileAccess.ReadWrite,
                options: MemoryMappedFileOptions.None,
                inheritability: HandleInheritability.None
                );
            var mmfView = mmf.CreateViewAccessor(
                offset: 0,
                size: 0,
                access: MemoryMappedFileAccess.ReadWrite);
            var mappedSection = mmfView.SafeMemoryMappedViewHandle.DangerousGetHandle();
            await Task.Yield();
            var tcs = new TaskCompletionSource<object>();
            var cts = new CancellationTokenSource();
            var pipeName = "MyTestPipe";
            var chunkSize = 1024 * 1024 * 1024;
            var speedBuf = new byte[chunkSize];
            Trace.WriteLine($"Mapped Section {mappedSection} 0x{mappedSection.ToInt32():x8}");
            var taskServer = Task.Run(async () =>
                {
                    try
                    {
                        Trace.WriteLine("Server: Starting ");
                        using (var pipeServer = new NamedPipeServerStream(
                            pipeName: pipeName,
                            direction: PipeDirection.InOut,
                            maxNumberOfServerInstances: 1,
                            transmissionMode: PipeTransmissionMode.Message,
                            options: PipeOptions.Asynchronous
                            ))
                        {
                            await pipeServer.WaitForConnectionAsync(cts.Token);

                            var buff = new byte[100];
                            var receivedQuit = false;
                            using (var ctsReg = cts.Token.Register(pipeServer.Disconnect))
                            {
                                while (!receivedQuit)
                                {
                                    if (cts.IsCancellationRequested)
                                    {
                                        Trace.WriteLine("server: got cancel");
                                        receivedQuit = true;
                                    }
                                    var nBytesRead = await pipeServer.ReadAsync(buff, 0, 1, cts.Token);
                                    switch ((Verbs)buff[0])
                                    {
                                        case Verbs.verbQuit:
                                            Trace.WriteLine($"Server got quit message");
                                            receivedQuit = true;
                                            break;
                                        case Verbs.verbStringSharedMem:
                                            var len = Marshal.ReadIntPtr(mappedSection);
                                            var str = Marshal.PtrToStringAnsi(mappedSection + IntPtr.Size, len.ToInt32());
                                            Trace.WriteLine($"Server: SharedMemStr {str}");
                                            break;
                                        case Verbs.verbString:
                                            var lstBytes = new List<byte>();
                                            while (!pipeServer.IsMessageComplete)
                                            {
                                                var byt = pipeServer.ReadByte();
                                                lstBytes.Add((byte)byt);
                                            }
                                            //var bufLen = buff[1];
                                            //var strbuf = new byte[bufLen];
                                            //await pipeServer.ReadAsync(strbuf, 0, bufLen, cts.Token);
                                            var strRead = Encoding.ASCII.GetString(lstBytes.ToArray());
                                            Trace.WriteLine($"Server Got str {strRead}");
                                            break;
                                        case Verbs.verbRequestData:
                                            var strB = Encoding.ASCII.GetBytes($"Server: {DateTime.Now}");
                                            await pipeServer.WriteAsync(strB, 0, strB.Length);
                                            break;
                                        case Verbs.verbSpeedTest:
                                            {
                                                await pipeServer.ReadAsync(speedBuf, 0, chunkSize - 1);
                                                Trace.WriteLine($"Server: got bytes {chunkSize:n0}");
                                            }
                                            break;
                                    }
                                }
                            }
                        }
                    }
                    catch (IOException ex)
                    {
                        Trace.WriteLine($"Server: IOException" + ex.ToString());
                        throw;
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
                using (var pipeClient = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: pipeName,
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
                            Marshal.WriteInt32(mappedSection, strBuf.Length);
                            Marshal.Copy(strBuf, 0, mappedSection + IntPtr.Size, strBuf.Length);
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
                        var bufSpeed = new byte[chunkSize];
                        bufSpeed[0] = (byte)Verbs.verbSpeedTest;
                        var sw = Stopwatch.StartNew();
                        for (int iter = 0; iter < nIter; iter++)
                        {
                            Trace.WriteLine($"Sending chunk {iter}");
                            await pipeClient.WriteAsync(bufSpeed, 0, bufSpeed.Length);
                        }
                        var bps = (double)chunkSize * nIter / sw.Elapsed.TotalSeconds;
                        Trace.WriteLine($"BytesPerSec = {bps:n0}"); // 1.2 to 1.3 G/Sec
                    }

                    Trace.WriteLine($"Client: sending quit");
                    verb[0] = (byte)Verbs.verbQuit;
                    await pipeClient.WriteAsync(verb, 0, 1);
                }
            });

            var tskDelay = Task.Delay(TimeSpan.FromSeconds(Debugger.IsAttached ? 3000 : 10));
            await Task.WhenAny(new[] { tskDelay, taskServer });
            if (tskDelay.IsCompleted)
            {
                Trace.WriteLine($"Delay completed: cancelling server");
                cts.Cancel();
                await taskServer;
            }
            mmfView.Dispose();
            mmf.Dispose();
            Trace.WriteLine($"Done");
        }
        /// <summary>
        /// each verb is 1 byte, with 
        /// </summary>
        public enum Verbs
        {
            verbQuit, // len =1 byte: 0 args
            verbString, // 
            verbStringSharedMem,
            verbSpeedTest,
            verbRequestData, // len = 1 byte: 0 args
            verbSendObjAndReferences, // a single obj and a list of it's references
        }
    }
}
