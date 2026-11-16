<#
.SYNOPSIS
  Development helper: saves a screenshot of the main window of a running process (for UI checks without manual clicking).
.EXAMPLE
  ./build/Capture-Window.ps1 -ProcessName ThixNote -OutFile artifacts/screen.png -Width 1180 -Height 760
#>
param(
    [string] $ProcessName = 'ThixNote',
    [Parameter(Mandatory)] [string] $OutFile,
    [int] $Width = 0,
    [int] $Height = 0
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowTools {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr hdc, int flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out Rect rect, int size);
}
'@
[WindowTools]::SetProcessDPIAware() | Out-Null

$process = Get-Process -Name $ProcessName -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$handle = $process.MainWindowHandle
[WindowTools]::ShowWindow($handle, 9) | Out-Null
if ($Width -gt 0 -and $Height -gt 0) {
    [WindowTools]::MoveWindow($handle, 40, 40, $Width, $Height, $true) | Out-Null
}
[WindowTools]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 900

$rect = New-Object WindowTools+Rect
[WindowTools]::GetWindowRect($handle, [ref] $rect) | Out-Null
$bitmap = New-Object System.Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc(); [WindowTools]::PrintWindow($handle, $hdc, 2) | Out-Null; $graphics.ReleaseHdc($hdc)

# The window rect includes the invisible resize border, and PrintWindow paints it (plus the 1 px window
# outline at the sides and bottom) black. Crop to the visible frame (DWMWA_EXTENDED_FRAME_BOUNDS) minus that outline.
$frame = New-Object WindowTools+Rect
$visible = New-Object System.Drawing.Rectangle 0, 0, $bitmap.Width, $bitmap.Height
if ([WindowTools]::DwmGetWindowAttribute($handle, 9, [ref] $frame, 16) -eq 0) {
    $visible = New-Object System.Drawing.Rectangle ($frame.Left - $rect.Left + 1), ($frame.Top - $rect.Top), ($frame.Right - $frame.Left - 2), ($frame.Bottom - $frame.Top - 1)
}
$cropped = $bitmap.Clone($visible, $bitmap.PixelFormat)
$cropped.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose(); $cropped.Dispose()
Write-Host "Saved $OutFile"
