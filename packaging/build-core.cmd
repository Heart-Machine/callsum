@echo off
rem Сборка ядра в dist\callsum-core. Запускать из корня проекта.
chcp 65001 >nul
"%~dp0..\.venv\Scripts\python.exe" -m PyInstaller "%~dp0callsum-core.spec" ^
    --noconfirm --distpath "%~dp0..\dist" --workpath "%~dp0..\build"
