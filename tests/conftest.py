"""Общие настройки тестов."""

import os

# Тесты окна не должны показывать его на экране и требовать видеокарту.
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
