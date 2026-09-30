using System;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Editor.Editors.WorldEditor.Components.FileExplorer.Models;

namespace VortexEditor.Controls
{
    public sealed class FileIconConverter : IValueConverter
    {
        public static readonly FileIconConverter Instance = new FileIconConverter();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is FileSystemItem f) return f.IsDirectory ? "FolderFill" : Shell.AssetPickerDialog.IconFor(f.FullPath);
            if (value is string s) return Directory.Exists(s) ? "FolderFill" : Shell.AssetPickerDialog.IconFor(s);
            return "File";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class FileIconBrushConverter : IValueConverter
    {
        public static readonly FileIconBrushConverter Instance = new FileIconBrushConverter();
        public static string BrushKeyFor(string path, bool isDir)
        {
            if (isDir) return "VxAccentBrush";
            switch (Path.GetExtension(path)?.ToLowerInvariant())
            {
                case ".vmat": return "VxPurpleBrush";
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".bmp": case ".hdr": case ".dds": return "VxTealBrush";
                case ".wav": case ".mp3": case ".ogg": case ".flac": case ".vsndc": return "VxGreenBrush";
                case ".cs": return "VxOrangeBrush";
                case ".ventity": return "VxPinkBrush";
                case ".vscene": return "VxYellowBrush";
                case ".fbx": case ".obj": case ".gltf": case ".glb": case ".dae": case ".3ds": case ".blend": case ".vmesh": return "VxAccentBrush";
                default: return "VxTextSecondaryBrush";
            }
        }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string key = value is FileSystemItem f ? BrushKeyFor(f.FullPath, f.IsDirectory) : value is string s ? BrushKeyFor(s, Directory.Exists(s)) : "VxTextSecondaryBrush";
            return Avalonia.Application.Current != null && Avalonia.Application.Current.TryFindResource(key, out var b) ? b : Brushes.Gray;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
