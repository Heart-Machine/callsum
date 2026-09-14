"""Докачка FFmpeg: своя копия, если в системе его нет."""

import hashlib
import io
import json
import zipfile

import pytest

from callsum import ffmpeg


@pytest.fixture
def folder(tmp_path):
    return tmp_path / "ffmpeg"


def archive(names=("bin/ffmpeg.exe", "bin/ffprobe.exe", "bin/ffplay.exe", "doc/readme.txt")) -> bytes:
    """Архив, устроенный как сборка с gyan.dev: программы лежат в bin внутри папки с версией."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as package:
        for name in names:
            package.writestr(f"ffmpeg-9.0.1-essentials_build/{name}", b"exe" + name.encode())
    return buffer.getvalue()


@pytest.fixture
def network(monkeypatch):
    """Подменённая сеть: архив и его контрольная сумма, без интернета."""
    data = archive()
    answers = {
        ffmpeg.VERSION_URL: b"9.0.1",
        ffmpeg.RELEASE_URL + ".sha256": hashlib.sha256(data).hexdigest().encode(),
        ffmpeg.RELEASE_URL: data,
    }
    asked: list[str] = []

    class Answer(io.BytesIO):
        def __init__(self, payload: bytes):
            super().__init__(payload)
            self.headers = {"Content-Length": str(len(payload))}

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return False

    def fake_urlopen(url, timeout=None):
        asked.append(str(url))
        return Answer(answers[str(url)])

    monkeypatch.setattr(ffmpeg.urllib.request, "urlopen", fake_urlopen)
    return asked


def test_ready_system_ffmpeg_is_not_downloaded(folder, monkeypatch, network):
    """Свой FFmpeg человека трогать незачем — и качать двести мегабайт тоже."""
    monkeypatch.setattr(ffmpeg.shutil, "which", lambda name: rf"C:\ffmpeg\bin\{name}.exe")

    assert ffmpeg.ensure(folder) is None
    assert network == [], "в сеть ходить было незачем"


def test_missing_ffmpeg_is_downloaded(folder, monkeypatch, network):
    monkeypatch.setattr(ffmpeg.shutil, "which", lambda name: None)
    steps: list[tuple] = []

    ffmpeg.ensure(folder, on_progress=lambda fraction, detail: steps.append((fraction, detail)))

    assert (folder / "ffmpeg.exe").is_file()
    assert (folder / "ffprobe.exe").is_file()
    # Лишнего не берём: ffplay и документация — сто мегабайт впустую.
    assert not (folder / "ffplay.exe").exists()
    assert steps, "ход скачивания должен быть виден: это сотня мегабайт"
    assert steps[-1] == (1.0, "готово")
    assert json.loads((folder / ffmpeg.MARKER).read_text(encoding="utf-8"))["version"] == "9.0.1"


def test_own_copy_is_preferred_over_the_system_one(folder, monkeypatch):
    """Человек мог поставить FFmpeg после нас: работаем тем, что проверяли."""
    folder.mkdir(parents=True)
    (folder / "ffmpeg.exe").write_bytes(b"exe")
    monkeypatch.setattr(ffmpeg.shutil, "which", lambda name: rf"C:\другой\{name}.exe")

    assert ffmpeg.found("ffmpeg", folder) == str(folder / "ffmpeg.exe")
    assert ffmpeg.found("ffprobe", folder) == r"C:\другой\ffprobe.exe"


def test_broken_download_is_refused(folder, monkeypatch, network):
    """Битый архив хуже отсутствующего: причину искали бы в падении ffmpeg."""
    monkeypatch.setattr(ffmpeg.shutil, "which", lambda name: None)
    monkeypatch.setattr(ffmpeg, "_digest", lambda: "0" * 64)

    with pytest.raises(ffmpeg.FFmpegError, match="повреждён"):
        ffmpeg.ensure(folder)

    assert not (folder / "ffmpeg.exe").exists()


def test_archive_without_tools_says_what_to_do(folder, monkeypatch):
    """Сборку могли переименовать или переложить — человек не должен гадать."""
    data = archive(names=("bin/ffplay.exe",))

    class Answer(io.BytesIO):
        def __init__(self, payload: bytes):
            super().__init__(payload)
            self.headers = {"Content-Length": str(len(payload))}

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return False

    answers = {
        ffmpeg.VERSION_URL: b"9.0.1",
        ffmpeg.RELEASE_URL + ".sha256": hashlib.sha256(data).hexdigest().encode(),
        ffmpeg.RELEASE_URL: data,
    }
    monkeypatch.setattr(ffmpeg.urllib.request, "urlopen", lambda url, timeout=None: Answer(answers[str(url)]))
    monkeypatch.setattr(ffmpeg.shutil, "which", lambda name: None)

    with pytest.raises(ffmpeg.FFmpegError, match="winget install Gyan.FFmpeg"):
        ffmpeg.ensure(folder)


def test_audio_takes_our_copy(folder, monkeypatch, tmp_path):
    """Извлечение дорожек должно брать ту же копию, что мы скачали."""
    from callsum import audio

    folder.mkdir(parents=True)
    (folder / "ffmpeg.exe").write_bytes(b"exe")
    monkeypatch.setattr(ffmpeg, "target_dir", lambda: folder)
    monkeypatch.setattr(ffmpeg.shutil, "which", lambda name: None)

    assert audio._tool("ffmpeg") == str(folder / "ffmpeg.exe")
    with pytest.raises(audio.FFmpegMissing, match="winget install Gyan.FFmpeg"):
        audio._tool("ffprobe")
