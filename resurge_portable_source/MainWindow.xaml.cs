using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class MainWindow : Window
{
    private static readonly string LegacyConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ResurgeLauncher", "config.txt");
    private static string ConfigPath => Path.Combine(GameLauncher.DataPath, "game-folder.txt");
    private static string BundleSetting => Path.Combine(GameLauncher.DataPath, "client-bundle.txt");
    private static string ServerSettings => Path.Combine(GameLauncher.DataPath, "servers.txt");
    private static string SnowSetting => Path.Combine(GameLauncher.DataPath, "snow.txt");
    private bool _sessionActive;
    private bool _loaded;
    private DiscordRpcManager? _discord;
    private PublicMatch? _match;
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        WorkshopPanel.GetGameFolder = () => CanEditGame() ? CurrentGameFolder() : null;
        WorkshopPanel.GameFolderPath = () => Dispatcher.Invoke(() => GamePathTextBox.Text.Trim());
        WorkshopPanel.ContentInstalled = RefreshLibrary;
        WorkshopPanel.Notify = message => StatusText.Text = message;
        Loaded += OnLoaded;
        SourceInitialized += (_, _) =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985) || SystemParameters.HighContrast) return;
            int enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        };
        Closed += (_, _) =>
        {
            _discord?.Dispose();
            _ = _friends.GoOfflineAsync();
        };
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateSnow();
        IsVisibleChanged += (_, _) => UpdateSnow();
        _liveTimer.Tick += (_, _) =>
        {
            TrackMasteryKills();
            LiveTick();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string savedPath = File.Exists(ConfigPath) ? ConfigPath : LegacyConfigPath;
            if (File.Exists(savedPath))
                GamePathTextBox.Text = File.ReadAllText(savedPath).Trim();
            else
                GamePathTextBox.Text = DetectGameFolder();

            string packagedServers = Path.Combine(AppContext.BaseDirectory, "whiteout-servers.txt");
            string serverSource = File.Exists(ServerSettings) ? ServerSettings : packagedServers;
            if (File.Exists(serverSource))
                ServerAddresses.Text = File.ReadAllText(serverSource);
            if (File.Exists(SnowSetting))
                LoadEffect(File.ReadAllText(SnowSetting).Trim());
        }
        catch (IOException ex) { StatusText.Text = ex.Message; }
        catch (UnauthorizedAccessException ex) { StatusText.Text = ex.Message; }

        try
        {
            _discord = new DiscordRpcManager();
            UpdatePresence(false);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        _loaded = true;
        UpdateInstallationSummary();
        RefreshLibrary();
        LoadProgression();
        LoadBots();
        LoadController();
        UpdateSnow();
        _ = ApplyBackgroundAsync();
        _ = EnsureClientAsync();
        StartSignIn();
        _liveTimer.Start();
        StartUpdateChecks();

        if (!Motion) return;
        var duration = TimeSpan.FromMilliseconds(470);
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(.05, 1, duration));
        Entrance.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(9, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (LoginOverlay.Visibility != Visibility.Visible) PlayEntrance();
    }

    private void UpdatePresence(bool inGame)
    {
        try
        {
            if (inGame) _discord?.SetStatusInGame();
            else _discord?.SetStatusInLauncher();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_sessionActive && !WorkshopPanel.IsBusy) return;
        e.Cancel = true;
        StatusText.Text = _sessionActive ? "Close the game so Whiteout can finish logging and cleanup."
            : "Finish or cancel the Workshop download before closing Whiteout.";
    }

    private BackdropEffect SelectedEffect =>
        FxSnow?.IsChecked == true ? BackdropEffect.Snow : FxOff?.IsChecked == true ? BackdropEffect.Off : BackdropEffect.Embers;

    private void Effect_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSnow();
        if (!_loaded) return;
        TryRun(() => SaveSetting(SnowSetting, SelectedEffect.ToString().ToLowerInvariant()));
    }

    private void LoadEffect(string saved)
    {
        // Older launchers saved "1"/"0" for the snow switch.
        (saved switch { "snow" => FxSnow, "off" or "0" => FxOff, _ => FxEmbers }).IsChecked = true;
    }

    private void UpdateSnow()
    {
        if (Backdrop is not null && FxEmbers is not null)
        {
            Backdrop.Effect = SelectedEffect;
            Backdrop.Animated = IsVisible && WindowState != WindowState.Minimized && !_sessionActive;
        }
        if (_loaded && ArtZoom is not null) UpdateArtMotion();
    }

    private string? CurrentGameFolder()
    {
        string path = GamePathTextBox.Text.Trim();
        if (Directory.Exists(path)) return Path.GetFullPath(path);
        StatusText.Text = "Choose your Black Ops III game folder first.";
        return null;
    }

    private bool CanEditGame()
    {
        if (!_sessionActive && !WorkshopPanel.IsBusy) return true;
        StatusText.Text = _sessionActive ? "Close the game before changing installation files."
            : "Finish or cancel the Workshop download before changing installation files.";
        return false;
    }

    private static void SaveSetting(string file, string value)
    {
        Directory.CreateDirectory(GameLauncher.DataPath);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, value);
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void SaveGamePath(string path) => SaveSetting(ConfigPath, path);

    private static string? LegacyBundle()
    {
        try
        {
            if (File.Exists(BundleSetting) && File.ReadAllText(BundleSetting).Trim() is { Length: > 0 } saved && File.Exists(saved))
                return saved;
        }
        catch (IOException) { }
        return File.Exists(AssetManager.DefaultLegacyBundle) ? AssetManager.DefaultLegacyBundle : null;
    }

    /// <summary>Finds the game client in the background on first start so new players skip that step.</summary>
    private async Task EnsureClientAsync()
    {
        string folder = GamePathTextBox.Text.Trim();
        if (LegacyBundle() is null && !(Directory.Exists(folder) && AssetManager.HasClient(folder, null)))
        {
            string? found = await Task.Run(AssetManager.FindClientBundle);
            if (found is not null)
            {
                TryRun(() => SaveSetting(BundleSetting, found));
                StatusText.Text = "Found the Resurge game client automatically.";
            }
        }
        UpdateSetup();
    }

    private async void FindClient_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Looking for Resurge.dll…";
        string? found = await Task.Run(AssetManager.FindClientBundle);
        if (found is null)
            StatusText.Text = "Resurge.dll was not found. Download the original Resurge launcher, then choose its Resurge.dll.";
        else
        {
            TryRun(() => SaveSetting(BundleSetting, found));
            StatusText.Text = "Game client found.";
        }
        UpdateSetup();
    }

    /// <summary>Shows the setup card on the Play page until the folder and client are both ready.</summary>
    private void UpdateSetup()
    {
        string folder = GamePathTextBox.Text.Trim();
        bool folderOk = Directory.Exists(Path.Combine(folder, "zone"));
        string? bundle = LegacyBundle();
        bool clientOk = folderOk && AssetManager.HasClient(folder, bundle) || bundle is not null;
        SetupFolderMark.Text = folderOk ? "✓" : "1";
        SetupFolderText.Text = folderOk ? folder : "The folder that contains zone.";
        SetupClientMark.Text = clientOk ? "✓" : "2";
        SetupClientText.Text = clientOk ? (bundle ?? "Already installed in the game folder.") : "Resurge.dll from the original Resurge launcher.";
        SetupCard.Visibility = folderOk && clientOk ? Visibility.Collapsed : Visibility.Visible;
        BundleSummary.Text = bundle is not null ? bundle
            : folderOk && AssetManager.HasClient(folder, null) ? "The client is already in the game folder."
            : "Not found yet. Use “Find it for me”, or choose Resurge.dll from the original Resurge launcher.";
        if (!_sessionActive)
        {
            ReadyText.Text = folderOk && clientOk ? "Game ready" : "Setup needed";
            ReadyDot.Fill = (Brush)FindResource(folderOk && clientOk ? "Good" : "Warn");
        }
    }

    private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the Black Ops III game folder (the one with zone)" };
        if (dialog.ShowDialog(this) != true) return;
        if (!Directory.Exists(Path.Combine(dialog.FolderName, "zone")))
        {
            StatusText.Text = "That folder has no zone folder. Pick the Black Ops III game folder.";
            return;
        }
        GamePathTextBox.Text = dialog.FolderName;
        try
        {
            SaveGamePath(dialog.FolderName);
            StatusText.Text = "Game folder saved.";
            RefreshLibrary();
            LoadProgression();
            LoadBots();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        UpdateSetup();
    }

    private async void LaunchResurgeButton_Click(object sender, RoutedEventArgs e) => await LaunchAsync(false, false);
    private async void LaunchEzzButton_Click(object sender, RoutedEventArgs e) => await LaunchAsync(true, false);
    private async void LaunchVanilla_Click(object sender, RoutedEventArgs e) => await LaunchAsync(false, true, vanilla: true);

    private void SetSessionUi(bool running)
    {
        _sessionActive = running;
        LaunchResurgeButton.IsEnabled = LaunchEzzButton.IsEnabled = SelectFolderButton.IsEnabled = !running;
        LaunchVanillaButton.IsEnabled = DiagnosticLaunchButton.IsEnabled = !running;
        RestoreButton.IsEnabled = !running;
        GamePathTextBox.IsReadOnly = running;
        ReadyText.Text = running ? "Game running" : "Game ready";
        ReadyDot.Fill = running ? (Brush)FindResource("Accent") : (Brush)FindResource("Good");
        UpdateSnow();
        LiveTick();
    }

    private async Task LaunchAsync(bool ezz, bool diagnostic, bool vanilla = false, PublicEndpoint? server = null)
    {
        if (_profile is not { } profile || !CanEditGame() || CurrentGameFolder() is not { } path) return;
        if (!Directory.Exists(Path.Combine(path, "zone")))
        {
            StatusText.Text = "This folder is missing zone. Select the folder containing the installed BO3 game files.";
            Navigation.SelectedItem = SettingsTab;
            return;
        }
        if (!ezz && !AssetManager.HasClient(path, LegacyBundle()))
        {
            StatusText.Text = "The Resurge game client is missing. Use “Find it for me” or choose Resurge.dll.";
            Navigation.SelectedItem = PlayTab;
            UpdateSetup();
            return;
        }

        SetSessionUi(true);
        try
        {
            string name = ezz ? (File.Exists(Path.Combine(path, "boiii.exe")) ? "boiii.exe" : "EZZ boiii.exe") : "Resurge - Beta.exe";
            string? bundle = LegacyBundle();
            bool cleanup = ResurgeConfig.Read(path, "Launcher", "auto_cleanup", 1) != 0;
            SaveGamePath(path);
            if (!ezz && !vanilla) PrepareMastery(path);
            StatusText.Text = "Preparing game session…";
            var progress = new Progress<string>(line =>
            {
                if (!_sessionActive) return;
                StatusText.Text = line;
                if (line.StartsWith("Game running", StringComparison.Ordinal)) UpdatePresence(true);
            });
            var result = await Task.Run(() => GameLauncher.LaunchAsync(ezz ? "boiii" : "ms", path, name, bundle,
                cleanup, diagnostic, progress, vanilla, server));
            StatusText.Text = result.NewCrashLog || (result.ExitCode != 0 && result.ExitCode != 1)
                ? $"The game crashed (0x{unchecked((uint)result.ExitCode):X8}). It closed cleanly; you can start it again."
                : "Game session ended.";
            if (result.CleanupFailures > 0)
                StatusText.Text += $" Cleanup left {result.CleanupFailures} file(s).";
            try
            {
                _profile = PlayerProfiles.RecordSession(profile, result.Duration, result.BestRound);
                ShowProfile();
            }
            catch (Exception ex) { StatusText.Text += $" Play history could not be saved. {ex.Message}"; }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Launch failed. {ex.Message}";
        }
        finally
        {
            SetSessionUi(false);
            UpdatePresence(false);
            if (Navigation.SelectedItem == SettingsTab) RefreshDiagnostics();
            RefreshLibrary();
        }
    }

    private void ShowGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentGameFolder() is { } path) OpenShell(path);
    }

    private void DiscordSupport_Click(object sender, RoutedEventArgs e) => OpenShell("https://discord.gg/dpmRyDfbnM");

    private void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void ResurgeSettings_Click(object sender, RoutedEventArgs e)
    {
        if (CanEditGame() && CurrentGameFolder() is { } path)
            ShowTool(() => new ResurgeSettingsWindow(path));
    }

    private void ModsManager_Click(object sender, RoutedEventArgs e)
    {
        if (CanEditGame() && CurrentGameFolder() is { } path)
            ShowTool(() => new ModsManagerWindow(path));
        RefreshLibrary();
    }

    private void ShowTool(Func<Window> create)
    {
        try
        {
            var window = create();
            window.Owner = this;
            window.ShowDialog();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void CleanWgsCache_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditGame()) return;
        string packagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "38985CA0.CallofDutyBlackOps3PCMS_5bkah9njm3e9g", "SystemAppData");
        string cache = Path.Combine(packagePath, "wgs");
        try
        {
            if (!Directory.Exists(cache))
            {
                StatusText.Text = "No WGS cache folder found.";
                return;
            }
            string backup = Path.Combine(packagePath, $"wgs.whiteout-backup-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.Move(cache, backup);
            StatusText.Text = $"WGS backed up to {Path.GetFileName(backup)}.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void RepairGamingServices_Click(object sender, RoutedEventArgs e)
    {
        if (CanEditGame()) OpenShell("https://aka.ms/GamingRepairTool");
    }
}
