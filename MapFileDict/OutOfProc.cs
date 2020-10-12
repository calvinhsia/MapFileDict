using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MapFileDict
{
    public enum Verbs
    {
        verbQuit, // len =1 byte: 0 args
        verbString, // 
        verbStringSharedMem,
        verbSpeedTest,
        verbRequestData, // len = 1 byte: 0 args
        verbSendObjAndReferences, // a single obj and a list of it's references
    }
    public class OutOfProc
    {
        public int chunkSize;
        public byte[] speedBuf;
        public string pipeName;
        public CancellationTokenSource cts;
        public IntPtr mappedSection;
        public uint sharedMapSize;
        public string sharedFileMapName;
        public MemoryMappedFile mmf;
        public MemoryMappedViewAccessor mmfView;
        public OutOfProc(int chunkSize = 100)
        {
            this.chunkSize = chunkSize;
            sharedFileMapName = $"MapFileDict{Process.GetCurrentProcess().Id}\0";
            sharedMapSize = 65536U;
            mmf = MemoryMappedFile.CreateNew(
               mapName: sharedFileMapName,
               capacity: sharedMapSize,
               access: MemoryMappedFileAccess.ReadWrite,
               options: MemoryMappedFileOptions.None,
               inheritability: HandleInheritability.None
               );
            mmfView = mmf.CreateViewAccessor(
               offset: 0,
               size: 0,
               access: MemoryMappedFileAccess.ReadWrite);
            mappedSection = mmfView.SafeMemoryMappedViewHandle.DangerousGetHandle();
            cts = new CancellationTokenSource();
            pipeName = "MyTestPipe";
            speedBuf = new byte[chunkSize];
        }
        public Task CreateServer(string pipeName, IntPtr mappedSection, CancellationToken token)
        {
            var taskServer = Task.Run(async () =>
             {
                 try
                 {
                     Trace.WriteLine("Server: Starting ");
                     using (var pipeServer = new NamedPipeServerStream(
                         pipeName: pipeName,
                         direction: PipeDirection.InOut,
                         maxNumberOfServerInstances: 1,
                         transmissionMode: PipeTransmissionMode.Message,
                         options: PipeOptions.Asynchronous
                         ))
                     {
                         await pipeServer.WaitForConnectionAsync(token);

                         var buff = new byte[100];
                         var receivedQuit = false;
                         using (var ctsReg = token.Register(
                                () => { pipeServer.Disconnect(); Trace.WriteLine("Cancel: disconnect pipe"); }))
                         {
                             while (!receivedQuit)
                             {
                                 if (token.IsCancellationRequested)
                                 {
                                     Trace.WriteLine("server: got cancel");
                                     receivedQuit = true;
                                 }
                                 var nBytesRead = await pipeServer.ReadAsync(buff, 0, 1, token);
                                 switch ((Verbs)buff[0])
                                 {
                                     case Verbs.verbQuit:
                                         Trace.WriteLine($"Server got quit message");
                                         receivedQuit = true;
                                         break;
                                     case Verbs.verbStringSharedMem:
                                         var len = Marshal.ReadIntPtr(mappedSection);
                                         var str = Marshal.PtrToStringAnsi(mappedSection + IntPtr.Size, len.ToInt32());
                                         Trace.WriteLine($"Server: SharedMemStr {str}");
                                         break;
                                     case Verbs.verbString:
                                         var lstBytes = new List<byte>();
                                         while (!pipeServer.IsMessageComplete)
                                         {
                                             var byt = pipeServer.ReadByte();
                                             lstBytes.Add((byte)byt);
                                         }
                                         //var bufLen = buff[1];
                                         //var strbuf = new byte[bufLen];
                                         //await pipeServer.ReadAsync(strbuf, 0, bufLen, cts.Token);
                                         var strRead = Encoding.ASCII.GetString(lstBytes.ToArray());
                                         Trace.WriteLine($"Server Got str {strRead}");
                                         break;
                                     case Verbs.verbRequestData:
                                         var strB = Encoding.ASCII.GetBytes($"Server: {DateTime.Now}");
                                         await pipeServer.WriteAsync(strB, 0, strB.Length);
                                         break;
                                     case Verbs.verbSpeedTest:
                                         {
                                             await pipeServer.ReadAsync(speedBuf, 0, chunkSize - 1);
                                             Trace.WriteLine($"Server: got bytes {chunkSize:n0}");
                                         }
                                         break;
                                 }
                             }
                         }
                     }
                 }
                 catch (IOException ex)
                 {
                     Trace.WriteLine($"Server: IOException" + ex.ToString());
                     throw;
                 }
                 catch (OperationCanceledException)
                 {
                     Trace.WriteLine("Server: cancelled");
                 }
                 Trace.WriteLine("Server: exiting servertask");

             });
            return taskServer;
        }
    }
}
