using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    public partial class DynamicInspectorView : UserControl
    {
        private GameEntity _selectedEntity;
        private Dictionary<Type, Func<Component, UserControl>> _inspectorFactories;

        public DynamicInspectorView()
        {
            InitializeComponent();
            InitializeInspectorFactories();
            
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void InitializeInspectorFactories()
        {
            _inspectorFactories = new Dictionary<Type, Func<Component, UserControl>>
            {
                // Register component inspectors here
                { typeof(MeshRenderer), comp => CreateMeshRendererInspector((MeshRenderer)comp) },
                { typeof(Camera), comp => CreateCameraInspector((Camera)comp) },
                { typeof(Light), comp => CreateLightInspector((Light)comp) },
                { typeof(Skybox), comp => CreateSkyboxInspector((Skybox)comp) }
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SelectionService.Instance.SelectionChanged += OnSelectionChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            SelectionService.Instance.SelectionChanged -= OnSelectionChanged;
        }

        private void OnSelectionChanged(object sender, SelectionEventArgs e)
        {
            SetEntity(e.SelectedEntity);
        }

        public void SetEntity(GameEntity entity)
        {
            _selectedEntity = entity;
            RefreshInspector();
        }

        private void RefreshInspector()
        {
            DynamicComponentsContainer.Children.Clear();

            if (_selectedEntity == null)
            {
                NoSelectionText.Visibility = Visibility.Visible;
                EntityHeader.Visibility = Visibility.Collapsed;
                AddComponentButton.Visibility = Visibility.Collapsed;
                return;
            }

            NoSelectionText.Visibility = Visibility.Collapsed;
            EntityHeader.Visibility = Visibility.Visible;
            AddComponentButton.Visibility = Visibility.Visible;

            // Update header
            EntityNameTextBox.Text = _selectedEntity.Name;
            EntityActiveCheckBox.IsChecked = _selectedEntity.IsActive;

            // Add Transform inspector (always present)
            var transform = _selectedEntity.GetComponent<Transform>();
            if (transform != null)
            {
                DynamicComponentsContainer.Children.Add(CreateTransformInspector(transform));
            }

            // Add other component inspectors
            foreach (var component in _selectedEntity.Components)
            {
                if (component is Transform) continue; // Already added
                
                var inspector = CreateInspectorForComponent(component);
                if (inspector != null)
                {
                    DynamicComponentsContainer.Children.Add(inspector);
                }
            }
        }

        private UserControl CreateInspectorForComponent(Component component)
        {
            var type = component.GetType();
            
            if (_inspectorFactories.TryGetValue(type, out var factory))
            {
                return factory(component);
            }

            // Return a generic inspector for unknown components
            return CreateGenericComponentInspector(component);
        }

        private UserControl CreateTransformInspector(Transform transform)
        {
            var inspector = new TransformInspector();
            inspector.Transform = transform;
            return inspector;
        }

        private UserControl CreateMeshRendererInspector(MeshRenderer meshRenderer)
        {
            var inspector = new MeshRendererInspector();
            inspector.MeshRenderer = meshRenderer;
            inspector.MeshChanged += OnMeshChanged;
            return inspector;
        }

        private UserControl CreateCameraInspector(Camera camera)
        {
            var inspector = new CameraInspector();
            inspector.Camera = camera;
            inspector.CameraChanged += OnCameraChanged;
            inspector.PreviewCameraRequested += OnPreviewCameraRequested;
            return inspector;
        }


        private UserControl CreateLightInspector(Light light)
        {
            var inspector = new LightInspector();
            inspector.Light = light;
            return inspector;
        }

        private UserControl CreateSkyboxInspector(Skybox skybox)
        {
            var inspector = new SkyboxInspector();
            inspector.Skybox = skybox;
            return inspector;
        }

        private UserControl CreateGenericComponentInspector(Component component)
        {
            // Create a simple border with the component name
            var border = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2D2D30")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 5, 0, 0)
            };

            var textBlock = new TextBlock
            {
                Text = component.GetType().Name,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C5C5C5")),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            };

            border.Child = textBlock;
            
            // Wrap in a UserControl
            var wrapper = new UserControl { Content = border };
            return wrapper;
        }

        private void OnMeshChanged(object sender, string meshType)
        {
            if (_selectedEntity != null)
            {
                SceneRenderService.Instance.OnMeshChanged(_selectedEntity.Id);
            }
        }

        private void OnCameraChanged(object sender, EventArgs e)
        {
            if (_selectedEntity != null)
            {
                // Notify scene that camera properties changed
                SceneRenderService.Instance.OnCameraChanged(_selectedEntity.Id);
            }
        }

        private void OnPreviewCameraRequested(object sender, CameraPreviewEventArgs e)
        {
            // Request viewport to show this camera's view
            CameraPreviewRequested?.Invoke(this, e);
        }

        /// <summary>
        /// Event fired when a camera preview is requested.
        /// </summary>
        public event EventHandler<CameraPreviewEventArgs> CameraPreviewRequested;

        private void EntityNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedEntity != null && !string.IsNullOrWhiteSpace(EntityNameTextBox.Text))
            {
                _selectedEntity.Name = EntityNameTextBox.Text;
            }
        }

        private void AddComponentButton_Click(object sender, RoutedEventArgs e)
        {
            // Show component picker popup
            var contextMenu = new ContextMenu();
            
            AddComponentMenuItem(contextMenu, "Mesh Renderer", () => AddComponent<MeshRenderer>());
            AddComponentMenuItem(contextMenu, "Camera", () => AddComponent<Camera>());
            contextMenu.Items.Add(new Separator());
            
            // Light submenu
            var lightMenu = new MenuItem { Header = "Light" };
            AddSubMenuItem(lightMenu, "Directional Light", () => AddLightComponent(LightType.Directional));
            AddSubMenuItem(lightMenu, "Point Light", () => AddLightComponent(LightType.Point));
            AddSubMenuItem(lightMenu, "Spot Light", () => AddLightComponent(LightType.Spot));
            contextMenu.Items.Add(lightMenu);
            
            contextMenu.Items.Add(new Separator());
            AddComponentMenuItem(contextMenu, "Skybox", () => AddComponent<Skybox>());
            
            contextMenu.Items.Add(new Separator());
            AddComponentMenuItem(contextMenu, "Rigidbody", () => AddComponent<ECS.Components.Physics.Rigidbody>());
            AddComponentMenuItem(contextMenu, "Box Collider", () => AddComponent<ECS.Components.Physics.BoxCollider>());

            contextMenu.PlacementTarget = AddComponentButton;
            contextMenu.IsOpen = true;
        }

        private void AddComponentMenuItem(ContextMenu menu, string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => action();
            menu.Items.Add(item);
        }

        private void AddSubMenuItem(MenuItem parent, string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => action();
            parent.Items.Add(item);
        }

        private void AddComponent<T>() where T : Component, new()
        {
            if (_selectedEntity == null) return;
            
            if (!_selectedEntity.HasComponent<T>())
            {
                _selectedEntity.AddComponent<T>();
                RefreshInspector();
            }
        }

        private void AddLightComponent(LightType lightType)
        {
            if (_selectedEntity == null) return;
            
            if (!_selectedEntity.HasComponent<Light>())
            {
                var light = new Light(_selectedEntity, lightType);
                _selectedEntity.AddComponent(light);
                RefreshInspector();
            }
        }
    }
}
