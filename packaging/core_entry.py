"""Точка входа собранного ядра: то же, что `python -m callsum`."""

from callsum.cli import main

if __name__ == "__main__":
    raise SystemExit(main())
