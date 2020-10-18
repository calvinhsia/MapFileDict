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
        public bool CreateServerOutOfProc = true; // or can be inproc for testing
        public string ExistingExeNameToUseForServer = string.Empty;
        public string exeNameToCreate = string.Empty; // defaults to "tempasm.exe" in curdir
        public PortableExecutableKinds portableExecutableKinds = PortableExecutableKinds.PE32Plus;
        public ImageFileMachine imageFileMachine = ImageFileMachine.AMD64;
        public string AdditionalAssemblyPaths = string.Empty;

        public int PidClient;

    }
    public abstract class OutOfProcBase : IDisposable
    {
        private CancellationToken token;
        public OutOfProcOptions Options;
        private string pipeName;

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
            pipeName = $"MapFileDictPipe_{pidClient}";
            if (Process.GetCurrentProcess().Id == options.PidClient) //we're the client process?
            {
                PipeFromClient = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: pipeName,
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
                mylistener = new MyTraceListener();
                Trace.Listeners.Add(mylistener);
                Trace.WriteLine($"Server Process IntPtr.Size = {IntPtr.Size} Trace Listener created");
                DoServerLoopTask = DoServerLoopAsync();
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
                    portableExecutableKinds: Options.portableExecutableKinds, // 64 bit
                    imageFileMachine: Options.imageFileMachine,
                    AdditionalAssemblyPaths: Options.AdditionalAssemblyPaths,
                    logOutput: true
                    );
            }
            var typeToInstantiateName = this.GetType().Name; // we need to get the name of the derived class to instantiate

            var args = $@"""{Assembly.GetAssembly(typeof(OutOfProcBase)).Location
                               }"" {nameof(OutOfProcBase)} {
                                   nameof(OutOfProcBase.MyMainMethod)} {pidClient} {typeToInstantiateName}";
            Trace.WriteLine($"args = {args}");
            ProcServer = Process.Start(
                asm64BitFile,
                args);
            Trace.WriteLine($"Client: started server PidClient={pidClient} PidServer={ProcServer.Id} {typeToInstantiateName}");
        }
        /// <summary>
        /// This is entry point in the 64 bit server process. Create an execution context for the asyncs
        /// </summary>
        public static async Task MyMainMethod(int pidClient, string typeToInstantiateName)
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
                    var args = new object[] { new OutOfProcOptions() { PidClient = pidClient }, new CancellationToken() };
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
        public Task ConnectAsync(CancellationToken token)
        {
            return PipeFromClient.ConnectAsync(token);
        }

        async Task DoServerLoopAsync()
        {
            try
            {
                Trace.WriteLine("Server: Starting ");
                await tcsAddedVerbs.Task;
                PipeFromServer = new NamedPipeServerStream(
                    pipeName: pipeName,
                    direction: PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous
                    );
                {
                    Trace.WriteLine($"Server: wait for connection IntPtr.Size={IntPtr.Size}");
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
                                var verb = (Verbs)PipeFromServer.ReadByte();
                                if (_dictVerbs.ContainsKey(verb))
                                {
                                    var res = await ServerDoVerb(verb, null);
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
                                    throw new Exception($"Received unknown Verb {verb}");
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
            finally
            {
                this.Dispose();
            }

            Trace.WriteLine("Server: exiting servertask");
        }
        /// <summary>
        /// Called from both client and server. Given a name, creates memory region of specified size (mult 64k) that can be addressed by each process
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
            Func<object, Task<object>> actClientSendVerb,
            Func<object, Task<object>> actServerDoVerb)
        {
            _dictVerbs.Add(verb, new ActionHolder() // throws if already exists
            {
                actionClientSendVerb = actClientSendVerb,
                actionServerDoVerb = actServerDoVerb
            });
        }
        public Task<object> ClientSendVerb(Verbs verb, object parm)
        {
            return _dictVerbs[verb].actionClientSendVerb(parm);
        }
        public Task<object> ServerDoVerb(Verbs verb, object parm)
        {
            return _dictVerbs[verb].actionServerDoVerb(parm);
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
            PipeFromClient?.Dispose();
            PipeFromServer?.Dispose();
            _MemoryMappedFileViewForSharedRegion?.Dispose();
            _MemoryMappedFileForSharedRegion?.Dispose();
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
