using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class MainWindow
{
    /// <summary>One mastery tier on the Unlocks page; its preview flips through the animation frames.</summary>
    private sealed class MasteryItem(Mastery.Tier tier, ImageSource[] frames) : INotifyPropertyChanged
    {
        private int _frame;
        public Mastery.Tier Tier { get; } = tier;
        public ImageSource? Preview => frames.Length > 0 ? frames[_frame % frames.Length] : null;
        public string Requirement { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Remaining { get; set; } = "";
        public Visibility LockVisibility { get; set; }
        public Brush Accent { get; set; } = Brushes.White;
        public Brush Frame { get; set; } = Brushes.Transparent;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Step()
        {
            _frame++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    private readonly DispatcherTimer _masteryFlip = new() { Interval = TimeSpan.FromMilliseconds(333) };
    private MasteryItem[] _masteryItems = [];
    private static readonly Dictionary<string, ImageSource[]> MasteryFrames = [];

    private static ImageSource[] MasteryPreview(string key)
    {
        if (MasteryFrames.TryGetValue(key, out var cached)) return cached;
        var frames = new List<ImageSource>();
        for (int i = 0; i < 12; i++)
        {
            try
            {
                var image = new BitmapImage(new Uri($"pack://application:,,,/Assets/camos/{key}_{i:00}.png"));
                image.Freeze();
                frames.Add(image);
            }
            catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException) { break; }
        }
        return MasteryFrames[key] = frames.ToArray();
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Refreshes the mastery card from the signed-in profile.</summary>
    private void ShowMastery()
    {
        if (_profile is not { } profile) return;
        if (_masteryItems.Length == 0)
        {
            _masteryItems = Mastery.Tiers.Select(tier => new MasteryItem(tier, MasteryPreview(tier.Key))).ToArray();
            _masteryFlip.Tick += (_, _) =>
            {
                if (Navigation.SelectedItem != UnlocksTab || WindowState == WindowState.Minimized) return;
                foreach (var item in _masteryItems) item.Step();
            };
            _masteryFlip.Start();
        }
        long kills = profile.MasteryKills;
        foreach (var item in _masteryItems)
        {
            bool earned = kills >= item.Tier.Kills;
            item.Requirement = $"{Number(item.Tier.Kills)} kills";
            item.Detail = earned ? $"Unlocked. Shows as \"{item.Tier.Host}\" in Extras." : item.Tier.Blurb;
            item.Remaining = earned ? "" : $"{Number(item.Tier.Kills - kills)} to go";
            item.LockVisibility = earned ? Visibility.Collapsed : Visibility.Visible;
            item.Accent = earned ? Res("Good") : Res("Accent");
            item.Frame = earned ? Res("Accent") : Res("Line");
        }
        MasteryTiers.ItemsSource = null; // Rebind so the per-tier text refreshes.
        MasteryTiers.ItemsSource = _masteryItems;
        MasteryKillsText.Text = Number(kills);
        var next = Mastery.NextTier(kills);
        long previous = Mastery.Tiers.Where(tier => tier.Kills <= kills).Select(tier => tier.Kills).DefaultIfEmpty(0).Max();
        MasteryProgress.Value = next is null ? 1 : (double)(kills - previous) / (next.Kills - previous);
        MasteryNextText.Text = next is null
            ? "Every mastery camo unlocked. Legendary."
            : $"{Number(next.Kills - kills)} more kills to unlock {next.Name}. Kills with any weapon count.";
    }

    /// <summary>Before Play: install the camo files and tell the runtime which tiers are earned.</summary>
    private void PrepareMastery(string gamePath)
    {
        if (_profile is not { } profile) return;
        try
        {
            Mastery.Install();
            ResurgeConfig.Write(gamePath, Mastery.Settings(profile.MasteryKills));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Mastery camos are unavailable this session. {ex.Message}";
        }
    }

    /// <summary>During a session: add new kills reported by the runtime, and unlock tiers live.</summary>
    private void TrackMasteryKills()
    {
        if (!_sessionActive || _profile is not { } profile || Mastery.ReadStatus() is not { } status) return;
        try
        {
            int before = Mastery.UnlockedTier(profile.MasteryKills);
            var (updated, added) = PlayerProfiles.RecordKills(profile, status.Source, status.Kills);
            _profile = updated;
            if (added == 0) return;
            ShowMastery();
            int after = Mastery.UnlockedTier(updated.MasteryKills);
            if (after <= before) return;
            if (CurrentGameFolder() is { } path)
                ResurgeConfig.Write(path, ("WhiteoutMastery", "tier", after.ToString(CultureInfo.InvariantCulture)));
            var tier = Mastery.Tiers[after - 1];
            var frames = MasteryPreview(tier.Key);
            _toast ??= new NotificationToast();
            _toast.Show("Mastery camo unlocked", tier.Name, $"{Number(tier.Kills)} zombie kills. Equip it as \"{tier.Host}\" in Extras.",
                tier.Name[..1], frames.Length > 0 ? new ImageBrush(frames[0]) { Stretch = Stretch.UniformToFill } : null,
                () => { BringToFront(); Navigation.SelectedItem = UnlocksTab; });
            if (_notifySound) PlaySound("Notification.Default", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or System.ComponentModel.Win32Exception)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
