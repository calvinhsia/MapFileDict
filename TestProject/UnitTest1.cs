using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Disassemblers;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace TestProject
{
    [TestClass]
    public class UnitTest1
    {
        [Flags()]
        public enum TestMode
        {
            UseFileRead = 1, // Just use a FileStream and read the bytes. Very fast.
            MapRegionOnly = 2, // Map a region the same size as the caller's request. This can be a very big region
            MapRestOfFile = 4, // Map starting from the caller's requested offset to the rest of the file
            UseFixedRegion = 8 // use a fixed size 64k region only
        }
        [MemoryDiagnoser]
        public class BenchTest
        {
            public const int FixedRegionSize = 65536;
            public const string fileLarge = @"C:\ConsumptionTestData\ConsumptionTest.VSStartup.etlx"; // 250Meg file
            public FileInfo _fileInfo = new FileInfo(fileLarge);

            [Benchmark]
            [Arguments(10L, 1000, TestMode.UseFileRead)]
            [Arguments(10L, 1000, TestMode.MapRegionOnly)]
            [Arguments(10L, 1000, TestMode.MapRestOfFile)]
            [Arguments(10L, 1000, TestMode.UseFixedRegion)]
            [Arguments(10L, 1000000, TestMode.UseFileRead)]
            [Arguments(10L, 1000000, TestMode.MapRegionOnly)]
            [Arguments(10L, 1000000, TestMode.MapRestOfFile)]
            [Arguments(10L, 1000000, TestMode.UseFixedRegion)]
            public ReadOnlySpan<byte> MapBench(long offset, int size, TestMode testMode)
            {
                if (testMode.HasFlag(TestMode.UseFileRead))
                {
                    using var _fileStream = File.OpenRead(_fileInfo.FullName);
                    var bytes = new byte[size];
                    _fileStream.Seek(offset, SeekOrigin.Begin);
                    _fileStream.Read(bytes);
                    var span = new ReadOnlySpan<byte>(bytes);
                    return span;
                }
                else
                {
                    using var mappedFile = MemoryMappedFile.CreateFromFile(_fileInfo.FullName);
                    //                    using var mappedFile = MemoryMappedFile.CreateFromFile(fileInfo.FullName, FileMode.Open, "MyMap1", capacity: 0, access: MemoryMappedFileAccess.Read);
                    var regionSize = testMode.HasFlag(TestMode.MapRestOfFile) ?
                                ((int)(_fileInfo.Length - offset)) :
                        (testMode.HasFlag(TestMode.UseFixedRegion) ?
                            (Math.Min(FixedRegionSize, size)) :
                            size);
                    //        public const uint AllocationGranularity = 0x10000; //64k VirtualAlloc granularity for both 64 & 32 bit Windows systems
                    unsafe
                    {
                        if (testMode.HasFlag(TestMode.UseFixedRegion)) // read the data in chunks of FixedRegionSize
                        {
                            var bytes = new byte[size];
                            int BytesSoFar = 0;
                            while (BytesSoFar < size)
                            {
                                var numToRead = Math.Min(size - BytesSoFar, regionSize);// if there are fewer bytes to read than the region size
                                using var mapView = mappedFile.CreateViewAccessor(offset: offset, size: numToRead, MemoryMappedFileAccess.Read);
                                mapView.ReadArray<byte>(position: 0, // offset within accessor
                                                        bytes,       // target array
                                                        offset: BytesSoFar, // index into target array
                                                        numToRead); // Count
                                BytesSoFar += regionSize;
                                offset += regionSize;
                            }
                            var newspan = new ReadOnlySpan<byte>(bytes);
                            return newspan;
                        }
                        else
                        {
                            byte* ptr = null;
                            MemoryMappedViewAccessor? mapView = null;
                            try
                            {
                                mapView = mappedFile.CreateViewAccessor(offset: offset, size: regionSize, MemoryMappedFileAccess.Read);
                                // https://github.com/dotnet/runtime/blob/3689fbec921418e496962dc0ee252bdc9eafa3de/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/SafeBuffer.cs
                                mapView.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr); //SafeMemoryMappedViewHandle gets a handle to the view.  AcquirePointer gets a pointer to the window. Always multiple of Granularity
                                var span = new ReadOnlySpan<byte>(ptr +
                                    mapView.PointerOffset // Gets the number of bytes by which the starting position of this view is offset from the beginning of the memory-mapped file.
                                    , size);
                                // NOTE: this is a span to the View buffer. It must be released after use. So we either incur the cost of copying it or we must release it at some point
                                var newspan = new ReadOnlySpan<byte>(span.ToArray());
                                return newspan;
                            }
                            finally
                            {
                                if (ptr != null)
                                {
                                    mapView?.SafeMemoryMappedViewHandle.ReleasePointer(); // must release pointer else leaks entire mapped section
                                }
                                mapView?.Dispose();
                            }
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void TestBenchMarkMemMapped()
        {
            BenchmarkDotNet.Running.BenchmarkRunner.Run<BenchTest>();
            /*
|   Method | offset |    size |       testMode |        Mean |     Error |     StdDev |     Gen0 |     Gen1 |     Gen2 | Allocated |
|--------- |------- |-------- |--------------- |------------:|----------:|-----------:|---------:|---------:|---------:|----------:|
| MapBench |     10 |    1000 |    UseFileRead |    53.80 us |  1.056 us |   1.216 us |   1.2817 |   0.6104 |        - |   5.25 KB |
| MapBench |     10 |    1000 |  MapRegionOnly |   124.13 us |  2.436 us |   2.279 us |   0.2441 |        - |        - |   1.44 KB |
| MapBench |     10 |    1000 |  MapRestOfFile | 1,028.19 us | 18.542 us |  22.771 us |        - |        - |        - |   1.44 KB |
| MapBench |     10 |    1000 | UseFixedRegion |   127.07 us |  2.395 us |   2.240 us |   0.2441 |        - |        - |   1.44 KB |
| MapBench |     10 | 1000000 |    UseFileRead |   255.96 us |  4.800 us |   9.475 us | 186.5234 | 186.0352 | 186.0352 | 977.28 KB |
| MapBench |     10 | 1000000 |  MapRegionOnly |   878.93 us | 13.424 us |  11.900 us | 200.1953 | 200.1953 | 200.1953 | 977.08 KB |
| MapBench |     10 | 1000000 |  MapRestOfFile | 1,837.38 us | 35.032 us |  38.938 us | 199.2188 | 199.2188 | 199.2188 | 977.08 KB |
| MapBench |     10 | 1000000 | UseFixedRegion | 3,857.94 us | 75.808 us | 126.658 us | 199.2188 | 199.2188 | 199.2188 | 980.45 KB |

             */
        }

        [TestMethod]
        public void VerifyBenchTest()
        {
            var benchtest = new BenchTest();
            var allbytes = File.ReadAllBytes(benchtest._fileInfo.FullName);
            VerifyData(10, 100, TestMode.UseFixedRegion);
            VerifyData(10 + 65536, 65537, TestMode.UseFixedRegion);
            VerifyData(0, (int)benchtest._fileInfo.Length, TestMode.UseFixedRegion); // verify entire file

            VerifyData(10, 100, TestMode.MapRestOfFile);
            VerifyData(10, 100, TestMode.MapRegionOnly);
            VerifyData(10, 100, TestMode.UseFileRead);
            VerifyData(10 + 65536, 10000, TestMode.MapRestOfFile);
            VerifyData(10 + 65536, 10000, TestMode.MapRegionOnly);
            void VerifyData(long offset, int size, TestMode testMode)
            {
                var span = benchtest.MapBench(offset, size, testMode);
                for (int pos = 0; pos < size; pos++)
                {
                    var correctByte = allbytes[offset + (uint)pos];
                    Assert.AreEqual(correctByte, span[pos], $"returned span should match");
                }
            }
        }

        [TestMethod]
        [Description("Verifies mapping of files by mapping either small desired regions or the rest of the file")]
        public void TestMemoryMapped()
        {
            var lstFiles = new List<MyMap>();
            //            foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            var fileLarge = @"C:\ConsumptionTestData\ConsumptionTest.VSStartup.etlx"; // 250Meg file
            {
                var finfo = new FileInfo(fileLarge);
                Trace.WriteLine($"{finfo.FullName}  {finfo.Length:n0}");
                if (finfo.Length > 0)
                {
                    var allbytes = File.ReadAllBytes(finfo.FullName);
                    var useFilRead = true;
                    using var map = new MyMap(finfo, UseFileRead: useFilRead);

                    // Read 1k bytes at offset 10, using 
                    VerifySpan(10, 1000, UseWholeFile: true);
                    VerifySpan(65536 + 10, 1000, UseWholeFile: true);
                    VerifySpan(10, 1000, UseWholeFile: false);
                    VerifySpan(65536 + 10, 1000, UseWholeFile: false);
                    //todo: verify mult region starts, > stksize
                    void VerifySpan(long offset, int size, bool UseWholeFile)
                    {
                        var span = map.GetSpan(offset, size, MapRestOfFile: UseWholeFile);
                        for (int pos = 0; pos < size; pos++)
                        {
                            var correctByte = allbytes[offset + (uint)pos];
                            if (map._mapView != null)
                            {
                                var dat = map._mapView.ReadByte(position: pos);
                                Assert.AreEqual(correctByte, dat, $"Mapread should match");
                            }
                            Assert.AreEqual(correctByte, span[pos], $"returned span should match");
                        }
                    }
                    lstFiles.Add(map);
                }
            }
        }

        class MyMap : IDisposable
        {
            private readonly FileInfo _FileInfo;
            FileStream? _fileStream;
            private readonly MemoryMappedFile? _mappedFile;
            public MemoryMappedViewAccessor? _mapView;
            public long Length = 0;
            public MyMap(FileInfo finfo, bool UseFileRead = false)
            {
                _FileInfo = finfo;
                if (UseFileRead)
                {
                    _fileStream = File.OpenRead(finfo.FullName);
                }
                else
                {
                    _mappedFile = MemoryMappedFile.CreateFromFile(finfo.FullName, FileMode.Open, "MyMap1", capacity: 0, access: MemoryMappedFileAccess.Read);
                }
            }
            public ReadOnlySpan<byte> GetSpan(long offset, int size, bool MapRestOfFile, bool UseFileRead = false)
            {
                if (_fileStream != null)
                {
                    var bytes = new byte[size];
                    _fileStream.Seek(offset, SeekOrigin.Begin);
                    _fileStream.Read(bytes);
                    var sp = new Span<byte>(bytes);
                    return sp;
                }
                _mapView?.Dispose();
                if (MapRestOfFile)
                {
                    _mapView = _mappedFile.CreateViewAccessor(offset: offset, size: _FileInfo.Length - offset, MemoryMappedFileAccess.Read);
                }
                else
                {
                    _mapView = _mappedFile.CreateViewAccessor(offset: offset, size: size, MemoryMappedFileAccess.Read);
                }
                unsafe
                {
                    byte* ptr = null;
                    try
                    {
                        //        public const uint AllocationGranularity = 0x10000; //64k
                        // https://github.com/dotnet/runtime/blob/3689fbec921418e496962dc0ee252bdc9eafa3de/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/SafeBuffer.cs
                        _mapView.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr); //SafeMemoryMappedViewHandle gets a handle to the view.  AcquirePointer gets a pointer to the window. Always multiple of Granularity

                        var span = new ReadOnlySpan<byte>(ptr +
                            _mapView.PointerOffset // Gets the number of bytes by which the starting position of this view is offset from the beginning of the memory-mapped file.
                            , size);
                        return span;
                    }
                    finally
                    {
                        _mapView.SafeMemoryMappedViewHandle.ReleasePointer(); // must release pointer else leaks entire mapped section
                    }
                }
            }
            public byte[] GetBytes(long offset, int size)
            {
                var sp = GetSpan(offset, size, MapRestOfFile: true);
                var barr = sp.ToArray();
                return barr;

            }
            public void Dispose()
            {
                _mapView?.Dispose();
                _mappedFile?.Dispose();
                _fileStream?.Dispose();
            }
        }
    }
}