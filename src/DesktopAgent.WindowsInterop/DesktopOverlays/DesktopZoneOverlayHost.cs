using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.WindowsInterop.DesktopOverlays;

/// <summary>
/// 只读 Desktop Zone 线框叠层。
///
/// 该类型只创建鼠标穿透、不可激活的顶层辅助窗口，用于在真实桌面上显示 Zone 边界与名称；
/// 不读取或写入图标位置，也不暴露任何 Writer 能力。
/// </summary>
public sealed class DesktopZoneOverlayHost : IDisposable
{
    private const int BorderBaseThickness = 2;
    private const int LabelBaseHeight = 28;
    private const int LabelBaseWidth = 128;
    private const int LabelInset = 8;

    private readonly List<nint> _windows = [];
    private bool _disposed;

    public bool IsVisible => _windows.Count > 0;

    public void Show(DisplayDescriptor display, IReadOnlyList<DesktopLayoutZone> zones)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(zones);

        if (zones.Count == 0)
            throw new ArgumentException("至少需要一个 Zone 才能显示桌面线框。", nameof(zones));

        NativeOverlayWindow.EnsureRegistered();
        Hide();

        try
        {
            var scale = Math.Max(1d, display.ScaleX);
            var thickness = Math.Max(1, (int)Math.Round(BorderBaseThickness * scale));

            foreach (var zone in zones.OrderBy(zone => zone.SortOrder))
            {
                var rect = MapZoneToPixelRect(display, zone);
                if (rect.Width <= 0 || rect.Height <= 0)
                    continue;

                AddBorder(rect.Left, rect.Top, rect.Width, thickness);
                AddBorder(rect.Left, rect.Bottom - thickness, rect.Width, thickness);
                AddBorder(rect.Left, rect.Top, thickness, rect.Height);
                AddBorder(rect.Right - thickness, rect.Top, thickness, rect.Height);

                var availableWidth = Math.Max(0, rect.Width - LabelInset * 2);
                if (availableWidth < 48)
                    continue;

                var labelWidth = Math.Min(
                    Math.Max(72, (int)Math.Round(LabelBaseWidth * scale)),
                    availableWidth);
                var labelHeight = Math.Min(
                    Math.Max(22, (int)Math.Round(LabelBaseHeight * scale)),
                    Math.Max(22, rect.Height - LabelInset * 2));

                AddLabel(
                    zone.Name,
                    rect.Left + LabelInset,
                    rect.Top + LabelInset,
                    labelWidth,
                    labelHeight);
            }
        }
        catch
        {
            Hide();
            throw;
        }
    }

    public void Hide()
    {
        foreach (var hwnd in _windows)
            NativeOverlayWindow.Destroy(hwnd);

        _windows.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Hide();
        GC.SuppressFinalize(this);
    }

    internal static PixelRect MapZoneToPixelRect(
        DisplayDescriptor display,
        DesktopLayoutZone zone)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(zone);

        var work = display.WorkArea;
        var bounds = zone.Bounds;

        static int Scale(int origin, int length, int normalized)
            => origin + (int)Math.Round(
                length * (normalized / (double)NormalizedRect.Scale),
                MidpointRounding.AwayFromZero);

        return new PixelRect(
            Scale(work.Left, work.Width, bounds.Left),
            Scale(work.Top, work.Height, bounds.Top),
            Scale(work.Left, work.Width, bounds.Right),
            Scale(work.Top, work.Height, bounds.Bottom));
    }

    private void AddBorder(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        _windows.Add(NativeOverlayWindow.CreateBorder(x, y, width, height));
    }

    private void AddLabel(string text, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        _windows.Add(NativeOverlayWindow.CreateLabel(text, x, y, width, height));
    }

    private static class NativeOverlayWindow
    {
        private const string BorderClassName = "DesktopAgent.ZoneOverlay.Border";
        private const string LabelClassName = "DesktopAgent.ZoneOverlay.Label";

        private const uint WsPopup = 0x80000000;
        private const uint WsExTopmost = 0x00000008;
        private const uint WsExTransparent = 0x00000020;
        private const uint WsExToolWindow = 0x00000080;
        private const uint WsExLayered = 0x00080000;
        private const uint WsExNoActivate = 0x08000000;
        private const uint OverlayExStyle =
            WsExTopmost | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;

        private const int SwShowNoActivate = 4;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private static readonly nint HwndTopMost = new(-1);

        private const uint LwaAlpha = 0x00000002;
        private const uint WmPaint = 0x000F;
        private const uint WmNcDestroy = 0x0082;
        private const uint WmNcHitTest = 0x0084;
        private const uint WmMouseActivate = 0x0021;
        private const int HtTransparent = -1;
        private const int MaNoActivate = 3;

        private const int TransparentBackgroundMode = 1;
        private const uint DtLeft = 0x00000000;
        private const uint DtVCenter = 0x00000004;
        private const uint DtSingleLine = 0x00000020;
        private const uint DtEndEllipsis = 0x00008000;
        private const int FontWeightSemiBold = 600;
        private const uint DefaultCharset = 1;
        private const uint OutDefaultPrecision = 0;
        private const uint ClipDefaultPrecision = 0;
        private const uint CleartypeQuality = 5;
        private const uint DefaultPitch = 0;

        private static readonly object RegistrationLock = new();
        private static readonly ConcurrentDictionary<nint, string> LabelTexts = new();
        private static readonly WindowProcedure BorderProcedure = BorderWindowProcedure;
        private static readonly WindowProcedure LabelProcedure = LabelWindowProcedure;

        private static readonly nint AccentBrush = CreateSolidBrush(Rgb(0x00, 0x78, 0xD4));
        private static readonly nint LabelBrush = CreateSolidBrush(Rgb(0x20, 0x20, 0x20));
        private static readonly nint LabelFont = CreateFontW(
            -14,
            0,
            0,
            0,
            FontWeightSemiBold,
            0,
            0,
            0,
            DefaultCharset,
            OutDefaultPrecision,
            ClipDefaultPrecision,
            CleartypeQuality,
            DefaultPitch,
            "Segoe UI");

        private static bool _registered;

        public static void EnsureRegistered()
        {
            if (_registered)
                return;

            lock (RegistrationLock)
            {
                if (_registered)
                    return;

                Register(BorderClassName, BorderProcedure);
                Register(LabelClassName, LabelProcedure);
                _registered = true;
            }
        }

        public static nint CreateBorder(int x, int y, int width, int height)
            => Create(BorderClassName, string.Empty, x, y, width, height, alpha: 120);

        public static nint CreateLabel(string text, int x, int y, int width, int height)
        {
            var hwnd = Create(LabelClassName, text, x, y, width, height, alpha: 205);
            LabelTexts[hwnd] = text;
            InvalidateRect(hwnd, nint.Zero, erase: true);
            return hwnd;
        }

        public static void Destroy(nint hwnd)
        {
            if (hwnd == nint.Zero)
                return;

            LabelTexts.TryRemove(hwnd, out _);
            _ = DestroyWindow(hwnd);
        }

        private static nint Create(
            string className,
            string title,
            int x,
            int y,
            int width,
            int height,
            byte alpha)
        {
            var hwnd = CreateWindowExW(
                OverlayExStyle,
                className,
                title,
                WsPopup,
                x,
                y,
                width,
                height,
                nint.Zero,
                nint.Zero,
                GetModuleHandleW(null),
                nint.Zero);

            if (hwnd == nint.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 Desktop Zone overlay window。");

            if (!SetLayeredWindowAttributes(hwnd, 0, alpha, LwaAlpha))
            {
                var error = Marshal.GetLastWin32Error();
                _ = DestroyWindow(hwnd);
                throw new Win32Exception(error, "无法设置 Desktop Zone overlay 透明度。");
            }

            _ = SetWindowPos(
                hwnd,
                HwndTopMost,
                x,
                y,
                width,
                height,
                SwpNoActivate | SwpShowWindow);
            _ = ShowWindow(hwnd, SwShowNoActivate);
            _ = UpdateWindow(hwnd);
            return hwnd;
        }

        private static void Register(string className, WindowProcedure procedure)
        {
            var instance = GetModuleHandleW(null);
            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                Instance = instance,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(procedure),
                ClassName = className,
            };

            if (RegisterClassExW(ref windowClass) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法注册 overlay window class: {className}");
        }

        private static nint BorderWindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
        {
            if (message == WmNcHitTest)
                return new nint(HtTransparent);
            if (message == WmMouseActivate)
                return new nint(MaNoActivate);
            if (message == WmPaint)
            {
                PaintSolid(hwnd, AccentBrush);
                return nint.Zero;
            }

            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        private static nint LabelWindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
        {
            if (message == WmNcHitTest)
                return new nint(HtTransparent);
            if (message == WmMouseActivate)
                return new nint(MaNoActivate);
            if (message == WmNcDestroy)
                LabelTexts.TryRemove(hwnd, out _);
            if (message == WmPaint)
            {
                PaintLabel(hwnd);
                return nint.Zero;
            }

            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        private static void PaintSolid(nint hwnd, nint brush)
        {
            var hdc = BeginPaint(hwnd, out var paint);
            try
            {
                _ = FillRect(hdc, ref paint.PaintRect, brush);
            }
            finally
            {
                _ = EndPaint(hwnd, ref paint);
            }
        }

        private static void PaintLabel(nint hwnd)
        {
            var hdc = BeginPaint(hwnd, out var paint);
            try
            {
                _ = GetClientRect(hwnd, out var rect);
                _ = FillRect(hdc, ref rect, LabelBrush);
                _ = SetBkMode(hdc, TransparentBackgroundMode);
                _ = SetTextColor(hdc, Rgb(0xFF, 0xFF, 0xFF));

                var previousFont = LabelFont == nint.Zero ? nint.Zero : SelectObject(hdc, LabelFont);
                try
                {
                    rect.Left += 9;
                    rect.Right -= 7;
                    var text = LabelTexts.GetValueOrDefault(hwnd, string.Empty);
                    _ = DrawTextW(
                        hdc,
                        text,
                        text.Length,
                        ref rect,
                        DtLeft | DtVCenter | DtSingleLine | DtEndEllipsis);
                }
                finally
                {
                    if (previousFont != nint.Zero)
                        _ = SelectObject(hdc, previousFont);
                }
            }
            finally
            {
                _ = EndPaint(hwnd, ref paint);
            }
        }

        private static uint Rgb(byte red, byte green, byte blue)
            => (uint)(red | (green << 8) | (blue << 16));

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClassEx
        {
            public uint Size;
            public uint Style;
            public nint WindowProcedure;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            public string? MenuName;
            public string ClassName;
            public nint IconSmall;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PaintStruct
        {
            public nint DeviceContext;
            [MarshalAs(UnmanagedType.Bool)] public bool Erase;
            public NativeRect PaintRect;
            [MarshalAs(UnmanagedType.Bool)] public bool Restore;
            [MarshalAs(UnmanagedType.Bool)] public bool Update;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint GetModuleHandleW(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            nint hwnd,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(nint hwnd, int commandShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateWindow(nint hwnd);

        [DllImport("user32.dll")]
        private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern nint BeginPaint(nint hwnd, out PaintStruct paint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EndPaint(nint hwnd, ref PaintStruct paint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(nint hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern int FillRect(nint hdc, ref NativeRect rect, nint brush);

        [DllImport("user32.dll")]
        private static extern int SetBkMode(nint hdc, int mode);

        [DllImport("gdi32.dll")]
        private static extern uint SetTextColor(nint hdc, uint color);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DrawTextW(nint hdc, string text, int textLength, ref NativeRect rect, uint format);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

        [DllImport("gdi32.dll")]
        private static extern nint CreateSolidBrush(uint color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern nint CreateFontW(
            int height,
            int width,
            int escapement,
            int orientation,
            int weight,
            uint italic,
            uint underline,
            uint strikeOut,
            uint charSet,
            uint outputPrecision,
            uint clipPrecision,
            uint quality,
            uint pitchAndFamily,
            string faceName);

        [DllImport("gdi32.dll")]
        private static extern nint SelectObject(nint hdc, nint objectHandle);
    }
}
