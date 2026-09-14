"""Тесты скачивания модели распознавания: без сети, на подделках."""

import types
from pathlib import Path

import pytest

from callsum import config, models


class FakeUtils:
    """faster_whisper.utils в объёме, который нам нужен."""

    def __init__(self, cached=False, folder="D:/модель"):
        self.cached = cached
        self.folder = folder
        self.calls: list[dict] = []
        self.disabled_tqdm = object()

    def download_model(self, name, cache_dir=None, local_files_only=False):
        self.calls.append(
            {"name": name, "cache_dir": cache_dir, "local_files_only": local_files_only}
        )
        if local_files_only and not self.cached:
            raise FileNotFoundError("модели нет в кэше")
        return self.folder


@pytest.fixture
def cfg(tmp_path):
    import copy

    data = copy.deepcopy(config.DEFAULTS)
    return config.Config(data)


def test_already_downloaded_model_is_not_fetched_again(cfg, monkeypatch):
    """Три гигабайта второй раз качать незачем."""
    utils = FakeUtils(cached=True)
    monkeypatch.setattr(models, "_import_utils", lambda: utils)
    said: list[str] = []

    assert models.ensure(cfg, said.append) == "D:/модель"
    assert [call["local_files_only"] for call in utils.calls] == [True]
    # Сказать, что модель взята из кэша, нужно: иначе о судьбе трёх гигабайт
    # человек узнаёт только по тому, бегут мегабайты под таймером или нет.
    assert said == ["Модель large-v3 на месте: D:/модель"]


def test_missing_model_is_downloaded_and_announced(cfg, monkeypatch):
    utils = FakeUtils(cached=False)
    monkeypatch.setattr(models, "_import_utils", lambda: utils)
    said: list[str] = []

    assert models.ensure(cfg, said.append) == "D:/модель"
    assert [call["local_files_only"] for call in utils.calls] == [True, False]
    assert "Скачиваю модель" in said[0]


def test_failed_download_is_explained_and_not_fatal(cfg, monkeypatch):
    """Без модели распознавать нечем, но падать посреди созвона — хуже."""
    utils = FakeUtils(cached=False)

    def refuse(name, cache_dir=None, local_files_only=False):
        raise OSError("нет связи")

    utils.download_model = refuse
    monkeypatch.setattr(models, "_import_utils", lambda: utils)
    said: list[str] = []

    assert models.ensure(cfg, said.append) is None
    assert "Не удалось скачать модель" in said[-1]
    assert "связь" in said[-1]


def test_setting_decides_where_weights_live(cfg):
    cfg.transcribe["model_dir"] = r"D:\модели"

    assert str(models.model_dir(cfg)) == r"D:\модели"


def test_installed_app_keeps_weights_beside_other_downloads(cfg, tmp_path, monkeypatch):
    """Веса ложатся туда же, куда всё скачанное, а не внутрь папки программы:
    её очищает установщик."""
    monkeypatch.setattr(config, "_frozen", lambda: True)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))

    assert models.model_dir(cfg) == config.local_dir() / "models"
    assert str(models.model_dir(cfg)).startswith(str(tmp_path))


def test_from_sources_the_shared_cache_is_used(cfg, monkeypatch):
    """У разработчика модель уже лежит в кэше Hugging Face — второй копии не нужно."""
    monkeypatch.setattr(config, "_frozen", lambda: False)

    assert models.model_dir(cfg) is None


def test_progress_counts_all_files_together():
    """Файлов несколько, полоса одна: иначе она дёргалась бы на каждом мелком."""
    seen: list[tuple] = []
    counter = models._Counter(lambda fraction, detail: seen.append((fraction, detail)))

    counter.note(1, 500_000_000, 3_000_000_000)   # веса
    counter.note(2, 1_000, 1_000)                 # мелкий файл рядом

    assert seen[-1][0] == pytest.approx(500_001_000 / 3_000_001_000)
    assert "МБ" in seen[-1][1]


def test_only_byte_counters_are_taken(monkeypatch):
    """huggingface_hub заводит и полосу «сколько файлов осталось» — её не считаем."""
    seen: list[tuple] = []
    watcher = models._watcher(models._Counter(lambda f, d: seen.append((f, d))))

    files = watcher(total=5, unit="it")
    files.update(1)
    assert seen == []

    bytes_bar = watcher(total=1_000_000, unit="B")
    bytes_bar.update(250_000)
    assert seen and seen[-1][0] == pytest.approx(0.25)


# --- кэш из символических ссылок -------------------------------------------
@pytest.fixture
def links(tmp_path):
    """Папка со ссылкой на содержимое — как устроен кэш Hugging Face."""
    try:
        (tmp_path / "проба").symlink_to(tmp_path / "нет-такого")
    except OSError:
        pytest.skip("на этой машине ссылки не создаются")

    snapshot = tmp_path / "snapshot"
    snapshot.mkdir()
    blobs = tmp_path / "blobs"
    blobs.mkdir()
    (blobs / "0123456789").write_bytes(b"model")
    return snapshot, blobs


def test_symlinks_are_forbidden_before_downloading(cfg, monkeypatch):
    """Иначе три скачанных гигабайта окажутся нечитаемыми на ровном месте."""
    monkeypatch.delenv("HF_HUB_DISABLE_SYMLINKS", raising=False)
    monkeypatch.setattr(models, "_import_utils", lambda: FakeUtils(cached=True))

    models.ensure(cfg)

    assert models.os.environ["HF_HUB_DISABLE_SYMLINKS"] == "1"


def test_content_takes_the_place_of_the_link(links):
    """Содержимое лежит рядом: переставляем его, а не качаем три гигабайта."""
    snapshot, blobs = links
    link = snapshot / "model.bin"
    link.symlink_to(Path("..") / "blobs" / "0123456789")

    assert models._repair([link], lambda _: None) is True

    assert link.read_bytes() == b"model"
    assert not link.is_symlink(), "на месте ссылки должен лежать сам файл"
    assert not (blobs / "0123456789").exists(), "второй копии на диске быть не должно"


def test_working_link_is_not_touched(links):
    """Чинить нечего там, где всё открывается: на большинстве машин так и есть."""
    snapshot, _ = links
    link = snapshot / "model.bin"
    link.symlink_to(Path("..") / "blobs" / "0123456789")

    assert models._broken(snapshot) == []


def test_repair_without_content_asks_to_download_again(links):
    snapshot, _ = links
    link = snapshot / "model.bin"
    link.symlink_to(Path("..") / "blobs" / "нет-такого")

    assert models._broken(snapshot) == [link], "битая ссылка должна находиться"

    said: list[str] = []
    assert models._repair([link], said.append) is False
    assert "скачаю модель заново" in said[0].lower()


def test_unreadable_folder_does_not_look_broken(tmp_path):
    """Кэш может лежать где угодно; неизвестное не объявляем сломанным."""
    assert models._broken(tmp_path / "нет такой папки") == []
