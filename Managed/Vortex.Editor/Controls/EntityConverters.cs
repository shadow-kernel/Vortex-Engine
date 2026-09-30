using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Editor.ECS;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;

namespace VortexEditor.Controls
{
    /// <summary>Entity → icon name, from its most characteristic component.</summary>
    public sealed class EntityIconConverter : IValueConverter
    {
        public static readonly EntityIconConverter Instance = new EntityIconConverter();
        public static string IconFor(GameEntity e)
        {
            if (e == null) return "Cube";
            if (e.GetComponent<Camera>() != null) return "Camera";
            if (e.GetComponent<Light>() != null) return "Light";
            if (e.GetComponent<Skybox>() != null) return "World";
            if (e.GetComponent<AudioSource>() != null || e.GetComponent<ReverbZone>() != null) return "Audio";
            if (e.GetComponent<MeshRenderer>() != null) return "Cube";
            if (e.GetComponent<Script>() != null) return "Script";
            return e.Children != null && e.Children.Count > 0 ? "Folder" : "Cube";
        }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => IconFor(value as GameEntity);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class EntityIconBrushConverter : IValueConverter
    {
        public static readonly EntityIconBrushConverter Instance = new EntityIconBrushConverter();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var e = value as GameEntity;
            string key = "VxTextSecondaryBrush";
            if (e != null)
            {
                if (e.GetComponent<Camera>() != null) key = "VxPurpleBrush";
                else if (e.GetComponent<Light>() != null) key = "VxYellowBrush";
                else if (e.GetComponent<Skybox>() != null) key = "VxTealBrush";
                else if (e.GetComponent<AudioSource>() != null || e.GetComponent<ReverbZone>() != null) key = "VxGreenBrush";
                else if (e.GetComponent<Script>() != null) key = "VxOrangeBrush";
                else if (e.GetComponent<MeshRenderer>() != null) key = "VxAccentBrush";
            }
            return Avalonia.Application.Current != null && Avalonia.Application.Current.TryFindResource(key, out var b) ? b : Brushes.Gray;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class EntityOpacityConverter : IValueConverter
    {
        public static readonly EntityOpacityConverter Instance = new EntityOpacityConverter();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is GameEntity e && !e.IsActive ? 0.45 : 1.0;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
