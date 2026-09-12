"""Тесты разбора настроек подключения к OBS."""

import json

from callsum import config, obs


def test_tracks_bitmap_leaves_one_track_active():
    assert obs._tracks(2) == {"1": False, "2": True, "3": False, "4": False, "5": False, "6": False}


def _write_obs_config(tmp_path, **fields):
    path = tmp_path / "config.json"
    path.write_text(json.dumps(fields), encoding="utf-8")
    return path


def test_password_and_port_come_from_obs_config(tmp_path, monkeypatch):
    """Пароль лежит в настройках OBS — спрашивать его у пользователя не нужно."""
    path = _write_obs_config(
        tmp_path, server_enabled=True, server_port=4466, auth_required=True,
        server_password="secret",
    )
    monkeypatch.setattr(obs, "WEBSOCKET_CONFIG", path)
    settings = obs.read_settings(config.load())
    assert (settings.port, settings.password, settings.enabled_in_obs) == (4466, "secret", True)


def test_disabled_server_is_reported(tmp_path, monkeypatch):
    path = _write_obs_config(tmp_path, server_enabled=False, server_port=4455, auth_required=False)
    monkeypatch.setattr(obs, "WEBSOCKET_CONFIG", path)
    assert obs.read_settings().enabled_in_obs is False


def test_missing_obs_config_falls_back_to_defaults(tmp_path, monkeypatch):
    monkeypatch.setattr(obs, "WEBSOCKET_CONFIG", tmp_path / "нет-такого.json")
    settings = obs.read_settings()
    assert (settings.host, settings.port, settings.password) == ("127.0.0.1", 4455, "")


def test_auth_disabled_means_no_password(tmp_path, monkeypatch):
    path = _write_obs_config(
        tmp_path, server_enabled=True, server_port=4455, auth_required=False,
        server_password="ignored",
    )
    monkeypatch.setattr(obs, "WEBSOCKET_CONFIG", path)
    assert obs.read_settings().password == ""
