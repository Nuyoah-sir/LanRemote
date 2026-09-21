@echo off
REM =====================================================================
REM  LanRemote M3 two-machine acceptance - DOUBLE-CLICK ENTRY POINT
REM
REM  This file contains ASCII ONLY on purpose. Windows cmd.exe parses
REM  .cmd files with the console code page; Chinese characters (even in
REM  REM lines) are mis-decoded and produce "is not recognized as an
REM  internal or external command". All Chinese text lives in
REM  run-acceptance.ps1, which is UTF-8 WITH BOM and renders correctly.
REM
REM  It just hands over to the PowerShell driver and keeps the window
REM  open afterwards, because the acceptance tool is a CONSOLE app:
REM  double-clicking LanRemote.Acceptance.exe directly makes it print
REM  usage and exit immediately, with no time to read anything.
REM =====================================================================

setlocal
cd /d "%~dp0"

set "PS1=%~dp0run-acceptance.ps1"

if not exist "%PS1%" (
  echo ERROR: run-acceptance.ps1 not found next to this file.
  echo        Keep START.cmd and run-acceptance.ps1 in the same folder.
  echo.
  pause
  exit /b 2
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
set "RC=%ERRORLEVEL%"

echo.
echo Exit code: %RC%    (0 = as expected / 1 = genuinely wrong / 2 = preconditions unmet)
echo.
pause
exit /b %RC%
