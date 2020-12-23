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
        PipeBroken = -1, //indicates end of stream (pipe closed): e.g. client process killed
        EstablishConnection, // Handshake the version of the connection
        GetLastError, // get the last error from the server. Will be reset to ""
        ServerQuit, // Quit the server process. Sends an Ack, so must wait for ack before done
        Acknowledge, // acknowledge receipt of verb
        /// <summary>
        /// 1 param: uint region size. Most efficient if multiple of AllocationGranularity=64k
        /// Sets <see cref="_MemoryMappedRegionAddress"/>
        /// </summary>
        CreateSharedMemSection, // create a region of memory that can be shared between the client/server
        ExceptionOccurredOnServer,
        SetExceptionValueForTest, //test only: create an exception when this value is reached
        CloseSharedMemSection,
        GetLog, // get server log entries: will clear all entries so far
        GetString, // very slow
        GetStringSharedMem, // very fast
        DoSpeedTestWithByteBuff,
        DoSpeedTestWithUInts,
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
        ObjsAndTypesDoneSending,
        GetObjsOfType,
        GetFirstType,
        GetNextType,
        GetTypesAndCounts,
        GetTypeSummary,
    }
    public class OutOfProc : OutOfProcBase
    {
        public static uint ConnectionVersion = 1;
        public string LastError = string.Empty;

        // these dictionaries live in the server. Some are temp
        Dictionary<uint, string> dictTypeIdToTypeName = new Dictionary<uint, string>(); // server: temporary typeId to TypeName
        // a dictionary can only have 65536 entries: resizing beyond 47,995,853 causes approx doubling to 95,991,737, which throws OOM (Array dimensions exceeded supported range
        //   the GC Heap tries to keep consecutive chunks smaller than a segment size
        //  This means we can't out all the objects in a single dictionary, so we'll use a list of dictionaries
        const int MaxDictSize = 32000;
        List<Dictionary<uint, Tuple<uint, uint>>> lstDictObjToTypeIdAndSize = new List<Dictionary<uint, Tuple<uint, uint>>>();// server: temporary: obj to Tuple<TypeId, Size> used to transfer objs and their types

        Dictionary<string, List<Tuple<uint, uint>>> dictTypeToObjAndSizeList = new Dictionary<string, List<Tuple<uint, uint>>>(); // server: TypeName to List<objs>


        SortedList<uint, Dictionary<uint, List<uint>>> slistObjToRefs = new SortedList<uint, Dictionary<uint, List<uint>>>();
        SortedList<uint, Dictionary<uint, List<uint>>> slistObjToParents = null;
        Dictionary<uint, List<uint>> dictObjToRefs = new Dictionary<uint, List<uint>>(); // server: obj=> list<objs referenced by obj>
        Dictionary<uint, List<uint>> dictObjToParents = null; // server: created from inverting dictObjToRefs. obj=>List<objs that reference obj>



        private Task taskCreateDictionariesForSentObjects;
        private Task taskCreateInvertedObjRefDictionary;
        private Dictionary<string, List<Tuple<uint, uint>>>.KeyCollection.Enumerator _enumeratorDictTypes;
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
                     return null;
                 });

            AddVerb(Verbs.SetExceptionValueForTest,
                 actClientSendVerb: async (arg) =>
                 {
                     await PipeFromClient.WriteVerbAsync(Verbs.SetExceptionValueForTest);
                     await PipeFromClient.WriteUInt32((uint)arg); // send value
                     await PipeFromClient.ReadAcknowledgeAsync();
                     return null;
                 },
                 actServerDoVerb: async (arg) =>
                 {
                     await PipeFromServer.WriteAcknowledgeAsync();
                     Options.TypeIdAtWhichToThrowException = await PipeFromServer.ReadUInt32();
                     await PipeFromServer.WriteAcknowledgeAsync();
                     return null;
                 });

            AddVerb(Verbs.ServerQuit,
                 actClientSendVerb: async (arg) =>
                 {
                     await PipeFromClient.WriteVerbAsync(Verbs.ServerQuit);
                     return null;
                 },
                 actServerDoVerb: async (arg) =>
                 {
                     try
                     {
                         await PipeFromServer.WriteAcknowledgeAsync(); // pipe could be broken
                     }
                     catch (System.IO.IOException)
                     {
                     }
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
                        Trace.WriteLine($"# dictObjRef = {dictObjToRefs.Count}");
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

            AddVerb(Verbs.DoSpeedTestWithByteBuff,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.DoSpeedTestWithByteBuff);
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
                    Trace.WriteLine($"Server: ByteBuff got bytes {bufSize:n0}"); // 1.2G/sec raw pipe speed
                    await PipeFromServer.WriteAcknowledgeAsync();
                    return null;
                });

            AddVerb(Verbs.DoSpeedTestWithUInts,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.DoSpeedTestWithUInts);
                    var bufSpeed = (uint[])arg;
                    var bufSize = (UInt32)bufSpeed.Length;
                    await PipeFromClient.WriteUInt32(bufSize);
                    var buf4 = new byte[4];
                    for (int i = 0; i < bufSize; i++)
                    {
                        PipeFromClient.Write(buf4, 0, 4);
                    }
                    await PipeFromClient.ReadAcknowledgeAsync();
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var bufSize = await PipeFromServer.ReadUInt32();
                    var buf = new uint[bufSize];
                    var buf4 = new byte[4];
                    for (int i = 0; i < bufSize; i++)
                    {
                        var dat = PipeFromServer.Read(buf4, 0, 4);
                    }
                    Trace.WriteLine($"Server: got UINT bytes {bufSize:n0}");
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
                    Dictionary<uint, Tuple<uint, uint>> dictObjToTypeIdAndSize = null;
                    if (lstDictObjToTypeIdAndSize.Count == 0)
                    {
                        dictObjToTypeIdAndSize = new Dictionary<uint, Tuple<uint, uint>>();
                        lstDictObjToTypeIdAndSize.Add(dictObjToTypeIdAndSize);
                    }
                    else
                    {
                        dictObjToTypeIdAndSize = lstDictObjToTypeIdAndSize[lstDictObjToTypeIdAndSize.Count - 1];
                    }
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
                            var objSize = ptr[bufNdx++];
                            dictObjToTypeIdAndSize[obj] = Tuple.Create(typeId, objSize);
                            if (dictObjToTypeIdAndSize.Count > MaxDictSize)
                            {
                                dictObjToTypeIdAndSize = new Dictionary<uint, Tuple<uint, uint>>();
                                lstDictObjToTypeIdAndSize.Add(dictObjToTypeIdAndSize);
                            }
                            if (Options.TypeIdAtWhichToThrowException != 0 && bufNdx >= Options.TypeIdAtWhichToThrowException)
                            {
                                throw new InvalidOperationException("Intentional exception for testing");
                            }
                        }
                    }
                    await PipeFromServer.WriteAcknowledgeAsync();
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

            AddVerb(Verbs.ObjsAndTypesDoneSending,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.ObjsAndTypesDoneSending);
                    return null;
                },
                actServerDoVerb: async (arg) =>
                {
                    this.taskCreateDictionariesForSentObjects = Task.Run(() =>
                    {
                        Trace.WriteLine($" lstDictObjToTypeIdAndSize.Count = {lstDictObjToTypeIdAndSize.Count}");
                        foreach (var dictObjToTypeIdAndSize in lstDictObjToTypeIdAndSize)
                        {
                            foreach (var objToTypeAndSizeItem in dictObjToTypeIdAndSize)
                            {
                                var typeName = dictTypeIdToTypeName[objToTypeAndSizeItem.Value.Item1];
                                if (!dictTypeToObjAndSizeList.TryGetValue(typeName, out var lstObjs))
                                {
                                    lstObjs = new List<Tuple<uint, uint>>(); // list(obj, size)
                                    dictTypeToObjAndSizeList[typeName] = lstObjs;
                                }
                                lstObjs.Add(Tuple.Create(objToTypeAndSizeItem.Key, objToTypeAndSizeItem.Value.Item2)); // obj and size
                            }
                        }
                        lstDictObjToTypeIdAndSize = null;// don't neeed it any more: it's huge
                        dictTypeIdToTypeName = null;
                        Trace.WriteLine($"Server has dictTypeToObjList {dictTypeToObjAndSizeList.Count}");
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
                    await taskCreateDictionariesForSentObjects;
                    _enumeratorDictTypes = dictTypeToObjAndSizeList.Keys.GetEnumerator();
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

            AddVerb(Verbs.GetTypesAndCounts,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.GetTypesAndCounts);
                    var res = new List<Tuple<string, uint>>();
                    while (true)
                    {
                        var cnt = await PipeFromClient.ReadUInt32();
                        if (cnt == 0)
                        {
                            break;
                        }
                        var type = await PipeFromClient.ReadStringAsAsciiAsync();
                        res.Add(Tuple.Create(type, cnt));
                    }
                    return res;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    await taskCreateDictionariesForSentObjects;
                    foreach (var kvp in dictTypeToObjAndSizeList.OrderByDescending(k => k.Value.Count))
                    {
                        await PipeFromServer.WriteUInt32((uint)kvp.Value.Count);
                        await PipeFromServer.WriteStringAsAsciiAsync(kvp.Key);
                    }
                    await PipeFromServer.WriteUInt32(0); //null term
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
                    var tup = (Tuple<string, uint>)arg; // typename, maxnumToReturn
                    await PipeFromClient.WriteVerbAsync(Verbs.GetObjsOfType);
                    await PipeFromClient.WriteUInt32(tup.Item2); // max: sometimes we only want 1 object of the class to get the ClrType (e.g. EventHandlers)
                    await PipeFromClient.WriteStringAsAsciiAsync(tup.Item1);
                    var lstObjs = new List<Tuple<uint, uint>>(); // obj, size
                    while (true)
                    {
                        var bufsize = await PipeFromClient.ReadUInt32();
                        if (bufsize == 0)
                        {
                            break;
                        }
                        for (int i = 0; i < bufsize; i += 2)
                        {
                            unsafe
                            {
                                var ptr = (uint*)_MemoryMappedRegionAddress;
                                var obj = ptr[i];
                                if (obj == 0)
                                {
                                    throw new InvalidOperationException("Null obj");
                                }
                                var size = ptr[i + 1];
                                lstObjs.Add(Tuple.Create(obj, size));
                            }
                        }
                        await PipeFromClient.WriteAcknowledgeAsync(); // got this chunk
                    }
                    return lstObjs;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    var maxnumobjs = await PipeFromServer.ReadUInt32();
                    var strType = await PipeFromServer.ReadStringAsAsciiAsync();
                    await taskCreateDictionariesForSentObjects;
                    int cnt = 0;
                    var bufChunkSize = _sharedMapSize - 4;// leave extra room for null term
                    var bufNdx = 0U;
                    if (dictTypeToObjAndSizeList.TryGetValue(strType, out var lstObjs))
                    {
                        foreach (var obj in lstObjs)
                        {
                            if (4 * bufNdx >= bufChunkSize)
                            {
                                await PipeFromServer.WriteUInt32(bufNdx);
                                await PipeFromServer.ReadAcknowledgeAsync();
                                bufNdx = 0;
                            }
                            unsafe
                            {
                                var ptr = (uint*)_MemoryMappedRegionAddress;
                                ptr[bufNdx++] = obj.Item1; // obj
                                ptr[bufNdx++] = obj.Item2; //size
                            }
                            cnt++;
                            if (maxnumobjs != 0 && cnt == maxnumobjs)
                            {
                                break;
                            }
                        }
                        if (bufNdx > 0) // send partial chunk
                        {
                            await PipeFromServer.WriteUInt32(bufNdx);
                            await PipeFromServer.ReadAcknowledgeAsync();
                        }
                    }
                    await PipeFromServer.WriteUInt32(0); // terminator
                    return null;
                });

            AddVerb(Verbs.GetTypeSummary,
                actClientSendVerb: async (arg) =>
                {
                    await PipeFromClient.WriteVerbAsync(Verbs.GetTypeSummary);
                    var res = new List<Tuple<string, uint, uint>>(); // typename, count, sum(size)
                    while (true)
                    {
                        var bufNdx = 0U;
                        var bufsize = await PipeFromClient.ReadUInt32();
                        if (bufsize == 0)
                        {
                            break;
                        }
                        unsafe
                        {
                            var ptr = (uint*)_MemoryMappedRegionAddress;
                            while (bufNdx < bufsize)
                            {
                                var strTypeLen = ptr[bufNdx++];
                                if (strTypeLen == 0)
                                {
                                    break;
                                }
                                var typeName = Marshal.PtrToStringAnsi(_MemoryMappedRegionAddress + (int)bufNdx * 4, (int)strTypeLen);
                                bufNdx += (uint)((typeName.Length + 3) / 4);//round up to nearest 4 so we can continue to index by UINT
                                var typeCount = ptr[bufNdx++];
                                var typeSize = ptr[bufNdx++];
                                res.Add(Tuple.Create(typeName, typeCount, typeSize));
                            }
                        }
                        await PipeFromClient.WriteAcknowledgeAsync();
                    }
                    return res;
                },
                actServerDoVerb: async (arg) =>
                {
                    await PipeFromServer.WriteAcknowledgeAsync();
                    await taskCreateDictionariesForSentObjects;
                    var bufChunkSize = _sharedMapSize - 4;// leave extra room for null term
                    var bufNdx = 0U;
                    foreach (var typeTuple in dictTypeToObjAndSizeList.OrderByDescending(k => k.Value.Sum(t => t.Item2)))
                    {
                        var numBytesRequired = 4 + typeTuple.Key.Length + 4 + 4; // strlen, strTypeNameBytes, count, sum(size)
                        if (4 * bufNdx + numBytesRequired >= bufChunkSize)
                        {
                            await SendBufferAsync();
                            bufNdx = 0;
                            if (numBytesRequired > bufChunkSize)
                            {
                                throw new InvalidOperationException($"Type name too big");
                            }
                        }
                        unsafe
                        {
                            var ptr = (uint*)_MemoryMappedRegionAddress;
                            ptr[bufNdx++] = (uint)typeTuple.Key.Length;
                            var bytesTypeName = Encoding.ASCII.GetBytes(typeTuple.Key);
                            Marshal.Copy(bytesTypeName, 0, _MemoryMappedRegionAddress + (int)bufNdx * 4, typeTuple.Key.Length);
                            bufNdx += (uint)((typeTuple.Key.Length + 3) / 4);//round up to nearest 4 so we can continue to index by UINT
                            ptr[bufNdx++] = (uint)typeTuple.Value.Count;
                            ptr[bufNdx++] = (uint)typeTuple.Value.Sum(t => t.Item2);
                        }
                    }
                    if (bufNdx > 0) // send partial chunk
                    {
                        await SendBufferAsync();
                    }
                    await PipeFromServer.WriteUInt32(0); // terminator for all chunks
                    return null;
                    async Task SendBufferAsync()
                    {
                        unsafe
                        {
                            var ptr = (uint*)_MemoryMappedRegionAddress;
                            ptr[bufNdx++] = 0; //null term for this chunk
                        }
                        await PipeFromServer.WriteUInt32(bufNdx); // size of chunk
                        await PipeFromServer.ReadAcknowledgeAsync();
                    }
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
        public async Task<Tuple<int, int>> SendObjRefGraphEnumerableInChunksAsync(
            IEnumerable<Tuple<uint, List<uint>>> ienumOGraph,
            Action<int> actPerChunk = null
            ) // don't want to have dependency on ValueTuple
        {
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
                actPerChunk?.Invoke(numChunksSent);
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