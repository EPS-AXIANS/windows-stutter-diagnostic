#requires -Version 5.1
<#
    check-scripts.ps1 - static validation of every .ps1 in the repository.

    The deploy scripts are run by a non-technical operator under Windows PowerShell 5.1,
    where two encoding traps have already broken releases:

      1. No UTF-8 BOM. 5.1 then reads a .ps1 as ANSI/cp1252. These scripts are written in
         French, so they are full of accents; an em-dash (UTF-8 E2 80 94) decodes to a right
         double quotation mark (cp1252 0x94), which PowerShell accepts as a string delimiter.
         Every string containing one terminates early, with cascading errors far from the cause.

      2. A character whose UTF-8 bytes contain a PowerShell string delimiter, which stays fatal
         even if an editor later strips the BOM.

    Run under powershell.exe (5.1) in CI so the checks match the interpreter the operator uses.
    Exits non-zero and names every offending file.
#>
[CmdletBinding()]
param([string]$Root = (Join-Path $PSScriptRoot '..'))

$ErrorActionPreference = 'Stop'

# Bytes PowerShell treats as string delimiters / escapes when a file is misread as cp1252.
$delimiters = @(0x22, 0x27, 0x60, 0x91, 0x92, 0x93, 0x94)
$failures = New-Object System.Collections.Generic.List[string]

$files = Get-ChildItem -Path $Root -Recurse -Filter *.ps1 -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts)[\\/]' } |
    Sort-Object FullName

if ($files.Count -eq 0) { Write-Error 'No .ps1 files found; the check is misconfigured.'; exit 2 }

foreach ($f in $files) {
    $rel = $f.FullName.Substring((Resolve-Path $Root).Path.Length).TrimStart('\', '/')
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)

    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        $failures.Add("$rel : missing UTF-8 BOM (Windows PowerShell 5.1 would read it as ANSI)")
    }

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }

    foreach ($ch in ($text.ToCharArray() | Where-Object { [int]$_ -gt 127 } | Sort-Object -Unique)) {
        $utf8 = [System.Text.Encoding]::UTF8.GetBytes([string]$ch)
        $hit = @($utf8 | Where-Object { $delimiters -contains $_ })
        if ($hit.Count -gt 0) {
            $hex = ($utf8 | ForEach-Object { $_.ToString('x2') }) -join ' '
            $failures.Add("$rel : character '$ch' (UTF-8 $hex) decodes to a PowerShell string delimiter under cp1252")
        }
    }

    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$errors)
    foreach ($e in $errors) {
        $failures.Add("$rel : line $($e.Extent.StartLineNumber): $($e.Message)")
    }
}

Write-Host ("Checked {0} .ps1 file(s) with PowerShell {1}." -f $files.Count, $PSVersionTable.PSVersion)

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host ("{0} problem(s) found:" -f $failures.Count) -ForegroundColor Red
    foreach ($p in $failures) { Write-Host "  $p" -ForegroundColor Red }
    exit 1
}

Write-Host 'All deploy scripts are 5.1-safe.' -ForegroundColor Green
exit 0
