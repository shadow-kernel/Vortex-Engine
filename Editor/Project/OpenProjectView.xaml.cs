using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Editor.Project
{
    public partial class OpenProjectView : UserControl
    {
        private readonly List<ProjectItem> _allProjects;
        public ObservableCollection<ProjectItem> Projects { get; }

        public OpenProjectView()
        {
            InitializeComponent();

            var items = CreateDummyProjects()
                .OrderByDescending(p => p.LastModified)
                .ToList();

            _allProjects = items;
            Projects = new ObservableCollection<ProjectItem>(_allProjects);
            DataContext = this;
        }

        private void SearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            var query = (sender as TextBox)?.Text ?? string.Empty;
            ApplyFilter(query);
        }

        private void ApplyFilter(string query)
        {
            var filtered = string.IsNullOrWhiteSpace(query)
                ? _allProjects
                : _allProjects
                    .Where(p => ContainsIgnoreCase(p.Name, query) || ContainsIgnoreCase(p.Path, query))
                    .ToList();

            Projects.Clear();
            foreach (var item in filtered.OrderByDescending(p => p.LastModified))
            {
                Projects.Add(item);
            }
        }

        private bool ContainsIgnoreCase(string source, string value)
        {
            if (string.IsNullOrEmpty(source))
                return false;
            return source.IndexOf(value ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private IEnumerable<ProjectItem> CreateDummyProjects()
        {
            var thumbnail = LoadThumbnail();
            return new[]
            {
                new ProjectItem
                {
                    Name = "Vortex Sandbox",
                    Path = "C:/Projekte/Vortex/Sandbox",
                    LastModified = DateTime.Now.AddDays(-1).AddHours(-3),
                    Thumbnail = thumbnail
                },
                new ProjectItem
                {
                    Name = "Dungeon Crawler",
                    Path = "D:/Dev/Games/DungeonCrawler",
                    LastModified = DateTime.Now.AddDays(-4),
                    Thumbnail = thumbnail
                },
                new ProjectItem
                {
                    Name = "Platformer Prototype",
                    Path = "C:/Users/User/Projects/Platformer",
                    LastModified = DateTime.Now.AddDays(-7).AddHours(-2),
                    Thumbnail = thumbnail
                },
                new ProjectItem
                {
                    Name = "VR Flight",
                    Path = "E:/VR/FlightSim",
                    LastModified = DateTime.Now.AddDays(-12).AddHours(-5),
                    Thumbnail = thumbnail
                },
                new ProjectItem
                {
                    Name = "Puzzle World",
                    Path = "C:/Workspace/PuzzleWorld",
                    LastModified = DateTime.Now.AddDays(-20),
                    Thumbnail = thumbnail
                }
            };
        }

        private ImageSource LoadThumbnail()
        {
            try
            {
                return new BitmapImage(new Uri("pack://application:,,,/Assets/Images/Logo.png", UriKind.Absolute));
            }
            catch
            {
                return null;
            }
        }
    }

    public class ProjectItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public DateTime LastModified { get; set; }
        public ImageSource Thumbnail { get; set; }
        public string LastModifiedDisplay => LastModified.ToString("dd.MM.yyyy HH:mm");
    }
}
