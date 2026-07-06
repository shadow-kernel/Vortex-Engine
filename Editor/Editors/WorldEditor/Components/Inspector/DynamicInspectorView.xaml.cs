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

        /// <summary>When true this inspector IGNORES the global SelectionService and is driven ONLY by SetEntity — so
        /// it can edit a standalone entity (the isolated Prefab Editor) without the main scene's selection stealing it.
        /// Set BEFORE the control is loaded.</summary>
        public bool IsolatedMode { get; set; }

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
                { typeof(ECS.Components.Animation.BoneAttachment), comp => CreateBoneAttachmentInspector((ECS.Components.Animation.BoneAttachment)comp) },
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
            if (IsolatedMode) return;   // isolated Prefab Editor drives this inspector only via SetEntity
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

            // Prefab instance: show which prefab this object comes from, with a button to select it in the Explorer.
            if (_selectedEntity.IsPrefabInstance)
                DynamicComponentsContainer.Children.Add(CreatePrefabRow(_selectedEntity));

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

        /// <summary>A small header card shown for a prefab INSTANCE: which prefab it comes from + a button that
        /// selects that prefab in the Asset Browser.</summary>
        private UIElement CreatePrefabRow(GameEntity entity)
        {
            var border = new Border { Background = InspBrush("#FF1B2A3A"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 8, 9), Margin = new Thickness(0, 0, 0, 10) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock { Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = InspBrush("#FF6FB0FF"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
            System.Windows.Controls.Grid.SetColumn(icon, 0);

            var label = new TextBlock { Text = "Prefab · " + System.IO.Path.GetFileNameWithoutExtension(entity.PrefabPath ?? ""), Foreground = InspBrush("#FF9FCBFF"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = entity.PrefabPath };
            System.Windows.Controls.Grid.SetColumn(label, 1);

            var btn = new Button { Content = "", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = InspBrush("#FF9FCBFF"), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Select this prefab in the Asset Browser", Padding = new Thickness(6, 2, 6, 2) };
            btn.Click += (s, e) =>
            {
                var proj = Editor.Core.Data.ProjectData.Current?.Path ?? "";
                var full = System.IO.Path.IsPathRooted(entity.PrefabPath) ? entity.PrefabPath : System.IO.Path.Combine(proj, entity.PrefabPath ?? "");
                if (System.IO.File.Exists(full))
                    Editor.Editors.WorldEditor.Components.AssetBrowser.AssetBrowserView.SelectFileInExplorer(full);
            };
            System.Windows.Controls.Grid.SetColumn(btn, 2);

            grid.Children.Add(icon);
            grid.Children.Add(label);
            grid.Children.Add(btn);
            border.Child = grid;
            return border;
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

            RemoveComponentRespectingMode(component);
            RefreshInspector();

            // The editor viewport uses submit-once (it reuses the last scene submission), so nudge it to
            // rebuild the queue — otherwise the removed renderer / light / skybox / collider would linger.
            Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit();
        }

        /// <summary>Removes a component honoring the editor mode: undoable in scene mode, DIRECT in IsolatedMode.
        /// The isolated Prefab Editor edits a throwaway entity outside the scene — pushing a command for it onto
        /// the GLOBAL UndoRedoManager would let a later Ctrl+Z in the main editor mutate a ghost.</summary>
        private void RemoveComponentRespectingMode(Component component)
        {
            if (_selectedEntity == null || component == null) return;
            if (IsolatedMode)
            {
                if (!(component is Transform))   // Transform is never removable (mirrors GameEntity.RemoveComponent)
                    _selectedEntity.Components.Remove(component);
            }
            else
            {
                _selectedEntity.RemoveComponent(component);
            }
        }

        /// <summary>Add counterpart of <see cref="RemoveComponentRespectingMode"/> — same undo-hygiene rule.</summary>
        private void AddComponentRespectingMode(Component component)
        {
            if (_selectedEntity == null || component == null) return;
            if (IsolatedMode) _selectedEntity.AddComponentDirect(component);
            else _selectedEntity.AddComponent(component);
        }

        private UserControl CreateScriptInspector(Script script)
        {
            var inspector = new ScriptInspector();
            inspector.ScriptComponent = script;
            inspector.DetachRequested += (s, e) =>
            {
                if (_selectedEntity != null)
                {
                    RemoveComponentRespectingMode(script);
                    RefreshInspector();
                }
            };
            inspector.ChangeRequested += (s, e) =>
            {
                // remove the old, then open the picker to assign a new one
                if (_selectedEntity != null) RemoveComponentRespectingMode(script);
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

        private UserControl CreateBoneAttachmentInspector(ECS.Components.Animation.BoneAttachment attachment)
        {
            var inspector = new BoneAttachmentInspector(attachment);
            inspector.RemoveRequested += (s, e) => RemoveComponentAndRefresh(attachment);
            return inspector;
        }

        // ---- AudioSource + ReverbZone inspectors (issues #12/#15/#19) -----------------
        // Programmatic rows in the dark style; every setter writes the live component,
        // and the edit-mode preview (AudioPreviewService) hears changes immediately.

        private UserControl CreateAudioSourceInspector(ECS.Components.Audio.AudioSource src)
        {
            var panel = InspectorPanel("Audio Source", src, out var body);

            body.Children.Add(AudioPickerRow("Audio Clip", () => src.AudioClipPath ?? "", v => src.AudioClipPath = v,
                "Project-relative .wav/.mp3/.ogg/.flac or .vsndc container — Browse…, or drag from the Audio tab"));
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

        /// <summary>Like <see cref="AudioTextRow"/> but with a Browse… button that opens an audio file picker (on the
        /// STA FilePicker thread so it can't deadlock the renderer) and stores a project-relative path.</summary>
        private UIElement AudioPickerRow(string label, Func<string> get, Action<string> set, string tooltip)
        {
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, Foreground = InspBrush("#FFB4B4BC"), FontSize = 11 });

            var grid = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var box = new TextBox { Text = get(), Background = InspBrush("#FF141416"), Foreground = InspBrush("#FFE9E9ED"), BorderBrush = InspBrush("#FF2C2C32"), Padding = new Thickness(6, 4, 6, 4), ToolTip = tooltip, VerticalContentAlignment = VerticalAlignment.Center };
            box.TextChanged += (s, e) => set(box.Text);
            Grid.SetColumn(box, 0);
            grid.Children.Add(box);

            var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), Background = InspBrush("#FF26262B"), Foreground = InspBrush("#FF9C8CFF"), BorderBrush = InspBrush("#FF3A3A42"), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Pick an audio clip or .vsndc container" };
            browse.Click += (s, e) =>
            {
                var proj = Core.Data.ProjectData.Current?.Path;
                var audioDir = string.IsNullOrEmpty(proj) ? null : System.IO.Path.Combine(proj, "Assets", "Audio");
                var start = (audioDir != null && System.IO.Directory.Exists(audioDir)) ? audioDir : proj;
                var picked = Core.Util.FilePicker.OpenFile("Audio + Containers|*.wav;*.mp3;*.ogg;*.flac;*.vsndc|All files|*.*", "Pick an audio clip", start);
                if (string.IsNullOrEmpty(picked)) return;
                box.Text = MakeProjRel(picked);   // TextChanged fires set()
            };
            Grid.SetColumn(browse, 1);
            grid.Children.Add(browse);

            row.Children.Add(grid);
            return row;
        }

        private static string MakeProjRel(string path)
        {
            var proj = Core.Data.ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(proj) || string.IsNullOrEmpty(path) || !System.IO.Path.IsPathRooted(path)) return path;
            try
            {
                var pu = new Uri(proj.EndsWith("\\") ? proj : proj + "\\");
                return Uri.UnescapeDataString(pu.MakeRelativeUri(new Uri(path)).ToString());
            }
            catch { return path; }
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
            AddComponentMenuItem(contextMenu, "Bone Attachment", () => AddComponent<ECS.Components.Animation.BoneAttachment>());

            contextMenu.Items.Add(new Separator());
            AddComponentMenuItem(contextMenu, "Rigidbody", () => AddComponent<ECS.Components.Physics.Rigidbody>());
            AddComponentMenuItem(contextMenu, "Box Collider", () => AddComponent<ECS.Components.Physics.BoxCollider>());
            AddComponentMenuItem(contextMenu, "Sphere Collider", () => AddComponent<ECS.Components.Physics.SphereCollider>());
            AddComponentMenuItem(contextMenu, "Capsule Collider", () => AddComponent<ECS.Components.Physics.CapsuleCollider>());
            AddComponentMenuItem(contextMenu, "Mesh Collider (edge-accurate)", () => AddComponent<ECS.Components.Physics.MeshCollider>());
            contextMenu.Items.Add(new Separator());
            AddComponentMenuItem(contextMenu, "Open Collision Editor…", () => OpenCollisionEditor());

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
            AddComponentRespectingMode(new Script(_selectedEntity, relativePath));
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
                AddComponentRespectingMode(source);
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

            // ---- inline shape fields: write the live component directly (the CollisionService and the
            // collider gizmo read these) — no round-trip through the Collision Editor needed. ----
            panel.Children.Add(ColliderLabel("Center"));
            panel.Children.Add(ColliderVector3Row(col.Center, v => col.Center = v));

            if (col is ECS.Components.Physics.BoxCollider box)
            {
                panel.Children.Add(ColliderLabel("Size"));
                panel.Children.Add(ColliderVector3Row(box.Size, v => box.Size = v));
            }
            else if (col is ECS.Components.Physics.SphereCollider sph)
            {
                panel.Children.Add(ColliderLabel("Radius"));
                panel.Children.Add(ColliderFloatRow(sph.Radius, v => sph.Radius = v));
            }
            else if (col is ECS.Components.Physics.CapsuleCollider cap)
            {
                panel.Children.Add(ColliderLabel("Radius"));
                panel.Children.Add(ColliderFloatRow(cap.Radius, v => cap.Radius = v));
                panel.Children.Add(ColliderLabel("Height"));
                panel.Children.Add(ColliderFloatRow(cap.Height, v => cap.Height = v));
            }
            else if (col is ECS.Components.Physics.MeshCollider)
            {
                panel.Children.Add(new TextBlock { Text = "Exact mesh shape (uses this entity's Mesh Renderer).", Foreground = brush("#FF8A8A92"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            }

            // Mesh-shaped colliders are built from the entity's OWN MeshRenderer — the CollisionService
            // silently produces no shape when there is none. Warn loudly instead of letting a "collider"
            // that does nothing sit on a wall prefab.
            bool meshShaped = col is ECS.Components.Physics.MeshCollider || col.GetType() == typeof(ECS.Components.Physics.Collider);
            var owner = col.Entity ?? _selectedEntity;
            if (meshShaped && owner != null && owner.GetComponent<MeshRenderer>() == null)
            {
                panel.Children.Add(new Border
                {
                    Background = brush("#FF3A1E22"),
                    BorderBrush = brush("#FF7E3A44"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 7, 9, 8),
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = new TextBlock
                    {
                        Text = "No Mesh Renderer on this entity — this collider produces NO collision. Add it to the child that has the mesh, or use a Box Collider.",
                        Foreground = brush("#FFFF8A96"),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }

            panel.Children.Add(new TextBlock { Text = "Switch shapes, auto-fit and preview in the Collision Editor.", Foreground = brush("#FF8A8A92"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            var btn = new Button { Content = "Open Collision Editor…", Padding = new Thickness(12, 6, 12, 6), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Foreground = System.Windows.Media.Brushes.White, Background = brush("#FF6C5CE7"), BorderThickness = new Thickness(0) };
            btn.Click += (s, e) => OpenCollisionEditor();
            panel.Children.Add(btn);
            return new UserControl { Content = panel };
        }

        /// <summary>Opens the Collision Editor targeting the right entity: in IsolatedMode (Prefab Editor) the
        /// window is pinned to the inspected standalone entity — otherwise it would follow the MAIN scene
        /// selection and edit the wrong entity (or show "No entity selected").</summary>
        private void OpenCollisionEditor()
        {
            try
            {
                var wnd = Editor.Editors.PhysicsEditor.CollisionEditorWindow.Open(Window.GetWindow(this), IsolatedMode ? _selectedEntity : null);
                if (IsolatedMode && wnd != null)
                {
                    // Re-render our cards when the Collision Editor swaps/removes components on the isolated
                    // entity — otherwise this inspector keeps fields bound to a removed collider instance.
                    // (-= first so repeated opens never stack duplicate handlers.)
                    wnd.TargetModified -= RefreshInspector;
                    wnd.TargetModified += RefreshInspector;
                }
            }
            catch { }
        }

        // ---- collider numeric-row builders (mirror the Collision Editor's NumBox / Vector3 rows) ----

        private TextBlock ColliderLabel(string text)
            => new TextBlock { Text = text, Foreground = InspBrush("#FF6E6E77"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

        private UIElement ColliderVector3Row(Vector3 v, Action<Vector3> set)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
            float x = v.X, y = v.Y, z = v.Z;
            var fx = ColliderNumBox(x, nv => { x = nv; set(new Vector3(x, y, z)); }, "X", "#FFE06C6C");
            var fy = ColliderNumBox(y, nv => { y = nv; set(new Vector3(x, y, z)); }, "Y", "#FF7CE0A3");
            var fz = ColliderNumBox(z, nv => { z = nv; set(new Vector3(x, y, z)); }, "Z", "#FF6C9CE0");
            Grid.SetColumn(fx, 0); Grid.SetColumn(fy, 1); Grid.SetColumn(fz, 2);
            g.Children.Add(fx); g.Children.Add(fy); g.Children.Add(fz);
            return g;
        }

        private UIElement ColliderFloatRow(float v, Action<float> set)
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            p.Children.Add(ColliderNumBox(v, set, null, "#FFC8C8CE"));
            return p;
        }

        private UIElement ColliderNumBox(float value, Action<float> set, string tag, string tagColor)
        {
            var wrap = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
            if (tag != null) wrap.Children.Add(new TextBlock { Text = tag, Foreground = InspBrush(tagColor), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Width = 12 });
            var tb = new TextBox
            {
                Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Background = InspBrush("#FF202023"), Foreground = InspBrush("#FFF0F0F3"), BorderBrush = InspBrush("#FF34343C"),
                BorderThickness = new Thickness(1), Padding = new Thickness(7, 5, 7, 5), MinWidth = 46,
                CaretBrush = InspBrush("#FF6C5CE7")
            };
            tb.TextChanged += (s, e) =>
            {
                float nv;
                if (float.TryParse(tb.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out nv))
                {
                    set(nv);
                    // Submit-once viewport: nudge a re-submit so the collider gizmo follows the edit at once.
                    Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit();
                }
            };
            wrap.Children.Add(tb);
            return wrap;
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
                AddComponentRespectingMode(new T { Entity = _selectedEntity });
                RefreshInspector();
            }
        }

        private void AddLightComponent(LightType lightType)
        {
            if (_selectedEntity == null) return;

            if (!_selectedEntity.HasComponent<Light>())
            {
                var light = new Light(_selectedEntity, lightType);
                AddComponentRespectingMode(light);
                RefreshInspector();
            }
        }
    }
}
