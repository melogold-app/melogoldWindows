<#
  Скриншот окна Melogold для отчёта по срезу (docs/PROMPT.md §2).
  Запускает отладочную сборку с отдельной папкой данных, ждёт, снимает окно и закрывает приложение.
    powershell -File tools/shot.ps1 -Out shot.png [-Args "melogold://..."] [-Wait 6] [-Keep] [-Width 1280 -Height 820] [-Theme Dark]
#>
param(
    [string]$Out = "shot.png",
    [string]$Args = "",
    [int]$Wait = 6,
    [switch]$Keep,
    [string]$Exe = "$PSScriptRoot\..\src\Melogold.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Melogold.exe",
    [string]$DataDir = "$env:TEMP\melogold-dev",
    [string]$Lang = ""
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
}
"@

$env:MELOGOLD_DATA_DIR = $DataDir
$env:MELOGOLD_LANG = $Lang
$running = Get-Process Melogold -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Resolve-Path $Exe).Path }
if ($running) { $running | Stop-Process -Force; Start-Sleep -Milliseconds 500 }
$p = if ($Args) { Start-Process $Exe -ArgumentList $Args -PassThru } else { Start-Process $Exe -PassThru }
$deadline = (Get-Date).AddSeconds(20)
while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200; $p.Refresh() }
Start-Sleep -Seconds $Wait
$h = $p.MainWindowHandle
[Win]::ShowWindow($h, 9) | Out-Null
[Win]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 700
$r = New-Object Win+RECT
[Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
$w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save((Join-Path (Get-Location) $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
if (-not $Keep) { Stop-Process -Id $p.Id -Force }
"saved $Out ${w}x$hgt"
