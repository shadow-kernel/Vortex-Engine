using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Editor.Scripting
{
    /// <summary>
    /// Reads a Sony DualSense / DualShock 4 directly from its HID report — the deterministic fallback used when
    /// Windows.Gaming.Input doesn't surface the pad (so a PS5 controller "just works" over USB with no Steam Input /
    /// DS4Windows). A background thread does the blocking ReadFile; the game thread reads the latest parsed state.
    /// USB (report 0x01) is fully supported; Bluetooth full-mode (0x31) too. Never throws to the caller.
    /// </summary>
    internal static class DualSenseHid
    {
        // normalized latest state (game thread reads these)
        public static volatile bool Connected;
        public static float LX, LY, RX, RY, L2, R2;
        public static ushort Buttons;   // same bitmask as Vortex.Input (A=0x1000 …, dpad 0x0001.. , L1 0x0100 …)

        private static SafeFileHandle _handle;
        private static Thread _thread;
        private static volatile byte[] _report;
        private static int _reportLen;
        private static bool _tried;
        private static readonly object _lock = new object();

        /// <summary>Ensure the device is open + being read, then parse the latest report. Cheap after the first call.</summary>
        public static bool Poll()
        {
            if (!_tried) { _tried = true; try { OpenAndStart(); } catch { Connected = false; } }
            if (!Connected) { TryReopenOccasionally(); }
            var rep = _report;
            if (rep == null || !Connected) return false;
            try { Parse(rep, _reportLen); return true; } catch { return false; }
        }

        private static int _reopenCounter;
        private static void TryReopenOccasionally()
        {
            // if the pad was unplugged / never found, retry the open now and then (not every frame)
            if ((++_reopenCounter % 240) != 0) return;
            try { CloseHandle(); OpenAndStart(); } catch { Connected = false; }
        }

        private static void Parse(byte[] r, int len)
        {
            // USB: report id 0x01 (64 bytes). BT full: 0x31 (78 bytes) -> data shifted by +1.
            int off = (len > 0 && r[0] == 0x31) ? 1 : 0;
            if (len < 11 + off) return;
            LX = Axis(r[1 + off]); LY = -Axis(r[2 + off]);
            RX = Axis(r[3 + off]); RY = -Axis(r[4 + off]);
            L2 = r[5 + off] / 255f; R2 = r[6 + off] / 255f;
            byte b0 = r[8 + off], b1 = r[9 + off];
            ushort m = 0;
            if ((b0 & 0x20) != 0) m |= 0x1000; // Cross  -> A
            if ((b0 & 0x40) != 0) m |= 0x2000; // Circle -> B
            if ((b0 & 0x10) != 0) m |= 0x4000; // Square -> X
            if ((b0 & 0x80) != 0) m |= 0x8000; // Triangle -> Y
            if ((b1 & 0x01) != 0) m |= 0x0100; // L1
            if ((b1 & 0x02) != 0) m |= 0x0200; // R1
            if ((b1 & 0x20) != 0) m |= 0x0010; // Options -> Start
            if ((b1 & 0x10) != 0) m |= 0x0020; // Create  -> Back
            if ((b1 & 0x40) != 0) m |= 0x0040; // L3
            if ((b1 & 0x80) != 0) m |= 0x0080; // R3
            int hat = b0 & 0x0F; // 0=N,1=NE,2=E,3=SE,4=S,5=SW,6=W,7=NW,8=neutral
            if (hat == 7 || hat == 0 || hat == 1) m |= 0x0001; // up
            if (hat >= 3 && hat <= 5) m |= 0x0002;             // down
            if (hat >= 5 && hat <= 7) m |= 0x0004;             // left
            if (hat >= 1 && hat <= 3) m |= 0x0008;             // right
            Buttons = m;
        }

        private static float Axis(byte v)
        {
            float f = (v - 128) / 127f;
            const float d = 0.12f;
            if (f > d) return (f - d) / (1f - d);
            if (f < -d) return (f + d) / (1f - d);
            return 0f;
        }

        // ---- device discovery + reader thread ----
        private static void OpenAndStart()
        {
            var path = FindDevicePath();
            if (path == null) { Connected = false; return; }
            var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); Connected = false; return; }
            _handle = h;
            Connected = true;
            _thread = new Thread(ReaderLoop) { IsBackground = true, Name = "DualSenseHid" };
            _thread.Start();
        }

        private static void ReaderLoop()
        {
            var buf = new byte[128];
            var h = _handle;
            while (h != null && !h.IsInvalid && !h.IsClosed)
            {
                try
                {
                    uint read;
                    if (ReadFile(h, buf, (uint)buf.Length, out read, IntPtr.Zero) && read > 0)
                    {
                        var copy = new byte[read];
                        Array.Copy(buf, copy, (int)read);
                        _report = copy; _reportLen = (int)read;
                    }
                    else { break; }
                }
                catch { break; }
            }
            Connected = false;
        }

        private static void CloseHandle()
        {
            lock (_lock)
            {
                Connected = false;
                try { if (_handle != null && !_handle.IsClosed) _handle.Dispose(); } catch { }
                _handle = null; _report = null;
            }
        }

        private static string FindDevicePath()
        {
            Guid hidGuid; HidD_GetHidGuid(out hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE_VALUE) return null;
            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA(); did.cbSize = (uint)Marshal.SizeOf(did);
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref did); i++)
                {
                    uint size = 0;
                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref size, IntPtr.Zero);
                    if (size == 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        // cbSize: 8 on x64 (4-byte cbSize + 2-byte WCHAR aligned), 6 on x86.
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref did, detail, size, ref size, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;
                        if (IsDualSense(path)) return path;
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        private static bool IsDualSense(string path)
        {
            // Sony VID 054C; PIDs: 0CE6 DualSense, 0DF2 DualSense Edge, 05C4/09CC DualShock4.
            var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return false; }
            try
            {
                var a = new HIDD_ATTRIBUTES(); a.Size = (uint)Marshal.SizeOf(a);
                if (!HidD_GetAttributes(h, ref a)) return false;
                if (a.VendorID != 0x054C) return false;
                return a.ProductID == 0x0CE6 || a.ProductID == 0x0DF2 || a.ProductID == 0x05C4 || a.ProductID == 0x09CC;
            }
            finally { h.Dispose(); }
        }

        // ---- P/Invoke ----
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)] private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] private struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)] private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, ref uint reqSize, IntPtr devInfo);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint toRead, out uint read, IntPtr overlapped);
    }
}
