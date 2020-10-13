using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
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

    [Serializable]
    class ObjAndRefs : ISerializable
    {
        public ulong obj;
        public List<ulong> lstRefs = new List<ulong>();
        public ObjAndRefs()
        {

        }
        protected ObjAndRefs(SerializationInfo info, StreamingContext context)
        {
            obj = info.GetUInt64("obj");
            var cnt = info.GetInt32("cntRefs");
            for (int i = 0; i < cnt; i++)
            {
                lstRefs.Add(info.GetUInt64($"i{i}"));
            }
        }
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("obj", obj);
            info.AddValue("cntRefs", lstRefs.Count);
            for (int i = 0; i < lstRefs.Count; i++)
            {
                info.AddValue($"i{i}", lstRefs[i]);
            }
        }

        public override string ToString()
        {
            return $"{obj:x8}  {string.Join(",", lstRefs.ToArray())}";
        }
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
            var procServer = Process.Start(
                asm64BitFile,
                args);
            Trace.WriteLine($"Client: started server PidClient={pidClient} PidServer={procServer.Id}");
            return procServer;
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
                        var dictObjRef = new Dictionary<ulong, ObjAndRefs>();

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
                                case Verbs.verbSendObjAndReferences:
                                    //var b = new BinaryFormatter();
                                    //var objAndRef = b.Deserialize(pipeServer) as ObjAndRefs;
                                    //dictObjRef[objAndRef.obj] = objAndRef;
                                    //Trace.WriteLine($"Server got {nameof(Verbs.verbSendObjAndReferences)}  {objAndRef}");
                                    for (int i = 0; i < 10; i++)
                                    {
                                        pipeServer.ReadByte();
                                    }
                                    await pipeServer.SendAckAsync();

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

        internal async Task<string> GetLogFromServer(NamedPipeClientStream pipeClient)
        {
            Trace.WriteLine($"getting log from server");
            pipeClient.WriteByte((byte)Verbs.verbGetLog);
            await pipeClient.GetAckAsync();
            var logstrs = Marshal.PtrToStringAnsi(mappedSection);
            return logstrs;
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
        /// <summary>
        /// Sends verb and waits for ack
        /// </summary>
        public static async Task SendVerb(this PipeStream pipe, Verbs verb, Func<Task> funcAsync = null)
        {
            var verbBuf = new byte[2];
            Trace.WriteLine($"Client: sending {verb}");
            verbBuf[0] = (byte)verb;
            await pipe.WriteAsync(verbBuf, 0, 1);
            if (funcAsync != null)
            {
                await funcAsync();
            }
            await pipe.GetAckAsync();
        }
    }
}
