"""Корни Windows: без них скачивание падает там, где антивирус подменяет сертификат."""

import os
import ssl

import pytest

from callsum import certs


@pytest.fixture(autouse=True)
def own_folder(tmp_path, monkeypatch):
    monkeypatch.setattr(certs.config, "local_dir", lambda: tmp_path)
    for name in certs.VARIABLES:
        monkeypatch.delenv(name, raising=False)


def test_roots_are_collected_and_libraries_are_pointed_at_them():
    path = certs.ensure()

    assert path is not None and path.is_file()
    text = path.read_text(encoding="utf-8")
    assert text.count("BEGIN CERTIFICATE") > 1, "в хранилище Windows корней всегда много"
    for name in certs.VARIABLES:
        assert os.environ[name] == str(path)


def test_own_choice_is_not_overridden(monkeypatch):
    """Свой набор корней человек указывает намеренно — его не трогаем."""
    monkeypatch.setenv("SSL_CERT_FILE", r"C:\свои\корни.pem")

    assert certs.ensure() is None
    assert os.environ["SSL_CERT_FILE"] == r"C:\свои\корни.pem"
    assert "REQUESTS_CA_BUNDLE" not in os.environ


def test_untrusted_roots_are_skipped(monkeypatch):
    """Корень, помеченный «не верить», не должен попасть в список."""
    good = ssl.PEM_cert_to_DER_cert(_any_certificate())
    monkeypatch.setattr(
        certs.ssl, "enum_certificates",
        lambda store: [(good, "x509_asn", True), (good, "x509_asn", False)])
    monkeypatch.setattr(certs, "_certifi_roots", lambda: "")

    path = certs.ensure()

    # Два хранилища по одному годному корню — и ни одного запрещённого.
    assert path.read_text(encoding="utf-8").count("BEGIN CERTIFICATE") == 2


def _any_certificate() -> str:
    """Любой настоящий корень из хранилища — как образец для подстановки."""
    for data, encoding, _trust in ssl.enum_certificates("ROOT"):
        if encoding == "x509_asn":
            return ssl.DER_cert_to_PEM_cert(data)
    pytest.skip("в хранилище Windows не нашлось ни одного корня")


def test_empty_list_of_roots_is_not_installed(monkeypatch):
    """Пустой набор корней не подменяет собой настоящие.

    Переменные читает не только ssl, но и requests — им качает модель
    huggingface_hub, — а он, в отличие от ssl, к хранилищу Windows уже не
    обращается: указанный файл для него единственный источник. Пустой файл
    означал бы «не верить никому»: скачивание падало бы там, где до нас
    работало (проверено — NO_CERTIFICATE_OR_CRL_FOUND).
    """
    monkeypatch.setattr(certs, "_collect", lambda: "   ")
    said: list[str] = []

    assert certs.ensure(log=said.append) is None
    assert said, "молчать об этом нельзя: скачивание останется как было"
    for name in certs.VARIABLES:
        assert name not in os.environ
