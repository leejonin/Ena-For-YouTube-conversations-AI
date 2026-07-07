"""
로컬 Supertonic 3 TTS — OpenAI 호환 /v1/audio/speech (CPU, 포트 8081)
"""
from __future__ import annotations

import asyncio
import io
import json
import re
import wave
from pathlib import Path
from typing import Optional

import numpy as np
from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

try:
    from supertonic import TTS
except ImportError as exc:
    raise SystemExit(
        "supertonic 미설치. setup_local_tts.ps1 실행 또는 pip install -r requirements.txt"
    ) from exc

ROOT = Path(__file__).resolve().parent.parent
CONFIG_PATH = ROOT / "local_tts_config.json"

DEFAULT_VOICE = "F2"
DEFAULT_LANG = "ko"
DEFAULT_THREADS = 2
DEFAULT_SPEED = 1.04
DEFAULT_STEPS = 8

_HANGUL_RE = re.compile(r"[\uAC00-\uD7A3]")
_NON_KOREAN_SPEECH_RE = re.compile(
    r"[A-Za-z]+|[\u3040-\u30FF\u4E00-\u9FFF\u3400-\u4DBF\uF900-\uFAFF]+"
)
_DISALLOWED_KOREAN_SPEECH_RE = re.compile(r"[^\uAC00-\uD7A3\s0-9.,!?~]+")
# 한·영 분할 합성 — 일본어·한자 등 제3언어만 제거 (영문 Latin 유지)
_FOREIGN_SCRIPT_RE = re.compile(
    r"[\u3040-\u30FF\u4E00-\u9FFF\u3400-\u4DBF\uF900-\uFAFF]+"
)
_DISALLOWED_BILINGUAL_SPEECH_RE = re.compile(
    r"[^\uAC00-\uD7A3A-Za-z\s0-9.,!?~']+"
)
_LATIN_LETTER_RE = re.compile(r"[A-Za-z]")

_tts_engine: Optional[TTS] = None
_style_cache: dict[str, object] = {}
_synth_lock = asyncio.Lock()
_warmed_up = False


def load_runtime_config() -> dict:
    if CONFIG_PATH.exists():
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                return json.load(f)
        except (json.JSONDecodeError, OSError):
            pass
    return {}


def filter_korean_only(text: str) -> str:
    """합성 입력에서 한글·숫자·기본 구두점만 남긴다."""
    if not text:
        return ""
    cleaned = _NON_KOREAN_SPEECH_RE.sub(" ", text)
    cleaned = _DISALLOWED_KOREAN_SPEECH_RE.sub(" ", cleaned)
    cleaned = re.sub(r"\s+", " ", cleaned).strip()
    return cleaned


def filter_bilingual_speech(text: str) -> str:
    """합성 입력 — 한글·영문·숫자·기본 구두점 허용, 제3언어 제거."""
    if not text:
        return ""
    cleaned = _FOREIGN_SCRIPT_RE.sub(" ", text)
    cleaned = _DISALLOWED_BILINGUAL_SPEECH_RE.sub(" ", cleaned)
    cleaned = re.sub(r"\s+", " ", cleaned).strip()
    return cleaned


def split_lang_segments(text: str) -> list[tuple[str, str]]:
    """연속 Hangul→ko, 연속 Latin→en 구간으로 분할. 구두점·숫자·공백은 앞 구간에 병합."""
    if not text:
        return []

    parts = re.findall(
        r"[\uAC00-\uD7A3]+|[A-Za-z]+(?:'[A-Za-z]+)?|\s+|[^\uAC00-\uD7A3A-Za-z\s]+",
        text,
    )
    segments: list[tuple[str, str]] = []
    pending = ""

    for part in parts:
        if re.fullmatch(r"[\uAC00-\uD7A3]+", part):
            lang = "ko"
            piece = (pending + part).strip()
            pending = ""
        elif re.fullmatch(r"[A-Za-z]+(?:'[A-Za-z]+)?", part):
            lang = "en"
            piece = (pending + part).strip()
            pending = ""
        else:
            pending += part
            continue

        if not piece:
            continue

        if segments and segments[-1][1] == lang:
            segments[-1] = (segments[-1][0] + " " + piece, lang)
        else:
            segments.append((piece, lang))

    return segments


def normalize_speech_text(text: str, korean_only: bool) -> str:
    cleaned = text.strip()
    if not cleaned:
        raise ValueError("입력 텍스트가 비어 있습니다")

    if korean_only:
        cleaned = filter_korean_only(cleaned)
        if not cleaned or not _HANGUL_RE.search(cleaned):
            raise ValueError("한국어 발화 텍스트가 없습니다")
    else:
        cleaned = filter_bilingual_speech(cleaned)
        if not cleaned or (
            not _HANGUL_RE.search(cleaned) and not _LATIN_LETTER_RE.search(cleaned)
        ):
            raise ValueError("발화 텍스트가 없습니다")

    return cleaned


def get_threads(cfg: dict) -> int:
    return max(1, int(cfg.get("onnxThreads", DEFAULT_THREADS)))


def get_engine(cfg: dict) -> TTS:
    global _tts_engine
    if _tts_engine is not None:
        return _tts_engine

    threads = get_threads(cfg)
    _tts_engine = TTS(
        auto_download=True,
        intra_op_num_threads=threads,
        inter_op_num_threads=1,
    )
    return _tts_engine


def get_voice_style(engine: TTS, voice_name: str):
    if voice_name not in _style_cache:
        available = set(engine.voice_style_names)
        if voice_name not in available:
            raise ValueError(f"음색 없음: {voice_name} (사용 가능: {sorted(available)})")
        _style_cache[voice_name] = engine.get_voice_style(voice_name)
    return _style_cache[voice_name]


def wav_array_to_bytes(wav: np.ndarray, sample_rate: int) -> bytes:
    samples = wav[0] if wav.ndim > 1 else wav
    samples = np.clip(samples.astype(np.float32), -1.0, 1.0)
    pcm = (samples * 32767.0).astype(np.int16)

    buf = io.BytesIO()
    with wave.open(buf, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(sample_rate)
        wf.writeframes(pcm.tobytes())
    return buf.getvalue()


class SpeechRequest(BaseModel):
    model: Optional[str] = None
    voice: Optional[str] = None
    input: str = Field(..., min_length=1)
    speed: Optional[float] = 1.04
    instructions: Optional[str] = None
    total_steps: Optional[int] = None


app = FastAPI(title="NIN Local Supertonic TTS", version="3.0.0")


def synthesize_bilingual_wav_bytes(
    engine: TTS,
    style,
    speak_text: str,
    speed: float,
    total_steps: int,
) -> bytes:
    """한·영 구간별 lang=ko/en 합성 후 WAV 이어 붙이기."""
    segments = split_lang_segments(speak_text)
    if not segments:
        raise ValueError("발화 텍스트가 없습니다")

    clamped_speed = max(0.7, min(2.0, speed))
    clamped_steps = max(2, min(12, total_steps))
    wav_parts: list[np.ndarray] = []

    for seg_text, seg_lang in segments:
        wav, _duration = engine.synthesize(
            seg_text,
            voice_style=style,
            lang=seg_lang,
            speed=clamped_speed,
            total_steps=clamped_steps,
        )
        wav_parts.append(wav[0] if wav.ndim > 1 else wav)

    combined = np.concatenate(wav_parts)
    return wav_array_to_bytes(combined.reshape(1, -1), int(engine.sample_rate))


def synthesize_wav_bytes(
    voice_name: str,
    text: str,
    speed: float,
    korean_only: bool,
    lang: str,
    total_steps: int,
) -> bytes:
    cfg = load_runtime_config()
    engine = get_engine(cfg)
    style = get_voice_style(engine, voice_name)
    speak_text = normalize_speech_text(text, korean_only)

    bilingual = bool(cfg.get("bilingualSegmentTts", False)) and not korean_only
    if bilingual:
        return synthesize_bilingual_wav_bytes(
            engine, style, speak_text, speed, total_steps
        )

    wav, _duration = engine.synthesize(
        speak_text,
        voice_style=style,
        lang=lang,
        speed=max(0.7, min(2.0, speed)),
        total_steps=max(2, min(12, total_steps)),
    )
    return wav_array_to_bytes(wav, int(engine.sample_rate))


@app.on_event("startup")
async def startup_warmup() -> None:
    global _warmed_up
    cfg = load_runtime_config()
    if not cfg.get("warmupOnStart", True):
        return

    korean_only = bool(cfg.get("koreanOnly", True))
    bilingual_segment_tts = bool(cfg.get("bilingualSegmentTts", False)) and not korean_only
    voice_name = cfg.get("voice", DEFAULT_VOICE)
    lang = cfg.get("lang", DEFAULT_LANG)
    speed = float(cfg.get("synthesisSpeed", DEFAULT_SPEED))
    steps = int(cfg.get("totalSteps", DEFAULT_STEPS))
    warmup_text = "hello 안녕" if bilingual_segment_tts else "안녕하세요"

    try:
        synthesize_wav_bytes(voice_name, warmup_text, speed, korean_only, lang, steps)
        _warmed_up = True
        print(
            f"[LocalTTS] warmup OK engine=supertonic3 voice={voice_name} "
            f"lang={lang} bilingual={bilingual_segment_tts}",
            flush=True,
        )
    except Exception as ex:
        print(f"[LocalTTS] warmup skipped: {ex}", flush=True)


@app.get("/health")
def health() -> dict:
    cfg = load_runtime_config()
    voice_name = cfg.get("voice", DEFAULT_VOICE)
    korean_only = bool(cfg.get("koreanOnly", True))
    bilingual_segment_tts = bool(cfg.get("bilingualSegmentTts", False)) and not korean_only
    lang = cfg.get("lang", DEFAULT_LANG)
    ready = _tts_engine is not None or cfg.get("warmupOnStart", True)

    return {
        "status": "ok" if ready else "starting",
        "voice": voice_name,
        "lang": lang,
        "warmed_up": _warmed_up,
        "engine": "supertonic3-cpu-ko",
        "korean_only": korean_only,
        "bilingual_segment_tts": bilingual_segment_tts,
    }


@app.post("/v1/audio/speech")
async def speech(req: SpeechRequest) -> Response:
    cfg = load_runtime_config()
    korean_only = bool(cfg.get("koreanOnly", True))
    voice_name = req.voice or cfg.get("voice", DEFAULT_VOICE)
    lang = cfg.get("lang", DEFAULT_LANG)
    speed = req.speed if req.speed and req.speed > 0 else float(cfg.get("synthesisSpeed", DEFAULT_SPEED))
    steps = int(req.total_steps) if req.total_steps and req.total_steps > 0 else int(cfg.get("totalSteps", DEFAULT_STEPS))
    text = req.input.strip()
    if not text:
        raise HTTPException(status_code=400, detail="input is empty")

    async with _synth_lock:
        try:
            wav_bytes = await asyncio.to_thread(
                synthesize_wav_bytes,
                voice_name,
                text,
                speed,
                korean_only,
                lang,
                steps,
            )
        except ValueError as ex:
            raise HTTPException(status_code=400, detail=str(ex)) from ex
        except Exception as ex:
            raise HTTPException(status_code=500, detail=str(ex)) from ex

    return Response(content=wav_bytes, media_type="audio/wav")


if __name__ == "__main__":
    import uvicorn

    print("[INFO] Local Supertonic 3 TTS http://127.0.0.1:8081", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=8081, log_level="info")
