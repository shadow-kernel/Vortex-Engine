using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;

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
                { typeof(Skybox), comp => CreateSkyboxInspector((Skybox)comp) },
                { typeof(Script), comp => CreateScriptInspector((Script)comp) },
                { typeof(ECS.Components.Physics.BoxCollider), comp => CreateColliderInspector((ECS.Components.Physics.Collider)comp) },
                { typeof(ECS.Components.Physics.SphereCollider), comp => CreateColliderInspector((ECS.Components.Physics.Collider)comp) },
                { typeof(ECS.Components.Physics.CapsuleCollider), comp => CreateColliderInspector((ECS.Components.Physics.Collider)comp) },
                { typeof(ECS.Components.Physics.MeshCollider), comp => CreateColliderInspector((ECS.Components.Physics.Collider)comp) },
                { typeof(ECS.Components.Animation.Animator), comp => CreateAnimatorInspector((ECS.Components.Animation.Animator)comp) },
                { typeof(ECS.Components.Audio.AudioSource), comp => CreateAudioSourceInspector((ECS.Components.Audio.AudioSource)comp) },
                { typeof(ECS.Components.Audio.ReverbZone), comp => CreateReverbZoneInspector((ECS.Components.Audio.ReverbZone)comp) },
            };

            // Accept scripts dropped from the Project Explorer / Asset Browser / Windows Explorer.
            AllowDrop = true;
            DragOver += DynamicInspector_DragOver;
            Drop += DynamicInspector_Drop;
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
            inspector.RemoveRequested += (s, e) => RemoveComponentAndRefresh(meshRenderer);
            return inspector;
        }

        private UserControl CreateCameraInspector(Camera camera)
        {
            var inspector = new CameraInspector();
            inspector.Camera = camera;
            inspector.CameraChanged += OnCameraChanged;
            inspector.PreviewCameraRequested += OnPreviewCameraRequested;
            inspector.RemoveRequested += (s, e) => RemoveComponentAndRefresh(camera);
            return inspector;
        }


        private UserControl CreateLightInspector(Light light)
        {
            var inspector = new LightInspector();
            inspector.Light = light;
            inspector.RemoveRequested += (s, e) => RemoveComponentAndRefresh(light);
            return inspector;
        }

        private UserControl CreateSkyboxInspector(Skybox skybox)
        {
            var inspector = new SkyboxInspector();
            inspector.Skybox = skybox;
            inspector.RemoveRequested += (s, e) => RemoveComponentAndRefresh(skybox);
            return inspector;
        }

        /// <summary>
        /// Remove a component from the selected entity and re-render the inspector — the single path every
        /// component's remove button routes through (mirrors the existing Script "detach"). Also cleans up any
        /// engine resource the component owned and forces the viewport to re-submit so the change shows at once.
        /// </summary>
        private void RemoveComponentAndRefresh(Component component)
        {
            if (_selectedEntity == null || component == null) return;

            // Engine-side cleanup for components that own a native resource.
            if (component is Camera)
                SceneRenderService.Instance.RemoveEntityCamera(_selectedEntity.Id);

            _selectedEntity.RemoveComponent(component);
            RefreshInspector();

            // The editor viewport uses submit-once (it reuses the last scene submission), so nudge it to
            // rebuild the queue — otherwise the removed renderer / light / skybox / collider would linger.
            Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit();
        }

        private UserControl CreateScriptInspector(Script script)
        {
            var inspector = new ScriptInspector();
            inspector.ScriptComponent = script;
            inspector.DetachRequested += (s, e) =>
            {
                if (_selectedEntity != null)
                {
                    _selectedEntity.RemoveComponent(script);
                    RefreshInspector();
                }
            };
            inspector.ChangeRequested += (s, e) =>
            {
                // remove the old, then open the picker to assign a new one
                if (_selectedEntity != null) _selectedEntity.RemoveComponent(script);
                ShowScriptPicker();
            };
            return inspector;
        }

        private UserControl CreateAnimatorInspector(ECS.Components.Animation.Animator animator)
        {
            var inspector = new AnimatorInspector(animator);
            inspector.RemoveRequested += (s, e) => RemoveComponentAndRefresh(animator);
            return inspector;
        }

        // ---- AudioSource + ReverbZone inspectors (issues #12/#15/#19) -----------------
        // Programmatic rows in the dark style; every setter writes the live component,
        // and the edit-mode preview (AudioPreviewService) hears changes immediately.

        private UserControl CreateAudioSourceInspector(ECS.Components.Audio.AudioSource src)
        {
            var panel = InspectorPanel("Audio Source", src, out var body);

            body.Children.Add(AudioTextRow("Audio Clip", () => src.AudioClipPath ?? "", v => src.AudioClipPath = v,
                "Project-relative .wav/.mp3/.ogg/.flac or .vsndc container — drag from the Audio tab"));
            body.Children.Add(AudioSliderRow("Volume", 0, 1, () => src.Volume, v => src.Volume = v));
            body.Children.Add(AudioSliderRow("Pitch", 0.25f, 3, () => src.Pitch, v => src.Pitch = v));
            body.Children.Add(AudioCheckRow("Loop", () => src.Loop, v => src.Loop = v));
            body.Children.Add(AudioCheckRow("Play On Awake", () => src.PlayOnAwake, v => src.PlayOnAwake = v));
            body.Children.Add(AudioCheckRow("Mute", () => src.Mute, v => src.Mute = v));
            body.Children.Add(AudioCheckRow("Streaming (music/long ambience)", () => src.Streaming, v => src.Streaming = v));
            body.Children.Add(AudioComboRow("Output Bus", DllWrapper.VortexAudio.BusNames, () => src.OutputBus, v => src.OutputBus = v));
            body.Children.Add(AudioSliderRow("Spatial Blend (0=2D, 1=3D)", 0, 1, () => src.SpatialBlend, v => src.SpatialBlend = v));
            body.Children.Add(AudioSliderRow("Min Distance", 0.1f, 50, () => src.MinDistance, v => src.MinDistance = v));
            body.Children.Add(AudioSliderRow("Max Distance", 1, 1000, () => src.MaxDistance, v => src.MaxDistance = v));
            body.Children.Add(AudioComboRow("Rolloff", new[] { "Logarithmic", "Linear", "Custom" }, () => (int)src.RolloffMode, v => src.RolloffMode = (ECS.Components.Audio.AudioRolloffMode)v));
            body.Children.Add(AudioSliderRow("Priority (0=highest)", 0, 256, () => src.Priority, v => src.Priority = (int)v));
            body.Children.Add(AudioSliderRow("Stereo Pan", -1, 1, () => src.StereoPan, v => src.StereoPan = v));
            body.Children.Add(AudioSliderRow("Reverb Zone Mix", 0, 1, () => src.ReverbZoneMix, v => src.ReverbZoneMix = v));
            body.Children.Add(AudioSliderRow("Doppler Level", 0, 2, () => src.DopplerLevel, v => src.DopplerLevel = v));
            body.Children.Add(AudioSliderRow("Spread", 0, 360, () => src.Spread, v => src.Spread = v));

            // ---- Steam Audio v2 (issue #21): per-source HRTF binaural + ray-traced occlusion. Opt-in and only
            // active when the project master switch (Audio Mixer window) is on AND phonon.dll is present; otherwise
            // the v1 spatializer is used. Occlusion needs HRTF and a 3D source (Spatial Blend > 0). ----
            body.Children.Add(AudioCheckRow("HRTF binaural (Steam Audio)", () => src.EnableHrtf, v => src.EnableHrtf = v));
            body.Children.Add(AudioCheckRow("Occlusion behind walls (needs HRTF)", () => src.EnableOcclusion, v => src.EnableOcclusion = v));

            // ---- edit-mode preview (issue #19) ----
            var previewRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var indicator = new TextBlock { Text = "", Foreground = InspBrush("#FF7CE0A3"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var spatialToggle = new CheckBox { Content = "Listen from camera (3D)", Foreground = InspBrush("#FFB4B4BC"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), IsChecked = src.SpatialBlend > 0f };
            var play = AudioButton("Preview  ▶", "#FF7CE0A3");
            play.ToolTip = "Play this source now, with the current settings — no play mode needed";
            play.Click += (s, e) =>
            {
                Core.Services.AudioPreviewService.Instance.Start(src, spatialToggle.IsChecked == true);
                indicator.Text = Core.Services.AudioPreviewService.Instance.IsPreviewing(src) ? "playing…" : "";
            };
            var stop = AudioButton("Stop  ■", "#FFB76B7E");
            stop.Click += (s, e) => { Core.Services.AudioPreviewService.Instance.Stop(); indicator.Text = ""; };
            previewRow.Children.Add(play);
            previewRow.Children.Add(stop);
            previewRow.Children.Add(spatialToggle);
            previewRow.Children.Add(indicator);
            body.Children.Add(previewRow);

            return panel;
        }

        private UserControl CreateReverbZoneInspector(ECS.Components.Audio.ReverbZone zone)
        {
            var panel = InspectorPanel("Reverb Zone", zone, out var body);
            body.Children.Add(AudioComboRow("Shape", new[] { "Sphere", "Box" }, () => zone.Shape, v => zone.Shape = v));
            body.Children.Add(AudioSliderRow("Radius (sphere)", 0.5f, 100, () => zone.Radius, v => zone.Radius = v));
            body.Children.Add(AudioSliderRow("Falloff", 0.1f, 20, () => zone.Falloff, v => zone.Falloff = v));
            body.Children.Add(AudioSliderRow("Decay Time (s)", 0.1f, 20, () => zone.DecayTime, v => zone.DecayTime = v));
            body.Children.Add(AudioSliderRow("Wet Level", 0, 1, () => zone.WetLevel, v => zone.WetLevel = v));
            body.Children.Add(AudioSliderRow("Pre-Delay (ms)", 0, 200, () => zone.PreDelayMs, v => zone.PreDelayMs = v));
            body.Children.Add(new TextBlock { Text = "Box half extents edit via the transform-scaled gizmo (issue #18) or the scene file for now.", Foreground = InspBrush("#FF66666E"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            return panel;
        }

        // ---- tiny row builders shared by the audio inspectors ----

        private UserControl InspectorPanel(string title, Component component, out StackPanel body)
        {
            var border = new Border { Background = InspBrush("#FF1E1E22"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(0, 5, 0, 0) };
            var stack = new StackPanel();
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = title, Foreground = InspBrush("#FFE9E9ED"), FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            var rm = new Button { Content = "✕", FontSize = 12, Foreground = InspBrush("#FFB76B7E"), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "Remove component" };
            rm.Click += (s, e) => RemoveComponentAndRefresh(component);
            head.Children.Add(rm);
            stack.Children.Add(head);
            body = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            stack.Children.Add(body);
            border.Child = stack;
            return new UserControl { Content = border };
        }

        private UIElement AudioSliderRow(string label, float min, float max, Func<float> get, Action<float> set)
        {
            var row = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = label, Foreground = InspBrush("#FFB4B4BC"), FontSize = 11 });
            var valueText = new TextBlock { Foreground = InspBrush("#FF8A8A92"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Text = get().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) };
            head.Children.Add(valueText);
            row.Children.Add(head);
            var slider = new Slider { Minimum = min, Maximum = max, Value = get(), SmallChange = (max - min) / 100.0, Foreground = InspBrush("#FF6C5CE7") };
            slider.ValueChanged += (s, e) =>
            {
                set((float)slider.Value);
                valueText.Text = ((float)slider.Value).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            };
            row.Children.Add(slider);
            return row;
        }

        private UIElement AudioCheckRow(string label, Func<bool> get, Action<bool> set)
        {
            var check = new CheckBox { Content = label, Foreground = InspBrush("#FFC8C8CE"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsChecked = get() };
            check.Checked += (s, e) => set(true);
            check.Unchecked += (s, e) => set(false);
            return check;
        }

        private UIElement AudioTextRow(string label, Func<string> get, Action<string> set, string tooltip)
        {
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, Foreground = InspBrush("#FFB4B4BC"), FontSize = 11 });
            var box = new TextBox { Text = get(), Background = InspBrush("#FF141416"), Foreground = InspBrush("#FFE9E9ED"), BorderBrush = InspBrush("#FF2C2C32"), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 3, 0, 0), ToolTip = tooltip };
            box.TextChanged += (s, e) => set(box.Text);
            row.Children.Add(box);
            return row;
        }

        private UIElement AudioComboRow(string label, string[] options, Func<int> get, Action<int> set)
        {
            var row = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, Foreground = InspBrush("#FFB4B4BC"), FontSize = 11 });
            var combo = new ComboBox { Margin = new Thickness(0, 3, 0, 0) };
            foreach (var o in options) combo.Items.Add(o);
            var current = get();
            combo.SelectedIndex = current >= 0 && current < options.Length ? current : 0;
            combo.SelectionChanged += (s, e) => set(combo.SelectedIndex);
            row.Children.Add(combo);
            return row;
        }

        private Button AudioButton(string text, string accent)
        {
            return new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Background = InspBrush("#FF26262B"), Foreground = InspBrush(accent), BorderBrush = InspBrush("#FF3A3A42"), Cursor = System.Windows.Input.Cursors.Hand };
        }

        private static System.Windows.Media.SolidColorBrush InspBrush(string hex)
            => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

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

            var row = new Grid();
            var textBlock = new TextBlock
            {
                Text = component.GetType().Name,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C5C5C5")),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(textBlock);

            // Every component gets a remove button — including ones with no dedicated inspector (e.g. Rigidbody).
            var rm = new Button
            {
                Content = "",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFB76B7E")),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Remove component"
            };
            rm.Click += (s, e) => RemoveComponentAndRefresh(component);
            row.Children.Add(rm);

            border.Child = row;

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
            var audioMenu = new MenuItem { Header = "Audio" };
            AddSubMenuItem(audioMenu, "Audio Source", () => AddComponent<ECS.Components.Audio.AudioSource>());
            AddSubMenuItem(audioMenu, "Audio Listener", () => AddComponent<ECS.Components.Audio.AudioListener>());
            AddSubMenuItem(audioMenu, "Reverb Zone", () => AddComponent<ECS.Components.Audio.ReverbZone>());
            contextMenu.Items.Add(audioMenu);

            contextMenu.Items.Add(new Separator());
            AddComponentMenuItem(contextMenu, "Animator", () => AddComponent<ECS.Components.Animation.Animator>());

            contextMenu.Items.Add(new Separator());
            AddComponentMenuItem(contextMenu, "Rigidbody", () => AddComponent<ECS.Components.Physics.Rigidbody>());
            AddComponentMenuItem(contextMenu, "Box Collider", () => AddComponent<ECS.Components.Physics.BoxCollider>());
            AddComponentMenuItem(contextMenu, "Sphere Collider", () => AddComponent<ECS.Components.Physics.SphereCollider>());
            AddComponentMenuItem(contextMenu, "Capsule Collider", () => AddComponent<ECS.Components.Physics.CapsuleCollider>());
            AddComponentMenuItem(contextMenu, "Mesh Collider (edge-accurate)", () => AddComponent<ECS.Components.Physics.MeshCollider>());
            contextMenu.Items.Add(new Separator());
            AddComponentMenuItem(contextMenu, "Open Collision Editor…", () => Editor.Editors.PhysicsEditor.CollisionEditorWindow.Open(Window.GetWindow(this)));

            // Script: list existing scripts to assign, plus "New Script…"
            contextMenu.Items.Add(new Separator());
            var scriptMenu = new MenuItem { Header = "Script" };
            try
            {
                foreach (var rel in ScriptingService.EnumerateScripts())
                {
                    var r = rel;
                    AddSubMenuItem(scriptMenu, System.IO.Path.GetFileNameWithoutExtension(r), () => AssignScript(r));
                }
            }
            catch { }
            if (scriptMenu.Items.Count > 0) scriptMenu.Items.Add(new Separator());
            AddSubMenuItem(scriptMenu, "New Script…", () => CreateAndAssignScript());
            contextMenu.Items.Add(scriptMenu);

            contextMenu.PlacementTarget = AddComponentButton;
            contextMenu.IsOpen = true;
        }

        /// <summary>Opens the same script-picker menu standalone (used by the inspector's "Change…").</summary>
        private void ShowScriptPicker()
        {
            if (_selectedEntity == null) { RefreshInspector(); return; }
            var menu = new ContextMenu();
            try
            {
                foreach (var rel in ScriptingService.EnumerateScripts())
                {
                    var r = rel;
                    AddComponentMenuItem(menu, System.IO.Path.GetFileNameWithoutExtension(r), () => AssignScript(r));
                }
            }
            catch { }
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            AddComponentMenuItem(menu, "New Script…", () => CreateAndAssignScript());
            menu.PlacementTarget = AddComponentButton;
            menu.IsOpen = true;
            RefreshInspector();
        }

        /// <summary>Attach (or replace) a Script component pointing at the given project-relative .cs path.</summary>
        private void AssignScript(string relativePath)
        {
            if (_selectedEntity == null || string.IsNullOrEmpty(relativePath)) return;

            // Replace any existing Script with the same path; otherwise just add.
            var existing = _selectedEntity.GetComponent<Script>();
            if (existing != null && string.Equals(existing.ScriptPath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                RefreshInspector();
                return;
            }
            _selectedEntity.AddComponent(new Script(_selectedEntity, relativePath));
            RefreshInspector();
        }

        private void CreateAndAssignScript()
        {
            if (_selectedEntity == null) return;
            try
            {
                var baseName = string.IsNullOrWhiteSpace(_selectedEntity.Name) ? "NewBehaviour" : _selectedEntity.Name + "Behaviour";
                var abs = ScriptingService.CreateScript(baseName);
                var rel = ScriptingService.MakeRelative(ScriptingService.ProjectRoot, abs);
                AssignScript(rel);
                ScriptingService.OpenInVisualStudio(abs);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Scripting] CreateAndAssign failed: " + ex.Message); }
        }

        // ---- drag & drop: drop a .cs (script) or an audio clip onto the inspector ----
        private void DynamicInspector_DragOver(object sender, DragEventArgs e)
        {
            string rel;
            e.Effects = (_selectedEntity != null && (TryGetDroppedScript(e, out rel) || TryGetDroppedAudio(e, out rel)))
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DynamicInspector_Drop(object sender, DragEventArgs e)
        {
            string rel;
            if (_selectedEntity != null && TryGetDroppedScript(e, out rel))
            {
                AssignScript(rel);
                e.Handled = true;
                return;
            }
            if (_selectedEntity != null && TryGetDroppedAudio(e, out rel))
            {
                AssignAudioClip(rel);
                e.Handled = true;
            }
        }

        /// <summary>Drop an audio asset (browser tile / project explorer / Windows file)
        /// onto the inspector: assigns the entity's AudioSource clip — adding the
        /// component first if there is none, mirroring the script auto-attach.</summary>
        private void AssignAudioClip(string relativePath)
        {
            if (_selectedEntity == null || string.IsNullOrEmpty(relativePath)) return;

            var source = _selectedEntity.GetComponent<ECS.Components.Audio.AudioSource>();
            if (source == null)
            {
                source = new ECS.Components.Audio.AudioSource(_selectedEntity);
                _selectedEntity.AddComponent(source);
            }
            source.AudioClipPath = relativePath;
            RefreshInspector();
        }

        private static bool TryGetDroppedAudio(DragEventArgs e, out string relativePath)
        {
            relativePath = null;
            string abs = null;

            var fsi = e.Data.GetData("FileSystemItem") as Editor.Editors.WorldEditor.Components.FileExplorer.Models.FileSystemItem;
            if (fsi != null) abs = fsi.FullPath;

            if (abs == null && e.Data.GetDataPresent("AssetPath"))
                abs = e.Data.GetData("AssetPath") as string;

            if (abs == null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var arr = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (arr != null && arr.Length > 0) abs = arr[0];
            }

            if (string.IsNullOrEmpty(abs)) return false;
            var ext = System.IO.Path.GetExtension(abs).ToLowerInvariant();
            if (ext != ".wav" && ext != ".mp3" && ext != ".ogg" && ext != ".flac" && ext != ".vsndc") return false;

            // AudioSource clip paths are project-relative; audio browser tiles already
            // drag relative paths, explorer/Windows drops arrive absolute.
            if (!System.IO.Path.IsPathRooted(abs))
            {
                relativePath = abs.Replace('\\', '/');
                return true;
            }
            var rel = ScriptingService.MakeRelative(ScriptingService.ProjectRoot, abs);
            // MakeRelative returns the absolute path unchanged when the file is OUTSIDE
            // the project — that would store a non-portable path that breaks on another
            // machine. Reject out-of-project drops (the file must be imported first).
            if (string.IsNullOrEmpty(rel) || System.IO.Path.IsPathRooted(rel)) return false;
            relativePath = rel;
            return true;
        }

        private static bool TryGetDroppedScript(DragEventArgs e, out string relativePath)
        {
            relativePath = null;
            string abs = null;

            // From the Project Explorer (FileExplorer drags a FileSystemItem)
            var fsi = e.Data.GetData("FileSystemItem") as Editor.Editors.WorldEditor.Components.FileExplorer.Models.FileSystemItem;
            if (fsi != null) abs = fsi.FullPath;

            // From the Asset Browser (drags an AssetPath)
            if (abs == null && e.Data.GetDataPresent("AssetPath"))
                abs = e.Data.GetData("AssetPath") as string;

            // From Windows Explorer
            if (abs == null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var arr = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (arr != null && arr.Length > 0) abs = arr[0];
            }

            if (string.IsNullOrEmpty(abs)) return false;
            if (!abs.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
            if (abs.EndsWith("VortexScripting.cs", StringComparison.OrdinalIgnoreCase)) return false;

            relativePath = ScriptingService.MakeRelative(ScriptingService.ProjectRoot, abs);
            return true;
        }

        private UserControl CreateColliderInspector(ECS.Components.Physics.Collider col)
        {
            Func<string, System.Windows.Media.Brush> brush = hex => (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex);
            var panel = new StackPanel { Margin = new Thickness(10, 6, 10, 10) };
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock { Text = col.DisplayName, Foreground = brush("#FFE9E9ED"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
            var rmCol = new Button { Content = "", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = brush("#FFB76B7E"), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Remove component" };
            rmCol.Click += (s, e) => RemoveComponentAndRefresh(col);
            header.Children.Add(rmCol);
            panel.Children.Add(header);
            var trig = new CheckBox { Content = "Is Trigger", Foreground = brush("#FFC8C8CE"), IsChecked = col.IsTrigger, Margin = new Thickness(0, 0, 0, 8) };
            trig.Checked += (s, e) => col.IsTrigger = true;
            trig.Unchecked += (s, e) => col.IsTrigger = false;
            panel.Children.Add(trig);
            panel.Children.Add(new TextBlock { Text = "Solid collision shape. Edit size / center and switch shapes in the Collision Editor.", Foreground = brush("#FF8A8A92"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            var btn = new Button { Content = "Open Collision Editor…", Padding = new Thickness(12, 6, 12, 6), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Foreground = System.Windows.Media.Brushes.White, Background = brush("#FF6C5CE7"), BorderThickness = new Thickness(0) };
            btn.Click += (s, e) => { try { Editor.Editors.PhysicsEditor.CollisionEditorWindow.Open(Window.GetWindow(this)); } catch { } };
            panel.Children.Add(btn);
            return new UserControl { Content = panel };
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
