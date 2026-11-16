using System;
using System.IO;
using System.Linq;
using System.Windows;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp
{
    public partial class GscInjectorWindow : Window
    {
        private readonly string _gamePath;

        public GscInjectorWindow(string gamePath)
        {
            InitializeComponent();
            _gamePath = gamePath;
            Loaded += GscInjectorWindow_Loaded;
        }

        private void GscInjectorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshList();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshList();
        }

        private void RefreshList()
        {
            GscListBox.Items.Clear();
            string gscDir = Path.Combine(_gamePath, "gsc");
            
            if (!Directory.Exists(gscDir))
            {
                Directory.CreateDirectory(gscDir);
            }

            var files = Directory.GetFiles(gscDir)
                                 .Where(f => f.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gscc", StringComparison.OrdinalIgnoreCase))
                                 .Select(Path.GetFileName)
                                 .ToArray();

            foreach (var file in files)
            {
                GscListBox.Items.Add(file);
            }

            if (GscListBox.Items.Count == 0)
            {
                GscListBox.Items.Add("No .gsc files found in game's /gsc folder.");
                GscListBox.IsEnabled = false;
            }
            else
            {
                GscListBox.IsEnabled = true;
            }
        }

        private void InjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (GscListBox.SelectedItem is not string selectedFile || !GscListBox.IsEnabled)
            {
                MessageBox.Show("Please select a valid .gsc file first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string fullPath = Path.Combine(_gamePath, "gsc", selectedFile);

            StatusText.Text = "Injecting...";
            
            string result = GscInjector.Inject(_gamePath, fullPath);
            StatusText.Text = result;

            if (result.StartsWith("Error:"))
            {
                MessageBox.Show(result, "Injection Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(result, "Injection Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
