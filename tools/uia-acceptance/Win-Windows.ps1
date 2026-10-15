param(
    [Parameter(Mandatory=$true)][ValidateSet('list','click','close','enter','activate','keys')][string]$Action,
    [int]$TargetPid = 0,
    [long]$Handle = 0,
    [int]$CommandId = 6,
    [string]$KeySpec = ''
)

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class W32 {
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}

public static class WinTree {
    public const uint GW_CHILD = 5;
    public const uint GW_HWNDNEXT = 2;
    public const uint WM_COMMAND = 0x0111;

    public static string Text(IntPtr h) {
        var sb = new StringBuilder(512);
        W32.GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Class(IntPtr h) {
        var sb = new StringBuilder(256);
        W32.GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Dump(uint filterPid) {
        var sb = new StringBuilder();
        var top = W32.GetWindow(W32.GetDesktopWindow(), GW_CHILD);
        while (top != IntPtr.Zero) {
            uint pid;
            W32.GetWindowThreadProcessId(top, out pid);
            if (filterPid == 0 || pid == filterPid) {
                sb.AppendLine(string.Format("TOP pid={0} class='{1}' visible={2} title='{3}' hwnd={4}",
                    pid, Class(top), W32.IsWindowVisible(top), Text(top), top.ToInt64()));
                var child = W32.GetWindow(top, GW_CHILD);
                while (child != IntPtr.Zero) {
                    sb.AppendLine(string.Format("   CHILD class='{0}' title='{1}' hwnd={2}",
                        Class(child), Text(child), child.ToInt64()));
                    child = W32.GetWindow(child, GW_HWNDNEXT);
                }
            }
            top = W32.GetWindow(top, GW_HWNDNEXT);
        }
        return sb.ToString();
    }

    public static string Click(long hwnd, int id) {
        var h = new IntPtr(hwnd);
        // Post WM_COMMAND straight to the dialog (button ids: IDYES=6, IDNO=7, IDOK=1);
        // does not depend on foreground focus.
        bool posted = W32.PostMessage(h, WM_COMMAND, new IntPtr(id), IntPtr.Zero);
        return string.Format("PostMessage WM_COMMAND id={0} to hwnd={1} -> {2}", id, hwnd, posted);
    }

    public static string Close(long hwnd) {
        var h = new IntPtr(hwnd);
        bool posted = W32.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
        return string.Format("PostMessage WM_CLOSE to hwnd={0} -> {1}", hwnd, posted);
    }

    public static string Enter(long hwnd) {
        var h = new IntPtr(hwnd);
        bool down = W32.PostMessage(h, 0x0100, new IntPtr(13), IntPtr.Zero);
        bool up = W32.PostMessage(h, 0x0101, new IntPtr(13), IntPtr.Zero);
        return string.Format("PostMessage VK_RETURN to hwnd={0} -> down={1} up={2}", hwnd, down, up);
    }

    public static string Keys(string spec) {
        var parts = spec.ToUpperInvariant().Split('+');
        var codes = new System.Collections.Generic.List<byte>();
        foreach (var raw in parts) {
            string n = raw.Trim();
            byte vk;
            switch (n) {
                case "LWIN": vk = 0x5B; break;
                case "RWIN": vk = 0x5C; break;
                case "SHIFT": vk = 0x10; break;
                case "CTRL": vk = 0x11; break;
                case "ALT": vk = 0x12; break;
                case "ESC": vk = 0x1B; break;
                case "ENTER": vk = 0x0D; break;
                case "TAB": vk = 0x09; break;
                case "LEFT": vk = 0x25; break;
                case "UP": vk = 0x26; break;
                case "RIGHT": vk = 0x27; break;
                case "DOWN": vk = 0x28; break;
                case "SPACE": vk = 0x20; break;
                case "APPS": vk = 0x5D; break;
                default:
                    if (n.Length == 1) vk = (byte)n[0];
                    else if (n.Length >= 2 && n[0] == 'F') vk = (byte)(0x6F + int.Parse(n.Substring(1)));
                    else throw new ArgumentException("unknown key: " + n);
                    break;
            }
            codes.Add(vk);
        }
        foreach (var c in codes) W32.keybd_event(c, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        for (int i = codes.Count - 1; i >= 0; i--) W32.keybd_event(codes[i], 0, 2, UIntPtr.Zero);
        return "keys sent: " + spec;
    }

    public static string Activate(long hwnd) {
        var h = new IntPtr(hwnd);
        bool shown = W32.SetForegroundWindow(h);
        return string.Format("SetForegroundWindow hwnd={0} -> {1}", hwnd, shown);
    }
}
"@

switch ($Action) {
    'list' {
        Write-Output (([WinTree]::Dump([uint32]$TargetPid)))
    }
    'click' {
        Write-Output ([WinTree]::Click($Handle, $CommandId))
    }
    'close' {
        Write-Output ([WinTree]::Close($Handle))
    }
    'enter' {
        Write-Output ([WinTree]::Enter($Handle))
    }
    'activate' {
        Write-Output ([WinTree]::Activate($Handle))
    }
    'keys' {
        Write-Output ([WinTree]::Keys($KeySpec))
    }
}
