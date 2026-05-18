from __future__ import annotations


def clamp_crop_rect(d: dict[str, float]) -> dict[str, float]:
    """x,y,w,h en [0,1] relativos al frame; asegura tamaño mínimo y que quepa."""
    x = max(0.0, min(1.0, float(d.get("x", 0))))
    y = max(0.0, min(1.0, float(d.get("y", 0))))
    w = max(0.04, min(1.0, float(d.get("w", 0.2))))
    h = max(0.04, min(1.0, float(d.get("h", 0.2))))
    if x + w > 1.0:
        w = 1.0 - x
    if y + h > 1.0:
        h = 1.0 - y
    w = max(0.04, w)
    h = max(0.04, h)
    return {"x": x, "y": y, "w": w, "h": h}
