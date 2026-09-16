"""Тесты разбора настроек подключения к OBS."""

import pytest

from callsum import config, credentials, obs


def test_tracks_bitmap_leaves_one_track_active():
    assert obs._tracks(2) == {"1": False, "2": True, "3": False, "4": False, "5": False, "6": False}


def test_connection_values_come_from_callsum_config(monkeypatch):
    monkeypatch.setattr(obs, "read_obs_password", lambda: "secret")
    cfg = config.Config({
        **config.DEFAULTS,
        "obs": {**config.DEFAULTS["obs"], "host": "localhost", "port": 4466},
    })

    settings = obs.read_settings(cfg)

    assert (settings.host, settings.port, settings.password) == ("localhost", 4466, "secret")


def test_old_zero_port_falls_back_to_obs_default(monkeypatch):
    monkeypatch.setattr(obs, "read_obs_password", lambda: "")
    cfg = config.Config({**config.DEFAULTS, "obs": {**config.DEFAULTS["obs"], "port": 0}})

    assert obs.read_settings(cfg).port == 4455


def test_unavailable_credential_store_does_not_break_old_window(monkeypatch):
    def fail() -> str:
        raise credentials.CredentialsError("Хранилище недоступно")

    monkeypatch.setattr(obs, "read_obs_password", fail)
    client = obs.Obs(cfg=config.Config(config.DEFAULTS))

    with pytest.raises(obs.ObsError, match="Хранилище недоступно"):
        client.connect()
