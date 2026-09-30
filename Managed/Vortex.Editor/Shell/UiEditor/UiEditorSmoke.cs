using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.UI.Vui;

namespace VortexEditor.Shell.UiEditor
{
    /// <summary>Smoke checks of the UI (.vui) editor: build a screen with every widget kind, drag / resize with the
    /// canvas math, anchors, button action codegen, undo, save + reload, test mode, and screenshots.</summary>
    internal static class UiEditorSmoke
    {
        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("ui editor: build a screen (every widget), drag/resize, anchors, action codegen, undo, save + reload", async () =>
            {
                string root = ProjectData.Current?.Path;
                if (string.IsNullOrEmpty(root)) return Fail("no project");
                string existing = null;
                try { existing = Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.vui", SearchOption.AllDirectories).FirstOrDefault(); } catch { }
                string path = Path.Combine(root, "Assets", "UI", "SmokeScreen.vui");
                string actions = VuiActions.ScriptPath(path);
                Editor.Core.Assets.AssetActions.CreateUiScreen(path);
                try
                {
                    UiEditorWindow.Open(path);
                    var w = UiEditorWindow.Find(path);
                    if (w == null) return Fail("window did not open");
                    await SmokeRegistry.Settle(700);
                    var screenRoot = w.Canvas.Root;
                    // one of every kind into the root (containers get a child each)
                    var made = new Dictionary<VuiKind, VuiElement>();
                    foreach (VuiKind k in Enum.GetValues(typeof(VuiKind)))
                    {
                        w.Select(screenRoot);
                        made[k] = w.AddElement(k);
                    }
                    w.Select(made[VuiKind.Panel]);
                    var inPanel = w.AddElement(VuiKind.Text);
                    bool structure = made.Values.All(x => x != null && ReferenceEquals(x.Parent, screenRoot)) && ReferenceEquals(inPanel?.Parent, made[VuiKind.Panel]) && made[VuiKind.List].RowTemplate != null;
                    // lay the widgets out in a grid so the capture shows each of them
                    int i = 0;
                    foreach (var el in made.Values)
                    {
                        if (el.Kind == VuiKind.Panel) { el.Anchor = AnchorEnum.TopLeft; el.OffX = 40; el.OffY = 40; continue; }
                        el.Anchor = AnchorEnum.TopLeft;
                        el.OffX = 560 + (i % 3) * 440; el.OffY = 60 + (i / 3) * 150;
                        i++;
                    }
                    var button = made[VuiKind.Button];
                    button.Text = "RESUME";
                    made[VuiKind.Image].ImageAsset = FirstTexture(root);
                    // canvas math: move, then resize the right edge -> the left edge must stay put
                    w.Select(button);
                    w.Design.RunLayout();
                    var r0 = button.Resolved;
                    w.Design.DragBy(button, -1, 30, 20);
                    w.Design.RunLayout();
                    var r1 = button.Resolved;
                    bool moved = Math.Abs(r1.X - r0.X - 30) <= 1.5f * w.Canvas.Scale + 1 && Math.Abs(r1.Y - r0.Y - 20) <= 1.5f * w.Canvas.Scale + 1;
                    w.Design.DragBy(button, 3, 60, 0);
                    w.Design.RunLayout();
                    var r2 = button.Resolved;
                    bool resized = Math.Abs(r2.X - r1.X) <= 1.5f && Math.Abs(r2.W - r1.W - 60) <= 2f;
                    // centre-anchored element: resizing the left edge keeps the right edge
                    var bar = made[VuiKind.Bar];
                    bar.Anchor = AnchorEnum.Center; bar.OffX = 0; bar.OffY = 200;
                    w.Design.RunLayout();
                    var b0 = bar.Resolved;
                    w.Design.DragBy(bar, 7, -50, 0);
                    w.Design.RunLayout();
                    var b1 = bar.Resolved;
                    bool leftResize = Math.Abs((b1.X + b1.W) - (b0.X + b0.W)) <= 2f && Math.Abs(b1.W - b0.W - 50) <= 2f;
                    // anchor: bottom-right keeps the margin mirrored inside the screen
                    var text = made[VuiKind.Text];
                    text.Anchor = AnchorEnum.TopLeft; text.OffX = 24; text.OffY = 18;
                    w.SetAnchor(text, AnchorEnum.BottomRight, false);
                    bool anchored = text.Anchor == AnchorEnum.BottomRight && text.OffX == -24 && text.OffY == -18;
                    // button -> C# action with code generation
                    button.ClickAction = VuiActions.SanitizeMethod("On Resume!");
                    bool stub = VuiActions.EnsureStub(path, button.ClickAction) && VuiActions.HasMethod(path, "OnResume");
                    // undo removes the last added element, redo brings it back
                    int count = CountAll(screenRoot);
                    w.Select(screenRoot);
                    var extra = w.AddElement(VuiKind.Crosshair);
                    w.Undo();
                    bool undone = CountAll(w.Canvas.Root) == count;
                    w.Redo();
                    bool redone = CountAll(w.Canvas.Root) == count + 1;
                    w.Undo();
                    // the design surface renders
                    await SmokeRegistry.Settle(500);
                    bool drawn = RendersContent(w.Design);
                    w.Select(button);
                    await SmokeRegistry.Settle(300);
                    SmokeRegistry.Capture(w, "ui_editor.png");
                    // test mode on a copy: a click on the button fires its action, the document is untouched
                    bool dirtyBeforeSave = w.IsDirty;
                    bool saved = w.Save();
                    var reloaded = VuiDocument.Load(path);
                    bool roundTrip = reloaded != null && CountAll(reloaded.Root) == CountAll(w.Canvas.Root)
                                     && Find(reloaded.Root, button.Id)?.ClickAction == "OnResume"
                                     && Find(reloaded.Root, made[VuiKind.List].Id)?.RowTemplate != null;
                    w.CloseWithoutSaving();
                    await SmokeRegistry.Settle(200);
                    ConsoleService.Instance.Log("smoke: ui editor structure=" + structure + " moved=" + moved + " resized=" + resized + " leftResize=" + leftResize + " anchored=" + anchored + " stub=" + stub + " undo=" + undone + "/" + redone + " drawn=" + drawn + " dirty=" + dirtyBeforeSave + " saved=" + saved + " roundTrip=" + roundTrip + (existing != null ? " (project has " + Path.GetFileName(existing) + ")" : ""));
                    return structure && moved && resized && leftResize && anchored && stub && undone && redone && drawn && dirtyBeforeSave && saved && roundTrip;
                }
                finally
                {
                    try { File.Delete(path); } catch { }
                    try { if (actions != null) File.Delete(actions); } catch { }
                }
            });

            SmokeRegistry.Add("ui editor: open an existing screen + test mode", async () =>
            {
                string root = ProjectData.Current?.Path;
                if (string.IsNullOrEmpty(root)) return Fail("no project");
                string path = null;
                try { path = Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.vui", SearchOption.AllDirectories).FirstOrDefault(p => !p.EndsWith("SmokeScreen.vui", StringComparison.OrdinalIgnoreCase)); } catch { }
                bool temp = path == null;
                if (temp)
                {
                    // the project has no screen: author a small pause menu as a stand-in
                    path = Path.Combine(root, "Assets", "UI", "SmokePause.vui");
                    File.WriteAllText(path, "{\"vui\":1,\"designW\":1920,\"designH\":1080,\"root\":{\"kind\":\"Panel\",\"id\":\"pauseRoot\",\"stretchX\":true,\"stretchY\":true,\"bg\":[0.02,0.01,0.012,0.66],\"blocksInput\":true,\"children\":[" +
                        "{\"kind\":\"Text\",\"id\":\"title\",\"anchor\":\"TopCenter\",\"off\":[0,160],\"size\":[900,80],\"text\":\"PAUSED\",\"fontSize\":64,\"weight\":700,\"align\":1,\"fg\":[0.62,0.03,0.03,1]}," +
                        "{\"kind\":\"Button\",\"id\":\"resumeButton\",\"anchor\":\"Center\",\"off\":[0,-40],\"size\":[300,60],\"text\":\"RESUME\",\"fontSize\":26,\"weight\":700,\"radius\":4,\"bg\":[0.10,0.02,0.02,0.92],\"hoverTint\":[0.55,0.04,0.04,1],\"fg\":[0.9,0.85,0.85,1],\"clickAction\":\"OnResume\"}," +
                        "{\"kind\":\"Button\",\"id\":\"quitButton\",\"anchor\":\"Center\",\"off\":[0,40],\"size\":[300,56],\"text\":\"QUIT GAME\",\"fontSize\":20,\"weight\":600,\"radius\":4,\"bg\":[0.09,0.02,0.02,0.92],\"fg\":[0.86,0.78,0.78,1],\"clickAction\":\"OnQuitGame\"}," +
                        "{\"kind\":\"Slider\",\"id\":\"volume\",\"anchor\":\"Center\",\"off\":[0,130],\"size\":[300,22],\"bg\":[0.2,0.2,0.24,1],\"fg\":[0.9,0.9,0.9,1],\"value\":0.4}," +
                        "{\"kind\":\"Text\",\"id\":\"footer\",\"anchor\":\"BottomCenter\",\"off\":[0,-22],\"size\":[900,24],\"text\":\"ESC to resume\",\"fontSize\":13,\"weight\":400,\"align\":1,\"fg\":[0.4,0.3,0.3,1]}]}}");
                }
                try
                {
                    UiEditorWindow.Open(path);
                    var w = UiEditorWindow.Find(path);
                    if (w == null) return Fail("window did not open");
                    await SmokeRegistry.Settle(800);
                    bool loaded = w.Canvas.Root != null && CountAll(w.Canvas.Root) > 1 && !w.IsDirty;
                    bool drawn = RendersContent(w.Design);
                    SmokeRegistry.Capture(w, "ui_editor_screen.png");
                    // test mode: press a button through the runtime input path
                    var btn = FindKind(w.Canvas.Root, VuiKind.Button);
                    bool fired = true;
                    if (btn != null && !string.IsNullOrEmpty(btn.ClickAction))
                    {
                        var copy = new VuiCanvas { DesignW = w.Canvas.DesignW, DesignH = w.Canvas.DesignH, Root = VuiDocument.ToRuntime(VuiDocument.FromRuntime(w.Canvas.Root), null) };
                        copy.Reindex();
                        copy.Layout(w.Design.ScreenW, w.Design.ScreenH);
                        var target = Find(copy.Root, btn.Id);
                        var r = target.Resolved;
                        copy.Update(new VuiInput { Mx = r.CenterX, My = r.CenterY, Down = true, Pressed = true, Chars = new char[0], KeyEvents = new int[0] });
                        fired = copy.FiredActions.Contains(btn.ClickAction);
                    }
                    w.CloseWithoutSaving();
                    await SmokeRegistry.Settle(200);
                    ConsoleService.Instance.Log("smoke: ui editor open " + Path.GetFileName(path) + " loaded=" + loaded + " drawn=" + drawn + " fired=" + fired);
                    return loaded && drawn && fired;
                }
                finally { if (temp) try { File.Delete(path); } catch { } }
            });
        }

        private static bool Fail(string why) { ConsoleService.Instance.LogWarning("smoke: " + why); return false; }

        private static int CountAll(VuiElement e) { if (e == null) return 0; int n = 1; foreach (var c in e.Children) n += CountAll(c); if (e.RowTemplate != null) n += CountAll(e.RowTemplate); return n; }

        private static VuiElement Find(VuiElement e, string id)
        {
            if (e == null) return null;
            if (e.Id == id) return e;
            foreach (var c in e.Children) { var r = Find(c, id); if (r != null) return r; }
            return Find(e.RowTemplate, id);
        }

        private static VuiElement FindKind(VuiElement e, VuiKind k)
        {
            if (e == null) return null;
            if (e.Kind == k) return e;
            foreach (var c in e.Children) { var r = FindKind(c, k); if (r != null) return r; }
            return null;
        }

        private static string FirstTexture(string root)
        {
            try { return Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.png", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).FirstOrDefault(); }
            catch { return null; }
        }

        /// <summary>Render the design surface offscreen and check it drew more than a flat background.</summary>
        private static bool RendersContent(VuiDesignCanvas c)
        {
            try
            {
                var size = c.Bounds.Size;
                if (size.Width < 10 || size.Height < 10) return false;
                var rtb = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
                rtb.Render(c);
                using (var ms = new MemoryStream())
                {
                    rtb.Save(ms);
                    ms.Position = 0;
                    using (var wb = WriteableBitmap.Decode(ms))
                    using (var fb = wb.Lock())
                    {
                        var seen = new HashSet<int>();
                        int w = fb.Size.Width, h = fb.Size.Height;
                        for (int y = 0; y < h; y += 3)
                            for (int x = 0; x < w; x += 3)
                            {
                                int px = System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address, y * fb.RowBytes + x * 4);
                                seen.Add(((px >> 3) & 0x1F) | (((px >> 11) & 0x1F) << 5) | (((px >> 19) & 0x1F) << 10));
                            }
                        return seen.Count > 8;
                    }
                }
            }
            catch { return false; }
        }
    }
}
