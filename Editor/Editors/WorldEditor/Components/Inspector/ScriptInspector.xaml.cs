using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Editor.Core.Services;
using Editor.ECS.Components.Scripting;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    /// <summary>Inspector for a <see cref="Script"/> component: shows the bound class + path, opens it in
    /// Visual Studio, lets you change or detach it.</summary>
    public partial class ScriptInspector : UserControl
    {
        private Script _script;

        /// <summary>Raised when the user clicks the detach (trash) button.</summary>
        public event EventHandler DetachRequested;
        /// <summary>Raised when the user wants to swap the assigned script.</summary>
        public event EventHandler ChangeRequested;

        public ScriptInspector()
        {
            InitializeComponent();
        }

        public Script ScriptComponent
        {
            get { return _script; }
            set { _script = value; Bind(); }
        }

        private void Bind()
        {
            if (_script == null) return;
            ClassText.Text = string.IsNullOrEmpty(_script.ScriptClassName) ? "(none)" : _script.ScriptClassName;
            PathText.Text = _script.ScriptPath ?? "";
            WarnPanel.Visibility = ScriptFileExists() ? Visibility.Collapsed : Visibility.Visible;
        }

        private bool ScriptFileExists()
        {
            try
            {
                var abs = AbsolutePath();
                return !string.IsNullOrEmpty(abs) && File.Exists(abs);
            }
            catch { return true; } // don't nag on unexpected errors
        }

        private string AbsolutePath()
        {
            var root = ScriptingService.ProjectRoot;
            if (string.IsNullOrEmpty(root) || _script == null || string.IsNullOrEmpty(_script.ScriptPath)) return null;
            return Path.Combine(root, _script.ScriptPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private void OpenVs_Click(object sender, RoutedEventArgs e)
        {
            ScriptingService.OpenInVisualStudio(AbsolutePath());
        }

        private void Detach_Click(object sender, RoutedEventArgs e)
        {
            var h = DetachRequested; if (h != null) h(this, EventArgs.Empty);
        }

        private void Change_Click(object sender, RoutedEventArgs e)
        {
            var h = ChangeRequested; if (h != null) h(this, EventArgs.Empty);
        }
    }
}
