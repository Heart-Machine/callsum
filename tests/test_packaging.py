"""Сценарии упаковки должны запускаться в штатном Windows PowerShell 5.1."""

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_release_script_has_utf8_bom_for_windows_powershell():
    """Без BOM PowerShell 5.1 ломает русские строки ещё до разбора скрипта."""
    source = (ROOT / "packaging" / "publish-release.ps1").read_bytes()

    assert source.startswith(b"\xef\xbb\xbf")
