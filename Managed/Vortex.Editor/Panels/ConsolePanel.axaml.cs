using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Editor.Core.Services;

namespace VortexEditor.Panels
{
    public partial class ConsolePanel : UserControl
    {
        private readonly ObservableCollection<LogEntry> _view = new ObservableCollection<LogEntry>();

        public ConsolePanel()
        {
            InitializeComponent();
            List.ItemsSource = _view;
            ConsoleService.Instance.Entries.CollectionChanged += OnEntriesChanged;
            Rebuild();
        }

        private void OnEntriesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
                {
                    foreach (LogEntry le in e.NewItems) if (Passes(le)) _view.Add(le);
                    Trim();
                    if (AutoScroll.IsChecked == true && _view.Count > 0) List.ScrollIntoView(_view[_view.Count - 1]);
                }
                else Rebuild();
                UpdateCounts();
            });
        }

        private void Trim() { while (_view.Count > ConsoleService.MaxEntries) _view.RemoveAt(0); }

        private bool Passes(LogEntry e)
        {
            if (e == null) return false;
            bool ok = e.Level == LogLevel.Error ? ChipError.IsChecked == true
                    : e.Level == LogLevel.Warning ? ChipWarn.IsChecked == true
                    : ChipInfo.IsChecked == true;
            if (!ok) return false;
            string f = Filter.Text;
            return string.IsNullOrEmpty(f) || (e.Message != null && e.Message.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void Rebuild()
        {
            _view.Clear();
            foreach (var e in ConsoleService.Instance.Entries) if (Passes(e)) _view.Add(e);
            UpdateCounts();
            if (AutoScroll.IsChecked == true && _view.Count > 0) List.ScrollIntoView(_view[_view.Count - 1]);
        }

        private void UpdateCounts()
        {
            var c = ConsoleService.Instance;
            InfoCount.Text = c.InfoCount.ToString();
            WarnCount.Text = c.WarnCount.ToString();
            ErrorCount.Text = c.ErrorCount.ToString();
        }

        private void OnChip(object sender, RoutedEventArgs e) => Rebuild();
        private void OnFilterChanged(object sender, TextChangedEventArgs e) => Rebuild();
        private void OnClear(object sender, RoutedEventArgs e) { ConsoleService.Instance.Clear(); Rebuild(); }
    }
}
