$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Repo = 'fadil767/GrowSandbox'
$VersionUrl = "https://github.com/$Repo/releases/latest/download/version.txt"
$ZipUrl = "https://github.com/$Repo/releases/latest/download/GrowSandbox-Client-win64.zip"

$AppDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$LocalVersionFile = Join-Path $AppDir 'version.txt'
$ExePath = Join-Path $AppDir 'GrowSandbox.Client.exe'

Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  GROWSANDBOX AUTO-UPDATER & LAUNCHER   ' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

function Get-CleanVersion([object]$raw) {
    if ($null -eq $raw) { return '' }
    # Buang BOM / karakter aneh agar perbandingan versi selalu akurat
    return ([string]$raw -replace '[^0-9A-Za-z\-\.]', '')
}

$localVersion = if (Test-Path $LocalVersionFile) { Get-CleanVersion ([System.IO.File]::ReadAllText($LocalVersionFile)) } else { '' }

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Write-Host 'Memeriksa update terbaru dari GitHub...' -ForegroundColor Gray
    $remoteVersion = Get-CleanVersion (Invoke-RestMethod -Uri $VersionUrl -TimeoutSec 8)

    if (-not [string]::IsNullOrWhiteSpace($remoteVersion) -and $remoteVersion -ne $localVersion) {
        Write-Host "Update baru ditemukan! ($remoteVersion)" -ForegroundColor Yellow

        $running = Get-Process -Name 'GrowSandbox.Client' -ErrorAction SilentlyContinue
        if ($running) {
            Write-Host 'Tutup dulu jendela GrowSandbox yang sedang berjalan, lalu jalankan ulang launcher ini.' -ForegroundColor Red
            Read-Host 'Tekan Enter untuk keluar'
            exit 1
        }

        $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("GrowSandboxUpdate_" + [Guid]::NewGuid().ToString('N'))
        $zipPath = Join-Path $tempRoot 'update.zip'
        $extractDir = Join-Path $tempRoot 'extracted'
        New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

        try {
            Write-Host 'Mengunduh versi terbaru (~34 MB)...' -ForegroundColor Cyan
            Invoke-WebRequest -Uri $ZipUrl -OutFile $zipPath -UseBasicParsing

            Write-Host 'Mengekstrak file update...' -ForegroundColor Cyan
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extractDir)

            $sourceDir = Join-Path $extractDir 'GrowSandbox-Client'
            if (-not (Test-Path $sourceDir)) {
                $sourceDir = $extractDir
            }

            $hadConfig = Test-Path (Join-Path $AppDir 'client_config.json')

            Get-ChildItem -Path $sourceDir -Force | ForEach-Object {
                if ($_.Name -eq 'client_config.json' -and $hadConfig) {
                    return
                }
                Copy-Item -Path $_.FullName -Destination (Join-Path $AppDir $_.Name) -Recurse -Force
            }

            [System.IO.File]::WriteAllText($LocalVersionFile, $remoteVersion, (New-Object System.Text.UTF8Encoding($false)))
            Write-Host 'Update selesai! Menjalankan GrowSandbox...' -ForegroundColor Green
        }
        finally {
            Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        Write-Host 'Versi game sudah paling baru!' -ForegroundColor Green
    }
}
catch {
    Write-Host "Tidak dapat mengecek update online ($($_.Exception.Message))." -ForegroundColor DarkYellow
    Write-Host 'Menjalankan versi lokal yang sudah terpasang...' -ForegroundColor Gray
}

if (Test-Path $ExePath) {
    Start-Process -FilePath $ExePath -WorkingDirectory $AppDir
}
else {
    Write-Host 'File GrowSandbox.Client.exe tidak ditemukan!' -ForegroundColor Red
    Read-Host 'Tekan Enter untuk keluar'
    exit 1
}
