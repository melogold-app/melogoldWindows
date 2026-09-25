<#
  Релиз Melogold для Windows (docs/PROMPT.md §3): обе архитектуры (x64, ARM64) → установщики Inno Setup →
  dist/update.json (формат Android + assets по архитектурам) → с -Publish релиз vX.Y.Z на GitHub (--latest).

    powershell -File scripts/release.ps1                      # только собрать в dist/
    powershell -File scripts/release.ps1 -Publish             # собрать и опубликовать из этой папки
    powershell -File scripts/release.ps1 -Publish -FromMain   # то же из чистой копии origin/main (..\mg-release)

  Быстро: одна общая восстановка пакетов, x64 и ARM64 собираются и упаковываются одновременно, тесты идут рядом
  (ждать CI не нужно), -FromMain держит постоянную копию — промежуточные файлы и ReadyToRun не собираются заново.
  Публикация атомарна: черновик → файлы → «последний релиз»; update.json не появится раньше установщиков.

  Версия — одна, в Directory.Build.props (MelogoldVersion). «Что нового»: release-notes/<версия>.ru.md и .en.md.
  Публикация — через API GitHub с токеном из Git Credential Manager. Подписи кода нет.
#>
param(
    [switch]$Publish,
    [switch]$FromMain,
    [switch]$SkipTests,
    [string]$Iscc = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$timer = [System.Diagnostics.Stopwatch]::StartNew()
function Step([string]$text) { Write-Host ("[{0:mm\:ss}] {1}" -f $timer.Elapsed, $text) }

if ($FromMain) {
    # Чистая копия origin/main рядом с репозиторием: рабочие правки в релиз не попадают, obj и bin остаются между релизами
    $copy = Join-Path (Split-Path $root -Parent) "mg-release"
    git fetch -q origin
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed" }
    if (Test-Path (Join-Path $copy ".git")) {
        git -C $copy checkout -q --detach -f origin/main
        git -C $copy clean -q -fd
    }
    else {
        if (Test-Path $copy) { Remove-Item $copy -Recurse -Force }
        git worktree prune
        git worktree add -q --detach $copy origin/main
    }
    if ($LASTEXITCODE -ne 0) { throw "release copy failed" }
    $forward = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $copy "scripts\release.ps1"))
    if ($Publish) { $forward += "-Publish" }
    if ($SkipTests) { $forward += "-SkipTests" }
    if ($Iscc) { $forward += @("-Iscc", $Iscc) }
    & powershell @forward
    exit $LASTEXITCODE
}

[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.MelogoldVersion } | Select-Object -First 1).MelogoldVersion
if (-not $version) { throw "MelogoldVersion not found in Directory.Build.props" }
Step "Melogold $version ($((git rev-parse --short HEAD).Trim()))"

if (-not $Iscc) {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    $Iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { throw "Inno Setup 6 (ISCC.exe) not found: install it or pass -Iscc" }
}

$dist = Join-Path $root "dist"
$logs = Join-Path $root "artifacts\logs"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist, $logs -Force | Out-Null

# Процесс в фоне с журналом в artifacts\logs; Handle — чтобы после выхода был ExitCode
function Start-Logged([string]$name, [string]$file, [string[]]$arguments) {
    $process = Start-Process $file -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $logs "$name.log") -RedirectStandardError (Join-Path $logs "$name.err.log")
    $null = $process.Handle
    return $process
}

function Wait-Logged([string]$name, $process) {
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        Get-Content (Join-Path $logs "$name.log") -Tail 30 | Write-Host
        Get-Content (Join-Path $logs "$name.err.log") -Tail 10 | Write-Host
        throw "$name failed (exit $($process.ExitCode)), log: artifacts\logs\$name.log"
    }
    Step "$name done"
}

# Пакеты — один раз для обеих архитектур и тестов: параллельные сборки не пишут одни и те же obj\project.assets.json
Step "Restore"
dotnet restore src/Melogold.App/Melogold.App.csproj -p:Configuration=Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "restore failed" }
if (-not $SkipTests) {
    dotnet restore tests/Melogold.Tests --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "restore tests failed" }
}

Step "Build x64 + arm64$(if (-not $SkipTests) { ' + tests' })"
$builds = [ordered]@{}
foreach ($arch in @("x64", "arm64")) {
    $platform = if ($arch -eq "arm64") { "ARM64" } else { "x64" }
    $publishDir = Join-Path $root "artifacts\publish\$arch"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    $builds[$arch] = Start-Logged "publish-$arch" "dotnet" @("publish", "src/Melogold.App/Melogold.App.csproj", "-c", "Release", "-r", "win-$arch",
        "-p:Platform=$platform", "--self-contained", "--no-restore", "-o", "`"$publishDir`"", "--nologo")
}
$tests = if (-not $SkipTests) { Start-Logged "tests" "dotnet" @("test", "tests/Melogold.Tests", "-p:Platform=x64", "--no-restore", "--nologo") }

# Установщик архитектуры — сразу, как собрана она сама; две упаковки идут одновременно
$installers = [ordered]@{}
foreach ($arch in $builds.Keys) {
    Wait-Logged "publish-$arch" $builds[$arch]
    $publishDir = Join-Path $root "artifacts\publish\$arch"
    $installers[$arch] = Start-Logged "installer-$arch" $Iscc @("/Q", "/DAppVersion=$version", "`"/DSourceDir=$publishDir`"", "`"/DOutputDir=$dist`"",
        "/DArch=$arch", "`"$(Join-Path $root 'installer\Melogold.iss')`"")
}

$assets = [ordered]@{}
foreach ($arch in $installers.Keys) {
    Wait-Logged "installer-$arch" $installers[$arch]
    # Имя без версии: ссылка releases/latest/download/<имя> всегда ведёт на последнюю версию
    $file = "Melogold-$arch-setup.exe"
    $path = Join-Path $dist $file
    $assets[$arch] = [ordered]@{
        fileName  = $file
        sizeBytes = (Get-Item $path).Length
        sha256    = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    Write-Host ("  {0} · {1:N0} bytes · sha256 {2}" -f $file, $assets[$arch].sizeBytes, $assets[$arch].sha256)
}
if ($tests) { Wait-Logged "tests" $tests }

$notes = [ordered]@{}
foreach ($lang in @("ru", "en")) {
    $notesPath = Join-Path $root "release-notes\$version.$lang.md"
    if (Test-Path $notesPath) { $notes[$lang] = (Get-Content $notesPath -Raw -Encoding UTF8).Trim() }
}
$manifest = [ordered]@{
    version     = $version
    notes       = $notes
    publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    assets      = $assets
}
$json = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText((Join-Path $dist "update.json"), $json, (New-Object System.Text.UTF8Encoding $false))
Step "dist/update.json written"

if (-not $Publish) { return }

# Тег должен указывать на коммит, который уже есть на GitHub
$commit = (git rev-parse HEAD).Trim()
git fetch -q origin
if (-not (git branch -r --contains $commit)) { throw "Push $commit first: the release tag must point at a commit GitHub has" }
$tag = "v$version"
$notesText = (@($notes.ru, $notes.en) | Where-Object { $_ }) -join "`n`n"

# API GitHub, токен — из Git Credential Manager (тот же, которым делается push). Запрос — из файла: конвейер Windows
# PowerShell портит ввод, и git отвечает «missing protocol field»
$request = New-TemporaryFile
[System.IO.File]::WriteAllText($request, "protocol=https`nhost=github.com`n`n", (New-Object System.Text.UTF8Encoding $false))
$credential = cmd /c "git credential fill < `"$request`""
Remove-Item $request
$token = ($credential | Where-Object { $_ -like "password=*" }) -replace "^password=", ""
if (-not $token) { throw "No GitHub token: sign in to Git Credential Manager" }
$headers = @{ Authorization = "Bearer $token"; Accept = "application/vnd.github+json"; "X-GitHub-Api-Version" = "2022-11-28" }
$api = "https://api.github.com/repos/melogold-app/melogoldWindows/releases"

Step "Upload"
$body = @{ tag_name = $tag; target_commitish = $commit; name = "Melogold $version"; body = $notesText; draft = $true } | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Uri $api -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json; charset=utf-8"

# Установщики — одновременно; черновик никто не видит, пока он не опубликован
Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromMinutes(30)
$http.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $token)
$http.DefaultRequestHeaders.UserAgent.ParseAdd("melogold-release")
$uploads = @()
foreach ($file in (Get-ChildItem $dist -Filter "*.exe") + (Get-Item (Join-Path $dist "update.json"))) {
    $content = New-Object System.Net.Http.StreamContent([System.IO.File]::OpenRead($file.FullName))
    $content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue($(if ($file.Extension -eq ".json") { "application/json" } else { "application/octet-stream" }))
    $uri = "https://uploads.github.com/repos/melogold-app/melogoldWindows/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($file.Name))"
    $uploads += [pscustomobject]@{ Name = $file.Name; Task = $http.PostAsync($uri, $content) }
}
foreach ($upload in $uploads) {
    $response = $upload.Task.GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) { throw "Upload $($upload.Name) failed: $([int]$response.StatusCode) $($response.Content.ReadAsStringAsync().Result)" }
    Step "  uploaded $($upload.Name)"
}

# Все файлы на месте — релиз виден всем и становится последним
$body = @{ draft = $false; make_latest = "true" } | ConvertTo-Json
$release = Invoke-RestMethod -Method Patch -Uri "$api/$($release.id)" -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json; charset=utf-8"
Step "Published $($release.html_url)"
