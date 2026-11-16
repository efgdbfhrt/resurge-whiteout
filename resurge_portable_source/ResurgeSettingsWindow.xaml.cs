using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class ResurgeSettingsWindow : Window
{
    private readonly string _gamePath;
    private readonly (ComboBox Control, string Section, string Key, int Default)[] _choices;
    private readonly (TextBox Control, string Key, int Default, int Max)[] _counts;

    public ResurgeSettingsWindow(string gamePath)
    {
        InitializeComponent();
        _gamePath = gamePath;
        _choices =
        [
            (ForceXboxLiveCombo, "System", "force_xbox_live", 1),
            (ForceDisableSteamCombo, "System", "force_disable_steam", 2),
            (EnableConsoleCombo, "System", "enable_console", 0),
            (UnlockAllCombo, "DLC", "enable_unlock_all", 1),
            (SpoofDlcCombo, "DLC", "spoof_dlc_mappacks", 1),
            (SecurityPrefixCombo, "Security", "security_prefix", 1),
            (SecurityHooksCombo, "Security", "security_hooks", 1),
            (GobblegumHackCombo, "Hacks", "gobblegum_hack", 0),
            (CryptokeysHackCombo, "Hacks", "cryptokeys_hack", 0),
            (LiquidDiviniumHackCombo, "Hacks", "liquid_divinium_hack", 0),
            (ControllerIconsCombo, "Controller", "controller_icons", 1),
            (AutoCleanupCombo, "Launcher", "auto_cleanup", 1)
        ];
        _counts =
        [
            (GobblegumValueText, "gobblegum_value", 999, 999),
            (CryptokeysValueText, "cryptokeys_value", 99999, 99999),
            (LiquidDiviniumValueText, "liquid_divinium_value", 99999, 99999)
        ];

        foreach (var (control, section, key, fallback) in _choices)
        {
            string value = ResurgeConfig.Read(gamePath, section, key, fallback).ToString(CultureInfo.InvariantCulture);
            control.SelectedItem = control.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == value);
            if (control.SelectedIndex < 0) control.SelectedIndex = 0;
        }
        foreach (var (control, key, fallback, _) in _counts)
            control.Text = ResurgeConfig.Read(gamePath, "Hacks", key, fallback).ToString(CultureInfo.InvariantCulture);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var changes = new List<(string Section, string Key, string Value)>();
        foreach (var (control, section, key, _) in _choices)
            changes.Add((section, key, (string)((ComboBoxItem)control.SelectedItem).Tag));

        foreach (var (control, key, _, max) in _counts)
        {
            if (!int.TryParse(control.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value > max)
            {
                MessageBox.Show(this, $"{key} must be a whole number from 0 to {max}.", "Invalid value",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                control.Focus();
                return;
            }
            changes.Add(("Hacks", key, value.ToString(CultureInfo.InvariantCulture)));
        }

        try
        {
            using var folderLock = GameLauncher.LockFolder(_gamePath);
            if (ResurgeConfig.HasDiagnosticBackup(_gamePath))
                throw new IOException("Restore the diagnostic backup before saving new settings.");

            ResurgeConfig.Write(_gamePath, changes.ToArray());
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
