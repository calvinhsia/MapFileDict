using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MapFileDict
{

    [DebuggerDisplay("Count = {Count}")]
    public class MapFileDict<TKey, TValue> : IDisposable, IDictionary<TKey, TValue>// where TValue : new()
    {
        public MemMap _MemMap;
        private IEqualityComparer<TKey> _comparer;
        Dictionary<TKey, MHandle?> __dictInternal; // map from Key to a simple MHandle index
        Dictionary<TKey, MHandle?> _dictInternal // property
        {
            get
            {
                if (__dictInternal == null)
                {
                    __dictInternal = new Dictionary<TKey, MHandle?>(_comparer);
                }
                return __dictInternal;
            }
        }
        public MapFileDict(
            IEqualityComparer<TKey> comparer = null,
            ulong ulInitialSize = 0,
            MapMemTypes mapfileType = MapMemTypes.MapMemTypePageFile,
            ulong ulGrowDynamicallyThreshold = 0,
            MemMap memMapToUse = null,
            uint uiViewSize = MemMap.AllocationGranularity
        )
        {
            _MemMap = memMapToUse;
            _comparer = comparer ?? EqualityComparer<TKey>.Default;
            if (_MemMap == null)
            {
                _MemMap = new MemMap(
                    ulInitialSize,
                    mapfileType,
                    ulGrowDynamicallyThreshold,
                    uiViewSize
                    );
            }

        }
        public MapFileDict(IDictionary<TKey, TValue> dict)
            : this()
        {
            foreach (var kvp in dict)
            {
                this[kvp.Key] = kvp.Value;
            }
        }

        public void Add(TKey key, TValue value)
        {
            var loc = _MemMap.AddData(value, typeof(TValue));
            _dictInternal.Add(key, loc);
        }

        internal void VerifyNoLeaks()
        {
            foreach (var l in _dictInternal.Values)
            {
                _MemMap.RemoveData(l.Value, typeof(TValue));
            }
            if (_MemMap._stats._nCurBytesAlloc != 0)
            {
                throw new InvalidOperationException("leftovers " + _MemMap._stats.ToString());
            }
        }

        public bool ContainsKey(TKey key)
        {
            return _dictInternal.ContainsKey(key);
        }

        public ICollection<TKey> Keys
        {
            get
            {
                return _dictInternal.Keys;
            }
        }

        public bool Remove(TKey key)
        {
            bool fDidRemove = false;
            MHandle? loc;
            if (_dictInternal.TryGetValue(key, out loc))
            {
                fDidRemove = _MemMap.RemoveData(loc.Value, typeof(TValue));
                _MemMap.VerifyStuff();
                _dictInternal.Remove(key);
            }
            return fDidRemove;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            MHandle? loc = null;
            value = default(TValue);
            bool res = _dictInternal.TryGetValue(key, out loc);
            if (res)
            {
                if (loc.HasValue)
                {
                    value = (TValue)_MemMap.GetData(loc.Value, typeof(TValue));
                }
            }
            return res;

        }

        public ICollection<TValue> Values
        {
            get
            {
                return new ValueCollection(this);
            }
        }
        [Conditional("DEBUG")]
        internal void verify()
        {
            //foreach (var mh in _dictInternal.Values)
            //{
            //    Debug.Assert(_MemMap._setCurAllocs.ContainsKey(mh._mapIndx),"doesn't contain key");
            //}
        }
        public TValue this[TKey key]
        {
            get
            {
                MHandle? loc = _dictInternal[key]; // if this throws (Key not found)..then so be it
                TValue val = default(TValue);
                if (loc.HasValue)
                {
                    val = (TValue)_MemMap.GetData(loc.Value, typeof(TValue));
                }
                return val;
            }
            set
            {
                MHandle? loc;
                if (_dictInternal.TryGetValue(key, out loc))
                {
                    if (loc.HasValue)
                    {
                        //already has data, remove prior value
                        var res = _MemMap.RemoveData(loc.Value, typeof(TValue));
                        Debug.Assert(res, "removeData failed?");
                    }
                }
                if (value == null)
                {
                    _dictInternal[key] = null;
                }
                else
                {
                    var Loc = _MemMap.AddData(value, typeof(TValue));
                    _dictInternal[key] = Loc;
                }
                verify();
            }
        }
        public void Dispose()
        {
            _MemMap.Dispose();
            GC.SuppressFinalize(this);
        }

        ~MapFileDict()
        {
            Dispose();
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            this[item.Key] = item.Value;
        }

        public void Clear()
        {
            _MemMap.Clear();
            if (_dictInternal != null)
            {
                _dictInternal.Clear();
            }
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return ContainsKey(item.Key);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            int ndx = arrayIndex;
            foreach (var entry in _dictInternal)
            {
                var x = new KeyValuePair<TKey, TValue>(entry.Key, this[entry.Key]);
                array[ndx++] = x;
                if (ndx == array.Length)
                {
                    break;
                }
            }
        }

        public int Count
        {
            get
            {
                var cnt = 0;
                if (_dictInternal != null)
                {
                    cnt = _dictInternal.Count;
                }
                return cnt;
            }
        }

        public bool IsReadOnly
        {
            get { throw new NotImplementedException(); }
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            throw new NotImplementedException();
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return new Enumerator<KeyValuePair<TKey, TValue>>(this);
        }


        //\\cpvsbuild\drops\dev12\VSPro_Platform\raw\current\sources\ndp\clr\src\BCL\System\Collections\Generic\Dictionary.cs
        public class ValueCollection : ICollection<TValue>, System.Collections.ICollection
        {
            private MapFileDict<TKey, TValue> _mapFileDict;

            public ValueCollection(MapFileDict<TKey, TValue> mapFileDict)
            {
                this._mapFileDict = mapFileDict;
            }
            public void Add(TValue item)
            {
                throw new NotImplementedException();
            }

            public void Clear()
            {
                throw new NotImplementedException();
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
                get { return _mapFileDict.Count; }
            }

            public bool IsReadOnly
            {
                get { return true; }
            }

            public bool Remove(TValue item)
            {
                throw new NotImplementedException();
            }

            public IEnumerator<TValue> GetEnumerator()
            {
                return new Enumerator(_mapFileDict);
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                throw new NotImplementedException();
            }

            public void CopyTo(Array array, int index)
            {
                throw new NotImplementedException();
            }

            public bool IsSynchronized
            {
                get { throw new NotImplementedException(); }
            }

            public object SyncRoot
            {
                get { throw new NotImplementedException(); }
            }
        };
        public struct Enumerator<kvp> : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private MapFileDict<TKey, TValue> _mapFileDict;
            private TValue _curvalue;
            private int _index;
            Dictionary<TKey, MHandle?>.Enumerator _enumerator;
            public Enumerator(MapFileDict<TKey, TValue> mapFileDict)
            {
                this._mapFileDict = mapFileDict;
                _enumerator = _mapFileDict.__dictInternal.GetEnumerator();
                _curvalue = default(TValue);
                _index = 0;
            }

            public KeyValuePair<TKey, TValue> Current
            {
                get
                {
                    if (_index >= 0 && _index < _mapFileDict.Count)
                    {
                        var va = new KeyValuePair<TKey, TValue>(_enumerator.Current.Key, _curvalue);
                        return va;
                    }
                    throw new NotImplementedException();
                }
            }

            public void Dispose()
            {
            }

            object System.Collections.IEnumerator.Current
            {
                get { return _curvalue; }
            }

            public bool MoveNext()
            {
                var bMoveNext = _enumerator.MoveNext();
                if (bMoveNext)
                {
                    var mh = _enumerator.Current.Value.Value;
                    _curvalue = (TValue)_mapFileDict._MemMap.GetData(mh, typeof(TValue));
                }
                return bMoveNext;
            }

            public void Reset()
            {
                _curvalue = default(TValue);
                _index = 0;
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            var en = new Enumerator(this);
            return en;
        }


        public struct Enumerator : IEnumerator<TValue>, System.Collections.IEnumerator
        {
            private MapFileDict<TKey, TValue> _mapFileDict;
            int _index;
            TValue _curVal;
            Dictionary<TKey, MHandle?>.Enumerator _enumerator;
            public Enumerator(MapFileDict<TKey, TValue> mapFileDict)
            {
                this._mapFileDict = mapFileDict;
                _index = 0;
                _curVal = default(TValue);
                this._enumerator = mapFileDict._dictInternal.GetEnumerator();
            }

            public void Dispose()
            {
                _mapFileDict = null;
            }

            public TValue Current
            {
                get { return _curVal; }
            }
            object System.Collections.IEnumerator.Current
            {
                get
                {
                    return _curVal;
                }
            }

            public bool MoveNext()
            {
                if (_enumerator.MoveNext())
                {
                    _curVal = (TValue)_mapFileDict._MemMap.GetData(_enumerator.Current.Value.Value, typeof(TValue));
                    return true;
                }
                _index = _mapFileDict.Count + 1;
                _curVal = default(TValue);
                return false;
            }

            public void Reset()
            {
                _enumerator = _mapFileDict.__dictInternal.GetEnumerator();
                _index = 0;
                _curVal = default(TValue);
            }
        }

    }
}


