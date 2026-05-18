from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

from edite_ia.crops import clamp_crop_rect
from edite_ia.ffmpeg_util import ffmpeg_escape_subtitle_path, require_ffmpeg
from edite_ia.layout import normalize_vertical_template
from edite_ia.models import Utterance
from edite_ia.subs import write_srt


def _q_expr(v: float) -> str:
    v = max(0.0, min(1.0, v))
    return f"{v:.6g}"


def _filter_complex_vertical_custom(
    width: int,
    height: int,
    cam: dict[str, float],
    cont: dict[str, float],
) -> str:
    """Dos recortes arbitrarios (coords normalizadas 0–1) apilados ~30/70 como salida."""
    cam = clamp_crop_rect(cam)
    cont = clamp_crop_rect(cont)
    h_top = int(round(height * 0.30))
    h_bot = height - h_top
    cx, cy, cw, ch = cam["x"], cam["y"], cam["w"], cam["h"]
    mx, my, mw, mh = cont["x"], cont["y"], cont["w"], cont["h"]
    sc_top = _scale_fit_pad(width, h_top)
    sc_bot = _scale_fit_pad(width, h_bot)
    return (
        f"[0:v]split=2[c1][c2];"
        f"[c1]crop=iw*{_q_expr(cw)}:ih*{_q_expr(ch)}:iw*{_q_expr(cx)}:ih*{_q_expr(cy)},"
        f"{sc_top}[top];"
        f"[c2]crop=iw*{_q_expr(mw)}:ih*{_q_expr(mh)}:iw*{_q_expr(mx)}:ih*{_q_expr(my)},"
        f"{sc_bot}[bot];"
        f"[top][bot]vstack=inputs=2[vout]"
    )


def _vf_fill_center(width: int, height: int) -> str:
    """Escala hasta cubrir WxH y recorta el centro (una sola fuente)."""
    return (
        f"scale={width}:{height}:force_original_aspect_ratio=increase,"
        f"crop={width}:{height}:(iw-{width})/2:(ih-{height})/2"
    )


def _scale_crop_center(width: int, height: int) -> str:
    """Escala para cubrir WxH y recorta el exceso por el centro (no desde 0,0)."""
    return (
        f"scale={width}:{height}:force_original_aspect_ratio=increase,"
        f"crop={width}:{height}:(iw-{width})/2:(ih-{height})/2,setsar=1"
    )


def _scale_fit_pad(width: int, height: int) -> str:
    """Escala para caber en WxH sin recortar la selección; bandas negras si hace falta."""
    return (
        f"scale={width}:{height}:force_original_aspect_ratio=decrease,"
        f"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1"
    )


def _filter_complex_vertical_split_half(width: int, height: int) -> str:
    """Mitad superior + mitad inferior del mismo video (50/50)."""
    h2 = height // 2
    sc = _scale_crop_center(width, h2)
    return (
        f"[0:v]split=2[va][vb];"
        f"[va]crop=iw:ih/2:0:0,{sc}[t];"
        f"[vb]crop=iw:ih/2:0:ih/2,{sc}[b];"
        f"[t][b]vstack=inputs=2[vout]"
    )


def _filter_complex_vertical_face_top(width: int, height: int) -> str:
    """
    Franja superior ~30% (zona típica cara/cámara) + resto abajo (gameplay / contenido),
    estilo Nexus, desde un solo archivo (recortes del mismo frame).
    """
    h_top = int(round(height * 0.30))
    h_bot = height - h_top
    sc_top = _scale_crop_center(width, h_top)
    sc_bot = _scale_crop_center(width, h_bot)
    return (
        f"[0:v]split=2[fx][fy];"
        f"[fx]crop=iw:ih*3/10:0:0,{sc_top}[top];"
        f"[fy]crop=iw:ih*7/10:0:ih*3/10,{sc_bot}[bot];"
        f"[top][bot]vstack=inputs=2[vout]"
    )


def _video_stage_args(
    width: int,
    height: int,
    vertical_template: str,
    custom_camera: dict[str, float] | None = None,
    custom_content: dict[str, float] | None = None,
) -> tuple[str | None, str | None]:
    """
    Salida apaisada: un solo -vf.
    Salida vertical: según plantilla (full = -vf; resto = -filter_complex → [vout]).
    """
    if width >= height:
        return (_vf_fill_center(width, height), None)

    vt = normalize_vertical_template(vertical_template)
    if vt == "full":
        return (_vf_fill_center(width, height), None)
    if vt == "split_half":
        return (None, _filter_complex_vertical_split_half(width, height))
    if vt == "face_top":
        return (None, _filter_complex_vertical_face_top(width, height))
    if vt == "custom":
        if not custom_camera or not custom_content:
            raise ValueError(
                "vertical_template=custom requiere custom_camera y custom_content."
            )
        return (
            None,
            _filter_complex_vertical_custom(
                width, height, custom_camera, custom_content
            ),
        )
    return (_vf_fill_center(width, height), None)


def render_vertical_clip_with_subs(
    input_video: Path,
    output_mp4: Path,
    clip_start: float,
    clip_end: float,
    utterances_clip: list[Utterance],
    *,
    width: int = 1080,
    height: int = 1920,
    burn_subtitles: bool = True,
    vertical_template: str = "full",
    custom_camera: dict[str, float] | None = None,
    custom_content: dict[str, float] | None = None,
    video_codec: str = "libx264",
    audio_codec: str = "aac",
    preset: str = "fast",
    crf: str = "23",
) -> Path:
    input_video = input_video.resolve()
    output_mp4 = output_mp4.resolve()
    output_mp4.parent.mkdir(parents=True, exist_ok=True)

    duration = max(0.01, clip_end - clip_start)
    tmp = output_mp4.with_suffix(".tmp_nosub.mp4")
    if tmp.exists():
        tmp.unlink()

    simple_vf, fc = _video_stage_args(
        width,
        height,
        vertical_template,
        custom_camera=custom_camera,
        custom_content=custom_content,
    )
    cmd1: list[str] = [
        require_ffmpeg(),
        "-hide_banner",
        "-loglevel",
        "error",
        "-y",
        "-ss",
        f"{clip_start:.3f}",
        "-i",
        str(input_video),
        "-t",
        f"{duration:.3f}",
    ]
    if fc is not None:
        cmd1.extend(
            [
                "-filter_complex",
                fc,
                "-map",
                "[vout]",
                "-map",
                "0:a?",
            ]
        )
    else:
        cmd1.extend(
            [
                "-vf",
                simple_vf or _vf_fill_center(width, height),
                "-map",
                "0:v",
                "-map",
                "0:a?",
            ]
        )

    cmd1.extend(
        [
            "-c:v",
            video_codec,
            "-preset",
            preset,
            "-crf",
            crf,
            "-c:a",
            audio_codec,
            "-b:a",
            "128k",
            "-movflags",
            "+faststart",
            str(tmp),
        ]
    )
    subprocess.run(cmd1, check=True)

    if burn_subtitles and utterances_clip:
        srt_path = output_mp4.with_suffix(".srt")
        write_srt(utterances_clip, srt_path)
        sub_path = ffmpeg_escape_subtitle_path(srt_path)
        force = "Fontsize=22,Outline=2,Shadow=1,MarginV=60"
        vf2 = f"subtitles='{sub_path}':charenc=UTF-8:force_style='{force}'"

        cmd2 = [
            require_ffmpeg(),
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            str(tmp),
            "-vf",
            vf2,
            "-c:v",
            video_codec,
            "-preset",
            preset,
            "-crf",
            crf,
            "-c:a",
            "copy",
            "-movflags",
            "+faststart",
            str(output_mp4),
        ]
        subprocess.run(cmd2, check=True)
        if tmp.exists():
            tmp.unlink()
        return output_mp4

    if utterances_clip:
        write_srt(utterances_clip, output_mp4.with_suffix(".srt"))

    shutil.move(str(tmp), str(output_mp4))
    return output_mp4
