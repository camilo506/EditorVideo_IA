from __future__ import annotations

import threading
import traceback
import uuid
from pathlib import Path
from typing import Any, Literal

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

from edite_ia.crops import clamp_crop_rect
from edite_ia.pipeline import PipelineConfig, run_pipeline

app = FastAPI(
    title="edite_ia",
    version="0.1.0",
    description="API local: video largo → clips de momentos relevantes (vertical y/u horizontal).",
)

_lock = threading.Lock()
_jobs: dict[str, dict[str, Any]] = {}


class CropRect(BaseModel):
    x: float = 0.25
    y: float = 0.04
    w: float = 0.38
    h: float = 0.26

    def to_clamped_dict(self) -> dict[str, float]:
        return clamp_crop_rect(
            {"x": self.x, "y": self.y, "w": self.w, "h": self.h}
        )


class JobCreate(BaseModel):
    input_path: str
    output_dir: str
    model_size: str = Field(default="small")
    device: str = Field(default="cpu")
    compute_type: str = Field(default="int8")
    language: str | None = None
    clip_length_sec: float = Field(default=30.0)
    max_clips: int = Field(default=8)
    keywords: list[str] = Field(default_factory=list)
    width: int = Field(default=1080)
    height: int = Field(default=1920)
    burn_subtitles: bool = Field(default=True)
    clip_layout: Literal["vertical", "horizontal", "both"] = Field(default="vertical")
    vertical_template: Literal["full", "split_half", "face_top", "custom"] = Field(
        default="full"
    )
    custom_camera: CropRect | None = None
    custom_content: CropRect | None = None


class JobStatus(BaseModel):
    id: str
    status: str
    stage: str
    message: str
    outputs: list[str] = Field(default_factory=list)
    work_dir: str | None = None
    language: str | None = None
    error: str | None = None


def _run_job(job_id: str, payload: JobCreate) -> None:
    def set_state(**kwargs: Any) -> None:
        with _lock:
            _jobs[job_id].update(kwargs)

    try:
        set_state(status="running", stage="starting", message="")
        cam_dict: dict[str, float] | None = None
        cont_dict: dict[str, float] | None = None
        if payload.vertical_template == "custom":
            cam_dict = (
                payload.custom_camera.to_clamped_dict()
                if payload.custom_camera
                else clamp_crop_rect(
                    {"x": 0.25, "y": 0.04, "w": 0.38, "h": 0.26}
                )
            )
            cont_dict = (
                payload.custom_content.to_clamped_dict()
                if payload.custom_content
                else clamp_crop_rect(
                    {"x": 0.0, "y": 0.30, "w": 1.0, "h": 0.68}
                )
            )

        cfg = PipelineConfig(
            input_video=Path(payload.input_path),
            output_dir=Path(payload.output_dir),
            model_size=payload.model_size,
            device=payload.device,
            compute_type=payload.compute_type,
            language=payload.language,
            clip_length_sec=payload.clip_length_sec,
            max_clips=payload.max_clips,
            keywords=payload.keywords,
            width=payload.width,
            height=payload.height,
            burn_subtitles=payload.burn_subtitles,
            clip_layout=payload.clip_layout,
            vertical_template=payload.vertical_template,
            custom_camera=cam_dict,
            custom_content=cont_dict,
        )

        def on_stage(name: str) -> None:
            set_state(stage=name, message=name)

        result = run_pipeline(cfg, on_stage=on_stage)
        outs = [str(p) for p in result.clips]
        set_state(
            status="completed",
            stage="done",
            message="ok",
            outputs=outs,
            work_dir=str(result.work_dir),
            language=result.language,
            error=None,
        )
    except Exception as e:
        tb = traceback.format_exc()
        set_state(
            status="failed",
            stage="error",
            message=str(e),
            error=f"{e}\n{tb}",
        )


@app.post("/job", response_model=JobStatus)
def create_job(body: JobCreate) -> JobStatus:
    inp = Path(body.input_path)
    if not inp.is_file():
        raise HTTPException(status_code=400, detail=f"No existe el archivo: {inp}")
    out = Path(body.output_dir)
    out.mkdir(parents=True, exist_ok=True)

    job_id = uuid.uuid4().hex
    with _lock:
        _jobs[job_id] = {
            "id": job_id,
            "status": "queued",
            "stage": "queued",
            "message": "",
            "outputs": [],
            "work_dir": None,
            "language": None,
            "error": None,
        }

    t = threading.Thread(target=_run_job, args=(job_id, body), daemon=True)
    t.start()
    return get_job(job_id)


@app.get("/job/{job_id}", response_model=JobStatus)
def get_job(job_id: str) -> JobStatus:
    with _lock:
        data = _jobs.get(job_id)
    if not data:
        raise HTTPException(status_code=404, detail="Job no encontrado")
    return JobStatus(**data)


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


def run() -> None:
    import uvicorn

    uvicorn.run(
        "edite_ia.api:app",
        host="127.0.0.1",
        port=8765,
        reload=False,
        log_level="info",
    )


if __name__ == "__main__":
    run()
