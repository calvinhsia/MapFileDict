using MapFileDict;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MapFileDictTest
{
	[TestClass]
	public class OOPCommTests : MyTestBase
	{
		string fnameObjectGraph = @"c:\users\calvinh\Desktop\ObjGraph.txt"; // 2.1Megs from "VSDbgData\VSDbgTestDumps\MSSln22611\MSSln22611.dmp
		uint WpfTextView = 0x362b72e0;
		uint TextBuffer = 0x3629712c;
		uint SystemStackOverflowException = 0x034610b4;// System.StackOverflowException

		[TestMethod]
		public async Task OOPGetObjectGraph()
		{
			var dictOGraph = await ReadObjectGraphAsync(fnameObjectGraph);

			Dictionary<uint, List<uint>> dictInvert = OutOfProc.InvertDictionary(dictOGraph);

			Trace.WriteLine($"Inverted dict {dictInvert.Count:n0}"); // System.Object, String.Empty have the most parents: e.g. 0xaaaa
																	 //362b72e0  Microsoft.VisualStudio.Text.Editor.Implementation.WpfTextView   (
																	 //    3629712c  Microsoft.VisualStudio.Text.Implementation.TextBuffer

			void ShowParents(uint obj, string desc)
			{
				var lstTxtBuffer = dictInvert[obj];
				Trace.WriteLine($"Parents of {desc}   {obj:x8}");
				foreach (var itm in lstTxtBuffer)
				{
					Trace.WriteLine($"   {itm:x8}");
				}
			}
			ShowParents(WpfTextView, "WpfTextView");
			ShowParents(TextBuffer, "TextBuffer");
			VerifyLogStrings(@"
Parents of WpfTextView   362b72e0
03b17f7c
1173a870
3618b1e4
");
			// 03b17f7c  System.Windows.EffectiveValueEntry[]
			// 1173a870 Microsoft.VisualStudio.Text.Editor.ITextView[]
			//1173aa68 Microsoft.VisualStudio.Text.Editor.IWpfTextView[]
			// 3618b1e4 Microsoft.VisualStudio.Editor.Implementation.VsCodeWindowAdapter
		}

		IEnumerable<Tuple<uint, List<uint>>> GetObjectGraphIEnumerable()
		{
			using (var fs = new StreamReader(fnameObjectGraph))
			{
				List<uint> lstChildren = null;
				var curObjId = 0U;
				while (!fs.EndOfStream)
				{
					var line = fs.ReadLine();
					var lineParts = line.Split(" ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
					var oidTemp = uint.Parse(lineParts[0].Trim(), System.Globalization.NumberStyles.AllowHexSpecifier);
					if (!line.StartsWith(" "))
					{
						if (curObjId != 0)
						{
							yield return Tuple.Create<uint, List<uint>>(curObjId, lstChildren);
						}
						lstChildren = null;
						curObjId = oidTemp;
					}
					else
					{
						if (lstChildren == null)
						{
							lstChildren = new List<uint>();
						}
						lstChildren.Add(oidTemp);
					}
				}
				yield return Tuple.Create<uint, List<uint>>(curObjId, lstChildren);
			}
		}

		private async Task<Dictionary<uint, List<uint>>> ReadObjectGraphAsync(string fnameObjectGraph)
		{
			/*MSSln22611\MSSln22611.dmp
                        {
                            var sb = new StringBuilder();
                            foreach (var entry in _heap.EnumerateObjectAddresses())
                            {
                                var candidateObjEntry = entry;
                                var candType = _heap.GetObject(candidateObjEntry).Type;
                                if (candType != null)
                                {
                                    var clrObj = new ClrObject(candidateObjEntry, candType);
                                    sb.AppendLine($"{clrObj.Address:x8} {candType}");
                                    foreach (var oidChild in clrObj.EnumerateObjectReferences())
                                    {
                                        var childType = _heap.GetObject(oidChild).Type;
                                        sb.AppendLine($"   {oidChild.Address:x8}  {childType}");
                                    }
                                }
                            }
                            File.AppendAllText(@"c:\users\calvinh\Desktop\objgraph.txt", sb.ToString());
                        }

            12075104 Microsoft.Build.Construction.XmlAttributeWithLocation
               120750d4  Microsoft.Build.Construction.XmlElementWithLocation
               12074540  System.Xml.XmlName
               1207511c  System.Xml.XmlText
               12123cc0  Microsoft.Build.Construction.ElementLocation+SmallElementLocation
            1207511c System.Xml.XmlText
               12075104  Microsoft.Build.Construction.XmlAttributeWithLocation
               1207511c  System.Xml.XmlText
               1207502c  System.String
            12075130 Microsoft.Build.Construction.XmlAttributeWithLocation
               120750d4  Microsoft.Build.Construction.XmlElementWithLocation
               12074f5c  System.Xml.XmlName
               120751cc  System.Xml.XmlText
               12075148  Microsoft.Build.Construction.ElementLocation+SmallElementLocation
            12075148 Microsoft.Build.Construction.ElementLocation+SmallElementLocation
               03461228  System.String
            12075158 System.String
            120751c0 Free
            120751cc System.Xml.XmlText
               12075130  Microsoft.Build.Construction.XmlAttributeWithLocation
               120751cc  System.Xml.XmlText
               12075158  System.String
            120751e0 System.Collections.ArrayList
               120751f8  System.Object[]
            120751f8 System.Object[]
               12075104  Microsoft.Build.Construction.XmlAttributeWithLocation
               12075130  Microsoft.Build.Construction.XmlAttributeWithLocation
            12075214 Microsoft.Build.Construction.ElementLocation+SmallElementLocation
               120739c4  System.String
            12075224 System.String
            120752a0 Microsoft.Build.Construction.ElementLocation+SmallElementLocation
               120739c4  System.String
            120752b0 Microsoft.Build.Construction.ProjectElement+WrapperForProjectRootElement
               12073a68  Microsoft.Build.Construction.ProjectRootElement
            120752dc System.Xml.XmlAttributeCollection
               120743c8  Microsoft.Build.Construction.XmlElementWithLocation
            120752ec Microsoft.Build.Construction.ProjectPropertyGroupElement
               12073a68  Microsoft.Build.Construction.ProjectRootElement
               03461228  System.String
               120754b4  Microsoft.Build.Construction.ProjectImportElement
               120743c8  Microsoft.Build.Construction.XmlElementWithLocation
               12075324  Microsoft.Build.Construction.ProjectPropertyElement
               12075488  Microsoft.Build.Construction.ProjectPropertyElement
            12075314 Microsoft.Build.Construction.ElementLocation+SmallElementLocation
               120739c4  System.String
            */
			var dictOGraph = new Dictionary<uint, List<uint>>();
			int nObjsWithAtLeastOneKid = 0;
			int nChildObjs = 0;
			using (var fs = new StreamReader(fnameObjectGraph))
			{
				List<uint> lstChildren = null;
				var curObjId = 0U;
				while (!fs.EndOfStream)
				{
					var line = await fs.ReadLineAsync();
					var lineParts = line.Split(" ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
					var oidTemp = uint.Parse(lineParts[0].Trim(), System.Globalization.NumberStyles.AllowHexSpecifier);
					if (!line.StartsWith(" "))
					{
						lstChildren = null;
						dictOGraph[oidTemp] = lstChildren;
						curObjId = oidTemp;
					}
					else
					{
						if (lstChildren == null)
						{
							nObjsWithAtLeastOneKid++;
							lstChildren = new List<uint>();
							dictOGraph[curObjId] = lstChildren;
						}
						nChildObjs++;
						lstChildren.Add(oidTemp);
					}
				}
			}
			Trace.WriteLine($"{nameof(ReadObjectGraphAsync)} Read {dictOGraph.Count:n0} objs. #Objs with at least 1 child = {nObjsWithAtLeastOneKid:n0}   TotalChildObjs = {nChildObjs:n0}");
			// 12 secs to read in graph Read 1,223,023 objs. #Objs with at least 1 child = 914,729   TotalChildObjs = 2,901,660
			return dictOGraph;
		}
		[TestMethod]
		public async Task OOPSendBigObjRefs()
		{
			try
			{
				var pidClient = Process.GetCurrentProcess().Id;
				var procServer = OutOfProc.CreateServer(pidClient);
				await DoServerStuff(procServer, pidClient, async (pipeClient, oop) =>
				{
					var sw = Stopwatch.StartNew();
					var ienumOGraph = GetObjectGraphIEnumerable();
					var tup = await oop.SendObjGraphEnumerableInChunksAsync(pipeClient, ienumOGraph);
					int numObjs = tup.Item1;
					var numChunksSent = tup.Item2;
					// the timing includes parsing the text file for obj graph
					Trace.WriteLine($"Sent {numObjs}  #Chunks = {numChunksSent} Objs/Sec = {numObjs / sw.Elapsed.TotalSeconds:n2}"); // 5k/sec

					pipeClient.WriteByte((byte)Verbs.CreateInvertedDictionary);
					await pipeClient.GetAckAsync();
					Trace.WriteLine($"Inverted Dictionary");

					DoShowResultsFromQueryForParents(pipeClient, SystemStackOverflowException, nameof(SystemStackOverflowException));
					DoShowResultsFromQueryForParents(pipeClient, WpfTextView, nameof(WpfTextView));

					Trace.WriteLine($"Got log from server\r\n" + await GetLogFromServer(pipeClient, oop));
				});
			}
			catch (Exception ex)
			{
				Trace.WriteLine(ex.ToString());
				throw;
			}
			VerifyLogStrings(@"
IntPtr.Size = 8 Shared Memory region
# dict entries = 1223023
SystemStackOverflowException 362b72e0  has 0 parents
WpfTextView 362b72e0  has 221 parents
");
		}


		[TestMethod]
		public async Task OOPSendObjRefsInProc()
		{
			var cts = new CancellationTokenSource();
			using (var oop = new OutOfProc(Process.GetCurrentProcess().Id, cts.Token))
			{

				var taskServer = oop.DoServerLoopAsync();

				var taskClient = DoTestClientAsync(oop, async (pipeClient) =>
				{
					var sw = Stopwatch.StartNew();
					var ienumOGraph = GetObjectGraphIEnumerable();
					var tup = await oop.SendObjGraphEnumerableInChunksAsync(pipeClient, ienumOGraph);
					int numObjs = tup.Item1;
					var numChunksSent = tup.Item2;
					// the timing includes parsing the text file for obj graph
					Trace.WriteLine($"Sent {numObjs}  #Chunks = {numChunksSent} Objs/Sec = {numObjs / sw.Elapsed.TotalSeconds:n2}"); // 5k/sec

					pipeClient.WriteByte((byte)Verbs.CreateInvertedDictionary);
					await pipeClient.GetAckAsync();
					Trace.WriteLine($"Inverted Dictionary");
					DoShowResultsFromQueryForParents(pipeClient, SystemStackOverflowException, nameof(SystemStackOverflowException));

					DoShowResultsFromQueryForParents(pipeClient, WpfTextView, nameof(WpfTextView));
				});
				var delaySecs = Debugger.IsAttached ? 3000 : 40;
				var tskDelay = Task.Delay(TimeSpan.FromSeconds(delaySecs));
				await Task.WhenAny(new[] { tskDelay, taskClient });
				if (tskDelay.IsCompleted)
				{
					Trace.WriteLine($"Delay {delaySecs} completed: cancelling server");
					cts.Cancel();
				}
				await taskServer;
				Trace.WriteLine($"Done");
				Assert.IsTrue(taskServer.IsCompleted);
			}
			VerifyLogStrings(@"
362b72e0  NumParents=221
# dict entries = 1223023
");
		}
		private void DoShowResultsFromQueryForParents(NamedPipeClientStream pipeClient, uint objId, string desc)
		{
			var lstParents = QueryServerForParent(pipeClient, objId);
			Trace.WriteLine($"{desc} {WpfTextView:x8}  has {lstParents.Count} parents");

			foreach (var parent in lstParents.Take(20))
			{
				Trace.WriteLine($"A Parent of {desc} {objId:x8} is {parent:x8}");
			}
		}

		private List<uint> QueryServerForParent(NamedPipeClientStream pipeClient, uint objId)
		{
			Trace.WriteLine($"Query Parent {objId:x8}");
			pipeClient.WriteByte((byte)Verbs.QueryParentOfObject);
			pipeClient.WriteUInt32(objId);
			var lstParents = new List<uint>();
			while (true)
			{
				var parent = pipeClient.ReadUInt32();
				if (parent == 0)
				{
					break;
				}
				lstParents.Add(parent);
			}
			return lstParents;
		}

		[TestMethod]
		public async Task OOPTest()
		{
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
		public async Task OOPTestConsoleApp()
		{
			var pidClient = Process.GetCurrentProcess().Id;
			var consapp = "ConsoleAppTest.exe";
			var procServer = Process.Start(consapp, $"{pidClient}");
			Trace.WriteLine($"Client: started server {procServer.Id}");
			await DoTestServerStuffAsync(pidClient, procServer);

			VerifyLogStrings(@"
Got log from server
Server Trace Listener created
Server: Getlog #entries
IntPtr.Size = 4 Shared Memory region address
IntPtr.Size = 8 Shared Memory region address
");
		}

		[TestMethod]
		public async Task OOPTestGenAsm()
		{
			var pidClient = Process.GetCurrentProcess().Id;

			var procServer = OutOfProc.CreateServer(pidClient);
			Trace.WriteLine($"Client: started server PidClient={pidClient} PidServer={procServer.Id}");
			await DoTestServerStuffAsync(pidClient, procServer);

			VerifyLogStrings(@"
IntPtr.Size = 4 Shared Memory region address
IntPtr.Size = 8 Shared Memory region address
");
		}

		private async Task DoTestServerStuffAsync(int pidClient, Process procServer)
		{
			await DoServerStuff(procServer, pidClient, async (pipeClient, oop) =>
			{
				await DoCreateSharedMemRegionAsync(pipeClient, oop, sharedRegionSize: 65536U);
				//                    await Task.Delay(5000);
				var verb = new byte[2] { 1, 1 };
				//                await Task.Delay(10000);
				for (int i = 0; i < 5; i++)
				{
					verb[0] = (byte)Verbs.verbRequestData;
					await pipeClient.WriteAsync(verb, 0, 1);
					var bufReq = new byte[100];
					var buflen = await pipeClient.ReadAsync(bufReq, 0, bufReq.Length);
					var readStr = Encoding.ASCII.GetString(bufReq, 0, buflen);
					Trace.WriteLine($"Client req data from server: {readStr}");
				}

				Trace.WriteLine($"Client: GetLog");
				verb[0] = (byte)Verbs.GetLog;
				await pipeClient.WriteAsync(verb, 0, 1);
				await pipeClient.GetAckAsync();
				var logstrs = Marshal.PtrToStringAnsi(oop._MemoryMappedRegionAddress);
				Trace.WriteLine($"Got log from server\r\n" + logstrs);

			});
		}

		private async Task DoServerStuff(Process procServer, int pidClient, Func<NamedPipeClientStream, OutOfProc, Task> func)
		{
			var sw = Stopwatch.StartNew();
			var cts = new CancellationTokenSource();

			using (var oop = new OutOfProc(pidClient, cts.Token))
			{
				using (var pipeClient = new NamedPipeClientStream(
					serverName: ".",
					pipeName: oop.pipeName,
					direction: PipeDirection.InOut,
					options: PipeOptions.Asynchronous))
				{
					Trace.WriteLine($"Client: starting to connect");
					await pipeClient.ConnectAsync(cts.Token);
					Trace.WriteLine($"Client: connected");
					await func(pipeClient, oop);
					await pipeClient.SendVerb((byte)Verbs.ServerQuit);
				}
			}
			var didKill = false;
			while (!procServer.HasExited)
			{
				Trace.WriteLine($"Waiting for cons app to exit");
				await Task.Delay(TimeSpan.FromMilliseconds(1000));
				if (!Debugger.IsAttached && sw.Elapsed.TotalSeconds > 60 * 5)
				{
					Trace.WriteLine($"Killing server process");
					procServer.Kill();
					didKill = true;
					break;
				}
			}
			Trace.WriteLine($"Done in {sw.Elapsed.TotalSeconds:n2}");
			Assert.IsFalse(didKill, "Had to kill server");
		}


		[TestMethod]
		public async Task OOPTestInProc()
		{
			var cts = new CancellationTokenSource();
			using (var oop = new OutOfProc(Process.GetCurrentProcess().Id, cts.Token))
			{

				var taskServer = oop.DoServerLoopAsync();

				var taskClient = DoTestClientAsync(oop, async (pipeClient) =>
				{
					await DoBasicCommTests(oop, pipeClient, cts.Token);
				});
				var nDelaySecs = Debugger.IsAttached ? 3000 : 50;
				var tskDelay = Task.Delay(TimeSpan.FromSeconds(nDelaySecs));
				await Task.WhenAny(new[] { tskDelay, taskClient });
				if (tskDelay.IsCompleted)
				{
					Trace.WriteLine($"Delay {nDelaySecs} secs completed: cancelling server");
					cts.Cancel();
				}
				await taskServer;
				Trace.WriteLine($"Done");
				Assert.IsTrue(taskServer.IsCompleted);
			}
			VerifyLogStrings(@"
Server: SharedMemStr StrSharedMem
Client: sending quit
Server got quit message
sent message..requesting data
");
		}
		private async Task DoTestClientAsync(OutOfProc oop, Func<NamedPipeClientStream, Task> actionAsync)
		{
			Trace.WriteLine("Starting Client");
			var cts = new CancellationTokenSource();
			using (var pipeClient = new NamedPipeClientStream(
				serverName: ".",
				pipeName: oop.pipeName,
				direction: PipeDirection.InOut,
				options: PipeOptions.Asynchronous))
			{
				try
				{
					await pipeClient.ConnectAsync(cts.Token);

					await DoCreateSharedMemRegionAsync(pipeClient, oop, 65536);

					await actionAsync(pipeClient);
					Trace.WriteLine("Client: sending quit");
					await pipeClient.SendVerb((byte)Verbs.ServerQuit);
				}
				catch (Exception ex)
				{
					Trace.WriteLine(ex.ToString());
					throw;
				}
			}
		}

		private async Task DoCreateSharedMemRegionAsync(NamedPipeClientStream pipeClient, OutOfProc oop, uint sharedRegionSize)
		{
			pipeClient.WriteByte((byte)Verbs.CreateSharedMemSection); //ask the server to create a shared region
			pipeClient.WriteUInt32(sharedRegionSize);
			var memRegionName = await pipeClient.ReadStringAsAsciiAsync();
			oop.CreateSharedSection(memRegionName, sharedRegionSize); // now map that region into the client
		}

		async Task DoBasicCommTests(OutOfProc oop, NamedPipeClientStream pipeClient, CancellationToken token)
		{
			var verb = new byte[2] { 1, 1 };
			for (int i = 0; i < 5; i++)
			{
				{
					var strBuf = Encoding.ASCII.GetBytes($"MessageString {i}");
					var buf = new byte[strBuf.Length + 1];
					buf[0] = (byte)Verbs.GetString;
					Array.Copy(strBuf, 0, buf, 1, strBuf.Length);
					Trace.WriteLine("Client: sending message");
					await pipeClient.WriteAsync(buf, 0, buf.Length);
					Trace.WriteLine($"Client: sent message..requesting data");
				}
				{
					var strBuf = Encoding.ASCII.GetBytes($"StrSharedMem {i}");
					verb[0] = (byte)Verbs.GetStringSharedMem;
					Marshal.WriteInt32(oop._MemoryMappedRegionAddress, strBuf.Length);
					Marshal.Copy(strBuf, 0, oop._MemoryMappedRegionAddress + IntPtr.Size, strBuf.Length);
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
				var nIter = 5U;
				var bufSize = 1024 * 1024 * 1024;
				var bufSpeed = new byte[bufSize];
				var sw = Stopwatch.StartNew();
				for (int iter = 0; iter < nIter; iter++)
				{
					Trace.WriteLine($"Sending buf {bufSize:n0} Iter={iter}");
					pipeClient.WriteByte((byte)Verbs.DoSpeedTest);
					pipeClient.WriteUInt32((uint)bufSize);
					await pipeClient.WriteAsync(bufSpeed, 0, bufSpeed.Length);
					await pipeClient.GetAckAsync();
				}
				var bps = (double)bufSize * nIter / sw.Elapsed.TotalSeconds;
				Trace.WriteLine($"BytesPerSec = {bps:n0}"); // 1.4 G/Sec
			}
			//                if (oop.option == OOPOption.InProcTestLogging)
			{

				Trace.WriteLine($"Got log from server\r\n" + await GetLogFromServer(pipeClient, oop));
			}

		}
		internal async Task<string> GetLogFromServer(NamedPipeClientStream pipeClient, OutOfProc oop)
		{
			Trace.WriteLine($"getting log from server");
			if (!oop.IsSharedRegionCreated())
			{
				await DoCreateSharedMemRegionAsync(pipeClient, oop, sharedRegionSize: 65536);
			}

			pipeClient.WriteByte((byte)Verbs.GetLog);
			await pipeClient.GetAckAsync();
			var logstrs = Marshal.PtrToStringAnsi(oop._MemoryMappedRegionAddress);
			return logstrs;
		}
	}
}
