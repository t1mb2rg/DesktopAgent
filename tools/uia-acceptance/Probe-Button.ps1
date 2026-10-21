param(
    [Parameter(Mandatory=$true)][string]$ProcessName,
    [Parameter(Mandatory=$true)][string]$TitleContains,
    [Parameter(Mandatory=$true)][string]$AutomationId,
    [switch]$Click
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class M {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, uint e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$IP = [System.Windows.Automation.InvokePattern]
$TP = [System.Windows.Automation.TogglePattern]
$SP = [System.Windows.Automation.SelectionItemPattern]

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "process '$ProcessName' not found." }
$root = $AE::RootElement
$pc = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pc)
$win = $null
for ($i = 0; $i -lt $wins.Count; $i++) {
    if ($wins[$i].Current.Name -like "*$TitleContains*") { $win = $wins[$i]; break }
}
if (-not $win) { throw "window containing '$TitleContains' not found." }
"window: name='$($win.Current.Name)'"

$cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $AutomationId)
$hits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
"hits: $($hits.Count)"
for ($i = 0; $i -lt $hits.Count; $i++) {
    $el = $hits[$i]
    $ct = $el.Current.ControlType.ProgrammaticName
    $r = $el.Current.BoundingRectangle
    $inv = $false; $tog = $false; $sel = $false
    try { $null = $el.GetCurrentPattern($IP::Pattern); $inv = $true } catch { }
    try { $null = $el.GetCurrentPattern($TP::Pattern); $tog = $true } catch { }
    try { $null = $el.GetCurrentPattern($SP::Pattern); $sel = $true } catch { }
    "  [$i] $ct name='$($el.Current.Name)' enabled=$($el.Current.IsEnabled) offscreen=$($el.Current.IsOffscreen) rect=$([int]$r.Left),$([int]$r.Top),$([int]$r.Width)x$([int]$r.Height) invoke=$inv toggle=$tog sel=$sel"
}

if ($Click) {
    $el = $null
    for ($i = 0; $i -lt $hits.Count; $i++) {
        if ($hits[$i].Current.IsEnabled -and -not $hits[$i].Current.IsOffscreen) { $el = $hits[$i]; break }
    }
    if (-not $el) { throw "no enabled, on-screen element to click." }
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.Left + $r.Width / 2)
    $y = [int]($r.Top + $r.Height / 2)
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    $null = [M]::ShowWindow($hwnd, 9)
    $null = [M]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 400
    $null = [M]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 150
    [M]::mouse_event(0x0002, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [M]::mouse_event(0x0004, 0, 0, 0, 0)
    "clicked at $x,$y"
}
