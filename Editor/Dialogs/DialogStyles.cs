using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Editor.Dialogs
{
    /// <summary>
    /// Provides dark theme styles for dialog controls.
    /// Use these methods to create consistently styled controls.
    /// </summary>
    public static class DialogStyles
    {
        // Colors
        public static readonly Color BackgroundColor = Color.FromRgb(30, 30, 30);
        public static readonly Color PanelColor = Color.FromRgb(37, 37, 38);
        public static readonly Color BorderColor = Color.FromRgb(60, 60, 60);
        public static readonly Color TextColor = Color.FromRgb(241, 241, 241);
        public static readonly Color TextSecondaryColor = Color.FromRgb(157, 157, 157);
        public static readonly Color AccentColor = Color.FromRgb(0, 120, 212);
        public static readonly Color ButtonColor = Color.FromRgb(63, 63, 70);
        public static readonly Color ButtonHoverColor = Color.FromRgb(80, 80, 85);
        public static readonly Color DropdownBackgroundColor = Color.FromRgb(45, 45, 48);
        public static readonly Color DropdownItemHoverColor = Color.FromRgb(62, 62, 64);

        // Brushes
        public static SolidColorBrush BackgroundBrush => new SolidColorBrush(BackgroundColor);
        public static SolidColorBrush PanelBrush => new SolidColorBrush(PanelColor);
        public static SolidColorBrush BorderBrush => new SolidColorBrush(BorderColor);
        public static SolidColorBrush TextBrush => new SolidColorBrush(TextColor);
        public static SolidColorBrush TextSecondaryBrush => new SolidColorBrush(TextSecondaryColor);
        public static SolidColorBrush AccentBrush => new SolidColorBrush(AccentColor);
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
        /// Creates a dark-themed Button.
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
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
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
