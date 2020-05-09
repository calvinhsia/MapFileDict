using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.InteropServices;
using MapFileDict;
using System.Collections.Generic;

namespace MapFileDictTest
{
    [TestClass]
    public class UnitTest1
    {
        public class baseDataClass
        {
            public long basenum;
        }
        public class Derived : DataClass
        {
        }

        //[Serializable]
        public class DataClass : baseDataClass
        {
            public static string xstatic;
            public int int1;
            public uint uint2;
            public long long3;
            public ulong ulong4;
            public string str5;
            public bool bool6;
            public float float7;
            public double double8;
            public DateTime dt9;

            public static DataClass MakeInstance(ulong i)
            {
                var testInstance = new DataClass()
                {
                    //str5 = "FOO" + i.ToString(), 
                    int1 = (int)i,
                    ulong4 = i,
                    uint2 = (uint)i,
                    long3 = 256 * (long)i,
                    basenum = (long)i,
                    str5 = makestring(i),
                    float7 = (float)i,
                    double8 = (double)i,
                    dt9 = new DateTime((long)i)
                };
                return testInstance;
            }

            //public DateTime dt;
            public override string ToString()
            {
                return string.Format("{0} {1} {2} {3} {4}", str5, basenum, int1, uint2, long3, bool6);
            }
            public override bool Equals(object obj)
            {
                var IsEqual = true;
                var right = obj as DataClass;
                if (right == null)
                {
                    IsEqual = false;
                }
                else
                    if (int1 != right.int1)
                {
                    IsEqual = false;
                }
                else if (uint2 != right.uint2)
                {
                    IsEqual = false;
                }
                else if (long3 != right.long3)
                {
                    IsEqual = false;
                }
                else if (ulong4 != right.ulong4)
                {
                    IsEqual = false;
                }
                else if (float7 != right.float7)
                {
                    IsEqual = false;
                }
                else if (double8 != right.double8)
                {
                    IsEqual = false;
                }
                else if (dt9 != right.dt9)
                {
                    IsEqual = false;
                }
                else
                {
                    if (str5 == null ^ right.str5 == null)
                    {
                        IsEqual = false;
                    }
                    if (str5 != right.str5)
                    {
                        IsEqual = false;
                    }
                }
                return IsEqual;
            }
            public override int GetHashCode()
            {
                return base.GetHashCode();
            }
        }
        Random _random;
        [TestInitialize]
        public void init()
        {
            _random = new Random(Seed: 1);
        }
        class NestClass
        {
            public int _nNum;
            public DataClass _stack;
        }
        [TestMethod]
        public void TestListNest()
        {
            for (int i = 0; i < 10000; i++)
            {
                var x1x = new MemMap();

            }

            MapFileList<NestClass> mfList = null;
            int iLoop = 0;
            try
            {
                var bigstr = new string('a', 10000);
                uint nInst = 1 << 25; // 2^26 = 67,108,864   
                nInst = 16777214;
                nInst = 10000;
                using (mfList = new MapFileList<NestClass>())
                {
                    var xx = new NestClass() { _nNum = 1, _stack = null };
                    mfList.Add(xx);
                    var ret = mfList[mfList.Count - 1];
                    Assert.AreEqual(ret._nNum, mfList.Count);
                    Assert.IsNull(ret._stack, "ret stack should be null");
                    mfList.Clear();
                    for (int i = 1; i < nInst; i++)
                    {
                        var testInstance = DataClass.MakeInstance((ulong)i);
                        testInstance.str5 = bigstr;
                        var x = new NestClass() { _nNum = i, _stack = testInstance };
                        mfList.Add(x);
                        var retrievedInstance = mfList[(int)i - 1];
                        Assert.AreEqual(bigstr, retrievedInstance._stack.str5, "objects not equal Count=" + mfList.Count.ToString());
                        //                        Assert.AreEqual(testInstance, retrievedInstance, "objects not equal Count=" + mfList.Count.ToString());
                    }
                    mfList.VerifyNoLeaks();
                }
            }
            catch (Exception ex)
            {
                Assert.Fail("exception lp={0} cnt={1} {2}\r\n{3}", iLoop, mfList.Count, mfList._MemMap._stats.ToString(), ex.ToString());
                throw;
            }
        }
        [TestMethod]
        public void TestLists()
        {
            MapFileList<DataClass> mfList = null;
            int iLoop = 0;
            try
            {
                var bigstr = new string('a', 10000);
                uint nInst = 1 << 25; // 2^26 = 67,108,864   
                nInst = 16777214;
                nInst = 1000;
                using (mfList = new MapFileList<DataClass>())
                {
                    for (int i = 1; i < nInst; i++)
                    {
                        var testInstance = DataClass.MakeInstance((ulong)i);
                        testInstance.str5 = bigstr;
                        mfList.Add(testInstance);
                        var retrievedInstance = mfList[(int)i - 1];
                        Assert.AreEqual(bigstr, retrievedInstance.str5, "objects not equal Count=" + mfList.Count.ToString());
                        //                        Assert.AreEqual(testInstance, retrievedInstance, "objects not equal Count=" + mfList.Count.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Assert.Fail("exception lp={0} cnt={1} {2}\r\n{3}", iLoop, mfList.Count, mfList._MemMap._stats.ToString(), ex.ToString());
                throw;
            }
        }

        [TestMethod]
        public void TestListDerived()
        {
            MapFileList<Derived> mfList = null;
            int iLoop = 0;
            try
            {
                var bigstr = new string('a', 10000);
                uint nInst = 1 << 25; // 2^26 = 67,108,864   
                nInst = 16777214;
                nInst = 1000;
                using (mfList = new MapFileList<Derived>())
                {
                    for (int i = 1; i < nInst; i++)
                    {
                        var testInstance = new Derived()
                        {
                            int1 = i,
                            str5 = i.ToString()
                        };
                        testInstance.str5 = bigstr;
                        mfList.Add((Derived)testInstance);
                        var retrievedInstance = mfList[(int)i - 1];
                        Assert.AreEqual(bigstr, retrievedInstance.str5, "objects not equal Count=" + mfList.Count.ToString());
                        //                        Assert.AreEqual(testInstance, retrievedInstance, "objects not equal Count=" + mfList.Count.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Assert.Fail("exception lp={0} cnt={1} {2}\r\n{3}", iLoop, mfList.Count, mfList._MemMap._stats.ToString(), ex.ToString());
                throw;
            }
        }

        [TestMethod]
        public void ValueCollection()
        {
            using (var mfd = new MapFileDict<int, DataClass>())
            {
                var t = DataClass.MakeInstance(9);
                mfd[1] = (t);
                mfd[2] = t;
                var ret = mfd[1];
                Assert.AreEqual(t, ret);
                var vc = mfd.Values;
                int ncnt = 0;
                foreach (var x in vc)
                {
                    ncnt++;
                    Assert.AreEqual(t, x);
                }
                Assert.AreEqual(ncnt, 2, "didn't get right count");
                mfd.VerifyNoLeaks();
            }
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidOperationException))]
        public void ValueCollectionException()
        {
            using (var mfd = new MapFileDict<int, DataClass>())
            {
                var t = DataClass.MakeInstance(9);
                mfd[1] = (t);
                var ret = mfd[1];
                Assert.AreEqual(t, ret);
                var vc = mfd.Values;
                foreach (var x in vc)
                {
                    mfd[2] = t; // change collection in middle of iterationl
                }
            }

        }



        [TestMethod]
        public void NoResize()
        {
            DoIt(42, 1);
        }
        [TestMethod]
        public void Resize()
        {
            DoIt(1837, 1);
        }
        [TestMethod]
        public void UseFileBig()
        {
            uint nInst = 1 << 20;
            DoIt(nInst, 1, MapMemTypes.MapMemTypeFileName);
        }
        [TestMethod]
        public void Huge()
        {
            // sizeof(data)=32
            // 2 gigs = 2^31 
            // nInst = 2gigs / sizeof(data) = 2^31 / 2^5 = 2^26 = 1 << 26
            uint nInst = 1 << 26; // 2^26 = 67,108,864   
            // 2 gigs takes roughly 4 minutes

            nInst = 5999470;
            DoIt(nInst, nLoops: 1);
        }
        [TestMethod]
        public void UseFile()
        {
            uint nInst = 1000 * 1;
            using (var mfd = new MapFileList<DataClass>(ulInitialSize: 1024 * 1024, mapfileType: MapMemTypes.MapMemTypeFileName, uiViewSize: 1024 * 1024))
            {
                for (int i = 0; i < nInst; i++)
                {
                    var kk = DataClass.MakeInstance((ulong)i);
                    kk.str5 = new string('a', 10 * 1000);
                    mfd.Add(kk);
                }
                //                throw new InvalidOperationException("Test Passed: Stats:\r\n" + mfd._MemMap._stats.ToString());
            }
        }
        [TestMethod]
        public void UseFileResize()
        {
            uint nInst = 1 << 10;
            DoIt(nInst, 1, MapMemTypes.MapMemTypeFileName);
        }
        [TestMethod]
        public void TestNullStr()
        {
            using (var mfd = new MapFileDict<int, DataClass>())
            {
                var t = new DataClass() { str5 = null, int1 = 1 };
                mfd[1] = (t);
                var ret = mfd[1];
                Assert.AreEqual(t, ret);
                mfd.VerifyNoLeaks();
            }
        }
        [TestMethod]
        public void DeleteStuff()
        {
            uint nInstances = 100;
            DoIt(nInstances, 1, MapMemTypes.MapMemTypePageFile, fDoDeleteStuff: true);
        }
        [TestMethod]
        public void ListString()
        {
            uint nInstances = 546200;
            nInstances = 411800;
            //nInstances = 1000;
            using (var lst = new MapFileList<string>())
            {
                int i = 0;
                try
                {
                    for (i = 0; i < nInstances; i++)
                    {
                        var randstr = new string('a', 1000);// _random.Next(1000));
                        var teststr = string.Format("Foo {0}, {1}", i, randstr);
                        lst.Add(teststr);
                        var retrieved = lst[lst.Count - 1];
                        Assert.AreEqual(teststr, retrieved, "strs not eq " + i.ToString());
                        if (i % 1 == 0)
                        {
                            lst[i] = "bar";
                        }
                    }
                    //                    throw new InvalidOperationException("Test Passed: Stats:\r\n" + lst._MemMap.GetStats());
                }
                catch (Exception ex)
                {
                    lst.Dispose();
                    Assert.Fail("exception lp={0} cnt={1} {2}\r\n{3}", i, lst.Count, lst._MemMap._stats.ToString(), ex.ToString());
                    throw;
                }
            }
        }
        [TestMethod]
        public void DictStuff()
        {
            uint nInstances = 546200;
            nInstances = 100000;
            using (var dict = new MapFileDict<int, string>())
            {
                int i = 0;
                try
                {
                    //                    var randstr = new string('a', 100000);
                    for (i = 0; i < nInstances; i++)
                    {
                        var randstr = new string('a', _random.Next(10000));
                        var teststr = string.Format("Foo {0}, {1}", i, randstr);
                        dict[i] = teststr;
                        var retrieved = dict[i];
                        Assert.AreEqual(teststr, retrieved, "strs not eq " + i.ToString());
                        dict[i] = "bar";
                    }
                    //throw new InvalidOperationException("Test Passed");
                }
                catch (Exception ex)
                {
                    dict.Dispose();
                    Assert.Fail("exception lp={0} cnt={1} {2}\r\n{3}", i, dict.Count, dict._MemMap._stats.ToString(), ex.ToString());
                    throw;
                }
                dict.VerifyNoLeaks();
            }
        }

        public static string makestring(ulong i)
        {
            if (i % 10 == 0)
            {
                return null; // test null strings too
            }
            if (i % 13 == 0)
            {
                return string.Empty; // test null strings too
            }
            return string.Format("Foo{0}", new string('0', (int)(i % 20)));
        }

        [TestMethod]
        public void RandStuff()
        {
            uint nInstances = 100000;
            using (var dict = new MapFileDict<int, DataClass>())
            {
                try
                {
                    for (int lp = 0; lp < 1; lp++)
                    {
                        for (int i = 0; i < nInstances; i++)
                        {
                            var rndInd = _random.Next((int)nInstances);
                            var rnd = _random.Next(10000) / 10;
                            dict[rndInd] = new DataClass()
                            {
                                int1 = rndInd,
                                uint2 = (uint)rnd,
                                str5 = string.Format("foo{0}", new string('1', rnd))
                            };
                            dict[rndInd].str5 = "bar";
                            var x = dict[rndInd];
                            Assert.AreEqual(rndInd, x.int1, "rnd inst not eq");
                        }
                    }
                    //               throw new InvalidOperationException("Test Passed");
                }
                catch (Exception ex)
                {
                    dict.Dispose();
                    Assert.Fail("exception lp={0} cnt={1} {2}\r\n{3}", 0, dict.Count, dict._MemMap._stats.ToString(), ex.ToString());
                }
                dict.VerifyNoLeaks();
            }
        }
        private void DoIt(
            uint nInstances,
            int nLoops,
            MapMemTypes mapfileType = MapMemTypes.MapMemTypePageFile,
            bool fDoDeleteStuff = false,
            bool fUseNormDict = false
            )
        {
            Assert.AreEqual(IntPtr.Size, 4, "intptr != 4?");
            for (uint iLoop = 0; iLoop < nLoops; iLoop++)
            {
                uint ulInitialFileSize = 0;

                if (fDoDeleteStuff)
                {

                }
                IDictionary<int, DataClass> dict;
                using (var mfd = new MapFileDict<int, DataClass>(ulInitialSize: ulInitialFileSize, mapfileType: mapfileType))
                {
                    if (fUseNormDict)//|| true)
                    {
                        dict = new Dictionary<int, DataClass>();
                    }
                    else
                    {
                        dict = mfd;
                    }
                    try
                    {
                        //                    Assert.IsTrue(mfd._objSize == 36, "obj size = 36");
                        for (int i = 00; i < nInstances; i++)
                        {
                            var testInstance = DataClass.MakeInstance((ulong)i);
                            dict[(int)i] = testInstance;
                            var res = i;

                            var retrievedInstance = dict[(int)i];
                            Assert.AreEqual(testInstance, retrievedInstance, "objects not equal Count=" + dict.Count.ToString());
                            if (fDoDeleteStuff)
                            {



                            }
                        }
                        for (int i = 00; i < nInstances; i++)
                        {
                            var retrievedInstance = dict[i];
                            var testInstance = DataClass.MakeInstance((ulong)i);
                            Assert.AreEqual(testInstance, retrievedInstance, "objects not equal Count=" + dict.Count.ToString());
                        }
                        //dict.Clear();
                        mfd.VerifyNoLeaks();
                    }
                    catch (Exception ex)
                    {
                        mfd.Dispose();
                        Assert.Fail("exception lp={0} cnt={1} {2}\r\n{3}", iLoop, mfd.Count, mfd._MemMap._stats.ToString(), ex.ToString());
                        throw;
                    }

                }
            }
        }

    }
}
