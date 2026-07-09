# AppDriver.ps1 — drives the (unelevated) DreamOfElectricStorage.App window for automated
# UI verification: screenshots + synthesized mouse/keyboard input at CLIENT coordinates.
# Coordinates match screenshots taken by this same script (client area, physical pixels).
#
# Usage:
#   .\AppDriver.ps1 screenshot out.png
#   .\AppDriver.ps1 click 400 300        (client px)
#   .\AppDriver.ps1 dblclick 400 300
#   .\AppDriver.ps1 rightclick 400 300
#   .\AppDriver.ps1 move 400 300         (hover)
#   .\AppDriver.ps1 wheel 120 400 300    (delta, then position)
#   .\AppDriver.ps1 drag 100 100 500 400
#   .\AppDriver.ps1 key <VirtualKeyCode> (e.g. 46 = Delete, 27 = Esc)
#   .\AppDriver.ps1 type "text"
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Action,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)

$ErrorActionPreference = "Stop"

$src = @'
using System;
using System.Runtime.InteropServices;
public static class Drv {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  public struct RECT { public int L, T, R, B; }
  public struct POINT { public int X, Y; }
  public const uint MOVE = 0x01, ABSOLUTE = 0x8000, LEFTDOWN = 0x02, LEFTUP = 0x04, RIGHTDOWN = 0x08, RIGHTUP = 0x10, WHEEL = 0x800, KEYUP = 0x2;
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
}
'@
try { Add-Type -TypeDefinition $src -ErrorAction Stop } catch {}
[Drv]::SetProcessDPIAware() | Out-Null  # physical pixels everywhere — no DPI virtualization lies

$proc = Get-Process DreamOfElectricStorage.App -ErrorAction Stop | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
[Drv]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 250

function ToScreen([int]$cx, [int]$cy) {
    $p = New-Object Drv+POINT; $p.X = $cx; $p.Y = $cy
    [Drv]::ClientToScreen($hwnd, [ref]$p) | Out-Null
    return $p
}

function MoveTo([int]$cx, [int]$cy) {
    # mouse_event ABSOLUTE injects real pointer input (SetCursorPos warps the cursor
    # WITHOUT generating pointer events — WinUI hover/PointerMoved never fires).
    $p = ToScreen $cx $cy
    $sw = [Drv]::GetSystemMetrics(0); $sh = [Drv]::GetSystemMetrics(1)
    $nx = [int]($p.X * 65535 / ($sw - 1)); $ny = [int]($p.Y * 65535 / ($sh - 1))
    [Drv]::mouse_event(([Drv]::MOVE -bor [Drv]::ABSOLUTE), $nx, $ny, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 120
}

function ClickAt([int]$cx, [int]$cy) {
    MoveTo $cx $cy
    [Drv]::mouse_event([Drv]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    [Drv]::mouse_event([Drv]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150
}

switch ($Action.ToLowerInvariant()) {
    "screenshot" {
        # True screen pixels via CopyFromScreen, cropped to the window. PrintWindow is NOT
        # used: it returns stale composition snapshots for WinUI windows (verified 2026-07-09).
        # Window must be frontmost (this script foregrounds it above).
        Add-Type -AssemblyName System.Drawing
        Start-Sleep -Milliseconds 300
        $r = New-Object Drv+RECT
        [Drv]::GetWindowRect($hwnd, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap(($r.R - $r.L), ($r.B - $r.T))
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
        $bmp.Save($Rest[0], [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Output "saved $($Rest[0]) ($($r.R - $r.L)x$($r.B - $r.T)) origin=$($r.L),$($r.T)"
    }
    "screen" {
        # Full-desktop capture around the app window — use for popups (flyouts, dialogs,
        # AutoSuggest dropdowns): they are separate HWNDs that PrintWindow can't see.
        Add-Type -AssemblyName System.Drawing
        Start-Sleep -Milliseconds 300
        $r = New-Object Drv+RECT
        [Drv]::GetWindowRect($hwnd, [ref]$r) | Out-Null
        $pad = 200
        $bmp = New-Object System.Drawing.Bitmap(($r.R - $r.L + 2 * $pad), ($r.B - $r.T + 2 * $pad))
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.L - $pad, $r.T - $pad, 0, 0, $bmp.Size)
        $bmp.Save($Rest[0], [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Output "saved $($Rest[0]) (screen capture, window offset +$pad,+$pad)"
    }
    "click"      { ClickAt ([int]$Rest[0]) ([int]$Rest[1]); Write-Output "clicked $($Rest[0]),$($Rest[1])" }
    "dblclick"   { ClickAt ([int]$Rest[0]) ([int]$Rest[1]); Start-Sleep -Milliseconds 60; ClickAt ([int]$Rest[0]) ([int]$Rest[1]); Write-Output "double-clicked" }
    "rightclick" {
        MoveTo ([int]$Rest[0]) ([int]$Rest[1])
        [Drv]::mouse_event([Drv]::RIGHTDOWN, 0, 0, 0, [IntPtr]::Zero)
        [Drv]::mouse_event([Drv]::RIGHTUP, 0, 0, 0, [IntPtr]::Zero)
        Write-Output "right-clicked"
    }
    "move"       { MoveTo ([int]$Rest[0]) ([int]$Rest[1]); Write-Output "moved" }
    "wheel"      {
        if ($Rest.Length -ge 3) { MoveTo ([int]$Rest[1]) ([int]$Rest[2]) }
        [Drv]::mouse_event([Drv]::WHEEL, 0, 0, [int]$Rest[0], [IntPtr]::Zero)
        Write-Output "wheeled $($Rest[0])"
    }
    "drag"       {
        MoveTo ([int]$Rest[0]) ([int]$Rest[1])
        [Drv]::mouse_event([Drv]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
        # Step the move so the app sees intermediate PointerMoved events (drag thresholds).
        $steps = 12
        for ($i = 1; $i -le $steps; $i++) {
            $x = [int]($Rest[0]) + ([int]($Rest[2]) - [int]($Rest[0])) * $i / $steps
            $y = [int]($Rest[1]) + ([int]($Rest[3]) - [int]($Rest[1])) * $i / $steps
            MoveTo ([int]$x) ([int]$y)
        }
        [Drv]::mouse_event([Drv]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
        Write-Output "dragged"
    }
    "key"        {
        [Drv]::keybd_event([byte][int]$Rest[0], 0, 0, [IntPtr]::Zero)
        [Drv]::keybd_event([byte][int]$Rest[0], 0, [Drv]::KEYUP, [IntPtr]::Zero)
        Write-Output "key $($Rest[0])"
    }
    "type"       {
        foreach ($ch in $Rest[0].ToCharArray()) {
            [System.Windows.Forms.SendKeys]::SendWait([string]$ch) 2>$null
        }
        Write-Output "typed"
    }
    default      { Write-Error "unknown action: $Action" }
}
