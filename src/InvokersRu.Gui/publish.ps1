[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

throw @'
Этот legacy-скрипт отключён: он собирал старую диагностическую оболочку и несовместим с runtime-cache патчером.

Используйте единый проверяемый процесс из корня репозитория:
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-windows-preview.ps1
'@
