using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class MainWindow
{
    private const string ProgressionSection = "WhiteoutProgression";
    private const string FlavorsSection = "WhiteoutGums";
    private sealed record Flavor(string Key, int Index, string Name, string Glyph, Brush Color, string IconUrl)
    {
        public ImageSource? Icon { get; init; }
        public Visibility FallbackVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    }
    private Flavor[]? _flavors;
    private bool _loadingUnlocks;
    private bool _iconsRequested;

    private async void LoadGumIcons()
    {
        if (_iconsRequested || _flavors is null) return;
        _iconsRequested = true;
        try
        {
            await GumIcons.EnsureAsync(_flavors.Select(flavor => (flavor.Key, flavor.IconUrl)), CancellationToken.None);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        string? selected = (GumGrid.SelectedItem as Flavor)?.Key;
        _flavors = _flavors.Select(flavor => flavor with { Icon = GumIcons.Load(flavor.Key) }).ToArray();
        GumGrid.ItemsSource = _flavors;
        GumGrid.SelectedItem = _flavors.FirstOrDefault(flavor => flavor.Key == selected) ?? _flavors.FirstOrDefault();
        UpdateFlavor();
    }

    private void LoadFlavors()
    {
        if (_flavors is not null) return;
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("Whiteout.GobbleGums")
            ?? throw new InvalidDataException("The GobbleGum catalog is missing.");
        using var input = new StreamReader(stream);
        var flavors = new List<Flavor>();
        string[] colors = ["#2679AF", "#653AA6", "#35866F", "#C06F25", "#A12E5C"];
        while (input.ReadLine() is { } line)
        {
            var fields = line.Split('\t');
            if (fields.Length < 3) continue;
            string key = fields[2];
            string name = key switch
            {
                "zm_bgb_im_feelin_lucky" => "I’m Feelin’ Lucky",
                "zm_bgb_whos_keeping_score" => "Who’s Keeping Score?",
                _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key[7..].Replace('_', ' '))
            };
            string glyph = string.Concat(name.Split(' ').Take(2).Select(word => word[0]));
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[flavors.Count % colors.Length]));
            brush.Freeze();
            flavors.Add(new Flavor(key, int.Parse(fields[1], CultureInfo.InvariantCulture), name, glyph, brush,
                fields.Length > 4 ? fields[4] : "") { Icon = GumIcons.Load(key) });
        }
        _flavors = flavors.OrderBy(flavor => flavor.Name).ToArray();
        GumGrid.ItemsSource = _flavors;
        GumGrid.SelectedIndex = 0;
        LoadGumIcons();
    }

    private void LoadProgression()
    {
        TryRun(() =>
        {
            LoadFlavors();
            string path = GamePathTextBox.Text.Trim();
            bool folder = Directory.Exists(path);
            _loadingUnlocks = true;
            try
            {
                StatsCamos.IsChecked = folder && ResurgeConfig.Read(path, ProgressionSection, "camos", 0) == 1;
                StatsAttachments.IsChecked = folder && ResurgeConfig.Read(path, ProgressionSection, "attachments", 0) == 1;
                StatsRank.IsChecked = folder && ResurgeConfig.Read(path, ProgressionSection, "level_1000", 0) == 1;
                StatsWeapons.IsChecked = folder && ResurgeConfig.Read(path, ProgressionSection, "weapon_max", 0) == 1;
                MegaAll.IsChecked = folder && ResurgeConfig.Read(path, ProgressionSection, "enabled", 0) == 1 &&
                    ResurgeConfig.Read(path, ProgressionSection, "all_mega", -1) >= 1;
            }
            finally { _loadingUnlocks = false; }
            UpdateFlavor();
            ShowUnlockSummary();
        });
    }

    private void ShowUnlockSummary()
    {
        var on = new List<string>();
        if (StatsCamos.IsChecked == true) on.Add("camos");
        if (StatsAttachments.IsChecked == true) on.Add("attachments");
        if (StatsWeapons.IsChecked == true) on.Add("max weapons");
        if (StatsRank.IsChecked == true) on.Add("level 1000");
        if (MegaAll.IsChecked == true) on.Add("Mega ×999");
        UnlockSummary.Text = on.Count == 0 ? "Unlocks off" : on.Count == 5 ? "Everything unlocked" : "Unlocked: " + string.Join(", ", on);
        UnlockAllButton.IsEnabled = on.Count < 5;
    }

    private void Unlock_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _loadingUnlocks) return;
        SaveUnlocks();
    }

    private void UnlockAll_Click(object sender, RoutedEventArgs e)
    {
        _loadingUnlocks = true;
        StatsCamos.IsChecked = StatsAttachments.IsChecked = StatsWeapons.IsChecked = StatsRank.IsChecked = MegaAll.IsChecked = true;
        _loadingUnlocks = false;
        SaveUnlocks();
    }

    private void SaveUnlocks()
    {
        var values = new List<(string, string, string)>
        {
            (ProgressionSection, "camos", StatsCamos.IsChecked == true ? "1" : "0"),
            (ProgressionSection, "attachments", StatsAttachments.IsChecked == true ? "1" : "0"),
            (ProgressionSection, "level_1000", StatsRank.IsChecked == true ? "1" : "0"),
            (ProgressionSection, "weapon_max", StatsWeapons.IsChecked == true ? "1" : "0"),
            ("Hacks", "gobblegum_hack", "0")
        };
        bool mega = MegaAll.IsChecked == true;
        string path = GamePathTextBox.Text.Trim();
        bool customFlavors = Directory.Exists(path) && (_flavors ?? []).Any(flavor => ResurgeConfig.Read(path, FlavorsSection, flavor.Key, -1) >= 0);
        values.Add((ProgressionSection, "all_mega", mega ? "999" : "-1"));
        values.Add((ProgressionSection, "enabled", mega || customFlavors ? "1" : "0"));
        if (!WriteGameConfig(values.ToArray()))
        {
            LoadProgression();
            return;
        }
        ShowUnlockSummary();
        UpdateFlavor();
        StatusText.Text = "Unlocks saved. They apply the next time you press Play.";
    }

    private void GumSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) UpdateFlavor();
    }

    private void UpdateFlavor()
    {
        if (GumGrid.SelectedItem is not Flavor flavor) return;
        GumArt.Text = flavor.Glyph;
        GumColor.Fill = flavor.Color;
        GumName.Text = flavor.Name;
        GumIcon.Source = flavor.Icon;
        GumFallback.Visibility = flavor.FallbackVisibility;
        string path = GamePathTextBox.Text.Trim();
        int amount = -1;
        bool inherits = false;
        if (Directory.Exists(path) && ResurgeConfig.Read(path, ProgressionSection, "enabled", 0) == 1)
        {
            amount = ResurgeConfig.Read(path, FlavorsSection, flavor.Key, -1);
            if (amount < 0)
            {
                amount = ResurgeConfig.Read(path, ProgressionSection, "all_mega", -1);
                inherits = amount >= 0;
            }
        }
        GumAmount.Text = amount >= 0 ? amount.ToString(CultureInfo.InvariantCulture) : "";
        GumStatus.Text = inherits ? "Using the ×999 preset"
            : amount >= 0 ? "Custom amount saved"
            : "Not set  ·  amount 0–999";
    }

    private int SelectedGumSlot()
    {
        int index = 0;
        foreach (var child in GumSlots.Children)
        {
            if (child is RadioButton { IsChecked: true }) return index;
            index++;
        }
        return 0;
    }

    private void SaveGum_Click(object sender, RoutedEventArgs e)
    {
        if (GumGrid.SelectedItem is not Flavor flavor) return;
        if (!int.TryParse(GumAmount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int quantity) ||
            quantity is < 0 or > 999)
        {
            StatusText.Text = "Enter a whole amount from 0 to 999.";
            return;
        }
        SaveGumProfile((FlavorsSection, flavor.Key, quantity.ToString(CultureInfo.InvariantCulture)));
    }

    private void ResetGum_Click(object sender, RoutedEventArgs e)
    {
        if (GumGrid.SelectedItem is Flavor flavor)
            SaveGumProfile((FlavorsSection, flavor.Key, "-1"));
    }

    private void EquipGum_Click(object sender, RoutedEventArgs e)
    {
        if (GumGrid.SelectedItem is not Flavor flavor) return;
        if (!int.TryParse(GumAmount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int amount) ||
            amount is < 1 or > 999)
        {
            StatusText.Text = "Set an amount of 1 to 999 before equipping the flavor.";
            return;
        }
        int selected = SelectedGumSlot() + 1;
        var values = new List<(string, string, string)>
        {
            (FlavorsSection, flavor.Key, amount.ToString(CultureInfo.InvariantCulture))
        };
        string path = GamePathTextBox.Text.Trim();
        for (int slot = 1; slot <= 5; slot++)
        {
            if (slot == selected)
                values.Add(("WhiteoutPack", $"slot{slot}", flavor.Index.ToString(CultureInfo.InvariantCulture)));
            else if (Directory.Exists(path) && ResurgeConfig.Read(path, "WhiteoutPack", $"slot{slot}", -1) == flavor.Index)
                values.Add(("WhiteoutPack", $"slot{slot}", "-1"));
        }
        SaveGumProfile(values.ToArray());
    }

    private void ClearGums_Click(object sender, RoutedEventArgs e)
    {
        var values = Enumerable.Range(1, 5).Select(slot => ("WhiteoutPack", $"slot{slot}", "-1")).ToList();
        values.AddRange((_flavors ?? []).Select(flavor => (FlavorsSection, flavor.Key, "-1")));
        values.Add((ProgressionSection, "all_mega", "-1"));
        values.Add((ProgressionSection, "enabled", "0"));
        if (!WriteGameConfig(values.ToArray())) return;
        LoadProgression();
        StatusText.Text = "GobbleGum preset cleared.";
    }

    private void SaveGumProfile(params (string Section, string Key, string Value)[] entries)
    {
        var values = entries.ToList();
        values.Add((ProgressionSection, "enabled", "1"));
        values.Add(("Hacks", "gobblegum_hack", "0"));
        if (!WriteGameConfig(values.ToArray())) return;
        UpdateFlavor();
        StatusText.Text = "GobbleGum saved for the next session.";
    }
}
