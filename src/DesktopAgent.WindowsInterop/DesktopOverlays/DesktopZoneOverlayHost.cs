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
/// Overlay 由 Windows Shell desktop window 拥有，保持在普通应用窗口之后；
/// 同时使用 layered + transparent + no-activate，确保它只承担桌面空间提示职责，
/// 不抢焦点、不拦截鼠标，也不读取或写入任何桌面图标位置。
/// </summary>
public sealed class DesktopZoneOverlayHost : IDisposable
{
    private nint _window;
    private bool _disposed;

    public bool IsVisible => _window != nint.Zero && NativeOverlayWindow.IsAlive(_window);

    public void Show(DisplayDescriptor display, IReadOnlyList<DesktopLayoutZone> zones)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(zones);

        if (zones.Count == 0)
            throw new ArgumentException("至少需要一个 Zone 才能显示桌面线框。", nameof(zones));

        Hide();
        _window = NativeOverlayWindow.Create(display, zones);
    }

    public void Hide()
    {
        var hwnd = _window;
        _window = nint.Zero;
        if (hwnd != nint.Zero)
            NativeOverlayWindow.Destroy(hwnd);
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

    private static class NativeOverlayWindow
    {
        private const string WindowClassName = "DesktopAgent.ZoneOverlay.Canvas";

        private const uint WsPopup = 0x80000000;
        private const uint WsExTransparent = 0x00000020;
        private const uint WsExToolWindow = 0x00000080;
        private const uint WsExLayered = 0x00080000;
        private const uint WsExNoActivate = 0x08000000;
        private const uint OverlayExStyle =
            WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;

        private const int SwShowNoActivate = 4;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private static readonly nint HwndBottom = new(1);

        private const uint LwaColorKey = 0x00000001;
        private const uint LwaAlpha = 0x00000002;
        private const byte OverlayAlpha = 180;

        private const uint WmEraseBackground = 0x0014;
        private const uint WmPaint = 0x000F;
        private const uint WmNcDestroy = 0x0082;
        private const uint WmNcHitTest = 0x0084;
        private const uint WmMouseActivate = 0x0021;
        private const int HtTransparent = -1;
        private const int MaNoActivate = 3;

        private const int BorderBaseThickness = 1;
        private const int LabelBaseHeight = 26;
        private const int LabelBaseWidth = 128;
        private const int LabelInset = 8;

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
        private static readonly ConcurrentDictionary<nint, OverlayState> States = new();
        private static readonly WindowProcedure Procedure = OverlayWindowProcedure;

        // 使用一个不太可能与 UI 颜色重合的 key；该颜色像素由 layered window 完全透明化。
        private static readonly uint TransparentColor = Rgb(0x01, 0x02, 0x03);
        private static readonly nint TransparentBrush = CreateSolidBrush(TransparentColor);
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

        public static bool IsAlive(nint hwnd)
            => hwnd != nint.Zero && IsWindow(hwnd);

        public static nint Create(
            DisplayDescriptor display,
            IReadOnlyList<DesktopLayoutZone> zones)
        {
            EnsureRegistered();

            var shellWindow = GetShellWindow();
            if (shellWindow == nint.Zero)
            {
                throw new InvalidOperationException(
                    "Windows Shell desktop window 当前不可用，拒绝创建全局顶层线框作为回退。 ");
            }

            var work = display.WorkArea;
            if (work.Width <= 0 || work.Height <= 0)
                throw new InvalidOperationException("目标显示器 WorkArea 无效。 ");

            var hwnd = CreateWindowExW(
                OverlayExStyle,
                WindowClassName,
                string.Empty,
                WsPopup,
                work.Left,
                work.Top,
                work.Width,
                work.Height,
                shellWindow,
                nint.Zero,
                GetModuleHandleW(null),
                nint.Zero);

            if (hwnd == nint.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 Desktop Zone overlay window。 ");

            States[hwnd] = new OverlayState(
                work.Width,
                work.Height,
                Math.Max(1d, display.ScaleX),
                zones.OrderBy(zone => zone.SortOrder).ToArray());

            try
            {
                if (!SetLayeredWindowAttributes(
                        hwnd,
                        TransparentColor,
                        OverlayAlpha,
                        LwaColorKey | LwaAlpha))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法设置 Desktop Zone overlay 透明度。 ");
                }

                // 关键边界：绝不使用 WS_EX_TOPMOST / HWND_TOPMOST。
                // Overlay 由 Shell desktop window 拥有，并请求 normal Z-order 底部，
                // 让普通应用窗口自然覆盖它，而不是让桌面辅助线压在应用之上。
                if (!SetWindowPos(
                        hwnd,
                        HwndBottom,
                        work.Left,
                        work.Top,
                        work.Width,
                        work.Height,
                        SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法定位 Desktop Zone overlay。 ");
                }

                _ = ShowWindow(hwnd, SwShowNoActivate);
                _ = InvalidateRect(hwnd, nint.Zero, erase: false);
                return hwnd;
            }
            catch
            {
                States.TryRemove(hwnd, out _);
                _ = DestroyWindow(hwnd);
                throw;
            }
        }

        public static void Destroy(nint hwnd)
        {
            if (hwnd == nint.Zero)
                return;

            States.TryRemove(hwnd, out _);
            if (IsWindow(hwnd))
                _ = DestroyWindow(hwnd);
        }

        private static void EnsureRegistered()
        {
            if (_registered)
                return;

            lock (RegistrationLock)
            {
                if (_registered)
                    return;

                var instance = GetModuleHandleW(null);
                var windowClass = new WindowClassEx
                {
                    Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                    Instance = instance,
                    WindowProcedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                    ClassName = WindowClassName,
                };

                if (RegisterClassExW(ref windowClass) == 0)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"无法注册 overlay window class: {WindowClassName}");
                }

                _registered = true;
            }
        }

        private static nint OverlayWindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
        {
            // 任何 managed 绘制异常都不得穿过 native WndProc 边界导致整个产品进程退出。
            try
            {
                if (message == WmNcHitTest)
                    return new nint(HtTransparent);
                if (message == WmMouseActivate)
                    return new nint(MaNoActivate);
                if (message == WmEraseBackground)
                    return new nint(1);
                if (message == WmNcDestroy)
                    States.TryRemove(hwnd, out _);
                if (message == WmPaint)
                {
                    PaintOverlay(hwnd);
                    return nint.Zero;
                }
            }
            catch
            {
                // Fail visually rather than terminate the WinUI process.
            }

            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        private static void PaintOverlay(nint hwnd)
        {
            var hdc = BeginPaint(hwnd, out var paint);
            try
            {
                if (!GetClientRect(hwnd, out var clientRect))
                    return;

                _ = FillRect(hdc, ref clientRect, TransparentBrush);

                if (!States.TryGetValue(hwnd, out var state))
                    return;

                var thickness = Math.Max(1, (int)Math.Round(BorderBaseThickness * state.Scale));
                foreach (var zone in state.Zones)
                {
                    var rect = MapZoneToClientRect(state, zone);
                    if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                        continue;

                    FillStrip(hdc, rect.Left, rect.Top, rect.Right - rect.Left, thickness, AccentBrush);
                    FillStrip(hdc, rect.Left, rect.Bottom - thickness, rect.Right - rect.Left, thickness, AccentBrush);
                    FillStrip(hdc, rect.Left, rect.Top, thickness, rect.Bottom - rect.Top, AccentBrush);
                    FillStrip(hdc, rect.Right - thickness, rect.Top, thickness, rect.Bottom - rect.Top, AccentBrush);

                    DrawLabel(hdc, state, zone.Name, rect);
                }
            }
            finally
            {
                _ = EndPaint(hwnd, ref paint);
            }
        }

        private static NativeRect MapZoneToClientRect(OverlayState state, DesktopLayoutZone zone)
        {
            var bounds = zone.Bounds;
            static int Scale(int length, int normalized)
                => (int)Math.Round(
                    length * (normalized / (double)NormalizedRect.Scale),
                    MidpointRounding.AwayFromZero);

            return new NativeRect
            {
                Left = Scale(state.Width, bounds.Left),
                Top = Scale(state.Height, bounds.Top),
                Right = Scale(state.Width, bounds.Right),
                Bottom = Scale(state.Height, bounds.Bottom),
            };
        }

        private static void FillStrip(nint hdc, int x, int y, int width, int height, nint brush)
        {
            if (width <= 0 || height <= 0)
                return;

            var rect = new NativeRect
            {
                Left = x,
                Top = y,
                Right = x + width,
                Bottom = y + height,
            };
            _ = FillRect(hdc, ref rect, brush);
        }

        private static void DrawLabel(
            nint hdc,
            OverlayState state,
            string text,
            NativeRect zoneRect)
        {
            var zoneWidth = zoneRect.Right - zoneRect.Left;
            var zoneHeight = zoneRect.Bottom - zoneRect.Top;
            var availableWidth = Math.Max(0, zoneWidth - LabelInset * 2);
            if (availableWidth < 48 || zoneHeight < 30)
                return;

            var width = Math.Min(
                Math.Max(72, (int)Math.Round(LabelBaseWidth * state.Scale)),
                availableWidth);
            var height = Math.Min(
                Math.Max(22, (int)Math.Round(LabelBaseHeight * state.Scale)),
                Math.Max(22, zoneHeight - LabelInset * 2));

            var labelRect = new NativeRect
            {
                Left = zoneRect.Left + LabelInset,
                Top = zoneRect.Top + LabelInset,
                Right = zoneRect.Left + LabelInset + width,
                Bottom = zoneRect.Top + LabelInset + height,
            };

            _ = FillRect(hdc, ref labelRect, LabelBrush);
            _ = SetBkMode(hdc, TransparentBackgroundMode);
            _ = SetTextColor(hdc, Rgb(0xFF, 0xFF, 0xFF));

            var previousFont = LabelFont == nint.Zero ? nint.Zero : SelectObject(hdc, LabelFont);
            try
            {
                labelRect.Left += 9;
                labelRect.Right -= 7;
                _ = DrawTextW(
                    hdc,
                    text,
                    text.Length,
                    ref labelRect,
                    DtLeft | DtVCenter | DtSingleLine | DtEndEllipsis);
            }
            finally
            {
                if (previousFont != nint.Zero)
                    _ = SelectObject(hdc, previousFont);
            }
        }

        private static uint Rgb(byte red, byte green, byte blue)
            => (uint)(red | (green << 8) | (blue << 16));

        private sealed record OverlayState(
            int Width,
            int Height,
            double Scale,
            DesktopLayoutZone[] Zones);

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

        // PAINTSTRUCT 的 rgbReserved[32] 从 offset 36 开始；8 个 Int32 保持 4-byte alignment，
        // 避免 x64 下使用 Int64 时引入额外 padding 并破坏 native 布局。
        [StructLayout(LayoutKind.Sequential)]
        private struct PaintStruct
        {
            public nint DeviceContext;
            public int Erase;
            public NativeRect PaintRect;
            public int Restore;
            public int Update;
            private int Reserved0;
            private int Reserved1;
            private int Reserved2;
            private int Reserved3;
            private int Reserved4;
            private int Reserved5;
            private int Reserved6;
            private int Reserved7;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint GetModuleHandleW(string? moduleName);

        [DllImport("user32.dll")]
        private static extern nint GetShellWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(nint hwnd);

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
