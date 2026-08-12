using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Productization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DesktopAgent.WinUI;

public sealed partial class DesktopRadarView : UserControl
{
    private const double CanvasInset = 16d;
    private DesktopRadarOverviewState? _state;

    public DesktopRadarView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Render();
    }

    internal void SetState(DesktopRadarOverviewState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        Render();
    }

    private void RadarRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        => Render();

    private void Render()
    {
        RadarCanvas.Children.Clear();

        var snapshot = _state?.Snapshot;
        if (snapshot is null || ActualWidth < 80d || ActualHeight < 80d)
        {
            RadarEmptyState.Visibility = Visibility.Visible;
            return;
        }

        RadarEmptyState.Visibility = Visibility.Collapsed;
        var displayLayouts = CalculateDisplayLayouts(snapshot);

        foreach (var item in displayLayouts)
            DrawDisplaySurface(item, snapshot.Displays.Count > 1);

        foreach (var item in displayLayouts)
            DrawDensityMarks(item);

        DrawZones(displayLayouts);
    }

    private IReadOnlyList<RenderedDisplay> CalculateDisplayLayouts(DesktopRadarSnapshot snapshot)
    {
        var left = snapshot.Displays.Min(display => display.Bounds.Left);
        var top = snapshot.Displays.Min(display => display.Bounds.Top);
        var right = snapshot.Displays.Max(display => display.Bounds.Right);
        var bottom = snapshot.Displays.Max(display => display.Bounds.Bottom);
        var virtualWidth = Math.Max(1d, right - left);
        var virtualHeight = Math.Max(1d, bottom - top);
        var availableWidth = Math.Max(1d, ActualWidth - (CanvasInset * 2d));
        var availableHeight = Math.Max(1d, ActualHeight - (CanvasInset * 2d));
        var scale = Math.Min(availableWidth / virtualWidth, availableHeight / virtualHeight);
        var renderedWidth = virtualWidth * scale;
        var renderedHeight = virtualHeight * scale;
        var originX = (ActualWidth - renderedWidth) / 2d;
        var originY = (ActualHeight - renderedHeight) / 2d;

        return snapshot.Displays
            .Select(display => new RenderedDisplay(
                display,
                originX + ((display.Bounds.Left - left) * scale),
                originY + ((display.Bounds.Top - top) * scale),
                display.Bounds.Width * scale,
                display.Bounds.Height * scale))
            .ToArray();
    }

    private void DrawDisplaySurface(RenderedDisplay rendered, bool showLabel)
    {
        var surface = new Border
        {
            Width = rendered.Width,
            Height = rendered.Height,
            CornerRadius = new CornerRadius(Math.Min(9d, Math.Max(3d, rendered.Height * 0.025d))),
            BorderThickness = new Thickness(1d),
            BorderBrush = NeutralBrush(ActualTheme == ElementTheme.Dark ? (byte)78 : (byte)56),
            Background = NeutralBrush(ActualTheme == ElementTheme.Dark ? (byte)15 : (byte)10),
        };
        Canvas.SetLeft(surface, rendered.Left);
        Canvas.SetTop(surface, rendered.Top);
        RadarCanvas.Children.Add(surface);

        if (!showLabel || rendered.Width < 92d || rendered.Height < 48d)
            return;

        var label = new TextBlock
        {
            Text = rendered.Display.IsPrimary ? "主显示器" : "显示器",
            FontSize = 11d,
            Opacity = 0.58d,
        };
        Canvas.SetLeft(label, rendered.Left + 8d);
        Canvas.SetTop(label, rendered.Top + 6d);
        RadarCanvas.Children.Add(label);
    }

    private void DrawDensityMarks(RenderedDisplay rendered)
    {
        var display = rendered.Display;
        if (display.DensityCells.Count == 0)
            return;

        var cellWidth = rendered.Width / display.GridColumns;
        var cellHeight = rendered.Height / display.GridRows;
        foreach (var cell in display.DensityCells)
        {
            var markSize = cell.Density switch
            {
                DesktopRadarDensityLevel.Low => 3.5d,
                DesktopRadarDensityLevel.Medium => 5.5d,
                DesktopRadarDensityLevel.High => 7.5d,
                _ => 4d,
            };
            var opacity = cell.Density switch
            {
                DesktopRadarDensityLevel.Low => 0.34d,
                DesktopRadarDensityLevel.Medium => 0.54d,
                DesktopRadarDensityLevel.High => 0.76d,
                _ => 0.4d,
            };

            markSize = Math.Min(markSize, Math.Max(2d, Math.Min(cellWidth, cellHeight) * 0.45d));
            var mark = new Border
            {
                Width = markSize,
                Height = markSize,
                CornerRadius = new CornerRadius(Math.Max(1d, markSize * 0.28d)),
                Background = NeutralBrush(255),
                Opacity = opacity,
            };
            var x = rendered.Left + ((cell.Column + 0.5d) * cellWidth) - (markSize / 2d);
            var y = rendered.Top + ((cell.Row + 0.5d) * cellHeight) - (markSize / 2d);
            Canvas.SetLeft(mark, x);
            Canvas.SetTop(mark, y);
            RadarCanvas.Children.Add(mark);
        }
    }

    private void DrawZones(IReadOnlyList<RenderedDisplay> displayLayouts)
    {
        var state = _state;
        var layout = state?.ActiveLayout;
        if (state is null || layout is null)
            return;

        var target = ResolveTargetDisplay(layout.TargetDisplayBinding, displayLayouts);
        if (target is null)
            return;

        foreach (var zone in layout.Zones.OrderBy(zone => zone.SortOrder))
        {
            var left = target.Left +
                (target.Width * (zone.Bounds.Left / (double)NormalizedRect.Scale));
            var top = target.Top +
                (target.Height * (zone.Bounds.Top / (double)NormalizedRect.Scale));
            var width = target.Width * (zone.Bounds.Width / (double)NormalizedRect.Scale);
            var height = target.Height * (zone.Bounds.Height / (double)NormalizedRect.Scale);
            if (width <= 1d || height <= 1d)
                continue;

            var rgb = state.Settings.OverlayZoneColors.Resolve(
                zone.ZoneId,
                state.Settings.OverlayAppearance.ZoneColorRgb);
            var zoneBorder = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(Math.Min(8d, Math.Max(2d, height * 0.035d))),
                BorderThickness = new Thickness(1d),
                BorderBrush = RgbBrush(rgb, 125),
                Background = RgbBrush(rgb, 14),
            };
            Canvas.SetLeft(zoneBorder, left);
            Canvas.SetTop(zoneBorder, top);
            RadarCanvas.Children.Add(zoneBorder);

            if (width < 68d || height < 30d)
                continue;

            var identityBar = new Border
            {
                Width = 3d,
                Height = 12d,
                CornerRadius = new CornerRadius(1.5d),
                Background = RgbBrush(rgb, 205),
            };
            Canvas.SetLeft(identityBar, left + 7d);
            Canvas.SetTop(identityBar, top + 7d);
            RadarCanvas.Children.Add(identityBar);

            var label = new TextBlock
            {
                Text = zone.Name,
                FontSize = 11d,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.72d,
                MaxWidth = Math.Max(24d, width - 24d),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Canvas.SetLeft(label, left + 14d);
            Canvas.SetTop(label, top + 5d);
            RadarCanvas.Children.Add(label);
        }
    }

    private static RenderedDisplay? ResolveTargetDisplay(
        TargetDisplayBinding binding,
        IReadOnlyList<RenderedDisplay> displayLayouts)
        => binding.Kind switch
        {
            TargetDisplayBindingKind.PrimaryDisplay =>
                displayLayouts.FirstOrDefault(item => item.Display.IsPrimary),
            TargetDisplayBindingKind.ExactDeviceName =>
                displayLayouts.FirstOrDefault(item => string.Equals(
                    item.Display.DisplayKey,
                    binding.DeviceName,
                    StringComparison.Ordinal)),
            // SelectedTarget 没有持久化具体设备身份，Overview 不能猜。
            TargetDisplayBindingKind.SelectedTarget => null,
            _ => null,
        };

    private SolidColorBrush NeutralBrush(byte alpha)
    {
        var baseValue = ActualTheme == ElementTheme.Dark ? (byte)255 : (byte)0;
        return new SolidColorBrush(Color.FromArgb(alpha, baseValue, baseValue, baseValue));
    }

    private static SolidColorBrush RgbBrush(uint rgb, byte alpha)
        => new(Color.FromArgb(
            alpha,
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF)));

    private sealed record RenderedDisplay(
        DesktopRadarDisplaySnapshot Display,
        double Left,
        double Top,
        double Width,
        double Height);
}
