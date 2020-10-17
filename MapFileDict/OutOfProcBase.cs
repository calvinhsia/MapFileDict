using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
//https://github.com/calvinhsia/MapFileDict/blob/OutOfProc/MapFileDict/OutOfProc.cs
namespace MapFileDict
{

    public abstract class OutOfProcBase : IDisposable
    {
        private CancellationToken token;
        public string pipeName;

        public uint _sharedMapSize;
        public string _sharedFileMapName;
        MemoryMappedFile _MemoryMappedFileForSharedRegion;
        MemoryMappedViewAccessor _MemoryMappedFileViewForSharedRegion;
        public IntPtr _MemoryMappedRegionAddress;// the address of the shared region. Will probably be different for client and for server
        protected int pidClient;
        protected MyTraceListener mylistener;
        private NamedPipeClientStream _pipeFromClient; // non-null for client
        private NamedPipeServerStream _pipeFromServer; // non-null for server
        public NamedPipeClientStream PipeFromClient { get { return _pipeFromClient; } private set { _pipeFromClient = value; } }
        public NamedPipeServerStream PipeFromServer { get { return _pipeFromServer; } private set { _pipeFromServer = value; } }

        public Dictionary<object, object> dictProperties = new Dictionary<object, object>();
        public Dictionary<Verbs, ActionHolder> _dictVerbs = new Dictionary<Verbs, ActionHolder>();


        public OutOfProcBase() // need a parameterless constructor for Activator when creating server proc
        {

        }
        public OutOfProcBase(int PidClient, CancellationToken token) // this ctor is for client side
        {
            this.Initialize(PidClient, token);
            PipeFromClient = new NamedPipeClientStream(
                serverName: ".",
                pipeName: pipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);
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
        }
        public bool IsSharedRegionCreated()
        {
            return IntPtr.Zero != _MemoryMappedRegionAddress;
        }

        public static Process CreateServer(int pidClient, 
                string exeNameToCreate = "",
                PortableExecutableKinds portableExecutableKinds = PortableExecutableKinds.PE32Plus,
                ImageFileMachine imageFileMachine =  ImageFileMachine.AMD64
            )
        {
            // sometimes using Temp file in temp dir causes System.ComponentModel.Win32Exception: Operation did not complete successfully because the file contains a virus or potentially unwanted software
            var asm64BitFile =string.IsNullOrEmpty(exeNameToCreate) ? new FileInfo(Path.ChangeExtension("tempasm", ".exe")).FullName : exeNameToCreate;
            var createIt = true;
            try
            {
                if (File.Exists(asm64BitFile))
                {
                    File.Delete(asm64BitFile);
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
                    portableExecutableKinds: System.Reflection.PortableExecutableKinds.PE32Plus, // 64 bit
                    imageFileMachine: ImageFileMachine.AMD64,
                    AdditionalAssemblyPaths: string.Empty,
                    logOutput: true
                    );
            }
            var args = $@"""{Assembly.GetAssembly(typeof(OutOfProcBase)).Location
                               }"" {nameof(OutOfProcBase)} {
                                   nameof(OutOfProcBase.MyMainMethod)} {pidClient}";
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
                    PipeFromServer = pipeServer;
                    Trace.WriteLine($"Server: wait for connection IntPtr.Size={IntPtr.Size}");
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
                            try
                            {
                                var verb = (Verbs)buff[0];
                                if (_dictVerbs.ContainsKey(verb))
                                {
                                    var res = await ServerCallClientWithVerb(verb, null);
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
                                    Trace.WriteLine($"Received unknown Verb: this indicates a violation of pipe communications. Comm is broken, so terminating");
                                    throw new Exception($"Received unknown Verb {buff[0]}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Trace.WriteLine($"Exception: terminating process: " + ex.ToString());
                                mylistener?.ForceAddToLog(ex.ToString());
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
        /// <summary>
        /// Called from both client and server. Given a name, 
        /// </summary>
        internal void CreateSharedSection(string memRegionName, uint regionSize)
        {
            if (Process.GetCurrentProcess().Id == pidClient && _MemoryMappedRegionAddress != IntPtr.Zero)
            {
                return;// client and server in same proc, so same region is shared
            }
            _sharedFileMapName = memRegionName;

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
            Trace.WriteLine($"IntPtr.Size = {IntPtr.Size} Shared Memory region address 0x{_MemoryMappedRegionAddress.ToInt64():x16}");
        }
        public void AddVerb(Verbs verb,
            Func<object, Task<object>> actClient,
            Func<object, Task<object>> actServer)
        {
            _dictVerbs[verb] = new ActionHolder()
            {
                actionClientCallServer = actClient,
                actionServerCallClient = actServer
            };
        }
        public async Task<object> ClientCallServerWithVerb(Verbs verb, object parm)
        {
            var result = await _dictVerbs[verb].actionClientCallServer(parm);
            return result;
        }
        public async Task<object> ServerCallClientWithVerb(Verbs verb, object parm)
        {
            var result = await _dictVerbs[verb].actionServerCallClient(parm);
            return result;
        }

        public void Dispose()
        {
            foreach (var kvp in dictProperties)
            {
                if (kvp.Value is IDisposable oDisp)
                {
                    oDisp.Dispose();
                }
            }
            _MemoryMappedFileViewForSharedRegion?.Dispose();
            _MemoryMappedFileForSharedRegion?.Dispose();
            mylistener?.Dispose();
        }
        public class ActionHolder
        {
            public Verbs verb;
            public Func<object, Task<object>> actionClientCallServer;
            public Func<object, Task<object>> actionServerCallClient;
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
        public static async Task WriteAcknowledgeAsync(this PipeStream pipe)
        {
            var verb = new byte[2];
            verb[0] = (byte)Verbs.Acknowledge;
            await pipe.WriteAsync(verb, 0, 1);
        }
        public static async Task ReadAcknowledgeAsync(this PipeStream pipe)
        {
            var buff = new byte[10];
            var len = await pipe.ReadAsync(buff, 0, 1);
            if (len != 1 || buff[0] != (byte)Verbs.Acknowledge)
            {
                Trace.Write($"Didn't get Expected Ack");
            }
        }
        /// <summary>
        /// Sends verb and waits for ack
        /// </summary>
        public static async Task WriteVerbAsync(this PipeStream pipe, Verbs verb)
        {
            pipe.WriteByte((byte)verb);
            await pipe.ReadAcknowledgeAsync();
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
        public static async Task WriteStringAsAsciiAsync(this PipeStream pipe, string str)
        {
            pipe.WriteUInt32((uint)str.Length);
            var byts = Encoding.ASCII.GetBytes(str);
            await pipe.WriteAsync(byts, 0, byts.Length);
        }
        public static async Task<string> ReadStringAsAsciiAsync(this PipeStream pipe)
        {
            var strlen = pipe.ReadUInt32();
            var bytes = new byte[strlen];
            await pipe.ReadAsync(bytes, 0, (int)strlen);
            var str = Encoding.ASCII.GetString(bytes);
            return str;
        }
    }
}
