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
//https://github.com/calvinhsia/MapFileDict/blob/OutOfProc/MapFileDict/OutOfProc.cs
namespace MapFileDict
{
	public enum Verbs
	{
		ServerQuit, // Quit the server process. Sends an Ack, so must wait for ack before done
		Acknowledge, // acknowledge receipt of verb
		CreateSharedMemSection, // create a region of memory that can be shared between the client/server
		GetLog, // get server log entries: will clear all entries so far
		GetString, // very slow
		GetStringSharedMem, // faster
		DoSpeedTest,
		verbRequestData, // len = 1 byte: 0 args
		SendObjAndReferences, // a single obj and a list of it's references
		SendObjAndReferencesInChunks, // yields perf gains: from 5k objs/sec to 1M/sec
		CreateInvertedDictionary, // Create an inverte dict. From the dict of Obj=> list of child objs ref'd by the obj, it creates a new
								  // dict containing every objid: for each is a list of parent objs (those that ref the original obj)
								  // very useful for finding: e.g. who holds a reference to FOO
		QueryParentOfObject, // given an obj, get a list of objs that reference it
	}

	public class OutOfProc : IDisposable
	{
		private CancellationToken token;
		public string pipeName;
		public uint _sharedMapSize;
		public string _sharedFileMapName;
		MemoryMappedFile _MemoryMappedFileForSharedRegion;
		MemoryMappedViewAccessor _MemoryMappedFileViewForSharedRegion;
		public IntPtr _MemoryMappedRegionAddress;// the address of the shared region. Will probably be different for client and for server
		private int pidClient;
		private MyTraceListener mylistener;

		public OutOfProc() // need a parameterless constructor for Activator
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
		}
		public bool IsSharedRegionCreated()
		{
			return IntPtr.Zero != _MemoryMappedRegionAddress;
		}

		public static Process CreateServer(int pidClient)
		{
			var asm64BitFile = new FileInfo(Path.ChangeExtension(Path.GetTempFileName(), ".exe")).FullName;
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
									case Verbs.ServerQuit:
										Trace.WriteLine($"# dict entries = {dictObjRef.Count}");
										await pipeServer.SendAckAsync();
										Trace.WriteLine($"Server got quit message");
										receivedQuit = true;
										break;
									case Verbs.CreateSharedMemSection:
										{
											var sizeRegion = pipeServer.ReadUInt32();
											CreateSharedSection(memRegionName: $"MapFileDictSharedMem_{pidClient}\0", regionSize: sizeRegion);
											Trace.WriteLine($"{Process.GetCurrentProcess().ProcessName} IntPtr.Size = {IntPtr.Size} Shared Memory region address {_MemoryMappedRegionAddress.ToInt64():x16}");
											await pipeServer.SendStringAsAsciiAsync(_sharedFileMapName);
										}
										break;
									case Verbs.SendObjAndReferences:
										{
											var lst = new List<uint>();
											var obj = pipeServer.ReadUInt32();
											var cnt = pipeServer.ReadUInt32();
											for (int i = 0; i < cnt; i++)
											{
												lst.Add(pipeServer.ReadUInt32());
											}
											dictObjRef[obj] = lst;
											Trace.WriteLine($"Server got {nameof(Verbs.SendObjAndReferences)}  {obj:x8} # child = {cnt:n8}");
										}
										await pipeServer.SendAckAsync();
										break;
									case Verbs.SendObjAndReferencesInChunks:
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
									case Verbs.CreateInvertedDictionary:
										dictInverted = InvertDictionary(dictObjRef);
										await pipeServer.SendAckAsync();
										break;
									case Verbs.QueryParentOfObject:
										var objQuery = pipeServer.ReadUInt32();
										if (dictInverted.TryGetValue(objQuery, out var lstParents))
										{
											var numParents = lstParents?.Count;
											Trace.WriteLine($"Server: {objQuery:x8}  NumParents={numParents}");
											if (numParents > 0)
											{
												foreach (var parent in lstParents)
												{
													pipeServer.WriteUInt32(parent);
												}
											}
										}
										pipeServer.WriteUInt32(0); // terminator
										break;
									case Verbs.GetLog:
										if (mylistener != null)
										{
											Trace.WriteLine($"# dict entries = {dictObjRef.Count}");
											Trace.WriteLine($"Server: Getlog #entries = {mylistener.lstLoggedStrings.Count}");
											var strlog = string.Join("\r\n   ", mylistener.lstLoggedStrings);
											mylistener.lstLoggedStrings.Clear();
											var buf = Encoding.ASCII.GetBytes("     ServerLog::" + strlog);
											if ((int)_sharedMapSize >= buf.Length)
											{
												Trace.WriteLine($"Log truncated"); //: get server log more often to reduce, or larger shared region size, or send as string or file or...
											}
											Marshal.Copy(buf, 0, _MemoryMappedRegionAddress, Math.Min((int)_sharedMapSize, buf.Length));
										}
										else
										{
											var buf = Encoding.ASCII.GetBytes("No Logs because in proc");
											Marshal.Copy(buf, 0, _MemoryMappedRegionAddress, buf.Length);
										}
										await pipeServer.SendAckAsync();
										break;
									case Verbs.GetStringSharedMem:
										var len = Marshal.ReadIntPtr(_MemoryMappedRegionAddress);
										var str = Marshal.PtrToStringAnsi(_MemoryMappedRegionAddress + IntPtr.Size, len.ToInt32());
										Trace.WriteLine($"Server: SharedMemStr {str}");
										break;
									case Verbs.GetString:
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
									case Verbs.DoSpeedTest:
										{
											var bufSize = pipeServer.ReadUInt32();
											var buf = new byte[bufSize];
											await pipeServer.ReadAsync(buf, 0, (int)bufSize);
											Trace.WriteLine($"Server: got bytes {bufSize:n0}");
											await pipeServer.SendAckAsync();
										}
										break;
									default:
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
			Trace.WriteLine($"IntPtr.Size = {IntPtr.Size} Shared Memory region address {_MemoryMappedRegionAddress} 0x{_MemoryMappedRegionAddress.ToInt64():x16}");
		}
		/// <summary>
		/// This runs on the client to send the data in chunks to the server
		/// </summary>
		public async Task<Tuple<int, int>> SendObjGraphEnumerableInChunksAsync(NamedPipeClientStream pipeClient, IEnumerable<Tuple<uint, List<uint>>> ienumOGraph)
		{
			var bufChunkSize = 65536;
			var bufChunk = new byte[bufChunkSize + 4]; // leave extra room for null term
			int ndxbufChunk = 0;
			var numChunksSent = 0;
			var numObjs = 0;
			foreach (var tup in ienumOGraph)
			{
				numObjs++;
				var numChildren = tup.Item2?.Count ?? 0;
				var numBytesForThisObj = (1 + 1 + numChildren) * IntPtr.Size; // obj + childCount + children
				if (numBytesForThisObj >= bufChunkSize)
				{
					await SendBufferAsync(); // empty it
					ndxbufChunk = 0;
					bufChunkSize = numBytesForThisObj;
					bufChunk = new byte[numBytesForThisObj + 4];
					// 0450ee60 Roslyn.Utilities.StringTable+Entry[]
					// 0460eea0 Microsoft.CodeAnalysis.VisualBasic.Syntax.InternalSyntax.SyntaxNodeCache+Entry[]
					// 049b90e0 Microsoft.CodeAnalysis.SyntaxNode[]
					Trace.WriteLine($"The cur obj {tup.Item1:x8} size={numBytesForThisObj} is too big for chunk {bufChunkSize}: sending via non-chunk");
					pipeClient.WriteByte((byte)Verbs.SendObjAndReferences);
					pipeClient.WriteUInt32(tup.Item1);
					pipeClient.WriteUInt32((uint)numChildren);

					for (int iChild = 0; iChild < numChildren; iChild++)
					{
						pipeClient.WriteUInt32(tup.Item2[iChild]);
					}
					await pipeClient.GetAckAsync();
				}
				if (ndxbufChunk + numBytesForThisObj >= bufChunk.Length) // too big for cur buf?
				{
					await SendBufferAsync(); // empty it
					ndxbufChunk = 0;
				}
				{
					var b1 = BitConverter.GetBytes(tup.Item1);
					Array.Copy(b1, 0, bufChunk, ndxbufChunk, b1.Length);
					ndxbufChunk += b1.Length;

					b1 = BitConverter.GetBytes(numChildren);
					Array.Copy(b1, 0, bufChunk, ndxbufChunk, b1.Length);
					ndxbufChunk += b1.Length;
					for (int iChild = 0; iChild < numChildren; iChild++)
					{
						b1 = BitConverter.GetBytes(tup.Item2[iChild]);
						Array.Copy(b1, 0, bufChunk, ndxbufChunk, b1.Length);
						ndxbufChunk += b1.Length;
					}
				}
			}
			if (ndxbufChunk > 0) // leftovers
			{
				Trace.WriteLine($"Client: send leftovers {ndxbufChunk}");
				await SendBufferAsync();
			}
			return Tuple.Create<int, int>(numObjs, numChunksSent);
			async Task SendBufferAsync()
			{
				bufChunk[ndxbufChunk++] = 0; // null terminating int32
				bufChunk[ndxbufChunk++] = 0;
				bufChunk[ndxbufChunk++] = 0;
				bufChunk[ndxbufChunk++] = 0;
				pipeClient.WriteByte((byte)Verbs.SendObjAndReferencesInChunks);
				pipeClient.WriteUInt32((uint)ndxbufChunk); // size of buf
				pipeClient.Write(bufChunk, 0, ndxbufChunk);
				await pipeClient.GetAckAsync();
				numChunksSent++;
			}
		}

		public static Dictionary<uint, List<uint>> InvertDictionary(Dictionary<uint, List<uint>> dictOGraph)
		{
			var dictInvert = new Dictionary<uint, List<uint>>(capacity: dictOGraph.Count); // obj ->list of objs that reference it
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
			_MemoryMappedFileViewForSharedRegion?.Dispose();
			_MemoryMappedFileForSharedRegion?.Dispose();
			mylistener?.Dispose();
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
			verb[0] = (byte)Verbs.Acknowledge;
			await pipe.WriteAsync(verb, 0, 1);
		}
		public static async Task GetAckAsync(this PipeStream pipe)
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
		public static async Task SendVerb(this PipeStream pipe, Verbs verb)
		{
			pipe.WriteByte((byte)verb);
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
		public static async Task SendStringAsAsciiAsync(this PipeStream pipe, string str)
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
