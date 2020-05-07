using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MapFileDict;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MapFileDictTest
{
    [TestClass]
    public class TreeTests
    {
        public const string regroot = @"Software\Microsoft\VisualStudio\";
        public class RegDataMHandle : IComparable
        {
            public static int _cnt = 0;
            public static MemMap _MemMap;
            public string _name;
            public List<MHandle> _childNodes;
            public virtual int GetCount()
            {
                return _childNodes == null ? 0 : _childNodes.Count;
            }
            //            public Lazy<List<RegData>> _lazyChildNodes;
            public RegDataMHandle()
            {
                _cnt++;
            }
            public override string ToString()
            {
                return string.Format("{0} ({1})", _name, GetCount());
            }

            public void Fillit(int nLevel = 0)
            {
                if (nLevel < 22)
                {
                    using (var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_name))
                    {
                        var subkeys = reg.GetSubKeyNames();
                        if (subkeys.Length > 0)
                        {
                            _childNodes = new List<MHandle>();
                            foreach (var key in reg.GetSubKeyNames())
                            {
                                var newName = _name + @"\" + key;
                                var newnode = new RegData()
                                {
                                    _name = newName
                                };
                                _childNodes.Add(_MemMap.AddData(newnode));
                                newnode.Fillit(nLevel + 1);
                            }
                        }
                    }
                }
            }

            public int CompareTo(object obj)
            {
                var xr = this;
                var yr = obj as RegDataMHandle;
                if (xr == null || yr == null)
                {
                    return -1;
                }
                var tmp = xr._name.CompareTo(yr._name);
                if (tmp == 0)
                {
                    if (xr._childNodes != null || yr._childNodes != null)
                    {
                        if (xr._childNodes == null || yr._childNodes == null)
                        {
                            return -1;
                        }
                        if (xr._childNodes.Count != yr._childNodes.Count)
                        {
                            return -1;
                        }
                        for (int i = 0; i < xr._childNodes.Count; i++)
                        {
                            tmp = xr._childNodes[i].CompareTo(yr._childNodes[i]);
                            if (tmp != 0)
                            {
                                return tmp;
                            }
                        }
                    }
                }
                return tmp;
            }
        }
        [TestMethod]
        public void RegMHandle()
        {
            //var x = new Lazy<int>(() => 2);
            //var tt = x.GetType();
            //dynamic nn= Activator.CreateInstance(tt);

            //var vv = nn.Value;
            //Assert.AreEqual(vv, 2);

            using (var mmap = new MemMap())
            {
                RegDataMHandle._MemMap = mmap;
                var reg = new RegDataMHandle()
                {
                    _name = regroot
                };
                reg.Fillit();
                var mh = mmap.AddData(reg);
                var ret = (RegDataMHandle)mmap.GetData(mh, reg.GetType());
                foreach (var m in ret._childNodes)
                {
                    var ch = mmap.GetData(m, reg.GetType());
                }
                Assert.IsTrue(reg.CompareTo(ret) == 0, "not eq cnt = {0}", RegDataMHandle._cnt);

            }

        }

        public class RegData : IComparable
        {
            public static int _cnt = 0;
            public string _name;
            public List<RegData> _childNodes;
            public virtual int GetCount()
            {
                return _childNodes == null ? 0 : _childNodes.Count;
            }
            //            public Lazy<List<RegData>> _lazyChildNodes;
            public RegData()
            {
                _cnt++;
            }
            public override string ToString()
            {
                return string.Format("{0} ({1})", _name, GetCount());
            }

            public void Fillit(int nLevel = 0)
            {
                if (nLevel < 2)
                {
                    using (var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_name))
                    {
                        var subkeys = reg.GetSubKeyNames();
                        if (subkeys.Length > 0)
                        {
                            _childNodes = new List<RegData>(subkeys.Length);
                            foreach (var key in reg.GetSubKeyNames())
                            {
                                var newName = _name + @"\" + key;
                                var newnode = new RegData()
                                {
                                    _name = newName
                                };
                                _childNodes.Add(newnode);
                                newnode.Fillit(nLevel + 1);
                            }
                        }
                    }
                }
            }

            public int CompareTo(object obj)
            {
                var xr = this;
                var yr = obj as RegData;
                if (xr == null || yr == null)
                {
                    return -1;
                }
                var tmp = xr._name.CompareTo(yr._name);
                if (tmp == 0)
                {
                    if (xr._childNodes != null || yr._childNodes != null)
                    {
                        if (xr._childNodes == null || yr._childNodes == null)
                        {
                            return -1;
                        }
                        if (xr._childNodes.Count != yr._childNodes.Count)
                        {
                            return -1;
                        }
                        for (int i = 0; i < xr._childNodes.Count; i++)
                        {
                            tmp = xr._childNodes[i].CompareTo(yr._childNodes[i]);
                            if (tmp != 0)
                            {
                                return tmp;
                            }
                        }
                    }
                }
                return tmp;
            }
        }

        public class RegDataLazy : IComparable
        {
            public static int _cnt = 0;
            public string _name;
            public MapFileDictLazy<List<RegDataLazy>> _childNodes;
            public RegDataLazy()
            {
                _cnt++;
            }
            public virtual int GetCount()
            {
                return _childNodes == null ? 0 : _childNodes.Value.Count;
            }
            public override string ToString()
            {
                return string.Format("{0} ({1})", _name, GetCount());
            }
            public static T Cast<T>(object o)
            {
                return (T)o;
            }

            public void Fillit(int nLevel = 0)
            {
                if (nLevel < 1)
                {
                    using (var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_name))
                    {
                        var subkeys = reg.GetSubKeyNames();
                        if (subkeys.Length > 0)
                        {
                            _childNodes = new MapFileDictLazy<List<RegDataLazy>>();
                            foreach (var key in reg.GetSubKeyNames())
                            {
                                var newName = _name + @"\" + key;
                                var newnode = new RegDataLazy()
                                {
                                    _name = newName
                                };
                                _childNodes.Value.Add(newnode);
                                newnode.Fillit(nLevel + 1);
                            }
                        }
                    }
                }
            }

            public int CompareTo(object obj)
            {
                var xr = this;
                var yr = obj as RegDataLazy;
                if (xr == null || yr == null)
                {
                    return -1;
                }
                var tmp = xr._name.CompareTo(yr._name);
                if (tmp == 0)
                {
                    if (xr._childNodes == null && yr._childNodes == null)
                    {
                        return 0;
                    }
                    if (xr._childNodes == null || yr._childNodes == null)
                    {
                        return -1;
                    }
                    if (xr._childNodes.IsValueCreated || yr._childNodes.IsValueCreated)
                    {
                        if (xr._childNodes.Value.Count != yr._childNodes.Value.Count)
                        {
                            return -1;
                        }
                        for (int i = 0; i < xr._childNodes.Value.Count; i++)
                        {
                            tmp = xr._childNodes.Value[i].CompareTo(yr._childNodes.Value[i]);
                            if (tmp != 0)
                            {
                                return tmp;
                            }
                        }
                    }
                }
                return tmp;
            }
        }

        [TestMethod]
        [Ignore]
        public void DelegateTest()
        {
            var dummy = 0;
            var x = new Action(() =>
            {
                dummy = 1;
            });



            using (var mfd = new MemMap())
            {
                var loc = mfd.AddData(x);
                var ret = (Action)mfd.GetData(loc, x.GetType());
                ret.Invoke();
                Assert.AreEqual(dummy, 1);


                //                Assert.IsTrue(ret.CompareTo(root) == 0, "not equal!");
            }
        }


        [TestMethod]
        [Ignore] // stackoverflow
        public void LazyTest()
        {
            var x = new Lazy<int>(() => 2);


            using (var mfd = new MapFileList<Lazy<int>>())
            {

                mfd.Add(x);
                var ret = mfd[0];
                var res = ret.Value;
                Assert.IsTrue(res == 2);

                //                Assert.IsTrue(ret.CompareTo(root) == 0, "not equal!");
            }
            Assert.IsTrue(false, "Count = {0}", RegDataLazy._cnt);
        }



        [TestMethod]
        public void TreeTest()
        {
            var root = new RegData()
            {
                _name = regroot
            };
            root.Fillit();
            //            Assert.IsTrue(RegData._cnt > 350, "count = {0}", RegData._cnt);

            using (var mfd = new MapFileDict<int, RegData>())
            {
                mfd[1] = root;
                var ret = mfd[1];
                Assert.IsTrue(ret.CompareTo(root) == 0, "not equal!");
            }
//            Assert.IsTrue(false, "Count = {0}", RegData._cnt);
        }

        [TestMethod]
        [Ignore]
        public void TreeTestLazy()
        {
            var root = new RegDataLazy()
            {
                _name = regroot
            };
            root.Fillit();
            //            Assert.IsTrue(RegData._cnt > 350, "count = {0}", RegData._cnt);


            var zz = (RegDataLazy)Activator.CreateInstance(root.GetType());



            using (var mfd = new MapFileDict<int, RegDataLazy>())
            {
                mfd[1] = root;
                var ret = mfd[1];
                Assert.IsTrue(ret.CompareTo(root) == 0, "not equal! #items= {0}", RegDataLazy._cnt);
            }
            Assert.IsTrue(false, "Count = {0}", RegDataLazy._cnt);
        }

        [TestMethod]
        public void ListTest()
        {
            RegData._cnt = 0;
            var root = new RegData()
            {
                _name = @"foo"
            };
            var lst = new List<RegData>();
            lst.Add(root);
            //            root._name = "bar";
            //for (int i = 0; i < 100; i++)
            //{
            //    lst.Add(new RegData() { _name = "foo" });
            //}
            //lst.Add(new RegData() { _name = "foo" });
            lst.Add(root);

            using (var mfd = new MapFileDict<int, List<RegData>>())
            {
                mfd[1] = lst;
                var ret = mfd[1];

                Assert.IsTrue(ret[0].CompareTo(root) == 0);

            }
            Assert.IsTrue(RegData._cnt == 3, "count = {0}", RegData._cnt);

        }
    }
}
