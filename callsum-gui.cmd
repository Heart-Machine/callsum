@echo off
rem Запуск окна без консоли: pythonw не открывает чёрное окно.
start "" "%~dp0.venv\Scripts\pythonw.exe" -m callsum gui
