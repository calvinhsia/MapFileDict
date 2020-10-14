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
using System.Windows.Threading;

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
        verbSendObjAndReferencesChunks, // yields perf gains: from 5k objs/sec to 1M/sec
        verbCreateInvertedDictionary,
        verbQueryParentOfObject,

    }

    [Serializable]
    class ObjAndRefs// : ISerializable
    {
        public uint obj;
        public List<uint> lstRefs; // since many objs don't ref things (like strings),then we'll allow NULL here
        public ObjAndRefs()
        {

        }
        // our own customs serialization is far faster
        // 1st send the objaddr
        // 2nd send the # of refs
        // 3+ send each ref
        public void SerializeToPipe(PipeStream pipe)
        {
            pipe.WriteUInt32(obj);
            var cnt = lstRefs == null ? 0 : lstRefs.Count;
            pipe.WriteUInt32((uint)cnt);
            for (int i = 0; i < cnt; i++)
            {
                pipe.WriteUInt32(lstRefs[i]);
            }
        }
        public static ObjAndRefs CreateFromPipe(PipeStream pipe)
        {
            var o = new ObjAndRefs();
            o.obj = pipe.ReadUInt32();
            if (o.obj != 0)
            {
                var cnt = pipe.ReadUInt32();
                if (cnt > 0)
                {
                    o.lstRefs = new List<uint>();
                    for (int i = 0; i < cnt; i++)
                    {
                        o.lstRefs.Add(pipe.ReadUInt32());
                    }
                }
            }
            return o;
        }
        public static Dictionary<uint, List<uint>> GetDictObjsFromPipe(PipeStream pipe)
        {
            var dictObjRef = new Dictionary<uint, List<uint>>();
            while (true)
            {
                var lst = new List<uint>();
                var obj = pipe.ReadUInt32();
                if (obj == 0)
                {
                    break;
                }
                var cnt = pipe.ReadUInt32();
                if (cnt > 0)
                {
                    for (int i = 0; i < cnt; i++)
                    {
                        lst.Add(pipe.ReadUInt32());
                    }
                }
                dictObjRef[obj] = lst;
            }
            return dictObjRef;
        }

        //protected ObjAndRefs(SerializationInfo info, StreamingContext context)
        //{
        //    obj = info.GetUInt64("obj");
        //    var cnt = info.GetInt32("cntRefs");
        //    for (int i = 0; i < cnt; i++)
        //    {
        //        lstRefs.Add(info.GetUInt64($"i{i}"));
        //    }
        //}
        //public void GetObjectData(SerializationInfo info, StreamingContext context)
        //{
        //    info.AddValue("obj", obj);
        //    info.AddValue("cntRefs", lstRefs.Count);
        //    for (int i = 0; i < lstRefs.Count; i++)
        //    {
        //        info.AddValue($"i{i}", lstRefs[i]);
        //    }
        //}

        public override string ToString()
        {
            var refs = lstRefs == null ? string.Empty : string.Join(",", lstRefs.ToArray());
            return $"{obj:x8}  {refs}";
        }
    }
    public class OutOfProc : IDisposable
    {
        public int SpeedBufSize = 10; // for testing transmission speeds
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

        public OutOfProc() // need a parameterless constructor for Activator
        {

        }
        public OutOfProc(int PidClient, CancellationToken token, int speedBufSize = 10)
        {
            this.Initialize(PidClient, token, speedBufSize);
        }

        private void Initialize(int pidClient, CancellationToken token, int ChunkSize)
        {
            this.token = token;
            this.pidClient = pidClient;
            this.SpeedBufSize = ChunkSize;
            speedBuf = new byte[ChunkSize];
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
            Trace.WriteLine($"{Process.GetCurrentProcess().ProcessName} IntPtr.Size = {IntPtr.Size} Shared Memory region address {mappedSection.ToInt64():x16}");
            speedBuf = new byte[SpeedBufSize];
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
            var tcsStaThread = new TaskCompletionSource<int>();
            var execContext = CreateExecutionContext(tcsStaThread);
            await execContext.Dispatcher.InvokeAsync(async () =>
            {
                var cts = new CancellationTokenSource();
                using (var oop = new OutOfProc(pidClient, cts.Token))
                {
                    Trace.WriteLine($"{nameof(oop.DoServerLoopAsync)} start");
                    await oop.DoServerLoopAsync();
                    Trace.WriteLine("{nameof(oop.DoServerLoopAsync)} done");
                }
                Trace.WriteLine($"End of OOP loop");
                tcsStaThread.SetResult(0);
            });
            await tcsStaThread.Task;
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
                        var dictObjRef = new Dictionary<uint, List<uint>>();
                        Dictionary<uint, List<uint>> dictInverted = null;

                        while (!receivedQuit)
                        {
                            if (token.IsCancellationRequested)
                            {
                                Trace.WriteLine("server: got cancel");
                                receivedQuit = true;
                            }
                            var nBytesRead = await pipeServer.ReadAsync(buff, 0, 1, token);
                            try
                            {
                                switch ((Verbs)buff[0])
                                {
                                    case Verbs.verbQuit:
                                        Trace.WriteLine($"# dict entries = {dictObjRef.Count}");
                                        await pipeServer.SendAckAsync();
                                        Trace.WriteLine($"Server got quit message");
                                        receivedQuit = true;
                                        break;
                                    case Verbs.verbSendObjAndReferences:
                                        //var b = new BinaryFormatter();
                                        //unsafe
                                        //{
                                        //    var mbPtr = (byte*)mappedSection.ToPointer();
                                        //    using (var ms = new UnmanagedMemoryStream(mbPtr, sharedMapSize, sharedMapSize, FileAccess.ReadWrite))
                                        //    {
                                        //        var objAndRef = b.Deserialize(ms) as ObjAndRefs;
                                        //        dictObjRef[objAndRef.obj] = objAndRef;
                                        //        //                                            Trace.WriteLine($"Server got {nameof(Verbs.verbSendObjAndReferences)}  {objAndRef}");
                                        //    }

                                        //}
                                        dictObjRef = ObjAndRefs.GetDictObjsFromPipe(pipeServer);
                                        await pipeServer.SendAckAsync();

                                        break;
                                    case Verbs.verbSendObjAndReferencesChunks:
                                        {
                                            var bufSize = (int)pipeServer.ReadUInt32();
                                            var buf = new byte[bufSize];
                                            await pipeServer.ReadAsync(buf, 0, bufSize);
                                            var bufNdx = 0;
                                            while (true)
                                            {
                                                var lst = new List<uint>();
                                                var obj = BitConverter.ToUInt32(buf, bufNdx);
                                                bufNdx += 4; // sizeof IntPtr in the client process
                                                if (obj == 0)
                                                {
                                                    break;
                                                }
                                                var cnt = BitConverter.ToUInt32(buf, bufNdx);
                                                bufNdx += 4;
                                                if (cnt > 0)
                                                {
                                                    for (int i = 0; i < cnt; i++)
                                                    {
                                                        lst.Add(BitConverter.ToUInt32(buf, bufNdx));
                                                        bufNdx += 4;
                                                    }
                                                }
                                                dictObjRef[obj] = lst;
                                            }
                                            await pipeServer.SendAckAsync();
                                        }
                                        break;
                                    case Verbs.verbCreateInvertedDictionary:
                                        dictInverted = InvertDictionary(dictObjRef);
                                        await pipeServer.SendAckAsync();
                                        break;
                                    case Verbs.verbQueryParentOfObject:
                                        var objQuery = pipeServer.ReadUInt32();
                                        if (dictInverted.TryGetValue(objQuery, out var lstParents))
                                        {
                                            Trace.WriteLine($"Server: {objQuery:x8}  NumParents={lstParents.Count}");
                                            foreach (var parent in lstParents)
                                            {
                                                pipeServer.WriteUInt32(parent);
                                            }
                                        }
                                        pipeServer.WriteUInt32(0); // terminator
                                        break;
                                    case Verbs.verbGetLog:
                                        if (mylistener != null)
                                        {
                                            Trace.WriteLine($"# dict entries = {dictObjRef.Count}");
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
                                            await pipeServer.ReadAsync(speedBuf, 0, SpeedBufSize - 1);
                                            Trace.WriteLine($"Server: got bytes {SpeedBufSize:n0}");
                                        }
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Trace.WriteLine(ex.ToString());
                                mylistener?.ForceAddToLog(ex.ToString());
                                throw;
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
            catch (Exception ex)
            {
                mylistener?.ForceAddToLog(ex.ToString());
                throw;
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
        }
        public static Dictionary<uint, List<uint>> InvertDictionary(Dictionary<uint, List<uint>> dictOGraph)
        {
            var dictInvert = new Dictionary<uint, List<uint>>(); // obj ->list of objs that reference it
                                                                 // the result will be a dict of every object, with a value of a List of all the objects referring to it.
                                                                 // thus looking for parents of a particular obj will be fast.

            List<uint> AddObjToDict(uint obj)
            {
                if (!dictInvert.TryGetValue(obj, out var lstParents))
                {
                    dictInvert[obj] = null; // initially, this obj has no parents: we haven't seen it before
                }
                return lstParents;
            }
            foreach (var kvp in dictOGraph)
            {
                var lsto = AddObjToDict(kvp.Key);
                if (kvp.Value != null)
                {
                    foreach (var oChild in kvp.Value)
                    {
                        var lstChildsParents = AddObjToDict(oChild);
                        if (lstChildsParents == null)
                        {
                            lstChildsParents = new List<uint>();
                            dictInvert[oChild] = lstChildsParents;
                        }
                        lstChildsParents.Add(kvp.Key);// set the parent of this child
                    }
                }
            }

            return dictInvert;
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
        static MyExecutionContext CreateExecutionContext(TaskCompletionSource<int> tcsStaThread)
        {
            const string Threadname = "MyStaThread";
            var tcsGetExecutionContext = new TaskCompletionSource<MyExecutionContext>();

            Trace.WriteLine($"Creating {Threadname}");
            var myStaThread = new Thread(() =>
            {
                // Create the context, and install it:
                Trace.WriteLine($"{Threadname} start");
                var dispatcher = Dispatcher.CurrentDispatcher;
                var syncContext = new DispatcherSynchronizationContext(dispatcher);

                SynchronizationContext.SetSynchronizationContext(syncContext);

                tcsGetExecutionContext.SetResult(new MyExecutionContext
                {
                    DispatcherSynchronizationContext = syncContext,
                    Dispatcher = dispatcher
                });

                // Start the Dispatcher Processing
                Trace.WriteLine($"MyStaThread before Dispatcher.run");
                Dispatcher.Run();
                Trace.WriteLine($"MyStaThread After Dispatcher.run");
                tcsStaThread.SetResult(0);
            })
            {

                //            myStaThread.SetApartmentState(ApartmentState.STA);
                Name = Threadname
            };
            myStaThread.Start();
            Trace.WriteLine($"Starting {Threadname}");
            return tcsGetExecutionContext.Task.Result;
        }

        public class MyExecutionContext
        {
            public DispatcherSynchronizationContext DispatcherSynchronizationContext { get; set; }
            public Dispatcher Dispatcher { get; set; }
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
            var leftovers = string.Join("\r\n     ", lstLoggedStrings);

            ForceAddToLog("LeftOverLogs\r\n     " + leftovers + "\r\n");
        }

        internal void ForceAddToLog(string str)
        {
            var outfile = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Desktop\TestOutput.txt");
            var leftovers = string.Join("\r\n     ", lstLoggedStrings) + "\r\n" + str;
            lstLoggedStrings.Clear();
            OutputToLogFileWithRetryAsync(() =>
            {
                File.AppendAllText(outfile, str + "\r\n");
            });
        }
        public void OutputToLogFileWithRetryAsync(Action actWrite)
        {
            var nRetry = 0;
            var success = false;
            while (nRetry++ < 10)
            {
                try
                {
                    actWrite();
                    success = true;
                    break;
                }
                catch (IOException)
                {
                }

                Task.Delay(TimeSpan.FromSeconds(0.3)).Wait();
            }
            if (!success)
            {
                Trace.WriteLine($"Error writing to log #retries ={nRetry}");
            }
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
        public static async Task SendVerb(this PipeStream pipe, Verbs verb)
        {
            var verbBuf = new byte[2];
            verbBuf[0] = (byte)verb;
            await pipe.WriteAsync(verbBuf, 0, 1);
            await pipe.GetAckAsync();
        }
        public static void WriteUInt32(this PipeStream pipe, uint addr)
        {
            var buf = BitConverter.GetBytes(addr);
            pipe.Write(buf, 0, buf.Length);
        }
        public static void WriteUInt64(this PipeStream pipe, UInt64 addr)
        {
            var buf = BitConverter.GetBytes(addr);
            pipe.Write(buf, 0, buf.Length);
        }
        public static uint ReadUInt32(this PipeStream pipe)
        {
            var buf = new byte[4];
            pipe.Read(buf, 0, buf.Length);
            var res = BitConverter.ToUInt32(buf, 0);
            return res;
        }
        public static ulong ReadUInt64(this PipeStream pipe)
        {
            var buf = new byte[8];
            pipe.Read(buf, 0, buf.Length);
            var res = BitConverter.ToUInt64(buf, 0);
            return res;
        }
    }
}
