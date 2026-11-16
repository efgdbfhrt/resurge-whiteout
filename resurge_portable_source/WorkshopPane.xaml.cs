using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class WorkshopPane : UserControl
{
    /// <summary>Returns the game folder, asking the player to fix it when it is missing.</summary>
    internal Func<string?>? GetGameFolder { get; set; }
    /// <summary>Returns the game folder silently (null when not set), used to mark installed items.</summary>
    internal Func<string?>? GameFolderPath { get; set; }
    internal Action? ContentInstalled { get; set; }
    internal Action<string>? Notify { get; set; }
    internal bool IsBusy => _operation is not null;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _query;
    private string _sort = WorkshopBrowser.Sorts[0].Sort;
    private int _page = 1;
    private int _pages = 1;
    private bool _browsed;

    public WorkshopPane()
    {
        InitializeComponent();
        foreach (var (label, sort) in WorkshopBrowser.Sorts)
        {
            var option = new RadioButton
            {
                Content = label, GroupName = "WorkshopSort", Tag = sort,
                Style = (Style)FindResource("Segment"), IsChecked = sort == _sort
            };
            option.Checked += Sort_Changed;
            SortButtons.Children.Add(option);
        }
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(WorkshopPathText.Text))
                WorkshopPathText.Text = FindWorkshopFolder();
        };
    }

    /// <summary>Loads the first page the first time the Workshop page opens.</summary>
    internal void EnsureBrowsed()
    {
        if (_browsed) return;
        _browsed = true;
        _ = BrowseAsync();
    }

    internal void SelectItem(string itemId)
    {
        SearchText.Text = itemId;
        _page = 1;
        _ = BrowseAsync();
    }

    private string Category => CatMods.IsChecked == true ? "mods" : CatMaps.IsChecked == true ? "maps" : "all";

    /// <summary>A pasted Workshop link or a long numeric ID opens that one item.</summary>
    private static bool IsItemReference(string text, out string id)
    {
        id = "";
        text = text.Trim();
        if (text.Length == 0) return false;
        var link = System.Text.RegularExpressions.Regex.Match(text,
            @"steamcommunity\.com/\S*?[?&]id=(\d{4,20})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (link.Success) text = link.Groups[1].Value;
        else if (text.Length < 7 || !text.All(char.IsAsciiDigit)) return false;
        return WorkshopService.TryReadId(text, out id);
    }

    private async Task BrowseAsync()
    {
        _query?.Cancel();
        using var query = new CancellationTokenSource();
        _query = query;
        BrowseStatus.Text = "Loading the Steam Workshop…";
        PrevPage.IsEnabled = NextPage.IsEnabled = false;
        try
        {
            if (IsItemReference(SearchText.Text, out string id))
            {
                var item = await WorkshopBrowser.GetItemAsync(id, query.Token);
                if (!ReferenceEquals(_query, query)) return;
                Results.ItemsSource = item is null ? Array.Empty<WorkshopListing>() : await Task.Run(() => MarkInstalled([item]));
                BrowseStatus.Text = item is null ? "That Workshop item was not found, or it is private." : "";
                ResultCount.Text = "";
                Pager.Visibility = Visibility.Collapsed;
                return;
            }

            var page = await WorkshopBrowser.QueryAsync(Category, _sort, SearchText.Text, _page, query.Token);
            if (!ReferenceEquals(_query, query)) return;
            _pages = page.TotalPages;
            Results.ItemsSource = await Task.Run(() => MarkInstalled(page.Items));
            BrowseStatus.Text = page.Items.Length == 0
                ? "Nothing found. Try fewer words, or paste the item's Steam link."
                : "";
            ResultCount.Text = page.TotalCount > 0 ? $"{page.TotalCount:N0} results" : "";
            Pager.Visibility = _pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            PageText.Text = $"Page {_page} of {_pages:N0}";
            PrevPage.IsEnabled = _page > 1;
            NextPage.IsEnabled = _page < _pages;
            if (_page > 1) ScrollToTop();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_query, query)) return;
            BrowseStatus.Text = $"Steam Workshop could not be loaded. Check your internet and press Search. ({ex.Message})";
            PrevPage.IsEnabled = _page > 1;
            NextPage.IsEnabled = true;
        }
        finally
        {
            if (ReferenceEquals(_query, query)) _query = null;
        }
    }

    private void ScrollToTop()
    {
        DependencyObject? node = this;
        while (node is not null and not ScrollViewer)
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        (node as ScrollViewer)?.ScrollToTop();
    }

    private WorkshopListing[] MarkInstalled(WorkshopListing[] items)
    {
        string? folder = GameFolderPath?.Invoke();
        if (folder is null || !Directory.Exists(folder)) return items;
        var installed = WorkshopService.InstalledIds(folder);
        return items.Select(item => item with { Installed = installed.Contains(item.Id) }).ToArray();
    }

    internal void RefreshInstalledState()
    {
        if (Results.ItemsSource is WorkshopListing[] items)
            Results.ItemsSource = MarkInstalled(items);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        _page = 1;
        _ = BrowseAsync();
    }

    private void SearchText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _page = 1;
        _ = BrowseAsync();
    }

    private void SearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // A pasted link opens straight away.
        if (IsLoaded && SearchText.Text.Contains("steamcommunity.com", StringComparison.OrdinalIgnoreCase) &&
            IsItemReference(SearchText.Text, out _))
        {
            _page = 1;
            _ = BrowseAsync();
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _page = 1;
        _ = BrowseAsync();
    }

    private void Sort_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string sort }) return;
        _sort = sort;
        if (!IsLoaded) return;
        _page = 1;
        _ = BrowseAsync();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 1) return;
        _page--;
        _ = BrowseAsync();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        _ = BrowseAsync();
    }

    private void OpenOnSteam_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || !WorkshopService.TryReadId(id, out id)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WorkshopListing item }) return;
        if (GetGameFolder?.Invoke() is not { } gamePath) return;
        await RunAsync(item.Title, async (status, percent, token) =>
        {
            var installed = await WorkshopService.DownloadAsync(gamePath, item.Id, false, status, token, item.FileSize, percent);
            string where = item.Kind switch
            {
                "MOD" => "In game: Mods.",
                "MP MAP" => "In game: Multiplayer › Custom Games › Map.",
                _ => "In game: Zombies › Change Map."
            };
            return $"Installed {installed.Title}. {where}";
        });
    }

    private static string FindWorkshopFolder()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
            foreach (string library in new[] { @"Program Files (x86)\Steam", @"Program Files\Steam", "Steam", "SteamLibrary" })
            {
                string folder = Path.Combine(drive.RootDirectory.FullName, library, @"steamapps\workshop\content\311210");
                if (Directory.Exists(folder)) return folder;
            }
        }
        return Directory.Exists(WorkshopService.CacheRoot) ? WorkshopService.CacheRoot : "";
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the Black Ops III Steam Workshop folder (311210)" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            WorkshopPathText.Text = dialog.FolderName;
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        string source = WorkshopPathText.Text.Trim();
        if (!Directory.Exists(source))
        {
            StatusText.Text = "Select the folder containing Steam's numbered Workshop item folders.";
            return;
        }

        if (GetGameFolder?.Invoke() is not { } gamePath) return;
        await RunAsync("Importing Steam library", (status, percent, token) =>
        {
            using var gameLock = GameLauncher.LockFolder(gamePath);
            string[] folders = Directory.EnumerateDirectories(source)
                .Where(folder => WorkshopService.TryReadId(Path.GetFileName(folder), out _)).ToArray();
            int count = 0, done = 0;
            var errors = new List<string>();
            foreach (string folder in folders)
            {
                token.ThrowIfCancellationRequested();
                string id = Path.GetFileName(folder);
                try
                {
                    WorkshopService.Install(gamePath, folder, id, status, token);
                    count++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{id}: {ex.Message}");
                }
                percent.Report(100.0 * ++done / folders.Length);
            }
            return Task.FromResult(errors.Count == 0 ? $"Imported {count} item(s)." :
                $"Imported {count}; skipped {errors.Count}. {errors[0]}");
        });
    }

    private async Task RunAsync(string title, Func<IProgress<string>, IProgress<double>, CancellationToken, Task<string>> run)
    {
        if (_operation is not null)
        {
            StatusText.Text = "Wait for the current download to finish.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        ImportBtn.IsEnabled = false;
        Results.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        DownloadCard.Visibility = Visibility.Visible;
        DownloadTitle.Text = title;
        DownloadDetail.Text = "Preparing…";
        Activity.IsIndeterminate = true;
        Activity.Value = 0;
        StatusText.Text = "";
        var progress = new Progress<string>(line =>
        {
            if (ReferenceEquals(_operation, cancellation) && !cancellation.IsCancellationRequested)
                DownloadDetail.Text = line;
        });
        var percent = new Progress<double>(value =>
        {
            if (!ReferenceEquals(_operation, cancellation) || value <= 0) return;
            Activity.IsIndeterminate = false;
            Activity.Value = value;
            DownloadTitle.Text = $"{title}  ·  {value:0}%";
        });
        try
        {
            string result = await Task.Run(() => run(progress, percent, cancellation.Token));
            StatusText.Text = result;
            Notify?.Invoke(result);
            ContentInstalled?.Invoke();
            RefreshInstalledState();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled. Completed installations were kept.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            Notify?.Invoke(ex.Message);
        }
        finally
        {
            _operation = null;
            Activity.IsIndeterminate = false;
            DownloadCard.Visibility = Visibility.Collapsed;
            CancelBtn.IsEnabled = false;
            Results.IsEnabled = true;
            ImportBtn.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelBtn.IsEnabled = false;
        DownloadDetail.Text = "Cancelling…";
        _operation?.Cancel();
    }
}
