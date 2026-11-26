param(
    # Pesan commit (opsional). Jika kosong, akan ditanya / dipakai pesan default.
    [string]$Message = '',
    # Build & upload rilis dari PC ini (fallback jika GitHub Actions tidak tersedia).
    [switch]$Local
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not (Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path 'C:\Program Files\Git\cmd\git.exe')) {
    $env:PATH = "C:\Program Files\Git\cmd;$env:PATH"
}

$Repo = 'fadil767/GrowSandbox'
$RootDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
Set-Location $RootDir

# Mode otomatis: pakai GitHub Actions jika workflow aktif, selain itu build di PC ini.
$UseActions = (-not $Local) -and (Test-Path (Join-Path $RootDir '.github\workflows\release-client.yml'))

# === 1. Commit otomatis semua perubahan ===
$pending = (git status --porcelain)
if ($pending) {
    if ([string]::IsNullOrWhiteSpace($Message)) {
        $Message = Read-Host 'Ketik pesan perubahan (Enter = "Update game")'
        if ([string]::IsNullOrWhiteSpace($Message)) { $Message = 'Update game' }
    }

    Write-Host "[1/3] Commit perubahan: $Message" -ForegroundColor Cyan
    git add -A
    git commit -m $Message
    if ($LASTEXITCODE -ne 0) { throw 'git commit gagal.' }
}
else {
    Write-Host '[1/3] Tidak ada perubahan baru untuk di-commit.' -ForegroundColor Gray
}

# === 2. Push ke GitHub ===
Write-Host '[2/3] Push ke GitHub...' -ForegroundColor Cyan
git push origin main
if ($LASTEXITCODE -ne 0) { throw 'git push gagal.' }

if ($UseActions) {
    Write-Host '[3/3] GitHub Actions akan otomatis build & merilis ZIP terbaru (~3-5 menit).' -ForegroundColor Green
    Write-Host "      Pantau progres: https://github.com/$Repo/actions" -ForegroundColor Yellow
    Write-Host "      Link ZIP teman : https://github.com/$Repo/releases/latest/download/GrowSandbox-Client-win64.zip" -ForegroundColor Yellow
    return
}

# === 3. (Opsional) Build & upload rilis dari PC lokal ===
$commitSha = (git rev-parse --short HEAD).Trim()
$commitSubject = (git log -1 --pretty=%s).Trim()
$versionTag = "$commitSha-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmm'))"

Write-Host "[3/3] Build lokal & upload rilis (versi $versionTag)..." -ForegroundColor Cyan
$pubRoot = Join-Path $RootDir 'publish'
$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("GrowSandboxRelease_" + [Guid]::NewGuid().ToString('N'))
$stageDir = Join-Path $stageRoot 'GrowSandbox-Client'
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

    Copy-Item -Path (Join-Path $RootDir 'packaging\*') -Destination $stageDir -Force
    [System.IO.File]::WriteAllText((Join-Path $stageDir 'version.txt'), $versionTag, $Utf8NoBom)
    [System.IO.File]::WriteAllText($versionFile, $versionTag, $Utf8NoBom)

    Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
}
finally {
    Remove-Item -Recurse -Force $stageRoot -ErrorAction SilentlyContinue
}

$notes = "Build lokal GrowSandbox Client (win-x64, self-contained).`n`n- Commit: $commitSha - $commitSubject`n- Versi: $versionTag`n- Cara update teman: klik dua kali Update_dan_Main.bat, atau unduh ulang ZIP di bawah."

$prevPref = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$null = & gh release view latest --repo $Repo 2>&1
$exists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevPref

if ($exists) {
    & gh release edit latest --repo $Repo --title "GrowSandbox Latest ($commitSha)" --notes $notes --latest
    if ($LASTEXITCODE -ne 0) { throw 'Edit GitHub Release gagal.' }
    & gh release upload latest $zipPath $versionFile --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Upload asset GitHub Release gagal.' }
}
else {
    & gh release create latest $zipPath $versionFile --repo $Repo --title "GrowSandbox Latest ($commitSha)" --notes $notes --latest
    if ($LASTEXITCODE -ne 0) { throw 'Create GitHub Release gagal.' }
}

Write-Host ''
Write-Host 'SUKSES! Rilis terbaru sudah live:' -ForegroundColor Green
Write-Host "https://github.com/$Repo/releases/latest/download/GrowSandbox-Client-win64.zip" -ForegroundColor Yellow
