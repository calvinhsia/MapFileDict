using MapFileDict;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
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
            if (args.Length == 0)
            {
                return;
            }
            var pidClient = int.Parse(args[0]);

            //*
            var cts = new CancellationTokenSource();
            using (var oop = new OutOfProc(pidClient, cts.Token)) // we're inproc in the console app, but out of proc to the client
            {
                Trace.WriteLine("CreateServerAsync start");
                await oop.DoServerLoopAsync();
                Trace.WriteLine("CreateServerAsync done");
                /*/
                var tcsStaThread = new TaskCompletionSource<int>();
                var execContext = CreateExecutionContext(tcsStaThread);
                await execContext.Dispatcher.InvokeAsync(async () =>
                {
                    var cts = new CancellationTokenSource();
                    var oop = new OutOfProc(pidClient, OOPOption.InProc, cts.Token); // we're inproc in the console app, but out of proc to the client
                    Trace.WriteLine("CreateServerAsync start");
                    await oop.CreateServerAsync();
                    Trace.WriteLine("CreateServerAsync done");
                    tcsStaThread.SetResult(0);
                });
                await tcsStaThread.Task;
                //*/
            }

            Trace.WriteLine($"Server done!! {nameof(DoMainAsync)}");
        }

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
