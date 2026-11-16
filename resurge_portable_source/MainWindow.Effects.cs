using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class MainWindow
{
    private bool _loadingBackground;

    // Animations run whenever the PC renders with the graphics card. Windows' "animate controls inside
    // windows" switch is not used: many players turn it off for games and then saw no motion at all.
    private static bool Motion => (RenderCapability.Tier >> 16) > 0;

    /// <summary>Fades and lifts an element in after an optional delay.</summary>
    private static void RiseIn(UIElement element, double delayMs, double distance = 14, double durationMs = 380)
    {
        if (!Motion) return;
        var shift = element.RenderTransform as TranslateTransform;
        if (shift is null || shift.IsFrozen)
        {
            shift = new TranslateTransform();
            element.RenderTransform = shift;
        }
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, duration) { BeginTime = begin, EasingFunction = ease });
    }

    private static void Stagger(Panel panel, double startMs, double stepMs)
    {
        int index = 0;
        foreach (UIElement child in panel.Children)
            RiseIn(child, startMs + stepMs * index++);
    }

    /// <summary>Entrance for the Play page: headline, play row, then the cards one by one.</summary>
    private void PlayEntrance()
    {
        if (!Motion) return;
        RiseIn(WelcomeText, 60, 18, 460);
        RiseIn(PlayRow, 160, 16, 460);
        Stagger(ModeCards, 260, 70);
        Stagger(StatCards, 430, 50);
    }

    private void AnimatePageChange()
    {
        if (!Motion || Navigation.Template.FindName("PART_SelectedContentHost", Navigation) is not ContentPresenter host) return;
        RiseIn(host, 0, 12, 300);
        if (Navigation.SelectedItem == PlayTab) PlayEntrance();
    }

    /// <summary>Slow drift on the background art; stops while the game runs or when motion is off.</summary>
    private void UpdateArtMotion()
    {
        bool run = Motion && FxOff?.IsChecked != true && !_sessionActive && WindowState != WindowState.Minimized;
        if (!run)
        {
            ArtZoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ArtZoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }
        var drift = new DoubleAnimation(1, 1.07, TimeSpan.FromSeconds(38))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        ArtZoom.BeginAnimation(ScaleTransform.ScaleXProperty, drift);
        ArtZoom.BeginAnimation(ScaleTransform.ScaleYProperty, drift);
    }

    private async Task ApplyBackgroundAsync()
    {
        string mode = BackgroundArt.Mode;
        _loadingBackground = true;
        (mode == BackgroundArt.Whiteout ? BgWhiteout : mode == "custom" ? BgCustom : BgBlackOps).IsChecked = true;
        _loadingBackground = false;

        ImageSource? art = null;
        try { art = await BackgroundArt.LoadAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

        bool keyArt = mode != BackgroundArt.Whiteout && mode != "custom" && art is not null;
        BackgroundBrush.ImageSource = art ?? new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Whiteout-Ridge.png"));
        BackgroundBrush.AlignmentY = keyArt ? AlignmentY.Top : AlignmentY.Center;
        BackgroundBrush.AlignmentX = keyArt ? AlignmentX.Center : AlignmentX.Right;
        // The key art is wide and centred on the soldier, so it sits in the right part of the window.
        Grid.SetColumn(BackgroundImage, keyArt ? 1 : 0);
        Grid.SetColumnSpan(BackgroundImage, keyArt ? 1 : 2);
        if (Motion) BackgroundImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)));
        UpdateArtMotion();
    }

    private async void Background_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _loadingBackground) return;
        try
        {
            if (BgCustom.IsChecked == true)
            {
                if (BackgroundArt.Mode != "custom") ChooseBackground_Click(sender, e);
                return;
            }
            BackgroundArt.Mode = BgWhiteout.IsChecked == true ? BackgroundArt.Whiteout : BackgroundArt.BlackOps;
            await ApplyBackgroundAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a background picture",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dialog.ShowDialog(this) != true)
        {
            await ApplyBackgroundAsync();
            return;
        }
        try
        {
            BackgroundArt.SetCustom(dialog.FileName);
            await ApplyBackgroundAsync();
            StatusText.Text = "Background updated.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            await ApplyBackgroundAsync();
        }
    }
}
