"""Тесты уведомлений: сборка команды и экранирование текста."""

import os

import pytest

from callsum import notify


def test_script_carries_title_message_and_app_id():
    script = notify._TEMPLATE.format(title="Запись обработана", message="готово",
                                     app_id=notify.APP_ID)
    assert "<text>Запись обработана</text>" in script
    assert "<text>готово</text>" in script
    assert f"CreateToastNotifier('{notify.APP_ID}')" in script


def test_xml_special_characters_are_escaped():
    """Имя файла с амперсандом не должно ломать разметку уведомления."""
    from xml.sax.saxutils import escape

    script = notify._TEMPLATE.format(title=escape("Планёрка & разбор"),
                                     message=escape("<итог>"), app_id=notify.APP_ID)
    assert "Планёрка &amp; разбор" in script
    assert "&lt;итог&gt;" in script
    assert "<итог>" not in script


@pytest.mark.skipif(os.name != "nt", reason="регистрация приложения есть только в Windows")
def test_registration_writes_display_name():
    import winreg

    assert notify.register() is True
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, notify.REGISTRY_KEY) as key:
        assert winreg.QueryValueEx(key, "DisplayName")[0] == notify.APP_DISPLAY_NAME
