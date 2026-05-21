from __future__ import annotations

import json
import re
import subprocess
from pathlib import Path

import librosa
import numpy as np
from scipy.signal import find_peaks

from edite_ia.ffmpeg_util import require_ffmpeg
from edite_ia.models import Highlight, Utterance

# Palabras / reacciones frecuentes en streams y gameplays (es + en)
_EMOTION_LEXICON = frozenset(
    {
        "no", "nooo", "noo", "way", "bro", "wtf", "omg", "dio", "dios", "mierda",
        "joder", "puta", "hostia", "increible", "increíble", "epico", "épico",
        "loco", "loca", "rotisimo", "rotísimo", "clutch", "victoria", "ganamos",
        "perdimos", "muerto", "muerte", "kill", "kills", "headshot", "doble",
        "triple", "quad", "pentakill", "fail", "rage", "grito", "grita", "risa",
        "jaja", "jeje", "lol", "lmao", "insane", "crazy", "imposible", "mentira",
        "que", "como", "cómo", "hdp", "tremendo", "god", "goat", "pro", "noob",
        "boss", "final", "gané", "gane", "perdí", "perdi", "ayuda", "corre",
        "cuidado", "explosion", "explosión", "boom", "what", "lets", "go",
    }
)

_SCENE_TIME_RE = re.compile(r"pts_time:([\d.]+)")

# Pesos del score combinado (suman ~1.0 antes de penalizaciones)
_W_ENERGY = 0.32
_W_SPIKE = 0.14
_W_SPEECH = 0.28
_W_KEYWORD = 0.12
_W_MOTION = 0.14


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
            return 1.0
    return 0.0


def _text_excitement(text: str) -> float:
    """0–1: palabras emocionales, signos, mayúsculas, alargamientos."""
    raw = (text or "").strip()
    if not raw:
        return 0.0

    low = raw.lower()
    words = re.findall(r"\w+", low, flags=re.UNICODE)
    score = 0.0
    for w in words:
        if w in _EMOTION_LEXICON:
            score += 0.18
        elif len(w) >= 4 and any(w.startswith(p) for p in ("no", "bro", "wtf", "omg")):
            score += 0.08

    score += min(0.35, raw.count("!") * 0.11)
    score += min(0.2, raw.count("?") * 0.07)
    if re.search(r"(.)\1{2,}", low):
        score += 0.15
    letters = [c for c in raw if c.isalpha()]
    if letters:
        caps_ratio = sum(1 for c in letters if c.isupper()) / len(letters)
        if caps_ratio > 0.45:
            score += 0.12

    return min(1.0, score)


def _speech_score_for_window(
    utterances: list[Utterance],
    t0: float,
    t1: float,
) -> float:
    best = 0.0
    speech_sec = 0.0
    for u in utterances:
        if u.end < t0 or u.start > t1:
            continue
        ov_s = max(t0, u.start)
        ov_e = min(t1, u.end)
        if ov_e > ov_s:
            speech_sec += ov_e - ov_s
        best = max(best, _text_excitement(u.text))

    if speech_sec <= 0:
        return 0.0

    dur = max(1e-6, t1 - t0)
    density = min(1.0, (speech_sec / dur) * 1.2)
    wps_bonus = 0.0
    text_len = sum(len(u.text.split()) for u in utterances if u.end >= t0 and u.start <= t1)
    if speech_sec > 0.5:
        wps = text_len / speech_sec
        if wps > 2.8:
            wps_bonus = min(0.25, (wps - 2.8) * 0.08)

    return min(1.0, best * 0.65 + density * 0.2 + wps_bonus)


def _scene_change_times(video_path: Path, threshold: float = 0.32) -> list[float]:
    """Picos de cambio de escena vía ffmpeg (movimiento / cortes)."""
    video_path = video_path.resolve()
    if not video_path.is_file():
        return []

    cmd = [
        require_ffmpeg(),
        "-hide_banner",
        "-loglevel",
        "info",
        "-i",
        str(video_path),
        "-filter:v",
        f"select='gt(scene,{threshold})',showinfo",
        "-an",
        "-f",
        "null",
        "-",
    ]
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, check=False)
    except OSError:
        return []

    text = (proc.stderr or "") + (proc.stdout or "")
    times: list[float] = []
    for m in _SCENE_TIME_RE.finditer(text):
        try:
            times.append(float(m.group(1)))
        except ValueError:
            continue
    return times


def _motion_score_timeline(
    scene_times: list[float],
    duration: float,
    hop_sec: float,
) -> np.ndarray:
    n = max(1, int(duration / hop_sec) + 1)
    motion = np.zeros(n, dtype=np.float32)
    if not scene_times:
        return motion

    sigma = max(1.0, 2.5 / hop_sec)
    for t in scene_times:
        idx = int(t / hop_sec)
        if 0 <= idx < n:
            motion[idx] += 1.0
    kernel = int(max(3, sigma * 4)) | 1
    motion = np.convolve(motion, np.ones(kernel) / kernel, mode="same")
    peak = float(motion.max()) if motion.max() > 0 else 1.0
    return motion / peak


def _build_combined_scores(
    rms: np.ndarray,
    times: np.ndarray,
    duration: float,
    utterances: list[Utterance],
    keywords: list[str],
    scene_times: list[float],
    hop_sec: float = 1.0,
) -> tuple[np.ndarray, np.ndarray]:
    """Score 0–1 por ventana temporal (paso hop_sec)."""
    n = max(1, int(duration / hop_sec) + 1)
    t_axis = np.arange(n, dtype=np.float64) * hop_sec

    if len(rms) == 0:
        return t_axis, np.zeros(n, dtype=np.float64)

    rms_norm = rms.astype(np.float64)
    p95 = float(np.percentile(rms_norm, 95)) or 1.0
    rms_norm = np.clip(rms_norm / p95, 0.0, 1.0)

    rms_at_t = np.interp(t_axis, times, rms_norm)
    silence_thr = float(np.percentile(rms_norm, 18))

    motion = _motion_score_timeline(scene_times, duration, hop_sec)

    scores = np.zeros(n, dtype=np.float64)
    for i, t in enumerate(t_axis):
        t0, t1 = t, min(duration, t + hop_sec)

        energy = float(rms_at_t[i])
        lookback = max(0, i - int(3.0 / hop_sec))
        spike = max(0.0, energy - float(np.min(rms_at_t[lookback : i + 1])))

        speech = _speech_score_for_window(utterances, t0, t1)
        kw = _keyword_boost(utterances, t0, t1, keywords)
        mov = float(motion[i]) if i < len(motion) else 0.0

        combined = (
            _W_ENERGY * energy
            + _W_SPIKE * min(1.0, spike * 2.5)
            + _W_SPEECH * speech
            + _W_KEYWORD * kw
            + _W_MOTION * mov
        )

        if energy < silence_thr and speech < 0.08:
            combined *= 0.35
        elif energy < silence_thr * 1.5 and speech < 0.15:
            combined *= 0.6

        scores[i] = combined

    if n > 5:
        kernel = np.ones(3) / 3.0
        scores = np.convolve(scores, kernel, mode="same")

    return t_axis, scores


def detect_highlights(
    wav_path: Path,
    utterances: list[Utterance],
    *,
    video_path: Path | None = None,
    duration: float | None = None,
    clip_length_sec: float = 30.0,
    max_clips: int = 8,
    score_percentile: float = 78.0,
    peak_min_distance_sec: float = 12.0,
    keywords: list[str] | None = None,
) -> list[Highlight]:
    """
    Detecta momentos relevantes combinando:
    - energía y picos de audio (gritos, risas, subidas de volumen)
    - transcripción Whisper (palabras emocionales, ritmo, signos)
    - palabras clave opcionales del usuario
    - cambios de escena en el video (movimiento / cortes vía ffmpeg)
    """
    wav_path = wav_path.resolve()
    y, sr = librosa.load(str(wav_path), sr=None, mono=True)
    if duration is None:
        duration = float(librosa.get_duration(y=y, sr=sr))

    hop = 512
    frame = 2048
    rms = librosa.feature.rms(y=y, frame_length=frame, hop_length=hop)[0]
    times = librosa.frames_to_time(np.arange(len(rms)), sr=sr, hop_length=hop)

    scene_times: list[float] = []
    if video_path is not None:
        scene_times = _scene_change_times(video_path)

    hop_sec = 1.0
    t_axis, scores = _build_combined_scores(
        rms,
        times,
        duration,
        utterances,
        keywords or [],
        scene_times,
        hop_sec=hop_sec,
    )

    if scores.size == 0 or float(scores.max()) <= 0:
        end = min(duration, clip_length_sec)
        return [Highlight(0.0, end, 0.5)]

    thr = float(np.percentile(scores, score_percentile))
    min_dist = max(1, int(peak_min_distance_sec / hop_sec))
    peaks, _ = find_peaks(scores, height=thr, distance=min_dist)

    if len(peaks) == 0:
        peaks = np.array([int(np.argmax(scores))])

    half = clip_length_sec / 2.0
    raw: list[tuple[float, float, float]] = []
    for idx in peaks:
        peak_t = float(t_axis[idx])
        score = float(scores[idx])
        s = max(0.0, peak_t - half)
        e = min(duration, peak_t + half)
        if e - s < clip_length_sec * 0.6:
            if s == 0.0:
                e = min(duration, s + clip_length_sec)
            elif e >= duration - 1e-6:
                s = max(0.0, duration - clip_length_sec)
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
