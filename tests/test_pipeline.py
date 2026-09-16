"""Конвейер сохраняет расшифровку, даже когда в записи не оказалось речи."""

import copy

from callsum import audio, config, pipeline, summarize


def test_empty_speech_does_not_create_a_hallucinated_summary(tmp_path, monkeypatch):
    data = copy.deepcopy(config.DEFAULTS)
    data["paths"] = {
        "recordings": str(tmp_path / "recordings"),
        "out": str(tmp_path / "out"),
        "folder_template": "{name}",
    }
    cfg = config.Config(data)
    source = tmp_path / "recordings" / "silence.mkv"
    source.parent.mkdir()
    source.write_bytes(b"recording")
    old_summary = cfg.path("out") / "silence" / "summary.md"
    old_summary.parent.mkdir(parents=True)
    old_summary.write_text("выдуманный протокол", encoding="utf-8")
    logs, stages = [], []

    class SilentTranscriber:
        def run(self, *_args, **_kwargs):
            return []

        def release(self):
            raise AssertionError("освобождать модель для пустой записи незачем")

    monkeypatch.setattr(pipeline.audio, "duration_seconds", lambda _src: 10.0)
    monkeypatch.setattr(pipeline.audio, "probe_tracks", lambda _src: [audio.Track(0, "", "", 1)])
    monkeypatch.setattr(
        pipeline.audio, "extract_track", lambda _src, _track, work: work / "track.wav")
    monkeypatch.setattr(summarize, "_generate", lambda *_args: AssertionError("Ollama не нужна"))

    result = pipeline.process(
        source,
        cfg,
        transcriber=SilentTranscriber(),
        log=logs.append,
        on_stage=lambda stage, *_args: stages.append(stage),
    )

    assert result.transcript_md.exists()
    assert not result.summary_md.exists()
    assert "summary" not in stages
    assert any("нет распознанной речи" in line for line in logs)
