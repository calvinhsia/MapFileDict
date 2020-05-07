using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MapFileDict
{
    public class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);
        //------------------------------------------------------------------------------
        // CreateFileMapping
        //------------------------------------------------------------------------------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr lpSecurityAttributes, uint flProtect,
            uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);
        //------------------------------------------------------------------------------
        // MapViewOfFile
        //------------------------------------------------------------------------------
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr MapViewOfFile(IntPtr hFileMapping, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, uint dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr MapViewOfFileEx(IntPtr hFileMapping, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, uint dwNumberOfBytesToMap, IntPtr prefAddress);
        //------------------------------------------------------------------------------
        // UnmapViewOfFile
        //------------------------------------------------------------------------------
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void CopyMemory(IntPtr Dest, IntPtr Src, UInt32 length);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto, BestFitMapping = false)]
        public static extern IntPtr CreateFile(string lpFileName,
            UInt32 dwDesiredAccess,
            UInt32 dwShareMode,
            IntPtr lpSecurityAttributes,
            UInt32 dwCreationDisposition,
            UInt32 dwFlagsAndAttributes,
            IntPtr hTemplateFile);


        [Flags]
        public enum Win32FileShare : uint
        {
            FILE_SHARE_NONE = 0x00000000,
            FILE_SHARE_READ = 0x00000001,
            FILE_SHARE_WRITE = 0x00000002,
            FILE_SHARE_DELETE = 0x00000004
        }
        public enum Win32CreationDisposition : uint
        {
            CREATE_NEW = 1,
            CREATE_ALWAYS = 2,
            OPEN_EXISTING = 3,
            OPEN_ALWAYS = 4,
            TRUNCATE_EXISTING = 5
        }

        public const int PAGE_NOACCESS = 0x01;
        public const int PAGE_READWRITE = 0x04;
        public const int MEM_COMMIT = 0x1000;
        public const int MEM_RELEASE = 0x8000;
        public const int MEM_FREE = 0x10000;

        public const uint GENERIC_WRITE = 0x40000000;
        public const uint GENERIC_READ = 0x80000000;
        public const int PAGE_READONLY = 0x02;
        public const int FILE_MAP_READ = 0x04;
        public const int FILE_MAP_WRITE = 0x0002;
        public const int FILE_TYPE_DISK = 0x01;
        public const int SE_ERR_ACCESSDENIED = 5;
        public const int ERROR_ALREADY_EXISTS = 183;
        public static IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public const int FILE_ATTRIBUTE_TEMPORARY = 0x100;

    }
}
