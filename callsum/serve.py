"""Режим «мотора»: ядром управляют по строкам JSON.

Настольное приложение запускает ядро один раз и разговаривает с ним через
стандартные потоки: команды приходят в stdin, события уходят в stdout, по одной
строке JSON на сообщение. Так модель распознавания грузится в видеопамять
единожды, а не на каждый файл.

Команды:

    {"cmd": "process",   "id": "1", "path": "D:/rec/созвон.mkv", "force": false}
    {"cmd": "summarize", "id": "2", "transcript": "D:/out/созвон/transcript.md"}
    {"cmd": "doctor",    "id": "3"}
    {"cmd": "settings",  "id": "4"}
    {"cmd": "settings_set", "id": "5", "values": {"paths": {"out": "D:/созвоны"}}}
    {"cmd": "shutdown"}

События:

    {"event": "ready",    "device": "cuda", "compute_type": "float16", "version": "1.0.0"}
    {"event": "progress", "id": "1", "stage": "transcribe", "fraction": 0.42, "detail": "Я"}
    {"event": "log",      "id": "1", "text": "…"}
    {"event": "done",     "id": "1", "out_dir": "…", "summary": true}
    {"event": "error",    "id": "1", "message": "…"}
    {"event": "settings", "id": "4", "path": "…/config.toml", "values": {…}, "resolved": {…}}
"""

from __future__ import annotations

import json
import queue
import sys
import threading
from pathlib import Path
from typing import Any, Callable, Iterable, Iterator

from . import __version__, audio, config, naming, settings, summarize
from .pipeline import process
from .transcribe import Transcriber

Emit = Callable[[dict[str, Any]], None]


class Engine:
    """Выполняет команды по очереди в отдельном потоке.

    Очередь нужна, чтобы чтение команд не ждало окончания обработки: пока идёт
    распознавание часового созвона, приложение должно иметь возможность
    поставить в очередь следующий файл или попросить ядро завершиться.
    """

    def __init__(self, cfg, emit: Emit):
        self.cfg = cfg
        self.emit = emit
        self._queue: queue.Queue = queue.Queue()
        self._thread: threading.Thread | None = None
        self._transcriber: Transcriber | None = None

    # --- жизненный цикл ----------------------------------------------
    def start(self) -> None:
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def stop(self, timeout: float | None = None) -> None:
        """Доделать принятые команды и остановиться.

        Прерывать распознавание на середине нельзя: час работы видеокарты
        пропал бы впустую, а запись осталась бы без расшифровки. По той же
        причине не отменяется и очередь — раз команду приняли, её выполняют.
        Приложению, если нужно быстрее, остаётся убить процесс.
        """
        self._queue.put(None)
        if self._thread is not None:
            self._thread.join(timeout)

    def submit(self, command: dict) -> None:
        self._queue.put(command)

    def _loop(self) -> None:
        while True:
            command = self._queue.get()
            if command is None:
                return
            try:
                self._run(command)
            except Exception as exc:  # noqa: BLE001 — одна команда не валит ядро
                self.emit({"event": "error", "id": command.get("id"), "message": str(exc)})

    # --- команды ------------------------------------------------------
    def _run(self, command: dict) -> None:
        name = command.get("cmd")
        handler = {
            "process": self._process,
            "summarize": self._summarize,
            "doctor": self._doctor,
            "settings": self._settings,
            "settings_set": self._settings_set,
        }.get(str(name))
        if handler is None:
            self.emit({
                "event": "error",
                "id": command.get("id"),
                "message": f"Неизвестная команда: {name!r}",
            })
            return
        handler(command)

    def _process(self, command: dict) -> None:
        request_id = command.get("id")
        src = Path(str(command.get("path", "")))
        if not src.is_file():
            message = f"Нет файла: {src}"
            if "?" in str(src):
                # Знаки вопроса вместо букв — потери при передаче команды.
                # Так делает PowerShell 5.1: конвейер в программу переводит
                # текст в ASCII, и кириллица в пути гибнет ещё до ядра.
                message += (
                    ". Похоже, путь потерял буквы при передаче: в PowerShell перед "
                    "запуском нужно выполнить $OutputEncoding = "
                    "[System.Text.Encoding]::UTF8"
                )
            self.emit({"event": "error", "id": request_id, "message": message})
            return

        out_root = self.cfg.path("out")
        folder = naming.folder_name(src, self.cfg.paths.get("folder_template", ""))
        done = out_root / folder / "transcript.md"
        if done.exists() and not command.get("force"):
            self.emit({"event": "log", "id": request_id, "text": f"{src.name}: уже обработан"})
            self.emit({
                "event": "done",
                "id": request_id,
                "out_dir": str(done.parent),
                "summary": (done.parent / "summary.md").exists(),
            })
            return

        res = process(
            src,
            self.cfg,
            transcriber=self._ensure_transcriber(),
            log=lambda text: self.emit({"event": "log", "id": request_id, "text": str(text)}),
            on_stage=lambda stage, fraction, detail: self.emit({
                "event": "progress",
                "id": request_id,
                "stage": stage,
                "fraction": fraction,
                "detail": detail,
            }),
        )
        self.emit({
            "event": "done",
            "id": request_id,
            "out_dir": str(res.out_dir),
            "summary": res.summary_md.exists(),
        })

    def _summarize(self, command: dict) -> None:
        request_id = command.get("id")
        target = Path(str(command.get("transcript", "")))
        if target.is_dir():
            target = target / "transcript.md"
        if not target.is_file():
            self.emit({"event": "error", "id": request_id, "message": f"Нет расшифровки: {target}"})
            return

        raw = target.read_text(encoding="utf-8")
        meta, _, dialog = raw.partition("\n---\n")
        dialog = (dialog or raw).strip()
        meta = "\n".join(line for line in meta.splitlines() if line.startswith("- "))
        text = summarize.summarize(
            dialog, meta, self.cfg,
            log=lambda line: self.emit({"event": "log", "id": request_id, "text": str(line)}),
        )
        destination = target.parent / "summary.md"
        destination.write_text(
            f"# Протокол созвона: {target.parent.name}\n\n{meta}\n\n{text}\n", encoding="utf-8"
        )
        self.emit({
            "event": "done", "id": request_id, "out_dir": str(target.parent), "summary": True,
        })

    # --- настройки ----------------------------------------------------
    def _settings(self, command: dict) -> None:
        """Отдать настройки приложению: окно рисует по ним форму."""
        self.emit(self._settings_report(command.get("id")))

    def _settings_set(self, command: dict) -> None:
        """Записать изменённые настройки в config.toml и применить их.

        Файл правится по одному значению, а не пишется заново: в нём живут
        комментарии к каждому параметру, и тот, кто правит его руками, должен
        находить файл прежним.
        """
        request_id = command.get("id")
        changes = command.get("values")
        if not isinstance(changes, dict) or not all(
            isinstance(values, dict) for values in changes.values()
        ):
            self.emit({
                "event": "error",
                "id": request_id,
                "message": "Настройки передаются разделами: {\"paths\": {\"out\": \"…\"}}",
            })
            return

        unknown = [name for name in changes if name not in config.DEFAULTS]
        if unknown:
            self.emit({
                "event": "error",
                "id": request_id,
                "message": f"Неизвестные разделы настроек: {', '.join(unknown)}",
            })
            return

        path = Path(self.cfg.source) if self.cfg.source else config.config_path()
        config.ensure_config(path)
        text = path.read_text(encoding="utf-8") if path.exists() else ""
        updated = settings.apply(text, changes)

        # Настройки пишутся через запасной файл: оборвись запись на середине,
        # пользователь остался бы без единственного файла с путями и моделью.
        if text:
            path.with_suffix(".toml.bak").write_text(text, encoding="utf-8")
        temporary = path.with_suffix(".toml.new")
        temporary.write_text(updated, encoding="utf-8")
        temporary.replace(path)

        self.cfg = config.load(path)
        if "transcribe" in changes:
            # Модель уже в видеопамяти, а её выбор мог поменяться — перезагрузим
            # при следующей записи, а не будем распознавать не тем, что выбрано.
            self._release_transcriber()

        self.emit({"event": "log", "id": request_id, "text": f"Настройки сохранены: {path}"})
        self.emit(self._settings_report(request_id))

    def _settings_report(self, request_id: Any) -> dict:
        """Настройки в виде данных: значения как в файле и куда они указывают."""
        return {
            "event": "settings",
            "id": request_id,
            "path": str(self.cfg.source or config.config_path()),
            "values": json.loads(json.dumps(self.cfg.data, ensure_ascii=False, default=str)),
            # Относительный путь в файле — обычное дело; окну нужно показать,
            # где файлы окажутся на самом деле.
            "resolved": {key: str(self.cfg.path(key)) for key in ("recordings", "out")},
        }

    def _release_transcriber(self) -> None:
        transcriber, self._transcriber = self._transcriber, None
        if transcriber is not None:
            try:
                transcriber.release()
            except Exception:  # noqa: BLE001 — освобождение памяти не должно ронять ядро
                pass

    def _doctor(self, command: dict) -> None:
        """Состояние окружения — в виде данных, чтобы приложение показало его само."""
        report: dict[str, Any] = {"event": "doctor", "id": command.get("id")}
        try:
            audio._tool("ffmpeg")
            audio._tool("ffprobe")
            report["ffmpeg"] = True
        except audio.FFmpegMissing as exc:
            report["ffmpeg"] = False
            report["ffmpeg_error"] = str(exc)

        device, compute_type = Transcriber._resolve_device(
            self.cfg.transcribe["device"], self.cfg.transcribe["compute_type"]
        )
        report["device"] = device
        report["compute_type"] = compute_type

        host = self.cfg.summary["host"]
        wanted = str(self.cfg.summary["model"])
        try:
            models = summarize.available_models(host)
            report["ollama"] = True
            report["model"] = any(
                m == wanted or m.startswith(wanted.split(":")[0] + ":") for m in models
            )
        except summarize.OllamaError as exc:
            report["ollama"] = False
            report["model"] = False
            report["ollama_error"] = str(exc)

        # Чем открывать протоколы, решает пользователь в config.toml. Настройки
        # читает ядро, поэтому приложение узнаёт её отсюда же, вместе с путями.
        report["markdown_app"] = str(self.cfg.view.get("markdown_app", ""))

        # Папки заводим сразу: приложению нужно знать готовые пути, а не
        # разбираться, чего ещё не хватает.
        for key in ("recordings", "out"):
            folder = self.cfg.path(key)
            try:
                folder.mkdir(parents=True, exist_ok=True)
            except OSError as exc:
                report[f"{key}_error"] = str(exc)
            report[key] = str(folder)

        self.emit(report)

    def _ensure_transcriber(self) -> Transcriber:
        if self._transcriber is None:
            self.emit({"event": "log", "text": "Загружаю модель распознавания…"})
            self._transcriber = Transcriber(self.cfg, verbose=False)
            self.emit({
                "event": "log",
                "text": f"Модель загружена ({self._transcriber.device}/"
                        f"{self._transcriber.compute_type})",
            })
        return self._transcriber


def _preload() -> None:
    """Подтянуть тяжёлые модули в главном потоке, до старта рабочего.

    В собранном ядре импорт faster-whisper из рабочего потока намертво вешает
    загрузчик библиотек Windows: поток застревает на загрузке numpy и ждёт
    блокировку, которую никто не отпустит. Из консоли этого не видно, а под
    управлением приложения ядро замирало после первой же команды. Импорт
    заранее снимает проблему и заодно делает честным сообщение «ядро готово».
    """
    try:
        import faster_whisper  # noqa: F401
    except Exception:  # noqa: BLE001 — без распознавания остальные команды работают
        pass


def _stdin_lines() -> Iterator[str]:
    """Читать команды построчно, а не пачками.

    Обход `for line in sys.stdin` намеренный: такое чтение буферизуется блоками,
    и одиночная команда от приложения застревает в буфере до тех пор, пока не
    наберётся восемь килобайт или не закроется поток. Из консоли это незаметно —
    поток закрывается сразу, — а живое приложение зависало, отправив команду.
    """
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        yield line


def serve(cfg, lines: Iterable[str] | None = None, write: Callable[[str], None] | None = None) -> int:
    """Цикл чтения команд. Завершается по команде shutdown или концу потока."""
    stream = sys.stdout
    if write is None:
        # Приложение читает вывод построчно, поэтому строку нужно дописывать
        # сразу, а не копить в буфере.
        def write(line: str) -> None:  # noqa: A001 — имя параметра говорит само за себя
            stream.write(line + "\n")
            stream.flush()

    # В потоке событий не должно быть ничего, кроме JSON: случайный print
    # из любого места программы сломал бы разбор на стороне приложения —
    # на этом уже споткнулось сообщение о созданном config.toml. Поэтому всё
    # постороннее уводится в поток ошибок.
    sys.stdout = sys.stderr

    lock = threading.Lock()

    def emit(message: dict) -> None:
        with lock:
            write(json.dumps(message, ensure_ascii=False))

    device, compute_type = Transcriber._resolve_device(
        cfg.transcribe["device"], cfg.transcribe["compute_type"]
    )
    _preload()
    engine = Engine(cfg, emit)
    engine.start()
    emit({
        "event": "ready",
        "version": __version__,
        "device": device,
        "compute_type": compute_type,
    })

    try:
        for line in (_stdin_lines() if lines is None else lines):
            # PowerShell дописывает в начало потока метку кодировки (BOM), да и
            # вообще первый символ строки может оказаться служебным — JSON от
            # этого не разбирается, поэтому отрезаем.
            line = line.strip().lstrip("\ufeff").strip()
            if not line:
                continue
            try:
                command = json.loads(line)
            except ValueError:
                emit({"event": "error", "message": f"Не разобрал команду: {line[:120]}"})
                continue
            if not isinstance(command, dict):
                emit({"event": "error", "message": "Команда должна быть объектом JSON"})
                continue
            if command.get("cmd") == "shutdown":
                break
            engine.submit(command)
    finally:
        engine.stop()
        sys.stdout = stream
    return 0
