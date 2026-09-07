#requires -Version 5.1
<#
    StutterDiag.ps1 - installe / pilote Windows Stutter Diagnostic sur la machine cible.

    Pensé pour être lancé par une personne non technique via Lancer-StutterDiag.bat :
    il s'auto-élève en administrateur, affiche un menu en français, et fait tout
    (copie de l'app, enregistrement du service, démarrage, rapport, désinstallation).

    Il trouve l'application dans cet ordre :
      1. -PackageZip <chemin>              (archive portable explicite)
      2. un dossier "app\" à côté de ce script   <-- cas normal du pack d'installation
      3. un fichier StutterDiag-portable*.zip à côté de ce script
      4. -PackageUrl <lien>  ou  la dernière release GitHub (repo public, ou 'gh' installé)

    Aucune donnée n'est envoyée sur Internet par l'application elle-même.
#>
[CmdletBinding()]
param(
    [ValidateSet('menu','install','update','start','stop','status','report','gui','uninstall')]
    [string]$Action = 'menu',
    [string]$InstallDir = 'C:\StutterDiag',
    [string]$PackageZip = '',
    [string]$PackageUrl = '',
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'StutterDiag.Service'
$ScriptPath  = $MyInvocation.MyCommand.Path
$ScriptDir   = Split-Path -Parent $ScriptPath
$RepoSlug    = 'EPS-AXIANS/windows-stutter-diagnostic'
$LogFile     = Join-Path $InstallDir 'deploy.log'

# --------------------------------------------------------------------------- log

function Write-Log {
    param([string]$Message, [string]$Color = 'Gray')
    $line = ('{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message)
    Write-Host $line -ForegroundColor $Color
    try {
        if (Test-Path $InstallDir) { Add-Content -Path $LogFile -Value $line -Encoding UTF8 }
    } catch { }
}
function Info  ([string]$m) { Write-Log $m 'Cyan' }
function Good  ([string]$m) { Write-Log $m 'Green' }
function Warn2 ([string]$m) { Write-Log $m 'Yellow' }
function Fail2 ([string]$m) { Write-Log $m 'Red' }

# --------------------------------------------------------------- élévation admin

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    if ($pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { return }

    Warn2 "Redemarrage avec les droits administrateur (repondez Oui a la fenetre Windows)..."
    $a = @('-NoProfile','-ExecutionPolicy','Bypass','-File', ('"{0}"' -f $ScriptPath),
           '-Action', $Action, '-InstallDir', ('"{0}"' -f $InstallDir))
    if ($PackageZip) { $a += @('-PackageZip', ('"{0}"' -f $PackageZip)) }
    if ($PackageUrl) { $a += @('-PackageUrl', ('"{0}"' -f $PackageUrl)) }
    if ($RemoveData) { $a += '-RemoveData' }
    # -Wait : le processus appelant (ex. Build-Windows.ps1) bloque jusqu'a la fin de l'installation.
    $proc = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $a -Wait -PassThru
    exit $proc.ExitCode
}

# ---------------------------------------------------- localisation de l'app

function Expand-ToTemp {
    param([string]$Zip)
    $dest = Join-Path $env:TEMP ('StutterDiag-extract-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    Expand-Archive -Path $Zip -DestinationPath $dest -Force
    $hit = Get-ChildItem -Path $dest -Recurse -Filter 'StutterDiag.Service.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $hit) { throw "Archive invalide : StutterDiag.Service.exe est absent de $Zip" }
    return $hit.Directory.FullName
}

function Resolve-GitHubAsset {
    # renvoie un chemin local OU une URL, ou $null
    try {
        if (Get-Command gh -ErrorAction SilentlyContinue) {
            $tmp = Join-Path $env:TEMP 'StutterDiag-ghdl'
            New-Item -ItemType Directory -Force -Path $tmp | Out-Null
            & gh release download --repo $RepoSlug --pattern 'StutterDiag-portable*.zip' --dir $tmp --clobber 2>$null
            $z = Get-ChildItem $tmp -Filter '*.zip' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($z) { return $z.FullName }
        }
    } catch { }
    try {
        $rel = Invoke-RestMethod -UseBasicParsing -Headers @{ 'User-Agent' = 'StutterDiag' } `
            -Uri ('https://api.github.com/repos/{0}/releases/latest' -f $RepoSlug)
        $asset = $rel.assets | Where-Object { $_.name -like 'StutterDiag-portable*.zip' } | Select-Object -First 1
        if ($asset) { return $asset.browser_download_url }
    } catch { }
    return $null
}

function Resolve-App {
    if ($PackageZip -and (Test-Path $PackageZip)) { return (Expand-ToTemp $PackageZip) }

    $bundled = Join-Path $ScriptDir 'app'
    if (Test-Path (Join-Path $bundled 'StutterDiag.Service.exe')) { return $bundled }

    $localZip = Get-ChildItem -Path $ScriptDir -Filter 'StutterDiag-portable*.zip' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($localZip) { return (Expand-ToTemp $localZip.FullName) }

    $got = $PackageUrl
    if (-not $got) { $got = Resolve-GitHubAsset }
    if ($got) {
        if (Test-Path $got) { return (Expand-ToTemp $got) }
        $tmp = Join-Path $env:TEMP 'StutterDiag-dl.zip'
        Info "Telechargement de l'application depuis $got ..."
        Invoke-WebRequest -Uri $got -OutFile $tmp -UseBasicParsing
        return (Expand-ToTemp $tmp)
    }

    throw ("Application introuvable. Placez un dossier 'app\' (ou StutterDiag-portable*.zip) " +
           "a cote de ce script, ou relancez avec -PackageUrl <lien>.")
}

# ----------------------------------------------------------------- exe helpers

function ServiceExe { Join-Path $InstallDir 'StutterDiag.Service.exe' }
function CliExe     { Join-Path $InstallDir 'StutterDiag.Cli.exe' }
function GuiExe     { Join-Path $InstallDir 'StutterDiag.Gui.exe' }

function Invoke-Cli {
    param([string[]]$CliArgs)
    $cli = CliExe
    if (-not (Test-Path $cli)) { throw "StutterDiag.Cli.exe introuvable - lancez d'abord l'installation (choix 1)." }
    & $cli @CliArgs
    return $LASTEXITCODE
}

# ---------------------------------------------------------------- actions

function Install-App {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $src = Resolve-App
    Info "Application trouvee : $src"

    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Info "Arret du service en place..."
        & sc.exe stop $ServiceName 2>$null | Out-Null
        Start-Sleep -Seconds 2
    }

    Info "Copie des fichiers vers $InstallDir ..."
    Get-ChildItem -Path $src -Force | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination $InstallDir -Recurse -Force
    }
    if (-not (Test-Path (ServiceExe))) { throw "La copie a echoue : StutterDiag.Service.exe absent de $InstallDir" }

    Info "Enregistrement du service Windows..."
    & (ServiceExe) uninstall 2>$null | Out-Null
    & (ServiceExe) install
    if ($LASTEXITCODE -ne 0) { throw "Echec de l'enregistrement du service (code $LASTEXITCODE)." }

    Info "Demarrage du service..."
    & (ServiceExe) start | Out-Null
    Start-Sleep -Seconds 2

    Good "Installation terminee. La surveillance tourne en arriere-plan."
    Start-Monitoring
    Open-Gui
    Info "Vous pouvez fermer cette fenetre et utiliser l'ordinateur normalement."
}

function Start-Monitoring {
    Info "Demarrage de la surveillance..."
    Invoke-Cli @('start') | Out-Null
    Get-Status
}

function Stop-Monitoring {
    Info "Arret de la surveillance..."
    Invoke-Cli @('stop') | Out-Null
    Get-Status
}

function Get-Status {
    $cli = CliExe
    if (Test-Path $cli) { & $cli status } else { Warn2 "Pas encore installe." }
}

function New-Report {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $out = Join-Path $desktop ('StutterDiag-rapport-{0}.html' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Info "Generation du rapport HTML..."
    Invoke-Cli @('report', '--format', 'html', '--out', ('"{0}"' -f $out)) | Out-Null
    if (Test-Path $out) {
        Good "Rapport cree : $out"
        Start-Process $out
    } else {
        Warn2 "Le rapport n'a pas ete cree (pas encore assez de donnees ?)."
    }
}

function Open-Gui {
    $gui = GuiExe
    if (Test-Path $gui) {
        # via explorer => l'interface se lance SANS elevation (comme prevu)
        Start-Process -FilePath 'explorer.exe' -ArgumentList ('"{0}"' -f $gui)
        Info "Interface lancee."
    } else {
        Warn2 "Interface introuvable - lancez d'abord l'installation."
    }
}

function Uninstall-App {
    if (Test-Path (ServiceExe)) { & (ServiceExe) uninstall }
    else { & sc.exe delete $ServiceName 2>$null | Out-Null }
    Good "Service supprime."
    if ($RemoveData) {
        $data = Join-Path $env:ProgramData 'StutterDiag'
        if (Test-Path $data) { Remove-Item $data -Recurse -Force; Good "Donnees supprimees : $data" }
    } else {
        Info "Les donnees et rapports sont conserves dans %ProgramData%\StutterDiag."
    }
}

# ------------------------------------------------------------------- menu

function Show-Menu {
    while ($true) {
        Write-Host ''
        Write-Host '===============  Windows Stutter Diagnostic  ===============' -ForegroundColor White
        Write-Host '  1.  Installer / mettre a jour et demarrer'
        Write-Host '  2.  Voir l''etat'
        Write-Host '  3.  Demarrer la surveillance'
        Write-Host '  4.  Arreter la surveillance'
        Write-Host '  5.  Generer un rapport (HTML sur le Bureau)'
        Write-Host '  6.  Ouvrir l''interface'
        Write-Host '  7.  Desinstaller le service'
        Write-Host '  0.  Quitter'
        Write-Host '===========================================================' -ForegroundColor White
        $choice = Read-Host 'Votre choix'
        try {
            switch ($choice) {
                '1' { Install-App }
                '2' { Get-Status }
                '3' { Start-Monitoring }
                '4' { Stop-Monitoring }
                '5' { New-Report }
                '6' { Open-Gui }
                '7' {
                    if ((Read-Host 'Confirmer la desinstallation ? (o/N)') -match '^[oO]') { Uninstall-App }
                }
                '0' { return }
                default { Warn2 'Choix invalide.' }
            }
        } catch {
            Fail2 $_.Exception.Message
            Fail2 "Faites une photo de cet ecran et envoyez-la. Detail : $LogFile"
        }
        Write-Host ''
        [void](Read-Host 'Appuyez sur Entree pour revenir au menu')
    }
}

# ------------------------------------------------------------------- main

Assert-Admin
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Info "Journal : $LogFile"

switch ($Action) {
    'menu'      { Show-Menu }
    'install'   { Install-App }
    'update'    { Install-App }
    'start'     { Start-Monitoring }
    'stop'      { Stop-Monitoring }
    'status'    { Get-Status }
    'report'    { New-Report }
    'gui'       { Open-Gui }
    'uninstall' { Uninstall-App }
}
