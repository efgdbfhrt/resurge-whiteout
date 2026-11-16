using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ResurgeLauncherCSharp;

/// <summary>
/// A small pop-up in the bottom-right corner of the screen for new messages and friend requests.
/// It never takes focus (so it doesn't pull you out of the game) and hides itself after a few seconds.
/// One toast is reused: a new notification replaces the text and restarts the timer.
/// </summary>
internal sealed class NotificationToast : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private readonly TextBlock _title = new() { FontWeight = FontWeights.Bold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _body = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxHeight = 56, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _kind = new() { FontSize = 11, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _initial = new() { FontWeight = FontWeights.Bold, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _avatar = new();
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(6) };
    private Action? _open;

    public NotificationToast()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 340;
        Cursor = Cursors.Hand;
        Title = "Resurge Whiteout";

        var ink = (Brush)Application.Current.FindResource("Ink");
        var muted = (Brush)Application.Current.FindResource("Muted");
        var accent = (Brush)Application.Current.FindResource("Accent");
        _title.Foreground = ink;
        _body.Foreground = muted;
        _kind.Foreground = accent;
        _initial.Foreground = ink;
        _initial.FontFamily = (FontFamily)Application.Current.FindResource("Display");

        var picture = new Grid { Width = 44, Height = 44, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        picture.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x2A, 0x34, 0x42)) });
        picture.Children.Add(_initial);
        picture.Children.Add(_avatar);
        picture.Children.Add(new Ellipse { Stroke = accent, StrokeThickness = 2 });
        DockPanel.SetDock(picture, Dock.Left);

        var close = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = muted, Margin = new Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; HideNow(); };
        DockPanel.SetDock(close, Dock.Right);

        var text = new StackPanel();
        text.Children.Add(_kind);
        text.Children.Add(_title);
        text.Children.Add(_body);

        var row = new DockPanel();
        row.Children.Add(picture);
        row.Children.Add(close);
        row.Children.Add(text);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x17, 0x1F)),
            BorderBrush = (Brush)Application.Current.FindResource("LineStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 12, 14),
            Margin = new Thickness(12),
            Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = .55, Color = Colors.Black }
        };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate | WsExToolWindow | WsExTopmost);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            var open = _open;
            HideNow();
            open?.Invoke();
        };
        MouseEnter += (_, _) => _hide.Stop();
        MouseLeave += (_, _) => _hide.Start();
        _hide.Tick += (_, _) => HideNow();
        SizeChanged += (_, _) => Place();
    }

    public void Show(string kind, string title, string body, string initial, Brush? avatar, Action? open)
    {
        _kind.Text = kind.ToUpperInvariant();
        _title.Text = title;
        _body.Text = body;
        _body.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _initial.Text = initial;
        _avatar.Fill = avatar;
        _open = open;
        _hide.Stop();
        _hide.Start();
        bool wasVisible = IsVisible;
        if (!wasVisible)
        {
            Opacity = 0;
            Show();
        }
        Place();
        BeginAnimation(OpacityProperty, new DoubleAnimation(wasVisible ? Opacity : 0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 8;
        Top = area.Bottom - ActualHeight - 8;
    }

    private void HideNow()
    {
        _hide.Stop();
        _open = null;
        BeginAnimation(OpacityProperty, null);
        Hide();
    }
}
