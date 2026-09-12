"""Тесты отзывчивости кнопки записи: нажал — кнопка занята до ответа OBS."""

import copy

import pytest
from PySide6.QtWidgets import QApplication

from callsum import config
from callsum.gui import MainWindow


class StubObs:
    """Подставной OBS: команды принимает, событий сам не шлёт."""

    def __init__(self):
        self.calls = []
        self.connected = True
        self.fail = False

    def start_record(self):
        self.calls.append("start")
        if self.fail:
            raise RuntimeError("OBS занят")

    def stop_record(self):
        self.calls.append("stop")
        if self.fail:
            raise RuntimeError("OBS занят")

    def current_profile(self):
        return "callsum"

    def profiles(self):
        return ["callsum"]

    def set_profile(self, name):
        self.calls.append(f"profile:{name}")

    def close(self):
        pass


@pytest.fixture
def window(tmp_path):
    app = QApplication.instance() or QApplication([])
    data = copy.deepcopy(config.DEFAULTS)
    data["paths"] = {"recordings": str(tmp_path / "rec"), "out": str(tmp_path / "out"),
                     "folder_template": "{name}"}
    win = MainWindow(config.Config(data))
    win.obs = StubObs()
    yield win
    win.shutdown()
    app.processEvents()


def test_button_locks_until_obs_confirms(window):
    window.toggle_record()

    assert window.record_button.isEnabled() is False
    assert window.record_button.text() == "Запускаю…"
    assert window.record_pending is True

    # Повторные нажатия, пока OBS не ответил, не должны доходить до него.
    window.toggle_record()
    window.toggle_record()
    assert window.obs.calls.count("start") == 1

    window.on_record_state(True, "")
    assert window.record_button.isEnabled() is True
    assert window.record_button.text() == "■ Остановить запись"


def test_stop_locks_the_button_too(window):
    window.on_record_state(True, "")
    window.toggle_record()

    assert window.record_button.isEnabled() is False
    assert window.record_button.text() == "Останавливаю…"
    assert window.obs.calls == ["stop"]

    window.on_record_state(False, "")
    assert window.record_button.isEnabled() is True
    assert window.record_button.text() == "● Начать запись"


def test_failed_command_unlocks_the_button(window, monkeypatch):
    """Если OBS отказал, кнопка обязана вернуться в рабочее состояние."""
    monkeypatch.setattr("callsum.gui.QMessageBox.warning", lambda *args, **kwargs: None)
    window.obs.fail = True

    window.toggle_record()

    assert window.record_pending is False
    assert window.record_button.isEnabled() is True
    assert window.record_button.text() == "● Начать запись"


def test_release_timer_recovers_a_lost_confirmation(window):
    """Событие от OBS может не дойти — кнопка не должна остаться мёртвой."""
    window.toggle_record()
    assert window.record_button.isEnabled() is False

    window._release_button()

    assert window.record_pending is False
    assert window.record_button.isEnabled() is True
