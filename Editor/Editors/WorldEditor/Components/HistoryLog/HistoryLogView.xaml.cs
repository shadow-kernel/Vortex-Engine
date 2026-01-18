using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Editor.Core.UndoRedo;

namespace Editor.Editors.WorldEditor.Components.HistoryLog
{
    public partial class HistoryLogView : UserControl
    {
        private HistoryLogViewModel ViewModel => DataContext as HistoryLogViewModel;

        public HistoryLogView()
        {
            InitializeComponent();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            UndoRedoManager.Instance.Undo();
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            UndoRedoManager.Instance.Redo();
        }

        private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ViewModel?.JumpToSelected();
        }
    }
}
