@echo off
rem ============================================================================
rem  Windows Stutter Diagnostic - compilation + installation sur CE PC
rem
rem  Double-cliquez ce fichier. Il installe ce qu'il faut (dans votre profil,
rem  sans droits admin), compile le programme, puis installe le service
rem  (une seule fenetre de securite Windows a valider a la fin).
rem
rem  Si la compilation echoue : deux fichiers .txt sont crees dans ce dossier
rem  (build-errors.txt et build-log.txt) - il faut les envoyer.
rem ============================================================================
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Windows.ps1"

echo.
pause
