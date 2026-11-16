using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class MainWindow
{
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(3) };
    private Updater.Release? _release;
    private bool _updating;

    /// <summary>On start: remove the previous exe left by an update, then look for a newer release now and every 3 hours.</summary>
    private void StartUpdateChecks()
    {
        Updater.CleanUpOldVersion();
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();
        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        if (_updating) return;
        try
        {
            if (await Updater.CheckAsync() is not { } release) return;
            _release = release;
            UpdateTitle.Text = $"Whiteout {release.Label} is ready (you have {Updater.Current.ToString(2)})";
            UpdateNotes.Text = release.Notes.Length > 0 ? release.Notes : "Restarts the launcher in a few seconds. Your profiles and settings stay.";
            UpdateCard.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_release is not { } release || _updating) return;
        if (_sessionActive || WorkshopPanel.IsBusy)
        {
            StatusText.Text = _sessionActive ? "Close the game first, then update." : "Finish the Workshop download first, then update.";
            return;
        }
        _updating = true;
        UpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            StatusText.Text = $"Downloading Whiteout {release.Label}…";
            var progress = new Progress<double>(value =>
            {
                UpdateProgress.Value = value;
                UpdateButton.Content = $"{value:P0}";
            });
            string file = await Updater.DownloadAsync(release, progress);
            StatusText.Text = "Installing the update…";
            Updater.Install(file);
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Update failed. {ex.Message}";
            UpdateButton.Content = "Try again";
            UpdateButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
            _updating = false;
        }
    }
}
