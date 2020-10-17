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
        verbRequestData, // len = 1 byte: 0 args
        SendObjAndReferences, // a single obj and a list of it's references
        SendObjAndReferencesInChunks, // yields perf gains: from 5k objs/sec to 1M/sec
        CreateInvertedDictionary, // Create an inverte dict. From the dict of Obj=> list of child objs ref'd by the obj, it creates a new
                                  // dict containing every objid: for each is a list of parent objs (those that ref the original obj)
                                  // very useful for finding: e.g. who holds a reference to FOO
        QueryParentOfObject, // given an obj, get a list of objs that reference it
        Delay,
    }
    public class OutOfProc : OutOfProcBase
    {
        Dictionary<uint, List<uint>> dictObjRef = new Dictionary<uint, List<uint>>();
        Dictionary<uint, List<uint>> dictInverted = null;
        public OutOfProc()
        {
            AddVerbs();
        }

        public OutOfProc(int pidClient, CancellationToken token) : base(pidClient, token)
        {
            AddVerbs();
        }
        /// <summary>
        /// The verbs are added to the Verbs enum and each has a two part implementation: one part that runs in the client code
        /// and one that runs in the separate 64 bit server process 
        /// This allows the client and the server code for each verb to be seen right next to each other
        /// The pattern that's sent from the client must exactly match the pattern read on the server:
        ///  e.g. if the client sends 2 bytes of SIZE and 4 bytes of OBJ, then the server must read the 2 then the 4
        /// Sending a verb can require an Acknowledge, but it's not required.
        /// WriteVerbAsync does wait for the Ack.
        /// </summary>
        void AddVerbs()
        {
            AddVerb(Verbs.ServerQuit,
                 async (arg) =>
                 {
                     await PipeFromClient.WriteVerbAsync(Verbs.ServerQuit);
                     return null;
                 },
                 async (arg) =>
                 {
                     Trace.WriteLine($"# dict entries = {dictObjRef.Count}");
                     await PipeFromServer.WriteAcknowledgeAsync();
                     return Verbs.ServerQuit;
                 });

            AddVerb(Verbs.GetLog,
                async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.GetLog);
                    var logstrs = await PipeFromClient.ReadStringAsAsciiAsync();
                    return logstrs as string;
                },
                async (arg) =>
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

            AddVerb(Verbs.CreateSharedMemSection,
                async (arg) =>
                {
                    var sharedRegionSize = (uint)arg;
                    await PipeFromClient.WriteVerbAsync(Verbs.CreateSharedMemSection);
                    PipeFromClient.WriteUInt32(sharedRegionSize);
                    var memRegionName = await PipeFromClient.ReadStringAsAsciiAsync();
                    CreateSharedSection(memRegionName, sharedRegionSize); // now map that region into the client
                    return null;
                },
                async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var sizeRegion = PipeFromServer.ReadUInt32();
                    CreateSharedSection(memRegionName: $"MapFileDictSharedMem_{pidClient}\0", regionSize: sizeRegion);
                    Trace.WriteLine($"{Process.GetCurrentProcess().ProcessName} IntPtr.Size = {IntPtr.Size} Shared Memory region address {_MemoryMappedRegionAddress.ToInt64():x16}");
                    await PipeFromServer.WriteStringAsAsciiAsync(_sharedFileMapName);
                    return null;
                });

            AddVerb(Verbs.GetStringSharedMem,
                async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.GetStringSharedMem);
                    var lenstr = PipeFromClient.ReadUInt32();
                    var str = Marshal.PtrToStringAnsi(_MemoryMappedRegionAddress, (int)lenstr);
                    return str;
                },
                async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var strg = new string('a', 10000);
                    var bytes = Encoding.ASCII.GetBytes(strg);
                    Marshal.Copy(bytes, 0, _MemoryMappedRegionAddress, bytes.Length);
                    PipeFromServer.WriteUInt32((uint)bytes.Length);

                    return null;
                });


            AddVerb(Verbs.verbRequestData,
                async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.verbRequestData);
                    var res = await PipeFromClient.ReadStringAsAsciiAsync();
                    return res;
                },
                async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    await PipeFromServer.WriteStringAsAsciiAsync(DateTime.Now.ToString() + $" IntPtr.Size= {IntPtr.Size} {Process.GetCurrentProcess().MainModule.FileName}");
                    return null;
                });

            AddVerb(Verbs.Delay,
                async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.Delay);
                    byte delaysecs = (byte)arg;
                    Trace.WriteLine($"Client requesting delay of {delaysecs}");
                    PipeFromClient.WriteByte(delaysecs); // # secs
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var delaysecs = PipeFromServer.ReadByte();
                    Trace.WriteLine($"Server got request to delay {delaysecs}");
                    await Task.Delay(TimeSpan.FromSeconds(delaysecs));
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });

            AddVerb(Verbs.DoSpeedTest,
                async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.DoSpeedTest);
                    var bufSpeed = (byte[])arg;
                    var bufSize = (UInt32)bufSpeed.Length;
                    PipeFromClient.WriteUInt32(bufSize);
                    await PipeFromClient.WriteAsync(bufSpeed, 0, bufSpeed.Length);
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var bufSize = PipeFromServer.ReadUInt32();
                    var buf = new byte[bufSize];
                    await PipeFromServer.ReadAsync(buf, 0, (int)bufSize);
                    Trace.WriteLine($"Server: got bytes {bufSize:n0}");
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });

            AddVerb(Verbs.SendObjAndReferences,
                async (arg) =>
                {
                    var tup = (Tuple<uint, List<uint>>)arg;
                    var numChildren = tup.Item2?.Count ?? 0;
                    PipeFromClient.WriteByte((byte)Verbs.SendObjAndReferences);
                    PipeFromClient.WriteUInt32(tup.Item1);
                    PipeFromClient.WriteUInt32((uint)numChildren);

                    for (int iChild = 0; iChild < numChildren; iChild++)
                    {
                        PipeFromClient.WriteUInt32(tup.Item2[iChild]);
                    }
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                async (arg) =>
                {
                    var lst = new List<uint>();
                    var obj = PipeFromServer.ReadUInt32();
                    var cnt = PipeFromServer.ReadUInt32();
                    for (int i = 0; i < cnt; i++)
                    {
                        lst.Add(PipeFromServer.ReadUInt32());
                    }
                    dictObjRef[obj] = lst;
                    Trace.WriteLine($"Server got {nameof(Verbs.SendObjAndReferences)}  {obj:x8} # child = {cnt:n0}");
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });

            AddVerb(Verbs.SendObjAndReferencesInChunks,
                async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.SendObjAndReferencesInChunks);

                    var tup = (Tuple<byte[], int>)arg;

                    var bufChunk = tup.Item1;
                    var ndxbufChunk = tup.Item2;
                    PipeFromClient.WriteUInt32((uint)ndxbufChunk);
                    await PipeFromClient.WriteAsync(bufChunk, 0, ndxbufChunk);
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var bufSize = (int)PipeFromServer.ReadUInt32();
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
                async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.CreateInvertedDictionary);
                    var res = await PipeFromClient.ReadStringAsAsciiAsync();
                    return res;
                },
                async (arg) =>
                {
                    dictInverted = InvertDictionary(dictObjRef);
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });


            AddVerb(Verbs.QueryParentOfObject,
                async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.QueryParentOfObject);
                    PipeFromClient.WriteUInt32((uint)arg);
                    var lstParents = new List<uint>();
                    while (true)
                    {
                        var parent = PipeFromClient.ReadUInt32();
                        if (parent == 0)
                        {
                            break;
                        }
                        lstParents.Add(parent);
                    }
                    return lstParents;
                },
                async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var objQuery = PipeFromServer.ReadUInt32();
                    if (dictInverted.TryGetValue(objQuery, out var lstParents))
                    {
                        var numParents = lstParents?.Count;
                        Trace.WriteLine($"Server: {objQuery:x8}  NumParents={numParents}");
                        if (numParents > 0)
                        {
                            foreach (var parent in lstParents)
                            {
                                PipeFromServer.WriteUInt32(parent);
                            }
                        }
                    }
                    PipeFromServer.WriteUInt32(0); // terminator
                    await Task.Yield();// need an await in method
                    return null;
                });

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
                    await ClientCallServerWithVerb(Verbs.SendObjAndReferences, tup);
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
                await ClientCallServerWithVerb(Verbs.SendObjAndReferencesInChunks, Tuple.Create<byte[], int>(bufChunk, ndxbufChunk));
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