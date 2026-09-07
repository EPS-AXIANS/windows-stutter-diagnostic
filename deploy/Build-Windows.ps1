#requires -Version 5.1
<#
    Build-Windows.ps1 — compile Windows Stutter Diagnostic sur CE PC, puis (si la
    compilation reussit) fabrique le pack d'installation et installe le service.

    Pensé pour être lancé par une personne non technique via Build-Windows.bat :
      - installe le SDK .NET 8 dans le profil utilisateur (AUCUN droit admin requis
        pour cette etape) s'il n'est pas deja present ;
      - compile la solution ;
      - EN CAS D'ECHEC : ecrit build-errors.txt (liste courte) + build-log.txt (complet)
        et demande de les envoyer ;
      - EN CAS DE SUCCES : lance deploy\publish-release.ps1 puis deploy\StutterDiag.ps1
        -Action install (qui demandera les droits admin, une seule fois).

    Boucle de mise au point attendue (le code n'a jamais ete compile) :
        lancer -> envoyer build-errors.txt -> correction cote developpeur (git push)
        -> relancer. 2 a 4 tours en general.
#>
[CmdletBinding()]
param(
    [string]$SourceDir = '',
    [string]$Configuration = 'Release',
    [switch]$NoInstall,
    [switch]$SkipTests,
    [string]$RepoUrl = 'https://github.com/EPS-AXIANS/windows-stutter-diagnostic.git',
    [string]$RepoSlug = 'EPS-AXIANS/windows-stutter-diagnostic'
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Say  { param([string]$m, [string]$c = 'Cyan') Write-Host $m -ForegroundColor $c }
function Done { param([string]$m) Write-Host $m -ForegroundColor Green }
function Stop-Here {
    param([string]$m)
    Write-Host ''
    Write-Host $m -ForegroundColor Red
    [void](Read-Host 'Appuyez sur Entree pour fermer')
    exit 1
}

Say '===  Compilation de Windows Stutter Diagnostic  ===' 'White'

# ----------------------------------------------------------- 1. sources

if (-not $SourceDir) {
    $parent = Split-Path -Parent $ScriptDir
    if (Test-Path (Join-Path $parent 'StutterDiag.sln')) { $SourceDir = $parent }
}
if (-not $SourceDir) {
    $SourceDir = Join-Path $env:USERPROFILE 'windows-stutter-diagnostic'
    if (-not (Test-Path (Join-Path $SourceDir 'StutterDiag.sln'))) {
        Say "Recuperation du code source dans $SourceDir ..."
        if (Get-Command git -ErrorAction SilentlyContinue) {
            & git clone $RepoUrl $SourceDir
        } elseif (Get-Command gh -ErrorAction SilentlyContinue) {
            & gh repo clone $RepoSlug $SourceDir
        } else {
            Stop-Here ("Ni 'git' ni 'gh' ne sont installes, et aucun dossier source n'a ete trouve.`n" +
                       "Demandez a la personne qui vous guide de vous envoyer le code source (fichier zip),`n" +
                       "extrayez-le, et placez Build-Windows.bat dans le dossier 'deploy' a l'interieur.")
        }
    }
}
if (-not (Test-Path (Join-Path $SourceDir 'StutterDiag.sln'))) {
    Stop-Here "StutterDiag.sln introuvable dans : $SourceDir"
}
Done "Sources : $SourceDir"

if (Test-Path (Join-Path $SourceDir '.git')) {
    try {
        Say 'Mise a jour du code (git pull)...'
        & git -C $SourceDir pull --ff-only
    } catch {
        Say 'git pull ignore (pas grave).' 'DarkGray'
    }
}

# ----------------------------------------------------------- 2. SDK .NET 8

$DotNet = 'dotnet'
$has8 = $false
try { $has8 = $null -ne (& dotnet --list-sdks 2>$null | Select-String -Pattern '^8\.') } catch { }

if (-not $has8) {
    Say 'Le SDK .NET 8 est absent. Installation dans votre profil (sans droits administrateur)...'
    $inst = Join-Path $env:TEMP 'dotnet-install.ps1'
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $inst
    } catch {
        Stop-Here ("Impossible de telecharger l'installeur du SDK .NET.`n" +
                   "Verifiez la connexion Internet, ou installez manuellement le .NET 8 SDK (x64) depuis`n" +
                   "https://dotnet.microsoft.com/download/dotnet/8.0 puis relancez ce script.")
    }
    $dnDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $inst -Channel 8.0 -InstallDir $dnDir -NoPath
    $DotNet = Join-Path $dnDir 'dotnet.exe'
    if (-not (Test-Path $DotNet)) { Stop-Here "L'installation du SDK .NET a echoue." }
    $env:PATH = "$dnDir;$env:PATH"
    $env:DOTNET_ROOT = $dnDir
}
Done ('SDK .NET : ' + (& $DotNet --version))

# ----------------------------------------------------------- 3. build

$sln = Join-Path $SourceDir 'StutterDiag.sln'
$log = Join-Path $ScriptDir 'build-log.txt'
$errFile = Join-Path $ScriptDir 'build-errors.txt'
Remove-Item $log, $errFile -ErrorAction SilentlyContinue

Say 'Compilation en cours (5 a 10 minutes la premiere fois)...'
& $DotNet build $sln -c $Configuration --nologo -v minimal 2>&1 | Tee-Object -FilePath $log
$buildOk = ($LASTEXITCODE -eq 0)

if (-not $buildOk) {
    $lines = Select-String -Path $log -Pattern ': error |: warning ' -ErrorAction SilentlyContinue
    $onlyErrors = $lines | Where-Object { $_.Line -match ': error ' } | Select-Object -First 300
    ($onlyErrors | ForEach-Object { $_.Line.Trim() }) | Set-Content -Path $errFile -Encoding UTF8

    Write-Host ''
    Say 'LA COMPILATION A ECHOUE (normal au premier essai).' 'Red'
    Say "Envoyez CES DEUX FICHIERS a la personne qui vous guide :" 'Yellow'
    Say "   $errFile     (liste courte des erreurs)" 'Yellow'
    Say "   $log          (journal complet, au cas ou)" 'Yellow'
    try { Start-Process notepad.exe $errFile } catch { }
    Stop-Here 'Puis attendez une nouvelle version et relancez Build-Windows.bat.'
}
Done 'Compilation reussie.'

# ----------------------------------------------------------- 4. tests (informatif)

if (-not $SkipTests) {
    Say 'Execution des tests (informatif, on continue meme en cas d''echec)...'
    try {
        & $DotNet test $sln -c $Configuration --no-build --nologo 2>&1 |
            Tee-Object -FilePath (Join-Path $ScriptDir 'test-log.txt') | Out-Null
    } catch { }
}

# ----------------------------------------------------------- 5. pack + installation

Push-Location $SourceDir
try {
    Say 'Fabrication du pack d''installation...'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $SourceDir 'deploy\publish-release.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { Stop-Here "La fabrication du pack a echoue (voir les messages ci-dessus)." }

    if (-not $NoInstall) {
        Say 'Installation du service sur CE PC (une fenetre va demander les droits admin : repondez Oui)...'
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $SourceDir 'deploy\StutterDiag.ps1') -Action install
    }
} finally {
    Pop-Location
}

Write-Host ''
Done 'TERMINE.'
Done ("Pack d'installation : " + (Join-Path $SourceDir 'artifacts'))
if ($NoInstall) { Done "Pour installer plus tard : deploy\Lancer-StutterDiag.bat -> choix 1" }
else { Done "La surveillance tourne. Vous pouvez utiliser le PC normalement." }
[void](Read-Host 'Appuyez sur Entree pour fermer')
