"""Управление записью в OBS через встроенный obs-websocket."""

from __future__ import annotations

import json
import os
import time
from dataclasses import dataclass
from pathlib import Path

from .obsws import ObsWsClient, ObsWsError

PROFILES_DIR = Path(os.environ.get("APPDATA", "")) / "obs-studio" / "basic" / "profiles"

WEBSOCKET_CONFIG = (
    Path(os.environ.get("APPDATA", ""))
    / "obs-studio" / "plugin_config" / "obs-websocket" / "config.json"
)

SETUP_HINT = (
    "В OBS: «Инструменты» → «Настройки WebSocket-сервера» → включить "
    "«Включить WebSocket-сервер». Пароль подхватится сам."
)

PROFILE_NAME = "callsum"

# Раскладка звука: микрофон — дорожка 1, всё, что играет в колонках, — дорожка 2.
TRACK_BY_KIND = {"wasapi_input_capture": 1, "wasapi_output_capture": 2}
DEFAULT_INPUT_NAMES = {"wasapi_input_capture": "Микрофон", "wasapi_output_capture": "Звук системы"}

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


def _tracks(active: int) -> dict[str, bool]:
    return {str(i): i == active for i in range(1, 7)}


class Obs:
    """Операции с OBS, нужные для записи созвонов.

    Отдельно от GUI, чтобы тем же кодом можно было управлять записью из CLI.
    """

    def __init__(self, settings: ObsSettings | None = None, cfg=None):
        self.settings = settings or read_settings(cfg)
        self._client: ObsWsClient | None = None

    # --- подключение -------------------------------------------------
    def connect(self) -> None:
        s = self.settings
        client = ObsWsClient(host=s.host, port=s.port, password=s.password, timeout=5)
        try:
            client.connect()
        except ObsWsError as exc:
            if not s.enabled_in_obs:
                raise ObsError(f"WebSocket-сервер OBS выключен. {SETUP_HINT}") from exc
            raise ObsError(
                f"Не удалось подключиться к OBS на {s.host}:{s.port}: {exc}. "
                "Проверьте, что OBS запущен. " + SETUP_HINT
            ) from exc
        self._client = client

    def close(self) -> None:
        if self._client is not None:
            self._client.close()
        self._client = None

    @property
    def connected(self) -> bool:
        return self._client is not None and self._client.connected

    def call(self, request_type: str, data: dict | None = None) -> dict:
        """Запрос к OBS с понятной ошибкой наружу."""
        if self._client is None:
            raise ObsError("Нет подключения к OBS")
        try:
            return self._client.request(request_type, data)
        except ObsWsError as exc:
            raise ObsError(str(exc)) from exc

    def subscribe_record_state(self, callback) -> None:
        """Подписка на старт/стоп записи — в том числе из самого OBS или по хоткею.

        callback(active: bool, path: str | None) вызывается из чужого потока.
        """
        if self._client is None:
            raise ObsError("Нет подключения к OBS")

        def dispatch(event_type: str, data: dict) -> None:
            if event_type != "RecordStateChanged":
                return
            state = data.get("outputState", "")
            if state.endswith("_STARTED"):
                callback(True, None)
            elif state.endswith("_STOPPED"):
                callback(False, data.get("outputPath") or None)

        self._client.on_event = dispatch

    # --- запись ------------------------------------------------------
    def start_record(self) -> None:
        self.call("StartRecord")

    def stop_record(self) -> str | None:
        """Остановить запись и вернуть путь к файлу (OBS отдаёт его в ответе)."""
        return self.call("StopRecord").get("outputPath")

    def status(self) -> tuple[bool, float]:
        """(идёт ли запись, длительность в секундах)."""
        data = self.call("GetRecordStatus")
        return bool(data.get("outputActive")), float(data.get("outputDuration") or 0) / 1000.0

    def recording_folder(self) -> Path | None:
        try:
            return Path(self.call("GetRecordDirectory")["recordDirectory"])
        except (ObsError, KeyError):  # необязательная информация
            return None

    def set_recording_folder(self, folder: Path) -> None:
        self.call("SetRecordDirectory", {"recordDirectory": str(folder)})

    # --- профиль и коллекция сцен ------------------------------------
    def current_profile(self) -> str:
        return str(self.call("GetProfileList")["currentProfileName"])

    def profiles(self) -> list[str]:
        return list(self.call("GetProfileList")["profiles"])

    def create_profile(self, name: str) -> None:
        self.call("CreateProfile", {"profileName": name})

    def scene_collections(self) -> tuple[str, list[str]]:
        data = self.call("GetSceneCollectionList")
        return str(data["currentSceneCollectionName"]), list(data["sceneCollections"])

    def create_scene_collection(self, name: str) -> None:
        self.call("CreateSceneCollection", {"sceneCollectionName": name})

    def set_profile(self, name: str) -> None:
        """Переключить профиль и одноимённую коллекцию сцен.

        Настройки записи живут в профиле, а раскладка звука по дорожкам —
        в коллекции сцен, поэтому переключать нужно оба.
        """
        if self.current_profile() != name:
            self.call("SetCurrentProfile", {"profileName": name})
            time.sleep(1.0)
        current, available = self.scene_collections()
        if name in available and current != name:
            self.call("SetCurrentSceneCollection", {"sceneCollectionName": name})
            time.sleep(1.0)

    def set_profile_parameter(self, category: str, name: str, value: str) -> None:
        self.call(
            "SetProfileParameter",
            {"parameterCategory": category, "parameterName": name, "parameterValue": value},
        )

    def set_video_settings(self, fps: int, width: int, height: int) -> None:
        self.call(
            "SetVideoSettings",
            {
                "fpsNumerator": fps,
                "fpsDenominator": 1,
                "baseWidth": width,
                "baseHeight": height,
                "outputWidth": width,
                "outputHeight": height,
            },
        )

    # --- источники звука ---------------------------------------------
    def current_scene(self) -> str:
        data = self.call("GetCurrentProgramScene")
        return str(data.get("currentProgramSceneName") or data.get("sceneName") or "")

    def inputs(self) -> dict[str, str]:
        """{вид источника: имя источника} — имена зависят от языка OBS."""
        return {i["inputKind"]: i["inputName"] for i in self.call("GetInputList")["inputs"]}

    def create_input(self, scene: str, name: str, kind: str) -> None:
        self.call(
            "CreateInput",
            {
                "sceneName": scene,
                "inputName": name,
                "inputKind": kind,
                "inputSettings": {"device_id": "default"},
                "sceneItemEnabled": True,
            },
        )

    def set_input_track(self, name: str, track: int) -> None:
        self.call("SetInputAudioTracks", {"inputName": name, "inputAudioTracks": _tracks(track)})


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
        obs = self.obs
        previous = obs.current_profile()
        self.log(f"Текущий профиль OBS: «{previous}» — он останется нетронутым")

        if name not in obs.profiles():
            obs.create_profile(name)
            self.log(f"Создан профиль «{name}»")
        else:
            obs.set_profile(name)
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
            obs.set_profile_parameter(section, key, value)
        obs.set_recording_folder(rec_dir)
        self.log(f"Запись: mkv, дорожки 1+2, папка {rec_dir}")

        # Звук: 96 кбит/с на дорожку — для речи с запасом, а файл втрое легче.
        for track in (1, 2):
            obs.set_profile_parameter("AdvOut", f"Track{track}Bitrate", "96")
        self._write_encoder_settings(name)

        # Картинка для протокола не нужна, поэтому холст маленький и 10 кадров/с:
        # файл занимает копейки, а видеокодек почти не ест процессор.
        obs.set_video_settings(10, 640, 360)
        self.log("Видео: 640x360, 10 кадров/с (пустая картинка, нужен только звук)")

        _, collections = obs.scene_collections()
        if name not in collections:
            obs.create_scene_collection(name)
            # OBS перестраивает микшер в UI-потоке; если сразу лезть к
            # источникам, он успевает зависнуть — даём ему договорить.
            time.sleep(2.0)
            self.log(f"Создана коллекция сцен «{name}»")
        else:
            obs.set_profile(name)
            self.log(f"Коллекция сцен «{name}» уже была")

        self._route_audio()
        self._apply(name)
        self.log("Готово. Вернуть свои настройки: меню «Профиль» и «Коллекция сцен» в OBS.")

    def _apply(self, name: str) -> None:
        """Перечитать профиль: переключаем его туда-обратно.

        set_profile_parameter правит конфиг, но активный вывод OBS продолжает
        работать на старых значениях до перезагрузки профиля — без этого шага
        запись всё ещё шла бы в mp4 с одной дорожкой. Заодно OBS сохраняет
        конфиг на диск, и настройки переживают аварийное завершение.
        """
        others = [p for p in self.obs.profiles() if p != name]
        if not others:
            self.log("! Перезапустите OBS, чтобы настройки профиля вступили в силу")
            return
        self.obs.call("SetCurrentProfile", {"profileName": others[0]})
        time.sleep(1.5)
        self.obs.call("SetCurrentProfile", {"profileName": name})
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

    def _route_audio(self) -> None:
        """Развести микрофон и звук системы по разным дорожкам."""
        scene = self.obs.current_scene()
        inputs = self.obs.inputs()
        for kind, track in TRACK_BY_KIND.items():
            name = inputs.get(kind)
            if name is None:
                name = DEFAULT_INPUT_NAMES[kind]
                self.obs.create_input(scene, name, kind)
                time.sleep(1.0)
                self.log(f"Добавлен источник «{name}»")
            self.obs.set_input_track(name, track)
            self.log(f"«{name}» -> дорожка {track}")
