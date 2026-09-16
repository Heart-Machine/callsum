"""Секреты callsum в Диспетчере учётных данных Windows."""

from __future__ import annotations

import ctypes
import os
from ctypes import wintypes


OBS_WEBSOCKET_TARGET = "callsum/obs-websocket"
_GENERIC_CREDENTIAL = 1
_ERROR_NOT_FOUND = 1168


class CredentialsError(RuntimeError):
    """Windows не позволил прочитать сохранённый секрет."""


class _Credential(ctypes.Structure):
    _fields_ = [
        ("Flags", wintypes.DWORD),
        ("Type", wintypes.DWORD),
        ("TargetName", wintypes.LPWSTR),
        ("Comment", wintypes.LPWSTR),
        ("LastWritten", wintypes.FILETIME),
        ("CredentialBlobSize", wintypes.DWORD),
        ("CredentialBlob", ctypes.c_void_p),
        ("Persist", wintypes.DWORD),
        ("AttributeCount", wintypes.DWORD),
        ("Attributes", ctypes.c_void_p),
        ("TargetAlias", wintypes.LPWSTR),
        ("UserName", wintypes.LPWSTR),
    ]


def read_obs_password() -> str:
    """Вернуть пароль OBS из записи Windows или пустую строку, если её нет."""
    if os.name != "nt":
        return ""

    advapi = ctypes.WinDLL("Advapi32.dll", use_last_error=True)
    read = advapi.CredReadW
    read.argtypes = [
        wintypes.LPCWSTR,
        wintypes.DWORD,
        wintypes.DWORD,
        ctypes.POINTER(ctypes.POINTER(_Credential)),
    ]
    read.restype = wintypes.BOOL
    free = advapi.CredFree
    free.argtypes = [ctypes.c_void_p]
    free.restype = None

    credential = ctypes.POINTER(_Credential)()
    if not read(OBS_WEBSOCKET_TARGET, _GENERIC_CREDENTIAL, 0, ctypes.byref(credential)):
        error = ctypes.get_last_error()
        if error == _ERROR_NOT_FOUND:
            return ""
        raise CredentialsError("Не удалось прочитать пароль OBS из Диспетчера учётных данных Windows")

    try:
        size = credential.contents.CredentialBlobSize
        blob = credential.contents.CredentialBlob
        if size == 0 or not blob:
            return ""
        return ctypes.string_at(blob, size).decode("utf-16-le")
    finally:
        free(credential)
