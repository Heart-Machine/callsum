r"""Проверки склейки реплик. Запуск: .venvScriptspython.exe -m pytest tests -q"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from callsum.merge import merge_segments, stats, to_srt  # noqa: E402
from callsum.transcribe import Segment, _segments_from_words  # noqa: E402


class FakeWord:
    def __init__(self, start, end, word):
        self.start, self.end, self.word = start, end, word


class FakeSeg:
    def __init__(self, start, end, text, words=None):
        self.start, self.end, self.text, self.words = start, end, text, words


def test_dialog_order_follows_word_starts():
    """Начало реплики берётся по первому слову, а не по растянутому VAD сегменту."""
    a = _segments_from_words(
        FakeSeg(0.0, 12.0, "вопрос", [FakeWord(10.0, 12.0, " вопрос")]), "Я", 1.5
    )
    b = _segments_from_words(
        FakeSeg(0.0, 5.0, "ответ", [FakeWord(3.0, 5.0, " ответ")]), "Собеседник", 1.5
    )
    dialog = merge_segments([a, b])
    assert [s.speaker for s in dialog] == ["Собеседник", "Я"]
    assert dialog[0].start == 3.0


def test_long_pause_splits_segment():
    seg = FakeSeg(0.0, 20.0, "раз два", [
        FakeWord(0.0, 1.0, " раз"),
        FakeWord(15.0, 16.0, " два"),
    ])
    parts = _segments_from_words(seg, "Я", 1.5)
    assert [p.text for p in parts] == ["раз", "два"]


def test_same_speaker_glued_within_gap():
    segs = [Segment(0.0, 2.0, "первое", "Я"), Segment(3.0, 4.0, "второе", "Я")]
    merged = merge_segments([segs], gap=2.0)
    assert len(merged) == 1 and merged[0].text == "первое второе"


def test_gap_over_threshold_keeps_two_lines():
    segs = [Segment(0.0, 2.0, "первое", "Я"), Segment(9.0, 10.0, "второе", "Я")]
    assert len(merge_segments([segs], gap=2.0)) == 2


def test_stats_and_srt():
    segs = merge_segments([[Segment(0.0, 4.0, "а", "Я"), Segment(5.0, 9.0, "б", "Он")]])
    st = stats(segs)
    assert st["lines"] == 2
    assert round(st["per_speaker"]["Я"]["share"], 2) == 0.5
    assert "00:00:05,000 --> 00:00:09,000" in to_srt(segs)
