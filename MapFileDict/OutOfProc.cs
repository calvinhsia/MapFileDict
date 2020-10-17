using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
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
        GetStringSharedMem, // faster
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
        void AddVerbs()
        {
            AddVerb(Verbs.ServerQuit,
                 async (oop, arg) =>
                 {
                     await oop._pipeClient.WriteVerbAsync(Verbs.ServerQuit);
                     return null;
                 },
                 async (oop, arg) =>
                 {
                     Trace.WriteLine($"# dict entries = {dictObjRef.Count}");
                     await oop._pipeServer.SendAckAsync();
                     return Verbs.ServerQuit;
                 });

            AddVerb(Verbs.GetLog,
                async (oop, arg) =>
                {
                    await oop._pipeClient.WriteVerbAsync(Verbs.GetLog);
                    var logstrs = await oop._pipeClient.ReadStringAsAsciiAsync();
                    return logstrs as string;
                },
                async (oop, arg) =>
                {
                    await oop._pipeServer.SendAckAsync();
                    var strlog = string.Empty;
                    if (mylistener != null)
                    {
                        Trace.WriteLine($"# dict entries = {dictObjRef.Count}");
                        Trace.WriteLine($"Server: Getlog #entries = {mylistener.lstLoggedStrings.Count}");
                        strlog = string.Join("\r\n   ", mylistener.lstLoggedStrings);
                        mylistener.lstLoggedStrings.Clear();
                    }
                    await _pipeServer.WriteStringAsAsciiAsync(strlog);

                    return null;
                });

            AddVerb(Verbs.CreateSharedMemSection,
                async (oop, arg) =>
                {
                    var sharedRegionSize = (uint)arg;
                    await oop._pipeClient.WriteVerbAsync(Verbs.CreateSharedMemSection);
                    _pipeClient.WriteUInt32(sharedRegionSize);
                    var memRegionName = await _pipeClient.ReadStringAsAsciiAsync();
                    oop.CreateSharedSection(memRegionName, sharedRegionSize); // now map that region into the client
                    return null;
                },
                async (oop, arg) =>
                {
                    await oop._pipeServer.SendAckAsync();
                    var sizeRegion = oop._pipeServer.ReadUInt32();
                    CreateSharedSection(memRegionName: $"MapFileDictSharedMem_{pidClient}\0", regionSize: sizeRegion);
                    Trace.WriteLine($"{Process.GetCurrentProcess().ProcessName} IntPtr.Size = {IntPtr.Size} Shared Memory region address {_MemoryMappedRegionAddress.ToInt64():x16}");
                    await oop._pipeServer.WriteStringAsAsciiAsync(_sharedFileMapName);
                    return null;
                });


            AddVerb(Verbs.verbRequestData,
                async (oop, arg) =>
                {
                    await oop._pipeClient.WriteVerbAsync(Verbs.verbRequestData);
                    var res = await oop._pipeClient.ReadStringAsAsciiAsync();
                    return res;
                },
                async (oop, arg) =>
                {
                    await oop._pipeServer.SendAckAsync();
                    await oop._pipeServer.WriteStringAsAsciiAsync(DateTime.Now.ToString() + $" IntPtr.Size= {IntPtr.Size} {Process.GetCurrentProcess().MainModule.FileName}");
                    return null;
                });

            AddVerb(Verbs.Delay,
                async (oop, arg) =>
                {
                    await oop._pipeClient.WriteVerbAsync(Verbs.Delay);
                    byte delaysecs = (byte)arg;
                    Trace.WriteLine($"Client requesting delay of {delaysecs}");
                    oop._pipeClient.WriteByte(delaysecs); // # secs
                    await oop._pipeClient.GetAckAsync();
                    return null;
                },
                async (oop, arg) =>
                {
                    await oop._pipeServer.SendAckAsync();
                    var delaysecs = oop._pipeServer.ReadByte();
                    Trace.WriteLine($"Server got request to delay {delaysecs}");
                    await Task.Delay(TimeSpan.FromSeconds(delaysecs));
                    await oop._pipeServer.SendAckAsync();
                    return null;
                });

            AddVerb(Verbs.DoSpeedTest,
                async (oop, arg) =>
                {
                    await oop._pipeClient.WriteVerbAsync(Verbs.DoSpeedTest);
                    var bufSpeed = (byte[])arg;
                    var bufSize = (UInt32)bufSpeed.Length;
                    oop._pipeClient.WriteUInt32(bufSize);
                    await oop._pipeClient.WriteAsync(bufSpeed, 0, bufSpeed.Length);
                    await oop._pipeClient.GetAckAsync();
                    return null;
                },
                async (oop, arg) =>
                {
                    await oop._pipeServer.SendAckAsync();
                    var bufSize = oop._pipeServer.ReadUInt32();
                    var buf = new byte[bufSize];
                    await oop._pipeServer.ReadAsync(buf, 0, (int)bufSize);
                    Trace.WriteLine($"Server: got bytes {bufSize:n0}");
                    await oop._pipeServer.SendAckAsync();
                    return null;
                });

            AddVerb(Verbs.SendObjAndReferences,
                async (oop, arg) =>
                {
                    var tup = (Tuple<uint, List<uint>>)arg;
                    var numChildren = tup.Item2?.Count ?? 0;
                    _pipeClient.WriteByte((byte)Verbs.SendObjAndReferences);
                    _pipeClient.WriteUInt32(tup.Item1);
                    _pipeClient.WriteUInt32((uint)numChildren);

                    for (int iChild = 0; iChild < numChildren; iChild++)
                    {
                        _pipeClient.WriteUInt32(tup.Item2[iChild]);
                    }
                    await _pipeClient.GetAckAsync();
                    return null;
                },
                async (oop, arg) =>
                {
                    var lst = new List<uint>();
                    var obj = _pipeServer.ReadUInt32();
                    var cnt = _pipeServer.ReadUInt32();
                    for (int i = 0; i < cnt; i++)
                    {
                        lst.Add(_pipeServer.ReadUInt32());
                    }
                    dictObjRef[obj] = lst;
                    Trace.WriteLine($"Server got {nameof(Verbs.SendObjAndReferences)}  {obj:x8} # child = {cnt:n0}");
                    await oop._pipeServer.SendAckAsync();
                    return null;
                });

            AddVerb(Verbs.SendObjAndReferencesInChunks,
                async (oop, arg) =>
                {
                    await oop._pipeClient.WriteVerbAsync(Verbs.SendObjAndReferencesInChunks);

                    var tup = (Tuple<byte[], int>)arg;

                    var bufChunk = tup.Item1;
                    var ndxbufChunk = tup.Item2;
                    oop._pipeClient.WriteUInt32((uint)ndxbufChunk);
                    await oop._pipeClient.WriteAsync(bufChunk, 0, ndxbufChunk);
                    await oop._pipeClient.GetAckAsync();
                    return null;
                },
                async (oop, arg) =>
                {
                    await oop._pipeServer.SendAckAsync();
                    var bufSize = (int)oop._pipeServer.ReadUInt32();
                    var buf = new byte[bufSize];
                    await _pipeServer.ReadAsync(buf, 0, (int)bufSize);

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
                    await oop._pipeServer.SendAckAsync();
                    return null;
                });

            AddVerb(Verbs.CreateInvertedDictionary,
                async (oop, arg) =>
                {
                    await oop._pipeClient.WriteVerbAsync(Verbs.CreateInvertedDictionary);
                    var res = await oop._pipeClient.ReadStringAsAsciiAsync();
                    return res;
                },
                async (oop, arg) =>
                {
                    dictInverted = InvertDictionary(dictObjRef);
                    await _pipeServer.SendAckAsync();
                    return null;
                });


            AddVerb(Verbs.QueryParentOfObject,
                async (oop, arg) =>
                {
                    await oop._pipeClient.WriteVerbAsync(Verbs.QueryParentOfObject);
                    _pipeClient.WriteUInt32((uint)arg);
                    var lstParents = new List<uint>();
                    while (true)
                    {
                        var parent = _pipeClient.ReadUInt32();
                        if (parent == 0)
                        {
                            break;
                        }
                        lstParents.Add(parent);
                    }
                    return lstParents;
                },
                async (oop, arg) =>
                {
                    await _pipeServer.SendAckAsync();
                    var objQuery = _pipeServer.ReadUInt32();
                    if (dictInverted.TryGetValue(objQuery, out var lstParents))
                    {
                        var numParents = lstParents?.Count;
                        Trace.WriteLine($"Server: {objQuery:x8}  NumParents={numParents}");
                        if (numParents > 0)
                        {
                            foreach (var parent in lstParents)
                            {
                                _pipeServer.WriteUInt32(parent);
                            }
                        }
                    }
                    _pipeServer.WriteUInt32(0); // terminator
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