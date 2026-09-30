using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VortexEditor.Controls
{
    /// <summary>PathIcon bound to a named geometry from Theme/Icons.axaml: &lt;c:VxIcon Icon="Play"/&gt;.</summary>
    public sealed class VxIcon : PathIcon
    {
        public static readonly StyledProperty<string> IconProperty = AvaloniaProperty.Register<VxIcon, string>(nameof(Icon));

        public string Icon
        {
            get => GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public VxIcon()
        {
            Width = 16; Height = 16;
        }

        // Keep PathIcon's control theme (a derived type would otherwise get no template and draw nothing).
        protected override System.Type StyleKeyOverride => typeof(PathIcon);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IconProperty) Apply();
        }

        protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
        {
            base.OnAttachedToLogicalTree(e);
            Apply();
        }

        private void Apply()
        {
            var name = Icon;
            if (string.IsNullOrEmpty(name)) return;
            if (Application.Current != null && Application.Current.TryFindResource("Icon" + name, out var res) && res is Geometry g)
                Data = g;
        }
    }
}
