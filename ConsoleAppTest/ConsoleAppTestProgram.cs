using MapFileDict;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ConsoleAppTest
{
    class ConsoleAppTestProgram
    {
        static void Main(string[] args)
        {
            var myMain = new ConsoleAppTestProgram();
            myMain.DoMainAsync(args).Wait();
        }

        private async Task DoMainAsync(string[] args)
        {
            //            MessageBox(0, $"Attach a debugger if desired {Process.GetCurrentProcess().Id} {Process.GetCurrentProcess().MainModule.FileName}", "Server Process", 0);
            if (args.Length != 2)
            {
                return;
            }
            var pidClient = int.Parse(args[0]);
            var typeToInstantiateName = args[1];
            var cts = new CancellationTokenSource();
            OutOfProcBase oop = null;
            try
            {
                var typeToInstantiate = typeof(OutOfProc).Assembly.GetTypes().Where(t => t.Name == typeToInstantiateName).FirstOrDefault();
                var argsToPass = new object[] { new OutOfProcOptions() { PidClient = pidClient }, new CancellationToken() };
                oop = (OutOfProcBase)Activator.CreateInstance(typeToInstantiate, argsToPass);
                Trace.WriteLine($"DoServerLoopAsync start");
                await oop.DoServerLoopTask;
                Trace.WriteLine("{nameof(oop.DoServerLoopAsync)} done");
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.ToString());
            }
            finally
            {
                oop?.Dispose();
            }
            Trace.WriteLine($"Server done!! {nameof(DoMainAsync)}");
        }
        [DllImport("user32")]
        public static extern int MessageBox(int hWnd, String text, String caption, uint type);

        MyExecutionContext CreateExecutionContext(TaskCompletionSource<int> tcsStaThread)
        {
            const string Threadname = "MyStaThread";
            var tcsGetExecutionContext = new TaskCompletionSource<MyExecutionContext>();

            Trace.WriteLine($"Creating {Threadname}");
            var myStaThread = new Thread(() =>
            {
                // Create the context, and install it:
                Trace.WriteLine($"{Threadname} start");
                var dispatcher = Dispatcher.CurrentDispatcher;
                var syncContext = new DispatcherSynchronizationContext(dispatcher);

                SynchronizationContext.SetSynchronizationContext(syncContext);

                tcsGetExecutionContext.SetResult(new MyExecutionContext
                {
                    DispatcherSynchronizationContext = syncContext,
                    Dispatcher = dispatcher
                });

                // Start the Dispatcher Processing
                Trace.WriteLine($"MyStaThread before Dispatcher.run");
                Dispatcher.Run();
                Trace.WriteLine($"MyStaThread After Dispatcher.run");
                tcsStaThread.SetResult(0);
            })
            {

                //            myStaThread.SetApartmentState(ApartmentState.STA);
                Name = Threadname
            };
            myStaThread.Start();
            Trace.WriteLine($"Starting {Threadname}");
            return tcsGetExecutionContext.Task.Result;
        }

        public class MyExecutionContext
        {
            public DispatcherSynchronizationContext DispatcherSynchronizationContext { get; set; }
            public Dispatcher Dispatcher { get; set; }
        }
    }
}
