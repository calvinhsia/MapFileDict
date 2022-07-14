using MapFileDict;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

namespace Test64
{
    [TestClass]
    public class UnitTest1
    {
        public TestContext TestContext { get; set; }
        public enum Myflags : long
        {
            val1 = 1,
            val2 = 2,
            valbig = (1L << 32) + 1
        }
        public class TestData
        {
//            public Myflags myflags;

            //public int key;
            //public string data { get; set; }
            public Uri HelpLink;

            //public Dictionary<string, string> dictParams { get; set; } = new Dictionary<string, string>();
            //public Dictionary<string, string> dictFields{ get; set; } = new Dictionary<string, string>();
            public TestData() { } // parameterless ctor required 
            public TestData(int i)
            {
//                myflags = Myflags.valbig;
                //key = i;
                //data = $"one {i}";
                //dictParams["a"] = "b";
            }
            //public override string ToString() => $"{data}  helplink={HelpLink} Parms= {dictParams["a"]}  FldCnt={dictFields.Count}";
            public override string ToString() => $" URI={HelpLink}";
        }
        [TestMethod]
        [Ignore]
        public void TestCSWin32LoadTestData()
        {
            var mapfiledict = new MapFileDict<string, TestData>(mapfileType: MapMemTypes.MapMemTypePageFile);
            var origData = new List<TestData>();
            origData.Add(new TestData(1)
            {
                HelpLink = new Uri("http://msn.com")
            });
            Myflags myflags = Myflags.valbig;
            var x = myflags.GetType().GetEnumUnderlyingType();
            var y = myflags.GetType();
            foreach (var itm in origData)
            {
                mapfiledict["a"] = itm;
            }
            foreach (var kvp in mapfiledict)
            {
                var mapver = mapfiledict[kvp.Key];
                TestContext.WriteLine($"{kvp.Key} = {kvp.Value}");
            }

        }
    }
}
