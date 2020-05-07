using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Xml;
using System.Threading.Tasks;
using MapFileDict;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MapFileDictTest
{
    [Serializable]
    public class SerialClass : ISerializable
    {
        int int1;
        //        public DateTime dt;
        public SerialClass()
        {
            int1 = 123;
            //          dt = DateTime.Now;
        }
        public int GetInt1
        {
            get { return int1; }
        }
        public SerialClass(SerializationInfo sinfo, StreamingContext scontext)
        {
            var y = (int)sinfo.GetValue("int1", typeof(int));
            int1 = y;
            //        var z = (DateTime)sinfo.GetValue("dt", typeof(DateTime));
            //        dt = z;

        }
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("int1", int1, typeof(int));
            //info.AddValue("dt", dt);
        }
    }
    public class Testbase
    {
        [TestInitialize]
        public void TestInit()
        {
            //    Assert.IsTrue(false, "in testinit");
        }
        [TestCleanup]
        public void TestCleanup()
        {
            //        Assert.IsTrue(false, "in testinit");
        }
    }

    [TestClass]
    public class MapTests : Testbase
    {
        public Random _random = new Random(1);
        [TestMethod]
        public void Serializable()
        {
            var nInstances = 10;
            var x = new SerialClass();
            var fname = System.IO.Path.GetTempFileName();
            var b = new BinaryFormatter();

            using (var lst = new MapFileList<Dictionary<int, SerialClass>>())
            {
                lst._MemMap.fUseISerializable = true;
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var y = new Dictionary<int, SerialClass>();
                    y[1] = x;
                    y[2] = x;
                    y[3] = x;
                    y[4] = x;
                    y[5] = x;
                    y[6] = x;
                    lst.Add(y);
                    var retrieved = lst[0];
                    Assert.AreEqual(y[2].GetInt1, 123);
                }

            }


            using (var s = new FileStream(fname, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var y = new Dictionary<int, SerialClass>();
                y[1] = x;
                y[2] = x;
                y[3] = x;
                y[4] = x;
                y[5] = x;
                y[6] = x;
                b.Serialize(s, x);
            }

            using (var s = new FileStream(fname, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var newobj = (SerialClass)b.Deserialize(s);
                Assert.AreEqual(newobj.GetInt1, 123);
            }
            using (var lst = new MapFileList<SerialClass>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var obj = new SerialClass();
                    lst.Insert(0, obj);
                    var retrieved = lst[0];
                    Assert.AreEqual(obj.GetInt1, retrieved.GetInt1);
                }
                lst.VerifyNoLeaks();
            }
        }
        [TestMethod]
        public void SerGenericDict()
        {
            var nInstances = 10;
            using (var lst = new MapFileList<Dictionary<int, string>>())
            {
                lst._MemMap.fUseISerializable = true;
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var dict = new Dictionary<int, string>() { { 1, "1" } };

                    lst.Insert(0, dict);
                    var retrieved = lst[0];
                    Assert.AreEqual(retrieved[1], "1");
                }
                lst.VerifyNoLeaks();
            }
        }

        [TestMethod]
        public void SimpleTypes()
        {
            var nInstances = 10;
            using (var lst = new MapFileList<int>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    lst.Insert(0, lp + 1);
                    var retrieved = lst[0];
                    Assert.AreEqual(lp + 1, retrieved);
                }
                lst.VerifyNoLeaks();
            }
        }
        enum simpleEnum
        {
            one,
            two
        };
        [TestMethod]
        public void SimpleEnum()
        {
            var nInstances = 10;
            using (var lst = new MapFileList<simpleEnum>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var val = lp % 2 == 0 ? simpleEnum.one : simpleEnum.two;
                    lst.Insert(0, val);
                    var retrieved = lst[0];
                    Assert.AreEqual(val, retrieved);
                }
                lst.VerifyNoLeaks();
            }
        }
        [TestMethod]
        public void SimpleString()
        {
            var nInstances = 10;
            using (var lst = new MapFileList<string>())
            {
                var ret = string.Empty;

                lst.Insert(0, null);
                ret = lst[0];
                Assert.IsNull(ret);

                lst.Insert(0, string.Empty);
                ret = lst[0];
                Assert.AreEqual(ret, string.Empty);

                for (int lp = 0; lp < nInstances; lp++)
                {
                    var str = UnitTest1.makestring((ulong)lp);
                    lst.Insert(0, str);
                    var retrieved = lst[0];
                    Assert.AreEqual(str, retrieved);
                }


                lst.VerifyNoLeaks();
            }
        }
        [TestMethod]
        public void SimpleGenericList()
        {
            var nInstances = 10;

            using (var lst = new MapFileList<List<int>>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var val = new List<int>() { 1, 2, 3, 4 };
                    lst.Insert(0, val);
                    var retrieved = lst[0];
                    Assert.AreEqual(val[1], retrieved[1]);
                }
                lst.VerifyNoLeaks();
            }
        }
        [TestMethod]
        public void GenericList()
        {
            var nInstances = 10;

            using (var lst = new MapFileList<List<string>>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var val = new List<string>() { "1", "2", "3", "4" };
                    lst.Insert(0, val);
                    var retrieved = lst[0];
                    Assert.AreEqual(val[1], retrieved[1]);
                }
                lst.VerifyNoLeaks();
            }
        }
        [TestMethod]
        //        [ExpectedException(typeof(InvalidOperationException))] // Single (float)
        public void HashTableSerial()
        {
            var nInstances = 10;

            using (var lst = new MapFileList<Hashtable>())
            {
                lst._MemMap.fUseISerializable = true;
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var val = new Hashtable() { { 1, 1 }, { 2, 2 } };
                    lst.Insert(0, val);
                    var retrieved = lst[0];
                    Assert.AreEqual(val[1], retrieved[1]);
                }
                lst.VerifyNoLeaks();
            }
        }
        [TestMethod]
        public void HashSet()
        {
            var nInstances = 10;

            using (var lst = new MapFileList<HashSet<int>>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var val = new HashSet<int>() { 1, 3, 4 };
                    lst.Insert(0, val);
                    var retrieved = lst[0];
                    foreach (var xx in val)
                    {
                        Assert.IsTrue(retrieved.Contains(xx));
                    }
                }
                lst.VerifyNoLeaks();
            }
        }
        [TestMethod]
        public void GenericDict()
        {
            var nInstances = 10;

            using (var lst = new MapFileList<Dictionary<int, int>>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var val = new Dictionary<int, int>() { { 1, 1 }, { 2, 2 }, { 3, 3 } };
                    lst.Insert(0, val);
                    var retrieved = lst[0];
                    //retrieved = lst[1];
                    Assert.AreEqual(val[1], retrieved[1]);
                    Assert.AreEqual(val[2], retrieved[2]);
                    Assert.AreEqual(val[3], retrieved[3]);
                }
                lst.VerifyNoLeaks();
            }
        }
        [TestMethod]
        public void GenericDictEnum()
        {

            var val = new Dictionary<int, string>() { {0,"0"}, { 1, "1" }, { 2, "2" }, { 3, "3" } };
            using (var dict = new MapFileDict<int, string>(val))
            {
                int ndx = 0;
                foreach (var item in dict)
                {
                    Assert.AreEqual(item.Key, ndx);
                    Assert.AreEqual(item.Value, ndx.ToString());
                    ndx++;
                }
                Assert.IsTrue(dict.Count == val.Count, "Counts not equal");
            }

        }
        [TestMethod]
        public void GenericDictStr()
        {
            var nInstances = 10;

            using (var lst = new MapFileList<Dictionary<string, string>>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var val = new Dictionary<string, string>() { { "1", "1" }, { "2", "2" }, { "3", "3" } };
                    lst.Insert(0, val);
                }
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var val = new Dictionary<string, string>() { { "1", "1" }, { "2", "2" }, { "3", "3" } };

                    var retrieved = lst[lp];
                    //retrieved = lst[1];
                    Assert.AreEqual(val["1"], retrieved["1"]);
                    Assert.AreEqual(val["2"], retrieved["2"]);
                    Assert.AreEqual(val["3"], retrieved["3"]);

                }
                lst.VerifyNoLeaks();
            }
        }
        [TestMethod]
        public void Array()
        {
            var nInstances = 10;
            using (var lst = new MapFileList<int[]>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var data = new int[10];
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] = i + 1;
                    }
                    lst.Insert(0, data); // insert entire array as 1st elem
                    var retrieved = lst[0];
                    for (int i = 0; i < data.Length; i++)
                    {
                        Assert.AreEqual(data[i], retrieved[i]);

                    }
                }
                lst.VerifyNoLeaks();
            }
        }
        [TestMethod]
        public void ArrayClass()
        {
            var nInstances = 10;
            using (var lst = new MapFileList<MapFileDictTest.UnitTest1.DataClass[]>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var data = new MapFileDictTest.UnitTest1.DataClass[10];
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] = MapFileDictTest.UnitTest1.DataClass.MakeInstance((ulong)i);
                    }
                    lst.Insert(0, data); // insert entire array as 1st elem
                    var retrieved = lst[0];
                    for (int i = 0; i < data.Length; i++)
                    {
                        Assert.AreEqual(data[i], retrieved[i]);

                    }
                }
                lst.VerifyNoLeaks();
            }
        }

        [TestMethod]
        public void ArrayString()
        {
            var nInstances = 10000;
            using (var lst = new MapFileList<string[]>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var data = new string[20];
                    for (int i = 0; i < data.Length; i++)
                    {
                        var strToInsert = UnitTest1.makestring((ulong)i);// "foo" + (i + 1).ToString();
                        data[i] = strToInsert;
                    }
                    lst.Insert(0, data);
                    var retrieved = lst[0];
                    Assert.AreEqual(data[0], retrieved[0]);
                }
                lst.VerifyNoLeaks();
            }
        }

        public abstract class MyAbstractClass
        {
            public int n1;
        }
        public class AbsDerived : MyAbstractClass
        {
            public AbsDerived()
            {
                n1 = 7;
            }
        }
        public class ClassWithAbstractMember
        {
            public MyAbstractClass deriverMember;
            public ClassWithAbstractMember()
            {
                deriverMember = new AbsDerived();
            }
        }
        [TestMethod]
        public void Abstract()
        {
            using (var memmap = new MemMap())
            {
                var xInst = new ClassWithAbstractMember();
                Assert.AreEqual(xInst.deriverMember.n1, 7);
                var mh = memmap.AddData(xInst);
                var ret = (ClassWithAbstractMember)memmap.GetData(mh, xInst.GetType());
                Assert.AreEqual(ret.deriverMember.n1, 7);
                memmap.RemoveData(mh, xInst.GetType());
                Assert.IsTrue(memmap._stats._nCurBytesAlloc == 0, "leftovers?");
            }
        }

        class class1
        {
            public int n1;
        }
        class classCont
        {
            public class1 _c1;
        }
        [TestMethod]
        public void MemberClass()
        {
            using (var memmap = new MemMap())
            {
                var xInst = new classCont()
                {
                    _c1 = new class1()
                    {
                        n1 = 7
                    }
                };
                var mh = memmap.AddData(xInst);
                var ret = (classCont)memmap.GetData(mh, xInst.GetType());
                Assert.AreEqual(ret._c1.n1, 7);
                memmap.RemoveData(mh, xInst.GetType());
                Assert.IsTrue(memmap._stats._nCurBytesAlloc == 0, "leftovers?");
            }
        }

        /*
        Test Name:	XMLDoc
Test FullName:	MapFileDictTest.MapTests.XMLDoc
Test Source:	c:\Users\calvinh\Documents\Visual Studio 2012\Projects\MapFileDict\MapFileDictTest\MapTests.cs : line 456
Test Outcome:	Failed
Test Duration:	0:00:00.10056

Result Message:	
Test method MapFileDictTest.MapTests.XMLDoc threw exception: 
System.MissingMethodException: No parameterless constructor defined for this object.
Result StackTrace:	
at System.RuntimeTypeHandle.CreateInstance(RuntimeType type, Boolean publicOnly, Boolean noCheck, Boolean& canBeCached, RuntimeMethodHandleInternal& ctor, Boolean& bNeedSecurityCheck)
   at System.RuntimeType.CreateInstanceSlow(Boolean publicOnly, Boolean skipCheckThis, Boolean fillCache, StackCrawlMark& stackMark)
   at System.RuntimeType.CreateInstanceDefaultCtor(Boolean publicOnly, Boolean skipCheckThis, Boolean fillCache, StackCrawlMark& stackMark)
   at System.Activator.CreateInstance(Type type, Boolean nonPublic)
   at System.Activator.CreateInstance(Type type)
   at MapFileDict.MemMap.GetData(MapFileLocator loc, Type type, Object parentObj, FieldInfo fldInfoContainer) in c:\Users\calvinh\Documents\Visual Studio 2012\Projects\MapFileDict\MapFileDict\MemMap.cs:line 1324
   at MapFileDict.MemMap.GetData(MapFileLocator loc, Type type, Object parentObj, FieldInfo fldInfoContainer) in c:\Users\calvinh\Documents\Visual Studio 2012\Projects\MapFileDict\MapFileDict\MemMap.cs:line 1431
   at MapFileDict.MemMap.GetData(MapFileLocator loc, Type type, Object parentObj, FieldInfo fldInfoContainer) in c:\Users\calvinh\Documents\Visual Studio 2012\Projects\MapFileDict\MapFileDict\MemMap.cs:line 1431
   at MapFileDict.MemMap.GetData(MapFileLocator loc, Type type, Object parentObj, FieldInfo fldInfoContainer) in c:\Users\calvinh\Documents\Visual Studio 2012\Projects\MapFileDict\MapFileDict\MemMap.cs:line 1431
   at MapFileDict.MemMap.GetData(MapFileLocator loc, Type type, Object parentObj, FieldInfo fldInfoContainer) in c:\Users\calvinh\Documents\Visual Studio 2012\Projects\MapFileDict\MapFileDict\MemMap.cs:line 1431
   at MapFileDict.MemMap.GetData(MHandle mHandle, Type type) in c:\Users\calvinh\Documents\Visual Studio 2012\Projects\MapFileDict\MapFileDict\MemMap.cs:line 1188
   at MapFileDictTest.MapTests.XMLDoc() in c:\Users\calvinh\Documents\Visual Studio 2012\Projects\MapFileDict\MapFileDictTest\MapTests.cs:line 467


        [TestMethod]
        public void XMLDoc()
        {
            using (var memmap = new MemMap())
            {
                var xdoc = new XmlDocument();
                //var node = x.CreateNode(XmlNodeType.XmlDeclaration, string.Empty, string.Empty);
                //x.AppendChild(node);
                //var elm = x.CreateElement("", "root", "");
                //x.AppendChild(elm);
                //elm.AppendChild(x.CreateTextNode("some text"));
                //x.Save(@"c:\t.xml");
                var mh= memmap.AddData(xdoc);
                var ret = memmap.GetData(mh, xdoc.GetType());
                memmap.RemoveData(mh, xdoc.GetType());

            }
        }

        [TestMethod]
        public void ListXML()
        {
            using (var lst = new MapFileList<XmlDocument>())
            {
                var x = new XmlDocument();
                var node = x.CreateNode(XmlNodeType.XmlDeclaration, string.Empty, string.Empty);
                x.AppendChild(node);
                var elm = x.CreateElement("","root","");
                x.AppendChild(elm);
                elm.AppendChild(x.CreateTextNode("some text"));
                x.Save(@"c:\t.xml");
                lst.Add(x);
                lst.VerifyNoLeaks();
            }
        }
        */

        [TestMethod]
        public void ListSingle()
        {
            var nInstances = 10000;
            using (var lst = new MapFileList<Single>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    lst.Add((float)lp / 31);
                }
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var retrieved = lst[lp];
                    Assert.AreEqual((float)lp / 31, retrieved);
                }
                lst.VerifyNoLeaks();
            }
        }

        [TestMethod]
        public void ListDouble()
        {
            var nInstances = 10000;
            using (var lst = new MapFileList<Double>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    lst.Add((Double)lp / 31 - 100);
                }
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var retrieved = lst[lp];
                    Assert.AreEqual((Double)lp / 31 - 100, retrieved);
                }
                lst.VerifyNoLeaks();
            }
        }
        [TestMethod]
        public void ListDateTime()
        {
            var nInstances = 10000;
            var dt = new DateTime(2013, 09, 13, 0, 0, 0);
            using (var lst = new MapFileList<DateTime>())
            {
                for (int lp = 0; lp < nInstances; lp++)
                {
                    lst.Add(dt.AddMinutes(lp));
                }
                for (int lp = 0; lp < nInstances; lp++)
                {
                    var retrieved = lst[lp];
                    Assert.AreEqual(dt.AddMinutes(lp), retrieved);
                }
                lst.VerifyNoLeaks();
            }
        }

        [TestMethod]
        public void MapTestShrink()
        {
            var nInstances = 100;
            using (var lst = new MapFileList<MapFileDictTest.UnitTest1.DataClass>())
            {
                for (int i = 0; i < nInstances; i++)
                {
                    var stk = new MapFileDictTest.UnitTest1.DataClass();
                    stk.str5 = new string('a', 10000);
                    lst.Insert(0, stk);
                    var retrieved = lst[0];
                    Assert.AreEqual(stk, retrieved);
                }
                Assert.AreEqual((ulong)2097152, lst._MemMap._stats._ulMapMemSize, "size not large");

                for (int i = 0; i < nInstances; i++)
                {
                    lst.RemoveAt(nInstances - 1 - i);
                }
                lst._MemMap.Compact();
                Assert.AreEqual((ulong)65536, lst._MemMap._stats._ulMapMemSize, "size not small");
                //lst._MemMap.Compact();
                //throw new InvalidOperationException(lst._MemMap._stats.ToString());
                lst.VerifyNoLeaks();
            }
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidOperationException))]
        public void MapTestRemove()
        {
            var nInstances = 100;
            using (var memmap = new MemMap())
            {
                for (int i = 0; i < nInstances; i++)
                {
                    var x = memmap.AddData("one");
                    var removeResult = memmap.RemoveData(x, typeof(string));
                    Assert.IsTrue(removeResult);
                    removeResult = memmap.RemoveData(x, typeof(string));
                    Assert.IsFalse(removeResult);
                }
            }
        }

        [TestMethod]
        public void MapTestSimple()
        {
            using (var memmap = new MemMap(ulInitialSize: 0, mapfileType: MapMemTypes.MapMemTypePageFile, ulGrowDynamicallyThreshold: 0))
            {
                var str = string.Format("asdfasdfa");
                var mHandle1 = memmap.AddData(str);

                var ret = (string)memmap.GetData(mHandle1, typeof(string));
                Assert.AreEqual(str, ret, "str != ret ?");

                var str2 = string.Format("aaa");
                var mHandle2 = memmap.AddData(str2);

                ret = (string)memmap.GetData(mHandle2, typeof(string));

                Assert.AreEqual(str2, ret, "str2 != ret ?");

                using (var lst = new MapFileList<MapFileDictTest.UnitTest1.DataClass>(memMapToUse: memmap))
                {
                    var stk = new MapFileDictTest.UnitTest1.DataClass();
                    lst.Insert(0, stk);
                    var retrieved = lst[0];
                    Assert.AreEqual(stk, retrieved);
                    lst.RemoveAt(0);
                    memmap.RemoveData(mHandle1, typeof(string));
                    memmap.RemoveData(mHandle2, typeof(string));
                    lst.VerifyNoLeaks();
                }
            }
        }

    }
}
