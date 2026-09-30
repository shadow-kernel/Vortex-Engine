using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Editor.Core.Services.Build
{
    /// <summary>
    /// Rewrites the Win32 resource section of a PE executable in pure managed code (works on macOS/Linux): sets the
    /// application icon (RT_ICON + RT_GROUP_ICON from an .ico file) and the version info (RT_VERSION with product /
    /// company / version strings). Every other resource (the manifest, ...) is preserved. The new resource tree is
    /// written into the existing .rsrc section when it is the last section of the image, otherwise a new section is
    /// appended; the resource data directory, SizeOfImage and the section table are updated and the checksum is
    /// cleared (Windows does not verify it for applications).
    /// </summary>
    public static class PeResourceWriter
    {
        public const uint RT_ICON = 3, RT_GROUP_ICON = 14, RT_VERSION = 16;
        private const ushort LANG_EN_US = 0x0409;

        private sealed class Node
        {
            public string Name;          // named entry (null = id entry)
            public uint Id;
            public List<Node> Children;  // directory
            public byte[] Data;          // leaf
            public uint CodePage;
            public bool IsDir => Children != null;
        }

        public static void SetIconAndVersion(string exePath, byte[] icoFile, string productName, string companyName, string version, string description)
        {
            byte[] pe = File.ReadAllBytes(exePath);
            var hdr = ParseHeaders(pe);
            var root = hdr.ResourceRva != 0 ? ParseResources(pe, hdr) : new Node { Children = new List<Node>(), Id = 0 };

            if (icoFile != null && icoFile.Length > 6)
            {
                RemoveType(root, RT_ICON); RemoveType(root, RT_GROUP_ICON);
                var images = ParseIco(icoFile);
                var iconDir = GetOrAddDir(root, RT_ICON);
                var grp = new MemoryStream();
                var gw = new BinaryWriter(grp);
                gw.Write((ushort)0); gw.Write((ushort)1); gw.Write((ushort)images.Count);
                for (int i = 0; i < images.Count; i++)
                {
                    var im = images[i];
                    AddLeaf(iconDir, (uint)(i + 1), LANG_EN_US, im.Data);
                    gw.Write(im.Width); gw.Write(im.Height); gw.Write(im.ColorCount); gw.Write((byte)0);
                    gw.Write(im.Planes); gw.Write(im.BitCount); gw.Write((uint)im.Data.Length); gw.Write((ushort)(i + 1));
                }
                AddLeaf(GetOrAddDir(root, RT_GROUP_ICON), 1, LANG_EN_US, grp.ToArray());
            }
            if (!string.IsNullOrEmpty(productName) || !string.IsNullOrEmpty(version))
            {
                RemoveType(root, RT_VERSION);
                AddLeaf(GetOrAddDir(root, RT_VERSION), 1, LANG_EN_US, BuildVersionInfo(productName ?? "", companyName ?? "", version ?? "1.0.0", description ?? productName ?? "", Path.GetFileName(exePath)));
            }

            // Serialize the tree twice: once to learn the size, then at the final RVA.
            var section = FindSectionForRva(hdr, hdr.ResourceRva);
            bool replaceInPlace = section != null && section.Index == hdr.Sections.Count - 1;
            uint newRva = replaceInPlace ? section.VirtualAddress : Align(LastSectionEndRva(hdr), hdr.SectionAlignment);
            byte[] blob = SerializeResources(root, newRva);
            uint rawSize = Align((uint)blob.Length, hdr.FileAlignment);

            byte[] output;
            if (replaceInPlace)
            {
                // Keep everything before the section, write the new payload, drop whatever followed (nothing does).
                uint rawStart = section.PointerToRawData;
                output = new byte[rawStart + rawSize];
                Array.Copy(pe, output, (int)Math.Min(pe.Length, rawStart));
                Array.Copy(blob, 0, output, rawStart, blob.Length);
                WriteU32(output, section.HeaderOffset + 8, (uint)blob.Length);   // VirtualSize
                WriteU32(output, section.HeaderOffset + 16, rawSize);            // SizeOfRawData
                uint oldSizeOfImage = ReadU32(output, hdr.OptionalHeaderOffset + 56);
                uint imageEnd = Align(newRva + (uint)blob.Length, hdr.SectionAlignment);
                if (imageEnd > oldSizeOfImage || section.VirtualSize > blob.Length) WriteU32(output, hdr.OptionalHeaderOffset + 56, imageEnd);
            }
            else
            {
                // Append a new section: needs one free section-header slot inside SizeOfHeaders.
                uint tableEnd = hdr.SectionTableOffset + (uint)hdr.Sections.Count * 40;
                if (tableEnd + 40 > hdr.SizeOfHeaders) throw new InvalidOperationException("no room for another section header in " + exePath);
                uint rawStart = Align((uint)pe.Length, hdr.FileAlignment);
                output = new byte[rawStart + rawSize];
                Array.Copy(pe, output, pe.Length);
                Array.Copy(blob, 0, output, rawStart, blob.Length);
                var name = Encoding.ASCII.GetBytes(".rsrc");
                Array.Clear(output, (int)tableEnd, 40);
                Array.Copy(name, 0, output, tableEnd, name.Length);
                WriteU32(output, tableEnd + 8, (uint)blob.Length);
                WriteU32(output, tableEnd + 12, newRva);
                WriteU32(output, tableEnd + 16, rawSize);
                WriteU32(output, tableEnd + 20, rawStart);
                WriteU32(output, tableEnd + 36, 0x40000040);   // initialized data | readable
                WriteU16(output, hdr.CoffOffset + 2, (ushort)(hdr.Sections.Count + 1));
                WriteU32(output, hdr.OptionalHeaderOffset + 56, Align(newRva + (uint)blob.Length, hdr.SectionAlignment));
                WriteU32(output, hdr.OptionalHeaderOffset + 8, ReadU32(output, hdr.OptionalHeaderOffset + 8) + rawSize);   // SizeOfInitializedData
            }
            WriteU32(output, hdr.ResourceDirEntryOffset, newRva);
            WriteU32(output, hdr.ResourceDirEntryOffset + 4, (uint)blob.Length);
            WriteU32(output, hdr.OptionalHeaderOffset + 64, 0);   // CheckSum
            File.WriteAllBytes(exePath, output);
        }

        /// <summary>Human-readable dump of the resource tree (verification / diagnostics).</summary>
        public static string Describe(string exePath)
        {
            byte[] pe = File.ReadAllBytes(exePath);
            var hdr = ParseHeaders(pe);
            if (hdr.ResourceRva == 0) return "(no resources)";
            var root = ParseResources(pe, hdr);
            var sb = new StringBuilder();
            void Walk(Node n, int depth)
            {
                foreach (var c in n.Children)
                {
                    sb.Append(new string(' ', depth * 2)).Append(c.Name ?? ("#" + c.Id));
                    if (c.IsDir) { sb.AppendLine(); Walk(c, depth + 1); }
                    else sb.AppendLine("  " + c.Data.Length + " bytes");
                }
            }
            Walk(root, 0);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ headers

        private sealed class Section { public int Index; public uint HeaderOffset, VirtualSize, VirtualAddress, SizeOfRawData, PointerToRawData; }
        private sealed class Headers
        {
            public uint CoffOffset, OptionalHeaderOffset, SectionTableOffset, SectionAlignment, FileAlignment, SizeOfHeaders, ResourceDirEntryOffset, ResourceRva, ResourceSize;
            public bool Pe32Plus;
            public List<Section> Sections = new List<Section>();
        }

        private static Headers ParseHeaders(byte[] pe)
        {
            if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z') throw new InvalidDataException("not a PE image (MZ)");
            uint lfanew = ReadU32(pe, 0x3C);
            if (ReadU32(pe, lfanew) != 0x00004550) throw new InvalidDataException("not a PE image (PE signature)");
            var h = new Headers { CoffOffset = lfanew + 4 };
            ushort numSections = ReadU16(pe, h.CoffOffset + 2);
            ushort optSize = ReadU16(pe, h.CoffOffset + 16);
            h.OptionalHeaderOffset = h.CoffOffset + 20;
            ushort magic = ReadU16(pe, h.OptionalHeaderOffset);
            h.Pe32Plus = magic == 0x20b;
            if (!h.Pe32Plus && magic != 0x10b) throw new InvalidDataException("unknown optional header magic");
            h.SectionAlignment = ReadU32(pe, h.OptionalHeaderOffset + 32);
            h.FileAlignment = ReadU32(pe, h.OptionalHeaderOffset + 36);
            h.SizeOfHeaders = ReadU32(pe, h.OptionalHeaderOffset + 60);
            uint dataDir = h.OptionalHeaderOffset + (uint)(h.Pe32Plus ? 112 : 96);
            h.ResourceDirEntryOffset = dataDir + 2 * 8;
            h.ResourceRva = ReadU32(pe, h.ResourceDirEntryOffset);
            h.ResourceSize = ReadU32(pe, h.ResourceDirEntryOffset + 4);
            h.SectionTableOffset = h.OptionalHeaderOffset + optSize;
            for (int i = 0; i < numSections; i++)
            {
                uint o = h.SectionTableOffset + (uint)i * 40;
                h.Sections.Add(new Section { Index = i, HeaderOffset = o, VirtualSize = ReadU32(pe, o + 8), VirtualAddress = ReadU32(pe, o + 12), SizeOfRawData = ReadU32(pe, o + 16), PointerToRawData = ReadU32(pe, o + 20) });
            }
            return h;
        }

        private static Section FindSectionForRva(Headers h, uint rva)
        {
            foreach (var s in h.Sections)
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)) return s;
            return null;
        }

        private static uint LastSectionEndRva(Headers h)
        {
            uint end = 0;
            foreach (var s in h.Sections) end = Math.Max(end, s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData));
            return end;
        }

        private static uint RvaToFile(Headers h, uint rva)
        {
            var s = FindSectionForRva(h, rva);
            if (s == null) throw new InvalidDataException("RVA outside every section");
            return rva - s.VirtualAddress + s.PointerToRawData;
        }

        // ------------------------------------------------------------------ resource tree

        private static Node ParseResources(byte[] pe, Headers h)
        {
            uint baseFile = RvaToFile(h, h.ResourceRva);
            return ParseDir(pe, h, baseFile, 0, 0);
        }

        private static Node ParseDir(byte[] pe, Headers h, uint baseFile, uint dirOffset, int depth)
        {
            var node = new Node { Children = new List<Node>() };
            uint o = baseFile + dirOffset;
            ushort named = ReadU16(pe, o + 12), ids = ReadU16(pe, o + 14);
            uint e = o + 16;
            for (int i = 0; i < named + ids; i++, e += 8)
            {
                uint nameField = ReadU32(pe, e), dataField = ReadU32(pe, e + 4);
                Node child;
                if ((dataField & 0x80000000u) != 0) child = depth < 8 ? ParseDir(pe, h, baseFile, dataField & 0x7FFFFFFFu, depth + 1) : new Node { Children = new List<Node>() };
                else
                {
                    uint de = baseFile + dataField;
                    uint dataRva = ReadU32(pe, de), size = ReadU32(pe, de + 4), cp = ReadU32(pe, de + 8);
                    var data = new byte[size];
                    Array.Copy(pe, RvaToFile(h, dataRva), data, 0, size);
                    child = new Node { Data = data, CodePage = cp };
                }
                if ((nameField & 0x80000000u) != 0)
                {
                    uint so = baseFile + (nameField & 0x7FFFFFFFu);
                    ushort len = ReadU16(pe, so);
                    child.Name = Encoding.Unicode.GetString(pe, (int)so + 2, len * 2);
                }
                else child.Id = nameField;
                node.Children.Add(child);
            }
            return node;
        }

        private static void RemoveType(Node root, uint type) => root.Children.RemoveAll(c => c.Name == null && c.Id == type);

        private static Node GetOrAddDir(Node parent, uint id)
        {
            foreach (var c in parent.Children) if (c.Name == null && c.Id == id && c.IsDir) return c;
            var n = new Node { Id = id, Children = new List<Node>() };
            parent.Children.Add(n);
            return n;
        }

        private static void AddLeaf(Node typeDir, uint id, ushort lang, byte[] data)
        {
            var nameDir = GetOrAddDir(typeDir, id);
            nameDir.Children.RemoveAll(c => c.Name == null && c.Id == lang);
            nameDir.Children.Add(new Node { Id = lang, Data = data, CodePage = 0 });
        }

        /// <summary>Lay the tree out the way the linker does: all directory tables + entries, then data entries,
        /// then name strings, then the data blobs (8-byte aligned). Offsets are section-relative, data RVAs absolute.</summary>
        private static byte[] SerializeResources(Node root, uint sectionRva)
        {
            var dirs = new List<Node>();
            var leaves = new List<Node>();
            var names = new List<Node>();
            void Collect(Node n)
            {
                dirs.Add(n);
                foreach (var c in Sorted(n)) { if (c.Name != null) names.Add(c); if (c.IsDir) Collect(c); else leaves.Add(c); }
            }
            Collect(root);
            var dirOffset = new Dictionary<Node, uint>();
            uint pos = 0;
            foreach (var d in dirs) { dirOffset[d] = pos; pos += 16 + 8 * (uint)d.Children.Count; }
            var leafEntryOffset = new Dictionary<Node, uint>();
            foreach (var l in leaves) { leafEntryOffset[l] = pos; pos += 16; }
            var nameOffset = new Dictionary<Node, uint>();
            foreach (var n in names) { nameOffset[n] = pos; pos += 2 + 2 * (uint)n.Name.Length; }
            pos = Align(pos, 8);
            var dataOffset = new Dictionary<Node, uint>();
            foreach (var l in leaves) { dataOffset[l] = pos; pos += Align((uint)l.Data.Length, 8); }
            var buf = new byte[pos];
            foreach (var d in dirs)
            {
                uint o = dirOffset[d];
                var sorted = Sorted(d);
                int named = 0; foreach (var c in sorted) if (c.Name != null) named++;
                WriteU16(buf, o + 12, (ushort)named); WriteU16(buf, o + 14, (ushort)(sorted.Count - named));
                uint e = o + 16;
                foreach (var c in sorted)
                {
                    WriteU32(buf, e, c.Name != null ? (0x80000000u | nameOffset[c]) : c.Id);
                    WriteU32(buf, e + 4, c.IsDir ? (0x80000000u | dirOffset[c]) : leafEntryOffset[c]);
                    e += 8;
                }
            }
            foreach (var l in leaves)
            {
                uint o = leafEntryOffset[l];
                WriteU32(buf, o, sectionRva + dataOffset[l]); WriteU32(buf, o + 4, (uint)l.Data.Length); WriteU32(buf, o + 8, l.CodePage);
                Array.Copy(l.Data, 0, buf, dataOffset[l], l.Data.Length);
            }
            foreach (var n in names)
            {
                uint o = nameOffset[n];
                WriteU16(buf, o, (ushort)n.Name.Length);
                Array.Copy(Encoding.Unicode.GetBytes(n.Name), 0, buf, o + 2, n.Name.Length * 2);
            }
            return buf;
        }

        private static List<Node> Sorted(Node dir)
        {
            var list = new List<Node>(dir.Children);
            list.Sort((a, b) =>
            {
                if ((a.Name != null) != (b.Name != null)) return a.Name != null ? -1 : 1;   // named entries first
                if (a.Name != null) return string.CompareOrdinal(a.Name, b.Name);
                return a.Id.CompareTo(b.Id);
            });
            return list;
        }

        // ------------------------------------------------------------------ ICO parsing

        private sealed class IcoImage { public byte Width, Height, ColorCount; public ushort Planes, BitCount; public byte[] Data; }

        private static List<IcoImage> ParseIco(byte[] ico)
        {
            var list = new List<IcoImage>();
            if (ReadU16(ico, 2) != 1) throw new InvalidDataException("not an .ico file");
            int count = ReadU16(ico, 4);
            for (int i = 0; i < count; i++)
            {
                uint e = 6 + (uint)i * 16;
                var im = new IcoImage { Width = ico[e], Height = ico[e + 1], ColorCount = ico[e + 2], Planes = ReadU16(ico, e + 4), BitCount = ReadU16(ico, e + 6) };
                uint size = ReadU32(ico, e + 8), off = ReadU32(ico, e + 12);
                im.Data = new byte[size];
                Array.Copy(ico, off, im.Data, 0, size);
                list.Add(im);
            }
            return list;
        }

        // ------------------------------------------------------------------ VS_VERSIONINFO

        private static byte[] BuildVersionInfo(string product, string company, string version, string description, string originalFilename)
        {
            ParseVersion(version, out ushort v1, out ushort v2, out ushort v3, out ushort v4);
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            // VS_VERSIONINFO header (lengths patched at the end)
            long start = ms.Position;
            w.Write((ushort)0); w.Write((ushort)52); w.Write((ushort)0); WriteKey(w, "VS_VERSION_INFO"); Pad(ms);
            w.Write(0xFEEF04BDu); w.Write(0x00010000u);
            w.Write(((uint)v1 << 16) | v2); w.Write(((uint)v3 << 16) | v4);
            w.Write(((uint)v1 << 16) | v2); w.Write(((uint)v3 << 16) | v4);
            w.Write(0x3Fu); w.Write(0u); w.Write(0x40004u); w.Write(1u); w.Write(0u); w.Write(0u); w.Write(0u);
            Pad(ms);
            // StringFileInfo
            long sfi = ms.Position;
            w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); WriteKey(w, "StringFileInfo"); Pad(ms);
            long st = ms.Position;
            w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); WriteKey(w, "040904B0"); Pad(ms);
            void Str(string key, string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                long s = ms.Position;
                w.Write((ushort)0); w.Write((ushort)(value.Length + 1)); w.Write((ushort)1); WriteKey(w, key); Pad(ms);
                WriteKey(w, value);
                PatchLen(ms, s); Pad(ms);
            }
            Str("CompanyName", company); Str("FileDescription", description); Str("FileVersion", version);
            Str("InternalName", Path.GetFileNameWithoutExtension(originalFilename)); Str("OriginalFilename", originalFilename);
            Str("ProductName", product); Str("ProductVersion", version);
            PatchLen(ms, st); Pad(ms);
            PatchLen(ms, sfi); Pad(ms);
            // VarFileInfo
            long vfi = ms.Position;
            w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); WriteKey(w, "VarFileInfo"); Pad(ms);
            long var = ms.Position;
            w.Write((ushort)0); w.Write((ushort)4); w.Write((ushort)0); WriteKey(w, "Translation"); Pad(ms);
            w.Write(0x04B00409u);
            PatchLen(ms, var); Pad(ms);
            PatchLen(ms, vfi);
            PatchLen(ms, start);
            return ms.ToArray();
        }

        private static void ParseVersion(string s, out ushort a, out ushort b, out ushort c, out ushort d)
        {
            a = b = c = d = 0;
            if (string.IsNullOrEmpty(s)) { a = 1; return; }
            var parts = s.Split('.', '-', '+', ' ');
            ushort[] v = new ushort[4];
            for (int i = 0; i < 4 && i < parts.Length; i++) { ushort.TryParse(parts[i], out v[i]); }
            a = v[0]; b = v[1]; c = v[2]; d = v[3];
        }

        private static void WriteKey(BinaryWriter w, string s) { w.Write(Encoding.Unicode.GetBytes(s)); w.Write((ushort)0); }
        private static void Pad(MemoryStream ms) { while (ms.Position % 4 != 0) ms.WriteByte(0); }
        private static void PatchLen(MemoryStream ms, long start)
        {
            long end = ms.Position;
            ms.Position = start; var w = new BinaryWriter(ms); w.Write((ushort)(end - start)); ms.Position = end;
        }

        // ------------------------------------------------------------------ helpers

        private static uint Align(uint v, uint a) => a == 0 ? v : (v + a - 1) / a * a;
        private static ushort ReadU16(byte[] b, uint o) => (ushort)(b[o] | (b[o + 1] << 8));
        private static uint ReadU32(byte[] b, uint o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        private static void WriteU16(byte[] b, uint o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        private static void WriteU32(byte[] b, uint o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    }

    /// <summary>Builds .ico files from raw 32-bit BGRA bitmaps (+ an optional PNG-compressed 256px entry).</summary>
    public static class IconContainer
    {
        public sealed class Bitmap32 { public int Width, Height; public byte[] Bgra; }

        public static byte[] BuildIco(IList<Bitmap32> bitmaps, byte[] png256)
        {
            var images = new List<(int w, int h, ushort bits, byte[] data)>();
            foreach (var b in bitmaps) images.Add((b.Width, b.Height, 32, EncodeDib(b)));
            if (png256 != null) images.Add((256, 256, 32, png256));
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
            uint offset = 6 + 16 * (uint)images.Count;
            foreach (var im in images)
            {
                w.Write((byte)(im.w >= 256 ? 0 : im.w)); w.Write((byte)(im.h >= 256 ? 0 : im.h)); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write(im.bits); w.Write((uint)im.data.Length); w.Write(offset);
                offset += (uint)im.data.Length;
            }
            foreach (var im in images) w.Write(im.data);
            return ms.ToArray();
        }

        /// <summary>BITMAPINFOHEADER + bottom-up BGRA XOR rows + 1-bit AND mask (opaque where alpha &gt; 0).</summary>
        private static byte[] EncodeDib(Bitmap32 b)
        {
            int w = b.Width, h = b.Height;
            int maskStride = (w + 31) / 32 * 4;
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((ushort)1); bw.Write((ushort)32); bw.Write(0);
            bw.Write(w * h * 4 + maskStride * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = h - 1; y >= 0; y--) bw.Write(b.Bgra, y * w * 4, w * 4);
            var row = new byte[maskStride];
            for (int y = h - 1; y >= 0; y--)
            {
                Array.Clear(row, 0, row.Length);
                for (int x = 0; x < w; x++) if (b.Bgra[(y * w + x) * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                bw.Write(row);
            }
            return ms.ToArray();
        }
    }
}
