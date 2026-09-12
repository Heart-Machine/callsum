"""Тесты своего клиента obs-websocket: рукопожатие, запросы, события."""

import base64
import hashlib
import json
import queue

import pytest

from callsum import obsws
from callsum.obsws import ObsWsClient, ObsWsError, build_auth


def test_auth_follows_the_protocol_formula():
    password, salt, challenge = "пароль", "соль", "вызов"
    secret = base64.b64encode(hashlib.sha256((password + salt).encode()).digest()).decode()
    expected = base64.b64encode(hashlib.sha256((secret + challenge).encode()).digest()).decode()
    assert build_auth(password, salt, challenge) == expected


# Выдуманный ответ поддельного OBS. К диску никто не обращается: значение
# только проверяется на равенство, файла по этому пути не существует.
FAKE_RECORDING = "D:/созвон.mkv"


class FakeSocket:
    """Мини-OBS: отвечает на рукопожатие и на запросы по их requestId."""

    instances: list["FakeSocket"] = []

    def __init__(self, auth_required=True, fail_request=None):
        self.auth_required = auth_required
        self.fail_request = fail_request
        self.sent: list[dict] = []
        self.incoming: queue.Queue = queue.Queue()
        self.closed = False
        FakeSocket.instances.append(self)

    # --- то, что дёргает клиент ---
    def connect(self, url, timeout=None):
        self.url = url
        hello = {"op": 0, "d": {"rpcVersion": 1}}
        if self.auth_required:
            hello["d"]["authentication"] = {"challenge": "вызов", "salt": "соль"}
        self.incoming.put(json.dumps(hello))

    def settimeout(self, value):
        self.timeout = value

    def send(self, raw):
        message = json.loads(raw)
        self.sent.append(message)
        if message["op"] == 1:
            self.incoming.put(json.dumps({"op": 2, "d": {"negotiatedRpcVersion": 1}}))
        elif message["op"] == 6:
            data = message["d"]
            ok = data["requestType"] != self.fail_request
            self.incoming.put(json.dumps({
                "op": 7,
                "d": {
                    "requestType": data["requestType"],
                    "requestId": data["requestId"],
                    "requestStatus": (
                        {"result": True, "code": 100} if ok
                        else {"result": False, "code": 501, "comment": "запись не идёт"}
                    ),
                    "responseData": {"outputPath": FAKE_RECORDING} if ok else None,
                },
            }))

    def recv(self):
        return self.incoming.get()

    def close(self):
        self.closed = True
        self.incoming.put("")

    def push_event(self, event_type, data):
        self.incoming.put(json.dumps({"op": 5, "d": {"eventType": event_type, "eventData": data}}))


@pytest.fixture
def fake_obs(monkeypatch):
    FakeSocket.instances.clear()
    made = {}

    def factory(**kwargs):
        socket = FakeSocket(**made.get("kwargs", {}))
        return socket

    monkeypatch.setattr(obsws.websocket, "WebSocket", factory)
    return made


def test_handshake_sends_auth_answer(fake_obs):
    client = ObsWsClient(password="пароль")
    client.connect()
    identify = FakeSocket.instances[0].sent[0]
    assert identify["op"] == 1
    assert identify["d"]["authentication"] == build_auth("пароль", "соль", "вызов")
    assert identify["d"]["eventSubscriptions"] == obsws.SUBSCRIPTION_OUTPUTS
    client.close()


def test_missing_password_is_reported_clearly(fake_obs):
    client = ObsWsClient(password="")
    with pytest.raises(ObsWsError, match="пароль"):
        client.connect()


def test_request_returns_response_data(fake_obs):
    client = ObsWsClient(password="пароль")
    client.connect()
    assert client.request("StopRecord")["outputPath"] == FAKE_RECORDING
    assert FakeSocket.instances[0].sent[-1]["d"]["requestType"] == "StopRecord"
    client.close()


def test_failed_request_raises_with_obs_comment(fake_obs):
    fake_obs["kwargs"] = {"fail_request": "StopRecord"}
    client = ObsWsClient(password="пароль")
    client.connect()
    with pytest.raises(ObsWsError, match="501.*запись не идёт"):
        client.request("StopRecord")
    client.close()


def test_events_reach_the_callback(fake_obs):
    seen = []
    client = ObsWsClient(password="пароль")
    client.on_event = lambda event_type, data: seen.append((event_type, data))
    client.connect()
    FakeSocket.instances[0].push_event(
        "RecordStateChanged",
        {"outputState": "OBS_WEBSOCKET_OUTPUT_STOPPED", "outputPath": FAKE_RECORDING},
    )
    for _ in range(200):
        if seen:
            break
        import time

        time.sleep(0.01)
    client.close()
    assert seen == [
        ("RecordStateChanged",
         {"outputState": "OBS_WEBSOCKET_OUTPUT_STOPPED", "outputPath": FAKE_RECORDING}),
    ]


def test_request_without_connection_is_refused():
    with pytest.raises(ObsWsError, match="Нет подключения"):
        ObsWsClient().request("GetRecordStatus")
