Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
"@

# Minimize any visible top-level window belonging to Code.exe (the IDE) so it doesn't cover the screen
$codeProcs = Get-Process -Name "Code" -ErrorAction SilentlyContinue
foreach ($p in $codeProcs) {
    [Win32]::EnumWindows({
        param($hWnd, $lParam)
        $pid_out = 0
        [Win32]::GetWindowThreadProcessId($hWnd, [ref]$pid_out) | Out-Null
        if ($pid_out -eq $lParam -and [Win32]::IsWindowVisible($hWnd)) {
            [Win32]::ShowWindow($hWnd, 6) | Out-Null # SW_MINIMIZE
        }
        return $true
    }, [IntPtr]$p.Id) | Out-Null
}

Start-Sleep -Milliseconds 800

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bmp.Save("d:\workspace\akashic-records\capture.png", [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bmp.Dispose()
Write-Output "Saved"
