#requires -Version 5.1
<#
.SYNOPSIS
  Produce a self-contained, no-install portable build of Windows Stutter Diagnostic.
.DESCRIPTION
  Publishes the Service, GUI and CLI as self-contained win-x64 executables and zips them
  together with the docs and a default appsettings.json into artifacts/.
  The portable build runs as a tray app; without elevation the kernel ETW session is
  unavailable and detection falls back to heartbeat + performance counters + event logs.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SingleFile
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts 'portable'

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
if (-not $version) { $version = '0.1.0' }

Write-Host "Publishing portable build $version ($Configuration / $Runtime)" -ForegroundColor Cyan

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

$common = @(
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    "-p:PublishSingleFile=$([bool]$SingleFile)".ToLower(),
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none'
)

$projects = @(
    'src/StutterDiag.Service/StutterDiag.Service.csproj',
    'src/StutterDiag.Cli/StutterDiag.Cli.csproj',
    'src/StutterDiag.Gui/StutterDiag.Gui.csproj'
)

foreach ($proj in $projects) {
    Write-Host "  dotnet publish $proj" -ForegroundColor DarkGray
    dotnet publish (Join-Path $root $proj) @common -o $stage
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $proj" }
}

Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $root 'docs') (Join-Path $stage 'docs') -Recurse
if (Test-Path (Join-Path $root 'src/StutterDiag.Service/appsettings.json')) {
    Copy-Item (Join-Path $root 'src/StutterDiag.Service/appsettings.json') $stage
}

$zip = Join-Path $artifacts "StutterDiag-portable-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Created $zip" -ForegroundColor Green
