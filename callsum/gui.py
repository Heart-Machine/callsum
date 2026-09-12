"""Окно и значок в трее: одна кнопка на запись, обработка сразу после остановки."""

from __future__ import annotations

import os
import sys
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import QObject, QThread, QTimer, Qt, Signal, Slot
from PySide6.QtGui import QAction, QColor, QIcon, QPainter, QPixmap
from PySide6.QtNetwork import QLocalServer, QLocalSocket
from PySide6.QtWidgets import (
    QApplication, QFileDialog, QHBoxLayout, QLabel, QListWidget, QListWidgetItem,
    QMainWindow, QMenu, QMessageBox, QPlainTextEdit, QProgressBar, QPushButton,
    QSystemTrayIcon, QVBoxLayout, QWidget,
)

from . import config, naming, notify, obs
from .pipeline import process
from .transcribe import Transcriber, hhmmss
from .view import open_document, reveal

# Имя канала для проверки «не запущены ли мы уже».
SINGLE_INSTANCE_KEY = "callsum-single-instance"

WINDOW_TITLE = "callsum"
WINDOW_TITLE_RECORDING = "callsum — Идёт запись"

STAGE_TEXT = {
    "audio": "Готовлю дорожки…",
    "transcribe": "Распознаю речь",
    "summary": "Составляю протокол…",
    "done": "Готово",
}


def dot_icon(color: str) -> QIcon:
    """Значок-кружок: рисуем сами, чтобы не тащить в проект файлы с картинками."""
    pix = QPixmap(64, 64)
    pix.fill(Qt.transparent)
    painter = QPainter(pix)
    painter.setRenderHint(QPainter.Antialiasing)
    painter.setBrush(QColor(color))
    painter.setPen(Qt.NoPen)
    painter.drawEllipse(8, 8, 48, 48)
    painter.end()
    return QIcon(pix)


class Worker(QObject):
    """Обработка записей в отдельном потоке: модель грузится один раз на все созвоны."""

    progress = Signal(str, float, str)   # стадия, доля (или -1), подпись
    message = Signal(str)
    done = Signal(str, str, bool)        # имя, папка результата, есть ли протокол
    failed = Signal(str, str)

    def __init__(self, cfg):
        super().__init__()
        self.cfg = cfg
        self._transcriber: Transcriber | None = None

    @Slot(str, bool)
    def handle(self, path: str, force: bool) -> None:
        src = Path(path)
        folder = naming.folder_name(src, self.cfg.paths.get("folder_template", ""))
        done = self.cfg.path("out") / folder / "transcript.md"
        if done.exists() and not force:
            self.message.emit(f"{src.name}: уже обработан, пропускаю")
            self.done.emit(folder, str(done.parent), (done.parent / "summary.md").exists())
            return
        try:
            if self._transcriber is None:
                self.progress.emit("audio", -1.0, "Загружаю модель распознавания…")
                self._transcriber = Transcriber(self.cfg, verbose=False)
                self.message.emit(
                    f"Модель загружена ({self._transcriber.device}/{self._transcriber.compute_type})"
                )
            res = process(
                src, self.cfg, transcriber=self._transcriber,
                log=self.message.emit,
                on_stage=lambda stage, frac, detail: self.progress.emit(
                    stage, -1.0 if frac is None else frac, detail
                ),
            )
            self.done.emit(res.out_dir.name, str(res.out_dir), res.summary_md.exists())
        except Exception as exc:  # noqa: BLE001 — ошибка одной записи не роняет приложение
            self.failed.emit(src.name, str(exc))


class Connector(QObject):
    """Подключение к OBS в отдельном потоке.

    Попытка занимает до нескольких секунд (сеть, ожидание ответа), поэтому
    выполнять её в потоке интерфейса нельзя — окно замирает.
    """

    finished = Signal(bool, str, bool)   # удалось, текст ошибки, идёт ли запись

    def __init__(self, client, on_record_state):
        super().__init__()
        self.obs = client
        self.on_record_state = on_record_state

    @Slot()
    def attempt(self) -> None:
        try:
            self.obs.connect()
            self.obs.subscribe_record_state(self.on_record_state)
            active, _ = self.obs.status()
        except Exception as exc:  # noqa: BLE001 — наружу уходит текстом в сигнале
            self.finished.emit(False, str(exc), False)
            return
        self.finished.emit(True, "", active)


class ObsBridge(QObject):
    """Переносит события OBS из чужого потока в поток интерфейса."""

    record_state = Signal(bool, str)


class MainWindow(QMainWindow):
    enqueue = Signal(str, bool)
    connect_requested = Signal()

    def __init__(self, cfg):
        super().__init__()
        self.cfg = cfg
        self.obs = obs.Obs(cfg=cfg)
        self.bridge = ObsBridge()
        self.recording_since: datetime | None = None
        self.queue_len = 0
        self.previous_profile: str | None = None
        # Нажатие уже отправлено в OBS, ждём от него подтверждения событием.
        self.record_pending = False
        # Выход начат: окно больше не прячется в трей, а честно закрывается.
        self.stopped = False
        # Попытка переподключения уже назначена — не плодить их по таймеру.
        self.reconnect_scheduled = False
        # Об отсутствии OBS пишем в журнал один раз, а не каждые три секунды.
        self.reported_offline = False
        # Попытка подключения выполняется прямо сейчас в рабочем потоке.
        self.connecting = False

        self.setWindowTitle(WINDOW_TITLE)
        self.setWindowIcon(dot_icon("#c0392b"))
        self.resize(560, 640)
        self._build_ui()
        self._build_tray()
        self._start_worker()
        self._start_connector()

        self.bridge.record_state.connect(self.on_record_state)
        self.clock = QTimer(self)
        self.clock.timeout.connect(self._tick)
        self.clock.start(1000)

        if cfg.created:
            self.append_log(f"Создан {cfg.source} из {config.EXAMPLE_NAME}")

        QTimer.singleShot(0, self.connect_obs)
        self.refresh_calls()

    # --- интерфейс ---------------------------------------------------
    def _build_ui(self) -> None:
        root = QWidget()
        layout = QVBoxLayout(root)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(10)

        self.obs_label = QLabel("OBS: подключаюсь…")
        self.obs_label.setStyleSheet("color: #888;")
        layout.addWidget(self.obs_label)

        self.record_button = QPushButton("● Начать запись")
        self.record_button.setMinimumHeight(56)
        self.record_button.setStyleSheet("font-size: 17px; font-weight: 600;")
        self.record_button.clicked.connect(self.toggle_record)
        layout.addWidget(self.record_button)

        self.timer_label = QLabel("00:00:00")
        self.timer_label.setAlignment(Qt.AlignCenter)
        self.timer_label.setStyleSheet("font-size: 30px; font-family: Consolas, monospace;")
        layout.addWidget(self.timer_label)

        self.stage_label = QLabel("Готов к записи")
        self.stage_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.stage_label)

        self.progress = QProgressBar()
        self.progress.setTextVisible(False)
        self.progress.hide()
        layout.addWidget(self.progress)

        row = QHBoxLayout()
        pick = QPushButton("Обработать файл…")
        pick.clicked.connect(self.pick_file)
        row.addWidget(pick)
        folder = QPushButton("Папка с результатами")
        folder.clicked.connect(lambda: reveal(self.cfg.path("out")))
        row.addWidget(folder)
        layout.addLayout(row)

        layout.addWidget(QLabel("Последние записи:"))
        self.calls = QListWidget()
        self.calls.itemDoubleClicked.connect(self._open_selected)
        layout.addWidget(self.calls, 1)

        row = QHBoxLayout()
        for title, slot in (
            ("Протокол", lambda: self._open_result("summary.md")),
            ("Расшифровка", lambda: self._open_result("transcript.md")),
            ("Папка", lambda: self._open_result(None)),
        ):
            button = QPushButton(title)
            button.clicked.connect(slot)
            row.addWidget(button)
        layout.addLayout(row)

        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setMaximumHeight(110)
        self.log.setStyleSheet("font-family: Consolas, monospace; font-size: 11px;")
        layout.addWidget(self.log)

        self.setCentralWidget(root)

    def _build_tray(self) -> None:
        self.tray = QSystemTrayIcon(dot_icon("#7f8c8d"), self)
        self.tray.setToolTip("callsum")
        menu = QMenu()
        self.tray_record = QAction("● Начать запись", self)
        self.tray_record.triggered.connect(self.toggle_record)
        menu.addAction(self.tray_record)
        show = QAction("Показать окно", self)
        show.triggered.connect(self._restore)
        menu.addAction(show)
        menu.addSeparator()
        quit_action = QAction("Выход", self)
        quit_action.triggered.connect(self.quit_app)
        menu.addAction(quit_action)
        self.tray.setContextMenu(menu)
        self.tray.activated.connect(self._tray_activated)
        self.tray.show()

    def _tray_activated(self, reason) -> None:
        if reason == QSystemTrayIcon.Trigger:
            self._restore()

    def _start_connector(self) -> None:
        self.connect_thread = QThread(self)
        self.connector = Connector(
            self.obs,
            lambda active, path: self.bridge.record_state.emit(active, path or ""),
        )
        self.connector.moveToThread(self.connect_thread)
        self.connect_requested.connect(self.connector.attempt)
        self.connector.finished.connect(self.on_connect_result)
        self.connect_thread.start()

    def _start_worker(self) -> None:
        self.thread = QThread(self)
        self.worker = Worker(self.cfg)
        self.worker.moveToThread(self.thread)
        self.enqueue.connect(self.worker.handle)
        self.worker.progress.connect(self.on_progress)
        self.worker.message.connect(self.append_log)
        self.worker.done.connect(self.on_done)
        self.worker.failed.connect(self.on_failed)
        self.thread.start()

    # --- OBS ---------------------------------------------------------
    def connect_obs(self) -> None:
        """Запустить попытку подключения. Ответ придёт сигналом от рабочего потока.

        Само подключение — сетевая операция на несколько секунд, и раньше она
        выполнялась прямо в потоке интерфейса: пока OBS не отвечал, окно
        замирало на каждую попытку.
        """
        self.reconnect_scheduled = False
        if self.connecting:
            return
        self.connecting = True
        self.obs_label.setText("OBS: подключаюсь…")
        self.connect_requested.emit()

    @Slot(bool, str, bool)
    def on_connect_result(self, ok: bool, error: str, active: bool) -> None:
        """Результат попытки подключения, уже в потоке интерфейса."""
        self.connecting = False
        if not ok:
            if not self.reported_offline:
                self.reported_offline = True
                self.append_log(f"! {error}")
            self._schedule_reconnect()
            return
        self.reported_offline = False
        self.obs_label.setText(
            f"OBS: подключён ({self.obs.settings.host}:{self.obs.settings.port})"
        )
        self.record_pending = False
        self.record_button.setEnabled(True)
        self.tray_record.setEnabled(True)
        if active and self.recording_since is None:
            self.recording_since = datetime.now()
        self._set_recording_ui(self.recording_since is not None)

    def _schedule_reconnect(self) -> None:
        """Отключение — не приговор: ждём OBS и сами восстанавливаем связь."""
        self.obs_label.setText("OBS: нет подключения, пробую подключиться…")
        self.record_pending = False
        self.record_button.setEnabled(False)
        self.record_button.setText("● Начать запись")
        if self.recording_since is not None:
            # Кто теперь ведёт запись, неизвестно — таймер врал бы.
            self.recording_since = None
            self._set_recording_ui(False)
        self.stage_label.setText("Жду OBS")
        if not self.reconnect_scheduled:
            self.reconnect_scheduled = True
            QTimer.singleShot(3000, self.connect_obs)

    def _watch_connection(self) -> None:
        """Заметить, что OBS закрыли, и не оставлять кнопку мёртвой.

        Раньше связь проверялась только при запуске: если OBS закрывали позже,
        кнопка после первой же ошибки оставалась серой до перезапуска.
        """
        if self.obs.connected or self.reconnect_scheduled or self.connecting:
            return
        self.append_log("! Связь с OBS потеряна, пробую подключиться заново")
        self.obs.close()
        self._schedule_reconnect()

    def toggle_record(self) -> None:
        """Команда OBS выполняется не мгновенно — пока она идёт, кнопка занята.

        Иначе по ней успевают нажать несколько раз: OBS переключает профиль и
        закрывает файл за секунду-другую, а интерфейс всё это время выглядит
        так, будто нажатие не сработало.
        """
        if self.record_pending:
            return
        starting = self.recording_since is None
        self._set_button_busy("Запускаю…" if starting else "Останавливаю…")
        try:
            if starting:
                self._switch_profile()
                self.obs.start_record()
            else:
                self.obs.stop_record()
        except Exception as exc:  # noqa: BLE001 — показываем и работаем дальше
            self._release_button()
            self._watch_connection()
            QMessageBox.warning(
                self, "OBS",
                f"{exc}\n\nКак только OBS появится, кнопка снова станет доступной.",
            )
            return
        # Подтверждение придёт событием от OBS; если оно почему-то не придёт,
        # кнопку нужно вернуть в рабочее состояние, а не оставлять мёртвой.
        QTimer.singleShot(15000, self._release_button)

    def _set_button_busy(self, title: str) -> None:
        self.record_pending = True
        self.record_button.setEnabled(False)
        self.record_button.setText(title)
        self.tray_record.setEnabled(False)
        self.stage_label.setText(title)

    def _release_button(self) -> None:
        if not self.record_pending:
            return
        self.record_pending = False
        self.record_button.setEnabled(self.obs.connected)
        self.tray_record.setEnabled(True)
        self._set_recording_ui(self.recording_since is not None)

    def _switch_profile(self) -> None:
        """Перед записью включить профиль callsum, запомнив прежний."""
        settings = self.cfg.obs
        wanted = str(settings.get("profile", "") or "")
        if not wanted or not settings.get("auto_switch", True):
            return
        current = self.obs.current_profile()
        if current == wanted:
            return
        if wanted not in self.obs.profiles():
            self.append_log(
                f"! Профиля «{wanted}» нет в OBS. Создать: callsum obs-setup. "
                "Пишу текущим профилем."
            )
            return
        self.obs.set_profile(wanted)
        self.previous_profile = current
        self.append_log(f"Профиль OBS: «{current}» -> «{wanted}»")

    def _restore_profile(self) -> None:
        if not self.previous_profile or not self.cfg.obs.get("restore_after", True):
            self.previous_profile = None
            return
        try:
            self.obs.set_profile(self.previous_profile)
            self.append_log(f"Профиль OBS возвращён: «{self.previous_profile}»")
        except Exception as exc:  # noqa: BLE001 — неудачный возврат не должен мешать обработке
            self.append_log(f"! Не удалось вернуть профиль: {exc}")
        self.previous_profile = None

    @Slot(bool, str)
    def on_record_state(self, active: bool, path: str) -> None:
        """Реагируем и на свою кнопку, и на хоткей OBS — источник команды не важен."""
        self.record_pending = False
        self.record_button.setEnabled(True)
        self.tray_record.setEnabled(True)
        if active:
            self.recording_since = datetime.now()
            self._set_recording_ui(True)
            self.append_log("Запись начата")
            return
        self.recording_since = None
        self._set_recording_ui(False)
        self._restore_profile()
        if path:
            self.append_log(f"Запись остановлена: {path}")
            self.queue_len += 1
            self.enqueue.emit(path, False)
        else:
            self.append_log("! OBS не сообщил путь к файлу — обработайте его вручную")

    def _set_recording_ui(self, active: bool) -> None:
        # Заголовок окна показывает состояние: видно и со свёрнутым окном,
        # по подписи в панели задач.
        self.setWindowTitle(WINDOW_TITLE_RECORDING if active else WINDOW_TITLE)
        title = "■ Остановить запись" if active else "● Начать запись"
        self.record_button.setText(title)
        self.tray_record.setText(title)
        self.record_button.setStyleSheet(
            "font-size: 17px; font-weight: 600;" + (" color: #c0392b;" if active else "")
        )
        self.tray.setIcon(dot_icon("#c0392b" if active else "#7f8c8d"))
        if active:
            self.stage_label.setText("Идёт запись")
        else:
            self.timer_label.setText("00:00:00")

    def _tick(self) -> None:
        self._watch_connection()
        if self.recording_since is not None:
            seconds = (datetime.now() - self.recording_since).total_seconds()
            self.timer_label.setText(hhmmss(seconds))

    # --- обработка ---------------------------------------------------
    @Slot(str, float, str)
    def on_progress(self, stage: str, fraction: float, detail: str) -> None:
        text = STAGE_TEXT.get(stage, stage)
        if stage == "transcribe" and detail:
            text = f"{text}: {detail}"
        elif stage == "audio" and detail:
            text = detail
        self.stage_label.setText(text)
        self.progress.show()
        if fraction < 0:
            self.progress.setRange(0, 0)          # бегущая полоса: сколько ждать — неизвестно
        else:
            self.progress.setRange(0, 100)
            self.progress.setValue(int(fraction * 100))

    @Slot(str, str, bool)
    def on_done(self, name: str, out_dir: str, has_summary: bool) -> None:
        self.queue_len = max(0, self.queue_len - 1)
        self.progress.hide()
        self.stage_label.setText(
            "Готово, обрабатываю следующий" if self.queue_len else "Готово"
        )
        self.refresh_calls()
        self.notify_user(
            "Запись обработана",
            f"{name}: {'протокол и расшифровка готовы' if has_summary else 'расшифровка готова'}",
        )

    @Slot(str, str)
    def on_failed(self, name: str, error: str) -> None:
        self.queue_len = max(0, self.queue_len - 1)
        self.progress.hide()
        self.stage_label.setText("Ошибка обработки")
        self.append_log(f"! {name}: {error}")
        self.notify_user("Не получилось обработать", f"{name}: {error}", warning=True)

    def pick_file(self) -> None:
        exts = " ".join(f"*{e}" for e in self.cfg.audio["extensions"])
        path, _ = QFileDialog.getOpenFileName(
            self, "Выберите запись", str(self.cfg.path("recordings")), f"Записи ({exts})"
        )
        if path:
            self.queue_len += 1
            self.enqueue.emit(path, True)

    # --- список записей ---------------------------------------------
    def refresh_calls(self) -> None:
        out_root = self.cfg.path("out")
        self.calls.clear()
        if not out_root.exists():
            return
        folders = sorted(
            (p for p in out_root.iterdir() if p.is_dir()),
            key=lambda p: p.stat().st_mtime,
            reverse=True,
        )
        for folder in folders[:50]:
            when = datetime.fromtimestamp(folder.stat().st_mtime).strftime("%d.%m %H:%M")
            mark = "+" if (folder / "summary.md").exists() else "·"
            item = QListWidgetItem(f" {mark}  {folder.name}    {when}")
            item.setData(Qt.UserRole, str(folder))
            self.calls.addItem(item)

    def _selected_folder(self) -> Path | None:
        item = self.calls.currentItem()
        return Path(item.data(Qt.UserRole)) if item else None

    def _open_result(self, filename: str | None) -> None:
        folder = self._selected_folder()
        if folder is None:
            QMessageBox.information(self, "callsum", "Выберите запись в списке.")
            return
        if filename is None:
            reveal(folder)
            return
        target = folder / filename
        if not target.exists():
            QMessageBox.information(self, "callsum", f"Файла ещё нет: {filename}")
            return
        try:
            open_document(target, self.cfg.view.get("markdown_app", ""))
        except OSError as exc:
            QMessageBox.warning(
                self, "callsum",
                f"Не удалось открыть {filename}: {exc}\n\n"
                "Проверьте параметр [view] markdown_app в config.toml.",
            )

    def _open_selected(self, item: QListWidgetItem) -> None:
        folder = Path(item.data(Qt.UserRole))
        self._open_result("summary.md" if (folder / "summary.md").exists() else "transcript.md")

    # --- служебное ---------------------------------------------------
    def notify_user(self, title: str, text: str, warning: bool = False) -> None:
        """Уведомление от имени callsum, а не от имени pythonw.exe.

        Подсказка из трея остаётся запасным вариантом: она подписана именем
        процесса, зато работает там, где системные уведомления недоступны.
        """
        if notify.toast(title, text):
            return
        icon = QSystemTrayIcon.Warning if warning else QSystemTrayIcon.Information
        self.tray.showMessage(title, text, icon, 8000)

    def append_log(self, text: str) -> None:
        self.log.appendPlainText(str(text).rstrip())

    def _restore(self) -> None:
        self.showNormal()
        self.raise_()
        self.activateWindow()

    def closeEvent(self, event) -> None:  # noqa: N802 — имя метода задано Qt
        """Крестик прячет окно в трей: запись и обработка не прерываются.

        При выходе Qt закрывает окно сам, и этот же обработчик срабатывал
        снова — пользователь нажимал «Выход», а получал уведомление о том,
        что программа свернулась.
        """
        if self.stopped:
            event.accept()
            return
        event.ignore()
        self.hide()
        self.notify_user(
            "callsum свернулся в трей",
            "Запись и обработка продолжаются. Выход — через меню значка.",
        )

    def shutdown(self) -> None:
        """Разобрать всё, что держит процесс живым.

        Клиент событий OBS крутит свой поток, и без явного разрыва связи
        процесс не завершается даже после закрытия окна.
        """
        if self.stopped:
            return
        self.stopped = True
        self.obs.close()
        self.thread.quit()
        self.thread.wait(5000)
        self.connect_thread.quit()
        self.connect_thread.wait(5000)
        self.tray.hide()

    def quit_app(self) -> None:
        self.shutdown()
        QApplication.instance().quit()


def _already_running() -> bool:
    """Постучаться в уже запущенный экземпляр и попросить показать окно.

    Два окна подписались бы на события OBS одновременно и принялись бы
    обрабатывать одну и ту же запись вдвоём, деля видеопамять и очередь Ollama.
    """
    probe = QLocalSocket()
    probe.connectToServer(SINGLE_INSTANCE_KEY)
    if not probe.waitForConnected(500):
        return False
    probe.write(b"show")
    probe.flush()
    probe.waitForBytesWritten(500)
    probe.disconnectFromServer()
    return True


def save_icon() -> Path | None:
    """Сохранить значок файлом — Windows берёт картинку для уведомления с диска.

    Рисуем мы его в коде, поэтому файл создаётся рядом с настройками
    пользователя, а не тащится в репозиторий.
    """
    folder = Path(os.environ.get("LOCALAPPDATA", "")) / "callsum"
    try:
        folder.mkdir(parents=True, exist_ok=True)
        target = folder / "icon.png"
        if not target.exists() and not dot_icon("#c0392b").pixmap(128, 128).save(str(target)):
            return None
    except OSError:
        return None
    return target


def main(cfg=None) -> int:
    # Имя приложения нужно объявить до создания окон.
    notify.register()
    app = QApplication(sys.argv)
    app.setApplicationName(notify.APP_DISPLAY_NAME)
    # Значок рисует Qt, поэтому дописываем регистрацию, когда он уже доступен.
    notify.register(save_icon())
    app.setQuitOnLastWindowClosed(False)
    if _already_running():
        print("callsum уже запущен — показываю его окно.")
        return 0

    window = MainWindow(cfg or config.load())

    server = QLocalServer(app)
    # Имя канала остаётся занятым после аварийного завершения, поэтому чистим.
    QLocalServer.removeServer(SINGLE_INSTANCE_KEY)
    server.listen(SINGLE_INSTANCE_KEY)
    server.newConnection.connect(lambda: (server.nextPendingConnection(), window._restore()))

    # Выход бывает не только через меню значка: закрытие сессии, Ctrl+C,
    # завершение работы Windows — прибираемся в любом случае.
    app.aboutToQuit.connect(window.shutdown)
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
