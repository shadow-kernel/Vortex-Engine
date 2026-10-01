using System;
using System.Runtime.InteropServices;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>
    /// Move a file or folder to the macOS Trash through NSFileManager (<c>trashItemAtURL:resultingItemURL:error:</c>),
    /// which picks the right Trash for the volume and keeps Finder's "Put Back" information. The resulting path is
    /// returned so the delete stays undoable (Undo moves the item back) without holding the file contents in memory.
    /// </summary>
    internal static class MacTrash
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgId(IntPtr self, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgIdPtr(IntPtr self, IntPtr sel, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool MsgTrash(IntPtr self, IntPtr sel, IntPtr url, out IntPtr resultingUrl, out IntPtr error);

        public static bool IsSupported => OperatingSystem.IsMacOS();

        /// <summary>Trash <paramref name="path"/>; returns the item's new path inside the Trash, or null on failure.</summary>
        public static string Trash(string path)
        {
            if (!IsSupported || string.IsNullOrEmpty(path)) return null;
            IntPtr utf8 = IntPtr.Zero;
            try
            {
                utf8 = Marshal.StringToCoTaskMemUTF8(path);
                IntPtr nsString = MsgIdPtr(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
                if (nsString == IntPtr.Zero) return null;
                IntPtr url = MsgIdPtr(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), nsString);
                IntPtr fm = MsgId(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));
                if (url == IntPtr.Zero || fm == IntPtr.Zero) return null;
                if (!MsgTrash(fm, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, out IntPtr result, out IntPtr error) || result == IntPtr.Zero)
                    return null;
                IntPtr resultPath = MsgId(result, sel_registerName("path"));
                IntPtr cstr = resultPath == IntPtr.Zero ? IntPtr.Zero : MsgId(resultPath, sel_registerName("UTF8String"));
                return cstr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(cstr);
            }
            catch { return null; }
            finally { if (utf8 != IntPtr.Zero) Marshal.FreeCoTaskMem(utf8); }
        }
    }
}
