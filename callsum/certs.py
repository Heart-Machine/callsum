"""Доверенные корни Windows — для всего, что программа скачивает.

Антивирусы и корпоративные прокси вклиниваются в HTTPS: соединение до сайта
устанавливают они, а программе отдают тот же ответ, подписанный собственным
корнем. В хранилище Windows этот корень есть — иначе браузер ругался бы на
каждой странице, — но библиотеки, которыми качают модель, берут корни не
оттуда, а из списка `certifi`, где его нет.

Проверено дорого: на машине с Kaspersky собранное ядро не смогло скачать
модель — «CERTIFICATE_VERIFY_FAILED: self-signed certificate in certificate
chain», — притом что тот же код из окружения разработчика качал свободно.
Перехват достаётся неподписанным программам, а привычный антивирусу python.exe
он пропускает. В офисе, где Kaspersky стоит у всех, коллега упёрся бы в это
на первом же созвоне.

Поэтому корни Windows складываются в один файл рядом с остальным скачанным,
и библиотеки отправляются брать корни оттуда.
"""

from __future__ import annotations

import os
import ssl
from pathlib import Path
from typing import Callable

from . import config

Log = Callable[[str], None]

# Кого отправляем к нашему файлу: переменные, которые понимают ssl, requests
# (им пользуется huggingface_hub) и curl-подобные клиенты.
VARIABLES = ("SSL_CERT_FILE", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE")


def bundle_path() -> Path:
    return config.local_dir() / "certs" / "roots.pem"


def ensure(log: Log | None = None) -> Path | None:
    """Собрать корни и сказать библиотекам, где их искать.

    Возвращает путь к файлу или None, если делать нечего: не Windows или
    человек уже указал свой набор корней — его выбор важнее нашего.
    """
    say = log or (lambda _: None)
    if os.name != "nt":
        return None

    if any(os.environ.get(name) for name in VARIABLES):
        return None

    try:
        roots = _collect()
        path = bundle_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(roots, encoding="utf-8")
    except OSError as exc:
        # Без этого файла скачивание просто останется таким, как было:
        # на большинстве машин оно и так работает.
        say(f"Не удалось собрать корневые сертификаты Windows: {exc}")
        return None

    for name in VARIABLES:
        os.environ[name] = str(path)
    return path


def _collect() -> str:
    """Список корней: сначала свои, привычные, потом всё, чему верит Windows."""
    parts: list[str] = []

    if (own := _certifi_roots()):
        parts.append(own)

    for store in ("ROOT", "CA"):
        for data, encoding, trust in ssl.enum_certificates(store):
            # trust бывает True или набором целей, для которых корень годится;
            # False означает «этому не верить» — такие пропускаем.
            if encoding == "x509_asn" and trust is not False:
                parts.append(ssl.DER_cert_to_PEM_cert(data))

    return "\n".join(parts)


def _certifi_roots() -> str:
    """Список корней, с которым программа собрана; без него обойдёмся."""
    try:
        import certifi

        return Path(certifi.where()).read_text(encoding="utf-8")
    except Exception:  # noqa: BLE001 — корни Windows важнее, и они есть всегда
        return ""
