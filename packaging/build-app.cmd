@echo off
rem Сборка установщика приложения в dist\releases. Запускать из корня проекта.
rem Перед этим должно быть собрано ядро: packaging\build-core.cmd
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-app.ps1" %*
