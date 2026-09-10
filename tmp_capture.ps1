Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class Cap {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  public const int SW_RESTORE = 9;
  public static void Bring(IntPtr hWnd) {
    ShowWindow(hWnd, SW_RESTORE);
    SetForegroundWindow(hWnd);
  }
}
"@

$hwnd = [System.IntPtr]::new([System.Convert]::ToInt64("00080726", 16))
[Cap]::Bring($hwnd)
[System.Threading.Thread]::Sleep(800)

$screen = [System.Windows.Forms.Screen]::PrimaryScreen
$sw = $screen.Bounds.Width
$sh = $screen.Bounds.Height
$bmp = New-Object System.Drawing.Bitmap $sw, $sh
$gr = [System.Drawing.Graphics]::FromImage($bmp)
$gr.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size($sw, $sh)), [System.Drawing.CopyPixelOperation]::SourceCopy)
$out = "d:\workspace\akashic-records\tmp_capture.png"
$bmp.Save($out, "png")
$gr.Dispose(); $bmp.Dispose()
Write-Output "SAVED:$out W=$sw H=$sh"