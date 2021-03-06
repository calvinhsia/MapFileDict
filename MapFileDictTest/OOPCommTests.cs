﻿using MapFileDict;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MapFileDictTest
{
    [TestClass]
    public class OOPCommTests : MyTestBase
    {
        string fnameObjectRefGraph = @"c:\users\calvinh\Desktop\ObjGraph.txt"; // 2.1Megs from "VSDbgData\VSDbgTestDumps\MSSln22611\MSSln22611.dmp
        uint WpfTextView = 0x362b72e0;
        uint MemoryMappedViewAccessor = 0x120cd2dc; //120cd2dc  System.IO.MemoryMappedFiles.MemoryMappedViewAccessor
        uint SystemStackOverflowException = 0x034610b4;// System.StackOverflowException
        uint ShortSimpleTextStorage = 0x36197924; // many parents in parent chain
        /*
Children of "-> builder = Microsoft.VisualStudio.Text.Implementation.BinaryStringRebuilder 0x36297110"
-> builder = Microsoft.VisualStudio.Text.Implementation.BinaryStringRebuilder 0x36297110
 -> _left = Microsoft.VisualStudio.Text.Implementation.BinaryStringRebuilder 0x3625bfb4
  -> _left = Microsoft.VisualStudio.Text.Implementation.BinaryStringRebuilder 0x362126f0
   -> _left = Microsoft.VisualStudio.Text.Implementation.BinaryStringRebuilder 0x361e1484
    -> _left = Microsoft.VisualStudio.Text.Implementation.BinaryStringRebuilder 0x361b8968
     -> _left = Microsoft.VisualStudio.Text.Implementation.BinaryStringRebuilder 0x3619ff20
      -> _left = Microsoft.VisualStudio.Text.Implementation.SimpleStringRebuilder 0x36197bf4
       -> _storage = Microsoft.VisualStudio.Text.Implementation.ShortSimpleTextStorage 0x36197924
        -> _content = System.String 0x3618f914 Imports System.IO
         */
        [TestMethod]
        public void OOPTestDictSize()
        {
            try
            {
                for (var p = 0; p < 32; p++)
                {
                    var pow = (uint)Math.Pow(2, 32 - p);
                    var mask = (uint)(~pow) + 1;
                    Trace.WriteLine($" {p,3} {pow:x8}  {mask:x8}   {pow:n0}");
                }
                for (var p = 9; p < 45; p++)
                {
                    var size = (int)Math.Pow(2, p);
                    Trace.WriteLine($" p={p} size = {size:n0}");
                    //                    var lst = new List<int>(size); // oom at 2^29 = 5.37e6
                    //                    var dict = new Dictionary<int, int>(size); // p=27 size = 134,217,728
                    //                    var arr = new int[size]; // p=29 size = 536,870,912
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.ToString());
            }
        }
        [TestMethod]
        //        [ExpectedException(typeof(TimeoutException))]
        public async Task OOPTestConnectionTimeout()
        {
            //var pidClient = Process.GetCurrentProcess().Id;
            //var procServer = Process.Start(consapp, $"{pidClient}");
            //Trace.WriteLine($"Client: started server {procServer.Id}");
            var options = new OutOfProcOptions()
            {
                CreateServerOutOfProc = true,
                ConnectTimeout = 0
            };
            try
            {
                await DoServerStuff(options, async (oop) =>
                {
                    Trace.WriteLine($"Server Logs: " + await oop.ClientSendVerbAsync(Verbs.GetLog, null));
                });

            }
            catch (TimeoutException ex)
            {
                Trace.WriteLine($"Timeout " + ex.ToString());
            }

            VerifyLogStrings(@"
Timeout System.TimeoutException: The operation has timed out.
Killing server process
");
        }

        // test reading the object ref graph from disk
        [TestMethod]
        public async Task OOPGetObjectGraph()
        {
            var dictOGraph = await ReadObjectRefGraphAsync(fnameObjectRefGraph);
            var slistDictOGraph = new SortedList<uint,Dictionary<uint, List<uint>>>(); // only one partition in this test, so Mask doesn't matter
            var partitionMask = 0xF0000000;
            slistDictOGraph.Add(0, dictOGraph);
            SortedList<uint, Dictionary<uint, List<uint>>> slistInvert = OutOfProc.InvertDictionary(slistDictOGraph, partitionMask);

            Trace.WriteLine($"Inverted dict {slistInvert.Values[0]:n0}"); // System.Object, String.Empty have the most parents: e.g. 0xaaaa
                                                                     //362b72e0  Microsoft.VisualStudio.Text.Editor.Implementation.WpfTextView   (
                                                                     //    3629712c  Microsoft.VisualStudio.Text.Implementation.TextBuffer

            void ShowParents(uint obj, string desc)
            {
                var dictInvert = slistInvert.GetPartitionForObject(obj, partitionMask);
                var lstTxtBuffer = dictInvert[obj];
                Trace.WriteLine($"Parents of {desc}   {obj:x8}");
                foreach (var itm in lstTxtBuffer)
                {
                    Trace.WriteLine($"   {itm:x8}");
                }
            }
            ShowParents(MemoryMappedViewAccessor, nameof(MemoryMappedViewAccessor));
            ShowParents(WpfTextView, nameof(WpfTextView));
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

        /// <summary>
        /// read the object ref graph from disk (just obj and refs, no types)
        /// </summary>
        /// <returns></returns>
        IEnumerable<Tuple<uint, List<uint>>> GetObjectGraphIEnumerable()
        {
            using (var fs = new StreamReader(fnameObjectRefGraph))
            {
                var hashset = new HashSet<uint>(); // EnumerateObjectReferences sometimes has duplicate children <sigh>
                var curObjId = 0U;
                while (!fs.EndOfStream)
                {
                    var line = fs.ReadLine();
                    var lineParts = line.Split(" ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                    if (lineParts[0] == "Seg")
                    {
                        continue;
                    }
                    var oidTemp = uint.Parse(lineParts[0].Trim(), System.Globalization.NumberStyles.AllowHexSpecifier);
                    if (!line.StartsWith(" "))
                    {
                        if (curObjId != 0)
                        {
                            yield return Tuple.Create<uint, List<uint>>(curObjId, hashset.ToList());
                        }
                        hashset = new HashSet<uint>();
                        curObjId = oidTemp;
                    }
                    else
                    {
                        hashset.Add(oidTemp);
                    }
                }
                yield return Tuple.Create<uint, List<uint>>(curObjId, hashset.ToList());
            }
        }

        private async Task<Dictionary<uint, List<uint>>> ReadObjectRefGraphAsync(string fnameObjectGraph)
        {
            /*MSSln22611\MSSln22611.dmp
                        {
                            var sb = new StringBuilder();
                            foreach (var seg in _heap.Segments)
                            {
                                sb.AppendLine($"Seg {seg.Start:x8} {seg.End:x8}");
                            }
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
Children of "<- System.IO.MemoryMappedFiles.MemoryMappedViewAccessor  120cd2dc"
<- System.IO.MemoryMappedFiles.MemoryMappedViewAccessor  120cd2dc
 <- Microsoft.CodeAnalysis.Host.TemporaryStorageServiceFactory+MemoryMappedInfo accessor 120cd280
  <- Microsoft.CodeAnalysis.Host.TemporaryStorageServiceFactory+TemporaryStorageService+TemporaryStreamStorage memoryMappedInfo 120cd270
   <- Microsoft.CodeAnalysis.Host.ITemporaryStreamStorage[]  120cd430
    <- System.Collections.Generic.List<Microsoft.CodeAnalysis.Host.ITemporaryStreamStorage> _items 12073ea0
     <- Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem.VisualStudioMetadataReferenceManager+RecoverableMetadataValueSource storages 120cdbac
      <- System.Collections.Generic.Dictionary+Entry<Roslyn.Utilities.FileKey,Roslyn.Utilities.ValueSource<Microsoft.CodeAnalysis.AssemblyMetadata>>[]  125b4538
  <- Microsoft.CodeAnalysis.Host.TemporaryStorageServiceFactory+MemoryMappedInfo+SharedReadableStream owner 120cd33c
   <- Microsoft.CodeAnalysis.ModuleMetadata  120cd364
    <- Microsoft.CodeAnalysis.ModuleMetadata[]  120cdb6c
     <- Microsoft.CodeAnalysis.AssemblyMetadata lazyPublishedModules 120cdb7c
     <- Microsoft.CodeAnalysis.AssemblyMetadata lazyPublishedModules 120cdb7c
     <- Microsoft.CodeAnalysis.AssemblyMetadata+Data Modules 120cde88
 <- Microsoft.CodeAnalysis.Host.TemporaryStorageServiceFactory+MemoryMappedInfo+SharedReadableStream accessor 120cd33c
  <- Microsoft.CodeAnalysis.ModuleMetadata  120cd364
   <- Microsoft.CodeAnalysis.ModuleMetadata[]  120cdb6c
    <- Microsoft.CodeAnalysis.AssemblyMetadata lazyPublishedModules 120cdb7c
    <- Microsoft.CodeAnalysis.AssemblyMetadata lazyPublishedModules 120cdb7c
    <- Microsoft.CodeAnalysis.AssemblyMetadata+Data Modules 120cde88


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
            var hashset = new HashSet<uint>(); // EnumerateObjectReferences sometimes has duplicate children <sigh>
            using (var fs = new StreamReader(fnameObjectGraph))
            {
                List<uint> lstChildren = null;
                var curObjId = 0U;
                while (!fs.EndOfStream)
                {
                    var line = await fs.ReadLineAsync();
                    var lineParts = line.Split(" ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                    if (lineParts[0] == "Seg")
                    {
                        continue;
                    }

                    var oidTemp = uint.Parse(lineParts[0].Trim(), System.Globalization.NumberStyles.AllowHexSpecifier);
                    if (!line.StartsWith(" "))
                    {
                        hashset = new HashSet<uint>(); // perf: much faster to create a new one than to use the old one (which might have expanded capacity and really slows down 15secs to 33 min
                        /*
                        Name                                                                                                                                                                                                	Inc %	     Inc	Exc %	   Exc
                         | |                  + system.core!PipeStream.AsyncPSCallback                                                                                                                                      	 49.3	   4,175	  0.0	     4
                         | |                   + mscorlib!System.Threading.Tasks.TaskFactory`1+<>c__DisplayClass44_0`3[System.Int32,System.__Canon,System.Int32,System.Int32].<FromAsyncImpl>b__0(class System.IAsyncResult)	 49.2	   4,167	  0.0	     0
                         | |                   |+ mscorlib.ni!?                                                                                                                                                             	 49.2	   4,167	  0.0	     0
                         | |                   | + mapfiledict!MapFileDict.ExtensionMethods+<ReadTimeout>d__2.MoveNext()                                                                                                    	 49.2	   4,167	  0.0	     0
                         | |                   |  + mscorlib.ni!?                                                                                                                                                           	 49.2	   4,167	  0.0	     0
                         | |                   |   + mapfiledict!MapFileDict.ExtensionMethods+<ReadAcknowledgeAsync>d__5.MoveNext()                                                                                         	 49.2	   4,167	  0.0	     0
                         | |                   |    + mscorlib.ni!?                                                                                                                                                         	 49.2	   4,167	  0.0	     0
                         | |                   |     + mapfiledict!MapFileDict.OutOfProc+<<AddVerbs>b__4_18>d.MoveNext()                                                                                                    	 49.2	   4,167	  0.0	     0
                         | |                   |      + mscorlib.ni!?                                                                                                                                                       	 49.2	   4,167	  0.0	     0
                         | |                   |       + mapfiledict!MapFileDict.OutOfProc+<>c__DisplayClass5_0+<<SendObjGraphEnumerableInChunksAsync>g__SendBufferAsync|0>d.MoveNext()                                     	 49.2	   4,167	  0.0	     0
                         | |                   |        + mscorlib.ni!?                                                                                                                                                     	 49.2	   4,167	  0.1	     5
                         | |                   |         + mapfiledict!MapFileDict.OutOfProc+<SendObjGraphEnumerableInChunksAsync>d__5.MoveNext()                                                                           	 49.1	   4,161	  0.2	    21
                         | |                   |         |+ mapfiledicttest!MapFileDictTest.OOPCommTests+<GetObjectGraphIEnumerable>d__5.MoveNext()                                                                         	 48.2	   4,078	  0.2	    13
                         | |                   |         ||+ system.core!System.Collections.Generic.HashSet`1[System.UInt32].Clear()                                                                                        	 46.4	   3,925	  0.1	     5
                         | |                   |         |||+ clr!ArrayNative::ArrayClear                                                                                                                                   	 46.3	   3,920	  0.0	     0
                         | |                   |         ||| + clr!ZeroMemoryInGCHeap                                                                                                                                       	 46.3	   3,920	 45.9	 3,889
                         */
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
                        if (!hashset.Contains(oidTemp))
                        {
                            hashset.Add(oidTemp);
                            nChildObjs++;
                            lstChildren.Add(oidTemp);
                        }
                    }
                }
            }
            Trace.WriteLine($"{nameof(ReadObjectRefGraphAsync)} Read {dictOGraph.Count:n0} objs. #Objs with at least 1 child = {nObjsWithAtLeastOneKid:n0}   TotalChildObjs = {nChildObjs:n0}");
            // 12 secs to read in graph Read 1,223,023 objs. #Objs with at least 1 child = 914,729   TotalChildObjs = 2,901,660
            return dictOGraph;
        }
        [TestMethod]
        public async Task OOPSendBigObjRefs()
        {
            try
            {
                await DoServerStuff(options: null, func: async (oop) =>
                 {
                     var sw = Stopwatch.StartNew();
                     var ienumOGraph = GetObjectGraphIEnumerable();
                     var modulo = 10;
                     var tup = await oop.SendObjRefGraphEnumerableInChunksAsync(ienumOGraph,
                         (nchunk) =>
                         {
                             if (nchunk % modulo == 0)
                             {
                                 Trace.WriteLine($" objref chunk {nchunk / modulo}");
                             }
                         });
                     int numObjs = tup.Item1;
                     var numChunksSent = tup.Item2;
                     // the timing includes parsing the text file for obj graph
                     Trace.WriteLine($"ObjRefGraph Sent {numObjs}  #Chunks = {numChunksSent} Objs/Sec = {numObjs / sw.Elapsed.TotalSeconds:n2}"); // 5k/sec

                     await oop.ClientSendVerbAsync(Verbs.CreateInvertedObjRefDictionary, null);
                     Trace.WriteLine($"Inverted Dictionary");


                     await DoQueryForParents(oop, SystemStackOverflowException, nameof(SystemStackOverflowException));
                     await DoQueryForParents(oop, MemoryMappedViewAccessor, nameof(MemoryMappedViewAccessor));

                     Trace.WriteLine($"Server Logs: " + await oop.ClientSendVerbAsync(Verbs.GetLog, null));
                 });
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.ToString());
                throw;
            }
            VerifyLogStrings(@"
IntPtr.Size = 8 Creating Shared Memory region
# dictObjRef = 1223023
034610b4 SystemStackOverflowException has 0 parents
120cd2dc MemoryMappedViewAccessor has 2 parents
");
        }


        [TestMethod]
        public async Task OOPSendObjRefsInProc()
        {
            var cts = new CancellationTokenSource();
            using (var oop = new OutOfProc(new OutOfProcOptions() { CreateServerOutOfProc = false }, cts.Token))
            {

                var taskServer = oop.DoServerLoopTask;
                Trace.WriteLine("Starting Client");
                {
                    try
                    {
                        await oop.ConnectToServerAsync(cts.Token);


                        var sw = Stopwatch.StartNew();
                        var ienumOGraph = GetObjectGraphIEnumerable();
                        var tup = await oop.SendObjRefGraphEnumerableInChunksAsync(ienumOGraph);
                        int numObjs = tup.Item1;
                        var numChunksSent = tup.Item2;
                        // the timing includes parsing the text file for obj graph
                        Trace.WriteLine($"ObjRefGraph Sent {numObjs}  #Chunks = {numChunksSent} Objs/Sec = {numObjs / sw.Elapsed.TotalSeconds:n2}"); // 5k/sec

                        await oop.ClientSendVerbAsync(Verbs.CreateInvertedObjRefDictionary, null);
                        Trace.WriteLine($"Inverted Dictionary");

                        await DoQueryForParents(oop, SystemStackOverflowException, nameof(SystemStackOverflowException));

                        await DoQueryForParents(oop, ShortSimpleTextStorage, nameof(ShortSimpleTextStorage));

                        await DoQueryForParents(oop, MemoryMappedViewAccessor, nameof(MemoryMappedViewAccessor));
                        Trace.WriteLine("Client: sending quit");
                        await oop.ClientSendVerbAsync(Verbs.ServerQuit, null);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine(ex.ToString());
                        throw;
                    }
                }

                var delaySecs = Debugger.IsAttached ? 3000 : 40;
                var tskDelay = Task.Delay(TimeSpan.FromSeconds(delaySecs));
                await Task.WhenAny(new[] { tskDelay, taskServer });
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
Inverted Dictionary
 034610b4 SystemStackOverflowException has 0 parents
   034610b4
 36197924 ShortSimpleTextStorage has 1 parents
   36297110
     3625bfb4
       362126f0
         361e1484
           361b8968
             3619ff20
               36197bf4
                 36197924
 120cd2dc MemoryMappedViewAccessor has 2 parents
   120cd2dc
");
        }
        private async Task<List<uint>> DoQueryForParents(OutOfProc oop, uint objIdLeaf, string desc)
        {
            int nMaxLevels = 20;
            int numImmediateParents = 0;
            var lstParentChain = new List<uint>();
            lstParentChain.Add(objIdLeaf);
            //            Trace.WriteLine($"Looking for Parents of {desc} {objIdLeaf:x8}");
            await WalkParentTreeAsync(objIdLeaf, 0);
            lstParentChain.Reverse();
            int nIndex = 0;
            foreach (var itm in lstParentChain)
            {
                var indent = new string(' ', 2 * nIndex++);
                Trace.WriteLine($"  {indent} {itm:x8}");
            }
            return lstParentChain;
            // we want to walk from leaf node up the parent chain while the parent count ==1.
            // then we want the ref chain list in reverse, from root to leaf
            async Task WalkParentTreeAsync(uint objId, int level)
            {
                if (level < nMaxLevels)
                {
                    var indent = new string(' ', 2 * level);
                    var lstParents = (List<uint>)await oop.ClientSendVerbAsync(Verbs.QueryParentOfObject, objId);
                    if (level == 0)
                    {
                        numImmediateParents = lstParents.Count;
                        Trace.WriteLine($"{indent} {objId:x8} {desc} has {lstParents.Count} parents");
                    }
                    if (lstParents.Count == 1)
                    {
                        lstParentChain.Add(lstParents[0]);
                        foreach (var parentObjId in lstParents.Take(10))
                        {
                            //                            Trace.WriteLine($"{indent}  {parentObjId:x8}");
                            await WalkParentTreeAsync(parentObjId, level + 1);
                        }
                    }
                }
            }
        }


        [TestMethod]
        public async Task OOPTestConsoleApp()
        {
            //var pidClient = Process.GetCurrentProcess().Id;
            var consapp = "ConsoleAppTest.exe";
            //var procServer = Process.Start(consapp, $"{pidClient}");
            //Trace.WriteLine($"Client: started server {procServer.Id}");
            var options = new OutOfProcOptions()
            {
                ExistingExeNameToUseForServer = consapp
            };
            await DoServerStuff(options, async (oop) =>
            {
                //                await oop.ClientSendVerb(Verbs.DoMessageBox, $"Message From Client");

                for (int i = 0; i < 5; i++)
                {
                    await oop.ClientSendVerbAsync(Verbs.Delayms, 300u);
                }
                //                    await Task.Delay(5000);
                Trace.WriteLine($"Server Logs: " + await oop.ClientSendVerbAsync(Verbs.GetLog, null));
            });

            VerifyLogStrings(@"
Server: Getlog
IntPtr.Size = 8 Trace Listener created
Server: Getlog #entries
");
        }

        private async Task DoServerStuff(OutOfProcOptions options, Func<OutOfProc, Task> func)
        {
            var sw = Stopwatch.StartNew();
            var cts = new CancellationTokenSource();
            var serverPid = 0;
            using (var oop = new OutOfProc(options, cts.Token))
            {
                serverPid = oop.ProcServer.Id;
                Trace.WriteLine($"Client: started server PidClient={oop.pidClient} PidServer={oop.ProcServer.Id}");
                Trace.WriteLine($"Client: starting to connect");
                await oop.ConnectToServerAsync(cts.Token);
                Trace.WriteLine($"Client: connected");
                await func(oop);
                await oop.ClientSendVerbAsync(Verbs.ServerQuit, null);
            }
            Trace.WriteLine($"Done in {sw.Elapsed.TotalSeconds:n2}");
            var serverDidExit = false;
            try
            {
                var procsleft = Process.GetProcessById(serverPid);

            }
            catch (Exception)
            {
                serverDidExit = true;
            }
            Assert.IsTrue(serverDidExit, "serverDidExit");
        }


        [TestMethod]
        public async Task OOPCreateDynamicServer()
        {
            var cts = new CancellationTokenSource();
            using (var oop = new OutOfProc(
                new OutOfProcOptions()
                {
                    CreateServerOutOfProc = true
                },
                cts.Token))
            {
                Task taskServerDone;
                if (!oop.Options.CreateServerOutOfProc)
                {
                    taskServerDone = oop.DoServerLoopTask;
                }
                else
                {
                    taskServerDone = Task.Delay(100);
                }

                Trace.WriteLine("Starting Client");
                {
                    try
                    {
                        await oop.ConnectToServerAsync(cts.Token);

                        //                        await oop.ClientSendVerb(Verbs.DoMessageBox, $"Message From Client");
                        await oop.ClientSendVerbAsync(Verbs.Delayms, (uint)2);

                        //Trace.WriteLine("Client: sending quit");
                        //await oop.ClientSendVerb(Verbs.ServerQuit, null);
                        //return;
                        var str = await oop.ClientSendVerbAsync(Verbs.verbRequestData, null);
                        Trace.WriteLine($"Req data {str}");


                        await oop.ClientSendVerbAsync(Verbs.Delayms, (uint)1);
                        // speedtest
                        var sw = Stopwatch.StartNew();

                        var nIter = 5U;
                        {
                            uint bufSize = 1024 * 1024 * 1024;
                            var bufSpeed = new byte[bufSize];
                            for (int iter = 0; iter < nIter; iter++)
                            {
                                Trace.WriteLine($"Speed Sending ByteBuff {bufSize:n0} Iter={iter}");
                                await oop.ClientSendVerbAsync(Verbs.DoSpeedTestWithByteBuff, bufSpeed);
                            }
                            var bps = (double)bufSize * nIter / sw.Elapsed.TotalSeconds;
                            Trace.WriteLine($"Sending ByteBuff BytesPerSec = {bps:n0}"); // 1.4 G/Sec
                        }
                        {
                            uint bufSize = 1 * 100 * 1024;
                            var bufSpeed = new uint[bufSize];
                            for (int iter = 0; iter < nIter; iter++)
                            {
                                Trace.WriteLine($"Speed Sending UInts {bufSize:n0} Iter={iter}");
                                await oop.ClientSendVerbAsync(Verbs.DoSpeedTestWithUInts, bufSpeed);
                            }
                            var bps = 4 * (double)bufSize * nIter / sw.Elapsed.TotalSeconds;
                            Trace.WriteLine($"Sending UInts BytesPerSec = {bps:n0}"); // 1.4 G/Sec
                        }
                        var strbig = (string)await oop.ClientSendVerbAsync(Verbs.GetStringSharedMem, 0);
                        Trace.Write($"Got big string Len = {strbig.Length} " + strbig);

                        Trace.WriteLine($"Server Logs: " + await oop.ClientSendVerbAsync(Verbs.GetLog, null));
                        Trace.WriteLine("Client: sending quit");
                        await oop.ClientSendVerbAsync(Verbs.ServerQuit, null);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine(ex.ToString());
                        throw;
                    }
                }

                var nDelaySecs = Debugger.IsAttached ? 3000 : 20;
                var tskDelay = Task.Delay(TimeSpan.FromSeconds(nDelaySecs));
                await Task.WhenAny(new[] { tskDelay, taskServerDone });
                if (tskDelay.IsCompleted)
                {
                    Trace.WriteLine($"Delay {nDelaySecs} secs completed: cancelling server");
                    cts.Cancel();
                }
                Trace.WriteLine($"Done");
                if (!oop.Options.CreateServerOutOfProc)
                {
                    await oop.DoServerLoopTask;
                    Assert.IsTrue(oop.DoServerLoopTask.IsCompleted);
                }
            }
        }

        [TestMethod]
        public async Task OOPMultiConnect()
        {
            var cts = new CancellationTokenSource();
            var numIter = 10;
            for (int i = 0; i < numIter; i++)
            {
                var oop = new OutOfProc(
                    new OutOfProcOptions()
                    {
                        CreateServerOutOfProc = true,
                        NamedPipeAddString = $"_{i}"
                    },
                    cts.Token);
                {
                    Task taskServerDone;
                    if (!oop.Options.CreateServerOutOfProc)
                    {
                        taskServerDone = oop.DoServerLoopTask;
                    }
                    else
                    {
                        taskServerDone = Task.Delay(100);
                    }

                    Trace.WriteLine("Starting Client");
                    {
                        try
                        {
                            await oop.ConnectToServerAsync(cts.Token);

                            var strbig = (string)await oop.ClientSendVerbAsync(Verbs.GetStringSharedMem, 0);
                            Trace.Write($"Got big string Len = {strbig.Length} " + strbig);

                            Trace.WriteLine($"Server Logs: " + await oop.ClientSendVerbAsync(Verbs.GetLog, null));
                            Trace.WriteLine("Client: sending quit");
                            await oop.ClientSendVerbAsync(Verbs.ServerQuit, null);
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine(ex.ToString());
                            throw;
                        }
                    }

                    var nDelaySecs = Debugger.IsAttached ? 3000 : 20;
                    var tskDelay = Task.Delay(TimeSpan.FromSeconds(nDelaySecs));
                    await Task.WhenAny(new[] { tskDelay, taskServerDone });
                    if (tskDelay.IsCompleted)
                    {
                        Trace.WriteLine($"Delay {nDelaySecs} secs completed: cancelling server");
                        cts.Cancel();
                    }
                    Trace.WriteLine($"Done");
                    if (!oop.Options.CreateServerOutOfProc)
                    {
                        await oop.DoServerLoopTask;
                        Assert.IsTrue(oop.DoServerLoopTask.IsCompleted);
                    }
                }
                oop.Dispose();
                Assert.IsTrue(oop.ProcServer.HasExited);

            }
        }


        [TestMethod]
        public async Task OOPCreateServerConstantExe()
        {
            var cts = new CancellationTokenSource();
            var LocalAppDir = Path.Combine(Environment.ExpandEnvironmentVariables("%localappdata%"), "VSDbg");
            Directory.CreateDirectory(LocalAppDir);
            var options = new OutOfProcOptions()
            {
                CreateServerOutOfProc = true,
                // avoid writing to temp dir because System.ComponentModel.Win32Exception (0x80004005): Operation did not complete successfully because the file contains a virus or potentially unwanted software
                // put the connectionversion in the exe name so as we upgrade, no collisions
                exeNameToCreate = Path.Combine(LocalAppDir, $"OutOfProc{OutOfProc.ConnectionVersion}.exe"),
                UseExistingExeIfExists = true,
            };
            using (var oop = new OutOfProc(options, cts.Token))
            {
                Task taskServerDone;
                if (!oop.Options.CreateServerOutOfProc)
                {
                    taskServerDone = oop.DoServerLoopTask;
                }
                else
                {
                    taskServerDone = Task.Delay(100);
                }

                Trace.WriteLine("Starting Client");
                {
                    try
                    {
                        await oop.ConnectToServerAsync(cts.Token);

                        //                        await oop.ClientSendVerb(Verbs.DoMessageBox, $"Message From Client");
                        await oop.ClientSendVerbAsync(Verbs.Delayms, (uint)2);

                        //Trace.WriteLine("Client: sending quit");
                        //await oop.ClientSendVerb(Verbs.ServerQuit, null);
                        //return;
                        var str = await oop.ClientSendVerbAsync(Verbs.verbRequestData, null);

                        Trace.WriteLine($"Server Logs: " + await oop.ClientSendVerbAsync(Verbs.GetLog, null));
                        Trace.WriteLine("Client: sending quit");
                        await oop.ClientSendVerbAsync(Verbs.ServerQuit, null);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine(ex.ToString());
                        throw;
                    }
                }

                var nDelaySecs = Debugger.IsAttached ? 3000 : 20;
                var tskDelay = Task.Delay(TimeSpan.FromSeconds(nDelaySecs));
                await Task.WhenAny(new[] { tskDelay, taskServerDone });
                if (tskDelay.IsCompleted)
                {
                    Trace.WriteLine($"Delay {nDelaySecs} secs completed: cancelling server");
                    cts.Cancel();
                }
                Trace.WriteLine($"Done");
                if (!oop.Options.CreateServerOutOfProc)
                {
                    await oop.DoServerLoopTask;
                    Assert.IsTrue(oop.DoServerLoopTask.IsCompleted);
                }
            }
        }

        [TestMethod]
        public async Task OOPTestSendObjsAndTypes()
        {
            var cts = new CancellationTokenSource();
            var opts = new OutOfProcOptions()
            {
                CreateServerOutOfProc = true,
                SizeOfSharedMemory = 65536u * 1
            };
            using (var oop = new OutOfProc(opts, cts.Token))
            {
                Task taskServerDone;
                if (!oop.Options.CreateServerOutOfProc)
                {
                    taskServerDone = oop.DoServerLoopTask;
                }
                else
                {
                    taskServerDone = Task.Delay(100);
                }

                {
                    try
                    {
                        await oop.ConnectToServerAsync(cts.Token);
                        var numObjsToSend = 1000 * 1000 * 10;
                        //                        numObjsToSend = 10;
                        var sw = Stopwatch.StartNew();
                        var clrUtil = new ClrUtil(numObjsToSend, oop);
                        await SendObjectsAndTypesAsync(clrUtil, oop);
                        Trace.WriteLine($"ObjsAndTypes Sent {numObjsToSend:n0}  Objs/Sec = {numObjsToSend / sw.Elapsed.TotalSeconds:n2}"); // 5k/sec

                        var typeCountFromServer = (uint)await oop.ClientSendVerbAsync(Verbs.GetTypeCount, 0);
                        Trace.WriteLine($"TypeCountFromServer = {typeCountFromServer}");

                        var TypeSummary = (List<Tuple<string, uint, uint>>)await oop.ClientSendVerbAsync(Verbs.GetTypeSummary, 0);
                        foreach (var tup in TypeSummary.Take(10))
                        {
                            Trace.WriteLine($"TSUMMARY  {tup.Item1}  {tup.Item2}  {tup.Item3}"); // type, count,size
                        }





                        async Task ShowObjsAsync(string typeName, uint maxNumObjs = 0)
                        {
                            var lstObjs = await clrUtil.GetObjectsOfType(typeName, maxNumObjs);
                            Trace.WriteLine($"#Objs of {typeName} {lstObjs.Count}");

                            foreach (var obj in lstObjs.Take(5))
                            {
                                Trace.WriteLine($"  {typeName} {obj.Item1:x8} size={obj.Item2}");
                            }
                        }
                        await ShowObjsAsync("ClrType1");
                        await ShowObjsAsync("ClrType2", maxNumObjs: 5);
                        await ShowObjsAsync("NonExistentType");
                        Trace.WriteLine("Now check enumeration");
                        foreach (var type in clrUtil.EnumerateObjectTypes("ClrType21.*").Skip(3))
                        {
                            Trace.WriteLine($"enumtype {type}");
                        }
                        sw.Restart();
                        var totcnt = clrUtil.EnumerateObjectTypes("").Count();
                        Trace.WriteLine($"EnumerateObjectTypes #Types={totcnt:n0}  Types/Sec = {totcnt / sw.Elapsed.TotalSeconds:n2}"); // 5k/sec
                        Trace.WriteLine($"# of all types = {totcnt}");
                        var cnt = clrUtil.EnumerateObjectTypes("nonefound.*").Count();
                        Trace.WriteLine($"# of 'nonefound' types = {cnt}");
                        foreach (var type in clrUtil.EnumerateObjectTypes("nonefound.*").Skip(3))
                        {
                            Trace.WriteLine($"enumtype {type}");
                        }

                        foreach (var type in clrUtil.EnumerateObjectTypes(@"Microsoft\.VisualStudio\.Text\.BufferUndoManager\.Implementation.*"))
                        {
                            Trace.WriteLine($"enumtype tbuffer {type}");
                        }


                        var lstTypesAndCounts = (List<Tuple<string, uint>>)await oop.ClientSendVerbAsync(Verbs.GetTypesAndCounts, 0);
                        foreach (var itm in lstTypesAndCounts.Take(10))
                        {
                            Trace.WriteLine($" Types&Counts {itm.Item2}  {itm.Item1}");
                        }

                        // let's time getting all types and all objs
                        sw.Restart();
                        var objsRetrieved = 0;
                        var typesAndCounts = (List<Tuple<string, uint>>)await oop.ClientSendVerbAsync(Verbs.GetTypesAndCounts, 0);
                        foreach (var itm in typesAndCounts)
                        {
                            var lst = (List<Tuple<uint, uint>>)await oop.ClientSendVerbAsync(Verbs.GetObjsOfType, Tuple.Create(itm.Item1, 0u));
                            objsRetrieved += lst.Count;
                        }
                        Trace.WriteLine($"Retrieve all objs from all types {objsRetrieved} Objs/Sec = {objsRetrieved / sw.Elapsed.TotalSeconds:n2}"); // 5k/sec");

                        Trace.WriteLine($"Server Logs: " + await oop.ClientSendVerbAsync(Verbs.GetLog, null));

                        Trace.WriteLine("Client: sending quit");
                        await oop.ClientSendVerbAsync(Verbs.ServerQuit, null);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine(ex.ToString());
                        //   throw;
                    }
                }

                var nDelaySecs = Debugger.IsAttached ? 3000 : 20;
                var tskDelay = Task.Delay(TimeSpan.FromSeconds(nDelaySecs));
                await Task.WhenAny(new[] { tskDelay, taskServerDone });
                if (tskDelay.IsCompleted)
                {
                    Trace.WriteLine($"Delay {nDelaySecs} secs completed: cancelling server");
                    cts.Cancel();
                }
                Trace.WriteLine($"Done");
                if (!oop.Options.CreateServerOutOfProc)
                {
                    await oop.DoServerLoopTask;
                    Assert.IsTrue(oop.DoServerLoopTask.IsCompleted);
                }
            }
            VerifyLogStrings(@"
#Objs of ClrType1 6667
ClrType1 00000001 size=1
ClrType1 000006af size=111
#Objs of ClrType2 5
ClrType2 00000002 size=2
ClrType2 000006b0 size=112
#Objs of NonExistentType 0
enumtype ClrType214
# of all types = 2710
# of 'nonefound' types = 0
TSUMMARY  ClrType199  6667  1026763
");
            Assert.IsTrue(opts.CreateServerOutOfProc, "Must be out of proc when not debugging");
        }

        [TestMethod]
        public async Task OOPTestSendObjsAndTypesWithException()
        {
            var cts = new CancellationTokenSource();
            var opts = new OutOfProcOptions()
            {
                CreateServerOutOfProc = true,
                SizeOfSharedMemory = 65536u * 1,
                TypeIdAtWhichToThrowException = 1234
            };
            using (var oop = new OutOfProc(opts, cts.Token))
            {
                Task taskServerDone;
                if (!oop.Options.CreateServerOutOfProc)
                {
                    taskServerDone = oop.DoServerLoopTask;
                }
                else
                {
                    taskServerDone = Task.Delay(100);
                }

                {
                    try
                    {
                        await oop.ConnectToServerAsync(cts.Token);
                        await oop.ClientSendVerbAsync(Verbs.SetExceptionValueForTest, opts.TypeIdAtWhichToThrowException);
                        var numObjsToSend = 1000 * 1000 * 10;
                        //                        numObjsToSend = 10;
                        var clrUtil = new ClrUtil(numObjsToSend, oop);
                        try
                        {
                            await SendObjectsAndTypesAsync(clrUtil, oop);
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"Got server exception {ex.ToString()}");
                        }

                        Trace.WriteLine($"Server Logs: " + await oop.ClientSendVerbAsync(Verbs.GetLog, null));

                        Trace.WriteLine("Client: sending quit");
                        await oop.ClientSendVerbAsync(Verbs.ServerQuit, null);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine(ex.ToString());
                        //   throw;
                    }
                }

                var nDelaySecs = Debugger.IsAttached ? 3000 : 20;
                var tskDelay = Task.Delay(TimeSpan.FromSeconds(nDelaySecs));
                await Task.WhenAny(new[] { tskDelay, taskServerDone });
                if (tskDelay.IsCompleted)
                {
                    Trace.WriteLine($"Delay {nDelaySecs} secs completed: cancelling server");
                    cts.Cancel();
                }
                Trace.WriteLine($"Done");
                if (!oop.Options.CreateServerOutOfProc)
                {
                    await oop.DoServerLoopTask;
                    Assert.IsTrue(oop.DoServerLoopTask.IsCompleted);
                }
            }
            VerifyLogStrings(@"
System.InvalidOperationException: Intentional exception for testing
");
            Assert.IsTrue(opts.CreateServerOutOfProc, "Must be out of proc when not debugging");
        }


        internal async Task SendObjectsAndTypesAsync(ClrUtil clrUtil, OutOfProc outOfProc)
        {
            uint typeIdNext = 0;
            var bufChunkSize = outOfProc._sharedMapSize - 8; // room for null term
            var numChunksSent = 0;
            var numObjs = 0;
            int ndxbufChunk = 0; // count of UINTs
            Dictionary<ClrType, uint> dictClrTypeToTypeId = new Dictionary<ClrType, uint>();  // client side: ClrType to TypeId
            async Task AddObjAsync(uint obj, ClrType type, uint objSize)
            {
                if (!dictClrTypeToTypeId.TryGetValue(type, out var typeId))
                {
                    typeIdNext++;
                    typeId = typeIdNext;
                    dictClrTypeToTypeId[type] = typeId;
                }
                if (4 * ndxbufChunk >= bufChunkSize)
                {
                    await SendBufferAsync(Verbs.SendObjAndTypeIdInChunks);
                    ndxbufChunk = 0;
                }
                // now we send the pair objaddr, typeId as series of 2 UINTs = 8 bytes. We compare byte count: 4 * # Uints
                unsafe
                {
                    var ptr = (uint*)outOfProc._MemoryMappedRegionAddress;
                    ptr[ndxbufChunk++] = obj;
                    ptr[ndxbufChunk++] = typeId;
                    ptr[ndxbufChunk++] = objSize;
                }
                numObjs++;
            }
            foreach (var objAddr in clrUtil._heap.EnumerateObjectAddresses())
            {
                ClrType type = null;
                try
                {
                    type = clrUtil._heap.GetObjectType(objAddr);
                }
                catch (Exception ex)
                {
                    //                    clrUtil.LogString("Enumobjs Got exception {0} {1}", objAddr, ex.ToString());
                    clrUtil.LogString(ex.ToString());
                }
                if (type != null) // corrupt heap can cause null
                {
                    var objSize = (uint)objAddr % 200;
                    await AddObjAsync((uint)objAddr, type, objSize);
                }
            }
            foreach (var root in clrUtil._heap.EnumerateRoots(enumerateStatics: true)) // could yield dupes
            {
                if (root.Type != null)
                {
                    var objSize = (uint)root.Object % 100;
                    await AddObjAsync((uint)root.Object, root.Type, objSize);
                }
            }
            // now we send leftover chunk
            if (ndxbufChunk > 0)
            {
                await SendBufferAsync(Verbs.SendObjAndTypeIdInChunks);
            }
            clrUtil.LogString($"Client sent # objs= {numObjs:n0}  ChunkSize={bufChunkSize:n0} # chunks = {numChunksSent}");
            // now we send type names and Ids as series of UINT ID, UINT nameLen, byte[namelen]
            var numTypes = 0;
            numChunksSent = 0;
            ndxbufChunk = 0;
            foreach (var itm in dictClrTypeToTypeId)
            {
                var strTypeNameBytes = Encoding.ASCII.GetBytes(itm.Key.Name);
                var numBytesRequired = 4 + 4 + strTypeNameBytes.Length; // id, len, strbytes
                if (4 * ndxbufChunk + numBytesRequired >= bufChunkSize)
                {
                    await SendBufferAsync(Verbs.SendTypeIdAndTypeNameInChunks);
                    ndxbufChunk = 0;
                    if (numBytesRequired > bufChunkSize)
                    {
                        throw new InvalidOperationException($"Type name too big");
                    }
                }
                unsafe
                {
                    var ptr = (uint*)outOfProc._MemoryMappedRegionAddress;
                    ptr[ndxbufChunk++] = itm.Value; // the typeId
                    ptr[ndxbufChunk++] = (uint)strTypeNameBytes.Length;
                    Marshal.Copy(strTypeNameBytes, 0, outOfProc._MemoryMappedRegionAddress + ndxbufChunk * 4, strTypeNameBytes.Length);
                    ndxbufChunk += ((strTypeNameBytes.Length + 3) / 4);//round up to nearest 4 so we can continue to index by UINT
                    numTypes++;
                }
            }
            // send leftovers
            if (ndxbufChunk > 0)
            {
                await SendBufferAsync(Verbs.SendTypeIdAndTypeNameInChunks);
            }
            // now that we've sent all the data, let the server know and calculate the various data structures required
            await outOfProc.ClientSendVerbAsync(Verbs.ObjsAndTypesDoneSending, 0);

            async Task SendBufferAsync(Verbs verb)
            {
                unsafe
                {
                    var ptr = (uint*)outOfProc._MemoryMappedRegionAddress;
                    ptr[ndxbufChunk++] = 0; //null term
                }
                await outOfProc.ClientSendVerbAsync(verb, 0);
                numChunksSent++;
                if (numChunksSent % 100 == 0)
                {
                    //                    clrUtil.LogString($"Client sent {verb} chunk {numChunksSent}");
                }
            }
        }
        public class ClrUtil
        {
            public ClrHeap _heap;
            internal readonly int numObjsToSend;
            internal OutOfProc _outOfProc;
            private readonly string fnameObjGraph;

            //use this ctor to gen fake clr data
            public ClrUtil(int numObjsToSend, OutOfProc outOfProc)
            {
                this.numObjsToSend = numObjsToSend;
                _heap = new ClrHeap(this);
                this._outOfProc = outOfProc;
            }
            // use this ctor to gen objs from a real heap
            public ClrUtil(string fnameObjGraph, OutOfProc outOfProc)
            {
                this._outOfProc = outOfProc;
                this.fnameObjGraph = fnameObjGraph;
            }

            public void LogString(string s)
            {
                Trace.WriteLine(s);
            }
            public class ClrHeap
            {
                private int numObjsToSend;
                List<ClrSegment> lstSegments = new List<ClrSegment>();
                public ClrHeap(ClrUtil clrUtil)
                {
                    this.numObjsToSend = clrUtil.numObjsToSend;
                    if (numObjsToSend > 0)
                    {
                        for (int i = 0; i < numClrTypes; i++)
                        {
                            var tname = $"ClrType{i % 1710}";
                            if (i % 1500 == 0)
                            {
                                tname = @"Microsoft.VisualStudio.Text.BufferUndoManager.Implementation.TextBufferUndoManager";
                            }
                            var ty = new ClrType() { Name = tname };
                            _types.Add(ty);
                        }
                        // let's simulate segments
                        var seglen = 100;
                        var numsegs = numObjsToSend / seglen + 1;
                        for (int i = 0; i < numsegs; i++)
                        {
                            lstSegments.Add(new ClrSegment() { Start = (ulong)(i * seglen), End = (ulong)(i * seglen + seglen - 1) });
                        }
                    }
                    else
                    {// get real clr data

                    }
                }

                public class root
                {
                    public uint Object;
                    public ClrType Type;
                }
                internal IEnumerable<uint> EnumerateObjectAddresses()
                {
                    for (uint i = 1; i < numObjsToSend; i++)
                    {
                        yield return i;
                    }
                }
                public class ClrSegment
                {
                    public ulong Start;
                    public ulong End;
                }
                internal IList<ClrSegment> Segments
                {
                    get
                    {
                        return lstSegments;
                    }
                }

                List<ClrType> _types = new List<ClrType>();
                int numClrTypes = 3000;
                internal ClrType GetObjectType(uint objAddr)
                {
                    return _types[(int)(objAddr % numClrTypes)];
                }
                internal IEnumerable<root> EnumerateRoots(bool enumerateStatics)
                {
                    for (uint i = 0; i < 1000; i++)
                    {
                        yield return new root() { Object = i + 2000000u, Type = new ClrType() { Name = $"root{i}" } };
                    }
                }
            }

            public IEnumerable<string> EnumerateObjectTypes(string regexFilter = null)
            {
                var x = new MyEnumerable<string>(regexFilter, _outOfProc);
                return x;
            }

            internal async Task<List<Tuple<uint, uint>>> GetObjectsOfType(string typeName, uint maxNumObjs = 0)
            {
                var lstRaw = (List<Tuple<uint, uint>>)await _outOfProc.ClientSendVerbAsync(Verbs.GetObjsOfType, Tuple.Create(typeName, maxNumObjs));
                return lstRaw;
            }
        }
        public class ClrType
        {
            public string Name;
            public override string ToString()
            {
                return Name;
            }
        }
    }
    struct MyEnumerable<T> : IEnumerable<T>
    {
        internal string regexFilter;
        internal OutOfProc _outOfProc;

        public MyEnumerable(string regexFilter, OutOfProc outOfProc)
        {
            this.regexFilter = regexFilter;
            this._outOfProc = outOfProc;
        }

        public IEnumerator<T> GetEnumerator()
        {
            var en = new MyEnumerator<T>(this);
            return en;
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            var en = new MyEnumerator<T>(this);
            return en;
        }
    }
    internal class MyEnumerator<T> : IEnumerator<T>
    {
        private T _curValue = default(T);
        private MyEnumerable<T> _myEnumerables;
        int curIndx = -1;
        public MyEnumerator(MyEnumerable<T> myEnumerables)
        {
            this._myEnumerables = myEnumerables;
        }

        public T Current => _curValue;

        object IEnumerator.Current => _curValue;

        public void Dispose()
        {
        }

        public bool MoveNext()
        {
            var verb = curIndx == -1 ? Verbs.GetFirstType : Verbs.GetNextType;
            var firstValueTask = _myEnumerables._outOfProc.ClientSendVerbAsync(verb, _myEnumerables.regexFilter);
            firstValueTask.Wait();
            _curValue = (T)(firstValueTask.Result);
            curIndx++;
            return !string.IsNullOrEmpty(_curValue as string);
        }

        public void Reset()
        {
            curIndx = -1;
        }
    }
}
