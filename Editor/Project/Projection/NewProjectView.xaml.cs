using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Editor.Project.Model;

namespace Editor.Project.Projection
{
    /// <summary>
    /// Interaktionslogik für NewProjectView.xaml
    /// </summary>
    public partial class NewProjectView : UserControl
    {
        private NewProjectModel _dataContextModel;

        public NewProjectView()
        {
            InitializeComponent();
            if(DataContext == null)
            {
                DataContext = new NewProjectModel();
            }
            _dataContextModel = DataContext as NewProjectModel;
        }

        private void ExitButton_Pressed(object sender, System.Windows.RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void OpenButton_Pressed(object sender, RoutedEventArgs e)
        {
            try
            {
                if(this._dataContextModel.createProject())
                {
                    MessageBox.Show("Project created successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating project: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
