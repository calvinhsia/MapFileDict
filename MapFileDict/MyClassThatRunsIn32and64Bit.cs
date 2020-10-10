using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MapFileDict
{
    public class MyClassThatRunsIn32and64bit
    {
        public static void CreateAndRun(string outputLogFile)
        {
            var asm64BitFile = new FileInfo(Path.ChangeExtension("tempasm", ".exe")).FullName;
            if (File.Exists(asm64BitFile))
            {
                File.Delete(asm64BitFile);
            }
            Trace.WriteLine($"Asm = {asm64BitFile}");
            var creator = new AssemblyCreator().CreateAssembly(
                asm64BitFile,
                portableExecutableKinds: System.Reflection.PortableExecutableKinds.PE32Plus, // 64 bit
                imageFileMachine: ImageFileMachine.AMD64,
                AdditionalAssemblyPaths: string.Empty,
                logOutput: true
                );
            var args = $@"""{Assembly.GetAssembly(typeof(MyClassThatRunsIn32and64bit)).Location
                               }"" {nameof(MyClassThatRunsIn32and64bit)} {
                                   nameof(MyClassThatRunsIn32and64bit.MyMainMethod)} ""{outputLogFile}"" ""Executing from 64 bit Asm"" ""64"" true";
            Trace.WriteLine($"args = {args}");
            var p64 = Process.Start(
                asm64BitFile,
                args);
            p64.WaitForExit(30 * 1000);
//            File.Delete(asm64BitFile);
            var result = File.ReadAllText(outputLogFile);
        }
        public static async Task MyMainMethod(string outLogFile, string desc, int intarg, bool boolarg)
        {
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine($"\r\n  {desc} Executing {nameof(MyClassThatRunsIn32and64bit)}.{nameof(MyMainMethod)} Pid={Process.GetCurrentProcess().Id} {Process.GetCurrentProcess().MainModule.FileName}");
                sb.AppendLine($"  IntPtr.Size = {IntPtr.Size} Intarg={intarg} BoolArg={boolarg}");
                if (IntPtr.Size == 8)
                {
                    sb.AppendLine("  We're in 64 bit land!!!");
                }
                else
                {
                    sb.AppendLine("  nothing exciting: 32 bit land");
                }
                long bytesAlloc = 0;
                var lst = new List<byte[]>();
                try
                {
                    var sizeToAlloc = 1024 * 1024;
                    while (true)
                    {
                        var x = new byte[sizeToAlloc];
                        lst.Add(x);
                        bytesAlloc += sizeToAlloc;
                    }
                }
                catch (OutOfMemoryException ex)
                {
                    lst = null;
                    sb.AppendLine($" {ex.Message} after allocating {bytesAlloc / 1024.0 / 1024 / 1024:n2} gigs");
                }
                //                sb.AppendLine($"Allocated {numAllocated} Gigs");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"in {nameof(MyMainMethod)} IntPtr.Size = {IntPtr.Size} Exception {ex}");
                if (IntPtr.Size == 8)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1)); // delay so can observe mem use by other tools (taskman, procexp, etc)
                }
            }
            File.AppendAllText(outLogFile, sb.ToString());
        }
    }
}
