"""Клиент протокола obs-websocket 5.

Протокол простой: JSON поверх websocket. Сообщения помечены полем `op`:

    0  Hello       — сервер представился и (если включён пароль) прислал вызов
    1  Identify    — отвечаем ответом на вызов и списком нужных событий
    2  Identified  — соединение готово
    5  Event       — событие от OBS
    6  Request     — наш запрос с произвольным requestId
    7  RequestResponse — ответ с тем же requestId

Запросы и события идут по одному соединению: фоновый поток читает сообщения,
ответы раздаёт ожидающим запросам по requestId, события — обработчику.
"""

from __future__ import annotations

import base64
import hashlib
import json
import threading
import uuid
from typing import Any, Callable

import websocket

# Битовая маска подписок из протокола: события вывода (в том числе записи).
SUBSCRIPTION_OUTPUTS = 1 << 6


class ObsWsError(RuntimeError):
    """Ошибка соединения или запроса к OBS."""


def build_auth(password: str, salt: str, challenge: str) -> str:
    """Ответ на вызов сервера.

    По протоколу: base64(sha256(base64(sha256(пароль + соль)) + вызов)).
    """
    secret = base64.b64encode(hashlib.sha256((password + salt).encode()).digest()).decode()
    return base64.b64encode(hashlib.sha256((secret + challenge).encode()).digest()).decode()


class ObsWsClient:
    """Подключение к OBS: запросы и события по одному соединению."""

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 4455,
        password: str = "",
        timeout: float = 5.0,
        subscriptions: int = SUBSCRIPTION_OUTPUTS,
    ):
        self.host = host
        self.port = port
        self.password = password
        self.timeout = timeout
        self.subscriptions = subscriptions
        self.on_event: Callable[[str, dict], None] | None = None

        self._ws: websocket.WebSocket | None = None
        self._reader: threading.Thread | None = None
        self._pending: dict[str, dict] = {}
        self._lock = threading.Lock()
        self._closing = False

    # --- соединение --------------------------------------------------
    def connect(self) -> None:
        ws = websocket.WebSocket()
        try:
            ws.connect(f"ws://{self.host}:{self.port}", timeout=self.timeout)
            hello = self._receive(ws)
            self._identify(ws, hello)
        except ObsWsError:
            ws.close()
            raise
        except Exception as exc:  # noqa: BLE001 — наружу отдаём одну понятную ошибку
            ws.close()
            raise ObsWsError(str(exc)) from exc
        # Дальше читаем без таймаута: соединение живёт между запросами,
        # а ожидание ответа ограничивает уже сам request().
        ws.settimeout(None)
        self._ws = ws
        self._closing = False
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()

    def _identify(self, ws: websocket.WebSocket, hello: dict) -> None:
        if hello.get("op") != 0:
            raise ObsWsError(f"Ожидался Hello, пришло: {hello}")
        data = {"rpcVersion": 1, "eventSubscriptions": self.subscriptions}
        auth = hello.get("d", {}).get("authentication")
        if auth:
            if not self.password:
                raise ObsWsError("OBS требует пароль websocket, а он не найден")
            data["authentication"] = build_auth(self.password, auth["salt"], auth["challenge"])
        ws.send(json.dumps({"op": 1, "d": data}))
        answer = self._receive(ws)
        if answer.get("op") != 2:
            raise ObsWsError(f"OBS не принял подключение: {answer}")

    @staticmethod
    def _receive(ws: websocket.WebSocket) -> dict:
        return json.loads(ws.recv())

    def close(self) -> None:
        self._closing = True
        ws, self._ws = self._ws, None
        if ws is not None:
            try:
                ws.close()
            except Exception:  # noqa: BLE001 — при разрыве связи ошибки закрытия не важны
                pass
        with self._lock:
            waiters = list(self._pending.values())
        for waiter in waiters:
            waiter["error"] = "Соединение с OBS закрыто"
            waiter["event"].set()

    @property
    def connected(self) -> bool:
        return self._ws is not None

    # --- чтение сообщений --------------------------------------------
    def _read_loop(self) -> None:
        while not self._closing and self._ws is not None:
            try:
                message = json.loads(self._ws.recv())
            except Exception:  # noqa: BLE001 — обрыв связи разбудит ожидающих ниже
                break
            if not message:
                break
            op = message.get("op")
            if op == 7:
                self._complete(message.get("d", {}))
            elif op == 5 and self.on_event is not None:
                payload = message.get("d", {})
                try:
                    self.on_event(payload.get("eventType", ""), payload.get("eventData") or {})
                except Exception:  # noqa: BLE001 — ошибка обработчика не должна рвать соединение
                    pass
        if not self._closing:
            self.close()

    def _complete(self, payload: dict) -> None:
        with self._lock:
            waiter = self._pending.pop(payload.get("requestId", ""), None)
        if waiter is None:
            return
        status = payload.get("requestStatus", {})
        if status.get("result"):
            waiter["data"] = payload.get("responseData") or {}
        else:
            comment = status.get("comment") or ""
            waiter["error"] = (
                f"{payload.get('requestType')}: код {status.get('code')}"
                + (f", {comment}" if comment else "")
            )
        waiter["event"].set()

    # --- запросы ------------------------------------------------------
    def request(self, request_type: str, data: dict[str, Any] | None = None) -> dict:
        """Отправить запрос и дождаться ответа. Возвращает responseData."""
        if self._ws is None:
            raise ObsWsError("Нет подключения к OBS")
        request_id = uuid.uuid4().hex
        waiter = {"event": threading.Event(), "data": {}, "error": None}
        with self._lock:
            self._pending[request_id] = waiter
        message = {
            "op": 6,
            "d": {"requestType": request_type, "requestId": request_id, "requestData": data or {}},
        }
        try:
            self._ws.send(json.dumps(message))
        except Exception as exc:  # noqa: BLE001
            with self._lock:
                self._pending.pop(request_id, None)
            raise ObsWsError(f"Не удалось отправить {request_type}: {exc}") from exc

        if not waiter["event"].wait(self.timeout):
            with self._lock:
                self._pending.pop(request_id, None)
            raise ObsWsError(f"OBS не ответил на {request_type} за {self.timeout:.0f} с")
        if waiter["error"]:
            raise ObsWsError(waiter["error"])
        return waiter["data"]
