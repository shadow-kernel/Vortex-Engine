using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Editor.Editors.WorldEditor.Components.GamePreview
{
    public class ViewportHost : HwndHost
    {
        private IntPtr _hwnd = IntPtr.Zero;
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parentHandle, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        private const int GCLP_HBRBACKGROUND = -10;
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint crColor);
        [DllImport("user32.dll")]
        private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            int width = Math.Max(1, (int)ActualWidth);
            int height = Math.Max(1, (int)ActualHeight);

            _hwnd = CreateWindowEx(0, "static", string.Empty, WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                0, 0, width, height,
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            // Paint the native child dark (#161618) so no white frame shows behind/around
            // the DX12 viewport during init/resize.
            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    IntPtr darkBrush = CreateSolidBrush(0x00181616u); // COLORREF 0x00BBGGRR for #161618
                    if (darkBrush != IntPtr.Zero)
                        SetClassLongPtr(_hwnd, GCLP_HBRBACKGROUND, darkBrush);
                }
            }
            catch { /* cosmetic only */ }

            OnHostCreated?.Invoke(this, EventArgs.Empty);
            return new HandleRef(this, _hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            OnHostDestroying?.Invoke(this, EventArgs.Empty);
            DestroyWindow(hwnd.Handle);
            _hwnd = IntPtr.Zero;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            OnViewportSizeChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler OnHostCreated;
        public event EventHandler OnHostDestroying;
        public event EventHandler OnViewportSizeChanged;

        public new IntPtr Handle => _hwnd;
        public bool IsHandleValid => _hwnd != IntPtr.Zero;
    }
}
