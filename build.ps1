# ============================================================
# MES Control Center - Build & Installer Script
# Usage: .\build.ps1 -Version "1.0.1"
#        .\build.ps1                     (uses version from installer.iss)
# ============================================================

param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$ProjectPath = "$Root\src\MesControlCenter.UI\MesControlCenter.UI.csproj"
$PublishDir = "$Root\publish"
$InstallerDir = "$Root\installer"
$IssFile = "$Root\installer.iss"
$InnoSetup = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

function Remove-ProjectDirectory {
    param([string]$PathToRemove)

    if (-not (Test-Path $PathToRemove)) { return }

    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
    $resolvedTarget = (Resolve-Path -LiteralPath $PathToRemove).Path
    $isInsideRoot = $resolvedTarget.Equals($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase) `
        -or $resolvedTarget.StartsWith("$resolvedRoot\", [System.StringComparison]::OrdinalIgnoreCase)

    if (-not $isInsideRoot) {
        throw "Refusing to remove path outside project root: $resolvedTarget"
    }

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item -LiteralPath $resolvedTarget -Recurse -Force -ErrorAction Stop
            return
        } catch {
            if ($attempt -eq 3) { throw }
            dotnet build-server shutdown 2>&1 | Out-Null
            Start-Sleep -Seconds 1
        }
    }
}

# ── Read current version from .iss if not provided ──
if ([string]::IsNullOrEmpty($Version)) {
    $match = Select-String -Path $IssFile -Pattern '#define MyAppVersion "(.+)"'
    if ($match) {
        $Version = $match.Matches[0].Groups[1].Value
    } else {
        $Version = "1.0.0"
    }
    Write-Host "[INFO] Using existing version: $Version" -ForegroundColor Cyan
} else {
    # Update version in installer.iss
    $content = Get-Content $IssFile -Raw
    $content = $content -replace '#define MyAppVersion ".+"', "#define MyAppVersion ""$Version"""
    Set-Content $IssFile -Value $content -NoNewline
    Write-Host "[INFO] Updated version to: $Version" -ForegroundColor Green
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  MES Control Center - Build v$Version" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# ── Step 1: Clean ──
Write-Host "[1/4] Cleaning previous build..." -ForegroundColor Yellow
dotnet build-server shutdown 2>&1 | Out-Null
Remove-ProjectDirectory $PublishDir
dotnet clean "$Root\MesControlCenter.sln" --nologo -v q 2>&1 | Out-Null
$artifactDirs = Get-ChildItem -LiteralPath "$Root\src" -Recurse -Directory |
    Where-Object { $_.Name -in @("bin", "obj") }
foreach ($dir in $artifactDirs) {
    Remove-ProjectDirectory $dir.FullName
}
Write-Host "      Done." -ForegroundColor Green

# ── Step 2: Build (Debug check) ──
Write-Host "[2/4] Building solution (Debug)..." -ForegroundColor Yellow
dotnet build "$Root\MesControlCenter.sln" --nologo -c Debug -m:1
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Build failed. Fix errors and try again." -ForegroundColor Red
    exit 1
}
Write-Host "      Build OK." -ForegroundColor Green

# ── Step 3: Publish (Release, self-contained) ──
Write-Host "[3/4] Publishing self-contained executable..." -ForegroundColor Yellow
dotnet publish $ProjectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $PublishDir `
    --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Publish failed." -ForegroundColor Red
    exit 1
}

$exeSize = [math]::Round((Get-Item "$PublishDir\MesControlCenter.UI.exe").Length / 1MB, 1)
Write-Host "      Published: MesControlCenter.UI.exe ($exeSize MB)" -ForegroundColor Green

# ── Step 4: Inno Setup Installer ──
Write-Host "[4/4] Compiling installer..." -ForegroundColor Yellow
if (-not (Test-Path $InnoSetup)) {
    Write-Host "[WARN] Inno Setup not found at: $InnoSetup" -ForegroundColor Red
    Write-Host "       Skipping installer. EXE is at: $PublishDir\MesControlCenter.UI.exe" -ForegroundColor Yellow
} else {
    if (-not (Test-Path $InstallerDir)) { New-Item $InstallerDir -ItemType Directory | Out-Null }

    $setupBase = "MesControlCenter_Setup_v$Version"
    $setupFile = "$setupBase.exe"
    $tempInstallerDir = Join-Path ([System.IO.Path]::GetTempPath()) "MesControlCenterInstaller_$PID"
    $tempSetupPath = Join-Path $tempInstallerDir $setupFile

    if (Test-Path $tempInstallerDir) { Remove-Item $tempInstallerDir -Recurse -Force }
    New-Item $tempInstallerDir -ItemType Directory | Out-Null

    try {
        & $InnoSetup "/O$tempInstallerDir" "/F$setupBase" $IssFile
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[ERROR] Inno Setup failed." -ForegroundColor Red
            exit 1
        }

        if (-not (Test-Path $tempSetupPath)) {
            Write-Host "[ERROR] Inno Setup did not create: $tempSetupPath" -ForegroundColor Red
            exit 1
        }

        $finalSetupPath = Join-Path $InstallerDir $setupFile
        if (Test-Path $finalSetupPath) {
            try {
                Remove-Item $finalSetupPath -Force -ErrorAction Stop
            } catch {
                $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
                $setupFile = "${setupBase}_$stamp.exe"
                $finalSetupPath = Join-Path $InstallerDir $setupFile
                Write-Host "[WARN] Previous installer is locked. Writing: $setupFile" -ForegroundColor Yellow
            }
        }

        Move-Item $tempSetupPath $finalSetupPath -Force
        $setupSize = [math]::Round((Get-Item $finalSetupPath).Length / 1MB, 1)
        Write-Host "      Installer: $setupFile ($setupSize MB)" -ForegroundColor Green
    } finally {
        if (Test-Path $tempInstallerDir) { Remove-Item $tempInstallerDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ── Done ──
Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "  BUILD COMPLETE - v$Version" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "  EXE:       $PublishDir\MesControlCenter.UI.exe"
Write-Host "  Installer: $InstallerDir\MesControlCenter_Setup_v$Version.exe"
Write-Host ""
