using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Shell
{
    /// <summary>Edit a .vmat: PBR scalars, colours, texture maps, blend mode. Applies live to the scene on save.</summary>
    public sealed class MaterialEditorWindow : Window
    {
        private readonly string _path;
        private readonly VortexMaterial _mat;
        private static readonly string[] Tex = { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.hdr", "*.dds" };

        public MaterialEditorWindow(string vmatPath)
        {
            _path = vmatPath;
            Title = "Material — " + Path.GetFileNameWithoutExtension(vmatPath); Width = 520; Height = 680; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            _mat = VortexMaterial.Load(vmatPath) ?? new VortexMaterial { Name = Path.GetFileNameWithoutExtension(vmatPath) };
            string dir = Path.GetDirectoryName(vmatPath);
            var stack = new StackPanel { Spacing = 3, Margin = new Thickness(16, 12) };
            stack.Children.Add(new TextBlock { Text = "Surface", Classes = { "section" } });
            stack.Children.Add(Row("Blend mode", Choice(() => BlendIndex(_mat.BlendMode), v => _mat.BlendMode = new[] { "Opaque", "AlphaBlend", "AlphaTest", "Additive" }[v], "Opaque", "Alpha Blend", "Alpha Test (cutout)", "Additive")));
            stack.Children.Add(Row("Base color", Color(() => (_mat.BaseColor[0], _mat.BaseColor[1], _mat.BaseColor[2]), (r, g, b) => { _mat.BaseColor[0] = r; _mat.BaseColor[1] = g; _mat.BaseColor[2] = b; })));
            stack.Children.Add(Row("Opacity", SliderRow(() => _mat.BaseColor.Length > 3 ? _mat.BaseColor[3] : 1f, v => { if (_mat.BaseColor.Length > 3) _mat.BaseColor[3] = v; }, 0, 1)));
            stack.Children.Add(Row("Metallic", SliderRow(() => _mat.Metallic, v => _mat.Metallic = v, 0, 1)));
            stack.Children.Add(Row("Roughness", SliderRow(() => _mat.Roughness, v => _mat.Roughness = v, 0, 1)));
            stack.Children.Add(Row("Ambient occlusion", SliderRow(() => _mat.AmbientOcclusion, v => _mat.AmbientOcclusion = v, 0, 1)));
            stack.Children.Add(Row("Normal strength", SliderRow(() => _mat.NormalStrength, v => _mat.NormalStrength = v, 0, 2)));
            stack.Children.Add(Row("Alpha cutoff", SliderRow(() => _mat.AlphaCutoff, v => _mat.AlphaCutoff = v, 0, 1)));
            stack.Children.Add(Row("Emissive", Color(() => (_mat.EmissiveColor[0], _mat.EmissiveColor[1], _mat.EmissiveColor[2]), (r, g, b) => { _mat.EmissiveColor[0] = r; _mat.EmissiveColor[1] = g; _mat.EmissiveColor[2] = b; })));
            stack.Children.Add(Row("Emissive strength", SliderRow(() => _mat.EmissiveStrength, v => _mat.EmissiveStrength = v, 0, 10)));
            stack.Children.Add(Row("Two sided", Bool(() => _mat.TwoSided, v => _mat.TwoSided = v)));
            stack.Children.Add(Row("Cast shadows", Bool(() => _mat.CastShadows, v => _mat.CastShadows = v)));
            stack.Children.Add(Row("DirectX normals", Bool(() => _mat.UseDirectXNormals, v => _mat.UseDirectXNormals = v)));
            stack.Children.Add(new TextBlock { Text = "Texture maps", Classes = { "section" } });
            stack.Children.Add(TexRow("Albedo", () => _mat.AlbedoTexture, v => _mat.AlbedoTexture = v));
            stack.Children.Add(TexRow("Normal", () => _mat.NormalTexture, v => _mat.NormalTexture = v));
            stack.Children.Add(TexRow("Metallic", () => _mat.MetallicTexture, v => _mat.MetallicTexture = v));
            stack.Children.Add(TexRow("Roughness", () => _mat.RoughnessTexture, v => _mat.RoughnessTexture = v));
            stack.Children.Add(TexRow("Occlusion", () => _mat.AOTexture, v => _mat.AOTexture = v));
            stack.Children.Add(TexRow("Emissive", () => _mat.EmissiveTexture, v => _mat.EmissiveTexture = v));
            stack.Children.Add(TexRow("Height", () => _mat.HeightTexture, v => _mat.HeightTexture = v));
            stack.Children.Add(TexRow("Opacity", () => _mat.OpacityTexture, v => _mat.OpacityTexture = v));
            stack.Children.Add(TexRow("Metallic-Roughness", () => _mat.MetallicRoughnessTexture, v => _mat.MetallicRoughnessTexture = v));
            stack.Children.Add(TexRow("ORM", () => _mat.OcclusionRoughnessMetallicTexture, v => _mat.OcclusionRoughnessMetallicTexture = v));
            stack.Children.Add(new TextBlock { Text = "Shader & UV", Classes = { "section" } });
            stack.Children.Add(Row("Shader", AssetPath(() => _mat.ShaderAsset, v => _mat.ShaderAsset = v, "Shader", new[] { "*.hlsl", "*.metal" }, () => AssetPickerDialog.Pick("Shaders", new[] { "*.hlsl", "*.metal" }))));
            stack.Children.Add(Row("UV tiling", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { FloatBox(() => _mat.UVTiling[0], v => _mat.UVTiling[0] = v), FloatBox(() => _mat.UVTiling[1], v => _mat.UVTiling[1] = v) } }));
            stack.Children.Add(Row("UV offset", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { FloatBox(() => _mat.UVOffset[0], v => _mat.UVOffset[0] = v), FloatBox(() => _mat.UVOffset[1], v => _mat.UVOffset[1] = v) } }));
            stack.Children.Add(Row("Footstep sound", AssetPath(() => _mat.FootstepSound, v => _mat.FootstepSound = v, "Audio", new[] { "*.wav", "*.mp3", "*.ogg", "*.vsndc" }, () => AssetPickerDialog.Pick("Audio", new[] { "*.wav", "*.mp3", "*.ogg", "*.vsndc" }))));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var close = new Button { Content = "Close", MinWidth = 90 }; close.Click += (s, e) => Close();
            var save = new Button { Content = "Save & Apply", Classes = { "accent" }, MinWidth = 110, IsDefault = true }; save.Click += (s, e) => Save();
            buttons.Children.Add(close); buttons.Children.Add(save);
            stack.Children.Add(buttons);
            Content = new ScrollViewer { Content = stack };
        }

        private Control TexRow(string label, Func<string> get, Action<string> set)
            => Row(label, AssetPath(get, v => set(string.IsNullOrEmpty(v) ? null : v), "Texture", Tex, () => AssetPickerDialog.Pick("Textures", Tex)));

        private static int BlendIndex(string b) => b == "AlphaBlend" || b == "Transparent" ? 1 : b == "AlphaTest" ? 2 : b == "Additive" ? 3 : 0;

        private void Save()
        {
            try
            {
                if (_mat.Save(_path))
                {
                    try { MaterialService.Instance.InvalidateVortexMaterial(_path); } catch { }
                    SceneRenderService.RuntimeDirty = true;
                    EditorCommands.Toast("Material saved");
                }
                else EditorCommands.Toast("Could not save the material");
            }
            catch (Exception ex) { EditorCommands.Fail("Save material", ex); }
        }
    }
}
