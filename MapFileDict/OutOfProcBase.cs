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
using System.Windows.Threading;
//https://github.com/calvinhsia/MapFileDict/blob/OutOfProc/MapFileDict/OutOfProc.cs
namespace MapFileDict
{

    public class OutOfProcOptions
    {
        public OutOfProcOptions()
        {
            PidClient = Process.GetCurrentProcess().Id;
        }
        /// <summary>
        /// Start an EXE out of proc. Defaults to true. If false, will use in-proc (for easier debugging)
        /// </summary>
        public bool CreateServerOutOfProc = true; // or can be inproc for testing
        public int ConnectTimeout = 5000; // connection timeout in mSecs (-1 == infinite)
        public string ExistingExeNameToUseForServer = string.Empty;
        public string exeNameToCreate = string.Empty; // defaults to "tempasm.exe" in curdir
        public bool UseExistingExeIfExists = false; // false for unit tests, so will be deleted. True for production, so created EXE persists. Beware updates with diff versions
        public PortableExecutableKinds portableExecutableKinds = PortableExecutableKinds.PE32Plus;
        public ImageFileMachine imageFileMachine = ImageFileMachine.AMD64;
        public string AdditionalAssemblyPaths = string.Empty;
        public bool ServerTraceLogging = true;
        public uint SizeOfSharedMemory = 65536u;
        // for testing throwing exceptions only
        public uint TypeIdAtWhichToThrowException = 0;
        /// <summary>
        /// Unit tests start/stop the OOP server very quickly: we don't want to use the same pipe name, because it's a race condition
        /// </summary>
        public string NamedPipeAddString = string.Empty;

        internal int PidClient;
        internal string NamedPipeName; // only internal use: the client creates the pipename, the server uses it to connect

    }
    public abstract class OutOfProcBase : IDisposable
    {
        private CancellationToken token;
        public OutOfProcOptions Options;

        public uint _sharedMapSize;
        protected string _sharedFileMapName;
        MemoryMappedFile _MemoryMappedFileForSharedRegion;
        MemoryMappedViewAccessor _MemoryMappedFileViewForSharedRegion;
        public IntPtr _MemoryMappedRegionAddress;// the address of the shared region. Will probably be different for client and for server
        public int pidClient => Options.PidClient;
        protected MyTraceListener mylistener;
        public Process ProcServer { get; set; } // only if out of proc
        public Task DoServerLoopTask; // the loop that listens for the pipe and executes verbs
        protected TaskCompletionSource<int> tcsAddedVerbs = new TaskCompletionSource<int>(); // we wait for the verbs to be added before we start listening to the pipe

        protected NamedPipeClientStream PipeFromClient { get; private set; } // non-null for client
        protected NamedPipeServerStream PipeFromServer { get; private set; } // non-null for server

        public Dictionary<object, object> dictProperties = new Dictionary<object, object>();
        public Dictionary<Verbs, ActionHolder> _dictVerbs = new Dictionary<Verbs, ActionHolder>();

        public struct ActionHolder
        {
            public Verbs verb;
            public Func<object, Task<object>> actionClientSendVerb;
            public Func<object, Task<object>> actionServerDoVerb;
        }

        public OutOfProcBase() // need a parameterless constructor for Activator when creating server proc
        {

        }
        public OutOfProcBase(OutOfProcOptions options, CancellationToken token) // this ctor is for both server and client
        {
            if (options == null)
            {
                options = new OutOfProcOptions();
            }
            this.Options = options;
            this.token = token;
            if (string.IsNullOrEmpty(this.Options.NamedPipeName))
            {
                this.Options.NamedPipeName = $"MapFileDictPipe_{pidClient}" + options.NamedPipeAddString;
            }
            if (Process.GetCurrentProcess().Id == options.PidClient) //we're the client process?
            {
                PipeFromClient = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: this.Options.NamedPipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous);
                if (options.CreateServerOutOfProc)
                {
                    if (!string.IsNullOrEmpty(options.ExistingExeNameToUseForServer))
                    {
                        var typeToInstantiateName = this.GetType().Name; // we need to get the name of the derived class to instantiate
                        ProcServer = Process.Start(options.ExistingExeNameToUseForServer, $"{pidClient} {typeToInstantiateName}");
                    }
                    else
                    {
                        CreateServerProcess();
                    }
                }
                else
                {
                    DoServerLoopTask = DoServerLoopAsync();
                }
            }
            else
            { //we're the server process
                if (options.ServerTraceLogging)
                {
                    mylistener = new MyTraceListener();
                    Trace.Listeners.Add(mylistener);
                }
                Trace.WriteLine($"Server Process IntPtr.Size = {IntPtr.Size} Trace Listener created");
                DoServerLoopTask = DoServerLoopAsync();
            }
        }

        public async Task ConnectToServerAsync(CancellationToken token)
        {
            PipeFromClient.Connect(timeout: Options.ConnectTimeout);
            var errcode = (uint)await this.ClientSendVerbAsync(Verbs.EstablishConnection, 0);
            if (errcode != 0)
            {
                var lastError = (string)await this.ClientSendVerbAsync(Verbs.GetLastError, null);
                throw new Exception($"Error establishing connection to server " + lastError);
            }
            if (Options.SizeOfSharedMemory > 0)
            {
                await ClientSendVerbAsync(Verbs.CreateSharedMemSection, Options.SizeOfSharedMemory);
            }
        }

        public bool IsSharedRegionCreated()
        {
            return IntPtr.Zero != _MemoryMappedRegionAddress;
        }

        void CreateServerProcess()
        {
            // sometimes using Temp file in temp dir causes System.ComponentModel.Win32Exception: Operation did not complete successfully because the file contains a virus or potentially unwanted software
            var asm64BitFile = string.IsNullOrEmpty(Options.exeNameToCreate) ? new FileInfo(Path.ChangeExtension("tempasm", ".exe")).FullName : Options.exeNameToCreate;
            var createIt = true;
            try
            {
                if (File.Exists(asm64BitFile))
                {
                    if (Options.UseExistingExeIfExists)
                    {
                        createIt = false;
                    }
                    else
                    {
                        File.Delete(asm64BitFile);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.ToString());
                createIt = false;
            }
            Trace.WriteLine($"Asm = {asm64BitFile}");
            if (createIt) // else reuse the EXE already created. 
            {
                var creator = new AssemblyCreator().CreateAssembly(
                    asm64BitFile,
                    portableExecutableKinds: Options.portableExecutableKinds, // 64 bit
                    imageFileMachine: Options.imageFileMachine,
                    AdditionalAssemblyPaths: Options.AdditionalAssemblyPaths,
                    logOutput: true
                    );
            }
            var typeToInstantiateName = this.GetType().Name; // we need to get the name of the derived class to instantiate

            var args = $@"""{Assembly.GetAssembly(typeof(OutOfProcBase)).Location
                               }"" {nameof(OutOfProcBase)} {
                                   nameof(OutOfProcBase.MyMainMethod)} {pidClient} {typeToInstantiateName} {this.Options.NamedPipeName}";
            Trace.WriteLine($"args = {args}");
            ProcServer = Process.Start(
                asm64BitFile,
                args);
            Trace.WriteLine($"Client: started server PidClient={pidClient} PidServer={ProcServer.Id} {asm64BitFile}");
        }
        /// <summary>
        /// This is entry point in the 64 bit server process. Create an execution context for the asyncs
        /// </summary>
        public static async Task MyMainMethod(int pidClient, string typeToInstantiateName, string namedPipeName)
        {
            var tcsStaThread = new TaskCompletionSource<int>();
            var execContext = CreateExecutionContext(tcsStaThread);
            await execContext.Dispatcher.InvokeAsync(async () =>
            {
                //                MessageBox(0, $"Attach a debugger if desired {Process.GetCurrentProcess().Id} {Process.GetCurrentProcess().MainModule.FileName}", "Server Process", 0);
                var cts = new CancellationTokenSource();
                OutOfProcBase oop = null;
                try
                {
                    var typeToInstantiate = typeof(OutOfProc).Assembly.GetTypes().Where(t => t.Name == typeToInstantiateName).FirstOrDefault();
                    var args = new object[] {
                        new OutOfProcOptions() { PidClient = pidClient, NamedPipeName= namedPipeName },
                        new CancellationToken()};
                    oop = (OutOfProcBase)Activator.CreateInstance(typeToInstantiate, args);
                    Trace.WriteLine($"{nameof(oop.DoServerLoopAsync)} start");
                    await oop.DoServerLoopTask;
                    Trace.WriteLine("{nameof(oop.DoServerLoopAsync)} done");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex.ToString());
                }
                finally
                {
                    oop?.Dispose();
                }
                Trace.WriteLine($"End of OOP loop");
                tcsStaThread.SetResult(0);
                execContext.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            });
            await tcsStaThread.Task;
        }

        async Task DoServerLoopAsync()
        {
            try
            {
                Trace.WriteLine("Server: Starting ");
                await tcsAddedVerbs.Task;
                PipeFromServer = new NamedPipeServerStream(
                    pipeName: this.Options.NamedPipeName,
                    direction: PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Message,
                    options: PipeOptions.Asynchronous
                    );
                {
                    Trace.WriteLine($"Server: wait for connection IntPtr.Size={IntPtr.Size} {Options.NamedPipeName}");
                    await PipeFromServer.WaitForConnectionAsync(token);
                    Trace.WriteLine($"Server: connected");
                    var receivedQuit = false;
                    using (var ctsReg = token.Register(
                           () => { PipeFromServer.Disconnect(); Trace.WriteLine("Cancel: disconnect pipe"); }))
                    {
                        while (!receivedQuit)
                        {
                            try
                            {
                                if (token.IsCancellationRequested)
                                {
                                    Trace.WriteLine("server: got cancel");
                                    receivedQuit = true;
                                }
                                var verb = (Verbs)PipeFromServer.ReadByte(); // when reading verb, we don't want timeout because client initiated calls can occur any time
                                if (verb == Verbs.PipeBroken)
                                {
                                    receivedQuit = true;
                                    break;
                                }
                                if (_dictVerbs.ContainsKey(verb))
                                {
                                    var res = await ServerDoVerbAsync(verb, null);
                                    if (res is Verbs)
                                    {
                                        if ((Verbs)res == Verbs.ServerQuit)
                                        {
                                            receivedQuit = true;
                                        }
                                    }
                                }
                                else
                                {
                                    throw new Exception($"Received unknown Verb {verb}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Trace.WriteLine($"Exception: terminating process: " + ex.ToString());
                                mylistener?.ForceAddToLog(ex.ToString());
#if DEBUG
                                if (!ClientAndServerInSameProcess)
                                {
                                    MessageBox(0, $"Server exception " + ex.ToString(),
                                        $"{Process.GetCurrentProcess().ProcessName} {Process.GetCurrentProcess().Id} {Options.NamedPipeName}", 0);
                                }
#endif
                                if (pidClient != Process.GetCurrentProcess().Id)
                                {
                                    Environment.Exit(0);
                                }
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
            finally
            {
                this.Dispose();
            }

            Trace.WriteLine("Server: exiting servertask");
        }

        public bool ClientAndServerInSameProcess => !Options.CreateServerOutOfProc;
        /// <summary>
        /// Called from both client and server. Given a name, creates memory region of specified size (mult 64k) that can be addressed by each process
        /// </summary>
        internal void CreateSharedSection(string memRegionName, uint regionSize)
        {
            if (ClientAndServerInSameProcess && _MemoryMappedRegionAddress != IntPtr.Zero)
            {
                return;// client and server in same proc, so same region is shared
            }
            _sharedFileMapName = memRegionName;

            //var extraMemNeeeded = regionSize % MemMap.AllocationGranularity;
            //var actualSize = regionSize + extraMemNeeeded; 
            _sharedMapSize = regionSize;
            _MemoryMappedFileForSharedRegion = MemoryMappedFile.CreateOrOpen(
               mapName: _sharedFileMapName,
               capacity: _sharedMapSize,
               access: MemoryMappedFileAccess.ReadWrite,
               options: MemoryMappedFileOptions.None,
               inheritability: HandleInheritability.None
               );
            _MemoryMappedFileViewForSharedRegion = _MemoryMappedFileForSharedRegion.CreateViewAccessor(
               offset: 0,
               size: 0,
               access: MemoryMappedFileAccess.ReadWrite);
            _MemoryMappedRegionAddress = _MemoryMappedFileViewForSharedRegion.SafeMemoryMappedViewHandle.DangerousGetHandle();
            Trace.WriteLine($"{Process.GetCurrentProcess().ProcessName} IntPtr.Size = {IntPtr.Size} Creating Shared Memory region size = {_sharedMapSize} address {_MemoryMappedRegionAddress.ToInt64():x16}");
        }
        /// <summary>
        /// Called from both client and server, clears the shared memory region
        /// </summary>
        internal void CloseSharedSection()
        {
            if (_MemoryMappedRegionAddress != IntPtr.Zero)
            {
                Trace.WriteLine($"{Process.GetCurrentProcess().ProcessName} IntPtr.Size = {IntPtr.Size} Closing Shared Memory region size = {_sharedMapSize} address {_MemoryMappedRegionAddress.ToInt64():x16}");
                _MemoryMappedFileViewForSharedRegion?.Dispose();
                _MemoryMappedFileViewForSharedRegion = null;
                _MemoryMappedFileForSharedRegion?.Dispose();
                _MemoryMappedFileForSharedRegion = null;
                _MemoryMappedRegionAddress = IntPtr.Zero;
                _sharedMapSize = 0;
                _sharedFileMapName = null;
            }
        }
        public void AddVerb(Verbs verb,
            Func<object, Task<object>> actClientSendVerb,
            Func<object, Task<object>> actServerDoVerb)
        {
            _dictVerbs.Add(verb, new ActionHolder() // throws if already exists
            {
                actionClientSendVerb = actClientSendVerb,
                actionServerDoVerb = actServerDoVerb
            });
        }
        public Task<object> ClientSendVerbAsync(Verbs verb, object parm)
        {
            return _dictVerbs[verb].actionClientSendVerb(parm);
        }
        public Task<object> ServerDoVerbAsync(Verbs verb, object parm)
        {
            var resTask = ExecuteFuncWithErrorHandling(() =>
               {
                   return _dictVerbs[verb].actionServerDoVerb(parm);
               }
            );
            return resTask;
        }

        /// <summary>
        /// The server is expected to consume all the bytes in the pipe sent from the client, even if there is an error
        /// When the server is executing code, and an exception occurs, we need a way to return either success or a failure
        /// But the error coule occur while the client is still sending data down the pipe, so we need to complete reading the data before sending the error
        /// Otherwise, the extra data will be misinterpreted.
        /// </summary>
        /// <param name="actAsync"></param>
        /// <returns></returns>
        public async Task<object> ExecuteFuncWithErrorHandling(Func<Task<object>> actAsync)
        {
            object resTask = null;
            try
            {
                resTask = await actAsync();
            }
            catch (Exception ex)
            {
                PipeFromServer.WriteByte((byte)Verbs.ExceptionOccurredOnServer);
                await PipeFromServer.WriteStringAsAsciiAsync(ex.ToString());
            }
            return resTask;
        }

        public void Dispose()
        {
            if (ProcServer != null)
            {
                int nRetries = 0;
                while (!ProcServer.HasExited)
                {
                    Trace.WriteLine($"Waiting for server to exit Pid={ProcServer.Id}");
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                    if (!Debugger.IsAttached && nRetries++ > 5)
                    {
                        Trace.WriteLine($"Killing server process");
                        ProcServer.Kill();
                        break;
                    }
                }
            }

            foreach (var kvp in dictProperties)
            {
                if (kvp.Value is IDisposable oDisp)
                {
                    oDisp.Dispose();
                }
            }
            PipeFromClient?.Dispose();
            PipeFromServer?.Dispose();
            CloseSharedSection();
            mylistener?.Dispose();
        }
        [DllImport("user32")]
        public static extern int MessageBox(int hWnd, String text, String caption, uint type);

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
                //                tcsStaThread.SetResult(0);
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
        public List<string> lstLoggedStrings = new List<string>();
        bool IsInTraceListener = false;
        public MyTraceListener()
        {
        }
        public override void WriteLine(string str)
        {
            if (!IsInTraceListener)
            {
                IsInTraceListener = true;
                var dt = string.Format("[{0}],",
                     DateTime.Now.ToString("hh:mm:ss:fff")
                     ) + $"{Thread.CurrentThread.ManagedThreadId,2} ";
                lstLoggedStrings.Add(dt + str);
                if (Debugger.IsAttached)
                {
                    Debug.WriteLine(dt + str);
                }
                IsInTraceListener = false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            var leftovers = string.Join("\r\n     ", lstLoggedStrings);

            ForceAddToLog("LeftOverLogs\r\n     " + leftovers + "\r\n");
            Trace.Listeners.Remove(this);
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
    [StructLayout(LayoutKind.Explicit, Size = 65536)]
    public unsafe struct MemBufChunk
    {
        [FieldOffset(0)]
        public fixed byte Bytes[65536];
        [FieldOffset(0)]
        public fixed uint UInts[65536 / 4];
    }
    public class OutOfProcException : Exception
    {
        public OutOfProcException(string message) : base(message)
        {
        }
    }

    public static class ExtensionMethods
    {
        static void PipeMsgTraceWriteline(string str)
        {
            //            Trace.WriteLine(str);
        }
        public static int TimeoutSecs = 30;
        public async static Task<byte[]> ReadTimeout(this PipeStream pipe, int count)
        {
            PipeMsgTraceWriteline($"  {pipe.GetType().Name} ReadTimeout count={count}");
            var buff = new byte[count];
            var taskRead = Task<int>.Factory.FromAsync(pipe.BeginRead, pipe.EndRead, buff, 0, 1, null, TaskCreationOptions.None);
#if DEBUG
            if (Debugger.IsAttached)
            {
                TimeoutSecs = 30 * 10000;
            }
#endif
            var taskTimeout = Task.Delay(TimeSpan.FromSeconds(TimeoutSecs));

            await Task.WhenAny(new Task[] { taskTimeout, taskRead });
            if (!taskRead.IsCompleted)
            {
                PipeMsgTraceWriteline($"   {pipe.GetType().Name} TaskRead not complete Count ={count}");
                throw new InvalidOperationException("Pipe read timeout");
            }
            PipeMsgTraceWriteline($"    {pipe.GetType().Name} ReadTimeout done count={count}");
            return buff;
        }

        public async static Task WriteTimeout(this PipeStream pipe, byte[] buff, int count)
        {
            PipeMsgTraceWriteline($"  {pipe.GetType().Name} writetimeout Count={count}");
            //            pipe.BeginWrite(buff, offset: 0, count: count,null,null)
            var taskWrite = Task.Factory.FromAsync(pipe.BeginWrite, pipe.EndWrite, buff, 0, count, null, TaskCreationOptions.None);
            var taskTimeout = Task.Delay(TimeSpan.FromSeconds(TimeoutSecs));

            await Task.WhenAny(new Task[] { taskTimeout, taskWrite });
            if (!taskWrite.IsCompleted)
            {
                PipeMsgTraceWriteline($"   {pipe.GetType().Name} TaskWrite not complete Count ={count}");
                throw new InvalidOperationException("Pipe write timeout");
            }
            PipeMsgTraceWriteline($"    {pipe.GetType().Name} writetimeout done Count={count}");
        }
        public static async Task WriteAcknowledgeAsync(this PipeStream pipe)
        {
            PipeMsgTraceWriteline($"{pipe.GetType().Name} WriteAck");
            var buff = new byte[1] { (byte)Verbs.Acknowledge };
            await pipe.WriteTimeout(buff, 1);
        }
        public static async Task ReadAcknowledgeAsync(this PipeStream pipe)
        {
            PipeMsgTraceWriteline($"{pipe.GetType().Name} ReadAck");
            var buff = await pipe.ReadTimeout(count: 1);
            if (buff[0] == (byte)Verbs.ExceptionOccurredOnServer)
            {
                var exceptToString = await pipe.ReadStringAsAsciiAsync();
                // delimit the exception on the server side from the exception on the client side
                throw new OutOfProcException($"Server exception =\r\n $$<$\r\n" + exceptToString + "\r\n$$>$");
            }
            if (buff[0] != (byte)Verbs.Acknowledge)
            {
                Trace.Write($"Didn't get Expected Ack");
            }
        }
        /// <summary>
        /// Sends verb and waits for ack
        /// </summary>
        public static async Task WriteVerbAsync(this PipeStream pipe, Verbs verb)
        {
            PipeMsgTraceWriteline($"Client Send verb{verb}");
            var buff = new byte[1] { (byte)verb };
            await pipe.WriteTimeout(buff, 1).ContinueWith(async t =>
            {
                await pipe.ReadAcknowledgeAsync();
            });
        }
        public static async Task WriteUInt32(this PipeStream pipe, uint addr)
        {
            PipeMsgTraceWriteline($"{pipe.GetType().Name} WriteUInt32 val={addr}");
            var buf = BitConverter.GetBytes(addr);
            /*
            pipe.Write(buf, 0, buf.Length);
            await Task.Yield();
            /*/
            await pipe.WriteTimeout(buf, buf.Length);
            //*/
        }
        public static async Task WriteUInt64(this PipeStream pipe, UInt64 addr)
        {
            var buf = BitConverter.GetBytes(addr);
            await pipe.WriteTimeout(buf, buf.Length);
        }
        public static async Task<uint> ReadUInt32(this PipeStream pipe)
        {
            PipeMsgTraceWriteline($"{pipe.GetType().Name} {nameof(ReadUInt32)}");
            ///*
            var buff = new byte[4];
            pipe.Read(buff, 0, 4);
            await Task.Yield();
            /*/
            var buff = await pipe.ReadTimeout(4);
            //*/
            var res = BitConverter.ToUInt32(buff, 0);
            return res;
        }
        public static ulong ReadUInt64(this PipeStream pipe)
        {
            var buf = new byte[8];
            pipe.Read(buf, 0, buf.Length);
            var res = BitConverter.ToUInt64(buf, 0);
            return res;
        }
        public static async Task WriteStringAsAsciiAsync(this PipeStream pipe, string str)
        {
            var strlen = string.IsNullOrEmpty(str) ? 0 : str.Length;
            PipeMsgTraceWriteline($"{nameof(WriteStringAsAsciiAsync)} Write len {strlen}");
            await pipe.WriteUInt32((uint)strlen);
            if (strlen > 0)
            {
                var byts = Encoding.ASCII.GetBytes(str);
                PipeMsgTraceWriteline($"{nameof(WriteStringAsAsciiAsync)} Write bytes {byts.Length}");
                await pipe.WriteAsync(byts, 0, byts.Length);
            }
        }
        public static async Task<string> ReadStringAsAsciiAsync(this PipeStream pipe)
        {
            PipeMsgTraceWriteline($"{nameof(ReadStringAsAsciiAsync)} Read len");
            var strlen = await pipe.ReadUInt32();
            PipeMsgTraceWriteline($"{nameof(ReadStringAsAsciiAsync)} Got len = {strlen}");
            var str = string.Empty;
            if (strlen > 0)
            {
                var bytes = new byte[strlen];
                await pipe.ReadAsync(bytes, 0, (int)strlen);
                str = Encoding.ASCII.GetString(bytes);
            }
            return str;
        }

        public static T GetPartitionForObject<T>(this SortedList<uint, T> list, uint obj, uint partitionMask)
        {
            var partition = partitionMask & obj;
            var ndx = list.Keys.FindIndexOfFirstGTorEQTo(partition);
            T result = default(T);
            if (ndx == -1 || ndx >= list.Count)
            {
                result = (T)Activator.CreateInstance(typeof(T));
                list.Add(partition, result);
            }
            else
            {
                result = list.Values[ndx];
            }
            return result;
        }
        /// <summary>
        /// Binary search for 1st item >= key
        /// Returns -1 for empty list
        /// Returns list.count if key > all items
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="sortedList"></param>
        /// <param name="key"></param>
        public static int FindIndexOfFirstGTorEQTo<T>(this IList<T> sortedList, T key) where T : IComparable<T>
        {
            int right = 0;
            if (sortedList.Count == 0) //empty list
            {
                right = -1;
            }
            else
            {
                right = sortedList.Count - 1;
                int left = 0;
                while (right > left)
                {
                    var ndx = (left + right) / 2;
                    var elem = sortedList[ndx];
                    if (elem.CompareTo(key) >= 0)
                    {
                        right = ndx;
                    }
                    else
                    {
                        left = ndx + 1;
                    }
                }
            }
            if (right >= 0) // see if we're beyond the list?
            {
                if (sortedList[right].CompareTo(key) < 0)
                {
                    right = sortedList.Count;
                }
            }
            return right;
        }
    }
}
