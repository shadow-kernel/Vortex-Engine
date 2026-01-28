using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Editor.Dialogs
{
    /// <summary>
    /// HSV-based Color Picker Dialog for PBR material editing.
    /// </summary>
    public partial class ColorPickerDialog : Window
    {
        private bool _isUpdating = false;
        private bool _isDraggingSquare = false;
        private bool _isDraggingHue = false;
        
        private double _hue = 0;        // 0-360
        private double _saturation = 1; // 0-1
        private double _value = 1;      // 0-1
        
        public Color SelectedColor { get; set; } = Colors.White;
        
        public ColorPickerDialog()
        {
            InitializeComponent();
            Loaded += ColorPickerDialog_Loaded;
        }
        
        public ColorPickerDialog(Color initialColor) : this()
        {
            SelectedColor = initialColor;
        }
        
        private void ColorPickerDialog_Loaded(object sender, RoutedEventArgs e)
        {
            // Convert initial color to HSV
            RgbToHsv(SelectedColor.R, SelectedColor.G, SelectedColor.B, 
                     out _hue, out _saturation, out _value);
            
            UpdateFromHsv();
            UpdateSelectors();
        }
        
        #region Color Square (Saturation/Value)
        
        private void ColorSquare_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSquare = true;
            Mouse.Capture(ColorSquare);
            UpdateSaturationValue(e.GetPosition(ColorSquare));
        }
        
        private void ColorSquare_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingSquare)
            {
                UpdateSaturationValue(e.GetPosition(ColorSquare));
            }
        }
        
        private void ColorSquare_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSquare = false;
            Mouse.Capture(null);
        }
        
        private void UpdateSaturationValue(Point pos)
        {
            double width = ColorSquare.ActualWidth;
            double height = ColorSquare.ActualHeight;
            
            _saturation = Math.Max(0, Math.Min(1, pos.X / width));
            _value = Math.Max(0, Math.Min(1, 1 - pos.Y / height));
            
            UpdateFromHsv();
            UpdateSelectors();
        }
        
        #endregion
        
        #region Hue Slider
        
        private void HueSlider_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingHue = true;
            var parent = (FrameworkElement)sender;
            Mouse.Capture(parent);
            UpdateHue(e.GetPosition(parent));
        }
        
        private void HueSlider_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingHue)
            {
                UpdateHue(e.GetPosition((FrameworkElement)sender));
            }
        }
        
        private void HueSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingHue = false;
            Mouse.Capture(null);
        }
        
        private void UpdateHue(Point pos)
        {
            var parent = (FrameworkElement)HueSelector.Parent;
            double height = parent.ActualHeight;
            
            _hue = Math.Max(0, Math.Min(360, (pos.Y / height) * 360));
            
            // Update the hue color for the square gradient
            var hueColor = HsvToRgb(_hue, 1, 1);
            HueColor.Color = hueColor;
            
            UpdateFromHsv();
            UpdateSelectors();
        }
        
        #endregion
        
        #region RGB Sliders
        
        private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdating) return;
            if (!IsLoaded) return; // Prevent crash during initialization
            if (RedValue == null || GreenValue == null || BlueValue == null) return;
            
            _isUpdating = true;
            
            byte r = (byte)RedSlider.Value;
            byte g = (byte)GreenSlider.Value;
            byte b = (byte)BlueSlider.Value;
            
            RedValue.Text = r.ToString();
            GreenValue.Text = g.ToString();
            BlueValue.Text = b.ToString();
            
            SelectedColor = Color.FromRgb(r, g, b);
            RgbToHsv(r, g, b, out _hue, out _saturation, out _value);
            
            UpdatePreview();
            UpdateHex();
            UpdateSelectors();
            
            // Update hue gradient
            if (HueColor != null)
            {
                var hueColor = HsvToRgb(_hue, 1, 1);
                HueColor.Color = hueColor;
            }
            
            _isUpdating = false;
        }
        
        private void RgbTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating) return;
            if (!IsLoaded) return; // Prevent crash during initialization
            if (RedValue == null || GreenValue == null || BlueValue == null) return;
            
            if (byte.TryParse(RedValue.Text, out byte r) &&
                byte.TryParse(GreenValue.Text, out byte g) &&
                byte.TryParse(BlueValue.Text, out byte b))
            {
                _isUpdating = true;
                
                RedSlider.Value = r;
                GreenSlider.Value = g;
                BlueSlider.Value = b;
                
                SelectedColor = Color.FromRgb(r, g, b);
                RgbToHsv(r, g, b, out _hue, out _saturation, out _value);
                
                UpdatePreview();
                UpdateHex();
                UpdateSelectors();
                
                _isUpdating = false;
            }
        }
        
        #endregion
        
        #region Hex
        
        private void HexValue_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating) return;
            
            string hex = HexValue.Text.TrimStart('#');
            if (hex.Length == 6)
            {
                try
                {
                    byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                    
                    _isUpdating = true;
                    
                    SelectedColor = Color.FromRgb(r, g, b);
                    RgbToHsv(r, g, b, out _hue, out _saturation, out _value);
                    
                    RedSlider.Value = r;
                    GreenSlider.Value = g;
                    BlueSlider.Value = b;
                    RedValue.Text = r.ToString();
                    GreenValue.Text = g.ToString();
                    BlueValue.Text = b.ToString();
                    
                    UpdatePreview();
                    UpdateSelectors();
                    
                    _isUpdating = false;
                }
                catch { }
            }
        }
        
        #endregion
        
        #region Update Methods
        
        private void UpdateFromHsv()
        {
            if (_isUpdating) return;
            if (!IsLoaded) return; // Prevent crash during initialization
            if (RedSlider == null || GreenSlider == null || BlueSlider == null) return;
            if (RedValue == null || GreenValue == null || BlueValue == null) return;
            
            _isUpdating = true;
            
            SelectedColor = HsvToRgb(_hue, _saturation, _value);
            
            RedSlider.Value = SelectedColor.R;
            GreenSlider.Value = SelectedColor.G;
            BlueSlider.Value = SelectedColor.B;
            
            RedValue.Text = SelectedColor.R.ToString();
            GreenValue.Text = SelectedColor.G.ToString();
            BlueValue.Text = SelectedColor.B.ToString();
            
            UpdatePreview();
            UpdateHex();
            
            _isUpdating = false;
        }
        
        private void UpdatePreview()
        {
            if (ColorPreview != null)
                ColorPreview.Background = new SolidColorBrush(SelectedColor);
        }
        
        private void UpdateHex()
        {
            if (HexValue != null)
                HexValue.Text = $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
        }
        
        private void UpdateSelectors()
        {
            if (!IsLoaded) return;
            
            // Update color square selector position
            if (ColorSquare != null && ColorSelector != null)
            {
                double squareWidth = ColorSquare.ActualWidth;
                double squareHeight = ColorSquare.ActualHeight;
                
                if (squareWidth > 0 && squareHeight > 0)
                {
                    Canvas.SetLeft(ColorSelector, _saturation * squareWidth - 8);
                    Canvas.SetTop(ColorSelector, (1 - _value) * squareHeight - 8);
                }
            }
            
            // Update hue slider selector position
            if (HueSelector != null)
            {
                var hueParent = HueSelector.Parent as Canvas;
                if (hueParent != null)
                {
                    var border = hueParent.Parent as Border;
                    if (border != null && border.ActualHeight > 0)
                    {
                        Canvas.SetTop(HueSelector, (_hue / 360.0) * border.ActualHeight - 3);
                    }
                }
            }
        }
        
        #endregion
        
        #region Color Conversion
        
        private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rd = r / 255.0;
            double gd = g / 255.0;
            double bd = b / 255.0;
            
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double delta = max - min;
            
            v = max;
            s = max == 0 ? 0 : delta / max;
            
            if (delta == 0)
            {
                h = 0;
            }
            else if (max == rd)
            {
                h = 60 * (((gd - bd) / delta) % 6);
            }
            else if (max == gd)
            {
                h = 60 * (((bd - rd) / delta) + 2);
            }
            else
            {
                h = 60 * (((rd - gd) / delta) + 4);
            }
            
            if (h < 0) h += 360;
        }
        
        private static Color HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;
            
            double r, g, b;
            
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            
            return Color.FromRgb(
                (byte)((r + m) * 255),
                (byte)((g + m) * 255),
                (byte)((b + m) * 255)
            );
        }
        
        #endregion
        
        #region Buttons
        
        private void OK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
        
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
        
        #endregion
    }
}
