using MapFileDict;
using System;
using System.Collections.Generic;
using System.Linq;
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
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using System.Collections;

namespace MapFileUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        IDictionary<int, someclass> _mfd = null;
        TextBox _txtboxStatus;
        DateTime _dtStart;
        Button _btnStartStop = null;
        bool _runningCode = false;
        Random _random = new Random(1);
        int _uiThread;
        int _MaxItems = 100000;

        List<int> _mylist = new List<int>();
        public MainWindow()
        {
            InitializeComponent();

            _uiThread = Thread.CurrentThread.ManagedThreadId;
            this.Closed += (oC, eC) =>
                {
                    _runningCode = false;
                };
            //*
            this.Top = 0;
            this.Width = 1500;
            this.Height = 900;
            /*/
            this.WindowState = System.Windows.WindowState.Maximized;
             * //*/
            this.Loaded += (ol, el) =>
                {
                    try
                    {
                        var args = Environment.GetCommandLineArgs();

                        var nInstances = 100000;
                        if (args.Length > 1)
                        {
                            nInstances = int.Parse(args[1]);
                        }
                        var spHoriz = new StackPanel() { Orientation = System.Windows.Controls.Orientation.Horizontal };
                        var spVert = new StackPanel() { Orientation = System.Windows.Controls.Orientation.Vertical };
                        spVert.Children.Add(spHoriz);
                        _btnStartStop = new Button() { Content = "_Start", Height = 30, Width = 100 };
                        spHoriz.Children.Add(_btnStartStop);
                        var chkUseMapFile = new CheckBox() { Content = "_Use Mapfile", IsChecked = true, Width = 100 };
                        spHoriz.Children.Add(chkUseMapFile);
                        var chkDoDeletes = new CheckBox() { Content = "_Deletes", IsChecked = false, Width = 100 };
                        spHoriz.Children.Add(chkDoDeletes);
                        _txtboxStatus = new TextBox()
                        {
                            MaxLines = 45,
                            Background = Brushes.AliceBlue,
                            AcceptsReturn = true,
                            AcceptsTab = true,
                            IsReadOnly = true,
                            FontFamily=new FontFamily("Consolas"),
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                        };
                        spVert.Children.Add(_txtboxStatus);
                        var strSize = 10000;
                        this.Content = spVert;
                        _btnStartStop.Click += (ob, eb) =>
                            {
                                if (!_runningCode)
                                {
                                    _btnStartStop.Content = "_Stop";
                                    _nBytesAlloc = 0;
                                    _dtStart = DateTime.Now;
                                    _runningCode = true;
                                    chkDoDeletes.IsEnabled = false;
                                    chkUseMapFile.IsEnabled = false;
                                    var fDoDeletes = chkDoDeletes.IsChecked.Value;
                                    var fUseMapFile = chkUseMapFile.IsChecked.Value;
                                    if (fUseMapFile)
                                    {
                                        //*

                                        var mfd = new MapFileDict.MapFileDict<int, someclass>();
                                        _mfd = mfd;
                                        mfd._MemMap.ChangeViewSize(65536 * 2);
                                        /*/
                                        _mfd = new MapFileDict.MapFileDict<int, someclass>(
                                            ulInitialSize: (ulong)MemMap.AllocationGranularity * 16 * 1024,
                                            uiViewSize: 256 * MemMap.AllocationGranularity
                                            );
                                         //*/
                                    }
                                    else
                                    {
                                        _mfd = new Dictionary<int, someclass>();
                                    }
                                    if (fDoDeletes)
                                    {
                                        LogStatus(string.Format("doing deletes {0}", nInstances));
                                        for (int i = 0; i < nInstances; i++)
                                        {
                                            var rnd = _random.Next(strSize);
                                            _nBytesAlloc += rnd * 2 + 60;
                                            var test = new someclass() { int1 = i, str5 = new string('a', rnd) };
                                            _mfd[i] = test;
                                        }
                                        var newstr = string.Empty;
                                        DoWork(_mfd, (int ndx) =>
                                            {
                                                var i = ndx % _mfd.Count;
                                                var cycle = ((int)((ndx - i) / _mfd.Count)) % 2;
                                                var retrieved = _mfd[i];
                                                _mfd.Remove(i);
                                                if (cycle == 1)
                                                {
                                                    var rnd = _random.Next(strSize);
                                                    newstr = new string('a', rnd);
                                                }
                                                if (fUseMapFile)
                                                {
                                                    //var memmap = ((MapFileDict<int, someclass>)_mfd)._MemMap;
                                                    //memmap.AddData(newstr);
                                                }
                                                retrieved.str5 = newstr;
                                                _nBytesAlloc += newstr.Length * 2 + 60;
                                                _mfd[i] = retrieved;
                                            }
                                        );
                                    }
                                    else
                                    {
                                        DoWork(_mfd, (int i) =>
                                                {
                                                    //var i = _mfd.Count == 0 ? 0 : (ndx % _mfd.Count);
                                                    //i = ndx;
                                                    var rnd = _random.Next(strSize);
                                                    var test = new someclass() { int1 = i, str5 = new string('a', rnd) };
                                                    _mfd[i] = test;
                                                    _nBytesAlloc += rnd * 2 + 60;
                                                }
                                            );
                                    }
                                }
                                else
                                {
                                    LogStatus("Stopping work " + DateTime.Now.ToLongTimeString());
                                    _btnStartStop.Content = "_Start";
                                    chkDoDeletes.IsEnabled = true;
                                    chkUseMapFile.IsEnabled = true;
                                    GC.Collect();
                                    _runningCode = false;
                                }

                            };
#if DEBUG
#endif
                        _btnStartStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    }
                    catch (Exception ex)
                    {
                        this.Content = ex.ToString();
                    }
                };
        }

        private void CloseDict()
        {
            if (_mfd != null)
            {
                if (_mfd as MapFileDict<int, someclass> != null)
                {
                    var mfd = (MapFileDict<int, someclass>)_mfd;
                    if (_mfd != null)
                    {
                        mfd.Dispose();
                    }
                }
                _mfd = null;
            }
        }

        long _nBytesAlloc = 0;

        void DoWork(IDictionary<int, someclass> mfd, Action<int> Work)
        {
            ThreadPool.QueueUserWorkItem((oThread) =>
                {
                    LogStatus("Starting Work " + DateTime.Now.ToLongTimeString());
                    try
                    {
                        var pid = Process.GetCurrentProcess().Id;

                        PerformanceCounter perfCounterPrivateBytes = GetPerfCounter("Process", "Private Bytes", "ID Process", pid, "MapFileUI");
                        PerformanceCounter perfCounterGCBytes = GetPerfCounter(".NET CLR Memory", "# Bytes in all Heaps", "Process ID", pid, "MapFileUI");
                        PerformanceCounter perfCounterVirtualBytes = GetPerfCounter("Process", "Virtual Bytes", "ID Process", pid, "MapFileUI");
                        //                    PerformanceCounter perfCounterGC = GetPerfCounterForVS(".NET CLR Memory", "# Gen 0 Collections", "ID Process", _procVS.Id);
                        while (_runningCode)
                        {
                            GC.Collect();
                            for (int i = 0; _runningCode; i++)
                            {
                                Work(i);
                                if (i == _MaxItems)
                                {
                                    _mfd.Clear();
                                    _nBytesAlloc = 0;
                                    LogStatus("Clearing");
                                    GC.Collect();
                                    Thread.Sleep(1000);
                                    break;
                                }

                                if (i % 10000 == 0)
                                {
                                    float nGCStart = perfCounterGCBytes.NextValue();
                                    float nPrivByteStart = perfCounterPrivateBytes.NextValue();
                                    float nVirtByteStart = perfCounterVirtualBytes.NextValue();
                                    var stat = string.Format("GCSize={0:n0}  PrivB={1:n0}  VirtB={2:n0} DictCnt={3:n0} Bytes={4:n0}",
                                        (int)nGCStart,
                                        (int)nPrivByteStart,
                                        (int)nVirtByteStart,
                                        _mfd.Count,
                                        _nBytesAlloc
                                        );

                                    if (mfd is MapFileDict<int, someclass>)
                                    {
                                        var amfd = (MapFileDict<int, someclass>)mfd;
                                        stat += amfd._MemMap._stats.ToString();
                                    }
                                    LogStatus(stat);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogStatus(ex.ToString());
                        //_btnStartStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); ' wrong thread
                    }
                    finally
                    {
                        CloseDict();
                    }
                });
        }

        public void LogStatus(string stat)
        {
            var deltastr = string.Empty;
            var dtNow = DateTime.Now;
            if (_dtStart != null)
            {
                deltastr = string.Format(@"{0:h\:mm\:ss\.fff}", (dtNow - _dtStart)); //"0:49:12.842"
            }
            var msg = String.Format(
                "{0:T} {1}", //like "11:52:37 PM"
                deltastr,
                stat);
            //  we ask the dispatcher of the txtbox to 
            //  update the txtbox on the UI thread
            _txtboxStatus.Dispatcher.
              BeginInvoke(new Action<string>((p1) =>
              {
                  _txtboxStatus.Text += p1 + "\r\n";
                  _txtboxStatus.ScrollToEnd();
              }), msg);
            if (Thread.CurrentThread.ManagedThreadId == _uiThread)
            {
                this.Dispatcher.Invoke(
                    DispatcherPriority.Render,
                    (Action)(() => { }));
            }
        }

        public PerformanceCounter GetPerfCounter(string perfcountCat, string perfcountName, string pidstr, int pid, string inst)
        {

            PerformanceCounter pc = null;
            while (true)
            {
                using (var cntr = new PerformanceCounter(perfcountCat, pidstr, Process.GetCurrentProcess().ProcessName, readOnly: true))
                {
                    try
                    {
                        var val = (int)cntr.NextValue();
                        if (val == pid)
                        {
                            pc = new PerformanceCounter(perfcountCat, perfcountName, inst);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogStatus(ex.ToString());
                        // System.InvalidOperationException: Instance 'IntelliTrace' does not exist in the specified Category.
                        //                        LogString("Exception {0}", ex.ToString());
                    }
                }
            }
            return pc;
        }


    }
    public class someclass
    {
        public int int1;
        public uint uint2;
        public long long3;
        public ulong ulong4;
        public string str5;
        public string strbig;
        public int basenum;
        //    public refclass _refclass;
        public someclass()
        {
            //      _refclass = new refclass() { strmem = "refstring" };
        }
        public override string ToString()
        {
            return string.Format("{0} {1} {2} {3}", str5, basenum, int1, uint2, long3);
        }
    }
    public class refclass
    {
        public string strmem;
    }
}
