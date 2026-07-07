# NIN DateServer — 메모리·대화 저장 API

> **FastAPI** (`Sever.py`) + **YYDate.Json** + **ChromaDB**  
> Unity `ServerCommunication.cs`가 Play 시 `.venv`로 서버를 자동 기동한다.

---

## 개요

| 항목 | 내용 |
|------|------|
| 프레임워크 | FastAPI + uvicorn (레거시 문서의 Flask 아님) |
| 기본 URL | `http://127.0.0.1:5000` |
| 데이터 파일 | `.\DateServer\YYDate.Json` |
| 벡터 DB | `chroma_db/` (`nin_dialogue_action_memory`) |
| Unity 클라이언트 | `ServerCommunication.cs` |

### 주요 기능

- 대화 턴 저장 (`dv_input`, `nin_response`)
- **gpt-4o-mini** 대화 요약 (`summation`, 키 없으면 규칙 fallback)
- Chroma 의미 검색 + GPT 답변 (`POST /search`)
- **모션 학습 로그** (`obs_vector`, `act_vector`, `pose_before/after`, `body_command_id`, `learning_weight`)
- 기동 시 YYDate ↔ Chroma 동기화 (`startup` / `POST /reindex`)

---

## 설치·실행

### 요구사항

- Python 3.10+ 권장
- 가상환경: `.\DateServer\.venv`

```powershell
# .\ForChat 기준
cd .\DateServer
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe Sever.py
```

### Unity 자동 실행

- 씬에 `ServerCommunication` 컴포넌트 배치
- Play → 프로세스 기동 → `/health` 대기
- (옵션) `dashboard.html` 브라우저 오픈

---

## YYDate.Json 구조

```json
{
  "2026": {
    "06": {
      "05": {
        "14": {
          "30": {
            "time": "2026-06-05-14-30-00",
            "timestamp": 1716892200000,
            "dv_input": "사용자 입력",
            "nin_response": "AI 응답",
            "summation": "[대화 요약] …",
            "action_embedding": [0.1, 0.2],
            "body_command_id": "both_hands_up",
            "emotion_folder": "Basic",
            "obs_vector": [],
            "act_vector": [],
            "pose_before": [],
            "pose_after": [],
            "saved_pose_name": "NIN_…",
            "learning_weight": 1.0
          }
        }
      }
    }
  }
}
```

- **리프 키:** 분(`mm`) — 동일 시·분에 여러 턴이면 키가 분 단위로 구분됨  
- 모션 필드는 Unity `SendMotionTurnData` 호출 시에만 채워짐 (없으면 생략)

---

## API 명세

### GET `/health`

```json
{ "status": "running", "timestamp": "…", "server": "fastapi" }
```

### POST `/data`

대화 저장 + summation 생성 + Chroma upsert.

**요청 (필수 + 선택):**

```json
{
  "time": "2026-06-05-14-30-00",
  "timestamp": 1716892200000,
  "dv_input": "사용자 입력",
  "nin_response": "AI 응답",
  "action_embedding": [0.1, 0.2],
  "body_command_id": "",
  "emotion_folder": "Basic",
  "obs_vector": [],
  "act_vector": [],
  "pose_before": [],
  "pose_after": [],
  "saved_pose_name": "",
  "learning_weight": 0.3
}
```

**응답:**

```json
{ "success": true, "message": "데이터 저장 완료", "summation": "…" }
```

### POST `/log_motion_turn`

`/data`와 **동일** 처리 (모션 턴 로깅 의도 명시용 별칭).

### POST `/search`

```json
{ "keyword": "양손", "top_k": 12 }
```

**응답:**

```json
{
  "success": true,
  "found": true,
  "keyword": "양손",
  "reason": "…",
  "gpt_answer": "…",
  "matches": [
    { "time": "…", "dv_input": "…", "nin_response": "…", "summation": "…" }
  ]
}
```

Unity: `SendMessage`의 `*대화검색*-키워드-` → `ServerCommunication.SearchMemory`.

### POST `/reindex`

`YYDate.Json` 전체를 Chroma에 재인덱싱.

### POST `/get_data`

```json
{ "time": "2026-06-05-14-30-00" }
```

### GET `/get_all`

저장된 모든 엔트리 목록.

### GET/POST `/fill_summations`

빈 `summation`만 일괄 생성 (기존 값 덮어쓰지 않음).

### POST `/summation`

특정 `time`의 summation **재생성**(덮어씀).

### GET `/`

`dashboard.html` 웹 UI.

---

## Summation

1. `normalize_dialogue_for_summary`로 텍스트 정제  
2. `OPENAI_API_KEY` 있으면 **gpt-4o-mini** 한 문장 요약  
3. 없으면 `finalize_summation` 규칙 fallback  

형식 예: `[대화 요약] …`

---

## Chroma

- 컬렉션: `nin_dialogue_action_memory`
- 임베딩 실패·다운로드 실패 시 `_chroma_usable=false` → **키워드 검색 fallback**, HTTP 서버는 계속 동작
- `hybrid_search`: 의미 검색 + 동의어 확장 (`SearchRequest.top_k` 기본 12)

---

## Unity 연동 예

### 기본 대화 저장

```csharp
serverCommunication.SendCustomData(dvInput, ninResponse, actionEmbedding, timestampMs);
```

### 모션 턴 (재학습용)

```csharp
noPidHumanoidAgent.TryBuildMotionLogVectors(out float[] obs, out float[] act);
serverCommunication.SendMotionTurnData(
    dvInput, ninResponse, bodyCommandId, emotionFolder,
    obs, act, poseBefore, poseAfter, savedPoseName,
    learningWeight, actionEmbedding, timestampMs);
```

오프라인: `Assets/AI/Body/ML/build_conversation_trajectories.py` → `merge_no_pid_supervised_npz.py`.

---

## 환경 변수

| 변수 | 용도 |
|------|------|
| `OPENAI_API_KEY` | summation, `/search` GPT 답변 |

---

## 트러블슈팅

| 증상 | 확인 |
|------|------|
| 서버 미기동 | `.venv` 경로, `ServerCommunication` 로그, 포트 5000 충돌 |
| 검색 400 | `keyword` 빈 문자열 — Unity에서 skip |
| Chroma만 실패 | JSON·keyword fallback으로 동작 — `/reindex` 시도 |
| summation 비어 있음 | `OPENAI_API_KEY` 또는 `/fill_summations` |

---

## 관련 문서

- [Assets/Readme_md/README.md](../../../Readme_md/README.md) — 프로젝트 전체
- [Assets/Readme_md/ARCHITECTURE.md](../../../Readme_md/ARCHITECTURE.md) — API·데이터 흐름
- [Assets/AI/Body/ML/START_NO_PID_MLAGENTS.md](../../Body/ML/START_NO_PID_MLAGENTS.md) — obs/act·재학습

---

*갱신: 2026-06 — `Sever.py` v2.0.0 FastAPI 기준*
