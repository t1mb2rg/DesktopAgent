using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
namespace DesktopAgent.WinUI;

// Explicit desktop editing reuses the same session and grid controller, with no Writer.
internal sealed class WorkspaceDesktopEditorWindow : Window
{
    private readonly WorkspaceDesignCanvas _controller;
    private readonly ProductWorkspaceSession _session;
    private readonly AppWindow _window;
    private readonly RectInt32 _bounds;
    private readonly Canvas _canvas = new();
    private bool _configured;
    private readonly string _geometryFingerprint;
    internal WorkspaceDesktopEditorWindow(ProductWorkspaceSession session)
    {
        _session = session;
        var geometry = session.DesignGeometry ?? throw new InvalidOperationException("请先校准桌面网格。");
        _geometryFingerprint = geometry.GeometryFingerprint;
        var left = geometry.Displays.Min(d => d.Bounds.Left);
        var top = geometry.Displays.Min(d => d.Bounds.Top);
        var right = geometry.Displays.Max(d => d.Bounds.Right);
        var bottom = geometry.Displays.Max(d => d.Bounds.Bottom);
        _bounds = new(left, top, right - left, bottom - top);
        Title = "Tessel · 共享桌面编辑";
        var root = new Grid { Background = new SolidColorBrush(Colors.Black) };
        root.Children.Add(_canvas);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _controller = new WorkspaceDesignCanvas(session, _canvas, summary) { DesktopCoordinates = true };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var done = new Button { Content = "完成 · 返回应用" };
        done.Click += (_, _) => Close();
        var undo = new Button { Content = "撤销" };
        undo.Click += (_, _) => session.Session.Undo(session.Session.Snapshot().Revision);
        var redo = new Button { Content = "重做" };
        redo.Click += (_, _) => session.Session.Redo(session.Session.Snapshot().Revision);
        toolbar.Children.Add(undo); toolbar.Children.Add(redo); toolbar.Children.Add(done);
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "拖动分区 / 拖右下角缩放 · 锁定实测网格 · 自动保存草稿，不移动图标", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(summary); content.Children.Add(toolbar);
        var bar = new Border { Child = content, Padding = new Thickness(14), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(255, 35, 35, 35)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        root.Children.Add(bar);
        // Keep the controls on the primary screen, including negative virtual origins.
        _canvas.SizeChanged += (_, _) =>
        {
            var primary = geometry.Displays.First(d => d.DisplayRef == "primary");
            var scale = _canvas.ActualWidth / _bounds.Width;
            bar.Margin = new Thickness((primary.Bounds.Left - left) * scale + 20,
                (primary.Bounds.Top - top) * scale + 20, 0, 0);
            bar.MaxWidth = Math.Max(200, primary.Bounds.Width * scale - 40);
        };
        root.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; Close(); }
        };
        Content = root;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _window = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        if (_window.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        Activated += (_, _) =>
        {
            if (_configured) return;
            _configured = true;
            _window.MoveAndResize(_bounds);
            SetWindowLongPtr(hwnd, -20, GetWindowLongPtr(hwnd, -20) | (nint)0x80000);
            if (!SetLayeredWindowAttributes(hwnd, 0, 190, 3)) Close();
            _controller.RenderChatTopology();
        };
        _session.Changed += SessionChanged;
        Closed += (_, _) => { _controller.CancelDrag(); _session.Changed -= SessionChanged; };
    }
    private void SessionChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_session.DesignGeometry is null || _session.DesignGeometry.GeometryFingerprint != _geometryFingerprint) { Close(); return; }
        _controller.RenderChatTopology();
    });
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
}
