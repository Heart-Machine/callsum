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

    def connect(self):
        """Окно пробует подключиться по таймеру — пусть находит готовое соединение."""
        self.connected = True

    def subscribe_record_state(self, callback):
        self.callback = callback

    def status(self):
        return False, 0.0

    @property
    def settings(self):
        from callsum.obs import ObsSettings

        return ObsSettings()

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


def test_window_title_shows_recording_state(window):
    from callsum.gui import WINDOW_TITLE, WINDOW_TITLE_RECORDING

    assert window.windowTitle() == WINDOW_TITLE

    window.on_record_state(True, "")
    assert window.windowTitle() == WINDOW_TITLE_RECORDING

    window.on_record_state(False, "")
    assert window.windowTitle() == WINDOW_TITLE


def test_close_button_hides_to_tray_with_a_notice(window):
    from PySide6.QtGui import QCloseEvent

    notices = []
    window.notify_user = lambda title, text, warning=False: notices.append(title)

    event = QCloseEvent()
    window.closeEvent(event)

    assert event.isAccepted() is False, "крестик не должен закрывать приложение"
    assert window.isHidden() is True
    assert notices == ["callsum свернулся в трей"]


def test_quit_does_not_announce_minimising(window):
    """Нажали «Выход» — окно закрывается молча, а не сообщает, что свернулось."""
    from PySide6.QtGui import QCloseEvent

    notices = []
    window.notify_user = lambda title, text, warning=False: notices.append(title)

    window.shutdown()
    event = QCloseEvent()
    window.closeEvent(event)

    assert event.isAccepted() is True
    assert notices == []
