"""Уведомления Windows от имени приложения, а не от имени pythonw.exe.

Всплывающая подсказка из трея (`QSystemTrayIcon.showMessage`) подписывается
именем процесса — в заголовке получалось «Python». Настоящее уведомление
Windows берёт имя и значок из регистрации приложения, поэтому здесь
используется оно, а подсказка из трея остаётся запасным вариантом.
"""

from __future__ import annotations

import os
import subprocess
from pathlib import Path
from xml.sax.saxutils import escape

APP_ID = "Callsum.CallRecorder"
APP_DISPLAY_NAME = "callsum"
REGISTRY_KEY = rf"Software\Classes\AppUserModelId\{APP_ID}"

# Окно консоли при запуске PowerShell показывать незачем.
_NO_WINDOW = 0x08000000

_TEMPLATE = """
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] > $null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType=WindowsRuntime] > $null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml(@'
<toast><visual><binding template="ToastGeneric"><text>{title}</text><text>{message}</text></binding></visual></toast>
'@)
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{app_id}').Show($toast)
"""


def register(icon: Path | None = None) -> bool:
    """Записать имя и значок приложения, под которыми Windows покажет уведомления."""
    if os.name != "nt":
        return False
    try:
        import ctypes
        import winreg

        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(APP_ID)
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, REGISTRY_KEY) as key:
            winreg.SetValueEx(key, "DisplayName", 0, winreg.REG_SZ, APP_DISPLAY_NAME)
            if icon is not None and Path(icon).is_file():
                winreg.SetValueEx(key, "IconUri", 0, winreg.REG_SZ, str(Path(icon).resolve()))
    except Exception:  # noqa: BLE001 — без регистрации остаётся запасной путь
        return False
    return True


def toast(title: str, message: str) -> bool:
    """Показать уведомление. False — если не вышло и нужен запасной вариант."""
    if os.name != "nt":
        return False
    script = _TEMPLATE.format(
        title=escape(title), message=escape(message), app_id=APP_ID.replace("'", "''")
    )
    try:
        subprocess.Popen(
            ["powershell", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
             "-Command", script],
            creationflags=_NO_WINDOW,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    except OSError:
        return False
    return True
