using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class MainWindow
{
    private sealed record ControllerRow(string Key, string Label, string ButtonLabel, Brush ChipBrush, Brush ChipInk);

    private ControllerBindings.Layout _layout = new();
    private string? _picking;
    private HashSet<string> _heldAtPickStart = [];
    private bool _loadingController;
    private readonly DispatcherTimer _padTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private int _padTicks;

    private void LoadController()
    {
        _layout = ControllerBindings.Load();
        _loadingController = true;
        SouthpawToggle.IsChecked = _layout.Southpaw;
        _loadingController = false;
        if (PresetButtons.Children.Count == 0)
        {
            foreach (var (name, _) in ControllerBindings.Presets)
            {
                var option = new RadioButton
                {
                    Content = name, GroupName = "ControllerPreset", Tag = name,
                    Style = (Style)FindResource("Segment")
                };
                option.Click += Preset_Click;
                PresetButtons.Children.Add(option);
            }
            foreach (var (button, label) in ControllerBindings.Buttons)
            {
                var pick = new Button { Content = label, Tag = button, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(12, 6, 12, 6) };
                pick.Click += PickButton_Click;
                PickerButtons.Children.Add(pick);
            }
            _padTimer.Tick += PadTimer_Tick;
        }
        ShowControllerRows();
    }

    /// <summary>Called when the Controller page opens.</summary>
    private void ShowController()
    {
        ShowControllerRows();
        UpdatePadStatus();
        _padTimer.Start();
    }

    private void ShowControllerRows()
    {
        ControllerRows.ItemsSource = ControllerBindings.Actions.Select(action =>
        {
            bool picking = action.Key == _picking;
            string button = _layout.Binds[action.Key];
            return new ControllerRow(action.Key, action.Label,
                picking ? "Press…" : ControllerBindings.Label(button),
                picking ? Res("Accent") : new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
                picking ? Res("AccentInk") : Res("Ink"));
        }).ToArray();

        // Highlight the preset that matches the current layout, if any.
        foreach (RadioButton option in PresetButtons.Children)
        {
            var preset = new ControllerBindings.Layout();
            ControllerBindings.Presets.First(entry => entry.Name == (string)option.Tag).Apply(preset);
            option.IsChecked = preset.Binds.All(pair => _layout.Binds.GetValueOrDefault(pair.Key) == pair.Value);
        }
    }

    private void UpdatePadStatus()
    {
        bool playstation = DualSenseReader.Connected;
        bool connected = playstation || XInputReader.AnyConnected();
        PadDot.Fill = connected ? Res("Good") : Res("Faint");
        PadStatus.Text = playstation ? "PS5 controller connected" : connected ? "Controller connected" : "No controller found";
        if (ControllerBindings.PlayStationNames != playstation)
        {
            ControllerBindings.PlayStationNames = playstation;
            foreach (Button pick in PickerButtons.Children)
                pick.Content = ControllerBindings.Label((string)pick.Tag);
            ShowControllerRows();
        }
    }

    private void PadTimer_Tick(object? sender, EventArgs e)
    {
        if (Navigation.SelectedItem != ControllerTab || !IsActive)
        {
            if (Navigation.SelectedItem != ControllerTab) _padTimer.Stop();
            return;
        }
        if (++_padTicks % 25 == 0) UpdatePadStatus();
        if (_picking is null) return;

        var pressed = XInputReader.Pressed();
        // Ignore buttons that were already held when picking started, until they are released.
        _heldAtPickStart.IntersectWith(pressed);
        pressed.ExceptWith(_heldAtPickStart);
        if (pressed.Count == 0) return;
        string button = ControllerBindings.Buttons.Select(entry => entry.Button).First(pressed.Contains);
        AssignButton(button);
    }

    private void ControllerAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        _picking = key;
        _heldAtPickStart = XInputReader.Pressed();
        string label = ControllerBindings.Actions.First(action => action.Key == key).Label;
        PickerTitle.Text = XInputReader.AnyConnected()
            ? $"{label}: press a button on your controller, or pick one below."
            : $"{label}: pick a button below.";
        PickerPanel.Visibility = Visibility.Visible;
        ControllerStatus.Text = "";
        ShowControllerRows();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PickerPanel.BringIntoView());
        _padTimer.Start();
    }

    private void PickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string button }) AssignButton(button);
    }

    private void CancelPick_Click(object sender, RoutedEventArgs e)
    {
        _picking = null;
        PickerPanel.Visibility = Visibility.Collapsed;
        ShowControllerRows();
    }

    private void AssignButton(string button)
    {
        if (_picking is not { } action) return;
        string label = ControllerBindings.Actions.First(entry => entry.Key == action).Label;
        string? swapped = ControllerBindings.Actions
            .FirstOrDefault(entry => entry.Key != action && _layout.Binds[entry.Key] == button)?.Label;
        ControllerBindings.Set(_layout, action, button);
        _picking = null;
        PickerPanel.Visibility = Visibility.Collapsed;
        SaveController(swapped is null
            ? $"{label} is now {ControllerBindings.Label(button)}."
            : $"{label} is now {ControllerBindings.Label(button)}; {swapped} moved to {ControllerBindings.Label(_layout.Binds[ControllerBindings.Actions.First(entry => entry.Label == swapped).Key])}.");
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string name }) return;
        var layout = new ControllerBindings.Layout { Southpaw = _layout.Southpaw };
        ControllerBindings.Presets.First(entry => entry.Name == name).Apply(layout);
        _layout = layout;
        _picking = null;
        PickerPanel.Visibility = Visibility.Collapsed;
        SaveController($"{name} layout selected.");
    }

    private void Southpaw_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingController || !_loaded) return;
        _layout.Southpaw = SouthpawToggle.IsChecked == true;
        SaveController(_layout.Southpaw ? "Southpaw sticks on." : "Southpaw sticks off.");
    }

    private void ResetController_Click(object sender, RoutedEventArgs e)
    {
        _layout = new ControllerBindings.Layout();
        _loadingController = true;
        SouthpawToggle.IsChecked = false;
        _loadingController = false;
        _picking = null;
        PickerPanel.Visibility = Visibility.Collapsed;
        SaveController("Controller reset to the default layout.");
    }

    private void SaveController(string message)
    {
        try
        {
            string folder = GamePathTextBox.Text.Trim();
            // While the game runs it owns its binding files; the layout is applied at the next launch instead.
            ControllerBindings.Save(_layout, !_sessionActive && Directory.Exists(folder) ? folder : null);
            ControllerStatus.Text = _sessionActive
                ? message + " Saved. It applies the next time you press Play."
                : message + " Saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ControllerStatus.Text = $"The layout could not be saved. {ex.Message}";
        }
        ShowControllerRows();
    }
}
