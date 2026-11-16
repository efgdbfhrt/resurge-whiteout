using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace ResurgeLauncherCSharp;

public enum BackdropEffect { Embers, Snow, Off }

/// <summary>
/// Animated layer over the background art: glowing embers rising (the default, matching the Black Ops III
/// key art) or falling snow. Runs on WPF's render loop and does not depend on Windows' "animate controls"
/// setting, so every player sees it; it pauses while the game runs or the window is minimised.
/// </summary>
public sealed class WhiteoutBackdrop : FrameworkElement
{
    private readonly record struct Particle(double X, double Y, double Speed, double Radius, int Shade, double Phase, double Sway);

    private readonly Particle[] _snow;
    private readonly Particle[] _embers;
    private readonly Stopwatch _clock = new();
    private bool _animated = true;
    private BackdropEffect _effect = BackdropEffect.Embers;
    private bool _rendering;

    private static readonly Brush[] SnowBrushes = [Paint(87, 192, 213, 235), Paint(143, 206, 222, 238), Paint(198, 228, 239, 250)];
    private static readonly Brush[] EmberCores = [Paint(255, 255, 122, 47), Paint(255, 255, 160, 70), Paint(255, 255, 214, 140)];
    private static readonly Brush[] EmberGlows = [Glow(255, 122, 47), Glow(255, 150, 60), Glow(255, 196, 110)];

    public WhiteoutBackdrop()
    {
        var random = new Random(709);
        _snow = Enumerable.Range(0, 192).Select(i =>
        {
            int depth = i % SnowBrushes.Length;
            return new Particle(random.NextDouble(), random.NextDouble(),
                24 + depth * 22 + random.NextDouble() * 18, .55 + depth * .45 + random.NextDouble() * .45, depth, 0, 0);
        }).ToArray();
        _embers = Enumerable.Range(0, 150).Select(i =>
        {
            int depth = i % EmberCores.Length;
            return new Particle(random.NextDouble(), random.NextDouble(),
                22 + depth * 16 + random.NextDouble() * 26, 1.1 + depth * .6 + random.NextDouble() * .9, depth,
                random.NextDouble() * Math.PI * 2, 8 + random.NextDouble() * 26);
        }).ToArray();

        Loaded += (_, _) => UpdateTimer();
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => Stop();
    }

    public bool Animated
    {
        get => _animated;
        set
        {
            if (_animated == value) return;
            _animated = value;
            UpdateTimer();
            InvalidateVisual();
        }
    }

    public BackdropEffect Effect
    {
        get => _effect;
        set
        {
            if (_effect == value) return;
            _effect = value;
            UpdateTimer();
            InvalidateVisual();
        }
    }

    private void UpdateTimer()
    {
        if (!_animated || _effect == BackdropEffect.Off || !IsLoaded || !IsVisible)
        {
            Stop();
            return;
        }
        if (_rendering) return;
        _rendering = true;
        _clock.Start();
        CompositionTarget.Rendering += RenderFrame;
    }

    private void Stop()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= RenderFrame;
        _clock.Stop();
    }

    private void RenderFrame(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawing)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (!_animated || width <= 0 || height <= 0) return;
        double time = _clock.Elapsed.TotalSeconds;
        if (_effect == BackdropEffect.Snow) DrawSnow(drawing, width, height, time);
        else if (_effect == BackdropEffect.Embers) DrawEmbers(drawing, width, height, time);
    }

    private void DrawSnow(DrawingContext drawing, double width, double height, double time)
    {
        foreach (var flake in _snow)
        {
            double x = (flake.X * width + time * flake.Speed * .24) % width;
            double y = (flake.Y * height + time * flake.Speed) % height;
            drawing.DrawEllipse(SnowBrushes[flake.Shade], null, new Point(x, y), flake.Radius, flake.Radius);
        }
    }

    private void DrawEmbers(DrawingContext drawing, double width, double height, double time)
    {
        double travel = height + 60;
        foreach (var ember in _embers)
        {
            // Rise from below the window, sway sideways and burn out towards the top.
            double rise = (ember.Y * travel + time * ember.Speed) % travel;
            double y = height + 30 - rise;
            double x = ember.X * width + Math.Sin(time * .8 + ember.Phase) * ember.Sway + rise * .06;
            if (x < -10 || x > width + 10) continue;
            double life = rise / travel;
            double flicker = .65 + .35 * Math.Sin(time * 7.3 + ember.Phase * 3);
            // Embers burn bright through most of the climb and fade out in the top third.
            double opacity = Math.Clamp((1 - life) * 1.8, 0, 1) * Math.Clamp(life * 8, 0, 1) * flicker;
            if (opacity <= .02) continue;
            var center = new Point(x, y);
            drawing.PushOpacity(opacity);
            drawing.DrawEllipse(EmberGlows[ember.Shade], null, center, ember.Radius * 4.5, ember.Radius * 4.5);
            drawing.DrawEllipse(EmberCores[ember.Shade], null, center, ember.Radius, ember.Radius);
            drawing.Pop();
        }
    }

    private static Brush Paint(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Brush Glow(byte red, byte green, byte blue)
    {
        var brush = new RadialGradientBrush(Color.FromArgb(130, red, green, blue), Color.FromArgb(0, red, green, blue));
        brush.Freeze();
        return brush;
    }
}
