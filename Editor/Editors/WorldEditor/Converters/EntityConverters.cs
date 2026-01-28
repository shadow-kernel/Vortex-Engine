using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Editor.Core.Data;
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
            {
                // Check if any child has a MeshRenderer (model container)
                bool hasChildMeshes = entity.Children.Any(c => c.HasComponent<MeshRenderer>());
                if (hasChildMeshes)
                    return "\uE809"; // Mesh/3D icon for model containers
                return "\uE8B7"; // Folder icon for regular containers
            }
                
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
                {
                    // Main Camera = Purple, Game Camera = Blue
                    var cam = entity.GetComponent<Camera>();
                    if (cam != null && cam.CameraType == CameraType.MainCamera)
                        colorHex = "#9B59B6"; // Purple for Main Camera
                    else
                        colorHex = "#569CD6"; // Blue for Game Camera
                }
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
                {
                    // Check if any child has a MeshRenderer (model container)
                    bool hasChildMeshes = entity.Children.Any(c => c.HasComponent<MeshRenderer>());
                    if (hasChildMeshes)
                        colorHex = "#4EC9B0"; // Teal for model containers
                    else
                        colorHex = "#C5C5C5"; // Light gray for regular containers
                }
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

    /// <summary>
    /// Konvertiert eine Szene zu Visibility basierend auf Aktivitätsstatus.
    /// Gibt Visible zurück wenn die Szene die aktive Szene ist.
    /// Mit ConverterParameter="Invert" wird das Ergebnis umgekehrt.
    /// </summary>
	public class SceneActiveConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			bool isActive = false;

			if (value is bool activeFlag)
			{
				isActive = activeFlag;
			}
			else if (value is Core.Data.Scene scene)
			{
				isActive = scene.IsActive || scene.Project?.ActiveScene == scene;
			}

			// Invertieren wenn Parameter gesetzt
			if (parameter is string param && param == "Invert")
			{
				isActive = !isActive;
			}

			return isActive ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotImplementedException();
		}
	}

    /// <summary>
    /// Konvertiert eine Szene zu einer Farbe basierend auf Aktivitätsstatus.
    /// Aktive Szene: Grün (#4EC9B0), Inaktive Szene: Gelb/Grau (#DCDCAA / #808080)
    /// </summary>
	public class SceneActiveToIconConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			string colorHex = "#808080"; // Default gray for inactive

			if (value is bool isActive)
			{
				colorHex = isActive ? "#4EC9B0" : "#DCDCAA"; // Green for active, Yellow for inactive
			}
			else if (value is Core.Data.Scene scene)
			{
				bool active = scene.IsActive || scene.Project?.ActiveScene == scene;
				colorHex = active ? "#4EC9B0" : "#DCDCAA";
			}

			return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotImplementedException();
		}
	}
}
