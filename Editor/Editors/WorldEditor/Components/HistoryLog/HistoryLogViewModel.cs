using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using Editor.Core.UndoRedo;

namespace Editor.Editors.WorldEditor.Components.HistoryLog
{
    /// <summary>
    /// ViewModel für das History Log Panel.
    /// Zeigt die letzten Undo/Redo Aktionen an.
    /// </summary>
    public class HistoryLogViewModel : Core.ViewModelBase
    {
        private HistoryItem _selectedItem;

        public ObservableCollection<HistoryItem> HistoryItems { get; } = new ObservableCollection<HistoryItem>();

        public HistoryItem SelectedItem
        {
            get => _selectedItem;
            set => SetProperty(ref _selectedItem, value, nameof(SelectedItem));
        }

        public HistoryLogViewModel()
        {
            // Subscribe to UndoRedoManager events
            UndoRedoManager.Instance.StateChanged += OnStateChanged;
            UndoRedoManager.Instance.CommandExecuted += OnCommandExecuted;
            
            // Initial load
            RefreshHistory();
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            RefreshHistory();
        }

        private void OnCommandExecuted(object sender, CommandExecutedEventArgs e)
        {
            RefreshHistory();
        }

        /// <summary>
        /// Aktualisiert die History-Liste
        /// </summary>
        public void RefreshHistory()
        {
            HistoryItems.Clear();

            var undoHistory = UndoRedoManager.Instance.GetUndoHistory();
            var redoHistory = UndoRedoManager.Instance.GetRedoHistory();

            // Redo-Stack (zukünftige Aktionen - oben, ausgegraut)
            int redoIndex = redoHistory.Count;
            foreach (var command in redoHistory.Reverse())
            {
                HistoryItems.Add(new HistoryItem
                {
                    Name = command.Name,
                    Command = command,
                    IsRedo = true,
                    Index = redoIndex--,
                    Icon = GetIconForCommand(command.Name),
                    IconColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#606060")),
                    TextColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#707070"))
                });
            }

            // Undo-Stack (vergangene Aktionen - unten, normal)
            int undoIndex = 1;
            foreach (var command in undoHistory)
            {
                HistoryItems.Add(new HistoryItem
                {
                    Name = command.Name,
                    Command = command,
                    IsRedo = false,
                    Index = undoIndex++,
                    Icon = GetIconForCommand(command.Name),
                    IconColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4EC9B0")),
                    TextColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C5C5C5"))
                });
            }
        }

        /// <summary>
        /// Springt zum ausgewählten History-Eintrag
        /// </summary>
        public void JumpToSelected()
        {
            if (_selectedItem == null) return;

            if (_selectedItem.IsRedo)
            {
                // Redo bis zum ausgewählten Punkt
                var redoHistory = UndoRedoManager.Instance.GetRedoHistory().ToList();
                var targetIndex = redoHistory.IndexOf(_selectedItem.Command);
                if (targetIndex >= 0)
                {
                    UndoRedoManager.Instance.RedoMultiple(redoHistory.Count - targetIndex);
                }
            }
            else
            {
                // Undo bis zum ausgewählten Punkt
                var undoHistory = UndoRedoManager.Instance.GetUndoHistory().ToList();
                var targetIndex = undoHistory.IndexOf(_selectedItem.Command);
                if (targetIndex >= 0)
                {
                    UndoRedoManager.Instance.UndoMultiple(targetIndex + 1);
                }
            }
        }

        private string GetIconForCommand(string commandName)
        {
            if (commandName == null) return "\uE7C3";

            if (commandName.Contains("Add") || commandName.Contains("Create"))
                return "\uE710"; // Plus
            if (commandName.Contains("Delete") || commandName.Contains("Remove"))
                return "\uE74D"; // Trash
            if (commandName.Contains("Move"))
                return "\uE7C2"; // Move
            if (commandName.Contains("Rename") || commandName.Contains("Change"))
                return "\uE70F"; // Edit
            if (commandName.Contains("Duplicate") || commandName.Contains("Copy"))
                return "\uE8C8"; // Copy
            if (commandName.Contains("Paste"))
                return "\uE77F"; // Paste
            if (commandName.Contains("Reorder"))
                return "\uE8CB"; // Sort

            return "\uE7C3"; // Default action icon
        }
    }

    /// <summary>
    /// Repräsentiert einen Eintrag in der History-Liste
    /// </summary>
    public class HistoryItem : Core.ViewModelBase
    {
        public string Name { get; set; }
        public IUndoableCommand Command { get; set; }
        public bool IsRedo { get; set; }
        public int Index { get; set; }
        public string Icon { get; set; }
        public Brush IconColor { get; set; }
        public Brush TextColor { get; set; }

        public string IndexDisplay => IsRedo ? $"+{Index}" : $"-{Index}";
    }
}
