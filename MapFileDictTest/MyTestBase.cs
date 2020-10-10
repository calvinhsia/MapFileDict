using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MapFileDictTest
{
    public class MyTestBase
    {
        internal MyTextWriterTraceListener MyTextWriterTraceListener;

        public TestContext TestContext { get; set; }
        public const string ContextPropertyLogFile = "LogFileName";
        [TestInitialize]
        public void TestInitialize()
        {
            var outfile = System.Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Desktop\TestOutput.txt");
            TestContext.Properties[ContextPropertyLogFile] = outfile;
            this.MyTextWriterTraceListener = new MyTextWriterTraceListener(
                outfile,
                TestContext,
                MyTextWriterTraceListener.MyTextWriterTraceListenerOptions.OutputToFileAsync | MyTextWriterTraceListener.MyTextWriterTraceListenerOptions.AddDateTime
                );
        }
        [TestCleanup]
        public void TestCleanup()
        {
            MyTextWriterTraceListener.Dispose();
        }
        public void VerifyLogStrings(string strings)
        {
            this.MyTextWriterTraceListener.VerifyLogStrings(strings);
        }
    }
    public class MyTextWriterTraceListener : TextWriterTraceListener, IDisposable
    {
        private readonly string LogFileName;
        private TestContext testContext;
        private readonly MyTextWriterTraceListenerOptions options;
        public List<string> _lstLoggedStrings;
        ConcurrentQueue<string> _qLogStrings;

        private Task taskOutput;
        private CancellationTokenSource ctsBatchProcessor;

        [Flags]
        public enum MyTextWriterTraceListenerOptions
        {
            None = 0x0,
            AddDateTime = 0x1,
            /// <summary>
            /// Some tests take a long time and if they fail, it's difficult to examine any output
            /// Also, getting the test output is very cumbersome: need to click on the Additional Output, then right click/Copy All output, then paste it somewhere
            /// Plus there's a bug that the right-click/copy all didn't copy all in many versions.
            /// Turn this on to output to a file in real time. Open the file in VS to watch the test progress
            /// </summary>
            OutputToFile = 0x2,
            /// <summary>
            /// Output to file while running takes a long time. Do it in batches every second. Ensure disposed
            /// so much faster (20000 lines takes minutes vs <500 ms)
            /// </summary>
            OutputToFileAsync = 0x4
        }

        public MyTextWriterTraceListener(string LogFileName, TestContext testContext, MyTextWriterTraceListenerOptions options = MyTextWriterTraceListenerOptions.OutputToFileAsync)
        {
            if (string.IsNullOrEmpty(LogFileName))
            {
                throw new InvalidOperationException("Log filename is null");
            }
            this.LogFileName = LogFileName;
            this.testContext = testContext;
            this.options = options;
            _lstLoggedStrings = new List<string>();
            OutputToLogFileWithRetryAsync(() =>
            {
                File.WriteAllText(LogFileName, string.Empty);
            }).Wait();
            File.Delete(LogFileName);
            Trace.Listeners.Clear(); // else Debug.Writeline can cause infinite recursion because the Test runner adds a listener.
            Trace.Listeners.Add(this);
            if (this.options.HasFlag(MyTextWriterTraceListenerOptions.OutputToFileAsync))
            {
                _qLogStrings = new ConcurrentQueue<string>();
                this.taskOutput = Task.Run(async () =>
                {
                    try
                    {
                        this.ctsBatchProcessor = new CancellationTokenSource();
                        bool fShutdown = false;
                        while (!fShutdown)
                        {
                            if (this.ctsBatchProcessor.IsCancellationRequested)
                            {
                                fShutdown = true; // we need to go through one last time to clean up
                            }
                            var lstBatch = new List<string>();
                            while (!_qLogStrings.IsEmpty)
                            {
                                if (_qLogStrings.TryDequeue(out var msg))
                                {
                                    lstBatch.Add(msg);
                                }
                            }
                            if (lstBatch.Count > 0)
                            {
                                await this.OutputToLogFileWithRetryAsync(() =>
                                {
                                    File.AppendAllLines(LogFileName, lstBatch);
                                });
                            }
                            try
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1), this.ctsBatchProcessor.Token);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });
            }
        }

        public override void Write(object o)
        {
            Write(o.ToString());
        }
        public override void Write(string message)
        {
            _lstLoggedStrings.Add(message);
            var dt = string.Empty;
            if (this.options.HasFlag(MyTextWriterTraceListenerOptions.AddDateTime))
            {
                dt = string.Format("[{0}],",
                    DateTime.Now.ToString("hh:mm:ss:fff")
                    ) + $"{Thread.CurrentThread.ManagedThreadId,2} ";
            }
            message = dt + message.Replace("{", "{{").Replace("}", "}}");
            this.testContext.WriteLine(message);
            if (this.options.HasFlag(MyTextWriterTraceListenerOptions.OutputToFileAsync))
            {
                _qLogStrings.Enqueue(message);
            }

            if (this.options.HasFlag(MyTextWriterTraceListenerOptions.OutputToFile))
            {
                try
                {
                    if (this.taskOutput != null)
                    {
                        this.taskOutput.Wait();
                    }
                    if (!this.taskOutput.IsCompleted) // faulted?
                    {
                        Trace.WriteLine(("Output Task faulted"));
                    }
                    this.taskOutput = Task.Run(async () =>
                    {
                        await OutputToLogFileWithRetryAsync(() =>
                        {
                            File.AppendAllText(LogFileName, message + Environment.NewLine);
                        });
                    });
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        public async Task OutputToLogFileWithRetryAsync(Action actWrite)
        {
            var nRetry = 0;
            var success = false;
            while (nRetry++ < 10)
            {
                try
                {
                    actWrite();
                    success = true;
                    break;
                }
                catch (IOException)
                {
                }
                await Task.Delay(TimeSpan.FromSeconds(0.3));
            }
            if (!success)
            {
                testContext.WriteLine($"Error writing to log #retries ={nRetry}");
            }
        }

        public override void WriteLine(object o)
        {
            Write(o.ToString());
        }
        public override void WriteLine(string message)
        {
            Write(message);
        }
        public void WriteLine(string str, params object[] args)
        {
            Write(string.Format(str, args));
        }
        public int VerifyLogStrings(IEnumerable<string> strsExpected, bool ignoreCase = false)
        {
            int numFailures = 0;
            var firstFailure = string.Empty;
            Func<string, string, bool> IsIt = (strExpected, strActual) =>
            {
                var hasit = false;
                if (!string.IsNullOrEmpty(strActual))
                {
                    if (ignoreCase)
                    {
                        hasit = strActual.ToLower().Contains(strExpected.ToLower());
                    }
                    else
                    {
                        hasit = strActual.Contains(strExpected);
                    }
                }
                return hasit;
            };
            foreach (var str in strsExpected)
            {
                if (!_lstLoggedStrings.Where(s => IsIt(str, s)).Any())
                {
                    numFailures++;
                    if (string.IsNullOrEmpty(firstFailure))
                    {
                        firstFailure = str;
                    }
                    WriteLine($"Expected '{str}'");
                }
            }
            Assert.AreEqual(0, numFailures, $"1st failure= '{firstFailure}'");
            return numFailures;
        }

        public int VerifyLogStrings(string strings, bool ignoreCase = false)
        {
            var strs = strings.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return VerifyLogStrings(strs, ignoreCase);
        }
        protected override void Dispose(bool disposing)
        {
            Trace.Listeners.Remove(this);
            if (this.taskOutput != null)
            {
                this.ctsBatchProcessor.Cancel();
                this.taskOutput.Wait();
            }
            base.Dispose(disposing);
        }
    }
}