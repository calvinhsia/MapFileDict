# MapFileDict. 2012
Implements interfaces like IDictionary&lt;K,V> and List&lt;T> but with backing store not in the main process memory
I saw a need to be able to access lots of large data, but doing it in managed memory means managed memory gets consumed. 
In a 32 bit process that's limited to 4 Gigs of memory, suppose you want to access much larger data, 
but want to use the same types that you're used to. 
For example, imagine a List<string> or Dictionary<String,String> with millions of entries, totalling multi Gigabytes
Normally the data would be stored in main memory.
MapFileDict allows you to use the system PageFile or a file on disk as backing store
