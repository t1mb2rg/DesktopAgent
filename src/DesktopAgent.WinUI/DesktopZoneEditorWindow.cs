using System.Runtime.InteropServices;
using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Productization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace DesktopAgent.WinUI;

/// <summary>
/// 显式进入的 Desktop Zone 编辑表面。
///
/// 该窗口只编辑 LayoutDraft 的 Zone 几何/名称/显示颜色；它不读取、不选择、
/// 也不移动桌面图标，不持有任何 Writer authority。完成后由布局工作台走既有
/// Template + RuleSet 保存协调器持久化。
/// </summary>
internal sealed class DesktopZoneEditorWindow : Window
{
    private const int MinimumZonePixels = 56;
    private const byte WindowOpacity = 232;

    private readonly DisplayDescriptor _display;
    private readonly DesktopLayoutEditDraft _sourceDraft;
    private readonly DesktopProductSettings _settings;
    private readonly Canvas _canvas = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBox _zoneNameBox = new();
    private readonly Button _deleteButton = new();
    private readonly Button _colorButton = new();
    private readonly ColorPicker _colorPicker = new()
    {
        IsAlphaEnabled = false,
        ColorSpectrumShape = ColorSpectrumShape.Ring,
    };
    private readonly AppWindow _appWindow;

    private IReadOnlyList<DesktopLayoutZone> _zones;
    private readonly Dictionary<Guid, uint> _zoneColorOverrides;
    private Guid? _selectedZoneId;
    private DesktopLayoutZone? _dragOrigin;
    private Border? _activeZoneVisual;
    private Windows.Foundation.Point _dragStart;
    private uint? _activePointerId;
    private DragMode _dragMode;
    private bool _suppressInspectorEvents;
    private bool _resultRaised;
    private bool _configured;

    internal event EventHandler<DesktopZoneEditorCompletedEventArgs>? Completed;
    internal event EventHandler? Cancelled;

    internal DesktopZoneEditorWindow(
        DisplayDescriptor display,
        DesktopLayoutEditDraft draft,
        DesktopProductSettings settings,
        IReadOnlyDictionary<Guid, uint>? workingZoneColorOverrides = null)
    {
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _sourceDraft = draft ?? throw new ArgumentNullException(nameof(draft));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _zones = Array.AsReadOnly(draft.Zones.ToArray());
        _selectedZoneId = _zones.OrderBy(zone => zone.SortOrder).First().ZoneId;
        _zoneColorOverrides = workingZoneColorOverrides is null
            ? draft.Zones
                .Where(zone => settings.OverlayZoneColors.TryGetOverride(zone.ZoneId, out _))
                .ToDictionary(
                    zone => zone.ZoneId,
                    zone =>
                    {
                        _ = settings.OverlayZoneColors.TryGetOverride(zone.ZoneId, out var rgb);
                        return rgb;
                    })
            : workingZoneColorOverrides.ToDictionary(pair => pair.Key, pair => pair.Value);

        Title = "Tessel · 桌面分区编辑";
        Content = BuildContent();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId)
            ?? throw new InvalidOperationException("无法获取 Desktop Zone Editor AppWindow。 ");

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        _canvas.SizeChanged += (_, _) => RenderZones();
        _canvas.PointerMoved += Canvas_PointerMoved;
        _canvas.PointerReleased += Canvas_PointerReleased;
        _canvas.PointerCanceled += Canvas_PointerCanceled;
        Activated += DesktopZoneEditorWindow_Activated;
        Closed += DesktopZoneEditorWindow_Closed;

        RefreshInspector();
    }

    private UIElement BuildContent()
    {
        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0E, 0x10, 0x15)),
        };

        _canvas.Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
        root.Children.Add(_canvas);

        var title = new TextBlock
        {
            Text = "桌面分区编辑",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var subtitle = new TextBlock
        {
            Text = "拖动分区移动，拖右下角调整大小。这里只改空间，不会移动桌面图标。",
            FontSize = 11,
            Opacity = 0.60,
        };
        _statusText.Text = _display.DeviceName;
        _statusText.FontSize = 11;
        _statusText.Opacity = 0.58;

        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(title);
        heading.Children.Add(subtitle);
        heading.Children.Add(_statusText);

        _zoneNameBox.Width = 170;
        _zoneNameBox.PlaceholderText = "分区名称";
        _zoneNameBox.LostFocus += (_, _) => CommitSelectedZoneName();

        var newButton = new Button { Content = "＋ 新建分区" };
        newButton.Click += NewZone_Click;

        _deleteButton.Content = "删除";
        _deleteButton.Click += DeleteZone_Click;

        ConfigureColorFlyout();

        var cancelButton = new Button { Content = "取消" };
        cancelButton.Click += (_, _) => CloseAsCancelled();

        var doneButton = new Button
        {
            Content = "完成并保存",
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x31, 0x52, 0xE8)),
            Foreground = new SolidColorBrush(Colors.White),
        };
        doneButton.Click += Done_Click;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(_zoneNameBox);
        actions.Children.Add(_colorButton);
        actions.Children.Add(newButton);
        actions.Children.Add(_deleteButton);
        actions.Children.Add(cancelButton);
        actions.Children.Add(doneButton);

        var toolbarGrid = new Grid { ColumnSpacing = 22 };
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbarGrid.Children.Add(heading);
        Grid.SetColumn(actions, 1);
        toolbarGrid.Children.Add(actions);

        var toolbar = new Border
        {
            Margin = new Thickness(24),
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x1B, 0x1D, 0x23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x55, 0x5B, 0x6A)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = toolbarGrid,
        };
        root.Children.Add(toolbar);

        return root;
    }

    private void ConfigureColorFlyout()
    {
        _colorButton.Content = "颜色";

        _colorPicker.ColorChanged += ColorPicker_ColorChanged;
        var inheritButton = new Button
        {
            Content = "继承全局颜色",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        inheritButton.Click += (_, _) =>
        {
            if (_selectedZoneId is not Guid zoneId)
                return;

            _zoneColorOverrides.Remove(zoneId);
            RefreshInspector();
            RenderZones();
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_colorPicker);
        panel.Children.Add(inheritButton);
        _colorButton.Flyout = new Flyout { Content = panel };
    }

    private void DesktopZoneEditorWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_configured)
            return;

        _configured = true;
        _appWindow.MoveAndResize(new RectInt32(
            _display.WorkArea.Left,
            _display.WorkArea.Top,
            _display.WorkArea.Width,
            _display.WorkArea.Height));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        _ = SetWindowLongPtr(hwnd, GwlExStyle, exStyle | (nint)WsExLayered);
        _ = SetLayeredWindowAttributes(hwnd, 0, WindowOpacity, LwaAlpha);

        RenderZones();
    }

    private void DesktopZoneEditorWindow_Closed(object sender, WindowEventArgs args)
    {
        if (!_resultRaised)
        {
            _resultRaised = true;
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RenderZones()
    {
        _canvas.Children.Clear();
        if (_canvas.ActualWidth <= 1 || _canvas.ActualHeight <= 1)
            return;

        foreach (var zone in _zones.OrderBy(zone => zone.SortOrder))
        {
            var isSelected = zone.ZoneId == _selectedZoneId;
            var rgb = ResolveZoneColor(zone.ZoneId);
            var color = ToColor(rgb);

            var content = new Grid();
            var label = new TextBlock
            {
                Text = zone.Name,
                Margin = new Thickness(12, 9, 28, 8),
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            content.Children.Add(label);

            var grip = new Border
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 7, 7),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(0xE8, color.R, color.G, color.B)),
                Tag = zone.ZoneId,
            };
            grip.PointerPressed += ResizeGrip_PointerPressed;
            content.Children.Add(grip);

            var visual = new Border
            {
                Tag = zone.ZoneId,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(isSelected ? 2.5 : 1.5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(
                    isSelected ? (byte)0xFF : (byte)0xB8,
                    color.R,
                    color.G,
                    color.B)),
                Background = new SolidColorBrush(Color.FromArgb(
                    isSelected ? (byte)0x44 : (byte)0x28,
                    color.R,
                    color.G,
                    color.B)),
                Child = content,
            };
            visual.PointerPressed += Zone_PointerPressed;

            ApplyZoneVisualBounds(visual, zone);
            _canvas.Children.Add(visual);
        }
    }

    private void Zone_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border visual || visual.Tag is not Guid zoneId)
            return;
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed)
            return;

        BeginDrag(zoneId, visual, DragMode.Move, e);
    }

    private void ResizeGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border grip || grip.Tag is not Guid zoneId ||
            grip.Parent is not Grid grid || grid.Parent is not Border visual)
        {
            return;
        }
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed)
            return;

        BeginDrag(zoneId, visual, DragMode.ResizeBottomRight, e);
        e.Handled = true;
    }

    private void BeginDrag(
        Guid zoneId,
        Border visual,
        DragMode mode,
        PointerRoutedEventArgs e)
    {
        var zone = _zones.First(item => item.ZoneId == zoneId);
        _selectedZoneId = zoneId;
        _dragOrigin = zone;
        _activeZoneVisual = visual;
        _dragMode = mode;
        _dragStart = e.GetCurrentPoint(_canvas).Position;
        _activePointerId = e.Pointer.PointerId;
        _ = _canvas.CapturePointer(e.Pointer);
        RefreshInspector();

        visual.BorderThickness = new Thickness(2.5);
        e.Handled = true;
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId != e.Pointer.PointerId ||
            _dragOrigin is null || _activeZoneVisual is null ||
            _canvas.ActualWidth <= 1 || _canvas.ActualHeight <= 1)
        {
            return;
        }

        var current = e.GetCurrentPoint(_canvas).Position;
        var deltaX = (int)Math.Round(
            (current.X - _dragStart.X) / _canvas.ActualWidth * NormalizedRect.Scale,
            MidpointRounding.AwayFromZero);
        var deltaY = (int)Math.Round(
            (current.Y - _dragStart.Y) / _canvas.ActualHeight * NormalizedRect.Scale,
            MidpointRounding.AwayFromZero);

        try
        {
            DesktopLayoutZone candidate;
            if (_dragMode == DragMode.ResizeBottomRight)
            {
                var minWidth = Math.Max(1, (int)Math.Round(
                    MinimumZonePixels / _canvas.ActualWidth * NormalizedRect.Scale));
                var minHeight = Math.Max(1, (int)Math.Round(
                    MinimumZonePixels / _canvas.ActualHeight * NormalizedRect.Scale));
                candidate = DesktopZoneEditOperations.ResizeBottomRight(
                    _dragOrigin,
                    deltaX,
                    deltaY,
                    minWidth,
                    minHeight);
            }
            else
            {
                candidate = DesktopZoneEditOperations.Move(_dragOrigin, deltaX, deltaY);
            }

            _zones = DesktopZoneEditOperations.ReplaceZone(_zones, candidate);
            ApplyZoneVisualBounds(_activeZoneVisual, candidate);
            _statusText.Text = $"{candidate.Name} · {candidate.Bounds.Width / 100d:0.#}% × {candidate.Bounds.Height / 100d:0.#}%";
        }
        catch (InvalidOperationException)
        {
            // 与其他 Zone 重叠时保持最后一个合法位置，不自动挤开别的 Zone。
        }
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        => EndDrag(e.Pointer);

    private void Canvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
        => EndDrag(e.Pointer);

    private void EndDrag(Pointer pointer)
    {
        if (_activePointerId != pointer.PointerId)
            return;

        _canvas.ReleasePointerCapture(pointer);
        _activePointerId = null;
        _dragOrigin = null;
        _activeZoneVisual = null;
        _dragMode = DragMode.None;
        RefreshInspector();
        RenderZones();
    }

    private void NewZone_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitSelectedZoneName();
            _zones = DesktopZoneEditOperations.AddZone(_zones);
            _selectedZoneId = _zones.OrderBy(zone => zone.SortOrder).Last().ZoneId;
            _statusText.Text = "已创建新分区 · 仍需完成并保存";
            RefreshInspector();
            RenderZones();
        }
        catch (Exception ex)
        {
            _statusText.Text = ex.Message;
        }
    }

    private void DeleteZone_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedZoneId is not Guid zoneId)
            return;

        try
        {
            _zones = DesktopZoneEditOperations.DeleteZone(
                _zones,
                _sourceDraft.RuleSet,
                zoneId);
            _zoneColorOverrides.Remove(zoneId);
            _selectedZoneId = _zones.OrderBy(zone => zone.SortOrder).First().ZoneId;
            _statusText.Text = "分区已删除 · 仍需完成并保存";
            RefreshInspector();
            RenderZones();
        }
        catch (Exception ex)
        {
            _statusText.Text = ex.Message;
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitSelectedZoneName();
            _resultRaised = true;
            Completed?.Invoke(
                this,
                new DesktopZoneEditorCompletedEventArgs(
                    Array.AsReadOnly(_zones.ToArray()),
                    _zoneColorOverrides.ToDictionary(pair => pair.Key, pair => pair.Value)));
            Close();
        }
        catch (Exception ex)
        {
            _statusText.Text = ex.Message;
        }
    }

    private void CloseAsCancelled()
    {
        if (_resultRaised)
            return;

        _resultRaised = true;
        Cancelled?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void CommitSelectedZoneName()
    {
        if (_suppressInspectorEvents || _selectedZoneId is not Guid zoneId)
            return;

        var zone = _zones.First(item => item.ZoneId == zoneId);
        var name = _zoneNameBox.Text.Trim();
        if (name.Length == 0)
        {
            _zoneNameBox.Text = zone.Name;
            throw new InvalidOperationException("分区名称不能为空。 ");
        }
        if (string.Equals(name, zone.Name, StringComparison.Ordinal))
            return;

        var renamed = new DesktopLayoutZone(
            zone.ZoneId,
            name,
            zone.Bounds,
            zone.SortOrder,
            zone.PlacementFlow,
            zone.MaxItemCount);
        _zones = DesktopZoneEditOperations.ReplaceZone(_zones, renamed);
        RenderZones();
    }

    private void RefreshInspector()
    {
        if (_selectedZoneId is not Guid zoneId)
            return;

        var zone = _zones.First(item => item.ZoneId == zoneId);
        _suppressInspectorEvents = true;
        try
        {
            _zoneNameBox.Text = zone.Name;
            _deleteButton.IsEnabled = _zones.Count > 1 &&
                !_sourceDraft.RuleSet.Rules.Any(rule =>
                    rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride &&
                    rule.TargetZoneId == zone.ZoneId);
            _colorPicker.Color = ToColor(ResolveZoneColor(zone.ZoneId));
            _colorButton.Content = _zoneColorOverrides.ContainsKey(zone.ZoneId)
                ? "颜色 · 独立"
                : "颜色 · 继承";
            _statusText.Text = $"{zone.Name} · {zone.Bounds.Width / 100d:0.#}% × {zone.Bounds.Height / 100d:0.#}%";
        }
        finally
        {
            _suppressInspectorEvents = false;
        }
    }

    private void ColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressInspectorEvents || _selectedZoneId is not Guid zoneId)
            return;

        _zoneColorOverrides[zoneId] = ToRgb(args.NewColor);
        _colorButton.Content = "颜色 · 独立";
        RenderZones();
    }

    private uint ResolveZoneColor(Guid zoneId)
        => _zoneColorOverrides.TryGetValue(zoneId, out var rgb)
            ? rgb
            : _settings.OverlayAppearance.ZoneColorRgb;

    private void ApplyZoneVisualBounds(Border visual, DesktopLayoutZone zone)
    {
        var width = Math.Max(1d, _canvas.ActualWidth);
        var height = Math.Max(1d, _canvas.ActualHeight);
        visual.Width = Math.Max(1d, width * zone.Bounds.Width / NormalizedRect.Scale);
        visual.Height = Math.Max(1d, height * zone.Bounds.Height / NormalizedRect.Scale);
        Canvas.SetLeft(visual, width * zone.Bounds.Left / NormalizedRect.Scale);
        Canvas.SetTop(visual, height * zone.Bounds.Top / NormalizedRect.Scale);
    }

    private static Color ToColor(uint rgb)
        => Color.FromArgb(
            0xFF,
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));

    private static uint ToRgb(Color color)
        => ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private enum DragMode
    {
        None = 0,
        Move = 1,
        ResizeBottomRight = 2,
    }

    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(
        nint hwnd,
        uint colorKey,
        byte alpha,
        uint flags);
}

internal sealed record DesktopZoneEditorCompletedEventArgs(
    IReadOnlyList<DesktopLayoutZone> Zones,
    IReadOnlyDictionary<Guid, uint> ZoneColorOverrides);
