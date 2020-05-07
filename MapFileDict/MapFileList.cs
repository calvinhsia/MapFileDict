using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapFileDict
{
    [DebuggerDisplay("Count = {Count}")]
    public class MapFileList<TValue> : IDisposable, IList<TValue>
    {
        public MemMap _MemMap;
        List<MHandle> _listInternal;

        public MapFileList(
            ulong ulInitialSize = 0,
            MapMemTypes mapfileType = MapMemTypes.MapMemTypePageFile,
            ulong ulGrowDynamicallyThreshold = 0,
            MemMap memMapToUse = null,
            uint uiViewSize = MemMap.AllocationGranularity
            )
        {
            _MemMap = memMapToUse;
            if (_MemMap == null)
            {
                _MemMap = new MemMap(
                    ulInitialSize, 
                    mapfileType, 
                    ulGrowDynamicallyThreshold,
                    uiViewSize
                    );
            }
            _listInternal = new List<MHandle>();
        }
        public void Dispose()
        {
            _MemMap.Dispose();
            GC.SuppressFinalize(this);
        }
        ~MapFileList()
        {
            Dispose();
        }

        internal void VerifyNoLeaks()
        {
            foreach (var l in _listInternal)
            {
                _MemMap.RemoveData(l, typeof(TValue));
            }
            if (_MemMap._stats._nCurBytesAlloc != 0)
            {
                throw new InvalidOperationException("leftovers " + _MemMap._stats.ToString());
            }
        }

        public int IndexOf(TValue item)
        {
            throw new NotImplementedException();
        }

        public void Insert(int index, TValue item)
        {
            var loc = _MemMap.AddData(item, typeof(TValue));
            _listInternal.Insert(index, loc);
        }

        public void RemoveAt(int index)
        {
            var loc = _listInternal[index];
            _MemMap.RemoveData(loc, typeof(TValue));
            _listInternal.RemoveAt(index);
        }

        public TValue this[int index]
        {
            get
            {
                var mHandle = _listInternal[index];
                TValue val = (TValue)_MemMap.GetData(mHandle, typeof(TValue));
                return val;
            }
            set
            {
                MHandle loc = _listInternal[index];
                _MemMap.RemoveData(loc, typeof(TValue));

                loc = _MemMap.AddData(value, typeof(TValue));
                _listInternal[index] = loc;
            }
        }

        public void Add(TValue item)
        {
            var mHandle = _MemMap.AddData(item, typeof(TValue));
            _listInternal.Add(mHandle);
        }

        public void Clear()
        {
            _MemMap.Clear();
            _listInternal.Clear();
        }

        public bool Contains(TValue item)
        {
            throw new NotImplementedException();
        }

        public void CopyTo(TValue[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }

        public int Count
        {
            get { return _listInternal.Count; }
        }

        public bool IsReadOnly
        {
            get { throw new NotImplementedException(); }
        }

        public bool Remove(TValue item)
        {
            throw new NotImplementedException();
        }

        public IEnumerator<TValue> GetEnumerator()
        {
            throw new NotImplementedException();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            throw new NotImplementedException();
        }
    }
}
