using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MapFileDict
{
    public enum Verbs
    {
        EstablishConnection, // Handshake the version of the connection
        GetLastError, // get the last error from the server. Will be reset to ""
        ServerQuit, // Quit the server process. Sends an Ack, so must wait for ack before done
        Acknowledge, // acknowledge receipt of verb
        /// <summary>
        /// 1 param: uint region size. Most efficient if multiple of AllocationGranularity=64k
        /// Sets <see cref="_MemoryMappedRegionAddress"/>
        /// </summary>
        CreateSharedMemSection, // create a region of memory that can be shared between the client/server
        CloseSharedMemSection,
        GetLog, // get server log entries: will clear all entries so far
        GetString, // very slow
        GetStringSharedMem, // very fast
        DoSpeedTest,
        verbRequestData, // for testing
        DoMessageBox,   // for testing
        SendObjAndTypeIdInChunks,
        SendTypeIdAndTypeNameInChunks,
        SendObjAndReferences, // a single obj and a list of it's references
        SendObjAndReferencesInChunks, // yields perf gains: from 5k objs/sec to 1M/sec
        CreateInvertedObjRefDictionary, // Create an inverte dict. From the dict of Obj=> list of child objs ref'd by the obj, it creates a new
                                        // dict containing every objid: for each is a list of parent objs (those that ref the original obj)
                                        // very useful for finding: e.g. who holds a reference to FOO
        QueryParentOfObject, // given an obj, get a list of objs that reference it
        Delayms,
        ObjsAndTypesDone,
        GetObjsOfType,
        GetFirstType,
        GetNextType,
    }
    public class OutOfProc : OutOfProcBase
    {
        public static uint ConnectionVersion = 1;
        public string LastError = string.Empty;

        readonly Dictionary<uint, string> dictTypeIdToTypeName = new Dictionary<uint, string>(); // typeId to TypeName
        Dictionary<uint, uint> dictObjToTypeId = new Dictionary<uint, uint>();
        //Dictionary<uint, string> dictObjToType = new Dictionary<uint, string>(); // from objId to TypeName
        Dictionary<string, List<uint>> dictTypeToObjList = new Dictionary<string, List<uint>>(); // TypeName to List<objs>

        Dictionary<uint, List<uint>> dictObjToRefs = new Dictionary<uint, List<uint>>();
        Dictionary<uint, List<uint>> dictObjToParents = null;
        private Task taskProcessSentObjects;
        private Task taskCreateInvertedObjRefDictionary;
        private Dictionary<string, List<uint>>.KeyCollection.Enumerator _enumeratorDictTypes;
        private string _strRegExFilterTypes;

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
        public async Task ConnectToServerAsync(CancellationToken token)
        {
            await PipeFromClient.ConnectAsync(token);
            var errcode = (uint)await this.ClientSendVerbAsync(Verbs.EstablishConnection, 0);
            if (errcode != 0)
            {
                var lastError = (string)await this.ClientSendVerbAsync(Verbs.GetLastError, null);
                throw new Exception($"Error establishing connection to server " + lastError);
            }
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
        /// IMPORTANT: in the client code, don't write to the server pipe and vv: they are both live when run as inproc. As out of proc a nullref.
        /// </summary>
        void AddVerbs()
        {
            AddVerb(Verbs.EstablishConnection,
                 actClientSendVerb: async (arg) =>
                 {
                     await PipeFromClient.WriteVerbAsync(Verbs.EstablishConnection);
                     await PipeFromClient.WriteUInt32(ConnectionVersion);
                     var errcode = await PipeFromClient.ReadUInt32();
                     return errcode;
                 },
                 actServerDoVerb: async (arg) =>
                 {
                     await PipeFromServer.WriteAcknowledgeAsync();
                     var clientVersion = await PipeFromServer.ReadUInt32();
                     uint errCode = 0;
                     if (clientVersion != ConnectionVersion)
                     {
                         LastError = $"Error Connection Version mismatch: ServerVersion:{ConnectionVersion} ClientVersion:{clientVersion}";
                         errCode = 1;
                     }
                     PipeFromServer.WriteByte((byte)errCode); // error
                     return null;
                 });
            AddVerb(Verbs.GetLastError,
                 actClientSendVerb: async (arg) =>
                 {
                     await PipeFromClient.WriteVerbAsync(Verbs.GetLastError);
                     var lastError = (string)await PipeFromClient.ReadStringAsAsciiAsync();
                     return lastError;
                 },
                 actServerDoVerb: async (arg) =>
                 {
                     await PipeFromServer.WriteAcknowledgeAsync();
                     await PipeFromServer.WriteStringAsAsciiAsync(LastError);
                     LastError = string.Empty;
                     return null; // tell the server loop to quit
                 });

            AddVerb(Verbs.ServerQuit,
                 actClientSendVerb: async (arg) =>
                 {
                     await PipeFromClient.WriteVerbAsync(Verbs.ServerQuit);
                     return null;
                 },
                 actServerDoVerb: async (arg) =>
                 {
                     if (ClientAndServerInSameProcess) // if same process, GetLog from server not called
                     {
                         Trace.WriteLine($"#dictObjToTypeId = {dictObjToTypeId.Count}  # dictObjRef = {dictObjToRefs.Count}");
                     }
                     await PipeFromServer.WriteAcknowledgeAsync();
                     return Verbs.ServerQuit; // tell the server loop to quit
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
                        Trace.WriteLine($"#dictObjToTypeId = {dictObjToTypeId.Count}  # dictObjRef = {dictObjToRefs.Count}");
                        Trace.WriteLine($"Server: Getlog #entries = {mylistener.lstLoggedStrings.Count}");
                        strlog = string.Join("\r\n   ", mylistener.lstLoggedStrings);
                        mylistener.lstLoggedStrings.Clear();
                    }
                    else
                    {
                        strlog = $"Nothing in server log. {nameof(Options.ServerTraceLogging)}= {Options.ServerTraceLogging}" + Environment.NewLine;
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
                    if (_MemoryMappedRegionAddress != IntPtr.Zero)
                    {
                        throw new InvalidOperationException($"Shared memory region already created");
                    }
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
                    await PipeFromServer.WriteStringAsAsciiAsync(_sharedFileMapName);
                    return null;
                });

            AddVerb(Verbs.CloseSharedMemSection,
                actClientSendVerb: async (arg) =>
                {
                    if (!ClientAndServerInSameProcess)
                    {
                        await PipeFromClient.WriteVerbAsync(Verbs.CloseSharedMemSection);
                    }
                    CloseSharedSection();
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    CloseSharedSection();
                    await PipeFromServer.WriteAcknowledgeAsync();
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
                    PipeFromClient.WriteByte((byte)Verbs.SendObjAndReferences);
                    var tup = (Tuple<uint, List<uint>>)arg;
                    var numChildren = tup.Item2?.Count ?? 0;
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
                    var hashset = new List<uint>();// EnumerateObjectReferences sometimes has duplicate children <sigh>
                    var obj = await PipeFromServer.ReadUInt32();
                    var cnt = await PipeFromServer.ReadUInt32();
                    for (int i = 0; i < cnt; i++)
                    {
                        hashset.Add(await PipeFromServer.ReadUInt32());
                    }
                    dictObjToRefs[obj] = hashset.ToList();
                    Trace.WriteLine($"Server got {nameof(Verbs.SendObjAndReferences)}  {obj:x8} # child = {cnt:n0}");
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });


            AddVerb(Verbs.SendObjAndTypeIdInChunks,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.SendObjAndTypeIdInChunks);
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var bufNdx = 0;
                    unsafe
                    {
                        var ptr = (uint*)_MemoryMappedRegionAddress;
                        while (true)
                        {
                            var obj = ptr[bufNdx++];
                            if (obj == 0)
                            {
                                break;
                            }
                            var typeId = ptr[bufNdx++];
                            dictObjToTypeId[obj] = typeId;
                        }
                    }
                    await PipeFromServer.WriteAcknowledgeAsync();
                    //                    Trace.WriteLine($"server got {dictObjToTypeId.Count}");
                    return null;
                });

            AddVerb(Verbs.SendTypeIdAndTypeNameInChunks,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.SendTypeIdAndTypeNameInChunks);
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var bufNdx = 0;
                    unsafe
                    {
                        var ptr = (uint*)_MemoryMappedRegionAddress;
                        while (true)
                        {
                            var typeId = ptr[bufNdx++];
                            if (typeId == 0)
                            {
                                break;
                            }
                            var typeNameLen = (int)ptr[bufNdx++];
                            var typeName = Marshal.PtrToStringAnsi(_MemoryMappedRegionAddress + bufNdx * 4, typeNameLen);
                            bufNdx += ((typeNameLen + 3) / 4);//round up to nearest 4 so we can continue to index by UINT
                            dictTypeIdToTypeName[typeId] = typeName;
                        }
                    }
                    await PipeFromServer.WriteAcknowledgeAsync();
                    //                    Trace.WriteLine($"server got {dictObjToTypeId.Count}");
                    return null;
                });

            AddVerb(Verbs.ObjsAndTypesDone,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.ObjsAndTypesDone);
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    this.taskProcessSentObjects = Task.Run(() =>
                    {
                        foreach (var objToTypeItem in dictObjToTypeId)
                        {
                            var typeName = dictTypeIdToTypeName[objToTypeItem.Value];
                            if (!dictTypeToObjList.TryGetValue(typeName, out var lstObjs))
                            {
                                lstObjs = new List<uint>();
                                dictTypeToObjList[typeName] = lstObjs;
                            }
                            lstObjs.Add(objToTypeItem.Key);
                        }
                        Trace.WriteLine($"Server has dictTypeToObjList {dictTypeToObjList.Count}");
                    });
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });

            AddVerb(Verbs.GetFirstType,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.GetFirstType);
                    await PipeFromClient.WriteStringAsAsciiAsync(arg as string); // regexfilter
                    var first = await PipeFromClient.ReadStringAsAsciiAsync();
                    return first;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    _strRegExFilterTypes = await PipeFromServer.ReadStringAsAsciiAsync();
                    await taskProcessSentObjects;
                    _enumeratorDictTypes = dictTypeToObjList.Keys.GetEnumerator();
                    var fGotOne = false;
                    while (_enumeratorDictTypes.MoveNext())
                    {
                        var val = _enumeratorDictTypes.Current;
                        if (string.IsNullOrEmpty(_strRegExFilterTypes) || Regex.IsMatch(val, _strRegExFilterTypes, RegexOptions.IgnoreCase))
                        {
                            await PipeFromServer.WriteStringAsAsciiAsync(val);
                            fGotOne = true;
                            break;
                        }
                    }
                    if (!fGotOne)
                    {
                        await PipeFromServer.WriteStringAsAsciiAsync(string.Empty);
                    }
                    return null;
                });

            AddVerb(Verbs.GetNextType,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.GetNextType);
                    var next = await PipeFromClient.ReadStringAsAsciiAsync();
                    return next;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var fGotOne = false;
                    while (_enumeratorDictTypes.MoveNext())
                    {
                        var val = _enumeratorDictTypes.Current;
                        if (string.IsNullOrEmpty(_strRegExFilterTypes) || Regex.IsMatch(val, _strRegExFilterTypes))
                        {
                            await PipeFromServer.WriteStringAsAsciiAsync(val);
                            fGotOne = true;
                            break;
                        }
                    }
                    if (!fGotOne)
                    {
                        await PipeFromServer.WriteStringAsAsciiAsync(string.Empty);
                    }
                    return null;
                });

            //Need to send 10s of millions of objs: sending in chunks is much faster than one at a time.
            AddVerb(Verbs.SendObjAndReferencesInChunks,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.SendObjAndReferencesInChunks);
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var bufNdx = 0;
                    unsafe
                    {
                        var ptr = (uint*)_MemoryMappedRegionAddress;
                        while (true)
                        {
                            var hashset = new HashSet<uint>();// EnumerateObjectReferences sometimes has duplicate children <sigh>
                            var obj = ptr[bufNdx++];
                            if (obj == 0)
                            {
                                break;
                            }
                            var cnt = ptr[bufNdx++];
                            if (cnt > 0)
                            {
                                for (int i = 0; i < cnt; i++)
                                {
                                    hashset.Add(ptr[bufNdx++]);
                                }
                            }
                            dictObjToRefs[obj] = hashset.ToList();
                        }
                    }
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });

            AddVerb(Verbs.GetObjsOfType,
                actClientSendVerb: async (arg) =>
                {
                    var tup = (Tuple<string, uint>)arg;
                    await PipeFromClient.WriteVerbAsync(Verbs.GetObjsOfType);
                    await PipeFromClient.WriteUInt32(tup.Item2); // max: sometimes we only want 1 object of the class to get the ClrType (e.g. EventHandlers)
                    await PipeFromClient.WriteStringAsAsciiAsync(tup.Item1);
                    var lstObjs = new List<uint>();
                    while (true)
                    {
                        var obj = await PipeFromClient.ReadUInt32();
                        if (obj == 0)
                        {
                            break;
                        }
                        lstObjs.Add(obj);
                    }
                    return lstObjs;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var maxnumobjs = await PipeFromServer.ReadUInt32();
                    var strType = await PipeFromServer.ReadStringAsAsciiAsync();
                    await taskProcessSentObjects;
                    int cnt = 0;
                    if (dictTypeToObjList.TryGetValue(strType, out var lstObjs))
                    {
                        foreach (var obj in lstObjs)
                        {
                            await PipeFromServer.WriteUInt32(obj);
                            if (maxnumobjs != 0 && ++cnt >= maxnumobjs)
                            {
                                break;
                            }
                        }
                    }
                    await PipeFromServer.WriteUInt32(0); // terminator
                    return null;
                });

            AddVerb(Verbs.CreateInvertedObjRefDictionary,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.CreateInvertedObjRefDictionary);
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    this.taskCreateInvertedObjRefDictionary = Task.Run(async () =>
                    {
                        dictObjToParents = await InvertDictionaryAsync(dictObjToRefs);
                    });
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
                    await this.taskCreateInvertedObjRefDictionary;
                    if (dictObjToParents.TryGetValue(objQuery, out var lstParents))
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
        public async Task<Tuple<int, int>> SendObjRefGraphEnumerableInChunksAsync(IEnumerable<Tuple<uint, List<uint>>> ienumOGraph) // don't want to have dependency on ValueTuple
        {
            await ClientSendVerbAsync(Verbs.CreateSharedMemSection, 2 * MemMap.AllocationGranularity);
            // can't have unsafe in lambda or anon func
            var bufChunkSize = _sharedMapSize - 4;// leave extra room for null term
            int ndxbufChunk = 0;
            var numChunksSent = 0;
            var numObjs = 0;
            foreach (var tup in ienumOGraph)
            {
                numObjs++;
                var numChildren = (uint)(tup.Item2?.Count ?? 0);
                var numBytesForThisObj = (1 + 1 + numChildren) * IntPtr.Size; // obj + childCount + children
                if (numBytesForThisObj >= bufChunkSize)
                { // if the entire obj won't fit, we have to send a different way
                  // 0450ee60 Roslyn.Utilities.StringTable+Entry[]
                  // 0460eea0 Microsoft.CodeAnalysis.VisualBasic.Syntax.InternalSyntax.SyntaxNodeCache+Entry[]
                  // 049b90e0 Microsoft.CodeAnalysis.SyntaxNode[]
                    Trace.WriteLine($"The cur obj {tup.Item1:x8} size={numBytesForThisObj} is too big for chunk {bufChunkSize}: sending via non-chunk");
                    await ClientSendVerbAsync(Verbs.SendObjAndReferences, tup); // send it on it's own with a different verb
                    numChunksSent++;
                }
                else
                {
                    if (4 * ndxbufChunk + numBytesForThisObj >= bufChunkSize) // too big for cur buf?
                    {
                        await SendBufferAsync();
                        ndxbufChunk = 0;
                    }
                    unsafe
                    {
                        var ptr = (uint*)_MemoryMappedRegionAddress;
                        ptr[ndxbufChunk++] = tup.Item1;
                        ptr[ndxbufChunk++] = numChildren;
                        for (int iChild = 0; iChild < numChildren; iChild++)
                        {
                            ptr[ndxbufChunk++] = tup.Item2[iChild];
                        }
                    }
                }
            }
            if (ndxbufChunk > 0) // leftovers
            {
                Trace.WriteLine($"Client: send leftovers {ndxbufChunk}");
                await SendBufferAsync();
            }
            await ClientSendVerbAsync(Verbs.CloseSharedMemSection, 0);
            return Tuple.Create<int, int>(numObjs, numChunksSent);
            async Task SendBufferAsync()
            {
                unsafe
                {
                    var ptr = (uint*)_MemoryMappedRegionAddress;
                    ptr[ndxbufChunk++] = 0; //null term
                }
                await ClientSendVerbAsync(Verbs.SendObjAndReferencesInChunks, ndxbufChunk);
                numChunksSent++;
            }
        }

        public static Task<Dictionary<uint, List<uint>>> InvertDictionaryAsync(Dictionary<uint, List<uint>> dictOGraph)
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
            return Task.FromResult(dictInvert);
        }
    }
}