#nullable enable
using MapFileDict;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;

namespace TestCSWin32
{
    [TestClass]
    public class CSWin32Test
    {
        public TestContext TestContext { get; set; }
        public enum Myflags
        {
            val1=1,
            val2=2,
        }
        public class TestData
        {
            public Myflags myflags;

            //public int key;
            //public string data { get; set; }
            //public Uri HelpLink { get; set; }

            //public Dictionary<string, string> dictParams { get; set; } = new Dictionary<string, string>();
            //public Dictionary<string, string> dictFields{ get; set; } = new Dictionary<string, string>();
            public TestData() { } // parameterless ctor required 
            public TestData(int i)
            {
                myflags = Myflags.val2;
                //key = i;
                //data = $"one {i}";
                //dictParams["a"] = "b";
            }
            //public override string ToString() => $"{data}  helplink={HelpLink} Parms= {dictParams["a"]}  FldCnt={dictFields.Count}";
            public override string ToString() => $"{myflags}";
        }
        [TestMethod]
        public void TestCSWin32LoadTestData()
        {
            var mapfiledict = new MapFileDict<string, TestData>(mapfileType: MapMemTypes.MapMemTypePageFile);
            var origData = new List<TestData>();
            origData.Add(new TestData(1));
            //{
            //    HelpLink=new Uri("http://msn.com")
            //});

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
        [TestMethod]
        [Ignore]
        public void TestCSWin32Load()
        {
            var docsPath = @"C:\Users\calvinh\source\repos\CsWin32\bin\Microsoft.Windows.CsWin32.Tests\Debug\netcoreapp3.1\apidocs.msgpack";
            using FileStream docsStream = File.OpenRead(docsPath);


            var dictAPI = MessagePackSerializer.Deserialize<Dictionary<string, ApiDetails>>(docsStream);
            var mapfiledict = new MapFileDict<string, ApiDetails>(mapfileType: MapMemTypes.MapMemTypePageFile);
            foreach (var kvp in dictAPI)
            {
                mapfiledict[kvp.Key] = kvp.Value;
            }
            Assert.AreEqual(mapfiledict.Count, dictAPI.Count);
            foreach (var kvp in dictAPI)
            {
                var mapver = mapfiledict[kvp.Key];
                Assert.AreEqual(kvp.Value, mapver);
            }
        }
    }
    //
    // Summary:
    //     Captures all the documentation we have available for an API.
    [MessagePackObject(false)]
    public class ApiDetails
    {
        //
        // Summary:
        //     Gets or sets the URL that provides more complete documentation for this API.
        [Key(0)]
        public Uri? HelpLink { get; set; }

        //
        // Summary:
        //     Gets or sets a summary of what the API is for.
        [Key(1)]
        public string? Description { get; set; }

        //
        // Summary:
        //     Gets or sets the remarks section of the documentation.
        [Key(2)]
        public string? Remarks { get; set; }

        //
        // Summary:
        //     Gets or sets a collection of parameter docs, keyed by their names.
        [Key(3)]
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();


        //
        // Summary:
        //     Gets or sets a collection of field docs, keyed by their names.
        [Key(4)]
        public Dictionary<string, string> Fields { get; set; } = new Dictionary<string, string>();


        //
        // Summary:
        //     Gets or sets the documentation of the return value of the API, if applicable.
        [Key(5)]
        public string? ReturnValue { get; set; }
    }

}
