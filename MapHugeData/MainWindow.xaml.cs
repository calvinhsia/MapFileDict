using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace MapHugeData
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public bool TouchMemory { get; set; } = false;
        public bool OpenReadOnly { get; set; } = false;
        public string TxtPath { get; set; } = @"c:\users\calvinh\source\repos";
        public long MaxToMapGigs { get; set; } = 4;
        bool IsRunning = false;
        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;
            var mems = typeof(System.IO.FileAccess).GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        }
        List<MyMap> lstFiles = new List<MyMap>();
        async void BtnGoClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var stat = string.Empty;
                IsRunning = !IsRunning;
                btnGo.IsEnabled = false;
                if (IsRunning)
                {
                    btnGo.Content = "Release mem";
                    tbxStatus.Text = "Mapping Mem";
                    await Task.Run(() =>
                    {
                        var proc = Process.GetCurrentProcess();
                        var totSize = 0L;
                        var PagedMemorySize64start = proc.PagedMemorySize64;
                        var wsstart = proc.WorkingSet64;
                        foreach (var file in Directory.EnumerateFiles(TxtPath, "*.cs", new EnumerationOptions() { RecurseSubdirectories = true }))
                        {
                            var finfo = new FileInfo(file);
                            if (finfo.Length > 0)
                            {
                                var m = new MyMap(finfo, TouchMemory, OpenReadOnly);
                                totSize += m.Length;
                                lstFiles.Add(m);
                                if (MaxToMapGigs != 0 && totSize > MaxToMapGigs * 1024 * 1024 * 1024)
                                {
                                    break;
                                }
                            }
                        }
                        proc.Refresh();
                        var wsend = proc.WorkingSet64;
                        var PagedMemorySize64End = proc.PagedMemorySize64;

                        var pfdelt = PagedMemorySize64End - PagedMemorySize64start;
                        var wsdelt = wsend - wsstart;
                        stat = $"Start GetPageFileUsage={PagedMemorySize64start:n0}   WS = {wsstart:n0}\n" +
                            $"End   PagedMemorySize64={PagedMemorySize64End:n0}   WS={wsend:n0}\n" +
                            $"Delta PagedMemorySize64={pfdelt:n0} WS={wsdelt:n0}\n" +
                            $" FileCnt= {lstFiles.Count:n0}   MaptotSize= {totSize:n0}  TouchMemory= {TouchMemory}\n";
                    });
                    tbxStatus.Text = stat;
                }
                else
                {
                    foreach (var f in lstFiles)
                    {
                        f.Dispose();
                    }
                    lstFiles.Clear();
                    tbxStatus.Text += $"Released memory";
                    btnGo.Content = "Go";
                }
            }
            catch (Exception)
            {
            }
            btnGo.IsEnabled = true;
        }
        static ulong GetPageFileUsage()
        {
            var retVal = 0ul;
            if (IntPtr.Size == 8)
            {
                var ctrs = default(PROCESS_MEMORY_COUNTERS_64);
                if (GetProcessMemoryInfo64(Process.GetCurrentProcess().Handle, out ctrs, Marshal.SizeOf(ctrs)) == 0)
                {
                    var err = Marshal.GetLastWin32Error();
                }
                retVal = ctrs.PagefileUsage;
            }
            else
            {
                var ctrs = default(PROCESS_MEMORY_COUNTERS);
                if (GetProcessMemoryInfo(Process.GetCurrentProcess().Handle, out ctrs, Marshal.SizeOf(ctrs)) == 0)
                {
                    var err = Marshal.GetLastWin32Error();
                }
                retVal = ctrs.PagefileUsage;
            }
            return retVal;
        }

        [StructLayout(LayoutKind.Sequential, Size = 40)]
        internal struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public uint PeakWorkingSetSize;
            public uint WorkingSetSize;
            public uint QuotaPeakPagedPoolUsage;
            public uint QuotaPagedPoolUsage;
            public uint QuotaPeakNonPagedPoolUsage;
            public uint QuotaNonPagedPoolUsage;
            public uint PagefileUsage;
            public uint PeakPagefileUsage;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_MEMORY_COUNTERS_64
        {
            public uint cb;
            public uint PageFaultCount;
            public ulong PeakWorkingSetSize;
            public ulong WorkingSetSize;
            public ulong QuotaPeakPagedPoolUsage;
            public ulong QuotaPagedPoolUsage;
            public ulong QuotaPeakNonPagedPoolUsage;
            public ulong QuotaNonPagedPoolUsage;
            public ulong PagefileUsage;
            public ulong PeakPagefileUsage;
        }

        [DllImport("psapi.dll", SetLastError = true, EntryPoint = "GetProcessMemoryInfo")]
        internal static extern int GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, int size);

        [DllImport("psapi.dll", SetLastError = true, EntryPoint = "GetProcessMemoryInfo")]
        internal static extern int GetProcessMemoryInfo64(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS_64 counters, int size);

        class MyMap : IDisposable
        {
            private readonly FileInfo _FileInfo;
            private readonly MemoryMappedFile? _mappedFile;
            private readonly MemoryMappedViewAccessor? _mapView;
            public long Length = 0;
            public MyMap(FileInfo finfo, bool TouchMemory, bool OpenReadOnly)
            {
                _FileInfo = finfo;
                try
                {
                    if (OpenReadOnly)
                    {
                        _mappedFile = MemoryMappedFile.CreateFromFile(finfo.FullName, FileMode.Open, mapName: null, capacity: 0, access: MemoryMappedFileAccess.Read);
                    }
                    else
                    {
                        _mappedFile = MemoryMappedFile.CreateFromFile(finfo.FullName, FileMode.Open);
                    }
                    _mapView = _mappedFile.CreateViewAccessor(offset: 0, size: _FileInfo.Length, MemoryMappedFileAccess.Read);
                    Length = _FileInfo.Length;
                    if (TouchMemory)
                    {
                        for (long pos = 0; pos < Length; pos += 4096)
                        {
                            if (pos + 8 >= Length)
                            {
                                break;
                            }
                            var dat = _mapView.ReadUInt64(position: pos);
                        }
                    }
                }
                catch (Exception)
                {

                }
            }

            public void Dispose()
            {
                _mapView?.Dispose();
                _mappedFile?.Dispose();
            }
        }
    }

}
