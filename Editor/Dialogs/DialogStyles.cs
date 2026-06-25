using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Editor.Dialogs
{
    /// <summary>
    /// Provides dark theme styles for dialog controls.
    /// Colors mirror the Vortex design system (Assets/Styles/VortexTheme.xaml) so dialogs are
    /// on-brand with the shell: near-black surfaces + indigo accent (NOT the old VS-Code blue).
    /// </summary>
    public static class DialogStyles
    {
        // Colors — Vortex design tokens
        public static readonly Color BackgroundColor = Color.FromRgb(22, 22, 24);   // Vortex.Bg #161618
        public static readonly Color PanelColor = Color.FromRgb(32, 32, 35);        // Vortex.Surface #202023
        public static readonly Color PanelAltColor = Color.FromRgb(42, 42, 46);     // Vortex.SurfaceAlt #2A2A2E
        public static readonly Color BorderColor = Color.FromRgb(58, 58, 62);       // Vortex.Border #3A3A3E
        public static readonly Color TextColor = Color.FromRgb(245, 245, 247);      // Vortex.Text #F5F5F7
        public static readonly Color TextSecondaryColor = Color.FromRgb(152, 152, 159); // Vortex.TextDim #98989F
        public static readonly Color AccentColor = Color.FromRgb(108, 92, 231);     // Vortex.Accent #6C5CE7
        public static readonly Color AccentHoverColor = Color.FromRgb(126, 112, 238); // Vortex.AccentHover #7E70EE
        public static readonly Color ButtonColor = Color.FromRgb(42, 42, 46);       // surface-alt
        public static readonly Color ButtonHoverColor = Color.FromRgb(50, 50, 55);  // Vortex.SurfaceHover #323237
        public static readonly Color DropdownBackgroundColor = Color.FromRgb(32, 32, 35);
        public static readonly Color DropdownItemHoverColor = Color.FromRgb(50, 50, 55);

        // Brushes
        public static SolidColorBrush BackgroundBrush => new SolidColorBrush(BackgroundColor);
        public static SolidColorBrush PanelBrush => new SolidColorBrush(PanelColor);
        public static SolidColorBrush BorderBrush => new SolidColorBrush(BorderColor);
        public static SolidColorBrush TextBrush => new SolidColorBrush(TextColor);
        public static SolidColorBrush TextSecondaryBrush => new SolidColorBrush(TextSecondaryColor);
        public static SolidColorBrush AccentBrush => new SolidColorBrush(AccentColor);
        public static SolidColorBrush AccentHoverBrush => new SolidColorBrush(AccentHoverColor);
        public static SolidColorBrush ButtonBrush => new SolidColorBrush(ButtonColor);
        public static SolidColorBrush DropdownBackgroundBrush => new SolidColorBrush(DropdownBackgroundColor);

        /// <summary>
        /// Creates a dark-themed ComboBox with readable text.
        /// </summary>
        public static ComboBox CreateComboBox(string[] items, int selectedIndex = 0)
        {
            var combo = new ComboBox
            {
                Background = DropdownBackgroundBrush,
                Foreground = TextBrush,
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 10)
            };

            // Add items with proper styling
            foreach (var item in items)
            {
                var comboItem = new ComboBoxItem
                {
                    Content = item,
                    Background = DropdownBackgroundBrush,
                    Foreground = TextBrush,
                    Padding = new Thickness(8, 6, 8, 6)
                };
                combo.Items.Add(comboItem);
            }

            if (selectedIndex >= 0 && selectedIndex < combo.Items.Count)
                combo.SelectedIndex = selectedIndex;

            // Apply style to make dropdown readable
            combo.Resources.Add(SystemColors.WindowBrushKey, DropdownBackgroundBrush);
            combo.Resources.Add(SystemColors.HighlightBrushKey, new SolidColorBrush(DropdownItemHoverColor));
            combo.Resources.Add(SystemColors.HighlightTextBrushKey, TextBrush);
            combo.Resources.Add(SystemColors.ControlTextBrushKey, TextBrush);

            return combo;
        }

        /// <summary>
        /// Creates a dark-themed TextBox.
        /// </summary>
        public static TextBox CreateTextBox(string text = "", double bottomMargin = 10)
        {
            return new TextBox
            {
                Text = text,
                Background = DropdownBackgroundBrush,
                Foreground = TextBrush,
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                CaretBrush = TextBrush,
                Margin = new Thickness(0, 0, 0, bottomMargin)
            };
        }

        /// <summary>
        /// Creates a rounded "pill" button on the Vortex palette — indigo accent for primary,
        /// surface-alt for secondary, with a hover state. Replaces the old flat default-chrome look.
        /// </summary>
        public static Button CreateButton(string text, double width = double.NaN, bool isPrimary = false)
        {
            return new Button
            {
                Content = text,
                Width = double.IsNaN(width) ? double.NaN : width,
                Background = isPrimary ? AccentBrush : ButtonBrush,
                Foreground = TextBrush,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 7, 14, 7),
                FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal,
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = CreateRoundedButtonTemplate(isPrimary)
            };
        }

        /// <summary>
        /// Rounded button template (Vortex.Radius = 8) with a mouse-over highlight.
        /// </summary>
        private static ControlTemplate CreateRoundedButtonTemplate(bool isPrimary)
        {
            var template = new ControlTemplate(typeof(Button));

            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetBinding(Border.BackgroundProperty,
                new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty,
                new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);

            template.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                isPrimary ? AccentHoverBrush : new SolidColorBrush(ButtonHoverColor), "bd"));
            template.Triggers.Add(hover);

            return template;
        }

        /// <summary>
        /// Creates a dark-themed CheckBox.
        /// </summary>
        public static CheckBox CreateCheckBox(string text, bool isChecked = false, double bottomMargin = 5)
        {
            return new CheckBox
            {
                Content = text,
                Foreground = TextSecondaryBrush,
                IsChecked = isChecked,
                Margin = new Thickness(0, 0, 0, bottomMargin)
            };
        }

        /// <summary>
        /// Creates a section header TextBlock.
        /// </summary>
        public static TextBlock CreateSectionHeader(string text, double topMargin = 10)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextSecondaryBrush,
                Margin = new Thickness(0, topMargin, 0, 8)
            };
        }

        /// <summary>
        /// Creates a label TextBlock.
        /// </summary>
        public static TextBlock CreateLabel(string text, double topMargin = 0)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = TextSecondaryBrush,
                FontSize = 11,
                Margin = new Thickness(0, topMargin, 0, 4)
            };
        }

        /// <summary>
        /// Creates a title TextBlock.
        /// </summary>
        public static TextBlock CreateTitle(string text, double fontSize = 16)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = new Thickness(0, 0, 0, 15)
            };
        }

        /// <summary>
        /// Applies dark theme to an existing ComboBox.
        /// </summary>
        public static void ApplyDarkTheme(ComboBox combo)
        {
            combo.Background = DropdownBackgroundBrush;
            combo.Foreground = TextBrush;
            combo.BorderBrush = BorderBrush;
            
            combo.Resources[SystemColors.WindowBrushKey] = DropdownBackgroundBrush;
            combo.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(DropdownItemHoverColor);
            combo.Resources[SystemColors.HighlightTextBrushKey] = TextBrush;
            combo.Resources[SystemColors.ControlTextBrushKey] = TextBrush;

            // Style existing items
            foreach (var item in combo.Items)
            {
                if (item is ComboBoxItem comboItem)
                {
                    comboItem.Background = DropdownBackgroundBrush;
                    comboItem.Foreground = TextBrush;
                }
            }
        }

        /// <summary>
        /// Creates a dark-themed Slider with value display.
        /// </summary>
        public static (Grid container, Slider slider, TextBlock valueText) CreateSliderRow(
            string label, double min, double max, double value, System.Action<double> onChanged = null)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(labelBlock);

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(slider, 1);
            grid.Children.Add(slider);

            var valueText = new TextBlock
            {
                Text = value.ToString("F2"),
                Foreground = TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(valueText, 2);
            grid.Children.Add(valueText);

            slider.ValueChanged += (s, e) =>
            {
                valueText.Text = e.NewValue.ToString("F2");
                onChanged?.Invoke(e.NewValue);
            };

            return (grid, slider, valueText);
        }
    }
}
