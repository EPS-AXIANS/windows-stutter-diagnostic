#requires -Version 5.1
<#
    publish-release.ps1 - À exécuter par le développeur, sur une machine Windows + .NET 8 SDK,
    UNE FOIS que la solution compile (voir docs/INTEGRATION-NOTES.md).

    Produit  artifacts\StutterDiag-Setup-<version>.zip  : le pack cle-en-main que la
    personne distante telecharge, extrait et lance via Lancer-StutterDiag.bat.
    Contenu du pack :
        Lancer-StutterDiag.bat
        StutterDiag.ps1
        LISEZ-MOI.txt
        app\   (build portable self-contained : Service + GUI + CLI, aucun runtime requis)

    -Release  publie aussi (ou met a jour) la release GitHub $Tag avec ce zip attache,
              pour un telechargement par simple lien.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Release,
    [string]$Tag = 'v0.1.0',
    [string]$RepoSlug = 'EPS-AXIANS/windows-stutter-diagnostic'
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot
$deploy    = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$setupDir  = Join-Path $artifacts 'StutterDiag-Setup'

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
if (-not $version) { $version = '0.1.0' }

Write-Host "== 1/3  Build portable self-contained ($Configuration) ==" -ForegroundColor Cyan
& (Join-Path $root 'build\publish-portable.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "publish-portable.ps1 a echoue." }

$portableZip = Get-ChildItem $artifacts -Filter 'StutterDiag-portable-*.zip' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $portableZip) { throw "Aucun StutterDiag-portable-*.zip dans $artifacts" }

Write-Host "== 2/3  Assemblage du pack d'installation ==" -ForegroundColor Cyan
if (Test-Path $setupDir) { Remove-Item $setupDir -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $setupDir 'app') | Out-Null

Expand-Archive -Path $portableZip.FullName -DestinationPath (Join-Path $setupDir 'app') -Force
foreach ($f in 'Lancer-StutterDiag.bat', 'StutterDiag.ps1', 'LISEZ-MOI.txt') {
    Copy-Item (Join-Path $deploy $f) $setupDir
}

$setupZip = Join-Path $artifacts ("StutterDiag-Setup-{0}.zip" -f $version)
if (Test-Path $setupZip) { Remove-Item $setupZip -Force }
Compress-Archive -Path (Join-Path $setupDir '*') -DestinationPath $setupZip
Write-Host "   -> $setupZip" -ForegroundColor Green

if ($Release) {
    Write-Host "== 3/3  Publication GitHub release $Tag ==" -ForegroundColor Cyan
    & gh release view $Tag --repo $RepoSlug *> $null
    if ($LASTEXITCODE -eq 0) {
        & gh release upload $Tag $setupZip --repo $RepoSlug --clobber
    } else {
        & gh release create $Tag $setupZip --repo $RepoSlug `
            --title ("Windows Stutter Diagnostic {0}" -f $version) `
            --notes "Pack d'installation cle-en-main. Extraire le zip, double-cliquer sur Lancer-StutterDiag.bat, choix 1."
    }
    Write-Host "Lien de telechargement du pack :" -ForegroundColor Green
    & gh release view $Tag --repo $RepoSlug --json assets --jq '.assets[] | select(.name | startswith("StutterDiag-Setup")) | .url'
} else {
    Write-Host "== 3/3  (skip release GitHub - relancez avec -Release pour publier) ==" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Termine. Envoyez  $setupZip  a la personne distante (ou le lien de release)." -ForegroundColor Green
