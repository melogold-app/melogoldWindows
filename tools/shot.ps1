<#
  Скриншот окна Melogold для отчёта по срезу (docs/PROMPT.md §2).
  Запускает отладочную сборку с отдельной папкой данных, ждёт, снимает только окно Melogold и закрывает приложение.
  Тихо (MELOGOLD_QUIET=1, QuietMode): окно правее всех мониторов, без фокуса, без кнопки на панели задач, без звука и
  медиапанели Windows. Но шаги -Steps (нажатие, ввод через UI Automation) всё равно ставят фокус клавиатуры в окно
  Melogold: набор пользователя уходит туда (2026-09-26). Пока пользователь за компьютером — только с его согласия.
  Снимок делает само приложение (DebugSnapshot, RenderTargetBitmap): чужие окна в него не попадают, и он не зависит
  от экрана — при выключенном экране PrintWindow отдаёт чёрный или устаревший кадр. -Window снимает окно снаружи
  (PrintWindow, с Mica и кнопками заголовка).
    powershell -File tools/shot.ps1 -Out shot.png [-AppArgs "текст или ссылка"] [-Wait 6] [-Keep] [-Lang ru-RU]
      [-Steps "История|Чаще всего|@history.png|Логин=value"]
  -Steps — шаги через «|» по UI Automation, без мыши:
    «имя» нажимает элемент (точное имя, иначе начало имени; «имя#2» — второй одноимённый), «имя=текст» вводит текст в поле,
    «@файл.png» снимает окно посреди сценария, «@mini:файл.png» — мини-плеер, «!max» и «!restore» — окно размером с
    рабочую область основного монитора и обычное 1280×820 (не сдвигая с места за экраном и не активируя), «!size:500x700» — окно такого размера (эффективные пиксели, как в XAML), «+имя» добавляет строку
    списка к выделению (как Ctrl+щелчок), «!wait:5» ждёт 5 с,
    «!show:имя» прокручивает до элемента. В конце окно снимается в -Out.
#>
param(
    [string]$Out = "shot.png",
    [string]$AppArgs = "",
    [int]$Wait = 6,
    [switch]$Keep,
    [string]$Exe = "$PSScriptRoot\..\src\Melogold.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Melogold.exe",
    [string]$DataDir = "$env:TEMP\melogold-dev",
    [string]$Lang = "",
    [string]$Steps = "",
    [switch]$Window,
    [switch]$Visible
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
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@

function Capture([IntPtr]$h, [string]$path) {
    $request = "shot-request"
    if ($path.StartsWith("mini:")) { $request = "shot-request-mini"; $path = $path.Substring(5) }
    $target = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).Path, $path))  # путь может быть абсолютным
    if (-not $Window) {
        Remove-Item $target -ErrorAction SilentlyContinue
        Set-Content -Path (Join-Path $DataDir $request) -Value $target -Encoding UTF8
        $until = (Get-Date).AddSeconds(10)
        while (-not (Test-Path $target) -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-Path $target)) { throw "snapshot not saved: $target" }
        return "saved $target"
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
    $bmp.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "saved $target ${w}x$hgt"
}

function Test-Actionable($e) {
    foreach ($p in @("IsInvokePatternAvailableProperty", "IsTogglePatternAvailableProperty", "IsSelectionItemPatternAvailableProperty", "IsExpandCollapsePatternAvailableProperty", "IsValuePatternAvailableProperty")) {
        if ($e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::$p)) { return $true }
    }
    return $false
}

# Меню «…» и выпадающие списки WinUI — отдельные окна того же процесса: ищем сначала в них, потом в окне
function Search-Roots($root) {
    $process = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $root.Current.ProcessId)
    $popups = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $process) |
        Where-Object { $_.Current.NativeWindowHandle -ne $root.Current.NativeWindowHandle }
    @($popups) + @($root)
}

function Find-Element($root, [string]$name, [switch]$Offscreen) {
    # «Текст#2» — второй из одноимённых нажимаемых элементов (кнопка плеера «Текст» и вкладка редактора «Текст»)
    $nth = 1
    if ($name -match '^(.+)#(\d+)$') { $name = $Matches[1]; $nth = [int]$Matches[2] }
    $until = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $until) {
        $all = foreach ($r in Search-Roots $root) {
            $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $Offscreen -or -not $_.Current.IsOffscreen }
        }
        # Среди одноимённых — сначала то, что нажимается (кнопка «Текст», а не подпись с тем же словом)
        $exact = @($all | Where-Object { $_.Current.Name -eq $name })
        $found = $exact | Where-Object { Test-Actionable $_ } | Select-Object -Skip ($nth - 1) -First 1
        if ($nth -gt 1) { if ($found) { return $found } else { Start-Sleep -Milliseconds 300; continue } }
        # Кнопка с именем «Все треки, 195 треков» важнее подписи «Все треки» внутри неё
        if (-not $found) { $found = $all | Where-Object { $_.Current.Name -like "$name*" -and (Test-Actionable $_) } | Select-Object -First 1 }
        if (-not $found) { $found = $exact | Select-Object -First 1 }
        if (-not $found) { $found = $all | Where-Object { $_.Current.Name -like "$name*" } | Select-Object -First 1 }
        if ($found) { return $found }
        Start-Sleep -Milliseconds 300
    }
    # Что есть похожего: опечатка, другое имя или элемент ещё не появился
    $word = $name.Split(" ")[0]
    $similar = foreach ($r in Search-Roots $root) { $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }
    $similar | Where-Object { $_.Current.Name -like "*$word*" } | Select-Object -First 10 |
        ForEach-Object { "  seen: $($_.Current.ControlType.ProgrammaticName) '$($_.Current.Name)' offscreen=$($_.Current.IsOffscreen)" } | Write-Host
    throw "element '$name' not found"
}

[Win]::SetProcessDPIAware() | Out-Null  # размеры окна — в физических пикселях, иначе снимок обрезан
$env:MELOGOLD_DATA_DIR = $DataDir
$env:MELOGOLD_LANG = $Lang
$env:MELOGOLD_QUIET = if ($Visible) { "0" } else { "1" }   # -Visible: обычное окно на экране — только когда пользователь разрешил
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
try {
if ($Steps) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    foreach ($step in $Steps.Split("|")) {
        if ($step.StartsWith("@")) { Capture $h $step.Substring(1); continue }
        if ($step.StartsWith("!show:")) {
            # Прокрутить до элемента (ScrollItemPattern), не нажимая его
            $pattern = $null
            $target = Find-Element $root $step.Substring(6) -Offscreen
            if ($target.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) { $pattern.ScrollIntoView() }
            Start-Sleep -Seconds 1
            continue
        }
        if ($step.StartsWith("!wait:")) { Start-Sleep -Seconds ([int]$step.Substring(6)); continue }
        if ($step.StartsWith("+")) {
            # Добавить строку списка к выделению (SelectionItemPattern.AddToSelection) — как Ctrl+щелчок
            $pattern = $null
            $target = Find-Element $root $step.Substring(1)
            if ($target.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.AddToSelection() }
            Start-Sleep -Milliseconds 500
            continue
        }
        # Размеры — без перемещения и без фокуса (SW_MAXIMIZE и SW_RESTORE вытащили бы окно на экран и забрали фокус)
        if ($step -eq "!max") {
            Add-Type -AssemblyName System.Windows.Forms
            $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
            [Win]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, $area.Width, $area.Height, 0x0016) | Out-Null   # NOMOVE|NOZORDER|NOACTIVATE
            Start-Sleep -Seconds 2; continue
        }
        if ($step -eq "!restore") { $step = "!size:1280x820" }
        if ($step.StartsWith("!size:")) {
            # Размер в эффективных пикселях: умножается на масштаб экрана окна
            $wh = $step.Substring(6).Split("x"); $scale = [Win]::GetDpiForWindow($h) / 96.0
            [Win]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, [int]([int]$wh[0] * $scale), [int]([int]$wh[1] * $scale), 0x0016) | Out-Null   # NOMOVE|NOZORDER|NOACTIVATE
            Start-Sleep -Seconds 2; continue
        }
        $pattern = $null
        if ($step.Contains("=")) {
            $name, $value = $step.Split("=", 2)
            $target = Find-Element $root $name
            if (-not $target.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { throw "element '$name' takes no text" }
            $pattern.SetValue($value)
            Start-Sleep -Milliseconds 500
            continue
        }
        $target = Find-Element $root $step
        if ($target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
        elseif ($target.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle() }
        elseif ($target.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
        elseif ($target.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) { $pattern.Expand() }
        else { throw "element '$step' can't be invoked" }
        Start-Sleep -Seconds 3
    }
}
Capture $h $Out
}
finally {
    # И после ошибки шага: окно не остаётся висеть и не держит exe для следующей сборки
    if (-not $Keep -and -not $p.HasExited) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
}
