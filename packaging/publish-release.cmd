@echo off
rem Собрать пакеты, поставить тег и опубликовать релиз на GitHub.
rem Версию сначала нужно подготовить и слить в main через set-version.ps1.
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-release.ps1" %*
