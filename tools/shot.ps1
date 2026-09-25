<#
  Скриншот окна Melogold для отчёта по срезу (docs/PROMPT.md §2).
  Запускает отладочную сборку с отдельной папкой данных, ждёт, снимает только окно Melogold (PrintWindow — даже если
  оно перекрыто другими окнами; чужие окна в снимок не попадают) и закрывает приложение.
    powershell -File tools/shot.ps1 -Out shot.png [-AppArgs "текст или ссылка"] [-Wait 6] [-Keep] [-Lang ru-RU]
      [-Click "История|Чаще всего"]
  -Click нажимает элементы по имени (UI Automation, начало имени) по очереди через «|», без мыши и фокуса.
#>
param(
    [string]$Out = "shot.png",
    [string]$AppArgs = "",
    [int]$Wait = 6,
    [switch]$Keep,
    [string]$Exe = "$PSScriptRoot\..\src\Melogold.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Melogold.exe",
    [string]$DataDir = "$env:TEMP\melogold-dev",
    [string]$Lang = "",
    [string]$Click = ""
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@

[Win]::SetProcessDPIAware() | Out-Null  # размеры окна — в физических пикселях, иначе снимок обрезан
$env:MELOGOLD_DATA_DIR = $DataDir
$env:MELOGOLD_LANG = $Lang
$exePath = (Resolve-Path $Exe).Path
Get-Process Melogold -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath } | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
$p = if ($AppArgs) { Start-Process $Exe -ArgumentList "`"$AppArgs`"" -PassThru } else { Start-Process $Exe -PassThru }
$deadline = (Get-Date).AddSeconds(20)
while ($p.MainWindowHandle -eq 0 -and -not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200; $p.Refresh() }
if ($p.HasExited) { throw "Melogold exited with code $($p.ExitCode)" }
Start-Sleep -Seconds $Wait
$p.Refresh()
if ($p.HasExited) { throw "Melogold exited with code $($p.ExitCode)" }
$h = $p.MainWindowHandle
if ($Click) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    foreach ($name in $Click.Split("|")) {
        $target = $null
        $until = (Get-Date).AddSeconds(10)
        while (-not $target -and (Get-Date) -lt $until) {
            $target = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.Name -like "$name*" -and -not $_.Current.IsOffscreen } | Select-Object -First 1
            if (-not $target) { Start-Sleep -Milliseconds 300 }
        }
        if (-not $target) { throw "element '$name' not found" }
        $pattern = $null
        if ($target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
        elseif ($target.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
        else { throw "element '$name' can't be invoked" }
        Start-Sleep -Seconds 3
    }
}
[Win]::ShowWindow($h, 4) | Out-Null   # SW_SHOWNOACTIVATE: из свёрнутого, без перехвата фокуса
Start-Sleep -Milliseconds 500
$r = New-Object Win+RECT
[Win]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[Win]::PrintWindow($h, $hdc, 2) | Out-Null  # PW_RENDERFULLCONTENT: окно целиком, с содержимым DirectComposition
$g.ReleaseHdc($hdc)
$target = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).Path, $Out))  # $Out может быть абсолютным
$bmp.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
if (-not $Keep) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
"saved $target ${w}x$hgt"
