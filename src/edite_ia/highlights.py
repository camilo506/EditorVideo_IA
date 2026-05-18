from __future__ import annotations

import json
from pathlib import Path

import librosa
import numpy as np
from scipy.signal import find_peaks

from edite_ia.models import Highlight, Utterance


def _merge_intervals(
    intervals: list[tuple[float, float, float]],
    gap: float,
) -> list[tuple[float, float, float]]:
    if not intervals:
        return []
    intervals = sorted(intervals, key=lambda x: x[0])
    merged: list[tuple[float, float, float]] = []
    cur_s, cur_e, cur_sc = intervals[0]
    for s, e, sc in intervals[1:]:
        if s <= cur_e + gap:
            cur_e = max(cur_e, e)
            cur_sc = max(cur_sc, sc)
        else:
            merged.append((cur_s, cur_e, cur_sc))
            cur_s, cur_e, cur_sc = s, e, sc
    merged.append((cur_s, cur_e, cur_sc))
    return merged


def _keyword_boost(
    utterances: list[Utterance],
    t0: float,
    t1: float,
    keywords: list[str],
) -> float:
    if not keywords:
        return 0.0
    kws = [k.lower() for k in keywords if k.strip()]
    if not kws:
        return 0.0
    for u in utterances:
        if u.end < t0 or u.start > t1:
            continue
        low = u.text.lower()
        if any(k in low for k in kws):
            return 0.35
    return 0.0


def detect_highlights(
    wav_path: Path,
    utterances: list[Utterance],
    *,
    duration: float | None = None,
    clip_length_sec: float = 30.0,
    max_clips: int = 8,
    energy_percentile: float = 82.0,
    peak_min_distance_sec: float = 12.0,
    keywords: list[str] | None = None,
) -> list[Highlight]:
    """
    Picos de energía (RMS) + refuerzo opcional por palabras clave en la transcripción.
    """
    wav_path = wav_path.resolve()
    y, sr = librosa.load(str(wav_path), sr=None, mono=True)
    if duration is None:
        duration = float(librosa.get_duration(y=y, sr=sr))

    hop = 512
    frame = 2048
    rms = librosa.feature.rms(y=y, frame_length=frame, hop_length=hop)[0]
    times = librosa.frames_to_time(np.arange(len(rms)), sr=sr, hop_length=hop)
    if len(rms) == 0:
        end = min(duration, clip_length_sec)
        return [Highlight(0.0, end, 0.5)]

    thr = np.percentile(rms, energy_percentile)
    min_dist_frames = max(1, int(peak_min_distance_sec * sr / hop))
    peaks, props = find_peaks(
        rms,
        height=float(thr),
        distance=min_dist_frames,
    )
    kws = keywords or []

    raw: list[tuple[float, float, float]] = []
    half = clip_length_sec / 2.0
    for idx in peaks:
        peak_t = float(times[idx])
        score = float(rms[idx])
        s = max(0.0, peak_t - half)
        e = min(duration, peak_t + half)
        if e - s < clip_length_sec * 0.6:
            if s == 0.0:
                e = min(duration, s + clip_length_sec)
            elif e >= duration - 1e-6:
                s = max(0.0, duration - clip_length_sec)
        score += _keyword_boost(utterances, s, e, kws)
        raw.append((s, e, score))

    raw = _merge_intervals(raw, gap=1.0)
    raw.sort(key=lambda x: x[2], reverse=True)
    raw = raw[: max(1, max_clips)]

    if not raw:
        end = min(duration, clip_length_sec)
        return [Highlight(0.0, end, 0.5)]

    return [Highlight(start=a, end=b, score=c) for a, b, c in raw]


def write_highlights_json(highlights: list[Highlight], path: Path) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    data = [{"start": h.start, "end": h.end, "score": h.score} for h in highlights]
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")
