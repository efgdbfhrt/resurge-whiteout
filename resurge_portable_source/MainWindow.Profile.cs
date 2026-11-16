using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class MainWindow
{
    private sealed record ProfileCardItem(PlayerProfile Profile, string Name, string Detail, string Initial, Brush? AvatarBrush);

    private PlayerProfile? _profile;
    private PlayerProfile? _pendingProfile;
    private string? _pendingAvatar;

    // The profile card lives in the sidebar template.
    private T? SidebarPart<T>(string name) where T : class
    {
        Navigation.ApplyTemplate();
        return Navigation.Template.FindName(name, Navigation) as T;
    }

    private static string PlayTime(long seconds) => seconds < 60 ? $"{seconds}s"
        : seconds < 3600 ? $"{seconds / 60}m"
        : $"{seconds / 3600}h {seconds % 3600 / 60}m";

    private static Brush? AvatarBrush(PlayerProfile profile)
    {
        try
        {
            return PlayerProfiles.LoadAvatar(profile) is { } image
                ? new ImageBrush(image) { Stretch = Stretch.UniformToFill } : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Opens the remembered profile, or shows the sign-in screen.</summary>
    private void StartSignIn()
    {
        // "Sign me in automatically" skips the screen, password or not: the player chose it on this PC.
        if (PlayerProfiles.RememberedProfile() is { } remembered)
            CompleteSignIn(remembered, animate: false);
        else
            ShowLogin();
    }

    private void ShowLogin()
    {
        var profiles = PlayerProfiles.List();
        ProfileList.ItemsSource = profiles.Select(profile => new ProfileCardItem(profile, profile.Name,
            PlayerProfiles.HasPassword(profile) ? $"🔒  {PlayTime(profile.Seconds)}" : PlayTime(profile.Seconds) + " played",
            profile.Name[..1].ToUpperInvariant(), AvatarBrush(profile))).ToArray();
        LoginError.Visibility = Visibility.Collapsed;
        LoginPassword.Clear();
        ShowLoginPanel(profiles.Length == 0 ? CreatePanel : PickPanel);
        if (profiles.Length == 0)
            NewProfileName.Text = new string(Environment.UserName.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').Take(24).ToArray());

        LoginOverlay.Visibility = Visibility.Visible;
        LoginOverlay.IsHitTestVisible = true;
        if (!Motion) { LoginOverlay.Opacity = 1; return; }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        LoginOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        LoginScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.94, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        LoginScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.94, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
    }

    private void ShowLoginPanel(FrameworkElement panel)
    {
        foreach (var candidate in new FrameworkElement[] { PickPanel, PasswordPanel, CreatePanel })
            candidate.Visibility = candidate == panel ? Visibility.Visible : Visibility.Collapsed;
        LoginError.Visibility = Visibility.Collapsed;
        if (Motion)
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        if (panel == PasswordPanel) Dispatcher.BeginInvoke(() => LoginPassword.Focus());
        if (panel == CreatePanel) Dispatcher.BeginInvoke(() => NewProfileName.Focus());
    }

    private void ShowLoginError(string message)
    {
        LoginError.Text = message;
        LoginError.Visibility = Visibility.Visible;
        if (!Motion) return;
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        foreach (var (offset, ms) in new[] { (-8.0, 50), (8.0, 110), (-5.0, 170), (5.0, 230), (0.0, 320) })
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(offset, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
        LoginShift.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    private void ProfileCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileCardItem item }) return;
        if (!PlayerProfiles.HasPassword(item.Profile))
        {
            CompleteSignIn(item.Profile, animate: true);
            return;
        }
        _pendingProfile = item.Profile;
        PasswordTitle.Text = $"Welcome back, {item.Name}";
        LoginPassword.Clear();
        ShowLoginPanel(PasswordPanel);
    }

    private void LoginPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PasswordSignIn_Click(sender, e);
    }

    private void PasswordSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingProfile is not { } pending) return;
        try { CompleteSignIn(PlayerProfiles.SignIn(pending.Name, LoginPassword.Password), animate: true); }
        catch (ArgumentException ex)
        {
            LoginPassword.Clear();
            ShowLoginError(ex.Message);
        }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        NewProfileName.Text = "";
        NewProfilePassword.Clear();
        _pendingAvatar = null;
        NewAvatarPreview.Fill = null;
        ShowLoginPanel(CreatePanel);
    }

    private void NewAvatar_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a profile picture", Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 200;
            image.UriSource = new Uri(dialog.FileName);
            image.EndInit();
            NewAvatarPreview.Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            _pendingAvatar = dialog.FileName;
        }
        catch (Exception ex) { ShowLoginError($"That picture could not be opened. {ex.Message}"); }
    }

    private void CreateProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = PlayerProfiles.Create(NewProfileName.Text, NewProfilePassword.Password);
            if (_pendingAvatar is not null)
            {
                try { PlayerProfiles.SaveAvatar(profile, _pendingAvatar); }
                catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or NotSupportedException) { }
            }
            NewProfilePassword.Clear();
            CompleteSignIn(profile, animate: true);
        }
        catch (ArgumentException ex) { ShowLoginError(ex.Message); }
        catch (System.IO.IOException ex) { ShowLoginError(ex.Message); }
    }

    private void LoginBack_Click(object sender, RoutedEventArgs e)
    {
        LoginPassword.Clear();
        NewProfilePassword.Clear();
        if (PlayerProfiles.List().Length > 0) ShowLoginPanel(PickPanel);
    }

    private void CompleteSignIn(PlayerProfile profile, bool animate)
    {
        _profile = profile;
        _pendingProfile = null;
        PlayerProfiles.SetAutoSignIn(profile, RememberMe.IsChecked == true || !animate);
        ShowProfile();
        ShowAvatar();
        StatusText.Text = $"Signed in as {profile.Name}.";
        _ = ConnectFriendsAsync();
        if (LoginOverlay.Visibility != Visibility.Visible) return;
        LoginOverlay.IsHitTestVisible = false;
        if (!animate || !Motion)
        {
            LoginOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => LoginOverlay.Visibility = Visibility.Collapsed;
        LoginOverlay.BeginAnimation(OpacityProperty, fade);
        LoginScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.04, TimeSpan.FromMilliseconds(320)));
        LoginScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.04, TimeSpan.FromMilliseconds(320)));
        PlayEntrance();
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionActive || WorkshopPanel.IsBusy)
        {
            StatusText.Text = "Close the game and finish downloads before signing out.";
            return;
        }
        PlayerProfiles.SignOut();
        await DisconnectFriendsAsync();
        _profile = null;
        ShowLogin();
    }

    private void ShowAvatar()
    {
        if (_profile is not { } profile || SidebarPart<Ellipse>("BadgeAvatar") is not { } avatar) return;
        avatar.Fill = AvatarBrush(profile);
    }

    private void ShowProfile()
    {
        if (_profile is not { } profile) return;
        string playTime = PlayTime(profile.Seconds);
        if (SidebarPart<TextBlock>("BadgeName") is { } name) name.Text = profile.Name;
        if (SidebarPart<TextBlock>("BadgeDetail") is { } detail) detail.Text = $"{playTime} played";
        ProfilePlayTime.Text = playTime;
        ProfileBestRound.Text = profile.BestRound?.ToString() ?? "—";
        WelcomeText.Text = $"Ready up, {profile.Name}.";
        ShowMastery();
    }

    private void ChangeAvatar_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is not { } profile) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose a profile picture",
            Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
        };
        if (dialog.ShowDialog(this) != true) return;
        TryRun(() =>
        {
            PlayerProfiles.SaveAvatar(profile, dialog.FileName);
            ShowAvatar();
            _ = UploadAvatarAsync();
        });
    }
}
