# Builds the Windows x64 installer and its checksum:
#   1. publishes Virtualized like Rider's "Virtualized Windows x64" profile (Release, self-contained, single file)
#   2. compiles setup.iss with Inno Setup, passing it the version from Virtualized.csproj
#   3. writes Output\<installer>.exe.sha256 ("<hash>  <file name>", as sha256sum prints it), to publish next to the download
# Usage: powershell -ExecutionPolicy Bypass -File build-installer.ps1 [-SkipPublish] [-Iscc <path to ISCC.exe>]

param(
    [switch]$SkipPublish,
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$project = Join-Path $installerDir '..\Virtualized\Virtualized.csproj'
$publishDir = Join-Path $installerDir '..\Virtualized\bin\Release\net11.0\win-x64\publish'
$script = Join-Path $installerDir 'setup.iss'

if (-not $Iscc) {
    $candidates = @(
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    )
    $Iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { throw 'ISCC.exe not found; install Inno Setup or pass -Iscc.' }
}

if (-not $SkipPublish) {
    # Rider's profile deletes the folder first, so nothing stale ends up in the installer.
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    & dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
}

# The version is set only in Virtualized.csproj; the installer's name carries it (1.2.0 -> ..._1-2-0) and the
# in-app updater looks for "virtualized_setup_w64_*.exe" and its .sha256 in the GitHub release.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version not found in Virtualized.csproj.' }
$baseName = "virtualized_setup_w64_$($version -replace '\.', '-')"

& $Iscc "/DMyAppVersion=$version" "/F$baseName" $script
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

$installer = Join-Path $installerDir "Output\$baseName.exe"
$hash = (Get-FileHash -Algorithm SHA256 $installer).Hash.ToLowerInvariant()
$checksumFile = "$installer.sha256"
# Plain ASCII without BOM, so sha256sum -c and other tools read it too.
[System.IO.File]::WriteAllText($checksumFile, "$hash  $baseName.exe`n", [System.Text.Encoding]::ASCII)

Write-Host ''
Write-Host "Installer: $installer"
Write-Host "SHA-256:   $hash"
Write-Host "Written to $checksumFile"
Write-Host ''
Write-Host "To publish: create the GitHub release v$version and upload both files to it."
