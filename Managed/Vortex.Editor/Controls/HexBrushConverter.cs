using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VortexEditor.Controls
{
    /// <summary>"#RRGGBB" / "#AARRGGBB" strings (the shared core's colour representation) → brush.</summary>
    public sealed class HexBrushConverter : IValueConverter
    {
        public static readonly HexBrushConverter Instance = new HexBrushConverter();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s && !string.IsNullOrEmpty(s))
            {
                try { return new SolidColorBrush(Color.Parse(s)); } catch { }
            }
            return Brushes.Gray;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
