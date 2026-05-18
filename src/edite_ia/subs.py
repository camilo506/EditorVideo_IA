from __future__ import annotations

from pathlib import Path

from edite_ia.models import Utterance


def _format_srt_ts(seconds: float) -> str:
    if seconds < 0:
        seconds = 0.0
    h = int(seconds // 3600)
    m = int((seconds % 3600) // 60)
    s = seconds % 60
    whole = int(s)
    ms = int(round((s - whole) * 1000))
    if ms >= 1000:
        ms = 0
        whole += 1
    return f"{h:02d}:{m:02d}:{whole:02d},{ms:03d}"


def _format_vtt_ts(seconds: float) -> str:
    if seconds < 0:
        seconds = 0.0
    h = int(seconds // 3600)
    m = int((seconds % 3600) // 60)
    s = seconds % 60
    return f"{h:02d}:{m:02d}:{s:06.3f}"


def write_srt(utterances: list[Utterance], path: Path) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    lines: list[str] = []
    for i, u in enumerate(utterances, start=1):
        lines.append(str(i))
        lines.append(f"{_format_srt_ts(u.start)} --> {_format_srt_ts(u.end)}")
        lines.append(u.text.strip().replace("\r\n", "\n"))
        lines.append("")
    path.write_text("\n".join(lines).strip() + "\n", encoding="utf-8")


def write_vtt(utterances: list[Utterance], path: Path) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = ["WEBVTT", ""]
    for u in utterances:
        lines.append(f"{_format_vtt_ts(u.start)} --> {_format_vtt_ts(u.end)}")
        lines.append(u.text.strip().replace("\r\n", "\n"))
        lines.append("")
    path.write_text("\n".join(lines).strip() + "\n", encoding="utf-8")


def utterances_for_clip(
    utterances: list[Utterance],
    clip_start: float,
    clip_end: float,
) -> list[Utterance]:
    out: list[Utterance] = []
    for u in utterances:
        if u.end <= clip_start or u.start >= clip_end:
            continue
        st = max(u.start, clip_start) - clip_start
        en = min(u.end, clip_end) - clip_start
        if en > st:
            out.append(Utterance(start=st, end=en, text=u.text))
    return out
