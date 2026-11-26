param(
    [switch]$SkipGitPush
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not (Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path 'C:\Program Files\Git\cmd\git.exe')) {
    $env:PATH = "C:\Program Files\Git\cmd;$env:PATH"
}

$Repo = 'fadil767/GrowSandbox'
$RootDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$GitExe = 'git'

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
$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("GrowSandboxRelease_" + [Guid]::NewGuid().ToString('N'))
$stageDir = Join-Path $stageRoot 'GrowSandbox-Client'
$finalDir = Join-Path $pubRoot 'GrowSandbox-Client'
$zipPath = Join-Path $pubRoot 'GrowSandbox-Client-win64.zip'
$versionFile = Join-Path $pubRoot 'version.txt'

New-Item -ItemType Directory -Path $pubRoot -Force | Out-Null
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

try {
    dotnet publish (Join-Path $RootDir 'src\GrowSandbox.Client\GrowSandbox.Client.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $stageDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish gagal.' }

    # Salin file pendukung dari packaging/ + tulis version.txt
    Copy-Item -Path (Join-Path $RootDir 'packaging\*') -Destination $stageDir -Force
    Set-Content -Path (Join-Path $stageDir 'version.txt') -Value $versionTag -NoNewline -Encoding UTF8
    Set-Content -Path $versionFile -Value $versionTag -NoNewline -Encoding UTF8

    Write-Host '[3/4] Membuat arsip GrowSandbox-Client-win64.zip...' -ForegroundColor Cyan
    Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

    # Sinkronkan juga folder publish\GrowSandbox-Client (best-effort jika tidak sedang dibuka di Explorer)
    try {
        New-Item -ItemType Directory -Path $finalDir -Force | Out-Null
        Copy-Item -Path (Join-Path $stageDir '*') -Destination $finalDir -Recurse -Force -ErrorAction SilentlyContinue
    } catch { }
}
finally {
    Remove-Item -Recurse -Force $stageRoot -ErrorAction SilentlyContinue
}

Write-Host '[4/4] Mengunggah ke GitHub Releases (tag: latest)...' -ForegroundColor Cyan
$releaseNotes = @"
Build otomatis GrowSandbox Client (`win-x64`, self-contained).

- **Commit:** ``$commitSha`` — $commitSubject
- **Waktu Build:** $timestamp WIB
- **Cara Update untuk Teman:** Cukup klik dua kali ``Update_dan_Main.bat`` di dalam folder game, atau unduh ulang ``GrowSandbox-Client-win64.zip`` di bawah.
"@

$prevPref = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$null = & gh release view latest --repo $Repo 2>&1
$exists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevPref

if ($exists) {
    & gh release edit latest --repo $Repo --title "GrowSandbox Latest ($commitSha)" --notes $releaseNotes --latest
    if ($LASTEXITCODE -ne 0) { throw 'Edit GitHub Release gagal.' }
    & gh release upload latest $zipPath $versionFile --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Upload asset GitHub Release gagal.' }
}
else {
    & gh release create latest $zipPath $versionFile --repo $Repo --title "GrowSandbox Latest ($commitSha)" --notes $releaseNotes --latest
    if ($LASTEXITCODE -ne 0) { throw 'Create GitHub Release gagal.' }
}

Write-Host ''
Write-Host 'SUKSES! Rilis terbaru sudah live di GitHub Releases:' -ForegroundColor Green
Write-Host 'https://github.com/fadil767/GrowSandbox/releases/latest/download/GrowSandbox-Client-win64.zip' -ForegroundColor Yellow
