@echo off
rem ============================================================================
rem  Windows Stutter Diagnostic - lanceur
rem  Double-cliquez ce fichier. Repondez "Oui" a la fenetre de securite Windows.
rem ============================================================================
setlocal
cd /d "%~dp0"

rem --- deja administrateur ? sinon, se relancer en administrateur ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Demande des droits administrateur...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0StutterDiag.ps1" -Action menu

echo.
echo (Fenetre terminee) Vous pouvez fermer cette fenetre.
pause
