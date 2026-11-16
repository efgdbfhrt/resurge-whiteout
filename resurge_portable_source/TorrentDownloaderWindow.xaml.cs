using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using MonoTorrent.Client;

namespace ResurgeLauncherCSharp
{
    public partial class TorrentDownloaderWindow : Window
    {
        private ClientEngine? _engine;
        private TorrentManager? _manager;

        public TorrentDownloaderWindow()
        {
            InitializeComponent();
        }

        private async void DownloadUnpacked_Click(object sender, RoutedEventArgs e)
        {
            await StartDownload("Downgrade\\OPTION 1 UNPACKED.torrent");
        }

        private async void DownloadPacked_Click(object sender, RoutedEventArgs e)
        {
            await StartDownload("Downgrade\\OPTION 2 PACKED.torrent");
        }

        private async Task StartDownload(string torrentRelativePath)
        {
            string torrentPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, torrentRelativePath);
            if (!File.Exists(torrentPath))
            {
                MessageBox.Show("Torrent file not found: " + torrentPath, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "Select Destination Folder"
            };

            if (dialog.ShowDialog() == true)
            {
                string destPath = dialog.FolderName;
                await SetupAndStartTorrent(torrentPath, destPath);
            }
        }

        private async Task SetupAndStartTorrent(string torrentPath, string destPath)
        {
            if (_manager != null)
            {
                await _manager.StopAsync();
            }
            if (_engine == null)
            {
                var settings = new EngineSettingsBuilder
                {
                    AllowPortForwarding = true,
                    AutoSaveLoadDhtCache = true,
                    AllowLocalPeerDiscovery = true
                }.ToSettings();
                _engine = new ClientEngine(settings);
            }

            try
            {
                var torrent = await MonoTorrent.Torrent.LoadAsync(torrentPath);
                _manager = await _engine.AddAsync(torrent, destPath);
                await _manager.StartAsync();

                StatusText.Text = $"Downloading: {torrent.Name}";
                
                // Start a background loop to update UI
                _ = Task.Run(async () =>
                {
                    while (_manager != null && _manager.State != TorrentState.Stopped)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            DownloadProgress.Value = _manager.Progress;
                            PercentText.Text = $"{_manager.Progress:0.00}%";
                            SpeedText.Text = $"Speed: {(_manager.Monitor.DownloadRate / 1024.0 / 1024.0):0.00} MB/s";
                            
                            if (_manager.State == TorrentState.Seeding)
                            {
                                StatusText.Text = "Download Complete!";
                                DownloadProgress.Value = 100;
                            }
                        });
                        await Task.Delay(1000);
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start download: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_manager != null && _manager.State != TorrentState.Stopped && _manager.State != TorrentState.Seeding)
            {
                var result = MessageBox.Show("A download is still in progress. Are you sure you want to exit and stop the download?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            if (_manager != null)
            {
                await _manager.StopAsync();
            }
            _engine?.Dispose();
        }
    }
}
