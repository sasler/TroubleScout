[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $ReleaseTag,

    [string] $ManifestDir,

    [string] $X64InstallerUrl,

    [string] $Arm64InstallerUrl,

    [string] $WingetPkgsRoot,

    [switch] $RunSandbox,

    [switch] $KeepDownloads
)

$ErrorActionPreference = 'Stop'
$manifestSchemaVersion = '1.12.0'

if ([string]::IsNullOrWhiteSpace($ReleaseTag)) {
    $ReleaseTag = if ($Version.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        $Version
    } else {
        "v$Version"
    }
}

if ($ReleaseTag.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
    $normalizedVersion = $ReleaseTag.Substring(1)
} else {
    $normalizedVersion = $ReleaseTag
}

if ($normalizedVersion -ne $Version) {
    Write-Verbose "Using version '$normalizedVersion' derived from release tag '$ReleaseTag'."
}

$Version = $normalizedVersion

if ([string]::IsNullOrWhiteSpace($ManifestDir)) {
    $ManifestDir = Join-Path $env:TEMP "TroubleScout-winget\$Version"
}

if ([string]::IsNullOrWhiteSpace($X64InstallerUrl)) {
    $X64InstallerUrl = "https://github.com/sasler/TroubleScout/releases/download/$ReleaseTag/TroubleScout-$ReleaseTag-win-x64.zip"
}

if ([string]::IsNullOrWhiteSpace($Arm64InstallerUrl)) {
    $Arm64InstallerUrl = "https://github.com/sasler/TroubleScout/releases/download/$ReleaseTag/TroubleScout-$ReleaseTag-win-arm64.zip"
}

$downloadDir = Join-Path $ManifestDir "_downloads"

New-Item -ItemType Directory -Path $ManifestDir -Force | Out-Null
New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null

$x64Zip = Join-Path $downloadDir "TroubleScout-$ReleaseTag-win-x64.zip"
$arm64Zip = Join-Path $downloadDir "TroubleScout-$ReleaseTag-win-arm64.zip"

Write-Host "Downloading x64 release asset..."
Invoke-WebRequest -Uri $X64InstallerUrl -OutFile $x64Zip

Write-Host "Downloading arm64 release asset..."
Invoke-WebRequest -Uri $Arm64InstallerUrl -OutFile $arm64Zip

$x64Hash = (Get-FileHash -Path $x64Zip -Algorithm SHA256).Hash.ToUpperInvariant()
$arm64Hash = (Get-FileHash -Path $arm64Zip -Algorithm SHA256).Hash.ToUpperInvariant()

$versionManifest = @"
PackageIdentifier: sasler.TroubleScout
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $manifestSchemaVersion
"@

$localeManifest = @"
PackageIdentifier: sasler.TroubleScout
PackageVersion: $Version
PackageLocale: en-US
Publisher: sasler
PublisherUrl: https://github.com/sasler
PublisherSupportUrl: https://github.com/sasler/TroubleScout/issues
PackageName: TroubleScout
PackageUrl: https://github.com/sasler/TroubleScout
License: MIT
LicenseUrl: https://github.com/sasler/TroubleScout/blob/$ReleaseTag/LICENSE.md
ShortDescription: AI-powered Windows Server troubleshooting assistant.
Description: TroubleScout is a .NET CLI tool using the GitHub Copilot SDK to diagnose Windows Server issues using safe, read-only PowerShell commands.
Tags:
- cli
- copilot
- powershell
- troubleshooting
- windows
- windows-server
ReleaseNotesUrl: https://github.com/sasler/TroubleScout/releases/tag/$ReleaseTag
ManifestType: defaultLocale
ManifestVersion: $manifestSchemaVersion
"@

$installerManifest = @"
PackageIdentifier: sasler.TroubleScout
PackageVersion: $Version
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: TroubleScout.exe
  PortableCommandAlias: troublescout
UpgradeBehavior: install
Commands:
- troublescout
Installers:
- Architecture: x64
  InstallerUrl: $X64InstallerUrl
  InstallerSha256: $x64Hash
- Architecture: arm64
  InstallerUrl: $Arm64InstallerUrl
  InstallerSha256: $arm64Hash
ManifestType: installer
ManifestVersion: $manifestSchemaVersion
"@

Set-Content -Path (Join-Path $ManifestDir 'sasler.TroubleScout.yaml') -Value $versionManifest -Encoding UTF8
Set-Content -Path (Join-Path $ManifestDir 'sasler.TroubleScout.locale.en-US.yaml') -Value $localeManifest -Encoding UTF8
Set-Content -Path (Join-Path $ManifestDir 'sasler.TroubleScout.installer.yaml') -Value $installerManifest -Encoding UTF8

Write-Host "Running winget validate..."
winget validate $ManifestDir
if ($LASTEXITCODE -ne 0) {
    throw "winget validate failed with exit code $LASTEXITCODE."
}

if ($RunSandbox) {
    if ([string]::IsNullOrWhiteSpace($WingetPkgsRoot)) {
        throw "WingetPkgsRoot is required when -RunSandbox is specified."
    }

    $sandboxScript = Join-Path $WingetPkgsRoot 'Tools\SandboxTest.ps1'
    if (-not (Test-Path $sandboxScript)) {
        throw "SandboxTest.ps1 was not found at '$sandboxScript'."
    }

    Write-Host "Running winget-pkgs Sandbox test..."
    & $sandboxScript $ManifestDir
    if ($LASTEXITCODE -ne 0) {
        throw "SandboxTest.ps1 failed with exit code $LASTEXITCODE."
    }
}

if (-not $KeepDownloads) {
    Remove-Item $downloadDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "WinGet validation helper completed successfully."
Write-Host "Manifest directory: $ManifestDir"
