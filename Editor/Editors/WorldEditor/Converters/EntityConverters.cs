using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;

namespace Editor.Editors.WorldEditor.Converters
{
/// <summary>
/// Konvertiert eine GameEntity zu einem Icon-Code basierend auf ihren Komponenten
/// </summary>
public class EntityToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is GameEntity entity)
        {
            // Ordner haben spezielles Icon
            if (entity.IsFolder)
                return entity.IsExpanded ? "\uE838" : "\uE8B7"; // Open/Closed folder
                
            // Priorität: Camera > Light > MeshRenderer > AudioSource > Script > Default
            if (entity.HasComponent<Camera>())
                return "\uE722"; // Camera icon
                
            if (entity.HasComponent<Light>())
                return "\uE793"; // Light bulb icon
                
            if (entity.HasComponent<MeshRenderer>())
                return "\uE809"; // Mesh/3D icon
                
            if (entity.HasComponent<SpriteRenderer>())
                return "\uE8B9"; // Image icon
                
            if (entity.HasComponent<AudioSource>())
                return "\uE767"; // Speaker icon
                
            if (entity.HasComponent<Rigidbody>())
                return "\uE7AD"; // Physics icon
                
            if (entity.HasComponent<Collider>())
                return "\uE73C"; // Shield/Collider icon
                
            if (entity.HasComponent<Script>())
                return "\uE756"; // Code icon
                
            // Check if it has children (container)
            if (entity.Children.Count > 0)
                return "\uE8B7"; // Folder icon
                
            // Default empty entity
            return "\uE734"; // Cube icon
        }
            
        return "\uE734";
    }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }


    /// <summary>
    /// Konvertiert eine GameEntity zu einer Icon-Farbe basierend auf ihren Komponenten
    /// </summary>
    public class EntityToIconColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string colorHex = "#808080"; // Default gray

            if (value is GameEntity entity)
            {
                if (entity.IsFolder)
                    colorHex = "#DCDC8B"; // Yellow for folders
                else if (entity.HasComponent<Camera>())
                    colorHex = "#569CD6"; // Blue
                else if (entity.HasComponent<Light>())
                    colorHex = "#FFD700"; // Gold
                else if (entity.HasComponent<MeshRenderer>())
                    colorHex = "#4EC9B0"; // Teal
                else if (entity.HasComponent<SpriteRenderer>())
                    colorHex = "#C586C0"; // Purple
                else if (entity.HasComponent<AudioSource>())
                    colorHex = "#CE9178"; // Orange
                else if (entity.HasComponent<Rigidbody>() || entity.HasComponent<Collider>())
                    colorHex = "#4FC14F"; // Green
                else if (entity.HasComponent<Script>())
                    colorHex = "#DCDCAA"; // Yellow
                else if (entity.Children.Count > 0)
                    colorHex = "#C5C5C5"; // Light gray for containers
            }

            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Konvertiert ob Entity aktiv ist zu Opacity
    /// </summary>
    public class EntityActiveToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isActive)
            {
                return isActive ? 1.0 : 0.5;
            }
            return 1.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Konvertiert IsSelected zu Background-Farbe für Multi-Select Visualisierung
    /// </summary>
    public class EntitySelectedToBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isSelected && isSelected)
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#37373D"));
            }
            return Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
