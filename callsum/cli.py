"""Командный интерфейс callsum."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from . import config, summarize, watch
from .pipeline import process
from .transcribe import Transcriber


def _utf8_console() -> None:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass


def _expand(paths: list[str], cfg) -> list[Path]:
    """Раскрыть файлы и папки в список записей."""
    exts = {e.lower() for e in cfg.audio["extensions"]}
    out: list[Path] = []
    for raw in paths:
        p = Path(raw)
        if p.is_dir():
            out += sorted(f for f in p.rglob("*") if f.is_file() and f.suffix.lower() in exts)
        elif p.is_file():
            out.append(p)
        else:
            print(f"! Нет такого файла: {p}")
    return out


def cmd_process(args, cfg) -> int:
    files = _expand(args.paths, cfg)
    if not files:
        print("Нечего обрабатывать.")
        return 1
    out_root = Path(args.out) if args.out else cfg.path("out")
    transcriber: Transcriber | None = None
    failed = 0
    for src in files:
        if not args.force and (out_root / src.stem / "transcript.md").exists():
            print(f"= {src.name}: уже обработан (--force чтобы переделать)")
            continue
        try:
            if transcriber is None:
                transcriber = Transcriber(cfg)
            process(
                src, cfg, out_root=out_root, do_summary=not args.no_summary,
                keep_audio=args.keep_audio, transcriber=transcriber,
            )
        except Exception as exc:  # noqa: BLE001
            failed += 1
            print(f"! {src.name}: {exc}")
    return 1 if failed else 0


def cmd_summarize(args, cfg) -> int:
    """Пересобрать протокол по готовому транскрипту, не распознавая заново."""
    target = Path(args.transcript)
    if target.is_dir():
        target = target / "transcript.md"
    if not target.exists():
        print(f"! Нет транскрипта: {target}")
        return 1
    raw = target.read_text(encoding="utf-8")
    meta, _, dialog = raw.partition("\n---\n")
    dialog = (dialog or raw).strip()
    meta = "\n".join(line for line in meta.splitlines() if line.startswith("- "))
    try:
        text = summarize.summarize(dialog, meta, cfg)
    except summarize.OllamaError as exc:
        print(f"! {exc}")
        return 1
    dst = target.parent / "summary.md"
    dst.write_text(f"# Протокол созвона: {target.parent.name}\n\n{meta}\n\n{text}\n", encoding="utf-8")
    print(f"Протокол: {dst}")
    return 0


def cmd_watch(args, cfg) -> int:
    try:
        watch.run(cfg, once=args.once, interval=args.interval, do_summary=not args.no_summary)
    except KeyboardInterrupt:
        print("\nОстановлено.")
    return 0


def cmd_obs_setup(args, cfg) -> int:
    """Отдельный профиль OBS под callsum — общие настройки остаются нетронутыми."""
    from . import obs as obs_mod

    client = obs_mod.Obs(cfg=cfg)
    try:
        client.connect()
        obs_mod.ObsSetup(client, cfg).run(args.name)
    except obs_mod.ObsError as exc:
        print(f"! {exc}")
        return 1
    finally:
        client.close()
    return 0


def cmd_gui(args, cfg) -> int:
    """Окно + значок в трее: запись одной кнопкой и обработка сразу после неё."""
    from .gui import main as gui_main

    return gui_main(cfg)


def cmd_doctor(args, cfg) -> int:
    ok = True
    print(f"Конфиг: {cfg.source or 'дефолтный (config.toml не найден)'}")

    from . import audio

    for tool in ("ffmpeg", "ffprobe"):
        try:
            audio._tool(tool)
            print(f"[ok] {tool} найден")
        except audio.FFmpegMissing as exc:
            ok = False
            print(f"[!!] {exc}")

    try:
        import ctranslate2

        count = ctranslate2.get_cuda_device_count()
        print(f"[ok] CTranslate2 {ctranslate2.__version__}, видеокарт: {count}")
        if count == 0:
            print("     GPU не виден — распознавание пойдёт на CPU (медленно).")
    except Exception as exc:  # noqa: BLE001
        ok = False
        print(f"[!!] CTranslate2 не работает: {exc}")

    host = cfg.summary["host"]
    want = cfg.summary["model"]
    try:
        models = summarize.available_models(host)
        print(f"[ok] Ollama на {host}, моделей: {len(models)}")
        if any(m == want or m.startswith(want.split(':')[0] + ':') for m in models):
            print(f"[ok] модель для саммари доступна: {want}")
        else:
            ok = False
            print(f"[!!] нет модели {want}. Установить: ollama pull {want}")
    except summarize.OllamaError as exc:
        ok = False
        print(f"[!!] {exc}")

    from . import obs as obs_mod

    settings = obs_mod.read_settings(cfg)
    if not settings.enabled_in_obs:
        print(f"[  ] WebSocket-сервер OBS выключен. {obs_mod.SETUP_HINT}")
    else:
        client = obs_mod.Obs(settings)
        try:
            client.connect()
            active, _ = client.status()
            print(f"[ok] OBS на {settings.host}:{settings.port}"
                  f"{', идёт запись' if active else ''}")
            client.close()
        except obs_mod.ObsError as exc:
            print(f"[  ] {exc}")

    for key in ("recordings", "out"):
        p = cfg.path(key)
        print(f"[{'ok' if p.exists() else '  '}] папка {key}: {p}")

    print("\nВсё готово." if ok else "\nЕсть проблемы — см. строки [!!].")
    return 0 if ok else 1


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="callsum",
        description="Локальный пайплайн: запись созвона -> расшифровка -> протокол.",
    )
    parser.add_argument("-c", "--config", help="путь к config.toml")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("process", help="обработать файл или папку с записями")
    p.add_argument("paths", nargs="+", help="файлы записей или папки с ними")
    p.add_argument("--out", help="куда сложить результат")
    p.add_argument("--no-summary", action="store_true", help="только расшифровка, без протокола")
    p.add_argument("--keep-audio", action="store_true", help="оставить извлечённые WAV-дорожки")
    p.add_argument("--force", action="store_true", help="переделать, даже если результат уже есть")
    p.set_defaults(func=cmd_process)

    p = sub.add_parser("watch", help="следить за папкой записей и обрабатывать новые")
    p.add_argument("--once", action="store_true", help="один проход по папке и выход")
    p.add_argument("--interval", type=float, default=10.0, help="период опроса, с")
    p.add_argument("--no-summary", action="store_true")
    p.set_defaults(func=cmd_watch)

    p = sub.add_parser("summarize", help="пересобрать протокол по готовой расшифровке")
    p.add_argument("transcript", help="transcript.md или папка с ним")
    p.set_defaults(func=cmd_summarize)

    p = sub.add_parser("obs-setup", help="создать в OBS профиль под запись созвонов")
    p.add_argument("--name", default="callsum", help="имя профиля и коллекции сцен")
    p.set_defaults(func=cmd_obs_setup)

    p = sub.add_parser("gui", help="окно с кнопкой записи и значком в трее")
    p.set_defaults(func=cmd_gui)

    p = sub.add_parser("doctor", help="проверить окружение")
    p.set_defaults(func=cmd_doctor)
    return parser


def _report_config_error(message: str, graphical: bool) -> int:
    """Окно запускается через pythonw без консоли — там ошибку видно только окном."""
    print(message)
    if graphical:
        try:
            from PySide6.QtWidgets import QApplication, QMessageBox

            app = QApplication.instance() or QApplication([])
            QMessageBox.critical(None, "callsum: ошибка в config.toml", message)
            del app
        except Exception:  # noqa: BLE001 — если и окно не поднялось, остаётся печать
            pass
    return 1


def main(argv: list[str] | None = None) -> int:
    _utf8_console()
    args = build_parser().parse_args(argv)
    try:
        cfg = config.load(args.config)
    except config.ConfigError as exc:
        return _report_config_error(str(exc), args.command == "gui")
    return args.func(args, cfg)


if __name__ == "__main__":
    raise SystemExit(main())
