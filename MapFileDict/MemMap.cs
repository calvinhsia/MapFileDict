using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;

namespace MapFileDict
{
    public enum MapMemTypes
    {
        MapMemTypePageFile,
        MapMemTypeFileName,
        MapMemTypeChildProcess,
    }
    public class MemMap : IDisposable
    {
        public const uint AllocationGranularity = 0x10000; //64k

        internal IntPtr _hFileHandle; // for Pagefile, INVALID_HANDLE_VALUE

        internal MapMemTypes _MapFileDictType;
        internal ulong _ulGrowDynamicallyThreshold;
        internal ulong _initialsize;
        internal uint _uiViewSize;
        public ulong ulMaxSize { get; set; } // throw when gets to this size
        public string MapFileName; // file name for MapMemTypeFileName. Defaults GetTempFileName
        public string _MapName = "test"; // base name for mapping file in memory (not a real file)
        public bool fUseISerializable;

        internal const int _sizeofRef = 8;//Marshal.SizeOf((UInt64)0); // size of a reference in the map

        internal Dictionary<Type, ClassInfo> _dictClassInfo; // class offsets, fieldinfo
        internal Dictionary<uint, List<MHandle>> _freeLists;// desired size, list of free spots
        internal Dictionary<ulong, MapFileLocator> _setCurAllocs; // all (including freed). Also includes internally allocated (like Class ref members) 
        internal bool _fCompactPending = false;

        MappingData _currentMapping; // can be null

        public stats _stats;
        [DebuggerDisplay("{ToString()}")]
        public struct stats
        {
            internal ulong _ulMapMemSize; // overall size of underlying mapping object
            internal ulong _ulOffsetFreeMem; // offset to next free available mem
            internal int _nResize; // count of Resize ops
            internal int _nMapViews; // count of MapViews ever created: yields unique name
            internal uint _nAdded; //used as index, starting with 1. Index = 0 indicates NULL
            internal int _nGetData;
            internal ulong _nRemoved;
            internal ulong _nCurBytesAlloc;
            internal ulong _nBytesAllocatedTot;
            internal ulong _nBytesFreedTot;
            internal int _nCompacts;
            internal int _nFileMaps;
            public override string ToString()
            {
                return string.Format(
              "Size={0:n0} OffFree={1:x8} Resize={2} nMaps{3:n0} Added={4:n0} Get {5:n0} Rmv {6:n0} TotBytesAlloc={7:n0} TotBytesFreed={8:n0} CurBytes={9:n0} #Cmpcts={10} nFileMaps={11}",
                   _ulMapMemSize,
                   _ulOffsetFreeMem,
                   _nResize,
                   _nMapViews,
                   _nAdded,
                   _nGetData,
                   _nRemoved,
                   _nBytesAllocatedTot,
                   _nBytesFreedTot,
                   _nCurBytesAlloc,
                   _nCompacts,
                   _nFileMaps
                   );
            }

            internal void Clear()
            {
                _ulOffsetFreeMem = 0;
                _ulMapMemSize = 0;
                _nMapViews = 0;
                _nCurBytesAlloc = 0;
                _nBytesAllocatedTot = 0;
                _nAdded = 0;
            }
        }

        /// <summary>
        /// Allows storing of data in system page file or file on disk
        /// dynamically grows/shrinks as needed. Can switch to disk automatically as needed
        /// Internally data is allocated in 64K chunks, but details are hidden from users
        /// Can be used as backing store to System.Collections.Generic items like Dictionary and List
        /// 
        /// </summary>
        /// <param name="ulInitialSize"></param>
        /// <param name="mapfileType"></param>
        /// <param name="ulGrowDynamicallyThreshold"></param>
        /// <param name="uiViewSize"></param>
        public MemMap(
            ulong ulInitialSize = 0,
            MapMemTypes mapfileType = MapMemTypes.MapMemTypePageFile,
            ulong ulGrowDynamicallyThreshold = 0,
            uint uiViewSize = AllocationGranularity
            )
        {
            _initialsize = ulInitialSize;
            _uiViewSize = uiViewSize;
            _MapFileDictType = mapfileType;
            _ulGrowDynamicallyThreshold = ulGrowDynamicallyThreshold;

            if (_initialsize < AllocationGranularity)
            {
                _initialsize = AllocationGranularity;
            }
            else
                if (_initialsize % AllocationGranularity != 0)
            {
                _initialsize = (_initialsize + AllocationGranularity) & (~AllocationGranularity + 1);
            }
            Clear();
        }

        public override string ToString()
        {
            return _stats.ToString();
        }

        public void Dispose()
        {
            cleanup();
            GC.SuppressFinalize(this);
        }
        ~MemMap()
        {
            cleanup();
        }

        private void cleanup()
        {
            if (_currentMapping != null)
            {
                _currentMapping.Dispose();
                _currentMapping = null;
            }
            if (_hFileHandle != (IntPtr)NativeMethods.INVALID_HANDLE_VALUE && _hFileHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_hFileHandle);
                _hFileHandle = IntPtr.Zero;
            }
        }

        public void Clear()
        {
            cleanup();
            _currentMapping = null;
            _fCompactPending = false;
            _freeLists = new Dictionary<uint, List<MHandle>>();
            _dictClassInfo = new Dictionary<Type, ClassInfo>();
            _setCurAllocs = new Dictionary<ulong, MapFileLocator>();
            _stats.Clear();
            //            MakeOffsets(typeof(string)); //ensure "String" is in class info dict
        }

        internal void VerifyStuff()
        {
            //*

            /*/
             
           ulong dwUsedSize = 0;
           foreach (var mfl in _setCurAllocs.Values)
           {
               dwUsedSize += mfl.ulSize;
           }
           Debug.Assert(dwUsedSize == _stats._ulOffsetFreeMem, "dwUsedSize == _stats._ulOffsetFreeMem");

           Debug.Assert(dwUsedSize <= _stats._ulMapMemSize, "dwUsedSize + _stats._nBytesFreedTot < _stats._ulMapMemSize");

           if (_freeLists != null)
           {
               ulong dwFreeSize = 0;
               foreach (var list in _freeLists.Values)
               {
                   foreach (var item in list)
                   {
                       dwFreeSize += _setCurAllocs[item._mapIndx].ulSize;
                   }
               }
               Debug.Assert(dwFreeSize == _stats._nBytesFreedTot, "dwFreeSize== _stats._nBytesFreedTot");
           }
             //*/
        }

        public bool Resize(ulong newSize)
        {
            bool fDidResize = false;
            if (_stats._ulMapMemSize == 0)
            {
                newSize = Math.Max(newSize, _initialsize);
            }
            _stats._nResize++;
            if (newSize > _stats._ulMapMemSize)  // initially , _ulMapMemSize is 0
            {
                var oldFreeMemOffset = _stats._ulOffsetFreeMem;
                switch (_MapFileDictType)
                {
                    case MapMemTypes.MapMemTypeChildProcess:
                        throw new NotImplementedException("resize " + _MapFileDictType.ToString());
                    case MapMemTypes.MapMemTypePageFile:
                        if (ulMaxSize != 0 && newSize >= ulMaxSize)
                        {
                            throw new OutOfMemoryException(string.Format("Greater than max size {0:n0}", ulMaxSize));
                        }
                        if (_ulGrowDynamicallyThreshold != 0 && newSize > _ulGrowDynamicallyThreshold)
                        {
                            _MapFileDictType = MapMemTypes.MapMemTypeFileName;
                            goto case MapMemTypes.MapMemTypeFileName;
                        }
                        _hFileHandle = (IntPtr)NativeMethods.INVALID_HANDLE_VALUE;// os paging file: use INVALID_HANDLE_VALUE
                        break;
                    case MapMemTypes.MapMemTypeFileName:
                        var tempfilename = MapFileName;
                        if (string.IsNullOrEmpty(MapFileName))
                        {
                            tempfilename = System.IO.Path.GetTempFileName();
                        }
                        if (_hFileHandle != IntPtr.Zero && _hFileHandle != (IntPtr)NativeMethods.INVALID_HANDLE_VALUE)
                        {
                            NativeMethods.CloseHandle(_hFileHandle);// close the file handle: the oldMapping is still not closed, so the copy of old data below works
                        }
                        _hFileHandle = NativeMethods.CreateFile(
                            tempfilename,
                            NativeMethods.GENERIC_READ + NativeMethods.GENERIC_WRITE,
                            (uint)NativeMethods.Win32FileShare.FILE_SHARE_NONE,
                            IntPtr.Zero,
                            (uint)NativeMethods.Win32CreationDisposition.CREATE_ALWAYS,
                            NativeMethods.FILE_ATTRIBUTE_TEMPORARY,
                            IntPtr.Zero
                            );
                        if (_hFileHandle == (IntPtr)NativeMethods.INVALID_HANDLE_VALUE)
                        {
                            var errFile = Marshal.GetLastWin32Error();
                            throw new Exception(string.Format("can't create temp file {0} {1}", errFile, tempfilename));
                        }
                        break;
                }
                MappingData newMappingData = CreateAMapping(newSize);

                if (_currentMapping != null && _currentMapping._hFileMapping != IntPtr.Zero) // prior data. ToDo: if it's file based, don't need this
                {
                    uint nBlocks = (uint)(_stats._ulMapMemSize / AllocationGranularity);
                    for (uint i = 0; i < nBlocks; i++)
                    {
                        var oldaddr = MapView(i * AllocationGranularity, AllocationGranularity, _currentMapping);
                        var newaddr = MapView(i * AllocationGranularity, AllocationGranularity, newMappingData);
                        NativeMethods.CopyMemory(newaddr, oldaddr, AllocationGranularity);
                    }
                    _currentMapping.Dispose();
                }
                _currentMapping = newMappingData;
                _stats._ulMapMemSize = newSize;
                fDidResize = true;
            }
            else
            { //shrink
                if (Compact())
                {
                    fDidResize = Resize(newSize);// recur
                }
            }
            return fDidResize;
        }

        MappingData CreateAMapping(ulong newSize)
        {
            while (true)
            {
                // a unique string to this process, and different from an existing map filename
                var mapName = string.Format("{0}{1}{2}", _MapName, ++_stats._nFileMaps, Process.GetCurrentProcess().Id); //"MapFileUI"

                var hFileMapping = NativeMethods.CreateFileMapping(
                    _hFileHandle, // INVALID_HANDLE_VALUE means use os page file
                    IntPtr.Zero, // lpSecurityAttributes
                    (uint)NativeMethods.PAGE_READWRITE,
                    (uint)(newSize >> 32 & UInt32.MaxValue),
                    (uint)(newSize & UInt32.MaxValue),
                    mapName
                    );
                var err = Marshal.GetLastWin32Error();
                if (hFileMapping == IntPtr.Zero)
                {
                    var h = Marshal.GetHRForLastWin32Error();
                    // 2^35 = 34,359,738,368   1455 = The paging file is too small for this operation to complete. 
                    if (h == 1455) // 34 Gigs!Total paging file size for all drives: 26,560Meg
                    {
                        throw new OutOfMemoryException(string.Format("The paging file is too small for this operation to complete.  {0} {1}", err, h));
                    }
                    throw new OutOfMemoryException(string.Format("Can't create file mapping {0} {1}", err, h));
                }
                // If the object exists before the function call, the function returns a handle to the existing object (with its current size, not the specified size), and GetLastErrorreturns ERROR_ALREADY_EXISTS.
                if (err == NativeMethods.ERROR_ALREADY_EXISTS)
                {
                    NativeMethods.CloseHandle(hFileMapping);
                    continue;
                }
                var mapData = new MappingData()
                {
                    _hFileMapping = hFileMapping,
                    _ulFileSize = newSize
                };
                return mapData;
            }
        }
        /// <summary>
        /// garbage collect/compact the data
        /// </summary>
        /// <param name="FreeSizeThreshold">if free</param>
        public bool Compact(int FreeSizeThreshold = 32768, bool fQuick = false)
        {
            //            return false;
            VerifyStuff();
            bool fDidCompact = false;
            _fCompactPending = false;
            ulong freeSize = _stats._nBytesFreedTot;

            if (freeSize >= (ulong)FreeSizeThreshold)
            {
                var newSize = _stats._nCurBytesAlloc;
                ulong pow2 = AllocationGranularity;
                while (pow2 < newSize)
                {
                    pow2 <<= 1; // find next power of 2 > desired size
                }
                newSize = pow2; // new size can be same as orig size
                var newSetAllocs = new Dictionary<ulong, MapFileLocator>();

                _stats._nCompacts++;
                var newMappingData = CreateAMapping(newSize);
                uint ulOffsetNew = 0;
                foreach (var mfl in _setCurAllocs.Values.Where(a => !a.fIsFree))
                {
                    newSetAllocs[mfl.nIndex] = mfl;
                    var oldAddr = MapView(mfl.ulOffset, mfl.ulSize, _currentMapping);
                    var newAddr = MapView(ulOffsetNew, mfl.ulSize, newMappingData);
                    mfl.ulOffset = ulOffsetNew;
                    NativeMethods.CopyMemory(newAddr, oldAddr, mfl.ulSize);
                    ulOffsetNew += mfl.ulSize;
                }
                _currentMapping.Dispose();
                _currentMapping = newMappingData;
                _stats._ulMapMemSize = newSize;
                _stats._ulOffsetFreeMem = ulOffsetNew;
                _setCurAllocs = newSetAllocs;
                _freeLists.Clear();
                _stats._nBytesFreedTot = 0;
                fDidCompact = true;
                VerifyStuff();
            }
            return fDidCompact;
        }

        internal class MappingData : IDisposable
        {
            public IntPtr _hFileMapping; // handle of file map (could be INVALID_HANDLE_VALUE for sys paging file, or a real file handle
            public IntPtr _mapBase; // base address of current mapping (multiple of AllocationGranularity)
            public ulong _ulBaseOffset; // the offset into the mapped object
            public uint _currentMappedViewSize; // the size of the current view, multiple of AllocationGranularity
            public ulong _ulFileSize; // total size of underlying mapped object (pagefile or file used)

            public override string ToString()
            {
                return string.Format("{0:x8} {1} {2} {3}", _mapBase.ToInt32(), _ulBaseOffset, _currentMappedViewSize, _ulFileSize);
            }
            public void CloseView()
            {
                if (_mapBase != IntPtr.Zero)
                {
                    UnmapView(_mapBase);
                    _mapBase = IntPtr.Zero;
                }
            }
            public void Dispose()
            {
                CloseView();
                if (_hFileMapping != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(_hFileMapping);
                    _hFileMapping = IntPtr.Zero;
                }
            }
        }

        /// <summary>
        ///  For perf, the MappedView size can be changed. If the client wants faster perf, set this bigger and vv.
        ///  e.g. user wants to use a feature and brings it to foreground
        ///  Must be multiple of AllocationGranularity (65536 for 32 bit process)
        /// </summary>
        public bool ChangeViewSize(uint newSize)
        {
            bool fDidIt = false;
            if (newSize > 0 && (newSize & (AllocationGranularity - 1)) == 0)
            {
                if (_currentMapping != null)
                {
                    _currentMapping.CloseView();
                }
                _uiViewSize = newSize;
                if (_initialsize < _uiViewSize)
                {
                    _initialsize = _uiViewSize;
                }
                if (_stats._ulMapMemSize < _uiViewSize)
                {
                    Resize(_uiViewSize);
                }
                fDidIt = true;
            }
            return fDidIt;
        }

        internal IntPtr MapView(ulong ulOffset, uint numBytesToMap, MappingData mappingData)
        {
            // mappings must be on 64k boundary.
            var mappedAddress = IntPtr.Zero;
            var newBaseOffset = ulOffset & (~AllocationGranularity + 1);
            var leftover = ulOffset - newBaseOffset;
            var preferredAddress = IntPtr.Zero; // if we use preferred address, OS doesn't have to try to find a free VA range
            var desiredSize = _uiViewSize;

            if (mappingData._mapBase != IntPtr.Zero) // is there a prior mapping?
            {
                // if what we want to map is already mapped (from the current mapping data)
                bool fFits = ulOffset >= mappingData._ulBaseOffset &&
                    ulOffset + numBytesToMap < mappingData._ulBaseOffset + mappingData._currentMappedViewSize;
                // if it fits. If we're very near the end of the view buffer
                if (fFits && leftover + numBytesToMap < mappingData._currentMappedViewSize)
                { // fits entirely in memory: reuse prior mapping
                    mappedAddress = mappingData._mapBase + ((int)(newBaseOffset - mappingData._ulBaseOffset + leftover));
                }
                else
                {
                    preferredAddress = mappingData._mapBase;
                    UnmapView(mappingData._mapBase); //unmap prior view
                    //                    Debug.Assert(numBytesToMap <= AllocationGranularity, "#bytes to map should be AllocationGran");
                }
            }
            if (mappedAddress == IntPtr.Zero)
            { // if we must do a mapping
                // if the thing we're using is bigger than initial view size, we need a bigger view
                while (leftover + numBytesToMap > desiredSize)
                {
                    desiredSize += AllocationGranularity;
                }
                if (newBaseOffset + desiredSize > mappingData._ulFileSize)
                { // if the view goes beyond the underlying filemapping, truncate
                    desiredSize = (uint)(mappingData._ulFileSize - newBaseOffset);
                }
                _stats._nMapViews++;
                // try at preferred address
                mappedAddress = NativeMethods.MapViewOfFileEx(
                    mappingData._hFileMapping,
                    NativeMethods.FILE_MAP_READ | NativeMethods.FILE_MAP_WRITE,
                    (uint)(newBaseOffset >> 32 & UInt32.MaxValue),
                    (uint)(newBaseOffset & UInt32.MaxValue),
                    desiredSize,
                    preferredAddress
                    );
                if (mappedAddress == IntPtr.Zero)
                {
                    if (preferredAddress != IntPtr.Zero)
                    {
                        // try again at any address
                        mappedAddress = NativeMethods.MapViewOfFileEx(
                            mappingData._hFileMapping,
                            NativeMethods.FILE_MAP_READ | NativeMethods.FILE_MAP_WRITE,
                            (uint)(newBaseOffset >> 32 & UInt32.MaxValue),
                            (uint)(newBaseOffset & UInt32.MaxValue),
                            desiredSize,
                            IntPtr.Zero
                            );
                    }
                    if (mappedAddress == IntPtr.Zero)
                    {
                        var err = Marshal.GetLastWin32Error();
                        var h = Marshal.GetHRForLastWin32Error();
                        throw new InvalidOperationException(string.Format("Can't MapView {0:n0} {1} hr = {2:x8}", ulOffset, numBytesToMap, h));
                    }
                }
                mappingData._mapBase = mappedAddress;
                mappingData._currentMappedViewSize = desiredSize;
                mappingData._ulBaseOffset = newBaseOffset;
                mappedAddress = mappedAddress + (int)leftover;
            }
            return mappedAddress;
        }

        static void UnmapView(IntPtr addr)
        {
            var res = NativeMethods.UnmapViewOfFile(addr);
            Debug.Assert(res, "Unmapview didn't work");
        }

        MemMapStream AllocSpace(uint dataSize, bool fIsRecurring)
        {
            var mapStream = new MemMapStream();
            // we want to round up to nearest NRound boundary  to reduce the # of separate lists in freelists
            dataSize = RoundIt(dataSize);
            ulong ndexToUse = ulong.MaxValue;
            List<MHandle> lstFree = null;

            if (_freeLists.TryGetValue(dataSize, out lstFree))
            {// found a free slot of desired size
                ndexToUse = lstFree[lstFree.Count - 1]._mapIndx;
                mapStream._mfloc = _setCurAllocs[ndexToUse];
                Debug.Assert(mapStream._mfloc.fIsFree, "freelist block not marked free?");
                lstFree.RemoveAt(lstFree.Count - 1);
                if (lstFree.Count == 0)
                {
                    _freeLists.Remove(dataSize);
                }
                _stats._nBytesFreedTot -= dataSize;
                Debug.Assert(mapStream._mfloc.ulSize == dataSize, "freelist size not desired size");
            }
            else
            { // need to allocate space. Is it > current mapping object? if so, gotta grow
                if (_stats._ulOffsetFreeMem + dataSize >= _stats._ulMapMemSize)
                {
                    // gotta grow
                    if (!fIsRecurring) // only if not recurring
                    {
                        Compact();
                    }
                    else
                    {
                        _fCompactPending = true;
                    }
                    if (_stats._ulOffsetFreeMem + dataSize >= _stats._ulMapMemSize)
                    {
                        var newSize = _stats._ulMapMemSize * 2;
                        if (newSize == 0)
                        {
                            newSize = _initialsize;
                        }
                        var requiredSize = dataSize + _stats._nCurBytesAlloc;
                        while (newSize < requiredSize)
                        {
                            newSize *= 2;
                        }
                        Resize(newSize);
                    }
                }
                mapStream._mfloc = new MapFileLocator()
                {
                    ulOffset = _stats._ulOffsetFreeMem, // Create a location to put the data
                    ulSize = dataSize
                };
                _stats._ulOffsetFreeMem += dataSize;
            }
            if (ndexToUse == ulong.MaxValue) // if we don't have one from freelist
            {
                ndexToUse = ++_stats._nAdded; // just use a counter
            }
            mapStream._mfloc.nIndex = ndexToUse;
            mapStream._addrStart = MapView(mapStream._mfloc.ulOffset, mapStream._mfloc.ulSize, _currentMapping);
            _stats._nBytesAllocatedTot += dataSize;
            _stats._nCurBytesAlloc += dataSize;
            return mapStream;
        }

        protected internal ClassInfo GetClassInfo(Type type, FieldInfo fldref = null, Type ParentType = null)
        {
            ClassInfo clsInfo;
            if (!_dictClassInfo.TryGetValue(type, out clsInfo))
            {
                clsInfo = new ClassInfo(type);
                _dictClassInfo[type] = clsInfo;
                if (type.IsArray)
                {
                    var arType = type.GetElementType();
                    var arclassinfo = GetClassInfo(arType); //recur
                    clsInfo._classOrArrayElemSize = arclassinfo._classOrArrayElemSize;
                    clsInfo._ArrayElemType = arType;
                }
                else
                {
                    int typeSize = IntPtr.Size;
                    if (IsSimpleType(type, out typeSize))
                    {
                        clsInfo._simpleType = type;
                        clsInfo._classOrArrayElemSize += typeSize; // for String, this size is not used: we use the actual string len
                    }
                    else if (type.Name == "String")
                    {
                        clsInfo._classOrArrayElemSize = _sizeofRef; // a string (null, empty or normal) is a reference. 
                    }
                    else
                    {
                        var fieldInfo = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(
                            mem => mem.MemberType == MemberTypes.Field && !((FieldInfo)mem).IsStatic
                            //(type.Name == "String" ? true : !((FieldInfo)mem).IsStatic)
                            ).ToArray();
                        if (fieldInfo.Length == 0)
                        {
                            if (fldref != null) // the cur type is ref'd by the parent
                            {
                                var parentClsInfo = _dictClassInfo[ParentType];

                                if (parentClsInfo._objDict == null)
                                {
                                    parentClsInfo._objDict = new Dictionary<string, List<object>>();
                                }
                                if (!parentClsInfo._objDict.ContainsKey(fldref.Name))
                                {
                                    parentClsInfo._objDict[fldref.Name] = new List<object>();
                                }
                            }
                            clsInfo._classOrArrayElemSize = 8;
                        }
                        else
                        {
                            clsInfo._offsets = new int[fieldInfo.Length];
                            clsInfo._FieldInfo = new FieldInfo[fieldInfo.Length];

                            int i = 0;
                            foreach (var mem in fieldInfo)
                            {
                                var fld = mem as FieldInfo;
                                clsInfo._FieldInfo[i] = fld;
                                clsInfo._offsets[i] = clsInfo._classOrArrayElemSize; // set offset to size calc'd so far
                                int nTypeSize = IntPtr.Size;
                                if (IsSimpleType(fld.FieldType, out nTypeSize))
                                {
                                    clsInfo._classOrArrayElemSize += nTypeSize;
                                }
                                else
                                {
                                    if (fld.FieldType.Name == "String")
                                    {
                                        GetClassInfo(typeof(string)); // ensure string exists
                                        clsInfo._classOrArrayElemSize += _sizeofRef;//Marshal.SizeOf(Locator);
                                    }
                                    else
                                    {
                                        var refclassinfo = GetClassInfo(fld.FieldType, fldref: fld, ParentType: type); //recur
                                        // regardless of what the class is: like member array, it's a reference
                                        clsInfo._classOrArrayElemSize += _sizeofRef;
                                        if (fld.FieldType.IsAbstract)
                                        { // abstract classes: after storing ref, store hashcode of type so we can look in class dict which type to instantiate
                                            clsInfo._classOrArrayElemSize += sizeof(int);
                                        }
                                    }
                                }
                                //                                Debug.Assert(clsInfo._classOrArrayElemSize > 0, "clsSize == 0?");
                                i++;
                            }
                        }
                    }
                }
                if (clsInfo._classOrArrayElemSize == 0)
                {
                    throw new InvalidOperationException("ClassSize == 0? " + type.Name);
                }
            }
            return clsInfo;
        }

        protected internal class ClassInfo
        {
            public Type _type;
            public Type _simpleType; // if it's e.g. an Int
            public FieldInfo[] _FieldInfo;
            public int[] _offsets;

            public int _classOrArrayElemSize; // in Bytes. for an array, it's the array element size
            public Type _ArrayElemType;
            public Dictionary<string, List<object>> _objDict;
            public ClassInfo(Type type)
            {
                _type = type;
            }
            public bool IsReferenceType
            {
                get
                {
                    bool fIsRef = false;
                    if (_FieldInfo != null)
                    {
                        fIsRef = true;
                    }
                    else
                    {
                        if (_ArrayElemType != null)
                        {
                            if (!IsSimpleType(_ArrayElemType))
                            {
                                fIsRef = true;
                            }
                        }
                    }
                    return fIsRef;
                }
            }

            public override string ToString()
            {
                return string.Format("Type={0} Sz={1}", _type.Name, _classOrArrayElemSize);
            }
        }
        static bool IsSimpleType(Type _type)
        {
            int nSize;
            return IsSimpleType(_type, out nSize);
        }

        static bool IsSimpleType(Type _type, out int nSize)
        {
            var fIsSimple = false;
            nSize = 4;
            if (_type.IsEnum)
            {
                var enumunderlyingtype = _type.GetFields()[0].FieldType;
                if (!IsSimpleType(enumunderlyingtype, out nSize))
                {
                    Debug.Assert(false);
                }
                fIsSimple = true;
            }
            else
            {
                switch (_type.Name)
                {
                    case "IntPtr":
                        nSize = IntPtr.Size;
                        fIsSimple = true;
                        break;
                    case "Int16":
                    case "UInt16":
                        nSize = 2;
                        fIsSimple = true;
                        break;
                    case "Int32":
                    case "UInt32":
                    case "Boolean":
                    case "Single":
                        fIsSimple = true;
                        break;
                    case "UInt64":
                    case "Int64":
                    case "Double":
                    case "DateTime":
                        nSize = 8;
                        fIsSimple = true;
                        break;
                }
            }
            return fIsSimple;
        }

        [DebuggerDisplay("{_ulFixupOffset}")]
        internal class AddDataFixup
        {
            public ulong _ulFixupOffset;
            public object _referredObject;
            public int _bstrByteLen;
            public IntPtr _bstrStr;
            public FieldInfo _fldInfo;
        }

        internal uint nRound = 1 << 3; // power of 2 >= 4

        public MHandle AddData(
            object objToAdd,
            Type objToAddType = null,
            FieldInfo fldInfoContainer = null,
            object objContainer = null,
            Dictionary<object, ulong> dictPriorObjs = null,
            int nRecursionLevel = 0
            )
        {
            Trace.WriteLine($"{nameof(AddData)}  {objToAdd} FldInfoCtr {fldInfoContainer?.FieldType.Name}   Recur {nRecursionLevel}");
            if (nRecursionLevel > 1000)
            {
                throw new InvalidOperationException("Recursion depth");
            }
            Debug.Assert(objToAdd != null || objToAddType != null, "AddData: must know type for null objs");
            uint dataSize = 0;
            ClassInfo classInfo = null;
            MemMapStream mapStream = null;

            if (objContainer == null) // if we're not recurring
            {
                VerifyStuff();
                if (_fCompactPending)
                {
                    _fCompactPending = false;
                    Compact();
                    //  VerifyStuff();
                }
            }
            if (objToAdd as AddDataFixup != null)
            {
                //When adding a non-null string itself
                var fixupObj = (AddDataFixup)objToAdd;
                Debug.Assert(fixupObj._referredObject == null, "fixupobj referred?");
                dataSize = (uint)fixupObj._bstrByteLen;
                mapStream = AllocSpace(dataSize, fIsRecurring: objContainer != null);
                NativeMethods.CopyMemory(mapStream._addrStart, fixupObj._bstrStr, (uint)fixupObj._bstrByteLen);
                Marshal.ZeroFreeBSTR(fixupObj._bstrStr);
            }
            else
            {
                bool fDidIt = false;
                if (fUseISerializable)
                {
                    if (objToAdd as ISerializable != null)
                    {
                        var b = new BinaryFormatter();
                        using (var mStream = new MemoryStream())
                        {
                            b.Serialize(mStream, objToAdd); //1st serialize to memstream to get size
                            dataSize = (uint)mStream.Length;
                            mapStream = AllocSpace(dataSize, fIsRecurring: objContainer != null);
                            var buf = mStream.GetBuffer();
                            mapStream.Write(buf, 0, (int)mapStream._mfloc.ulSize);
                        }
                        fDidIt = true;
                    }
                }
                var queueFixups = new Queue<AddDataFixup>();
                if (!fDidIt)
                {
                    if (objToAdd != null && objToAddType == null)
                    {
                        objToAddType = objToAdd.GetType();
                    }
                    classInfo = GetClassInfo(objToAddType, fldref: fldInfoContainer, ParentType: objContainer == null ? null : objContainer.GetType());
                    if (classInfo._ArrayElemType != null)
                    {
                        var tmpArray = objToAdd as Array;
                        if (classInfo.IsReferenceType) // an array of strings: we need the refs
                        {
                            dataSize = (uint)(_sizeofRef * tmpArray.Length);
                        }
                        else
                        {
                            dataSize = (uint)(tmpArray.Length * classInfo._classOrArrayElemSize);
                        }
                    }
                    else
                    {
                        if (objToAddType.Name == "String")
                        {
                            // a string is a ref followed by the string
                            // the len of the string is the len of the alloc - the size of the ref
                            //If null, the ref is null
                            if (objToAdd == null)
                            {
                                dataSize = _sizeofRef;
                            }
                            else
                            {
                                dataSize = (uint)(_sizeofRef + 2 * ((string)objToAdd).Length);
                            }
                        }
                        else
                        {
                            dataSize = (uint)classInfo._classOrArrayElemSize;
                        }
                    }
                    mapStream = AllocSpace(dataSize, fIsRecurring: objContainer != null);
                    if (dictPriorObjs == null)
                    {
                        dictPriorObjs = new Dictionary<object, ulong>();
                    }
                    if (objToAdd != null)
                    {
                        dictPriorObjs[objToAdd] = mapStream._mfloc.ulOffset;
                    }
                    if (classInfo._ArrayElemType != null)
                    {
                        var thearray = objToAdd as Array;

                        var clsinfoArrayType = GetClassInfo(classInfo._ArrayElemType);
                        if (clsinfoArrayType._simpleType != null && clsinfoArrayType._simpleType.Name != "String")
                        {
                            foreach (var item in thearray)
                            {
                                WriteTheType(item, item.GetType(), mapStream._addrStart, mapStream);
                                mapStream._addrStart += clsinfoArrayType._classOrArrayElemSize;
                            }
                        }
                        else
                        {
                            int ndx = 0;
                            foreach (var item in thearray)
                            {
                                if (item == null)
                                {
                                    WriteARef(mapStream._addrStart, 0); // null
                                }
                                else
                                {
                                    queueFixups.Enqueue(new AddDataFixup()
                                    {
                                        _referredObject = item,
                                        _ulFixupOffset = mapStream._mfloc.ulOffset + (uint)(ndx * _sizeofRef)
                                    }
                                    );
                                }
                                ndx++;
                                mapStream._addrStart += clsinfoArrayType._classOrArrayElemSize;
                            }
                        }
                    }
                    else
                    {// not an array
                        if (classInfo._type.Name == "String")
                        {
                            if (objToAdd == null)
                            {
                                WriteARef(mapStream._addrStart, 0);
                            }
                            else
                            {
                                /*
                                var enc = new UnicodeEncoding();
                                mapStream.Write(enc.GetBytes(str), 0, str.Length * 2);
                                //var len = str.Length;
                                //var bytestr = new byte[len * 2];
                                //var xxx = enc.GetBytes(str, 0, len, bytestr, 0);
                                //mapStream.Write(bytestr, 0, bytestr.Length);
                                /*/
                                //                        mapStream.Position += str.Length * 2;
                                WriteARef(mapStream._addrStart, (long)(mapStream._mfloc.ulOffset + _sizeofRef));
                                var str = (string)objToAdd;
                                var bstr = Marshal.StringToBSTR(str);
                                NativeMethods.CopyMemory(mapStream._addrStart + _sizeofRef, bstr, (uint)str.Length * 2);
                                Marshal.ZeroFreeBSTR(bstr);
                                //*/
                            }
                        }
                        else
                        {
                            if (classInfo._simpleType != null)
                            {
                                WriteTheType(objToAdd, classInfo._simpleType, mapStream._addrStart, mapStream);
                            }
                            else
                            {
                                if (classInfo._FieldInfo == null)
                                {
                                    int ndxObjDict = -1;
                                    var clsinfoParent = _dictClassInfo[objContainer.GetType()];
                                    if (clsinfoParent._objDict != null)
                                    {
                                        var lst = clsinfoParent._objDict[fldInfoContainer.Name];
                                        foreach (var objItem in lst)
                                        {
                                            ndxObjDict = 1; // reserve 
                                            if (object.ReferenceEquals(objItem, objToAdd))
                                            {
                                                break;
                                            }
                                            ndxObjDict++;
                                        }
                                        if (ndxObjDict == -1)
                                        {
                                            lst.Add(objToAdd);
                                            ndxObjDict = lst.Count; // reserve ndx = 0 for NULL
                                            //                                classInfo._objDict[classInfo._type.Name] = obj;
                                        }
                                    }

                                    WriteARef(mapStream._addrStart, (Int64)ndxObjDict);
                                }
                                else
                                {
                                    int ndx = 0;
                                    foreach (var fldInfo in classInfo._FieldInfo)
                                    {
                                        IntPtr addr = mapStream._addrStart + classInfo._offsets[ndx];
                                        var val = fldInfo.GetValue(objToAdd);
                                        if (fldInfo.FieldType.Name == "MapFileDictLazy`1")
                                        {
                                            dynamic dyn = val;

                                            if (val != null && dyn.IsValueCreated)
                                            {
                                                var theval = dyn.Value;
                                                var x = val.GetType().GetGenericArguments()[0].GetGenericArguments()[0].FullName; //"MapFileDictTest.TreeTests+RegDataLazy"
                                                var classIfnoLazy = _dictClassInfo.Where(e => e.Key.FullName == x).SingleOrDefault().Value;
                                                var castMeth = classIfnoLazy._type.GetMethod("Cast").MakeGenericMethod(classIfnoLazy._type);
                                                //var zz = castMeth.Invoke(null, new object[] { val });
                                                queueFixups.Enqueue(
                                                    new AddDataFixup()
                                                    {
                                                        _ulFixupOffset = mapStream._mfloc.ulOffset + (ulong)classInfo._offsets[ndx],
                                                        _referredObject = theval,
                                                        _fldInfo = fldInfo
                                                    });

                                            }
                                            else
                                            {
                                                WriteARef(addr, 0); // no value for Lazy
                                            }

                                        }
                                        else if (fldInfo.FieldType.Name == "String")
                                        {
                                            if (val == null)
                                            {
                                                WriteARef(addr, 0);
                                            }
                                            else
                                            {
                                                var str = (string)val;
                                                var bstr = Marshal.StringToBSTR(str);
                                                queueFixups.Enqueue(
                                                    new AddDataFixup()
                                                    {
                                                        _ulFixupOffset = mapStream._mfloc.ulOffset + (ulong)classInfo._offsets[ndx],
                                                        _bstrByteLen = str.Length * 2,
                                                        _bstrStr = bstr,
                                                        _fldInfo = fldInfo
                                                    }
                                                    );
                                            }
                                        }
                                        else
                                        {
                                            if (!WriteTheType(val, fldInfo.FieldType, addr, mapStream))
                                            {
                                                var referreddobj = fldInfo.GetValue(objToAdd);
                                                if (referreddobj != null)
                                                {
                                                    queueFixups.Enqueue(
                                                        new AddDataFixup()
                                                        {
                                                            _ulFixupOffset = mapStream._mfloc.ulOffset + (ulong)classInfo._offsets[ndx],
                                                            _referredObject = fldInfo.GetValue(objToAdd),
                                                            _fldInfo = fldInfo

                                                        });
                                                }
                                                else
                                                {
                                                    WriteARef(addr, 0); // null value
                                                }
                                                if (fldInfo.FieldType.IsAbstract)
                                                {
                                                    var hash = 0;
                                                    if (referreddobj != null)
                                                    {
                                                        var referredtype = referreddobj.GetType();
                                                        hash = referredtype.GetHashCode();
                                                        GetClassInfo(referredtype); // ensure it's in the dict
                                                    }
                                                    Marshal.WriteInt32(addr + _sizeofRef, hash);

                                                }
                                            }
                                        }
                                        ndx++;
                                    }
                                }
                            }
                        }
                    }
                }

                while (queueFixups.Count > 0)
                {
                    var fixup = queueFixups.Dequeue();
                    object objToFixup = fixup;
                    if (fixup._referredObject != null)
                    {
                        Debug.Assert(fixup._bstrStr == IntPtr.Zero, "fixpobj has bstr?");
                        objToFixup = fixup._referredObject;
                    }
                    else
                    {
                        Debug.Assert(fixup._bstrStr != IntPtr.Zero, "fixupobj has no bstr?");
                    }
                    // before we recur : be very careful: once we recur, the mapview could be different
                    if (fixup._referredObject != null && dictPriorObjs.ContainsKey(objToFixup) && false)
                    {
                        //throw new InvalidOperationException("aaa");
                        var fixupAddr = MapView(fixup._ulFixupOffset, _sizeofRef /*(uint)Marshal.SizeOf(locFixup)*/, _currentMapping);
                        var offsetOrigObj = dictPriorObjs[objToFixup];
                        WriteARef(fixupAddr, (long)offsetOrigObj);
                    }
                    else
                    {
                        mapStream._addrStart = IntPtr.Zero; // just to be sure, let's invalidate it
                        var locFixup = AddData(
                            objToFixup,
                            fldInfoContainer: fixup._fldInfo,
                            objContainer: objToAdd,
                            dictPriorObjs: dictPriorObjs,
                            nRecursionLevel: nRecursionLevel + 1
                            ); // recur
                        var fixupAddr = MapView(fixup._ulFixupOffset, _sizeofRef /*(uint)Marshal.SizeOf(locFixup)*/, _currentMapping);
                        WriteARef(fixupAddr, (long)locFixup._mapIndx);
                    }
                }
            }
            Debug.Assert(mapStream._mfloc.ulOffset < _stats._ulMapMemSize, "mfLoc.ulOffset < _stats._ulMapMemSize");

            if (mapStream._mfloc.ulOffset + mapStream._mfloc.ulSize > _stats._ulOffsetFreeMem)
            {
                throw new InvalidOperationException("Overflow mapStream._mfloc.ulOffset + mapStream._mfloc.ulSize >  _stats._ulOffsetFreeMem");
            }
            Debug.Assert(mapStream._mfloc.ulOffset + dataSize <= _stats._ulOffsetFreeMem, "mfLoc.ulOffset < _stats._ulOffsetFreeMem");

            _setCurAllocs[mapStream._mfloc.nIndex] = mapStream._mfloc;
            var mHandle = new MHandle() { _mapIndx = mapStream._mfloc.nIndex };
            return mHandle;
        }


        void WriteARef(IntPtr addr, long val)
        {
            Marshal.WriteInt64(addr, val);
        }

        private bool WriteTheType(object val, Type type, IntPtr addr, MemMapStream mapStream)
        {
            bool fHandled = true;
            if (type.IsEnum)
            {
                var underlyingtype = type.GetFields()[0].FieldType;
                fHandled = WriteTheType(val, underlyingtype, addr, mapStream);
            }
            else
            {
                Byte[] bytes = null;
                switch (type.Name)
                {
                    case "Int16":
                        bytes = BitConverter.GetBytes((Int16)val);
                        break;
                    case "UInt16":
                        bytes = BitConverter.GetBytes((UInt16)val);
                        break;
                    case "Int32":
                        Marshal.WriteInt32(addr, (int)val);
                        break;
                    case "IntPtr":
                        if (IntPtr.Size == 4)
                        {
                            Marshal.WriteInt32(addr, ((IntPtr)val).ToInt32());
                        }
                        else
                        {
                            Marshal.WriteInt64(addr, ((IntPtr)val).ToInt64());
                        }
                        break;
                    case "UInt32":
                        // there's no WriteUint :(
                        //   Marshal.WriteInt32(addr.MyAdd(_offsets[ndx]), val);
                        bytes = BitConverter.GetBytes((uint)val);
                        break;
                    case "UInt64":
                        bytes = BitConverter.GetBytes((UInt64)val);
                        break;
                    case "Int64":
                        Marshal.WriteInt64(addr, (Int64)val);
                        break;
                    case "Single":
                        bytes = BitConverter.GetBytes((Single)val);
                        break;
                    case "Double":
                        bytes = BitConverter.GetBytes((double)val);
                        break;
                    case "Boolean":
                        Marshal.WriteInt32(addr, (bool)val ? 1 : 0);
                        break;
                    //case "String":
                    //    var str = (string)val;
                    //    if (str == null)
                    //    {
                    //        WriteARef(addr, 0);
                    //    }
                    //    else
                    //    {
                    //        /*
                    //        var enc = new UnicodeEncoding();
                    //        mapStream.Write(enc.GetBytes(str), 0, str.Length * 2);
                    //        //var len = str.Length;
                    //        //var bytestr = new byte[len * 2];
                    //        //var xxx = enc.GetBytes(str, 0, len, bytestr, 0);
                    //        //mapStream.Write(bytestr, 0, bytestr.Length);
                    //        /*/
                    //        //                        mapStream.Position += str.Length * 2;
                    //        var bstr = Marshal.StringToBSTR(str);
                    //        NativeMethods.CopyMemory(addr, bstr, (uint)str.Length * 2);
                    //        Marshal.ZeroFreeBSTR(bstr);
                    //        //*/
                    //    }
                    //    break;
                    case "DateTime":
                        //Marshal.StructureToPtr(dt, addr, fDeleteOld: false);
                        Marshal.WriteInt64(addr, ((DateTime)val).Ticks);
                        break;
                    default:
                        fHandled = false;
                        break;
                }
                if (bytes != null)
                {
                    /*
                    mapStream.Position = addr.ToInt64();
                    mapStream.Write(bytes, 0, bytes.Length);
                    /*/

                    for (var i = 0; i < bytes.Length; i++)
                    {
                        Marshal.WriteByte(addr + i, bytes[i]);
                    }
                    //*/
                }
            }
            return fHandled;
        }

        internal uint RoundIt(uint dataSize)
        {
            var remainder = dataSize % 1; // nRound;
            if (remainder > 0)
            {
                dataSize += (nRound - remainder);
            }
            return dataSize;
        }

        [DebuggerDisplay("{_locReference.ToString()}")]
        private class GetDataFixup
        {
            public MapFileLocator _locReference;
            public FieldInfo _fieldInfoFixup;
            public Type _type;
            public Array _theArray;
            public int _arrayIndex;
        }

        public object GetData(MHandle mHandle, Type type)
        {
            var mfl = _setCurAllocs[mHandle._mapIndx];
            var obj = GetData(mfl, type);
            return obj;
        }

        internal object GetData(MapFileLocator loc, Type type, Object parentObj = null, FieldInfo fldInfoContainer = null)
        {
            _stats._nGetData++;
            object data = null;
            Debug.Assert(loc.ulOffset + loc.ulSize < _stats._ulMapMemSize, "locator out of range?");
            var addrStart = MapView(loc.ulOffset, loc.ulSize, _currentMapping);

            if (fUseISerializable)
            {
                var b = new BinaryFormatter();
                var mystream = new MemMapStream()
                {
                    _mfloc = loc,
                    _addrStart = addrStart
                };
                data = b.Deserialize(mystream);
            }
            else
            {
                Debug.Assert(type != null);
                var classInfo = _dictClassInfo[type];
                var queFixup = new Queue<GetDataFixup>();
                if (type.IsArray)
                {
                    var arrlen = loc.ulSize / classInfo._classOrArrayElemSize;
                    if (classInfo.IsReferenceType)
                    {
                        arrlen = loc.ulSize / _sizeofRef; // store refs, rather than values for strings
                    }
                    var arr = Array.CreateInstance(classInfo._ArrayElemType, arrlen);
                    data = arr;
                    var addr = addrStart;
                    if (classInfo.IsReferenceType)
                    {
                        for (int i = 0; i < arrlen; i++)
                        {
                            ulong mapIndex = (ulong)Marshal.ReadInt64(addr);
                            if (mapIndex == 0) // null value
                            {
                                arr.SetValue(null, i);
                            }
                            else
                            {
                                var refd = _setCurAllocs[mapIndex];
                                queFixup.Enqueue(new GetDataFixup()
                                {
                                    _locReference = refd,
                                    _theArray = arr,
                                    _type = classInfo._ArrayElemType,
                                    _arrayIndex = i
                                }
                                );
                            }
                            addr += _sizeofRef;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < arrlen; i++)
                        {
                            var val = ReadSimpleType(addr, classInfo._ArrayElemType);
                            arr.SetValue(val, i);
                            addr += classInfo._classOrArrayElemSize;
                        }
                    }
                }
                else // not array
                {
                    if (type.IsInterface) // {Name = "IEqualityComparer`1" FullName = "System.Collections.Generic.IEqualityComparer`1[[System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]"}
                    {
                        var ndx = (int)Marshal.ReadInt64(addrStart);
                        var parentClassInfo = _dictClassInfo[parentObj.GetType()]; // parentClassInfo	{Type=Dictionary`2 Sz=168}

                        if (ndx == 0) // null
                        {
                            data = null;
                        }
                        else
                        {
                            List<object> lst = null;
                            if (parentClassInfo._objDict.TryGetValue(fldInfoContainer.Name, out lst))
                            {
                                data = lst[ndx - 1];
                            }
                            //var obj = classInfo._objDict[ndx];
                            //if ( type.IsAssignableFrom(obj.GetType()))
                            //{
                            //    data = obj;
                            //}

                        }
                    }
                    else
                    {
                        if (type.Name == "MapFileDictLazy`1")
                        {
                            var refloc = Marshal.ReadInt64(addrStart);
                            if (refloc != 0) // not null
                            {
                                var x = type.GetGenericArguments()[0].GetGenericArguments()[0].FullName; //"MapFileDictTest.TreeTests+RegDataLazy"
                                var classIfnoLazy = _dictClassInfo.Where(e => e.Key.FullName == x).SingleOrDefault().Value;
                                var castMeth = classIfnoLazy._type.GetMethod("Cast").MakeGenericMethod(classIfnoLazy._type);
                                //var zz = castMeth.Invoke(null, new object[] { val });
                                dynamic lazyvalue = Activator.CreateInstance(classIfnoLazy._type);

                                lazyvalue._memMap = this;
                                //lazyvalue._mfl = new MapFileLocator() { ulOffset = addrStart, ulSize = 2 };

                            }
                            else
                            {
                                fldInfoContainer.SetValue(parentObj, null);

                            }
                        }
                        else if (type.Name == "String")
                        {
                            if (fldInfoContainer == null) // not contained: so it's a raw string
                            {
                                var refloc = Marshal.ReadInt64(addrStart);
                                if (refloc == 0) // null string
                                {
                                    data = null;
                                }
                                else
                                {
                                    var strlen = (int)(loc.ulSize - _sizeofRef) / 2;
                                    if (strlen == 0)
                                    {
                                        data = string.Empty;
                                    }
                                    else
                                    {
                                        data = Marshal.PtrToStringUni(addrStart + _sizeofRef, strlen);
                                    }
                                }
                            }
                            else
                            {
                                var strLen = loc.ulSize / 2;
                                if (strLen == 0)
                                {
                                    data = string.Empty;
                                }
                                else
                                {
                                    data = Marshal.PtrToStringUni(addrStart, (int)strLen);
                                }

                            }
                        }
                        else
                        {
                            //var ctors = type.GetConstructors();
                            //if (ctors.Length > 0 && ctors[0].GetParameters().Length > 0)
                            //{
                            //    data = Activator.CreateInstance(type, new object[] { "http://msn.com" });
                            //}
                            //else
                            //{
                            //}
                            data = Activator.CreateInstance(type);

                            if (classInfo._simpleType != null)
                            {
                                data = ReadSimpleType(addrStart, classInfo._simpleType);
                            }
                            else
                            {
                                int ndx = 0;
                                foreach (var fldInfoRaw in classInfo._FieldInfo)
                                {
                                    var addr = addrStart + classInfo._offsets[ndx];
                                    void DoGetFld(string TypeName, FieldInfo fldInfo)
                                    {
                                        switch (TypeName)
                                        {
                                            case "UInt32":
                                                {
                                                    var val = (UInt32)Marshal.ReadInt32(addr);
                                                    fldInfo.SetValue(data, val);
                                                }
                                                break;
                                            case "Int32":
                                                {
                                                    var val = Marshal.ReadInt32(addr);
                                                    fldInfo.SetValue(data, val);
                                                }
                                                break;
                                            case "UInt64":
                                                {
                                                    var val = (UInt64)Marshal.ReadInt32(addr);
                                                    fldInfo.SetValue(data, val);
                                                }
                                                break;
                                            case "Int64":
                                                {
                                                    var val = (Int64)Marshal.ReadInt64(addr);
                                                    fldInfo.SetValue(data, val);
                                                }
                                                break;
                                            case "Single":
                                                unsafe
                                                {
                                                    Single val = 0;
                                                    byte* pBuff = (byte*)addr.ToPointer();
                                                    val = *(Single*)pBuff;
                                                    fldInfo.SetValue(data, val);
                                                }
                                                break;
                                            case "Double":
                                                unsafe
                                                {
                                                    Double val = 0;
                                                    byte* pBuff = (byte*)addr.ToPointer();
                                                    val = *(Double*)pBuff;
                                                    fldInfo.SetValue(data, val);
                                                }
                                                break;
                                            case "Boolean":
                                                {
                                                    var val = Marshal.ReadInt32(addr);
                                                    fldInfo.SetValue(data, val == 0 ? false : true);
                                                }
                                                break;
                                            case "DateTime":
                                                {
                                                    var val = Marshal.ReadInt64(addr);
                                                    var dt = new DateTime(val);
                                                    fldInfo.SetValue(data, dt);
                                                }
                                                break;
                                            default: //including strings
                                                {
                                                    ulong mapIndex = (ulong)Marshal.ReadInt64(addr);
                                                    if (mapIndex == 0) // value is NULL
                                                    {
                                                        fldInfo.SetValue(data, null);
                                                    }
                                                    else
                                                    {
                                                        var locRefObj = _setCurAllocs[mapIndex];
                                                        var fixup = new GetDataFixup()
                                                        {
                                                            _locReference = locRefObj,
                                                            _fieldInfoFixup = fldInfo
                                                        };
                                                        if (fldInfo.FieldType.IsAbstract && !fldInfo.FieldType.IsInterface)
                                                        {
                                                            int hash = Marshal.ReadInt32(addr + _sizeofRef);
                                                            fixup._type = _dictClassInfo.Values.Where(v => v._type.GetHashCode() == hash).Single()._type;
                                                        }
                                                        queFixup.Enqueue(fixup);
                                                    }
                                                }
                                                break;
                                        }

                                    }
                                    if (fldInfoRaw.FieldType.IsEnum)
                                    {
                                        var underlyingfldType = fldInfoRaw.FieldType.GetFields()[0];
                                        DoGetFld(underlyingfldType.FieldType.Name, fldInfoRaw);
                                    }
                                    else
                                    {
                                        DoGetFld(fldInfoRaw.FieldType.Name, fldInfoRaw);
                                    }

                                    ndx++;
                                }
                            }
                        }
                    }
                }
                while (queFixup.Count > 0)
                {
                    GetDataFixup fixup = queFixup.Dequeue();
                    var typeToUse = fixup._type;
                    if (typeToUse == null)
                    {
                        typeToUse = fixup._fieldInfoFixup.FieldType;
                    }
                    var val = GetData(fixup._locReference, typeToUse, parentObj: data, fldInfoContainer: fixup._fieldInfoFixup); // recur
                    if (fixup._fieldInfoFixup == null)
                    {
                        fixup._theArray.SetValue(val, fixup._arrayIndex);
                    }
                    else
                    {
                        fixup._fieldInfoFixup.SetValue(data, val);
                    }
                }
            }
            return data;
        }

        object ReadSimpleType(IntPtr addr, Type type)
        {
            object val = null;
            if (type.IsEnum)
            {
                if (IsSimpleType(type, out var nSize))
                {
                    switch (nSize)
                    {
                        case 8:
                            val = Marshal.ReadInt64(addr);
                            break;
                        case 4:
                            val = Marshal.ReadInt32(addr);
                            break;
                        case 2:
                            val = Marshal.ReadInt16(addr);
                            break;
                        case 1:
                            val = Marshal.ReadByte(addr);
                            break;
                    }
                }

            }
            else
            {
                switch (type.Name)
                {
                    case "Int16":
                        val = (Int16)Marshal.ReadInt16(addr);
                        break;
                    case "UInt16":
                        val = (UInt16)Marshal.ReadInt16(addr);
                        break;
                    case "UInt32":
                        val = (UInt32)Marshal.ReadInt32(addr);
                        break;
                    case "Int32":
                        val = Marshal.ReadInt32(addr);
                        break;
                    case "IntPtr":
                        if (IntPtr.Size == 4)
                        {
                            val = Marshal.ReadInt32(addr);
                            val = new IntPtr((int)val);
                        }
                        else
                        {
                            val = Marshal.ReadInt64(addr);
                            val = new IntPtr((Int64)val);
                        }
                        break;
                    case "UInt64":
                        val = (UInt64)Marshal.ReadInt32(addr);
                        break;
                    case "Int64":
                        val = (Int64)Marshal.ReadInt64(addr);
                        break;
                    case "Boolean":
                        val = Marshal.ReadInt32(addr);
                        break;
                    case "Double":
                        unsafe
                        {
                            byte* pBytes = (byte*)addr.ToPointer();
                            val = *(double*)pBytes;
                        }
                        break;
                    case "Single":
                        unsafe
                        {
                            byte* pBuff = (byte*)addr.ToPointer();
                            val = *(Single*)pBuff;
                        }
                        break;
                    case "DateTime":
                        var ticks = (Int64)Marshal.ReadInt64(addr);
                        val = new DateTime(ticks);
                        break;
                }
            }
            return val;
        }

        public bool RemoveData(MHandle mHandle, Type type, bool fIsRecurring = false)
        {
            MapFileLocator mfl;
            bool fDidRemove = false;
            if (_setCurAllocs.TryGetValue(mHandle._mapIndx, out mfl) && !mfl.fIsFree)
            {
                Debug.Assert(mfl.ulOffset < _stats._ulMapMemSize, "mHandle._locator.ulOffset < _stats._ulMapMemSize");
                mfl.fIsFree = true;
                if (!fUseISerializable)
                {
                    // see if there are any nested types
                    var classInfo = _dictClassInfo[type];
                    int ndx = 0;
                    Action<Type, IntPtr> removeRef = (fldtype, addr) =>
                    {
                        var mapIndex = (ulong)Marshal.ReadInt64(addr);
                        if (mapIndex != 0) // null data
                        {
                            var mhToRemove = new MHandle(mapIndex);
                            var resStr = RemoveData(mhToRemove, fldtype, fIsRecurring: true); // recur
                            if (!resStr)
                            {
                                throw new InvalidOperationException("Failed to remove strg " + mhToRemove.ToString());
                            }
                        }
                    };
                    // want to avoid mapping if possible
                    // but when we recur, we could be invalidating the mapview
                    if (classInfo._FieldInfo != null)
                    {
                        foreach (var fldInfo in classInfo._FieldInfo)
                        {
                            if (!IsSimpleType(fldInfo.FieldType))
                            {
                                IntPtr addrStart = MapView(mfl.ulOffset, mfl.ulSize, _currentMapping);
                                var addr = addrStart + classInfo._offsets[ndx];
                                removeRef(fldInfo.FieldType, addr);
                            }
                            ndx++;
                        }
                    }
                    else
                    {
                        if (type.IsArray)
                        {
                            var arrlen = mfl.ulSize / classInfo._classOrArrayElemSize;
                            if (classInfo.IsReferenceType)
                            {
                                arrlen = mfl.ulSize / _sizeofRef; // store refs, rather than values for strings
                            }
                            if (classInfo._ArrayElemType != null && !IsSimpleType(classInfo._ArrayElemType))
                            {
                                for (int i = 0; i < arrlen; i++)
                                {
                                    IntPtr addrStart = MapView(mfl.ulOffset, mfl.ulSize, _currentMapping) + i * _sizeofRef;
                                    removeRef(classInfo._ArrayElemType, addrStart);
                                }
                            }
                        }
                    }
                }
                List<MHandle> lstFree;
                var dataSize = RoundIt(mfl.ulSize);
                if (!_freeLists.TryGetValue(dataSize, out lstFree))
                {
                    lstFree = _freeLists[dataSize] = new List<MHandle>();
                }
                lstFree.Add(mHandle);

                _stats._nBytesFreedTot += dataSize;
                Debug.Assert(_stats._nBytesFreedTot < _stats._ulMapMemSize, "freeSize <= _stats._ulMapMemSize ?");
                if (!fIsRecurring && _stats._nBytesFreedTot > AllocationGranularity * 4)
                {
                    _fCompactPending = true;
                }

                _stats._nCurBytesAlloc -= dataSize;
                fDidRemove = true;
                _stats._nRemoved += dataSize;
            }
            else
            {
                throw new InvalidOperationException("Double free ? " + mHandle.ToString());
            }
            return fDidRemove;
        }
    }
    /// <summary>
    /// clients use this: a handle to the memory
    /// </summary>
    public struct MHandle : IComparable
    {
        internal ulong _mapIndx;
        internal MHandle(ulong mapIndx)
        {
            _mapIndx = mapIndx;
        }
        public static bool operator ==(MHandle l1, MHandle l2)
        {
            return l1._mapIndx == l2._mapIndx;
        }
        public static bool operator !=(MHandle l1, MHandle l2)
        {
            return !(l1 == l2);
        }
        public override bool Equals(object obj)
        {
            if (!(obj is MHandle) || obj == null)
            {
                return false;
            }
            var x = (MHandle)obj;
            return this == x;
        }
        public override int GetHashCode()
        {
            return _mapIndx.GetHashCode();
        }
        public override string ToString()
        {
            return _mapIndx.ToString();
        }

        public int CompareTo(object obj)
        {
            int res = 0;
            var other = (MHandle)obj;
            if (this._mapIndx != other._mapIndx)
            {
                if (this._mapIndx < other._mapIndx)
                {
                    res = -1;
                }
                else
                {
                    res = 1;
                }
            }
            return res;

        }
    }

    [DebuggerDisplay("{ToString()}")]
    internal class MapFileLocator
    {
        public ulong ulOffset; // target proc must be same bitness, but using a file, this could be > 4gig 
        public uint ulSize;
        public ulong nIndex;
        public bool fIsFree
        {
            get
            {
                return nIndex == ulong.MaxValue;
            }
            set
            {
                if (value)
                {
                    nIndex = ulong.MaxValue;
                }
                else
                {
                    throw new InvalidOperationException("MapFileLocator: don't set free to false: just change index to indicate used");
                }
            }
        }
        public override string ToString()
        {
            return string.Format("Off{0:x8} Size{1} ndx {2} Free{3}", ulOffset, ulSize, nIndex, fIsFree);
        }
    }

    public class MapFileDictLazy<T> : Lazy<T>
    {
        internal MapFileLocator _mfl;
        internal MemMap _memMap;
        public T GetValue
        {
            get
            {
                T val = default(T);
                if (_mfl == null)
                {
                    if (!IsValueCreated)
                    {
                        val = default(T);
                    }
                    else
                    {
                        val = this.Value;
                    }
                }
                else
                {
                    val = (T)_memMap.GetData(_mfl, typeof(T));
                }
                return val;
            }
        }
        //public override T Value
        //{
        //    get
        //    {
        //        return default(T);
        //    }
        //}

    }

}
