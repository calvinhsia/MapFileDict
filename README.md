# MapFileDict. 2012
Implements interfaces like IDictionary&lt;K,V> and List&lt;T> but with backing store not in the main process memory
I saw a need to be able to access lots of large data, but doing it in managed memory means managed memory gets consumed. 
In a 32 bit process that's limited to 4 Gigs of memory, suppose you want to access much larger data, 
but want to use the same types that you're used to. 
For example, imagine a List<string> or Dictionary<String,String> with millions of entries, totalling multi Gigabytes
Normally the data would be stored in main memory.
MapFileDict allows you to use the system PageFile or a file on disk as backing store
Used successfully in cases where the data being stored is far larger than the infrastructure: 
e.g. if the dictionary has a milliion items of 1 byte in length, it's not advantageous.
If there are thousands of things that are large, then it saves a lot of memory.
The data types must be simple: e.g. string, integer, datetime: object references are not allowed.
So for example, the list of all the intellisense data method, like "Console.WriteLine()". Why should this be stored in main memory?


2020: Can create a child process (possibly 64 bit) which loads the same assembly (the asm runs in both processes: build as AnyCPU)
and it communicates via named pipes. This takes advantage of the fact that a .Net assembly can run in both 64 and 32 bit processes.
For example, the main process can create a server process, and send commands (verbs) to the server
process to execute, and send results back in either shared memory (each process has a region in shared address space region: instantaneous)
or named pipe (very very fast)
See OutOfProc.cs where the verbs are defined and the client and server code for each verb are side by side for easy authoring.
The named pipe communication is raw byte (faster), but additional serializations can be used (slower) such as JSonRPC, XML, Binary, etc.
For example, examining a dump file, it's easy to get an object from the CLR Heap, and all the objects that the object 
directly references (direct children). However, given a child, how can we get the parent(s)? 
This is really useful for finding leaks.
So the client sends the object graph and the server inverts the graph in a new dict (this takes a huge amount of memory). 
Now querying why FOO is in memory is a quick look up the parent chain (e.g. a verb QueryParentOfObject)
