using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VortexEditor.Shell.Material;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Claude's answers as the panel shows them: the Markdown Claude writes in chat — paragraphs, headings, bullet and
    /// numbered lists, quotes, rules, tables (as monospaced text), fenced code with a copy button, inline code, bold,
    /// italic and links. Every text is selectable. Unclosed constructs (an answer still streaming) render as far as
    /// they go.
    /// </summary>
    internal static class ChatMarkdown
    {
        private static readonly Regex Heading = new Regex(@"^(#{1,6})\s+(.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex ListItem = new Regex(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Rule = new Regex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.CultureInvariant);

        public static Control Render(string markdown)
        {
            var root = new StackPanel { Spacing = 6 };
            var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
            var para = new List<string>();

            void FlushPara()
            {
                if (para.Count == 0) return;
                root.Children.Add(Text(string.Join("\n", para)));
                para.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    FlushPara();
                    string lang = trimmed.Substring(3).Trim();
                    var code = new StringBuilder();
                    int j = i + 1;
                    for (; j < lines.Length && !lines[j].TrimStart().StartsWith("```", StringComparison.Ordinal); j++)
                    {
                        if (code.Length > 0) code.Append('\n');
                        code.Append(lines[j]);
                    }
                    root.Children.Add(CodeBlock(code.ToString(), lang));
                    i = j;   // the closing fence (or the end)
                    continue;
                }
                if (trimmed.Length == 0) { FlushPara(); continue; }
                var h = Heading.Match(trimmed);
                if (h.Success)
                {
                    FlushPara();
                    int level = h.Groups[1].Value.Length;
                    var tb = Text(h.Groups[2].Value);
                    tb.FontWeight = FontWeight.SemiBold;
                    tb.FontSize = level <= 1 ? 16 : level == 2 ? 14.5 : 13;
                    tb.Margin = new Thickness(0, 4, 0, 0);
                    root.Children.Add(tb);
                    continue;
                }
                if (Rule.IsMatch(line)) { FlushPara(); root.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 4), Background = EditorKit.Brush("VxHairlineBrush") }); continue; }
                var li = ListItem.Match(line);
                if (li.Success)
                {
                    FlushPara();
                    int indent = li.Groups[1].Value.Replace("\t", "    ").Length / 2;
                    string marker = li.Groups[2].Value;
                    bool numbered = char.IsDigit(marker[0]);
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(4 + 14 * Math.Min(indent, 4), 0, 0, 0) };
                    var bullet = new TextBlock
                    {
                        Text = numbered ? marker.TrimEnd(')', '.') + "." : "•", MinWidth = numbered ? 20 : 14,
                        Foreground = EditorKit.Brush("VxTextSecondaryBrush"), FontSize = 13, LineHeight = 19,
                    };
                    row.Children.Add(bullet);
                    // continuation lines of the item (indented, not a new item)
                    var item = new List<string> { li.Groups[3].Value };
                    while (i + 1 < lines.Length && lines[i + 1].Length > 0 && char.IsWhiteSpace(lines[i + 1][0])
                           && !ListItem.IsMatch(lines[i + 1]) && !lines[i + 1].TrimStart().StartsWith("```", StringComparison.Ordinal))
                        item.Add(lines[++i].Trim());
                    var body = Text(string.Join("\n", item));
                    Grid.SetColumn(body, 1);
                    row.Children.Add(body);
                    root.Children.Add(row);
                    continue;
                }
                if (trimmed.StartsWith(">", StringComparison.Ordinal))
                {
                    FlushPara();
                    var quote = new List<string>();
                    for (; i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal); i++)
                        quote.Add(lines[i].TrimStart().Substring(1).TrimStart());
                    i--;
                    var q = Text(string.Join("\n", quote));
                    q.Foreground = EditorKit.Brush("VxTextSecondaryBrush");
                    root.Children.Add(new Border
                    {
                        BorderBrush = EditorKit.Brush("VxSeparatorBrush"), BorderThickness = new Thickness(2, 0, 0, 0),
                        Padding = new Thickness(10, 0, 0, 0), Child = q,
                    });
                    continue;
                }
                if (trimmed.StartsWith("|", StringComparison.Ordinal))
                {
                    FlushPara();
                    var table = new List<string>();
                    for (; i < lines.Length && lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal); i++) table.Add(lines[i].Trim());
                    i--;
                    root.Children.Add(Table(table));
                    continue;
                }
                para.Add(line.TrimEnd());
            }
            FlushPara();
            return root;
        }

        /// <summary>A paragraph with inline formatting (selectable).</summary>
        private static SelectableTextBlock Text(string text)
        {
            var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 19, FontSize = 13 };
            tb.Inlines ??= new InlineCollection();
            AddInlines(tb.Inlines, text, bold: false, italic: false);
            return tb;
        }

        private static void AddInlines(InlineCollection into, string text, bool bold, bool italic)
        {
            var plain = new StringBuilder();
            void FlushPlain()
            {
                if (plain.Length == 0) return;
                string s = plain.ToString();
                plain.Clear();
                var parts = s.Split('\n');
                for (int k = 0; k < parts.Length; k++)
                {
                    if (k > 0) into.Add(new LineBreak());
                    if (parts[k].Length > 0) into.Add(Styled(new Run(parts[k]), bold, italic));
                }
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i + 1)
                    {
                        FlushPlain();
                        into.Add(new Run(text.Substring(i + 1, end - i - 1))
                        {
                            FontFamily = Mono, FontSize = 12, Background = EditorKit.Brush("VxHoverBrush"),
                            Foreground = EditorKit.Brush("VxTextBrush"),
                        });
                        i = end + 1;
                        continue;
                    }
                }
                else if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
                {
                    int end = text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                    if (end > i + 2 && !char.IsWhiteSpace(text[i + 2]))
                    {
                        FlushPlain();
                        AddInlines(into, text.Substring(i + 2, end - i - 2), true, italic);
                        i = end + 2;
                        continue;
                    }
                }
                else if ((c == '*' || c == '_') && BoundaryBefore(text, i) && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
                {
                    int end = FindClosing(text, c, i + 1);
                    if (end > i + 1)
                    {
                        FlushPlain();
                        AddInlines(into, text.Substring(i + 1, end - i - 1), bold, true);
                        i = end + 1;
                        continue;
                    }
                }
                else if (c == '[')
                {
                    int close = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                    int paren = close > 0 ? text.IndexOf(')', close + 2) : -1;
                    if (close > i + 1 && paren > close + 2 && text.IndexOf('\n', i, paren - i) < 0)
                    {
                        FlushPlain();
                        string label = text.Substring(i + 1, close - i - 1);
                        string url = text.Substring(close + 2, paren - close - 2);
                        into.Add(new Run(label) { Foreground = EditorKit.Brush("VxAccentBrush"), TextDecorations = TextDecorations.Underline });
                        if (!string.Equals(label, url, StringComparison.OrdinalIgnoreCase))
                            into.Add(new Run(" (" + url + ")") { Foreground = EditorKit.Brush("VxTextTertiaryBrush"), FontSize = 11.5 });
                        i = paren + 1;
                        continue;
                    }
                }
                plain.Append(c);
                i++;
            }
            FlushPlain();
        }

        // `snake_case` names and 2*3 must stay as they are: an emphasis marker opens only after a space or punctuation
        // and closes only before one
        private static bool BoundaryBefore(string s, int i) => i == 0 || char.IsWhiteSpace(s[i - 1]) || "([{\"'".IndexOf(s[i - 1]) >= 0;

        private static int FindClosing(string s, char marker, int from)
        {
            for (int k = from; k < s.Length; k++)
            {
                if (s[k] == '\n') return -1;
                if (s[k] == marker && !char.IsWhiteSpace(s[k - 1]) && (k + 1 == s.Length || !char.IsLetterOrDigit(s[k + 1]))) return k;
            }
            return -1;
        }

        private static Run Styled(Run r, bool bold, bool italic)
        {
            if (bold) r.FontWeight = FontWeight.SemiBold;
            if (italic) r.FontStyle = FontStyle.Italic;
            return r;
        }

        private static FontFamily Mono =>
            Application.Current != null && Application.Current.TryFindResource("VxMono", out var f) && f is FontFamily ff ? ff : new FontFamily("Menlo, Consolas, monospace");

        private static Control CodeBlock(string code, string lang)
        {
            var text = new SelectableTextBlock { Text = code, FontFamily = Mono, FontSize = 12, LineHeight = 17, TextWrapping = TextWrapping.NoWrap };
            var scroll = new ScrollViewer
            {
                Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(10, 2, 10, 8),
            };
            var copy = new Button { Classes = { "link" }, Content = "Copy", FontSize = 11, Padding = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
            copy.Click += async (s, e) =>
            {
                try
                {
                    var clip = TopLevel.GetTopLevel(copy)?.Clipboard;
                    if (clip != null) await clip.SetTextAsync(code);
                    copy.Content = "Copied";
                    DispatcherTimer.RunOnce(() => copy.Content = "Copy", TimeSpan.FromSeconds(1.5));
                }
                catch { }
            };
            var head = new DockPanel { Margin = new Thickness(10, 4, 6, 0) };
            DockPanel.SetDock(copy, Dock.Right);
            head.Children.Add(copy);
            head.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(lang) ? "code" : lang, FontSize = 10.5, Foreground = EditorKit.Brush("VxTextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(scroll);
            return new Border
            {
                Background = EditorKit.Brush("VxFieldBrush"), CornerRadius = new CornerRadius(6),
                BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(1), Child = stack,
            };
        }

        private static Control Table(List<string> rows)
        {
            // columns padded to equal width, separator rows (|---|) as a line
            var cells = new List<string[]>();
            foreach (var r in rows)
            {
                var parts = r.Trim().Trim('|').Split('|');
                for (int k = 0; k < parts.Length; k++) parts[k] = parts[k].Trim().Replace("**", "").Replace("`", "");
                cells.Add(parts);
            }
            int cols = 0;
            foreach (var c in cells) cols = Math.Max(cols, c.Length);
            var widths = new int[cols];
            foreach (var c in cells)
                if (!IsSeparator(c))
                    for (int k = 0; k < c.Length; k++) widths[k] = Math.Max(widths[k], c[k].Length);
            var sb = new StringBuilder();
            foreach (var c in cells)
            {
                if (sb.Length > 0) sb.Append('\n');
                for (int k = 0; k < cols; k++)
                {
                    if (k > 0) sb.Append("  ");
                    if (IsSeparator(c)) sb.Append(new string('─', widths[k]));
                    else sb.Append((k < c.Length ? c[k] : "").PadRight(widths[k]));
                }
            }
            var text = new SelectableTextBlock { Text = sb.ToString(), FontFamily = Mono, FontSize = 11.5, LineHeight = 16, TextWrapping = TextWrapping.NoWrap };
            return new ScrollViewer { Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

            static bool IsSeparator(string[] c)
            {
                foreach (var x in c) if (x.Trim(':', '-', ' ').Length > 0 || x.Length == 0) return false;
                return true;
            }
        }
    }

    /// <summary>One of Claude's answers in the transcript: Markdown that grows while it streams (re-rendered at most
    /// every 60 ms).</summary>
    internal sealed class MarkdownBlock : ContentControl
    {
        private readonly StringBuilder _text = new StringBuilder();
        private bool _scheduled;

        public MarkdownBlock(string text = null)
        {
            if (!string.IsNullOrEmpty(text)) _text.Append(text);
            Render();
        }

        /// <summary>The Markdown so far.</summary>
        public string Text => _text.ToString();

        public void Append(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            _text.Append(delta);
            if (_scheduled) return;
            _scheduled = true;
            DispatcherTimer.RunOnce(Render, TimeSpan.FromMilliseconds(60));
        }

        /// <summary>Render now (the answer is complete).</summary>
        public void Flush() => Render();

        private void Render()
        {
            _scheduled = false;
            Content = ChatMarkdown.Render(_text.ToString());
        }
    }
}
