"""Управление записью в OBS через встроенный obs-websocket."""

from __future__ import annotations

import json
import logging
import os
import time
from dataclasses import dataclass
from pathlib import Path

PROFILES_DIR = Path(os.environ.get("APPDATA", "")) / "obs-studio" / "basic" / "profiles"

# Пустую чёрную картинку незачем писать с дефолтным битрейтом OBS: на CRF 32
# час записи занимает единицы мегабайт вместо гигабайта.
RECORD_ENCODER = {
    "bitrate": 500,
    "crf": 32,
    "keyint_sec": 4,
    "preset": "veryfast",
    "profile": "high",
    "rate_control": "CRF",
    "tune": "",
    "x264opts": "",
}

WEBSOCKET_CONFIG = (
    Path(os.environ.get("APPDATA", ""))
    / "obs-studio" / "plugin_config" / "obs-websocket" / "config.json"
)

SETUP_HINT = (
    "В OBS: «Инструменты» → «Настройки WebSocket-сервера» → включить "
    "«Включить WebSocket-сервер». Пароль подхватится сам."
)


# Библиотека печатает полный traceback при каждой неудачной попытке подключения,
# а мы переподключаемся по таймеру — в логе это выглядит как поломка.
logging.getLogger("obsws_python").setLevel(logging.CRITICAL)


class ObsError(RuntimeError):
    pass


@dataclass
class ObsSettings:
    host: str = "127.0.0.1"
    port: int = 4455
    password: str = ""
    enabled_in_obs: bool = True


def read_settings(cfg=None) -> ObsSettings:
    """Настройки подключения: из config.toml, недостающее — из конфига OBS.

    Пароль к websocket лежит в конфиге самого OBS, поэтому его не нужно
    ни спрашивать, ни хранить в config.toml.
    """
    section = dict(getattr(cfg, "data", {}).get("obs", {})) if cfg else {}
    settings = ObsSettings(
        host=str(section.get("host", "127.0.0.1")),
        # 0 означает «взять порт из настроек OBS»: там он и задаётся.
        port=int(section.get("port", 0)) or 4455,
        password=str(section.get("password", "")),
    )
    try:
        raw = json.loads(WEBSOCKET_CONFIG.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return settings
    settings.enabled_in_obs = bool(raw.get("server_enabled", False))
    if not int(section.get("port", 0) or 0):
        settings.port = int(raw.get("server_port", settings.port))
    if not settings.password and raw.get("auth_required", True):
        settings.password = str(raw.get("server_password", "") or "")
    return settings


class Obs:
    """Тонкая обёртка: подключение, старт/стоп записи, состояние.

    Отдельно от GUI, чтобы тем же кодом можно было управлять записью из CLI.
    """

    def __init__(self, settings: ObsSettings | None = None, cfg=None):
        self.settings = settings or read_settings(cfg)
        self._req = None
        self._events = None

    # --- подключение -------------------------------------------------
    def connect(self) -> None:
        import obsws_python as obsws

        s = self.settings
        try:
            self._req = obsws.ReqClient(
                host=s.host, port=s.port, password=s.password, timeout=5
            )
        except Exception as exc:  # noqa: BLE001 — наружу отдаём одну понятную ошибку
            if not s.enabled_in_obs:
                raise ObsError(f"WebSocket-сервер OBS выключен. {SETUP_HINT}") from exc
            raise ObsError(
                f"Не удалось подключиться к OBS на {s.host}:{s.port}: {exc}. "
                "Проверьте, что OBS запущен. " + SETUP_HINT
            ) from exc

    def close(self) -> None:
        for client in (self._events, self._req):
            try:
                if client is not None:
                    client.disconnect()
            except Exception:  # noqa: BLE001 — на выходе ошибки разрыва не важны
                pass
        self._req = self._events = None

    @property
    def connected(self) -> bool:
        return self._req is not None

    def _client(self):
        if self._req is None:
            raise ObsError("Нет подключения к OBS")
        return self._req

    def subscribe_record_state(self, callback) -> None:
        """Подписка на старт/стоп записи — в том числе из самого OBS или по хоткею.

        callback(active: bool, path: str | None) вызывается из чужого потока.
        """
        import obsws_python as obsws

        s = self.settings
        self._events = obsws.EventClient(host=s.host, port=s.port, password=s.password)

        def on_record_state_changed(data):
            state = getattr(data, "output_state", "")
            if state.endswith("_STARTED"):
                callback(True, None)
            elif state.endswith("_STOPPED"):
                callback(False, getattr(data, "output_path", None) or None)

        self._events.callback.register(on_record_state_changed)

    # --- команды -----------------------------------------------------
    def start_record(self) -> None:
        self._client().start_record()

    def stop_record(self) -> str | None:
        """Остановить запись и вернуть путь к файлу (OBS отдаёт его в ответе)."""
        resp = self._client().stop_record()
        return getattr(resp, "output_path", None)

    def status(self) -> tuple[bool, float]:
        """(идёт ли запись, длительность в секундах)."""
        resp = self._client().get_record_status()
        return bool(resp.output_active), float(getattr(resp, "output_duration", 0) or 0) / 1000.0

    # --- профиль и коллекция сцен ------------------------------------
    def current_profile(self) -> str:
        return self._client().get_profile_list().current_profile_name

    def profiles(self) -> list[str]:
        return list(self._client().get_profile_list().profiles)

    def set_profile(self, name: str) -> None:
        """Переключить профиль и одноимённую коллекцию сцен.

        Настройки записи живут в профиле, а раскладка звука по дорожкам —
        в коллекции сцен, поэтому переключать нужно оба.
        """
        req = self._client()
        if req.get_profile_list().current_profile_name != name:
            req.set_current_profile(name)
            time.sleep(1.0)
        collections = req.get_scene_collection_list()
        if name in collections.scene_collections and collections.current_scene_collection_name != name:
            req.set_current_scene_collection(name)
            time.sleep(1.0)

    def recording_folder(self) -> Path | None:
        try:
            return Path(self._client().get_record_directory().record_directory)
        except Exception:  # noqa: BLE001 — необязательная информация
            return None


PROFILE_NAME = "callsum"

# Раскладка звука: микрофон — дорожка 1, всё, что играет в колонках, — дорожка 2.
TRACK_BY_KIND = {"wasapi_input_capture": 1, "wasapi_output_capture": 2}
DEFAULT_INPUT_NAMES = {"wasapi_input_capture": "Микрофон", "wasapi_output_capture": "Звук системы"}


def _tracks(active: int) -> dict[str, bool]:
    return {str(i): i == active for i in range(1, 7)}


class ObsSetup:
    """Создание отдельного профиля и коллекции сцен под запись созвонов.

    Настройки OBS живут в двух переключаемых контейнерах: профиль хранит вывод
    (формат, дорожки, папка, видео), коллекция сцен — источники и раскладку звука
    по дорожкам. Поэтому callsum заводит свои и не трогает те, которыми
    пользователь пользуется для всего остального.
    """

    def __init__(self, client: Obs, cfg, log=print):
        self.obs = client
        self.cfg = cfg
        self.log = log

    def run(self, name: str = PROFILE_NAME) -> None:
        req = self.obs._client()
        previous = req.get_profile_list().current_profile_name
        self.log(f"Текущий профиль OBS: «{previous}» — он останется нетронутым")

        if name not in req.get_profile_list().profiles:
            req.create_profile(name)
            self.log(f"Создан профиль «{name}»")
        else:
            req.set_current_profile(name)
            self.log(f"Профиль «{name}» уже был, обновляю настройки")

        rec_dir = self.cfg.path("recordings")
        rec_dir.mkdir(parents=True, exist_ok=True)
        for section, key, value in (
            ("Output", "Mode", "Advanced"),
            ("Output", "FilenameFormatting", "%CCYY-%MM-%DD %hh-%mm-%ss созвон"),
            ("AdvOut", "RecType", "Standard"),
            ("AdvOut", "RecFormat2", "mkv"),
            # Битовая маска дорожек: 1 (микрофон) + 2 (звук системы) = 3.
            ("AdvOut", "RecTracks", "3"),
            ("AdvOut", "RecFilePath", str(rec_dir)),
            ("AdvOut", "RecEncoder", "obs_x264"),
        ):
            req.set_profile_parameter(section, key, value)
        req.set_record_directory(str(rec_dir))
        self.log(f"Запись: mkv, дорожки 1+2, папка {rec_dir}")

        # Звук: 96 кбит/с на дорожку — для речи с запасом, а файл втрое легче.
        for track in (1, 2):
            req.set_profile_parameter("AdvOut", f"Track{track}Bitrate", "96")
        self._write_encoder_settings(name)

        # Картинка для протокола не нужна, поэтому холст маленький и 10 кадров/с:
        # файл занимает копейки, а видеокодек почти не ест процессор.
        req.set_video_settings(10, 1, 640, 360, 640, 360)
        self.log("Видео: 640x360, 10 кадров/с (пустая картинка, нужен только звук)")

        collections = req.get_scene_collection_list().scene_collections
        if name not in collections:
            req.create_scene_collection(name)
            # OBS перестраивает микшер в UI-потоке; если сразу лезть к
            # источникам, он успевает зависнуть — даём ему договорить.
            time.sleep(2.0)
            self.log(f"Создана коллекция сцен «{name}»")
        else:
            req.set_current_scene_collection(name)
            self.log(f"Коллекция сцен «{name}» уже была")

        self._route_audio(req)
        self._apply(req, name)
        self.log("Готово. Вернуть свои настройки: меню «Профиль» и «Коллекция сцен» в OBS.")

    def _apply(self, req, name: str) -> None:
        """Перечитать профиль: переключаем его туда-обратно.

        set_profile_parameter правит конфиг, но активный вывод OBS продолжает
        работать на старых значениях до перезагрузки профиля — без этого шага
        запись всё ещё шла бы в mp4 с одной дорожкой. Заодно OBS сохраняет
        конфиг на диск, и настройки переживают аварийное завершение.
        """
        others = [p for p in req.get_profile_list().profiles if p != name]
        if not others:
            self.log("! Перезапустите OBS, чтобы настройки профиля вступили в силу")
            return
        req.set_current_profile(others[0])
        time.sleep(1.5)
        req.set_current_profile(name)
        time.sleep(1.5)
        self.log("Профиль перечитан, настройки записи активны")

    def _write_encoder_settings(self, profile: str) -> None:
        """Настройки видеокодека OBS хранит файлом рядом с профилем, не в конфиге."""
        folder = self._profile_dir(profile)
        if folder is None:
            self.log("! Папка профиля не найдена — битрейт видео остался по умолчанию")
            return
        (folder / "recordEncoder.json").write_text(
            json.dumps(RECORD_ENCODER, indent=4), encoding="utf-8"
        )
        self.log("Видеокодек: x264 CRF 32 (чёрная картинка весит копейки)")

    @staticmethod
    def _profile_dir(profile: str) -> Path | None:
        """Папка профиля: её имя OBS чистит от спецсимволов, поэтому ищем по basic.ini."""
        for folder in PROFILES_DIR.glob("*"):
            ini = folder / "basic.ini"
            if not ini.is_file():
                continue
            for line in ini.read_text(encoding="utf-8", errors="replace").splitlines():
                if line.strip() == f"Name={profile}":
                    return folder
        return None

    def _route_audio(self, req) -> None:
        """Развести микрофон и звук системы по разным дорожкам."""
        scene = req.get_current_program_scene().scene_name
        inputs = {i["inputKind"]: i["inputName"] for i in req.get_input_list().inputs}
        for kind, track in TRACK_BY_KIND.items():
            name = inputs.get(kind)
            if name is None:
                name = DEFAULT_INPUT_NAMES[kind]
                req.create_input(scene, name, kind, {"device_id": "default"}, True)
                time.sleep(1.0)
                self.log(f"Добавлен источник «{name}»")
            req.set_input_audio_tracks(name, _tracks(track))
            self.log(f"«{name}» -> дорожка {track}")
