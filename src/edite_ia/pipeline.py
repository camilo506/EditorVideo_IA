from __future__ import annotations

import json
import uuid
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

from edite_ia.audio import extract_audio_wav
from edite_ia.clip import render_vertical_clip_with_subs
from edite_ia.highlights import detect_highlights, write_highlights_json
from edite_ia.layout import render_targets_for_layout
from edite_ia.models import Highlight, Utterance
from edite_ia.subs import utterances_for_clip, write_srt, write_vtt
from edite_ia.transcribe import transcribe_file


@dataclass
class PipelineConfig:
    input_video: Path
    output_dir: Path
    model_size: str = "small"
    device: str = "cpu"
    compute_type: str = "int8"
    language: str | None = None
    clip_length_sec: float = 30.0
    max_clips: int = 8
    keywords: list[str] = field(default_factory=list)
    width: int = 1080
    height: int = 1920
    burn_subtitles: bool = True
    # vertical | horizontal | both
    clip_layout: str = "vertical"
    # full | split_half | face_top | custom
    vertical_template: str = "full"
    custom_camera: dict[str, float] | None = None
    custom_content: dict[str, float] | None = None


@dataclass
class PipelineResult:
    work_dir: Path
    audio_wav: Path
    full_srt: Path
    full_vtt: Path
    highlights_json: Path
    utterances_json: Path
    clips: list[Path]
    language: str


def _write_utterances_json(utterances: list[Utterance], path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    data = [{"start": u.start, "end": u.end, "text": u.text} for u in utterances]
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def run_pipeline(
    cfg: PipelineConfig,
    *,
    on_stage: Callable[[str], None] | None = None,
) -> PipelineResult:
    def stage(name: str) -> None:
        if on_stage:
            on_stage(name)

    cfg.input_video = cfg.input_video.resolve()
    cfg.output_dir = cfg.output_dir.resolve()
    if not cfg.input_video.is_file():
        raise FileNotFoundError(cfg.input_video)

    targets = render_targets_for_layout(
        cfg.clip_layout,
        vertical_width=cfg.width,
        vertical_height=cfg.height,
    )

    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    work_dir = cfg.output_dir / f"run_{stamp}_{uuid.uuid4().hex[:8]}"
    work_dir.mkdir(parents=True, exist_ok=True)
    clips_dir = work_dir / "clips"
    clips_dir.mkdir(parents=True, exist_ok=True)

    stage("extract_audio")
    audio_wav = work_dir / "audio.wav"
    extract_audio_wav(cfg.input_video, audio_wav)

    stage("transcribe")
    utterances, lang = transcribe_file(
        cfg.input_video,
        model_size=cfg.model_size,
        device=cfg.device,
        compute_type=cfg.compute_type,
        language=cfg.language,
    )
    full_srt = work_dir / "full.srt"
    full_vtt = work_dir / "full.vtt"
    write_srt(utterances, full_srt)
    write_vtt(utterances, full_vtt)
    utterances_json = work_dir / "utterances.json"
    _write_utterances_json(utterances, utterances_json)

    stage("analyze_highlights")
    highlights: list[Highlight] = detect_highlights(
        audio_wav,
        utterances,
        video_path=cfg.input_video,
        clip_length_sec=cfg.clip_length_sec,
        max_clips=cfg.max_clips,
        keywords=cfg.keywords,
    )
    highlights_path = work_dir / "highlights.json"
    write_highlights_json(highlights, highlights_path)

    stage("render_clips")
    clips: list[Path] = []
    for i, h in enumerate(highlights, start=1):
        u_clip = utterances_for_clip(utterances, h.start, h.end)
        for t in targets:
            suffix = t.filename_suffix or ""
            out = clips_dir / f"clip_{i:02d}{suffix}.mp4"
            vtpl = cfg.vertical_template if t.height > t.width else "full"
            render_vertical_clip_with_subs(
                cfg.input_video,
                out,
                h.start,
                h.end,
                u_clip,
                width=t.width,
                height=t.height,
                burn_subtitles=cfg.burn_subtitles,
                vertical_template=vtpl,
                custom_camera=cfg.custom_camera if vtpl == "custom" else None,
                custom_content=cfg.custom_content if vtpl == "custom" else None,
            )
            clips.append(out)

    stage("done")
    return PipelineResult(
        work_dir=work_dir,
        audio_wav=audio_wav,
        full_srt=full_srt,
        full_vtt=full_vtt,
        highlights_json=highlights_path,
        utterances_json=utterances_json,
        clips=clips,
        language=lang,
    )
