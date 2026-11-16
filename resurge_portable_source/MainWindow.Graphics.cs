using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class MainWindow
{
    private static readonly (string Label, string Value)[] FpsChoices =
        [("60", "60"), ("120", "120"), ("144", "144"), ("165", "165"), ("240", "240"), ("Unlimited", "0")];

    private bool _loadingGraphics;
    private readonly Dictionary<string, string> _sliderChanges = [];
    private DispatcherTimer? _sliderSave;

    private string? GraphicsFolder()
    {
        string path = GamePathTextBox.Text.Trim();
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>Called when the Graphics page opens.</summary>
    private void ShowGraphics()
    {
        if (FpsButtons.Children.Count == 0)
        {
            foreach (var (label, value) in FpsChoices)
            {
                var option = new RadioButton { Content = label, GroupName = "FpsLimit", Tag = value, Style = (Style)FindResource("Segment") };
                option.Checked += FpsLimit_Changed;
                FpsButtons.Children.Add(option);
            }
            string? card = GraphicsSettings.GraphicsCard();
            GpuText.Text = card ?? "Graphics card not detected";
            RtxTip.Visibility = card?.Contains("RTX", StringComparison.OrdinalIgnoreCase) == true ? Visibility.Visible : Visibility.Collapsed;
        }

        var values = GraphicsSettings.Read(GraphicsFolder());
        _loadingGraphics = true;
        try
        {
            string fps = values.GetValueOrDefault("MaxFPS", "0");
            bool known = false;
            foreach (RadioButton option in FpsButtons.Children)
            {
                bool match = (string)option.Tag == fps;
                option.IsChecked = match;
                known |= match;
            }
            FpsHint.Text = known || fps.Length == 0
                ? "Higher is smoother; Unlimited gives the most frames."
                : $"Currently {fps} (set in game). Higher is smoother; Unlimited gives the most frames.";
            ResolutionSlider.Value = Math.Clamp(GraphicsSettings.Int(values, "ResolutionPercent", 100), 50, 150);
            FovSlider.Value = Math.Clamp(GraphicsSettings.Int(values, "FOV", 80), 65, 120);
            ResolutionValue.Text = $"{ResolutionSlider.Value:0}%";
            FovValue.Text = $"{FovSlider.Value:0}";
            (values.GetValueOrDefault("FullScreenMode", "2") switch { "1" => ModeFullscreen, "0" => ModeWindowed, _ => ModeBorderless }).IsChecked = true;
            VsyncToggle.IsChecked = values.GetValueOrDefault("Vsync", "0") == "1";
            ShowFpsToggle.IsChecked = values.GetValueOrDefault("DrawFPS", "0") == "1";
        }
        finally { _loadingGraphics = false; }
        ShowPresetState(values);
    }

    private void ShowPresetState(IReadOnlyDictionary<string, string> values)
    {
        var current = GraphicsSettings.Matching(values);
        PresetFpsChip.Visibility = current?.Key == "fps" ? Visibility.Visible : Visibility.Collapsed;
        PresetBalancedChip.Visibility = current?.Key == "balanced" ? Visibility.Visible : Visibility.Collapsed;
        PresetQualityChip.Visibility = current?.Key == "quality" ? Visibility.Visible : Visibility.Collapsed;
        PresetState.Text = current is null
            ? "Your own mix of settings. Pick a style to reset everything to it."
            : $"Using {current.Name}.";
    }

    private void SaveGraphics(IReadOnlyDictionary<string, string> changes, string message)
    {
        try
        {
            bool applied = GraphicsSettings.Save(changes, GraphicsFolder(), _sessionActive);
            GraphicsStatus.Text = applied
                ? $"{message} Saved."
                : $"{message} Saved; it applies the next time you press Play.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GraphicsStatus.Text = $"Couldn't save the graphics settings. {ex.Message}";
        }
        ShowPresetState(GraphicsSettings.Read(GraphicsFolder()));
    }

    private void GraphicsPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        var preset = GraphicsSettings.Presets.First(entry => entry.Key == key);
        var changes = new Dictionary<string, string>(preset.Values);
        // Max FPS also lifts the frame cap and turns V-Sync off, unless the player picked a cap already.
        if (key == "fps")
        {
            changes["Vsync"] = "0";
            _loadingGraphics = true;
            VsyncToggle.IsChecked = false;
            _loadingGraphics = false;
        }
        SaveGraphics(changes, $"{preset.Name} selected.");
    }

    private void FpsLimit_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingGraphics || sender is not RadioButton { Tag: string value } option) return;
        SaveGraphics(new Dictionary<string, string> { ["MaxFPS"] = value },
            value == "0" ? "FPS limit removed." : $"FPS limited to {option.Content}.");
    }

    private void GraphicsSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ResolutionValue is null || FovValue is null) return;
        ResolutionValue.Text = $"{ResolutionSlider.Value:0}%";
        FovValue.Text = $"{FovSlider.Value:0}";
        if (_loadingGraphics || !_loaded || !IsLoaded) return;
        if (sender == ResolutionSlider) _sliderChanges["ResolutionPercent"] = ResolutionSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        else _sliderChanges["FOV"] = FovSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        // Save once the slider stops moving instead of on every step.
        _sliderSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Background, (_, _) =>
        {
            _sliderSave!.Stop();
            var changes = new Dictionary<string, string>(_sliderChanges);
            _sliderChanges.Clear();
            SaveGraphics(changes, "Display updated.");
        }, Dispatcher);
        _sliderSave.Stop();
        _sliderSave.Start();
    }

    private void DisplayMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingGraphics || sender is not RadioButton { Tag: string mode } option) return;
        SaveGraphics(new Dictionary<string, string> { ["FullScreenMode"] = mode }, $"{option.Content} mode selected.");
    }

    private void GraphicsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingGraphics) return;
        if (sender == VsyncToggle)
            SaveGraphics(new Dictionary<string, string> { ["Vsync"] = VsyncToggle.IsChecked == true ? "1" : "0" },
                VsyncToggle.IsChecked == true ? "V-Sync on." : "V-Sync off.");
        else
            SaveGraphics(new Dictionary<string, string> { ["DrawFPS"] = ShowFpsToggle.IsChecked == true ? "1" : "0" },
                ShowFpsToggle.IsChecked == true ? "FPS counter on." : "FPS counter off.");
    }
}
