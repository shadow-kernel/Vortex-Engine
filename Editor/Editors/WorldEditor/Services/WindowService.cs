using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Editor.Editors.WorldEditor.Services
{
    /// <summary>
    /// Service zur Verwaltung der Sichtbarkeit von Editor-Fenstern.
    /// </summary>
    public sealed class WindowService : INotifyPropertyChanged
    {
        private static readonly Lazy<WindowService> _instance = new Lazy<WindowService>(() => new WindowService());
        public static WindowService Instance => _instance.Value;

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<WindowVisibilityChangedEventArgs> WindowVisibilityChanged;

        private readonly Dictionary<string, bool> _windowVisibility;

        private WindowService()
        {
            _windowVisibility = new Dictionary<string, bool>
            {
                { "Scene", true },
                { "Project", true },
                { "Explorer", true },
                { "Console", true },
                { "Hierarchy", true },
                { "Inspector", true }
            };
        }

        public bool IsSceneVisible
        {
            get => _windowVisibility["Scene"];
            set => SetWindowVisibility("Scene", value);
        }

        public bool IsProjectVisible
        {
            get => _windowVisibility["Project"];
            set => SetWindowVisibility("Project", value);
        }

        public bool IsExplorerVisible
        {
            get => _windowVisibility["Explorer"];
            set => SetWindowVisibility("Explorer", value);
        }

        public bool IsConsoleVisible
        {
            get => _windowVisibility["Console"];
            set => SetWindowVisibility("Console", value);
        }

        public bool IsHierarchyVisible
        {
            get => _windowVisibility["Hierarchy"];
            set => SetWindowVisibility("Hierarchy", value);
        }

        public bool IsInspectorVisible
        {
            get => _windowVisibility["Inspector"];
            set => SetWindowVisibility("Inspector", value);
        }

        public bool GetWindowVisibility(string windowName)
        {
            return _windowVisibility.ContainsKey(windowName) && _windowVisibility[windowName];
        }

        public void SetWindowVisibility(string windowName, bool isVisible)
        {
            if (_windowVisibility.ContainsKey(windowName) && _windowVisibility[windowName] != isVisible)
            {
                _windowVisibility[windowName] = isVisible;
                OnPropertyChanged($"Is{windowName}Visible");
                WindowVisibilityChanged?.Invoke(this, new WindowVisibilityChangedEventArgs(windowName, isVisible));
            }
        }

        public void ToggleWindow(string windowName)
        {
            if (_windowVisibility.ContainsKey(windowName))
            {
                SetWindowVisibility(windowName, !_windowVisibility[windowName]);
            }
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class WindowVisibilityChangedEventArgs : EventArgs
    {
        public string WindowName { get; }
        public bool IsVisible { get; }

        public WindowVisibilityChangedEventArgs(string windowName, bool isVisible)
        {
            WindowName = windowName;
            IsVisible = isVisible;
        }
    }
}
