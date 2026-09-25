<#
  Релиз Melogold для Windows (docs/PROMPT.md §3): обе архитектуры (x64, ARM64) → установщики Inno Setup →
  dist/update.json (формат Android + assets по архитектурам) → с -Publish релиз vX.Y.Z на GitHub (--latest).

    powershell -File scripts/release.ps1            # только собрать в dist/
    powershell -File scripts/release.ps1 -Publish   # собрать и опубликовать

  Версия — одна, в Directory.Build.props (MelogoldVersion). «Что нового»: release-notes/<версия>.ru.md и .en.md.
  Публикация — через gh, если он есть, иначе через API GitHub с токеном из Git Credential Manager. Подписи кода нет.
#>
param(
    [switch]$Publish,
    [string]$Iscc = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.MelogoldVersion } | Select-Object -First 1).MelogoldVersion
if (-not $version) { throw "MelogoldVersion not found in Directory.Build.props" }
Write-Host "Melogold $version"

if (-not $Iscc) {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    $Iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { throw "Inno Setup 6 (ISCC.exe) not found: install it or pass -Iscc" }
}

$dist = Join-Path $root "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null
$assets = [ordered]@{}

foreach ($arch in @("x64", "arm64")) {
    $platform = if ($arch -eq "arm64") { "ARM64" } else { "x64" }
    $publishDir = Join-Path $root "artifacts\publish\$arch"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    Write-Host "Publishing $arch..."
    dotnet publish src/Melogold.App/Melogold.App.csproj -c Release -r "win-$arch" -p:Platform=$platform --self-contained -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $arch failed" }

    Write-Host "Installer $arch..."
    & $Iscc /Q "/DAppVersion=$version" "/DSourceDir=$publishDir" "/DOutputDir=$dist" "/DArch=$arch" (Join-Path $root "installer\Melogold.iss")
    if ($LASTEXITCODE -ne 0) { throw "ISCC $arch failed" }

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
Write-Host "dist/update.json written"

if (-not $Publish) { return }

# Тег должен указывать на коммит, который уже есть на GitHub
$commit = (git rev-parse HEAD).Trim()
if (-not (git branch -r --contains $commit)) { throw "Push $commit first: the release tag must point at a commit GitHub has" }
$tag = "v$version"
$notesText = (@($notes.ru, $notes.en) | Where-Object { $_ }) -join "`n`n"
$files = @((Get-ChildItem $dist -Filter "*.exe").FullName) + (Join-Path $dist "update.json")

if (Get-Command gh -ErrorAction SilentlyContinue) {
    $notesFile = New-TemporaryFile
    [System.IO.File]::WriteAllText($notesFile, $notesText, (New-Object System.Text.UTF8Encoding $false))
    gh release create $tag @files --repo melogold-app/melogoldWindows --target $commit --title "Melogold $version" --notes-file $notesFile --latest
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
    Remove-Item $notesFile
    return
}

# Без gh: API GitHub, токен — из Git Credential Manager (тот же, которым делается push)
$credential = "protocol=https`nhost=github.com`n`n" | git credential fill
$token = ($credential | Where-Object { $_ -like "password=*" }) -replace "^password=", ""
if (-not $token) { throw "No GitHub token: install gh or sign in to Git Credential Manager" }
$headers = @{ Authorization = "Bearer $token"; Accept = "application/vnd.github+json"; "X-GitHub-Api-Version" = "2022-11-28" }
$body = @{ tag_name = $tag; target_commitish = $commit; name = "Melogold $version"; body = $notesText; make_latest = "true" } | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/melogold-app/melogoldWindows/releases" -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json; charset=utf-8"
foreach ($file in $files) {
    $name = Split-Path $file -Leaf
    $type = if ($name -like "*.json") { "application/json" } else { "application/octet-stream" }
    $uploadUri = "https://uploads.github.com/repos/melogold-app/melogoldWindows/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($name))"
    Invoke-RestMethod -Method Post -Uri $uploadUri -Headers $headers -InFile $file -ContentType $type | Out-Null
    Write-Host "  uploaded $name"
}
Write-Host "Published $($release.html_url)"
