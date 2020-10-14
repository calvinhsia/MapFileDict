using MapFileDict;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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

        private async Task<Dictionary<uint, List<uint>>> ReadObjectGraphAsync(string fnameObjectGraph)
        {
            /*
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
                var dictOGraph = await ReadObjectGraphAsync(fnameObjectGraph);
                var pidClient = Process.GetCurrentProcess().Id;
                var procServer = OutOfProc.CreateServer(pidClient);
                await DoServerStuff(procServer, pidClient, async (pipeClient, oop) =>
                {
                    int numObjs = dictOGraph.Count;
                    Trace.WriteLine($"Client: sending {numObjs:n0} objs");
                    var sw = Stopwatch.StartNew();
                    pipeClient.WriteByte((byte)Verbs.verbSendObjAndReferences);
                    foreach (var kvp in dictOGraph)
                    {
                        pipeClient.WriteUInt32(kvp.Key);
                        var cntChildren = kvp.Value == null ? 0 : kvp.Value.Count;
                        pipeClient.WriteUInt32((uint)cntChildren);
                        if (kvp.Value != null)
                        {
                            foreach (var child in kvp.Value)
                            {
                                pipeClient.WriteUInt32(child);
                            }
                        }
                    }
                    //for (uint iObj = 0; iObj < numObjs; iObj++)
                    //{
                    //    var x = new ObjAndRefs()
                    //    {
                    //        obj = 1 + iObj
                    //    };
                    //    x.lstRefs = new List<uint>();
                    //    x.lstRefs.Add(2 + iObj * 10);

                    //    x.lstRefs.Add(3 + iObj * 10);
                    //    x.SerializeToPipe(pipeClient);
                    //}
                    pipeClient.WriteUInt32(0); // signal end
                    await pipeClient.GetAckAsync();
                    Trace.WriteLine($"Sent {numObjs} Objs/Sec = {numObjs / sw.Elapsed.TotalSeconds:n2}"); // 5k/sec


                    Trace.WriteLine($"Got log from server\r\n" + await oop.GetLogFromServer(pipeClient));
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.ToString());
                throw;
            }
        }



        [TestMethod]
        public async Task OOPSendObjRefs()
        {
            try
            {
                await Task.Yield();
                var pidClient = Process.GetCurrentProcess().Id;
                //*
                var procServer = OutOfProc.CreateServer(pidClient);
                /*/
                var procServer = Process.Start("ConsoleAppTest.exe", $"{pidClient}");
                 //*/
                await DoServerStuff(procServer, pidClient, async (pipeClient, oop) =>
                {
                    int numObjs = 10000;
                    Trace.WriteLine($"Client: sending {numObjs:n0} objs");
                    var sw = Stopwatch.StartNew();
                    pipeClient.WriteByte((byte)Verbs.verbSendObjAndReferences);
                    for (uint iObj = 0; iObj < numObjs; iObj++)
                    {
                        var x = new ObjAndRefs()
                        {
                            obj = 1 + iObj
                        };
                        x.lstRefs = new List<uint>();
                        x.lstRefs.Add(2 + iObj * 10);

                        x.lstRefs.Add(3 + iObj * 10);
                        x.SerializeToPipe(pipeClient);
                    }
                    pipeClient.WriteUInt32(0);
                    await pipeClient.GetAckAsync();

                    Trace.WriteLine($"Sent {numObjs}  Objs/Sec = {numObjs / sw.Elapsed.TotalSeconds:n2}"); // 5k/sec
                    Trace.WriteLine($"Got log from server\r\n" + await oop.GetLogFromServer(pipeClient));
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.ToString());
                throw;
            }
        }

        private async Task<int> SendObjGraphInChunksAsync(NamedPipeClientStream pipeClient, Dictionary<uint, List<uint>> dictOGraph)
        {
            var bufChunkSize = 300000;
            var bufChunk = new byte[bufChunkSize + 4]; // leave extra room for null term
            int ndxbufChunk = 0;
            var numChunksSent = 0;
            var numObjs = dictOGraph.Count;
            foreach (var kvp in dictOGraph)
            {
                var numChildren = kvp.Value?.Count ?? 0;
                var numBytesForThisObj = (1 + 1 + numChildren) * IntPtr.Size; // obj + childCount + children
                if (numBytesForThisObj >= bufChunkSize)
                {
                    await SendBufferAsync(); // empty it
                    ndxbufChunk = 0;
                    bufChunkSize = numBytesForThisObj;
                    bufChunk = new byte[numBytesForThisObj + 4];

                    Trace.WriteLine($"The cur obj {numBytesForThisObj} is too big for chunk {bufChunkSize}");
                }
                if (ndxbufChunk + numBytesForThisObj >= bufChunk.Length) // too big for cur buf?
                {
                    await SendBufferAsync(); // empty it
                    ndxbufChunk = 0;
                }

                var b1 = BitConverter.GetBytes(kvp.Key);
                Array.Copy(b1, 0, bufChunk, ndxbufChunk, b1.Length);
                ndxbufChunk += b1.Length;

                b1 = BitConverter.GetBytes(numChildren);
                Array.Copy(b1, 0, bufChunk, ndxbufChunk, b1.Length);
                ndxbufChunk += b1.Length;
                for (int iChild = 0; iChild < numChildren; iChild++)
                {
                    b1 = BitConverter.GetBytes(kvp.Value[iChild]);
                    Array.Copy(b1, 0, bufChunk, ndxbufChunk, b1.Length);
                    ndxbufChunk += b1.Length;
                }
            }
            //for (uint iObj = 0; iObj < numObjs; iObj++)
            //{
            //    int numChildren = 2;
            //    var numBytesForThisObj = (1 + 1 + numChildren) * IntPtr.Size; // obj + childCount + children
            //    if (numBytesForThisObj >= bufChunkSize)
            //    {
            //        throw new Exception("The cur obj is too big for chunk");
            //    }
            //    if (ndxbufChunk + numBytesForThisObj >= bufChunk.Length) // too big for cur buf?
            //    {
            //        await SendBufferAsync(); // empty it
            //        ndxbufChunk = 0;
            //    }
            //    var b1 = BitConverter.GetBytes(1 + iObj);
            //    Array.Copy(b1, 0, bufChunk, ndxbufChunk, b1.Length);
            //    ndxbufChunk += b1.Length;

            //    b1 = BitConverter.GetBytes(2); // # of children
            //    Array.Copy(b1, 0, bufChunk, ndxbufChunk, b1.Length);
            //    ndxbufChunk += b1.Length;

            //    b1 = BitConverter.GetBytes(2 + iObj * 10);
            //    Array.Copy(b1, 0, bufChunk, ndxbufChunk, b1.Length);
            //    ndxbufChunk += b1.Length;

            //    b1 = BitConverter.GetBytes(3 + iObj * 10);
            //    Array.Copy(b1, 0, bufChunk, ndxbufChunk, b1.Length);
            //    ndxbufChunk += b1.Length;
            //}
            if (ndxbufChunk > 0) // leftovers
            {
                Trace.WriteLine($"Client: send leftovers {ndxbufChunk}");
                await SendBufferAsync();
            }
            return numChunksSent;
            async Task SendBufferAsync()
            {
                bufChunk[ndxbufChunk++] = 0; // null terminating int32
                bufChunk[ndxbufChunk++] = 0;
                bufChunk[ndxbufChunk++] = 0;
                bufChunk[ndxbufChunk++] = 0;
                pipeClient.WriteByte((byte)Verbs.verbSendObjAndReferencesChunks);
                pipeClient.WriteUInt32((uint)ndxbufChunk); // size of buf
                pipeClient.Write(bufChunk, 0, ndxbufChunk);
                await pipeClient.GetAckAsync();
                numChunksSent++;
            }
        }

        [TestMethod]
        public async Task OOPSendObjRefsInProc()
        {
            var cts = new CancellationTokenSource();
            var dictOGraph = await ReadObjectGraphAsync(fnameObjectGraph);
            using (var oop = new OutOfProc(Process.GetCurrentProcess().Id, cts.Token, speedBufSize: 1024 * 1024 * 1024))
            {
                Trace.WriteLine($"Mapped Section {oop.mappedSection} 0x{oop.mappedSection.ToInt32():x8}");

                var taskServer = oop.DoServerLoopAsync();

                var taskClient = DoTestClientAsync(oop, async (pipeClient) =>
                {
                    int numObjs = dictOGraph.Count;
                    Trace.WriteLine($"Client: sending {numObjs:n0} objs");
                    var sw = Stopwatch.StartNew();
                    var numChunksSent = await SendObjGraphInChunksAsync(pipeClient, dictOGraph);
                    Trace.WriteLine($"Sent {numObjs}  #Chunks = {numChunksSent} Objs/Sec = {numObjs / sw.Elapsed.TotalSeconds:n2}"); // 5k/sec

                    pipeClient.WriteByte((byte)Verbs.verbCreateInvertedDictionary);
                    await pipeClient.GetAckAsync();
                    Trace.WriteLine($"Inverted Dictionary");

                    Trace.WriteLine($"Query Parent");
                    pipeClient.WriteByte((byte)Verbs.verbQueryParentOfObject);
                    pipeClient.WriteUInt32(WpfTextView);
                    while (true)
                    {
                        var parent = pipeClient.ReadUInt32();
                        if (parent == 0)
                        {
                            break;
                        }
                        Trace.WriteLine($"A Parent of {WpfTextView:x8} is {parent:x8}");
                    }



                    //                    Trace.WriteLine($"Got log from server\r\n" + await oop.GetLogFromServer(pipeClient));
                });
                var delaySecs = Debugger.IsAttached ? 3000 : 60;
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
# dict entries = 1223023
");
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
                verb[0] = (byte)Verbs.verbGetLog;
                await pipeClient.WriteAsync(verb, 0, 1);
                await pipeClient.GetAckAsync();
                var logstrs = Marshal.PtrToStringAnsi(oop.mappedSection);
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
                    await pipeClient.SendVerb((byte)Verbs.verbQuit);
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
            using (var oop = new OutOfProc(Process.GetCurrentProcess().Id, cts.Token, speedBufSize: 1024 * 1024 * 1024))
            {
                Trace.WriteLine($"Mapped Section {oop.mappedSection} 0x{oop.mappedSection.ToInt32():x8}");

                var taskServer = oop.DoServerLoopAsync();

                var taskClient = DoTestClientAsync(oop, async (pipeClient) =>
                {
                    await DoBasicCommTests(oop, pipeClient, cts.Token);
                });
                var tskDelay = Task.Delay(TimeSpan.FromSeconds(Debugger.IsAttached ? 3000 : 30));
                await Task.WhenAny(new[] { tskDelay, taskClient });
                if (tskDelay.IsCompleted)
                {
                    Trace.WriteLine($"Delay completed: cancelling server");
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
                    await actionAsync(pipeClient);
                    Trace.WriteLine("Client: sending quit");
                    await pipeClient.SendVerb((byte)Verbs.verbQuit);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex.ToString());
                    throw;
                }
            }
        }
        async Task DoBasicCommTests(OutOfProc oop, NamedPipeClientStream pipeClient, CancellationToken token)
        {
            var verb = new byte[2] { 1, 1 };
            for (int i = 0; i < 5; i++)
            {
                {
                    var strBuf = Encoding.ASCII.GetBytes($"MessageString {i}");
                    var buf = new byte[strBuf.Length + 1];
                    buf[0] = (byte)Verbs.verbString;
                    Array.Copy(strBuf, 0, buf, 1, strBuf.Length);
                    Trace.WriteLine("Client: sending message");
                    await pipeClient.WriteAsync(buf, 0, buf.Length);
                    Trace.WriteLine($"Client: sent message..requesting data");
                }
                {
                    var strBuf = Encoding.ASCII.GetBytes($"StrSharedMem {i}");
                    verb[0] = (byte)Verbs.verbStringSharedMem;
                    Marshal.WriteInt32(oop.mappedSection, strBuf.Length);
                    Marshal.Copy(strBuf, 0, oop.mappedSection + IntPtr.Size, strBuf.Length);
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
                var nIter = 10;
                var bufSpeed = new byte[oop.SpeedBufSize];
                bufSpeed[0] = (byte)Verbs.verbSpeedTest;
                var sw = Stopwatch.StartNew();
                for (int iter = 0; iter < nIter; iter++)
                {
                    Trace.WriteLine($"Sending chunk {iter}");
                    await pipeClient.WriteAsync(bufSpeed, 0, bufSpeed.Length);
                }
                var bps = (double)oop.SpeedBufSize * nIter / sw.Elapsed.TotalSeconds;
                Trace.WriteLine($"BytesPerSec = {bps:n0}"); // 1.4 G/Sec
            }
            //                if (oop.option == OOPOption.InProcTestLogging)
            {

                Trace.WriteLine($"Got log from server\r\n" + await oop.GetLogFromServer(pipeClient));

            }

        }

    }
}
