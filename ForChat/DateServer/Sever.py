import json
import os
import re
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse
from pydantic import BaseModel, Field

try:
    import chromadb
except Exception:
    chromadb = None

try:
    from openai import OpenAI
except Exception:
    OpenAI = None


CURRENT_DIR = Path(__file__).resolve().parent
HTML_FILE = CURRENT_DIR / "dashboard.html"
JSON_FILE = CURRENT_DIR / "YYDate.Json"
CHROMA_DIR = CURRENT_DIR / "chroma_db"
COLLECTION_NAME = "nin_dialogue_action_memory"

# Chroma 임베딩 모델 다운로드/네트워크 실패 시 JSON fallback으로 전환한다.
_chroma_usable = True

app = FastAPI(title="NIN FastAPI Memory Server", version="2.0.0")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


class DataPayload(BaseModel):
    time: str
    timestamp: Optional[int] = None
    dv_input: str
    nin_response: str
    action_embedding: Optional[List[float]] = None
    body_command_id: Optional[str] = ""
    emotion_folder: Optional[str] = ""
    obs_vector: Optional[List[float]] = None
    act_vector: Optional[List[float]] = None
    pose_before: Optional[List[float]] = None
    pose_after: Optional[List[float]] = None
    saved_pose_name: Optional[str] = ""
    learning_weight: Optional[float] = None
    policy_act_vector: Optional[List[float]] = None
    guidance_applied: Optional[bool] = False
    guidance_joint_mask: Optional[List[float]] = None
    guidance_reason: Optional[str] = ""
    reference_pose_name: Optional[str] = ""
    dataset_match_score: Optional[float] = None
    intent_category: Optional[str] = ""
    behavior_tag: Optional[str] = ""
    turn_source: Optional[str] = ""
    youtube_channel_id: Optional[str] = ""
    youtube_display_name: Optional[str] = ""


class SearchRequest(BaseModel):
    keyword: str = Field(min_length=1)
    top_k: int = 12


class ViewerSearchRequest(BaseModel):
    channel_id: str = Field(min_length=1)
    top_k: int = 5


# 검색어 확장(동의어·복합어 분해) — 옛 기록(예: 2025 생일 축하) 누락 완화
_QUERY_SYNONYMS: Dict[str, List[str]] = {
    "생일": ["태어난", "축하", "생일날", "birthday", "birth", "생일 축하", "4월", "25일"],
    "생일날짜": ["생일", "날짜", "태어난", "4월", "25일"],
    "기억력": ["기억", "메모리", "memory", "시스템 개발"],
    "대화검색": ["검색", "명령어", "키워드"],
    "논문": ["학회", "투고", "산학", "AI-Pi", "학술회", "기술학회"],
    "학술회": ["학회", "기술학회", "산학", "논문", "투고", "포스터", "발표", "AI-Pi"],
    "학회": ["학술회", "기술학회", "산학", "논문", "투고", "포스터"],
    "기술학회": ["학술회", "학회", "산학", "논문", "포스터"],
    "웹": ["웹페이지", "사이트", "과제", "웹프로그래밍"],
}

# 검색 실패·오답 대화 — 동일 키워드 재검색 시 상위 오염 방지
_SEARCH_FAILURE_NEEDLES: Tuple[str, ...] = (
    "검색 결과가 없음",
    "found=false",
    "기억을 못 하고",
    "저장되지 않은",
    "못 찾",
    "기억하지 못해",
)

_COMPOUND_SPLITS: Dict[str, List[str]] = {
    "생일날짜": ["생일", "날짜"],
    "검색어": ["검색", "명령어"],
    "기억력": ["기억", "시스템"],
}


class SummationRequest(BaseModel):
    time: str


def get_openai_client() -> Optional[Any]:
    if OpenAI is None:
        return None

    # 로컬 llama-server (OpenAI 호환) 우선 — DateServer 요약·검색 gpt_answer
    local_base = os.getenv("LOCAL_LLM_BASE_URL", "http://127.0.0.1:8080/v1").strip()
    local_key = os.getenv("LOCAL_LLM_API_KEY", "local").strip()
    if local_base:
        try:
            return OpenAI(api_key=local_key or "local", base_url=local_base)
        except Exception as ex:
            print(f"[WARN] local LLM client failed: {ex}", flush=True)

    api_key = os.getenv("OPENAI_API_KEY", "").strip()
    if not api_key:
        key_file = CURRENT_DIR.parent / "ckey.txt"
        if key_file.exists():
            api_key = key_file.read_text(encoding="utf-8").strip()

    if not api_key:
        return None
    return OpenAI(api_key=api_key)


def get_llm_model_name() -> str:
    return os.getenv("LOCAL_LLM_MODEL", "qwen2.5-7b-instruct").strip() or "qwen2.5-7b-instruct"


def load_json_data() -> Dict[str, Any]:
    if not JSON_FILE.exists():
        return {}
    try:
        return json.loads(JSON_FILE.read_text(encoding="utf-8"))
    except Exception:
        return {}


def save_json_data(data: Dict[str, Any]) -> None:
    JSON_FILE.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def ensure_path(data: Dict[str, Any], path_list: List[str]) -> Dict[str, Any]:
    cur = data
    for k in path_list[:-1]:
        if k not in cur or not isinstance(cur[k], dict):
            cur[k] = {}
        cur = cur[k]
    return cur


def collect_all_entries(data: Dict[str, Any]) -> List[Dict[str, Any]]:
    entries: List[Dict[str, Any]] = []

    def traverse(node: Any) -> None:
        if isinstance(node, dict):
            if "time" in node and "dv_input" in node and "nin_response" in node:
                entries.append(
                    {
                        "time": node.get("time", ""),
                        "timestamp": node.get("timestamp", 0),
                        "dv_input": node.get("dv_input", ""),
                        "nin_response": node.get("nin_response", ""),
                        "summation": node.get("summation", ""),
                        "action_embedding": node.get("action_embedding", []),
                        "body_command_id": node.get("body_command_id", ""),
                        "emotion_folder": node.get("emotion_folder", ""),
                        "obs_vector": node.get("obs_vector", []),
                        "act_vector": node.get("act_vector", []),
                        "pose_before": node.get("pose_before", []),
                        "pose_after": node.get("pose_after", []),
                        "saved_pose_name": node.get("saved_pose_name", ""),
                        "learning_weight": node.get("learning_weight"),
                        "policy_act_vector": node.get("policy_act_vector", []),
                        "guidance_applied": node.get("guidance_applied", False),
                        "guidance_joint_mask": node.get("guidance_joint_mask", []),
                        "guidance_reason": node.get("guidance_reason", ""),
                        "reference_pose_name": node.get("reference_pose_name", ""),
                        "dataset_match_score": node.get("dataset_match_score"),
                        "turn_source": node.get("turn_source", ""),
                        "youtube_channel_id": node.get("youtube_channel_id", ""),
                        "youtube_display_name": node.get("youtube_display_name", ""),
                    }
                )
                return
            for v in node.values():
                traverse(v)
        elif isinstance(node, list):
            for item in node:
                traverse(item)

    traverse(data)
    for entry in entries:
        if not entry.get("timestamp"):
            entry["timestamp"] = resolve_entry_timestamp(entry)
    entries.sort(key=lambda x: (x.get("timestamp") or 0, x.get("time", "")))
    return entries


def resolve_entry_timestamp(entry: Dict[str, Any]) -> int:
    """timestamp 필드가 없는 옛 YYDate 항목도 time 문자열로 정렬·검색 가능하게 한다."""
    ts = int(entry.get("timestamp") or 0)
    if ts > 0:
        return ts
    time_str = str(entry.get("time", "")).strip()
    if not time_str:
        return 0
    try:
        return int(parse_time(time_str).timestamp() * 1000)
    except ValueError:
        pass
    try:
        return int(datetime.fromisoformat(time_str.replace("Z", "+00:00")).timestamp() * 1000)
    except ValueError:
        return 0


def expand_search_terms(keyword: str) -> List[str]:
    """검색어·동의어·복합어 분해 토큰 목록(긴 토큰 우선)."""
    raw = (keyword or "").strip()
    if not raw:
        return []

    terms: Set[str] = set()
    lowered = raw.lower()
    terms.add(lowered)
    terms.add(raw)

    if raw in _COMPOUND_SPLITS:
        for part in _COMPOUND_SPLITS[raw]:
            if len(part) >= 2:
                terms.add(part)

    for token in re.findall(r"[\w가-힣]{2,}", raw):
        terms.add(token)
        if token in _QUERY_SYNONYMS:
            for syn in _QUERY_SYNONYMS[token]:
                if len(syn) >= 2:
                    terms.add(syn)
        if token in _COMPOUND_SPLITS:
            for part in _COMPOUND_SPLITS[token]:
                if len(part) >= 2:
                    terms.add(part)

    for key, syns in _QUERY_SYNONYMS.items():
        if key in raw or key in lowered:
            terms.add(key)
            for syn in syns:
                if len(syn) >= 2:
                    terms.add(syn)

    return sorted(terms, key=len, reverse=True)


def entry_haystack(entry: Dict[str, Any]) -> str:
    return (
        f"{entry.get('time', '')} "
        f"{entry.get('turn_source', '')} "
        f"{entry.get('youtube_channel_id', '')} "
        f"{entry.get('youtube_display_name', '')} "
        f"{entry.get('dv_input', '')} "
        f"{entry.get('nin_response', '')} "
        f"{entry.get('summation', '')}"
    ).lower()


def _truncate_text(text: str, max_len: int) -> str:
    text = (text or "").replace("\r\n", " ").replace("\n", " ").strip()
    if len(text) <= max_len:
        return text
    return text[: max_len - 1] + "…"


def search_failure_penalty(entry: Dict[str, Any]) -> float:
    """「검색 결과 없음」 등 메타 실패 응답 — 키워드만 맞고 사실 없는 기록."""
    haystack = entry_haystack(entry)
    penalty = 0.0
    for needle in _SEARCH_FAILURE_NEEDLES:
        if needle in haystack:
            penalty += 30.0
            break

    nin_response = str(entry.get("nin_response", ""))
    if "*대화검색*" in nin_response and any(
        token in nin_response for token in ("없네", "없어", "못 하", "못해", "못 찾")
    ):
        penalty += 25.0

    return penalty


def score_entry_for_search(entry: Dict[str, Any], keyword: str, terms: List[str]) -> float:
    """전체 YYDate.Json 스캔용 점수 — 부분 일치·동의어·요약 가중."""
    haystack = entry_haystack(entry)
    if not haystack.strip():
        return 0.0

    score = 0.0
    kw_lower = keyword.lower()

    if kw_lower and kw_lower in haystack:
        score += 40.0

    matched_terms = 0
    for term in terms:
        t = term.lower()
        if len(t) < 2:
            continue
        if t in haystack:
            matched_terms += 1
            if t == kw_lower:
                score += 18.0
            elif len(t) >= 4:
                score += 12.0
            else:
                score += 8.0

    if matched_terms >= 2:
        score += 6.0 * (matched_terms - 1)

    summation = str(entry.get("summation", "")).lower()
    if kw_lower and kw_lower in summation:
        score += 10.0

    dv_input = str(entry.get("dv_input", "")).lower()
    if kw_lower and kw_lower in dv_input:
        score += 15.0

    fact_blob = summation + " " + dv_input
    if re.search(r"\d{4}년|\d{4}-\d{2}-\d{2}", fact_blob):
        score += 12.0

    score -= search_failure_penalty(entry)
    return max(0.0, score)


def ranked_keyword_search(keyword: str, top_k: int) -> List[Dict[str, Any]]:
    """JSON 전체 스캔 + 점수 정렬 — Chroma 미동기화·의미검색 누락 시에도 옛 기억 검색."""
    terms = expand_search_terms(keyword)
    if not terms:
        terms = [keyword.strip()]

    data = load_json_data()
    all_entries = collect_all_entries(data)
    scored: List[Tuple[float, Dict[str, Any]]] = []

    for entry in all_entries:
        s = score_entry_for_search(entry, keyword, terms)
        if s > 0:
            scored.append((s, entry))

    scored.sort(key=lambda x: (-x[0], -(x[1].get("timestamp") or 0)))
    out: List[Dict[str, Any]] = []
    for _, entry in scored[:top_k]:
        clean = dict(entry)
        clean.pop("_search_score", None)
        out.append(clean)
    return out


def _entry_merge_key(entry: Dict[str, Any]) -> str:
    return f"{entry.get('time', '')}|{int(entry.get('timestamp') or 0)}"


def merge_search_results(
    keyword: str,
    ranked_lists: List[List[Dict[str, Any]]],
    top_k: int,
) -> List[Dict[str, Any]]:
    """Chroma + 키워드 결과 병합(중복 제거, 점수 합산)."""
    terms = expand_search_terms(keyword)
    merged: Dict[str, Tuple[float, Dict[str, Any]]] = {}

    for list_idx, items in enumerate(ranked_lists):
        for rank, entry in enumerate(items):
            key = _entry_merge_key(entry)
            base = score_entry_for_search(entry, keyword, terms)
            # Chroma 상위 결과에 소폭 가산, 키워드 전용 스캔 결과도 유지
            list_bonus = max(0.0, 24.0 - list_idx * 4.0 - rank * 2.0)
            total = base + list_bonus
            if key in merged:
                prev_score, prev_entry = merged[key]
                if total > prev_score:
                    merged[key] = (total, entry)
                else:
                    merged[key] = (prev_score, prev_entry)
            else:
                merged[key] = (total, entry)

    ordered = sorted(merged.values(), key=lambda x: (-x[0], -(x[1].get("timestamp") or 0)))
    result: List[Dict[str, Any]] = []
    for _, entry in ordered[:top_k]:
        clean = dict(entry)
        clean.pop("_search_score", None)
        result.append(clean)
    return result


def make_record_id(time_str: str, timestamp: int) -> str:
    return f"{time_str}_{timestamp}"


def get_chroma_collection():
    global _chroma_usable
    if chromadb is None or not _chroma_usable:
        return None

    try:
        client = chromadb.PersistentClient(path=str(CHROMA_DIR))
        return client.get_or_create_collection(name=COLLECTION_NAME, metadata={"hnsw:space": "cosine"})
    except Exception as ex:
        _chroma_usable = False
        print(f"[WARN] Chroma disabled: {ex}", flush=True)
        return None


# summation 저장·생성 상한 (끊김 방지)
SUMMATION_STORAGE_MAX_CHARS = 600
SUMMATION_PROMPT_DV_MAX_CHARS = 400
SUMMATION_PROMPT_NIN_MAX_CHARS = 1200
SUMMATION_FALLBACK_FIELD_CHARS = 180
SUMMATION_GPT_MAX_TOKENS = 320

_SUMMARY_NOISE_PREFIXES = ("[최근대화]", "[실시간시각]", "[대화검색]")


def normalize_dialogue_for_summary(text: str) -> str:
    """요약 입력에서 컨텍스트 주입 태그·과도한 공백을 정리한다."""
    if not text:
        return ""
    cleaned = str(text).replace("\r\n", "\n").replace("\r", "\n")
    lines: List[str] = []
    for line in cleaned.split("\n"):
        stripped = line.strip()
        if not stripped:
            continue
        if any(stripped.startswith(prefix) for prefix in _SUMMARY_NOISE_PREFIXES):
            continue
        lines.append(stripped)
    return " ".join(lines).strip()


def preview_text_for_summary(text: str, max_chars: int) -> str:
    """폴백 요약용 미리보기 — 글자 단위로 끊지 않고 말줄임."""
    normalized = normalize_dialogue_for_summary(text)
    if len(normalized) <= max_chars:
        return normalized
    cut = normalized[:max_chars].rstrip()
    last_space = cut.rfind(" ")
    if last_space >= max_chars // 2:
        cut = cut[:last_space].rstrip()
    return cut + "…"


def _ends_complete_sentence(text: str) -> bool:
    s = (text or "").strip()
    if not s:
        return False
    if s[-1] in ".!?。)]\"'":
        return True
    for suffix in (
        "습니다",
        "습니까",
        "해요",
        "했다",
        "한다",
        "입니다",
        "있음",
        "없음",
        "함",
        "음",
        "임",
        "네요",
        "거예요",
        "죠",
        "예요",
    ):
        if s.endswith(suffix):
            return True
    return False


def is_summation_incomplete(summation: str) -> bool:
    """YYDate.Json에 저장된 summation이 중간에 끊긴 경우 감지."""
    s = (summation or "").strip()
    if len(s) < 12:
        return True
    if s.count("(") > s.count(")"):
        return True
    if s.count("[") > s.count("]"):
        return True
    if s.startswith("[대화 요약]") and " / " in s:
        tail = s.split(" / ", 1)[-1].strip()
        if len(tail) < 40 and not _ends_complete_sentence(tail):
            return True
    if len(s) >= 70 and not _ends_complete_sentence(s):
        return True
    return False


def build_fallback_summation(dv_input: str, nin_response: str) -> str:
    dv_preview = preview_text_for_summary(dv_input, SUMMATION_FALLBACK_FIELD_CHARS)
    nin_preview = preview_text_for_summary(nin_response, SUMMATION_FALLBACK_FIELD_CHARS)
    if not dv_preview and not nin_preview:
        return "[빈 대화]"
    return f"[대화 요약] {dv_preview} / {nin_preview}".strip()


def finalize_summation(raw: str, dv_input: str, nin_response: str) -> str:
    text = (raw or "").strip()
    if not text or is_summation_incomplete(text):
        text = build_fallback_summation(dv_input, nin_response)
    if len(text) > SUMMATION_STORAGE_MAX_CHARS:
        text = preview_text_for_summary(text, SUMMATION_STORAGE_MAX_CHARS)
    return text


def summarize_dialogue(dv_input: str, nin_response: str) -> str:
    dv_clean = normalize_dialogue_for_summary(dv_input)
    nin_clean = normalize_dialogue_for_summary(nin_response)
    if not dv_clean and not nin_clean:
        return "[빈 대화]"

    client = get_openai_client()
    if client is None:
        return finalize_summation("", dv_input, nin_response)

    dv_for_prompt = preview_text_for_summary(dv_clean, SUMMATION_PROMPT_DV_MAX_CHARS)
    nin_for_prompt = preview_text_for_summary(nin_clean, SUMMATION_PROMPT_NIN_MAX_CHARS)

    try:
        prompt = (
            "다음 대화를 한국어로 완결된 한 문장으로 요약하세요. "
            "문장은 반드시 끝까지 쓰고 중간에 끊지 마세요.\n"
            f"개발자: {dv_for_prompt}\n"
            f"NIN: {nin_for_prompt}\n"
            "형식: [대화 요약] 핵심 내용 한 문장."
        )
        resp = client.chat.completions.create(
            model=get_llm_model_name(),
            messages=[
                {
                    "role": "system",
                    "content": "대화를 한 문장으로 완결해 요약합니다. 요약은 중간에 끊기지 않게 끝맺음하세요.",
                },
                {"role": "user", "content": prompt},
            ],
            max_tokens=SUMMATION_GPT_MAX_TOKENS,
            temperature=0.2,
        )
        content = (resp.choices[0].message.content or "").strip()
        return finalize_summation(content, dv_input, nin_response)
    except Exception:
        return finalize_summation("", dv_input, nin_response)


def build_document_text(entry: Dict[str, Any]) -> str:
    action_text = ",".join([f"{x:.5f}" for x in (entry.get("action_embedding") or [])[:64]])
    return (
        f"time={entry.get('time', '')}\n"
        f"turn_source={entry.get('turn_source', '')}\n"
        f"youtube_channel_id={entry.get('youtube_channel_id', '')}\n"
        f"youtube_display_name={entry.get('youtube_display_name', '')}\n"
        f"dv_input={entry.get('dv_input', '')}\n"
        f"nin_response={entry.get('nin_response', '')}\n"
        f"summation={entry.get('summation', '')}\n"
        f"action_embedding={action_text}"
    )


def upsert_chroma_entry(entry: Dict[str, Any]) -> None:
    try:
        collection = get_chroma_collection()
        if collection is None:
            return

        record_id = make_record_id(entry.get("time", ""), int(entry.get("timestamp") or 0))
        metadata = {
            "time": entry.get("time", ""),
            "timestamp": int(entry.get("timestamp") or 0),
            "turn_source": entry.get("turn_source", ""),
            "youtube_channel_id": entry.get("youtube_channel_id", ""),
            "youtube_display_name": entry.get("youtube_display_name", ""),
            "dv_input": entry.get("dv_input", ""),
            "nin_response": entry.get("nin_response", ""),
            "summation": entry.get("summation", ""),
            "action_embedding": json.dumps(entry.get("action_embedding") or []),
        }
        document = build_document_text(entry)

        collection.upsert(ids=[record_id], documents=[document], metadatas=[metadata])
    except Exception as ex:
        # Chroma 임베딩 모델 다운로드/네트워크 실패 시에도 FastAPI 서버는 계속 기동한다.
        global _chroma_usable
        _chroma_usable = False
        print(f"[WARN] Chroma upsert skipped: {ex}", flush=True)


def chroma_semantic_search(keyword: str, top_k: int) -> List[Dict[str, Any]]:
    """Chroma 의미 검색만 수행(실패 시 빈 목록)."""
    top_k = max(1, min(top_k, 30))
    collection = get_chroma_collection()
    if collection is None:
        return []

    try:
        result = collection.query(query_texts=[keyword], n_results=top_k, include=["metadatas"])
        metas = result.get("metadatas", [[]])[0]
        matches: List[Dict[str, Any]] = []
        for m in metas:
            matches.append(
                {
                    "time": m.get("time", ""),
                    "timestamp": int(m.get("timestamp") or resolve_entry_timestamp(m)),
                    "dv_input": m.get("dv_input", ""),
                    "nin_response": m.get("nin_response", ""),
                    "summation": m.get("summation", ""),
                    "action_embedding": json.loads(m.get("action_embedding", "[]")),
                }
            )
        return matches
    except Exception as ex:
        global _chroma_usable
        _chroma_usable = False
        print(f"[WARN] Chroma search failed: {ex}", flush=True)
        return []


def hybrid_search(keyword: str, top_k: int) -> List[Dict[str, Any]]:
    """
    하이브리드 검색: JSON 전체 키워드·동의어 스캔 + Chroma 의미 검색 병합.
    옛날 기억 누락 방지 — Chroma 미인덱스·의미 불일치 시에도 YYDate.Json에서 직접 찾음.
    """
    top_k = max(1, min(top_k, 30))
    fetch_k = max(top_k * 2, 16)

    keyword_hits = ranked_keyword_search(keyword, fetch_k)
    chroma_hits = chroma_semantic_search(keyword, fetch_k)

    if not keyword_hits and not chroma_hits:
        return []

    if not chroma_hits:
        return keyword_hits[:top_k]

    if not keyword_hits:
        return chroma_hits[:top_k]

    return merge_search_results(keyword, [keyword_hits, chroma_hits], top_k)


def reindex_all_entries_from_json() -> Dict[str, int]:
    """YYDate.Json 전체를 Chroma에 다시 넣는다(옛 대화 누락 복구)."""
    data = load_json_data()
    entries = collect_all_entries(data)
    ok = 0
    for entry in entries:
        upsert_chroma_entry(entry)
        ok += 1

    chroma_count = 0
    collection = get_chroma_collection()
    if collection is not None:
        try:
            chroma_count = int(collection.count())
        except Exception:
            chroma_count = ok

    return {"json_entries": len(entries), "upserted": ok, "chroma_count": chroma_count}


def ensure_chroma_synced_with_json(force: bool = False) -> None:
    """서버 기동 시 JSON 건수와 Chroma 건수가 크게 다르면 자동 재인덱싱."""
    collection = get_chroma_collection()
    if collection is None:
        return

    entries = collect_all_entries(load_json_data())
    json_count = len(entries)
    if json_count == 0:
        return

    try:
        chroma_count = int(collection.count())
    except Exception:
        chroma_count = 0

    if force or chroma_count < max(1, int(json_count * 0.85)):
        print(
            f"[INFO] Chroma reindex: json={json_count} chroma={chroma_count} force={force}",
            flush=True,
        )
        stats = reindex_all_entries_from_json()
        print(f"[INFO] Chroma reindex done: {stats}", flush=True)


def build_fallback_answer(keyword: str, matches: List[Dict[str, Any]]) -> str:
    """OpenAI 실패·미설정 시 — 상위 match 시간·개발자 입력·요약을 직접 반환."""
    if not matches:
        return ""

    parts = [f"'{keyword}' 관련 기억 {len(matches)}건."]
    for idx, m in enumerate(matches[:4]):
        time_str = m.get("time", "")
        summ = _truncate_text(str(m.get("summation") or ""), 180)
        if not summ:
            summ = _truncate_text(str(m.get("nin_response") or ""), 140)
        dv = _truncate_text(str(m.get("dv_input") or ""), 100)
        parts.append(f"[{idx + 1}] 시간:{time_str} 개발자:{dv} 요약:{summ}")
    return " ".join(parts)


def build_gpt_answer(keyword: str, matches: List[Dict[str, Any]]) -> str:
    if not matches:
        return ""

    client = get_openai_client()
    if client is None:
        return build_fallback_answer(keyword, matches)

    payload = "\n\n".join(
        [
            f"[{idx + 1}] 시간:{m.get('time')} 요약:{_truncate_text(str(m.get('summation') or ''), 200)} "
            f"개발자:{_truncate_text(str(m.get('dv_input') or ''), 120)} "
            f"NIN:{_truncate_text(str(m.get('nin_response') or ''), 220)}"
            for idx, m in enumerate(matches[:4])
        ]
    )

    try:
        resp = client.chat.completions.create(
            model=get_llm_model_name(),
            messages=[
                {"role": "system", "content": "대화 기억을 핵심만 정확히 정리하세요. 날짜·사건·고유명사는 반드시 포함하세요."},
                {
                    "role": "user",
                    "content": f"키워드: {keyword}\n아래 기억으로 답하세요.\n{payload}",
                },
            ],
            max_tokens=200,
            temperature=0.25,
        )
        answer = resp.choices[0].message.content.strip()
        if answer:
            return answer
    except Exception as ex:
        print(f"[WARN] build_gpt_answer failed: {ex}", flush=True)

    return build_fallback_answer(keyword, matches)


def search_viewer_history(channel_id: str, top_k: int) -> List[Dict[str, Any]]:
    """youtube_channel_id 또는 turn_source=youtube:{id} 일치 기록."""
    cid = (channel_id or "").strip()
    if not cid:
        return []

    top_k = max(1, min(top_k, 20))
    turn_key = f"youtube:{cid}"
    entries = collect_all_entries(load_json_data())
    matched: List[Dict[str, Any]] = []
    for entry in entries:
        if entry.get("youtube_channel_id") == cid or entry.get("turn_source") == turn_key:
            matched.append(dict(entry))

    matched.sort(key=lambda x: -(int(x.get("timestamp") or 0)))
    return matched[:top_k]


def parse_time(time_str: str) -> datetime:
    return datetime.strptime(time_str, "%Y-%m-%d-%H-%M-%S")


@app.get("/health")
def health_check():
    return {"status": "running", "timestamp": datetime.now().isoformat(), "server": "fastapi"}


@app.post("/data")
def receive_data(payload: DataPayload):
    try:
        dt = parse_time(payload.time)
    except ValueError:
        raise HTTPException(status_code=400, detail="시간 형식 오류. YYYY-MM-DD-HH-mm-ss")

    timestamp = payload.timestamp or int(datetime.now().timestamp() * 1000)
    summary = summarize_dialogue(payload.dv_input, payload.nin_response)

    data = load_json_data()
    # 초(second) 단위까지 경로에 포함해 같은 분 내 대화가 덮어써지는 것을 방지한다.
    path = [dt.strftime("%Y"), dt.strftime("%m"), dt.strftime("%d"), dt.strftime("%H"), dt.strftime("%M"), dt.strftime("%S")]
    parent = ensure_path(data, path)
    leaf = {
        "time": payload.time,
        "timestamp": timestamp,
        "dv_input": payload.dv_input,
        "nin_response": payload.nin_response,
        "summation": summary,
        "action_embedding": payload.action_embedding or [],
    }
    if payload.body_command_id:
        leaf["body_command_id"] = payload.body_command_id
    if payload.emotion_folder:
        leaf["emotion_folder"] = payload.emotion_folder
    if payload.obs_vector:
        leaf["obs_vector"] = payload.obs_vector
    if payload.act_vector:
        leaf["act_vector"] = payload.act_vector
    if payload.pose_before:
        leaf["pose_before"] = payload.pose_before
    if payload.pose_after:
        leaf["pose_after"] = payload.pose_after
    if payload.saved_pose_name:
        leaf["saved_pose_name"] = payload.saved_pose_name
    if payload.learning_weight is not None:
        leaf["learning_weight"] = float(payload.learning_weight)
    if payload.policy_act_vector:
        leaf["policy_act_vector"] = payload.policy_act_vector
    if payload.guidance_applied:
        leaf["guidance_applied"] = bool(payload.guidance_applied)
    if payload.guidance_joint_mask:
        leaf["guidance_joint_mask"] = payload.guidance_joint_mask
    if payload.guidance_reason:
        leaf["guidance_reason"] = payload.guidance_reason
    if payload.reference_pose_name:
        leaf["reference_pose_name"] = payload.reference_pose_name
    if payload.dataset_match_score is not None:
        leaf["dataset_match_score"] = float(payload.dataset_match_score)
    if payload.intent_category:
        leaf["intent_category"] = payload.intent_category
    if payload.behavior_tag:
        leaf["behavior_tag"] = payload.behavior_tag
    if payload.turn_source:
        leaf["turn_source"] = payload.turn_source
    if payload.youtube_channel_id:
        leaf["youtube_channel_id"] = payload.youtube_channel_id
    if payload.youtube_display_name:
        leaf["youtube_display_name"] = payload.youtube_display_name

    parent[path[-1]] = leaf
    save_json_data(data)

    upsert_chroma_entry(parent[path[-1]])
    return {"success": True, "message": "데이터 저장 완료", "summation": summary}


@app.post("/log_motion_turn")
def log_motion_turn(payload: DataPayload):
    """모션 필드 포함 턴 로그 — /data와 동일 저장 경로."""
    return receive_data(payload)


@app.on_event("startup")
def on_startup_sync_chroma() -> None:
    # YYDate.Json에만 있고 Chroma에 없는 옛 기록을 기동 시 복구한다.
    ensure_chroma_synced_with_json(force=False)


@app.post("/reindex")
def reindex_memory():
    stats = reindex_all_entries_from_json()
    return {"success": True, "message": "YYDate.Json → Chroma 재인덱싱 완료", **stats}


@app.post("/search")
def search_memory(req: SearchRequest):
    keyword = req.keyword.strip()
    if not keyword:
        raise HTTPException(status_code=400, detail="keyword 필드가 필요합니다.")

    matches = hybrid_search(keyword, req.top_k)
    found = len(matches) > 0
    gpt_answer = build_gpt_answer(keyword, matches) if found else ""
    reason = (
        f"'{keyword}'와 관련된 대화 {len(matches)}건을 찾았습니다."
        if found
        else f"'{keyword}'와 관련된 대화 기억이 없습니다."
    )
    return {
        "success": True,
        "found": found,
        "keyword": keyword,
        "reason": reason,
        "gpt_answer": gpt_answer,
        "matches": matches,
    }


@app.post("/search_viewer")
def search_viewer(req: ViewerSearchRequest):
    channel_id = req.channel_id.strip()
    if not channel_id:
        raise HTTPException(status_code=400, detail="channel_id 필드가 필요합니다.")

    matches = search_viewer_history(channel_id, req.top_k)
    found = len(matches) > 0
    gpt_answer = build_gpt_answer(channel_id, matches) if found else ""
    reason = (
        f"YouTube 시청자 {channel_id} 관련 대화 {len(matches)}건을 찾았습니다."
        if found
        else f"YouTube 시청자 {channel_id} 관련 대화 기억이 없습니다."
    )
    return {
        "success": True,
        "found": found,
        "keyword": channel_id,
        "reason": reason,
        "gpt_answer": gpt_answer,
        "matches": matches,
    }


@app.post("/get_data")
def get_data(req: SummationRequest):
    try:
        dt = parse_time(req.time)
    except ValueError:
        raise HTTPException(status_code=400, detail="시간 형식 오류")

    data = load_json_data()
    node = (
        data.get(dt.strftime("%Y"), {})
        .get(dt.strftime("%m"), {})
        .get(dt.strftime("%d"), {})
        .get(dt.strftime("%H"), {})
        .get(dt.strftime("%M"))
    )
    if not node:
        raise HTTPException(status_code=404, detail="데이터 없음")
    return {"success": True, "data": node}


@app.get("/get_all")
def get_all_data():
    data = load_json_data()
    entries = collect_all_entries(data)
    return {"success": True, "entries": entries, "count": len(entries)}


@app.api_route("/fill_summations", methods=["GET", "POST"])
def fill_summations(repair_truncated: bool = True):
    data = load_json_data()
    filled = 0
    repaired = 0

    def traverse(node: Any) -> None:
        nonlocal filled, repaired
        if isinstance(node, dict):
            if "time" in node and "dv_input" in node and "nin_response" in node:
                current = str(node.get("summation", "")).strip()
                needs_new = not current
                if repair_truncated and current and is_summation_incomplete(current):
                    needs_new = True
                    repaired += 1
                if needs_new:
                    node["summation"] = summarize_dialogue(node.get("dv_input", ""), node.get("nin_response", ""))
                    filled += 1
                if "timestamp" not in node:
                    node["timestamp"] = int(datetime.now().timestamp() * 1000)
                if "action_embedding" not in node:
                    node["action_embedding"] = []
                upsert_chroma_entry(node)
                return
            for v in node.values():
                traverse(v)
        elif isinstance(node, list):
            for item in node:
                traverse(item)

    traverse(data)
    save_json_data(data)
    return {
        "success": True,
        "message": f"{filled}개의 summation을 생성/갱신했습니다. (끊김 복구 {repaired}건)",
        "filled_count": filled,
        "repaired_truncated_count": repaired,
        "repair_truncated": repair_truncated,
    }


@app.post("/summation")
def regenerate_summation(req: SummationRequest):
    try:
        dt = parse_time(req.time)
    except ValueError:
        raise HTTPException(status_code=400, detail="시간 형식 오류")

    data = load_json_data()
    node = (
        data.get(dt.strftime("%Y"), {})
        .get(dt.strftime("%m"), {})
        .get(dt.strftime("%d"), {})
        .get(dt.strftime("%H"), {})
        .get(dt.strftime("%M"))
    )
    if not node:
        raise HTTPException(status_code=404, detail="데이터 없음")

    node["summation"] = summarize_dialogue(node.get("dv_input", ""), node.get("nin_response", ""))
    if "timestamp" not in node:
        node["timestamp"] = int(datetime.now().timestamp() * 1000)
    if "action_embedding" not in node:
        node["action_embedding"] = []
    save_json_data(data)
    upsert_chroma_entry(node)
    return {"success": True, "message": "Summation이 재생성되었습니다.", "summation": node["summation"]}


@app.get("/")
def serve_dashboard():
    if HTML_FILE.exists():
        return FileResponse(str(HTML_FILE))
    return JSONResponse(content={"success": True, "message": "dashboard.html not found"})


def run():
    import uvicorn

    # Chroma 동기화는 네트워크/모델 다운로드에 의존하므로 서버 기동을 막지 않는다.
    print("[INFO] FastAPI memory server starting on http://127.0.0.1:5000", flush=True)
    try:
        ensure_chroma_synced_with_json(force=False)
    except Exception as ex:
        print(f"[WARN] startup chroma sync skipped: {ex}", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=5000, log_level="info")


if __name__ == "__main__":
    run()
