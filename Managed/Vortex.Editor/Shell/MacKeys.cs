using System;
using System.Runtime.InteropServices;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Posts real AppKit key events through <c>[NSApp sendEvent:]</c> — the same path a physical key press takes
    /// (window key equivalents → main-menu key equivalents → keyDown: on the first responder). Lets the smoke run
    /// prove that the ⌘ shortcuts reach their command exactly once.
    /// </summary>
    internal static class MacKeys
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }

        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgId(IntPtr self, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MsgVoidId(IntPtr self, IntPtr sel, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long MsgLong(IntPtr self, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgStr(IntPtr self, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr MsgKeyEvent(IntPtr cls, IntPtr sel, ulong type, CGPoint location, ulong modifierFlags, double timestamp,
            long windowNumber, IntPtr context, IntPtr characters, IntPtr charactersIgnoringModifiers, [MarshalAs(UnmanagedType.I1)] bool isARepeat, ushort keyCode);

        private static IntPtr S(string s) => sel_registerName(s);

        public const ulong Cmd = 1UL << 20, Shift = 1UL << 17, Option = 1UL << 19, Control = 1UL << 18;

        // ---- keyboard layout (the user may type on QWERTZ: the physical key code of "z" differs from ANSI) ----
        private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        [DllImport(Carbon)] private static extern IntPtr TISCopyCurrentKeyboardLayoutInputSource();
        [DllImport(Carbon)] private static extern IntPtr TISGetInputSourceProperty(IntPtr inputSource, IntPtr propertyKey);
        [DllImport(Carbon)] private static extern byte LMGetKbdType();
        [DllImport(Carbon)]
        private static extern int UCKeyTranslate(IntPtr keyLayoutPtr, ushort virtualKeyCode, ushort keyAction, uint modifierKeyState, uint keyboardType,
            uint keyTranslateOptions, ref uint deadKeyState, nuint maxStringLength, out nuint actualStringLength, [Out] char[] unicodeString);
        [DllImport(CoreFoundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr obj);
        private static System.Collections.Generic.Dictionary<char, ushort> _layoutCodes;

        /// <summary>Key code that produces <paramref name="c"/> on the CURRENT keyboard layout (UCKeyTranslate over
        /// all codes), falling back to the ANSI position.</summary>
        public static ushort KeyCode(char c)
        {
            if (_layoutCodes == null)
            {
                _layoutCodes = new System.Collections.Generic.Dictionary<char, ushort>();
                try
                {
                    IntPtr lib = NativeLibrary.Load(Carbon);
                    IntPtr keyPtr = NativeLibrary.GetExport(lib, "kTISPropertyUnicodeKeyLayoutData");
                    IntPtr key = Marshal.ReadIntPtr(keyPtr);
                    IntPtr src = TISCopyCurrentKeyboardLayoutInputSource();
                    IntPtr data = src != IntPtr.Zero ? TISGetInputSourceProperty(src, key) : IntPtr.Zero;
                    IntPtr layout = data != IntPtr.Zero ? CFDataGetBytePtr(data) : IntPtr.Zero;
                    if (layout != IntPtr.Zero)
                    {
                        var buf = new char[4];
                        for (ushort code = 0; code < 128; code++)
                        {
                            uint dead = 0;
                            if (UCKeyTranslate(layout, code, 3 /*kUCKeyActionDisplay*/, 0, LMGetKbdType(), 1 /*no dead keys*/, ref dead, 4, out nuint len, buf) == 0 && len == 1)
                            {
                                char ch = char.ToLowerInvariant(buf[0]);
                                if (!_layoutCodes.ContainsKey(ch)) _layoutCodes[ch] = code;
                            }
                        }
                    }
                    if (src != IntPtr.Zero) CFRelease(src);
                }
                catch (Exception ex) { Console.WriteLine("keyboard layout lookup failed: " + ex.Message); }
            }
            if (c != '\b' && _layoutCodes.TryGetValue(char.ToLowerInvariant(c), out ushort k)) return k;
            return AnsiKeyCode(c);
        }

        /// <summary>macOS virtual key codes (kVK_ANSI_*) for the letters the editor binds.</summary>
        public static ushort AnsiKeyCode(char c)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 'a': return 0x00; case 's': return 0x01; case 'd': return 0x02; case 'f': return 0x03;
                case 'z': return 0x06; case 'x': return 0x07; case 'c': return 0x08; case 'v': return 0x09;
                case 'b': return 0x0B; case 'w': return 0x0D; case 'e': return 0x0E; case 'r': return 0x0F;
                case 'y': return 0x10; case 'o': return 0x1F; case 'p': return 0x23; case 'n': return 0x2D;
                case 'g': return 0x05; case 'h': return 0x04; case 'i': return 0x22;
                case '\b': return 0x33;   // backspace ("delete" on a Mac keyboard)
                default: return 0xFFFF;
            }
        }

        /// <summary>Post key down + key up for <paramref name="c"/> with the given modifier flags to the window
        /// (NSWindow* from the platform handle). Returns false off macOS or when AppKit refuses the event.</summary>
        public static bool Press(IntPtr nsWindow, char c, ulong modifiers)
        {
            if (!OperatingSystem.IsMacOS() || nsWindow == IntPtr.Zero) return false;
            try
            {
                ushort code = KeyCode(c);
                if (code == 0xFFFF) return false;
                long windowNumber = MsgLong(nsWindow, S("windowNumber"));
                IntPtr nsString = objc_getClass("NSString");
                // like a real key press: charactersIgnoringModifiers keeps Shift ("Z" for ⇧⌘Z) — AppKit matches menu key
                // equivalents against it
                string chars = c == '\b' ? "\u007f" : (modifiers & Shift) != 0 ? c.ToString().ToUpperInvariant() : c.ToString().ToLowerInvariant();
                IntPtr ch = MsgStr(nsString, S("stringWithUTF8String:"), chars);
                IntPtr app = MsgId(objc_getClass("NSApplication"), S("sharedApplication"));
                IntPtr sel = S("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:");
                double t = Environment.TickCount64 / 1000.0;
                const ulong KeyDown = 10, KeyUp = 11;
                IntPtr down = MsgKeyEvent(objc_getClass("NSEvent"), sel, KeyDown, default, modifiers, t, windowNumber, IntPtr.Zero, ch, ch, false, code);
                IntPtr up = MsgKeyEvent(objc_getClass("NSEvent"), sel, KeyUp, default, modifiers, t + 0.02, windowNumber, IntPtr.Zero, ch, ch, false, code);
                if (down == IntPtr.Zero || up == IntPtr.Zero) return false;
                MsgVoidId(app, S("sendEvent:"), down);
                MsgVoidId(app, S("sendEvent:"), up);
                return true;
            }
            catch (Exception ex) { Console.WriteLine("synthetic key failed: " + ex.Message); return false; }
        }

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgIdLong(IntPtr self, IntPtr sel, long arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong MsgULong(IntPtr self, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool MsgBool(IntPtr self, IntPtr sel);

        private static string NsString(IntPtr s) => s == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(MsgId(s, S("UTF8String"))) ?? "";

        /// <summary>One item of the application's main menu bar (what AppKit actually shows).</summary>
        public sealed class MenuItemInfo { public string Path, Title, Key; public ulong Mask; public bool Enabled; public int Children; }

        /// <summary>Walk [NSApp mainMenu] (the real macOS menu bar) — titles, key equivalents, modifier masks.</summary>
        public static System.Collections.Generic.List<MenuItemInfo> DumpMainMenu(int maxDepth = 3)
        {
            var list = new System.Collections.Generic.List<MenuItemInfo>();
            if (!OperatingSystem.IsMacOS()) return list;
            try
            {
                IntPtr app = MsgId(objc_getClass("NSApplication"), S("sharedApplication"));
                IntPtr main = MsgId(app, S("mainMenu"));
                Walk(main, "", 0, maxDepth, list);
            }
            catch (Exception ex) { Console.WriteLine("menu dump failed: " + ex.Message); }
            return list;
        }

        private static void Walk(IntPtr menu, string prefix, int depth, int maxDepth, System.Collections.Generic.List<MenuItemInfo> list)
        {
            if (menu == IntPtr.Zero || depth > maxDepth) return;
            long n = MsgLong(menu, S("numberOfItems"));
            for (long i = 0; i < n; i++)
            {
                IntPtr item = MsgIdLong(menu, S("itemAtIndex:"), i);
                if (item == IntPtr.Zero || MsgBool(item, S("isSeparatorItem"))) continue;
                string title = NsString(MsgId(item, S("title")));
                IntPtr sub = MsgId(item, S("submenu"));
                var info = new MenuItemInfo
                {
                    Path = prefix + title, Title = title,
                    Key = NsString(MsgId(item, S("keyEquivalent"))),
                    Mask = MsgULong(item, S("keyEquivalentModifierMask")),
                    Enabled = MsgBool(item, S("isEnabled")),
                    Children = sub != IntPtr.Zero ? (int)MsgLong(sub, S("numberOfItems")) : 0,
                };
                list.Add(info);
                if (sub != IntPtr.Zero) Walk(sub, prefix + title + " ▸ ", depth + 1, maxDepth, list);
            }
        }

        public static string MaskText(ulong m) => ((m & Control) != 0 ? "⌃" : "") + ((m & Option) != 0 ? "⌥" : "") + ((m & Shift) != 0 ? "⇧" : "") + ((m & Cmd) != 0 ? "⌘" : "");

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MsgVoidBool(IntPtr self, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool arg);

        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        [DllImport(CoreGraphics)] private static extern IntPtr CGSessionCopyCurrentDictionary();
        [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
        [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string s, uint encoding);
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(IntPtr b);

        /// <summary>True while the macOS login session's screen is locked: windows are not composited then, so
        /// synthetic mouse input cannot hit-test (a click would start a window drag) — smoke runs report that clearly.</summary>
        public static bool IsScreenLocked()
        {
            if (!OperatingSystem.IsMacOS()) return false;
            try
            {
                IntPtr dict = CGSessionCopyCurrentDictionary();
                if (dict == IntPtr.Zero) return false;
                IntPtr key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked", 0x08000100);
                IntPtr v = CFDictionaryGetValue(dict, key);
                bool locked = v != IntPtr.Zero && CFBooleanGetValue(v);
                CFRelease(key); CFRelease(dict);
                return locked;
            }
            catch { return false; }
        }

        /// <summary>Make the editor the active application (menu-bar key equivalents only apply to the active app).</summary>
        public static void ActivateApp()
        {
            if (!OperatingSystem.IsMacOS()) return;
            try { MsgVoidBool(MsgId(objc_getClass("NSApplication"), S("sharedApplication")), S("activateIgnoringOtherApps:"), true); } catch { }
        }

        public static bool IsAppActive()
        {
            if (!OperatingSystem.IsMacOS()) return true;
            try { return MsgBool(MsgId(objc_getClass("NSApplication"), S("sharedApplication")), S("isActive")); } catch { return false; }
        }

        /// <summary>Bring the window to the front and make it key (a real key press goes to the key window).</summary>
        public static void MakeKey(IntPtr nsWindow)
        {
            if (!OperatingSystem.IsMacOS() || nsWindow == IntPtr.Zero) return;
            try { MsgVoidId(nsWindow, S("makeKeyAndOrderFront:"), IntPtr.Zero); } catch { }
        }
    }
}
