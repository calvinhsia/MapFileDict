using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
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
    public enum OOPOption
    {
        InProc,
        InProcTestLogging,
        Out32bit,
        Out64Bit
    }
    public class OutOfProc : IDisposable
    {
        public int chunkSize = 10;
        public byte[] speedBuf; // used for testing speed comm only
        private CancellationToken token;
        private readonly int pidClient;
        internal OOPOption option;
        public string pipeName;
        public IntPtr mappedSection;
        public uint sharedMapSize;
        public string sharedFileMapName;
        MemoryMappedFile mmf;
        MemoryMappedViewAccessor mmfView;
        private readonly MyTraceListener mylistener;

        public OutOfProc(int PidClient, OOPOption option, CancellationToken token)
        {
            this.token = token;
            this.pidClient = PidClient;
            this.option = option;
            if (pidClient != Process.GetCurrentProcess().Id)
            {
                mylistener = new MyTraceListener();
                Trace.Listeners.Add(mylistener);
            }
            pipeName = $"MapFileDictPipe_{PidClient}";
            sharedFileMapName = $"MapFileDictSharedMem_{PidClient}\0";
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
            speedBuf = new byte[chunkSize];
        }
        internal void SetChunkSize(int newsize)
        {
            this.chunkSize = newsize;
            speedBuf = new byte[chunkSize];
        }
        public async Task CreateServerAsync()
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
                    await pipeServer.WaitForConnectionAsync(token);

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
                                    var strlog = string.Join("\r\n     ServerLog::", mylistener.lstLoggedStrings);
                                    mylistener.lstLoggedStrings.Clear();
                                    var buf = Encoding.ASCII.GetBytes(strlog);
                                    Marshal.Copy(buf, 0, mappedSection, buf.Length);
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
            if (mylistener != null)
            {
                Trace.Listeners.Remove(mylistener);
            }
            if (pidClient != Process.GetCurrentProcess().Id)
            {
                Environment.Exit(0);
            }
            //var taskServer = Task.Run(async () =>
            // {
            // });
            //return taskServer;
        }

        public void Dispose()
        {
            mmfView.Dispose();
            mmf.Dispose();
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
            lstLoggedStrings.Add(str);
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
