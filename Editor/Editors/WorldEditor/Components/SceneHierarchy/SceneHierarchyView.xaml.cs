using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Editor.Editors.WorldEditor.Components.SceneHierarchy
{
    public partial class SceneHierarchyView : UserControl
    {
        public SceneHierarchyView()
        {
            InitializeComponent();
            LoadDummyData();
        }

        private void LoadDummyData()
        {
            // Main Scene
            var mainScene = CreateTreeItem("Main Scene", "\uE81E", "#DCDCAA"); // Scene icon - yellow
            mainScene.IsExpanded = true;

            // Camera
            mainScene.Items.Add(CreateTreeItem("Main Camera", "\uE722", "#569CD6")); // Camera icon - blue

            // Lights
            mainScene.Items.Add(CreateTreeItem("Directional Light", "\uE793", "#FFD700")); // Light icon - gold
            mainScene.Items.Add(CreateTreeItem("Point Light", "\uE793", "#FFD700"));
            mainScene.Items.Add(CreateTreeItem("Spot Light", "\uE793", "#FFD700"));

            // Player (Prefab)
            var player = CreateTreeItem("Player", "\uE74C", "#3FA9F5"); // Prefab icon - light blue
            player.IsExpanded = true;
            player.Items.Add(CreateTreeItem("PlayerMesh", "\uE809", "#4EC9B0")); // Mesh icon - teal
            player.Items.Add(CreateTreeItem("PlayerCollider", "\uE73C", "#4FC14F")); // Collider icon - green
            player.Items.Add(CreateTreeItem("FootstepAudio", "\uE767", "#CE9178")); // Audio icon - orange
            mainScene.Items.Add(player);

            // Environment (Empty Container)
            var environment = CreateTreeItem("Environment", "\uE734", "#808080"); // Empty icon - gray
            environment.IsExpanded = true;
            environment.Items.Add(CreateTreeItem("Ground", "\uE809", "#4EC9B0"));
            environment.Items.Add(CreateTreeItem("Skybox", "\uE809", "#4EC9B0"));

            var props = CreateTreeItem("Props", "\uE734", "#808080");
            props.Items.Add(CreateTreeItem("Tree_01", "\uE809", "#4EC9B0"));
            props.Items.Add(CreateTreeItem("Tree_02", "\uE809", "#4EC9B0"));
            props.Items.Add(CreateTreeItem("Rock_01", "\uE809", "#4EC9B0"));
            props.Items.Add(CreateTreeItem("Barrel_01", "\uE74C", "#3FA9F5"));
            environment.Items.Add(props);
            mainScene.Items.Add(environment);

            // UI Canvas
            var uiCanvas = CreateTreeItem("UI Canvas", "\uE8A1", "#C586C0"); // Canvas icon - purple
            uiCanvas.IsExpanded = true;
            uiCanvas.Items.Add(CreateTreeItem("HealthBar", "\uEA86", "#C5C5C5")); // GameObject icon - light gray
            uiCanvas.Items.Add(CreateTreeItem("ScoreText", "\uEA86", "#C5C5C5"));
            uiCanvas.Items.Add(CreateTreeItem("PauseMenu", "\uEA86", "#C5C5C5"));
            mainScene.Items.Add(uiCanvas);

            SceneTree.Items.Add(mainScene);
        }

        private TreeViewItem CreateTreeItem(string name, string iconCode, string colorHex)
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            var brush = new SolidColorBrush(color);

            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };

            var icon = new TextBlock
            {
                Text = iconCode,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var text = new TextBlock
            {
                Text = name,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C5C5C5")),
                VerticalAlignment = VerticalAlignment.Center
            };

            stackPanel.Children.Add(icon);
            stackPanel.Children.Add(text);

            return new TreeViewItem
            {
                Header = stackPanel,
                Style = (Style)FindResource("SceneTreeItemStyle")
            };
        }
    }
}
