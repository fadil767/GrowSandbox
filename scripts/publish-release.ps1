param(
    [switch]$SkipGitPush
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RootDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$GitExe = if (Get-Command git -ErrorAction SilentlyContinue) { 'git' } else { 'C:\Program Files\Git\cmd\git.exe' }

Set-Location $RootDir

if (-not $SkipGitPush) {
    Write-Host '[1/4] Push commit terbaru ke GitHub...' -ForegroundColor Cyan
    & $GitExe push origin main
    if ($LASTEXITCODE -ne 0) { throw 'git push gagal.' }
}

$commitSha = (& $GitExe rev-parse --short HEAD).Trim()
$commitSubject = (& $GitExe log -1 --pretty=%s).Trim()
$timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
$versionTag = "$commitSha-$((Get-Date).ToString('yyyyMMddHHmm'))"

Write-Host "[2/4] Publish Release win-x64 client (versi $versionTag)..." -ForegroundColor Cyan
$pubRoot = Join-Path $RootDir 'publish'
$stageDir = Join-Path $pubRoot 'GrowSandbox-Client-stage'
$finalDir = Join-Path $pubRoot 'GrowSandbox-Client'
$zipPath = Join-Path $pubRoot 'GrowSandbox-Client-win64.zip'
$versionFile = Join-Path $pubRoot 'version.txt'

Remove-Item -Recurse -Force $stageDir -ErrorAction SilentlyContinue
dotnet publish (Join-Path $RootDir 'src\GrowSandbox.Client\GrowSandbox.Client.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $stageDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish gagal.' }

# Salin file pendukung dari packaging/ + tulis version.txt
Copy-Item -Path (Join-Path $RootDir 'packaging\*') -Destination $stageDir -Force
Set-Content -Path (Join-Path $stageDir 'version.txt') -Value $versionTag -NoNewline -Encoding UTF8
Set-Content -Path $versionFile -Value $versionTag -NoNewline -Encoding UTF8

Remove-Item -Recurse -Force $finalDir -ErrorAction SilentlyContinue
Rename-Item -Path $stageDir -NewName 'GrowSandbox-Client'

Write-Host '[3/4] Membuat arsip GrowSandbox-Client-win64.zip...' -ForegroundColor Cyan
Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($finalDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

Write-Host '[4/4] Mengunggah ke GitHub Releases (tag: latest)...' -ForegroundColor Cyan
$releaseNotes = @"
Build otomatis GrowSandbox Client (`win-x64`, self-contained).

- **Commit:** ``$commitSha`` — $commitSubject
- **Waktu Build:** $timestamp WIB
- **Cara Update untuk Teman:** Cukup klik dua kali ``Update_dan_Main.bat`` di dalam folder game, atau unduh ulang ``GrowSandbox-Client-win64.zip`` di bawah.
"@

$exists = $true
gh release view latest 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { $exists = $false }

if ($exists) {
    gh release edit latest --title "GrowSandbox Latest ($commitSha)" --notes $releaseNotes --latest
    gh release upload latest $zipPath $versionFile --clobber
}
else {
    gh release create latest $zipPath $versionFile --title "GrowSandbox Latest ($commitSha)" --notes $releaseNotes --latest
}
if ($LASTEXITCODE -ne 0) { throw 'Upload GitHub Release gagal.' }

Write-Host ''
Write-Host 'SUKSES! Rilis terbaru sudah live di GitHub Releases:' -ForegroundColor Green
Write-Host 'https://github.com/fadil767/GrowSandbox/releases/latest/download/GrowSandbox-Client-win64.zip' -ForegroundColor Yellow
