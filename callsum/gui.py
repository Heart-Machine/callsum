"""Окно и значок в трее: одна кнопка на запись, обработка сразу после остановки."""

from __future__ import annotations

import sys
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import QObject, QThread, QTimer, Qt, Signal, Slot
from PySide6.QtGui import QAction, QColor, QIcon, QPainter, QPixmap
from PySide6.QtWidgets import (
    QApplication, QFileDialog, QHBoxLayout, QLabel, QListWidget, QListWidgetItem,
    QMainWindow, QMenu, QMessageBox, QPlainTextEdit, QProgressBar, QPushButton,
    QSystemTrayIcon, QVBoxLayout, QWidget,
)

from . import config, obs
from .pipeline import process
from .view import open_document, reveal
from .transcribe import Transcriber, hhmmss

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

    @Slot(str)
    def handle(self, path: str) -> None:
        src = Path(path)
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
            self.done.emit(src.stem, str(res.out_dir), res.summary_md.exists())
        except Exception as exc:  # noqa: BLE001 — ошибка одной записи не роняет приложение
            self.failed.emit(src.name, str(exc))


class ObsBridge(QObject):
    """Переносит события OBS из чужого потока в поток интерфейса."""

    record_state = Signal(bool, str)


class MainWindow(QMainWindow):
    enqueue = Signal(str)

    def __init__(self, cfg):
        super().__init__()
        self.cfg = cfg
        self.obs = obs.Obs(cfg=cfg)
        self.bridge = ObsBridge()
        self.recording_since: datetime | None = None
        self.queue_len = 0
        self.previous_profile: str | None = None

        self.setWindowTitle("callsum — запись созвонов")
        self.setWindowIcon(dot_icon("#c0392b"))
        self.resize(560, 640)
        self._build_ui()
        self._build_tray()
        self._start_worker()

        self.bridge.record_state.connect(self.on_record_state)
        self.clock = QTimer(self)
        self.clock.timeout.connect(self._tick)
        self.clock.start(1000)

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

        layout.addWidget(QLabel("Последние созвоны:"))
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
        try:
            self.obs.connect()
            self.obs.subscribe_record_state(
                lambda active, path: self.bridge.record_state.emit(active, path or "")
            )
            active, _ = self.obs.status()
        except obs.ObsError as exc:
            self.obs_label.setText("OBS: нет подключения")
            self.append_log(f"! {exc}")
            self.record_button.setEnabled(False)
            QTimer.singleShot(5000, self.connect_obs)
            return
        self.obs_label.setText(
            f"OBS: подключён ({self.obs.settings.host}:{self.obs.settings.port})"
        )
        self.record_button.setEnabled(True)
        if active and self.recording_since is None:
            self.recording_since = datetime.now()
            self._set_recording_ui(True)

    def toggle_record(self) -> None:
        try:
            if self.recording_since is None:
                self._switch_profile()
                self.obs.start_record()
            else:
                self.obs.stop_record()
        except Exception as exc:  # noqa: BLE001 — показываем и работаем дальше
            QMessageBox.warning(self, "OBS", f"Не вышло: {exc}")

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
            self.enqueue.emit(path)
        else:
            self.append_log("! OBS не сообщил путь к файлу — обработайте его вручную")

    def _set_recording_ui(self, active: bool) -> None:
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
        self.tray.showMessage(
            "Созвон обработан",
            f"{name}: {'протокол и расшифровка готовы' if has_summary else 'расшифровка готова'}",
            QSystemTrayIcon.Information,
            8000,
        )

    @Slot(str, str)
    def on_failed(self, name: str, error: str) -> None:
        self.queue_len = max(0, self.queue_len - 1)
        self.progress.hide()
        self.stage_label.setText("Ошибка обработки")
        self.append_log(f"! {name}: {error}")
        self.tray.showMessage(
            "Не получилось обработать", f"{name}: {error}", QSystemTrayIcon.Warning, 10000
        )

    def pick_file(self) -> None:
        exts = " ".join(f"*{e}" for e in self.cfg.audio["extensions"])
        path, _ = QFileDialog.getOpenFileName(
            self, "Выберите запись", str(self.cfg.path("recordings")), f"Записи ({exts})"
        )
        if path:
            self.queue_len += 1
            self.enqueue.emit(path)

    # --- список созвонов ---------------------------------------------
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
            QMessageBox.information(self, "callsum", "Выберите созвон в списке.")
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
    def append_log(self, text: str) -> None:
        self.log.appendPlainText(str(text).rstrip())

    def _restore(self) -> None:
        self.showNormal()
        self.raise_()
        self.activateWindow()

    def closeEvent(self, event) -> None:  # noqa: N802 — имя метода задано Qt
        """Крестик прячет окно в трей: запись и обработка не прерываются."""
        event.ignore()
        self.hide()
        self.tray.showMessage(
            "callsum свернулся в трей",
            "Запись и обработка продолжаются. Выход — через меню значка.",
            QSystemTrayIcon.Information,
            4000,
        )

    def shutdown(self) -> None:
        """Разобрать всё, что держит процесс живым.

        Клиент событий OBS крутит свой поток, и без явного разрыва связи
        процесс не завершается даже после закрытия окна.
        """
        if getattr(self, "_stopped", False):
            return
        self._stopped = True
        self.obs.close()
        self.thread.quit()
        self.thread.wait(5000)
        self.tray.hide()

    def quit_app(self) -> None:
        self.shutdown()
        QApplication.instance().quit()


def main(cfg=None) -> int:
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)
    window = MainWindow(cfg or config.load())
    # Выход бывает не только через меню значка: закрытие сессии, Ctrl+C,
    # завершение работы Windows — прибираемся в любом случае.
    app.aboutToQuit.connect(window.shutdown)
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
