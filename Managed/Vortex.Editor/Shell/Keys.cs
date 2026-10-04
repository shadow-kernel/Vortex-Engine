using System;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Modifier names for shortcut hints shown in the UI (tooltips, dialogs, context-menu gestures).
    /// macOS spells them with the glyphs people expect there (⌘⇧⌥⌫); on Windows and Linux the command
    /// modifier is Control and the names are written out — a Linux user reading "⌘Z" has no idea what to press.
    ///
    /// Only the TEXT lives here. The actual key handling already accepts either modifier
    /// (<c>KeyModifiers.Meta || KeyModifiers.Control</c>), so the shortcuts themselves work everywhere.
    /// </summary>
    internal static class Keys
    {
        public static bool Mac => OperatingSystem.IsMacOS();

        /// <summary>The command modifier on its own: "⌘" / "Ctrl+".</summary>
        public static string Cmd => Mac ? "⌘" : "Ctrl+";
        /// <summary>"⇧" / "Shift+".</summary>
        public static string Shift => Mac ? "⇧" : "Shift+";
        /// <summary>"⌥" / "Alt+".</summary>
        public static string Alt => Mac ? "⌥" : "Alt+";
        /// <summary>The delete key: "⌫" / "Del".</summary>
        public static string Delete => Mac ? "⌫" : "Del";

        /// <summary>
        /// A full chord, in each platform's own order and spelling:
        /// <c>Chord("Z")</c> → "⌘Z" / "Ctrl+Z", <c>Chord("Z", shift: true)</c> → "⇧⌘Z" / "Ctrl+Shift+Z".
        /// </summary>
        public static string Chord(string key, bool shift = false, bool alt = false) => Mac
            ? (alt ? "⌥" : "") + (shift ? "⇧" : "") + "⌘" + key
            : "Ctrl+" + (shift ? "Shift+" : "") + (alt ? "Alt+" : "") + key;

        /// <summary>
        /// Rewrites a hint written with the macOS glyphs into this platform's spelling:
        /// "Scenes — new / load (⌘N, ⇧⌘O)" → "Scenes — new / load (Ctrl+N, Ctrl+Shift+O)". Returns the text
        /// unchanged on macOS, and anywhere it holds no glyphs.
        /// </summary>
        public static string Localize(string text)
        {
            if (Mac || string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf('⌘') < 0 && text.IndexOf('⇧') < 0 && text.IndexOf('⌥') < 0 && text.IndexOf('⌫') < 0) return text;

            var sb = new System.Text.StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; ++i)
            {
                char c = text[i];
                if (c != '⌘' && c != '⇧' && c != '⌥' && c != '⌫') { sb.Append(c); continue; }

                // A run of modifier glyphs, then the key they apply to (one character, e.g. "⇧⌘O").
                bool cmd = false, shift = false, alt = false, del = false;
                int j = i;
                for (; j < text.Length; ++j)
                {
                    if (text[j] == '⌘') cmd = true;
                    else if (text[j] == '⇧') shift = true;
                    else if (text[j] == '⌥') alt = true;
                    else if (text[j] == '⌫') del = true;
                    else break;
                }
                string key = null;
                if (j < text.Length && text[j] != ' ' && text[j] != ')' && text[j] != ',') { key = text[j].ToString(); ++j; }

                if (del && key == null) sb.Append(Delete);
                else if (key != null && cmd) sb.Append(Chord(key, shift, alt));
                else if (key != null) sb.Append(shift ? Shift : alt ? Alt : "").Append(key);
                else sb.Append(cmd ? Cmd : shift ? Shift : alt ? Alt : "");
                i = j - 1;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Applies <see cref="Localize"/> to the shortcut hints that are spelled out in XAML (ToolTip.Tip and
        /// TextBox watermarks) below <paramref name="root"/>. The .axaml keeps the glyphs because they read
        /// best on the Mac; this pass runs once per window on the platforms where they do not.
        /// </summary>
        public static void LocalizeHints(Avalonia.Visual root)
        {
            if (Mac || root == null) return;
            try
            {
                foreach (var v in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root))
                {
                    if (v is not Avalonia.Controls.Control control) continue;
                    if (Avalonia.Controls.ToolTip.GetTip(control) is string tip)
                    {
                        string fixedTip = Localize(tip);
                        if (!ReferenceEquals(fixedTip, tip)) Avalonia.Controls.ToolTip.SetTip(control, fixedTip);
                    }
                    if (control is Avalonia.Controls.TextBox box && box.Watermark is string wm)
                        box.Watermark = Localize(wm);
                }
            }
            catch { }   // a cosmetic pass: never let it take the window down
        }
    }
}
