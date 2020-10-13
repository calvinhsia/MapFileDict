﻿using System;
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

namespace MapFileDict
{
    public enum Verbs
    {
        verbQuit, // len =1 byte: 0 args
        verbAck, // acknowledge
        verbGetLog,
        verbString, // 
        verbStringSharedMem,
        verbSpeedTest,
        verbRequestData, // len = 1 byte: 0 args
        verbSendObjAndReferences, // a single obj and a list of it's references
    }
    public class OutOfProc : IDisposable
    {
        public int chunkSize = 10;
        public byte[] speedBuf; // used for testing speed comm only
        private CancellationToken token;
        public string pipeName;
        public IntPtr mappedSection;
        public uint sharedMapSize;
        public string sharedFileMapName;
        MemoryMappedFile mmf;
        MemoryMappedViewAccessor mmfView;
        private int pidClient;
        private MyTraceListener mylistener;

        public OutOfProc() // need a parameterless constructor 
        {

        }
        public OutOfProc(int PidClient, CancellationToken token)
        {
            this.Initialize(PidClient, token);
        }

        private void Initialize(int pidClient, CancellationToken token)
        {
            this.token = token;
            this.pidClient = pidClient;
            if (pidClient != Process.GetCurrentProcess().Id)
            {
                mylistener = new MyTraceListener();
                Trace.Listeners.Add(mylistener);
                Trace.WriteLine($"Server Trace Listener created");
            }
            pipeName = $"MapFileDictPipe_{pidClient}";
            sharedFileMapName = $"MapFileDictSharedMem_{pidClient}\0";
            sharedMapSize = 65536U;
            mmf = MemoryMappedFile.CreateOrOpen(
               mapName: sharedFileMapName,
               capacity: sharedMapSize,
               access: MemoryMappedFileAccess.ReadWrite,
               options: MemoryMappedFileOptions.None,
               inheritability: HandleInheritability.None
               );
            mmfView = mmf.CreateViewAccessor(
               offset: 0,
               size: 0,
               access: MemoryMappedFileAccess.ReadWrite);
            mappedSection = mmfView.SafeMemoryMappedViewHandle.DangerousGetHandle();
            Trace.WriteLine($"IntPtr.Size = {IntPtr.Size} Shared Memory region address {mappedSection.ToInt64():x16}");
            speedBuf = new byte[chunkSize];
        }

        internal void SetChunkSize(int newsize)
        {
            this.chunkSize = newsize;
            speedBuf = new byte[chunkSize];
        }
        public static Process CreateServer(int pidClient)
        {
            var asm64BitFile = new FileInfo(Path.ChangeExtension("tempasm", ".exe")).FullName;
            if (File.Exists(asm64BitFile))
            {
                File.Delete(asm64BitFile);
            }
            Trace.WriteLine($"Asm = {asm64BitFile}");
            var creator = new AssemblyCreator().CreateAssembly(
                asm64BitFile,
                portableExecutableKinds: System.Reflection.PortableExecutableKinds.PE32Plus, // 64 bit
                imageFileMachine: ImageFileMachine.AMD64,
                AdditionalAssemblyPaths: string.Empty,
                logOutput: true
                );
            var args = $@"""{Assembly.GetAssembly(typeof(OutOfProc)).Location
                               }"" {nameof(OutOfProc)} {
                                   nameof(OutOfProc.MyMainMethod)} {pidClient}";
            Trace.WriteLine($"args = {args}");
            var p64 = Process.Start(
                asm64BitFile,
                args);
            return p64;
//            p64.WaitForExit(30 * 1000);
        }
        /// <summary>
        /// This runs in the 64 bit server process
        /// </summary>
        public static async Task MyMainMethod(int pidClient)
        {
            var cts = new CancellationTokenSource();
            using (var oop = new OutOfProc(pidClient, cts.Token))
            {
                Trace.WriteLine($"{nameof(oop.DoServerLoopAsync)} start");
                await oop.DoServerLoopAsync();
                Trace.WriteLine("{nameof(oop.DoServerLoopAsync)} done");
            }
            Trace.WriteLine($"End of OOP loop");
        }
        public async Task DoServerLoopAsync()
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
                    Trace.WriteLine($"Server: wait for connection");
                    await pipeServer.WaitForConnectionAsync(token);
                    Trace.WriteLine($"Server: connected");
                    var buff = new byte[100];
                    var receivedQuit = false;
                    using (var ctsReg = token.Register(
                           () => { pipeServer.Disconnect(); Trace.WriteLine("Cancel: disconnect pipe"); }))
                    {
                        while (!receivedQuit)
                        {
                            if (token.IsCancellationRequested)
                            {
                                Trace.WriteLine("server: got cancel");
                                receivedQuit = true;
                            }
                            var nBytesRead = await pipeServer.ReadAsync(buff, 0, 1, token);
                            switch ((Verbs)buff[0])
                            {
                                case Verbs.verbQuit:
                                    await pipeServer.SendAckAsync();
                                    Trace.WriteLine($"Server got quit message");
                                    receivedQuit = true;
                                    break;
                                case Verbs.verbGetLog:
                                    if (mylistener != null)
                                    {
                                        Trace.WriteLine($"Server: Getlog #entries = {mylistener.lstLoggedStrings.Count}");
                                        var strlog = string.Join("\r\n   ", mylistener.lstLoggedStrings);
                                        mylistener.lstLoggedStrings.Clear();
                                        var buf = Encoding.ASCII.GetBytes("     ServerLog::" + strlog);
                                        Marshal.Copy(buf, 0, mappedSection, buf.Length);
                                    }
                                    else
                                    {
                                        var buf = Encoding.ASCII.GetBytes("No Logs because in proc");
                                        Marshal.Copy(buf, 0, mappedSection, buf.Length);
                                    }
                                    await pipeServer.SendAckAsync();
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
                                    var strRead = Encoding.ASCII.GetString(lstBytes.ToArray());
                                    Trace.WriteLine($"Server Got str {strRead}");
                                    break;
                                case Verbs.verbRequestData:
                                    var strToSend = $"Server: {DateTime.Now}";
                                    var strB = Encoding.ASCII.GetBytes(strToSend);
                                    Trace.WriteLine($"Server: req data {strToSend}");
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
            if (mylistener != null)
            {
                Trace.Listeners.Remove(mylistener);
            }
            if (pidClient != Process.GetCurrentProcess().Id)
            {
                //                Environment.Exit(0);
            }
        }

        public void Dispose()
        {
            mmfView.Dispose();
            mmf.Dispose();
            mylistener?.Dispose();
        }
    }
    public class MyTraceListener : TextWriterTraceListener
    {
        public List<string> lstLoggedStrings;
        public MyTraceListener()
        {
            lstLoggedStrings = new List<string>();
        }
        public override void WriteLine(string str)
        {
            var dt = string.Format("[{0}],",
                 DateTime.Now.ToString("hh:mm:ss:fff")
                 ) + $"{Thread.CurrentThread.ManagedThreadId,2} ";
            lstLoggedStrings.Add(dt + str);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            var outfile = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Desktop\TestOutput.txt");
            var leftovers = string.Join("\r\n     ", lstLoggedStrings);
            File.AppendAllText(outfile, "LeftOverLogs\r\n     " + leftovers + "\r\n");
        }
    }
    public static class ExtensionMethods
    {
        public static async Task SendAckAsync(this PipeStream pipe)
        {
            var verb = new byte[2];
            verb[0] = (byte)Verbs.verbAck;
            await pipe.WriteAsync(verb, 0, 1);
        }
        public static async Task GetAckAsync(this PipeStream pipe)
        {
            var buff = new byte[10];
            var len = await pipe.ReadAsync(buff, 0, 1);
            if (len != 1 || buff[0] != (byte)Verbs.verbAck)
            {
                Trace.Write($"Didn't get Expected Ack");
            }
        }
    }
}
