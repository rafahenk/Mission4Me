@echo off
rem Atalho para o build.ps1 (aceita os mesmos parâmetros: -Version 1.2.0 / -Dev)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if errorlevel 1 (
  echo.
  echo Falhou. Copie a mensagem de erro acima e me mande.
)
pause
