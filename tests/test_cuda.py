"""Тесты докачки библиотек CUDA: без сети, на поддельных колёсах."""

import json
import os
import zipfile

import pytest

from callsum import cuda


def wheel(path, files):
    """Колесо, как его отдаёт PyPI: библиотеки лежат в nvidia/<пакет>/bin."""
    with zipfile.ZipFile(path, "w") as archive:
        for name, content in files.items():
            archive.writestr(name, content)
    return path


def test_only_libraries_are_taken_from_the_wheel(tmp_path):
    """Из колеса нужны библиотеки, а не метаданные и заголовки на мегабайты."""
    source = wheel(tmp_path / "cublas.whl", {
        "nvidia/cublas/bin/cublas64_12.dll": "библиотека",
        "nvidia/cublas/include/cublas.h": "заголовок",
        "nvidia_cublas_cu12-12.9.2.10.dist-info/METADATA": "описание",
    })
    folder = tmp_path / "cuda"
    folder.mkdir()

    cuda._extract(source, folder)

    assert [item.name for item in folder.iterdir()] == ["cublas64_12.dll"]
    assert (folder / "cublas64_12.dll").read_text(encoding="utf-8") == "библиотека"


def test_broken_archive_says_what_happened(tmp_path):
    (tmp_path / "битое.whl").write_text("это не архив", encoding="utf-8")

    with pytest.raises(cuda.CudaError) as failure:
        cuda._extract(tmp_path / "битое.whl", tmp_path)

    assert "распаковать" in str(failure.value)


def test_windows_build_is_chosen(monkeypatch):
    """У пакета есть колёса под несколько систем — нужно ровно одно."""
    monkeypatch.setattr(cuda, "_read_json", lambda _: {"urls": [
        {"filename": "nvidia_cublas_cu12-12.9.2.10-py3-none-manylinux_x86_64.whl",
         "url": "https://пример/linux.whl", "digests": {"sha256": "линукс"}},
        {"filename": "nvidia_cublas_cu12-12.9.2.10-py3-none-win_amd64.whl",
         "url": "https://пример/windows.whl", "digests": {"sha256": "виндоус"}},
    ]})

    assert cuda._wheel("nvidia-cublas-cu12", "12.9.2.10") == (
        "https://пример/windows.whl", "виндоус")


def test_missing_windows_build_is_explained(monkeypatch):
    monkeypatch.setattr(cuda, "_read_json", lambda _: {"urls": [
        {"filename": "nvidia_cublas_cu12-12.9.2.10-py3-none-manylinux_x86_64.whl",
         "url": "https://пример/linux.whl", "digests": {}},
    ]})

    with pytest.raises(cuda.CudaError) as failure:
        cuda._wheel("nvidia-cublas-cu12", "12.9.2.10")

    assert "под Windows" in str(failure.value)


def test_downloaded_packages_are_remembered(tmp_path):
    """Иначе при каждом запуске пришлось бы гадать по набору файлов."""
    cuda._remember(tmp_path, "nvidia-cublas-cu12", "12.9.2.10")

    assert cuda.installed(tmp_path) == {"nvidia-cublas-cu12": "12.9.2.10"}
    assert ("nvidia-cublas-cu12", "12.9.2.10") not in cuda.missing(tmp_path)


def test_other_version_counts_as_missing(tmp_path):
    """Обновление callsum может принести другие версии библиотек."""
    (tmp_path / cuda.MARKER).write_text(
        json.dumps({name: "0.0.0" for name, _ in cuda.PACKAGES}), encoding="utf-8")

    assert cuda.missing(tmp_path) == list(cuda.PACKAGES)


def test_everything_in_place_downloads_nothing(tmp_path, monkeypatch):
    for name, version in cuda.PACKAGES:
        cuda._remember(tmp_path, name, version)

    def refuse(*args, **kwargs):
        raise AssertionError("качать нечего")

    monkeypatch.setattr(cuda, "_fetch", refuse)

    assert cuda.ensure(tmp_path) == tmp_path


def test_progress_runs_through_all_packages_once(tmp_path, monkeypatch):
    """Полоса должна пройти от нуля до конца один раз, а не трижды."""
    fractions: list[float] = []

    def fake_fetch(name, version, folder, report, say):
        report(0.0)
        report(0.5)
        report(1.0)

    monkeypatch.setattr(cuda, "_fetch", fake_fetch)

    cuda.ensure(tmp_path, on_progress=lambda fraction, detail: fractions.append(fraction))

    assert fractions == sorted(fractions), "доля не должна откатываться назад"
    assert fractions[0] == 0.0
    assert fractions[-1] == 1.0
    assert cuda.missing(tmp_path) == []


def test_corrupted_download_is_refused(tmp_path, monkeypatch):
    """Битый файл хуже отсутствующего: искать причину пришлось бы в недрах."""
    class FakeResponse:
        headers = {"Content-Length": "6"}

        def read(self, _size=None):
            data = b"" if getattr(self, "_done", False) else "данные".encode("utf-8")
            self._done = True
            return data

        def __enter__(self):
            return self

        def __exit__(self, *args):
            return False

    monkeypatch.setattr(cuda.urllib.request, "urlopen", lambda *a, **kw: FakeResponse())

    with pytest.raises(cuda.CudaError) as failure:
        cuda._download("https://пример/wheel.whl", tmp_path / "wheel.whl", "не та сумма",
                       lambda fraction, detail: None)

    assert "повреждён" in str(failure.value)


def test_downloaded_libraries_land_in_the_search_path(tmp_path, monkeypatch):
    """Скачанные библиотеки должны попасть в PATH, а не только в список каталогов.

    CTranslate2 грузит cublas64_12.dll сам, обычным LoadLibrary, а тот смотрит
    в PATH и про os.add_dll_directory не знает. Пока библиотеки лежали рядом
    с ядром, это было незаметно: папку программы Windows ищет всегда — а после
    переезда в профиль пользователя ядро перестало их находить.
    """
    from callsum import transcribe

    folder = tmp_path / "cuda"
    folder.mkdir()
    monkeypatch.setattr(transcribe.sys, "frozen", True, raising=False)
    monkeypatch.setattr(transcribe.sys, "executable", str(tmp_path / "callsum-core.exe"))
    monkeypatch.setattr(cuda, "target_dir", lambda: folder)
    monkeypatch.setenv("PATH", "")

    transcribe._register_cuda_dlls()

    assert str(folder) in transcribe.os.environ["PATH"].split(os.pathsep)
