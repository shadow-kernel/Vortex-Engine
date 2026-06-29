using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.Core.Assets;
using Editor.Editors.WorldEditor.Components.GamePreview;
using Editor.Editors.WorldEditor.Components.ModelViewer;

namespace Editor.Dialogs
{
    /// <summary>
    /// A dedicated Mesh inspector window (same dark design as the Material Editor): a large live 3D preview of the
    /// mesh on the right (orbit/zoom/WASD) and the geometry breakdown on the left (format, vertex/triangle counts,
    /// submesh list). Opened for a referenced mesh from the model editor.
    /// </summary>
    public class MeshEditorDialog : Window
    {
        public MeshEditorDialog(string modelPath)
        {
            UniversalModelData data = null;
            try { data = UniversalModelParser.Instance.ParseModel(modelPath); } catch { }

            Title = "Mesh Editor - " + Path.GetFileName(modelPath);
            Width = 1200;
            Height = 800;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 24));
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 900;
            MinHeight = 600;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            grid.Children.Add(BuildInfoPanel(data, modelPath));

            // Center/right: the live mesh viewer (orbit / zoom / WASD) — reuses the model-viewer control.
            try
            {
                var viewer = new ModelViewerControl(modelPath, Path.GetFileNameWithoutExtension(modelPath));
                Grid.SetColumn(viewer, 1);
                grid.Children.Add(viewer);
            }
            catch { }

            Content = grid;

            // Pause the main viewport while this live preview is open (shared render path).
            GamePreviewView.ActivePreviewDialogs++;
            Closed += (s, e) =>
            {
                try { GamePreviewView.ActivePreviewDialogs--; GamePreviewView.RequestResubmit(); } catch { }
            };
        }

        private Border BuildInfoPanel(UniversalModelData data, string modelPath)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 0, 1, 0)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // header
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // stats
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // submeshes header
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // submeshes

            // Header
            var header = new StackPanel { Margin = new Thickness(14, 14, 14, 10) };
            header.Children.Add(new TextBlock
            {
                Text = data?.FileName ?? Path.GetFileName(modelPath),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            header.Children.Add(new TextBlock
            {
                Text = data?.FormatName ?? Path.GetExtension(modelPath).TrimStart('.').ToUpperInvariant(),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(108, 92, 231)),
                Margin = new Thickness(0, 2, 0, 0)
            });
            grid.Children.Add(header);

            // Stats
            var stats = new TextBlock
            {
                Text = data?.StatsSummary ?? "",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 195)),
                Margin = new Thickness(14, 0, 14, 14),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(stats, 1);
            grid.Children.Add(stats);

            // Submeshes header
            var subHeader = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                Padding = new Thickness(14, 8, 14, 8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 1, 0, 1)
            };
            subHeader.Child = new TextBlock
            {
                Text = "SUBMESHES",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157))
            };
            Grid.SetRow(subHeader, 2);
            grid.Children.Add(subHeader);

            // Submesh list
            var list = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6),
                Foreground = Brushes.White
            };
            if (data?.Submeshes != null)
            {
                list.ItemsSource = data.Submeshes;
                var tpl = new DataTemplate();
                var f = new FrameworkElementFactory(typeof(StackPanel));
                f.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 4, 2, 4));
                var name = new FrameworkElementFactory(typeof(TextBlock));
                name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DisplayName"));
                name.SetValue(TextBlock.ForegroundProperty, Brushes.White);
                name.SetValue(TextBlock.FontSizeProperty, 12.0);
                f.AppendChild(name);
                var info = new FrameworkElementFactory(typeof(TextBlock));
                info.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("GeometryInfo"));
                info.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 150, 155)));
                info.SetValue(TextBlock.FontSizeProperty, 10.0);
                f.AppendChild(info);
                tpl.VisualTree = f;
                list.ItemTemplate = tpl;
            }
            Grid.SetRow(list, 3);
            grid.Children.Add(list);

            border.Child = grid;
            return border;
        }

        public static void OpenMesh(Window owner, string modelPath)
        {
            try
            {
                var dlg = new MeshEditorDialog(modelPath);
                if (owner != null) dlg.Owner = owner;
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the Mesh Editor:\n" + ex.Message, "Mesh Editor",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
