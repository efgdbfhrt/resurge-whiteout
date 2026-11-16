using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class MainWindow
{
    /// <summary>A row in the installed list. <see cref="FullPath"/> is set for Workshop maps and mods, which can be uninstalled.</summary>
    private sealed record LibraryEntry(string Type, string Title, string Folder, string? FullPath = null, bool Confirming = false)
    {
        public Visibility RemoveVisibility => FullPath is null ? Visibility.Collapsed : Visibility.Visible;
        public string RemoveText => Confirming ? "Click again to uninstall" : "Uninstall";
        public string RemoveName => $"Uninstall {Title}";
    }

    private string? _confirmUninstall;
    private DateTime _confirmUninstallAt;
    private static readonly (string Id, string Name)[] OfficialZombieMaps =
    [
        ("zm_zod", "Shadows of Evil"), ("zm_factory", "The Giant"),
        ("zm_castle", "Der Eisendrache"), ("zm_island", "Zetsubou No Shima"),
        ("zm_stalingrad", "Gorod Krovi"), ("zm_genesis", "Revelations"),
        ("zm_prototype", "Nacht der Untoten"), ("zm_asylum", "Verrückt"),
        ("zm_sumpf", "Shi No Numa"), ("zm_theater", "Kino der Toten"),
        ("zm_cosmodrome", "Ascension"), ("zm_temple", "Shangri-La"),
        ("zm_moon", "Moon"), ("zm_tomb", "Origins")
    ];
    private static readonly string[] BotDifficultyNames = ["Recruit", "Regular", "Hardened", "Veteran"];
    private bool _loadingBots;
    private int _botAllies = 6;
    private int _botEnemies = 6;

    private void GoSettings_Click(object sender, RoutedEventArgs e) => Navigation.SelectedItem = SettingsTab;
    private void GoMultiplayer_Click(object sender, RoutedEventArgs e) => Navigation.SelectedItem = MultiplayerTab;
    private void GoWorkshop_Click(object sender, RoutedEventArgs e) => Navigation.SelectedItem = WorkshopTab;
    private void GoFriends_Click(object sender, RoutedEventArgs e) => Navigation.SelectedItem = FriendsTab;

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || !ReferenceEquals(e.Source, Navigation)) return;
        if (Navigation.Template.FindName("PageScroll", Navigation) is ScrollViewer scroll) scroll.ScrollToTop();
        AnimatePageChange();
        if (Navigation.SelectedItem == WorkshopTab)
        {
            WorkshopPanel.EnsureBrowsed();
            RefreshLibrary();
        }
        if (Navigation.SelectedItem == UnlocksTab) LoadProgression();
        if (Navigation.SelectedItem == MultiplayerTab) LoadBots();
        if (Navigation.SelectedItem == SettingsTab) RefreshDiagnostics();
        if (Navigation.SelectedItem == FriendsTab) _ = RefreshFriendsAsync();
        if (Navigation.SelectedItem == ControllerTab) ShowController();
        if (Navigation.SelectedItem == GraphicsTab) ShowGraphics();
    }

    private void WorkshopMode_Changed(object sender, RoutedEventArgs e)
    {
        if (LibraryPanel is null || WorkshopPanel is null) return;
        bool installed = WorkshopInstalledMode.IsChecked == true;
        LibraryPanel.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        WorkshopPanel.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        if (installed) RefreshLibrary();
    }

    private void GamePath_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loaded) UpdateInstallationSummary();
    }

    private void UpdateInstallationSummary()
    {
        string path = GamePathTextBox.Text.Trim();
        bool valid = Directory.Exists(Path.Combine(path, "zone"));
        InstallSummary.Text = valid ? path : "Choose your Black Ops III installation in Settings.";
        InstallSummary.ToolTip = InstallSummary.Text;
        if (_loaded) UpdateSetup();
    }

    /// <summary>Looks for a Black Ops III folder (Steam libraries, Xbox app folders, common names).</summary>
    private static string DetectGameFolder()
    {
        var candidates = new List<string>();
        string steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        var libraries = new List<string> { steam };
        string list = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(list))
                foreach (string line in File.ReadLines(list))
                {
                    string[] parts = line.Split('"');
                    if (parts.Length >= 5 && parts[1] == "path")
                        libraries.Add(parts[3].Replace(@"\\", @"\", StringComparison.Ordinal));
                }
        }
        catch (IOException) { }
        candidates.AddRange(libraries.Select(folder => Path.Combine(folder, @"steamapps\common\Call of Duty Black Ops III")));
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
        {
            string root = drive.RootDirectory.FullName;
            candidates.Add(Path.Combine(root, "black ops 3", "t7_full_game"));
            candidates.Add(Path.Combine(root, "t7_full_game"));
            candidates.Add(Path.Combine(root, "XboxGames", "Call of Duty Black Ops III", "Content"));
            candidates.Add(Path.Combine(root, "Games", "Black Ops III"));
        }
        return candidates.FirstOrDefault(folder => Directory.Exists(Path.Combine(folder, "zone"))) ?? "";
    }

    private void SaveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditGame() || CurrentGameFolder() is not { } path) return;
        if (!Directory.Exists(Path.Combine(path, "zone")))
        {
            StatusText.Text = "The selected folder does not contain the BO3 zone directory.";
            return;
        }
        TryRun(() =>
        {
            SaveGamePath(path);
            StatusText.Text = "Game folder saved.";
            RefreshLibrary();
        });
        UpdateSetup();
    }

    private void SelectBundle_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditGame()) return;
        var dialog = new OpenFileDialog { Title = "Select Resurge.dll from the original Resurge launcher", Filter = "Resurge.dll|Resurge.dll" };
        if (dialog.ShowDialog(this) != true) return;
        TryRun(() =>
        {
            AssetManager.ValidateLegacyBundle(dialog.FileName);
            SaveSetting(BundleSetting, dialog.FileName);
            StatusText.Text = "Game client saved.";
        });
        UpdateSetup();
    }

    // ---------------------------------------------------------------- Bots

    private void LoadBots()
    {
        string path = GamePathTextBox.Text.Trim();
        if (!Directory.Exists(path)) return;
        _loadingBots = true;
        try
        {
            BotsEnabled.IsChecked = ResurgeConfig.Read(path, "WhiteoutBots", "enabled", 0) == 1;
            _botAllies = Math.Clamp(ResurgeConfig.Read(path, "WhiteoutBots", "allies", 6), 0, 9);
            _botEnemies = Math.Clamp(ResurgeConfig.Read(path, "WhiteoutBots", "enemies", 6), 0, 9);
            int difficulty = Math.Clamp(ResurgeConfig.Read(path, "WhiteoutBots", "difficulty", 3), 0, 3);
            new[] { BotDiff0, BotDiff1, BotDiff2, BotDiff3 }[difficulty].IsChecked = true;
        }
        finally { _loadingBots = false; }
        ShowBots();
    }

    private int BotDifficulty =>
        BotDiff0.IsChecked == true ? 0 : BotDiff1.IsChecked == true ? 1 : BotDiff2.IsChecked == true ? 2 : 3;

    private void ShowBots()
    {
        BotAlliesText.Text = _botAllies.ToString(CultureInfo.InvariantCulture);
        BotEnemiesText.Text = _botEnemies.ToString(CultureInfo.InvariantCulture);
        BotsSummary.Text = BotsEnabled.IsChecked == true
            ? $"{BotDifficultyNames[BotDifficulty]} bots  ·  {_botAllies} v {_botEnemies}"
            : "Bots off. Turn them on to fill your lobbies.";
    }

    private void BotSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _loadingBots) return;
        SaveBots();
    }

    private void AlliesDown_Click(object sender, RoutedEventArgs e) => StepBots(ref _botAllies, -1);
    private void AlliesUp_Click(object sender, RoutedEventArgs e) => StepBots(ref _botAllies, 1);
    private void EnemiesDown_Click(object sender, RoutedEventArgs e) => StepBots(ref _botEnemies, -1);
    private void EnemiesUp_Click(object sender, RoutedEventArgs e) => StepBots(ref _botEnemies, 1);

    private void StepBots(ref int value, int delta)
    {
        value = Math.Clamp(value + delta, 0, 9);
        SaveBots();
    }

    private void SaveBots()
    {
        ShowBots();
        if (!WriteGameConfig(
            ("WhiteoutBots", "enabled", BotsEnabled.IsChecked == true ? "1" : "0"),
            ("WhiteoutBots", "allies", _botAllies.ToString(CultureInfo.InvariantCulture)),
            ("WhiteoutBots", "enemies", _botEnemies.ToString(CultureInfo.InvariantCulture)),
            ("WhiteoutBots", "difficulty", BotDifficulty.ToString(CultureInfo.InvariantCulture))))
            return;
        BotStatus.Text = "Saved for the next session.";
    }

    /// <summary>Writes resurge.ini while the game is closed. Returns false and explains why otherwise.</summary>
    private bool WriteGameConfig(params (string Section, string Key, string Value)[] values)
    {
        if (!CanEditGame() || CurrentGameFolder() is not { } path) return false;
        try
        {
            using var folderLock = GameLauncher.LockFolder(path);
            GameLauncher.RequireStopped(path);
            GameLauncher.RecoverInterruptedSession(path);
            ResurgeConfig.Write(path, values);
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------- Community servers (EZZ)

    private async void FindMatch_Click(object sender, RoutedEventArgs e)
    {
        _match = null;
        JoinPublicButton.IsEnabled = CopyJoinButton.IsEnabled = false;
        try
        {
            var addresses = PublicServerSearch.ParseAddresses(ServerAddresses.Text);
            FindMatchButton.IsEnabled = false;
            ServerAddresses.IsReadOnly = true;
            PublicResult.Text = addresses.Length == 0 ? "Searching the EZZ public directory…" : $"Checking {addresses.Length} community server(s)…";
            PublicDetails.Text = "Looking for a running Multiplayer match with open slots.";
            _match = await PublicServerSearch.FindAsync(addresses);
            if (_match is not { } result)
            {
                PublicResult.Text = "No joinable match responded.";
                PublicDetails.Text = addresses.Length == 0
                    ? "No public server responded with an open Multiplayer slot."
                    : "Check the address, UDP port, and whether the dedicated server is running a map.";
                return;
            }

            PublicResult.Text = result.Name;
            PublicDetails.Text = $"Players {result.Humans}  ·  Bots {result.Bots}  ·  Open slots {result.Available}  ·  {result.Ping} ms\n{result.Map}  /  {result.Address}";
            JoinPublicButton.IsEnabled = CopyJoinButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            PublicResult.Text = ex.Message;
            PublicDetails.Text = ex is FormatException ? "Enter an IPv4 address or hostname and port on each line." : "Check the EZZ directory or enter a dedicated server address.";
        }
        finally
        {
            FindMatchButton.IsEnabled = true;
            ServerAddresses.IsReadOnly = false;
        }
    }

    private async void JoinPublic_Click(object sender, RoutedEventArgs e)
    {
        if (_match is not { } match || CurrentGameFolder() is not { } path) return;
        if (!File.Exists(Path.Combine(path, "boiii.exe")))
        {
            StatusText.Text = "Direct join requires a current EZZ BOIII client (boiii.exe). The connect command is also available.";
            return;
        }
        await LaunchAsync(true, false, server: match.Address);
    }

    private void SaveServers_Click(object sender, RoutedEventArgs e) => TryRun(() =>
    {
        var addresses = PublicServerSearch.ParseAddresses(ServerAddresses.Text);
        SaveSetting(ServerSettings, string.Join(Environment.NewLine, addresses.Select(address => address.ToString())));
        StatusText.Text = "Server addresses saved.";
    });

    private void CopyJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_match is not { } match) return;
        TryRun(() =>
        {
            Clipboard.SetText($"/connect {match.Address}");
            StatusText.Text = "Connect command copied. Paste it into the EZZ BOIII console.";
        });
    }

    private void ExportServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "whiteout-public.cfg", Filter = "Server preset (*.cfg)|*.cfg" };
        if (dialog.ShowDialog(this) != true) return;
        TryRun(() =>
        {
            using var preset = typeof(MainWindow).Assembly.GetManifestResourceStream("Whiteout.PublicServer.cfg")
                ?? throw new InvalidDataException("Server preset is missing.");
            using var output = File.Create(dialog.FileName);
            preset.CopyTo(output);
            StatusText.Text = "Server preset exported. Load it on your EZZ BOIII dedicated host.";
        });
    }

    // ---------------------------------------------------------------- Library

    private void RefreshLibrary_Click(object sender, RoutedEventArgs e) => RefreshLibrary();

    private void RefreshLibrary()
    {
        try
        {
            string path = GamePathTextBox.Text.Trim();
            if (!Directory.Exists(path))
            {
                LibraryItems.ItemsSource = null;
                LibraryStatus.Text = "Choose the game folder in Settings.";
                OfficialMapCount.Text = WorkshopCount.Text = "—";
                return;
            }

            var content = new List<LibraryEntry>();
            foreach (var (id, name) in OfficialZombieMaps)
            {
                var fastfile = new FileInfo(Path.Combine(path, "zone", id + ".ff"));
                if (fastfile.Exists && fastfile.Length > 0)
                    content.Add(new LibraryEntry("OFFICIAL", name, $"zone/{fastfile.Name}"));
            }
            int official = content.Count;
            int workshop = 0;
            foreach (var (root, type) in new[] { ("usermaps", "WORKSHOP"), ("mods", "MOD") })
            {
                string folder = Path.Combine(path, root);
                if (!Directory.Exists(folder)) continue;
                foreach (string itemPath in Directory.EnumerateDirectories(folder))
                {
                    string folderName = Path.GetFileName(itemPath);
                    if (folderName.Contains(".whiteout-", StringComparison.Ordinal)) continue;
                    string title;
                    try { title = WorkshopService.ReadItem(itemPath).Title; }
                    catch (InvalidDataException) { title = folderName; }
                    catch (IOException) { title = folderName; }
                    catch (System.Text.Json.JsonException) { title = folderName; }
                    bool isMap = root == "usermaps";
                    var fastfile = new FileInfo(Path.Combine(itemPath, "zone", folderName + ".ff"));
                    string detail = $"{root}/{folderName}";
                    if (isMap && (!fastfile.Exists || fastfile.Length == 0))
                        detail += "  ·  Main map file missing";
                    bool confirming = _confirmUninstall == itemPath && DateTime.UtcNow - _confirmUninstallAt < TimeSpan.FromSeconds(6);
                    content.Add(new LibraryEntry(type, title, detail, itemPath, confirming));
                    workshop++;
                }
            }
            LibraryItems.ItemsSource = content.OrderBy(item => item.Type != "WORKSHOP").ThenBy(item => item.Type).ThenBy(item => item.Title).ToArray();
            LibraryStatus.Text = $"{official} official maps  ·  {workshop} Workshop item(s)";
            OfficialMapCount.Text = official.ToString(CultureInfo.InvariantCulture);
            WorkshopCount.Text = workshop.ToString(CultureInfo.InvariantCulture);
            ZombiesSummary.Text = workshop > 0
                ? $"{official} official maps + {workshop} from the Workshop"
                : $"{official} official maps, DLC included";
        }
        catch (Exception ex) { LibraryStatus.Text = ex.Message; }
    }

    /// <summary>
    /// Uninstalls a Workshop map or mod: the first click asks for confirmation in place, the second moves
    /// its folder to the Recycle Bin (so it can be restored from there).
    /// </summary>
    private void UninstallItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LibraryEntry { FullPath: { } itemPath } entry }) return;
        if (!CanEditGame()) return;
        if (_confirmUninstall != itemPath || DateTime.UtcNow - _confirmUninstallAt > TimeSpan.FromSeconds(6))
        {
            _confirmUninstall = itemPath;
            _confirmUninstallAt = DateTime.UtcNow;
            RefreshLibrary();
            return;
        }
        _confirmUninstall = null;
        try
        {
            string game = Path.GetFullPath(GamePathTextBox.Text.Trim());
            string full = Path.GetFullPath(itemPath);
            string parent = Path.GetDirectoryName(full) ?? "";
            bool inside = string.Equals(parent, Path.Combine(game, "usermaps"), StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(parent, Path.Combine(game, "mods"), StringComparison.OrdinalIgnoreCase);
            if (!inside || !Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            {
                StatusText.Text = "That item isn't in the game's usermaps or mods folder, so it wasn't removed.";
                return;
            }
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(full,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            StatusText.Text = $"Uninstalled {entry.Title}. It's in the Recycle Bin if you want it back.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            StatusText.Text = $"Couldn't uninstall {entry.Title}. {ex.Message}";
        }
        RefreshLibrary();
    }

    // ---------------------------------------------------------------- Troubleshooting

    private void ReportExpander_Expanded(object sender, RoutedEventArgs e) => RefreshDiagnostics();
    private async void DiagnosticLaunch_Click(object sender, RoutedEventArgs e) => await LaunchAsync(false, true);

    private void RefreshDiagnostics()
    {
        TryRun(() =>
        {
            string path = GamePathTextBox.Text.Trim();
            bool hasFolder = Directory.Exists(path);
            bool backup = hasFolder && (ResurgeConfig.HasDiagnosticBackup(path) || AssetManager.HasNativeBackup(path));
            DiagnosticLaunchButton.IsEnabled = hasFolder && !_sessionActive;
            RestoreButton.IsEnabled = backup && !_sessionActive;
            OpenLogButton.IsEnabled = File.Exists(GameLauncher.LogPath);
            DiagnosticsReport.Text = InstallationDiagnostics.CreateReport(path, LegacyBundle() ?? "");
        });
    }

    private void RestoreFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditGame() || CurrentGameFolder() is not { } path) return;
        TryRun(() =>
        {
            using var folderLock = GameLauncher.LockFolder(path);
            GameLauncher.RequireStopped(path);
            bool restored = AssetManager.RestoreNative(path) | ResurgeConfig.RestoreDiagnosticBackup(path);
            StatusText.Text = restored ? "Original game files and settings restored." : "No backup found.";
            RefreshDiagnostics();
        });
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e) => OpenShell(GameLauncher.LogPath);

    private void TryRun(Action action)
    {
        try { action(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
