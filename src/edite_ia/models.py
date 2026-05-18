from __future__ import annotations

from dataclasses import dataclass


@dataclass
class Utterance:
    start: float
    end: float
    text: str


@dataclass
class Highlight:
    start: float
    end: float
    score: float
