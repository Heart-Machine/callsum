# -*- mode: python ; coding: utf-8 -*-
"""Сборка ядра в самостоятельный exe для настольного приложения.

Собирается папкой, а не одним файлом: запуск быстрее (нечего распаковывать),
а обновления через Velopack передают только изменившиеся файлы — библиотеки
CUDA весом больше гигабайта не перекачиваются на каждую версию.
"""

import sys
from pathlib import Path

from PyInstaller.utils.hooks import collect_all

ROOT = Path(SPECPATH).parent

datas = [
    (str(ROOT / "prompts"), "prompts"),
    (str(ROOT / "config.example.toml"), "."),
]
binaries = []
hiddenimports = []

# Пакеты с нативными библиотеками и файлами данных: у faster-whisper это модель
# фильтра тишины, у ctranslate2 — сам вычислительный движок.
for package in ("faster_whisper", "ctranslate2", "tokenizers", "onnxruntime", "av"):
    package_datas, package_binaries, package_hidden = collect_all(package)
    datas += package_datas
    binaries += package_binaries
    hiddenimports += package_hidden

# Библиотеки CUDA ставятся пакетами nvidia-*-cu12 и лежат вне обычных путей
# поиска PyInstaller. Кладём их рядом с ядром: Windows ищет библиотеки в папке
# программы, и CTranslate2 находит их без дополнительных настроек.
site_packages = Path(sys.prefix) / "Lib" / "site-packages" / "nvidia"
for dll in site_packages.glob("*/bin/*.dll"):
    binaries.append((str(dll), "."))

# Те же библиотеки PyInstaller тянет ещё раз внутрь пакета nvidia, потому что
# ctranslate2 импортирует его ради путей. Второй экземпляр не нужен: без этой
# строки сборка весила 3,2 ГБ вместо 1,4 ГБ. Сами модули пакета оставляем —
# без них не отработает импорт.
def _duplicate_cuda(destination) -> bool:
    parts = Path(destination).parts
    return len(parts) > 1 and parts[0] == "nvidia" and "bin" in parts




analysis = Analysis(
    [str(ROOT / "packaging" / "core_entry.py")],
    pathex=[str(ROOT)],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    runtime_hooks=[],
    # Интерфейс в ядре не нужен: его рисует приложение на C#.
    excludes=["PySide6", "shiboken6", "tkinter", "matplotlib"],
    noarchive=False,
)

# Те же библиотеки PyInstaller добавляет ещё раз внутрь пакета nvidia, когда
# разбирает зависимости: ctranslate2 импортирует его ради путей поиска.
# Второй экземпляр не нужен — без этой чистки сборка весит 3,2 ГБ вместо 1,4.
# Фильтруем после анализа: именно там складывается итоговый список файлов.
# Внимание: в списках после анализа поля идут в обратном порядке — сначала
# путь внутри сборки, потом источник. Фильтруем по первому.
analysis.binaries = [entry for entry in analysis.binaries if not _duplicate_cuda(entry[0])]
analysis.datas = [entry for entry in analysis.datas if not _duplicate_cuda(entry[0])]

pyz = PYZ(analysis.pure)

exe = EXE(
    pyz,
    analysis.scripts,
    [],
    exclude_binaries=True,
    name="callsum-core",
    console=True,
    debug=False,
    strip=False,
    upx=False,
)

COLLECT(
    exe,
    analysis.binaries,
    analysis.datas,
    strip=False,
    upx=False,
    name="callsum-core",
)
