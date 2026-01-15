using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Project.Model;

namespace Editor.Project.Projection
{
    public partial class OpenProjectView : UserControl
    {
        private OpenProjectModel _dataContextModel;

        public OpenProjectView()
        {
            InitializeComponent();
            if (DataContext == null)
            {
                DataContext = new OpenProjectModel();
            }
            _dataContextModel = DataContext as OpenProjectModel;
        }

        private void ExitButton_Pressed(object sender, System.Windows.RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void SearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_dataContextModel != null)
            {
                var textBox = sender as TextBox;
                _dataContextModel.SearchText = textBox?.Text ?? string.Empty;
            }
        }
    }
}
