using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MapFileDict
{
    public enum Verbs
    {
        ServerQuit, // Quit the server process. Sends an Ack, so must wait for ack before done
        Acknowledge, // acknowledge receipt of verb
        CreateSharedMemSection, // create a region of memory that can be shared between the client/server
        GetLog, // get server log entries: will clear all entries so far
        GetString, // very slow
        GetStringSharedMem, // very fast
        DoSpeedTest,
        verbRequestData, // for testing
        DoMessageBox,   // for testing
        SendObjAndReferences, // a single obj and a list of it's references
        SendObjAndReferencesInChunks, // yields perf gains: from 5k objs/sec to 1M/sec
        CreateInvertedDictionary, // Create an inverte dict. From the dict of Obj=> list of child objs ref'd by the obj, it creates a new
                                  // dict containing every objid: for each is a list of parent objs (those that ref the original obj)
                                  // very useful for finding: e.g. who holds a reference to FOO
        QueryParentOfObject, // given an obj, get a list of objs that reference it
        Delayms,
    }
    public class OutOfProc : OutOfProcBase
    {
        Dictionary<uint, List<uint>> dictObjRef = new Dictionary<uint, List<uint>>();
        Dictionary<uint, List<uint>> dictInverted = null;
        public OutOfProc()
        {
            AddVerbs();
            tcsAddedVerbs.SetResult(0);
        }

        public OutOfProc(OutOfProcOptions options, CancellationToken token) : base(options, token)
        {
            AddVerbs();
            tcsAddedVerbs.SetResult(0);
        }
        /// <summary>
        /// The verbs are added to the Verbs enum and each has a two part implementation: one part that runs in the client code
        /// and one that runs in the separate 64 bit server process 
        /// This allows the client and the server code for each verb to be seen right next to each other
        /// The pattern that's sent from the client must exactly match the pattern read on the server:
        ///  e.g. if the client sends 2 bytes of SIZE and 4 bytes of OBJ, then the server must read the 2 then the 4
        /// Sending a verb can require an Acknowledge, but it's not required.
        /// WriteVerbAsync does wait for the Ack.
        /// A verb can have an argument or return a result. Complex arguments can be put into an object, as samples below show
        /// </summary>
        void AddVerbs()
        {
            AddVerb(Verbs.ServerQuit,
                 actClientSendVerb: async (arg) =>
                 {
                     await PipeFromClient.WriteVerbAsync(Verbs.ServerQuit);
                     return null;
                 },
                 actServerDoVerb: async (arg) =>
                 {
                     Trace.WriteLine($"# dict entries = {dictObjRef.Count}");
                     await PipeFromServer.WriteAcknowledgeAsync();
                     return Verbs.ServerQuit;
                 });

            AddVerb(Verbs.GetLog,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.GetLog);
                    var logstrs = await PipeFromClient.ReadStringAsAsciiAsync();
                    return logstrs as string;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var strlog = string.Empty;
                    if (mylistener != null)
                    {
                        Trace.WriteLine($"# dict entries = {dictObjRef.Count}");
                        Trace.WriteLine($"Server: Getlog #entries = {mylistener.lstLoggedStrings.Count}");
                        strlog = string.Join("\r\n   ", mylistener.lstLoggedStrings);
                        mylistener.lstLoggedStrings.Clear();
                    }
                    await PipeFromServer.WriteStringAsAsciiAsync(strlog);
                    return null;
                });

            // sample call: await oop.ClientCallServerWithVerb(Verbs.DoMessageBox, $"Message From Client");
            AddVerb(Verbs.DoMessageBox,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.DoMessageBox);
                    await PipeFromClient.WriteStringAsAsciiAsync((string)arg);
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var strFromClient = await PipeFromServer.ReadStringAsAsciiAsync();
                    MessageBox(0, $"Attach a debugger if desired {Process.GetCurrentProcess().Id} {Process.GetCurrentProcess().MainModule.FileName}\r\n{strFromClient}", "Server Process", 0);
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });

            AddVerb(Verbs.CreateSharedMemSection,
                actClientSendVerb: async (arg) =>
                {
                    var sharedRegionSize = (uint)arg;
                    await PipeFromClient.WriteVerbAsync(Verbs.CreateSharedMemSection);
                    await PipeFromClient.WriteUInt32(sharedRegionSize);
                    var memRegionName = await PipeFromClient.ReadStringAsAsciiAsync();
                    CreateSharedSection(memRegionName, sharedRegionSize); // now map that region into the client
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var sizeRegion = await PipeFromServer.ReadUInt32();
                    CreateSharedSection(memRegionName: $"MapFileDictSharedMem_{pidClient}\0", regionSize: sizeRegion);
                    Trace.WriteLine($"{Process.GetCurrentProcess().ProcessName} IntPtr.Size = {IntPtr.Size} Shared Memory region address {_MemoryMappedRegionAddress.ToInt64():x16}");
                    await PipeFromServer.WriteStringAsAsciiAsync(_sharedFileMapName);
                    return null;
                });

            AddVerb(Verbs.GetStringSharedMem,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.GetStringSharedMem);
                    var lenstr = await PipeFromClient.ReadUInt32();
                    var str = Marshal.PtrToStringAnsi(_MemoryMappedRegionAddress, (int)lenstr);
                    return str;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var strg = new string('a', 10000);
                    var bytes = Encoding.ASCII.GetBytes(strg);
                    Marshal.Copy(bytes, 0, _MemoryMappedRegionAddress, bytes.Length);
                    await PipeFromServer.WriteUInt32((uint)bytes.Length);
                    return null;
                });

            AddVerb(Verbs.verbRequestData,
                actClientSendVerb: async (arg) =>
                {
                    Trace.WriteLine($"{nameof(Verbs.verbRequestData)} WriteVerb");
                    await PipeFromClient.WriteVerbAsync(Verbs.verbRequestData);
                    var res = await PipeFromClient.ReadStringAsAsciiAsync();
                    return res;
                },
                actServerDoVerb: async (arg) =>
                {
                    Trace.WriteLine($"{nameof(Verbs.verbRequestData)} Server DoVerb");
                    await PipeFromServer.WriteAcknowledgeAsync();
                    await PipeFromServer.WriteStringAsAsciiAsync(
                        DateTime.Now.ToString() + $" IntPtr.Size= {IntPtr.Size} {Process.GetCurrentProcess().MainModule.FileName}");
                    return null;
                });

            AddVerb(Verbs.Delayms,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.Delayms);
                    uint delaymsecs = (uint)arg;
                    Trace.WriteLine($"Client requesting delay of {delaymsecs} ms");
                    await PipeFromClient.WriteUInt32(delaymsecs); // # secs
                    Trace.WriteLine($"  Client delay of {delaymsecs} sent");
                    await PipeFromClient.ReadAcknowledgeAsync();
                    Trace.WriteLine($"    Client delay of {delaymsecs} ms done");
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var delaymsecs = await PipeFromServer.ReadUInt32();
                    Trace.WriteLine($"Server got request to delay {delaymsecs} ms");
                    await Task.Delay(TimeSpan.FromMilliseconds(delaymsecs));
                    Trace.WriteLine($"  Server delay of {delaymsecs} ms done: send ack");
                    await PipeFromServer.WriteAcknowledgeAsync();
                    Trace.WriteLine($"    Server delay of {delaymsecs} ms done: send ack done");
                    return null;
                });

            AddVerb(Verbs.DoSpeedTest,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.DoSpeedTest);
                    var bufSpeed = (byte[])arg;
                    var bufSize = (UInt32)bufSpeed.Length;
                    await PipeFromClient.WriteUInt32(bufSize);
                    await PipeFromClient.WriteAsync(bufSpeed, 0, bufSpeed.Length);
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var bufSize = await PipeFromServer.ReadUInt32();
                    var buf = new byte[bufSize];
                    await PipeFromServer.ReadAsync(buf, 0, (int)bufSize);
                    Trace.WriteLine($"Server: got bytes {bufSize:n0}"); // 1.2G/sec raw pipe speed
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });

            AddVerb(Verbs.SendObjAndReferences,
                actClientSendVerb: async (arg) =>
                {
                    var tup = (Tuple<uint, List<uint>>)arg;
                    var numChildren = tup.Item2?.Count ?? 0;
                    PipeFromClient.WriteByte((byte)Verbs.SendObjAndReferences);
                    await PipeFromClient.WriteUInt32(tup.Item1);
                    await PipeFromClient.WriteUInt32((uint)numChildren);
                    for (int iChild = 0; iChild < numChildren; iChild++)
                    {
                        await PipeFromClient.WriteUInt32(tup.Item2[iChild]);
                    }
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    var lst = new List<uint>();
                    var obj = await PipeFromServer.ReadUInt32();
                    var cnt = await PipeFromServer.ReadUInt32();
                    for (int i = 0; i < cnt; i++)
                    {
                        lst.Add(await PipeFromServer.ReadUInt32());
                    }
                    dictObjRef[obj] = lst;
                    Trace.WriteLine($"Server got {nameof(Verbs.SendObjAndReferences)}  {obj:x8} # child = {cnt:n0}");
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });

            //Need to send 10s of millions of objs: sending in chunks is much faster than one at a time.
            AddVerb(Verbs.SendObjAndReferencesInChunks,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.SendObjAndReferencesInChunks);
                    var tup = (Tuple<byte[], int>)arg;
                    var bufChunk = tup.Item1;
                    var ndxbufChunk = tup.Item2;
                    await PipeFromClient.WriteUInt32((uint)ndxbufChunk);
                    await PipeFromClient.WriteAsync(bufChunk, 0, ndxbufChunk);
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var bufSize = (int)await PipeFromServer.ReadUInt32();
                    var buf = new byte[bufSize];
                    await PipeFromServer.ReadAsync(buf, 0, (int)bufSize);
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
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });

            AddVerb(Verbs.CreateInvertedDictionary,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.CreateInvertedDictionary);
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    dictInverted = InvertDictionary(dictObjRef);
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });


            AddVerb(Verbs.QueryParentOfObject,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.QueryParentOfObject);
                    await PipeFromClient.WriteUInt32((uint)arg);
                    var lstParents = new List<uint>();
                    while (true)
                    {
                        var parent = await PipeFromClient.ReadUInt32();
                        if (parent == 0)
                        {
                            break;
                        }
                        lstParents.Add(parent);
                    }
                    return lstParents;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var objQuery = await PipeFromServer.ReadUInt32();
                    if (dictInverted.TryGetValue(objQuery, out var lstParents))
                    {
                        var numParents = lstParents?.Count;
//                        Trace.WriteLine($"Server: {objQuery:x8}  NumParents={numParents}");
                        if (numParents > 0)
                        {
                            foreach (var parent in lstParents)
                            {
                                await PipeFromServer.WriteUInt32(parent);
                            }
                        }
                    }
                    await PipeFromServer.WriteUInt32(0); // terminator
                    return null;
                });

        }
        /// <summary>
        /// This runs on the client to send the data in chunks to the server. The object graph is multi million objects
        /// so we don't want to create them all in a data structure to send, but enumerate them and send in chunks
        /// </summary>
        public async Task<Tuple<int, int>> SendObjGraphEnumerableInChunksAsync(IEnumerable<Tuple<uint, List<uint>>> ienumOGraph)
        {
            var bufChunkSize = 65532;
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
                { // if the entire obj won't fit, we have to send a different way
                    // 0450ee60 Roslyn.Utilities.StringTable+Entry[]
                    // 0460eea0 Microsoft.CodeAnalysis.VisualBasic.Syntax.InternalSyntax.SyntaxNodeCache+Entry[]
                    // 049b90e0 Microsoft.CodeAnalysis.SyntaxNode[]
                    Trace.WriteLine($"The cur obj {tup.Item1:x8} size={numBytesForThisObj} is too big for chunk {bufChunkSize}: sending via non-chunk");
                    await ClientSendVerb(Verbs.SendObjAndReferences, tup); // send it on it's own with a different verb
                }
                else
                {
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
                await ClientSendVerb(Verbs.SendObjAndReferencesInChunks, Tuple.Create<byte[], int>(bufChunk, ndxbufChunk));
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
    }
}