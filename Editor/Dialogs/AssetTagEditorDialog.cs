using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Assets;

namespace Editor.Dialogs
{
    /// <summary>
    /// Dialog for editing tags on a single asset.
    /// </summary>
    public class AssetTagEditorDialog : Window
    {
        private readonly Guid _assetGuid;
        private readonly ObservableCollection<string> _currentTags;
        private TextBox _newTagTextBox;
        private ListBox _tagsList;
        private WrapPanel _predefinedTagsPanel;

        /// <summary>
        /// The resulting tags after editing.
        /// </summary>
        public IReadOnlyCollection<string> ResultTags => _currentTags.ToList().AsReadOnly();

        /// <summary>
        /// Creates a tag editor dialog, loading existing tags from the AssetTagService.
        /// </summary>
        public AssetTagEditorDialog(Guid assetGuid, string assetName) 
            : this(assetGuid, assetName, AssetTagService.Instance.GetTags(assetGuid))
        {
        }

        public AssetTagEditorDialog(Guid assetGuid, string assetName, IEnumerable<string> existingTags)
        {
            _assetGuid = assetGuid;
            _currentTags = new ObservableCollection<string>(existingTags ?? Enumerable.Empty<string>());
            
            Title = "Edit Tags";
            Width = 450;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 24));
            ResizeMode = ResizeMode.NoResize;
            
            BuildUI(assetName);
        }

        private void BuildUI(string assetName)
        {
            var mainStack = new StackPanel { Margin = new Thickness(20) };

            // Asset name header
            mainStack.Children.Add(new TextBlock
            {
                Text = "Asset:",
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 3)
            });
            mainStack.Children.Add(new TextBlock
            {
                Text = assetName,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 15)
            });

            // Current tags section
            mainStack.Children.Add(new TextBlock
            {
                Text = "Current Tags:",
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 5)
            });

            var tagsBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                MinHeight = 60,
                MaxHeight = 100
            };

            _tagsList = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemsSource = _currentTags,
                Foreground = Brushes.White
            };
            _tagsList.ItemTemplate = CreateTagItemTemplate();
            tagsBorder.Child = _tagsList;
            mainStack.Children.Add(tagsBorder);

            // Add new tag section
            mainStack.Children.Add(new TextBlock
            {
                Text = "Add Tag:",
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                FontSize = 11,
                Margin = new Thickness(0, 15, 0, 5)
            });

            var addTagPanel = new Grid();
            addTagPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            addTagPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _newTagTextBox = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                Padding = new Thickness(8, 6, 8, 6)
            };
            _newTagTextBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) AddTagFromTextBox(); };
            addTagPanel.Children.Add(_newTagTextBox);

            var addButton = new Button
            {
                Content = "Add",
                Width = 60,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(108, 92, 231)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(108, 92, 231)),
                Padding = new Thickness(0, 6, 0, 6)
            };
            addButton.Click += (s, e) => AddTagFromTextBox();
            Grid.SetColumn(addButton, 1);
            addTagPanel.Children.Add(addButton);

            mainStack.Children.Add(addTagPanel);

            // Predefined tags section
            mainStack.Children.Add(new TextBlock
            {
                Text = "Quick Add:",
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                FontSize = 11,
                Margin = new Thickness(0, 15, 0, 5)
            });

            _predefinedTagsPanel = new WrapPanel();
            foreach (var tag in AssetTagService.PredefinedTags)
            {
                var btn = new Button
                {
                    Content = "+ " + tag,
                    Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
                    Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 75)),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 6, 6),
                    Cursor = Cursors.Hand,
                    Tag = tag
                };
                btn.Click += (s, e) => AddTagIfNotExists((string)((Button)s).Tag);
                _predefinedTagsPanel.Children.Add(btn);
            }
            mainStack.Children.Add(_predefinedTagsPanel);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 80,
                Padding = new Thickness(0, 8, 0, 8),
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85))
            };
            cancelButton.Click += (s, e) => { DialogResult = false; Close(); };
            buttonPanel.Children.Add(cancelButton);

            var saveButton = new Button
            {
                Content = "Save",
                Width = 80,
                Padding = new Thickness(0, 8, 0, 8),
                Background = new SolidColorBrush(Color.FromRgb(108, 92, 231)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(108, 92, 231))
            };
            saveButton.Click += (s, e) => { SaveTags(); DialogResult = true; Close(); };
            buttonPanel.Children.Add(saveButton);

            mainStack.Children.Add(buttonPanel);

            Content = mainStack;
        }

        private DataTemplate CreateTagItemTemplate()
        {
            var template = new DataTemplate();
            
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(108, 92, 231)));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            factory.SetValue(Border.PaddingProperty, new Thickness(10, 4, 10, 4));
            factory.SetValue(Border.MarginProperty, new Thickness(0, 2, 6, 2));
            
            var stackFactory = new FrameworkElementFactory(typeof(StackPanel));
            stackFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            
            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
            textFactory.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            textFactory.SetValue(TextBlock.FontSizeProperty, 11.0);
            textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            stackFactory.AppendChild(textFactory);
            
            var removeFactory = new FrameworkElementFactory(typeof(Button));
            removeFactory.SetValue(Button.ContentProperty, "�");
            removeFactory.SetValue(Button.BackgroundProperty, Brushes.Transparent);
            removeFactory.SetValue(Button.ForegroundProperty, Brushes.White);
            removeFactory.SetValue(Button.BorderThicknessProperty, new Thickness(0));
            removeFactory.SetValue(Button.FontSizeProperty, 14.0);
            removeFactory.SetValue(Button.MarginProperty, new Thickness(6, 0, 0, 0));
            removeFactory.SetValue(Button.PaddingProperty, new Thickness(2, 0, 2, 0));
            removeFactory.SetValue(Button.CursorProperty, Cursors.Hand);
            removeFactory.AddHandler(Button.ClickEvent, new RoutedEventHandler(RemoveTag_Click));
            stackFactory.AppendChild(removeFactory);
            
            factory.AppendChild(stackFactory);
            template.VisualTree = factory;
            
            return template;
        }

        private void RemoveTag_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var tag = button?.DataContext as string;
            if (tag != null)
            {
                _currentTags.Remove(tag);
            }
        }

        private void AddTagFromTextBox()
        {
            var tag = _newTagTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(tag))
            {
                AddTagIfNotExists(tag);
                _newTagTextBox.Clear();
                _newTagTextBox.Focus();
            }
        }

        private void AddTagIfNotExists(string tag)
        {
            if (!_currentTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                _currentTags.Add(tag);
            }
        }

        private void SaveTags()
        {
            // Save tags to the AssetTagService
            AssetTagService.Instance.SetTags(_assetGuid, _currentTags);
        }
    }
}
