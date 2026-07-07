# 이나(ForChat) 로컬 가동 가이드

로컬 **대화 LM** + **로컬 TTS(Supertonic 3)** + **DateServer(기억)** 를 Unity Play 전에 기동하는 방법입니다.

| 구성요소 | 포트 | 역할 |
|---------|------|------|
| llama-server (Qwen 7B) | **8080** | 대화·SelfTalk·검색 2차 LLM |
| Supertonic 3 TTS (CPU) | **8081** | 음성 합성 (WAV) |
| DateServer | **5000** | Chroma 기억 검색·YYDate 저장 |
| Unity Play | — | 3D·TTS 재생·motion |

---

## 1. 최초 1회 설치

### 1-1. 로컬 대화 LM

```powershell
# llama-server (미설치 시)
winget install llama.cpp
# 설치 후 터미널을 한 번 닫았다가 다시 여세요.

# Git 저장소 루트에서
powershell -ExecutionPolicy Bypass -File .\ForChat\setup_local_llm.ps1

# 이미 .\ForChat 안이면
powershell -ExecutionPolicy Bypass -File .\setup_local_llm.ps1
```

- GGUF 배치 폴더: `.\Models\`
- 권장: **Qwen2.5-7B-Instruct Q4_K_M** (`*.gguf`)
- 상세: `Models/README.txt`

### 1-2. 로컬 TTS (Supertonic 3)

```powershell
# Git 저장소 루트에서
powershell -ExecutionPolicy Bypass -File .\ForChat\setup_local_tts.ps1

# 이미 .\ForChat 안이면
powershell -ExecutionPolicy Bypass -File .\setup_local_tts.ps1
```

- venv: `TTS_Server/.venv`
- 모델: Hugging Face 캐시 자동 다운로드 (~400MB, 첫 합성 시)
- 음색: `local_tts_config.json` → `voice` (기본 `F3`)
- 상세: `TTS_Models/README.txt`

### 1-3. DateServer

DateServer는 `DateServer/.venv` 가 있으면 자동 사용합니다.  
최초 Python 환경이 없으면 `DateServer` 폴더에서 venv를 만들고 `requirements`를 설치하세요.

### 1-4. Unity API 키 (fallback용)

`.example` 을 복사한 뒤 `PUT_OUR_*` 를 실제 키로 교체하세요.

```powershell
copy ckey.txt.example ckey.txt
copy key.txt.example key.txt
copy "yt key.txt.example" "yt key.txt"
```

| 파일 | 용도 |
|------|------|
| `ckey.txt` | Anthropic (로컬 LLM 비활성/fallback) |
| `key.txt` | OpenAI TTS (로컬 TTS 실패 시) |

YouTube Live: `yt key.txt` + Unity `GetYoutubeLiveChat` Inspector의 **channelId** 설정.

로컬만 쓸 때도 fallback 대비로 유지하는 것을 권장합니다.

---

## 2. 방송 전 기동 순서 (매번)

작업 폴더로 이동한 뒤 실행합니다.

- **Git 저장소**: 저장소 루트에서 `cd .\ForChat`
- **Unity 프로젝트**: `cd Assets\AI\ForChat` (복사 후)

```powershell
cd .\ForChat
```

### 2-1. bat 실행 (권장)

```cmd
start_local_llm.bat
warmup_local_llm.bat
start_local_tts.bat
warmup_local_tts.bat
start_dateserver.bat
```

**OBS 방송 중 (Victus 16-s1xxx 권장)** — `live_resource_profile.json` 기준 원클릭:

```cmd
start_live_stack_obs.bat
```

**32GB RAM 최대 절약 (parallel 1, barge-in OFF)** — Phase 0 정리 후:

```cmd
start_live_stack_min_ram.bat
```

이후 Unity Play → OBS(선택).

### 2-2. PowerShell 한 줄씩

```powershell
# Git 저장소 루트에서
cd .\ForChat

.\start_local_llm.bat
.\warmup_local_llm.bat
.\start_local_tts.bat
.\warmup_local_tts.bat
.\start_dateserver.bat
```

### 2-3. 서버별 실행 명령어

#### 로컬 대화 LM (llama-server · :8080)

llama serve -hf Qwen/Qwen2.5-7B-Instruct-GGUF:Q4_K_M

```cmd
REM bat (백그라운드 창 NIN-LocalLLM)
start_local_llm.bat
warmup_local_llm.bat
```

```powershell
# PowerShell
.\start_local_llm.bat
.\warmup_local_llm.bat
```

```cmd
REM 수동 (bat 없이, Models\*.gguf 필요)
llama-server -m "Models\YOUR_MODEL.gguf" ^
  --host 127.0.0.1 --port 8080 -c 4096 -ngl 12 --threads 3 --parallel 1
```

```powershell
# health / warmup ping
curl http://127.0.0.1:8080/health
curl -X POST http://127.0.0.1:8080/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{"model":"qwen2.5-7b-instruct","messages":[{"role":"user","content":"ping"}],"max_tokens":8}'
```

#### 로컬 TTS (Supertonic 3 · :8081)

```cmd
REM bat (백그라운드 창 NIN-LocalTTS)
start_local_tts.bat
warmup_local_tts.bat
```

```powershell
# PowerShell
.\start_local_tts.bat
.\warmup_local_tts.bat
```

```powershell
# 수동 (bat 없이, setup_local_tts.ps1 완료 후)
cd TTS_Server
.\.venv\Scripts\python.exe -m uvicorn tts_server:app --host 127.0.0.1 --port 8081
```

```powershell
# health / warmup 합성
curl http://127.0.0.1:8081/health
curl -X POST http://127.0.0.1:8081/v1/audio/speech `
  -H "Content-Type: application/json" `
  -d '{"input":"안녕","voice":"F3","speed":1.04}' -o test.wav
```

#### DateServer (기억 · :5000)

```cmd
REM bat (포그라운드 — 이 창을 닫으면 서버 종료)
start_dateserver.bat
```

```powershell
# PowerShell
.\start_dateserver.bat
```

```powershell
# 수동 (bat 없이)
cd DateServer
$env:LOCAL_LLM_BASE_URL = "http://127.0.0.1:8080/v1"
$env:LOCAL_LLM_API_KEY = "local"
$env:LOCAL_LLM_MODEL = "qwen2.5-7b-instruct"
.\.venv\Scripts\python.exe -m uvicorn Sever:app --host 127.0.0.1 --port 5000
```

```powershell
# health
curl http://127.0.0.1:5000/health
```

### 2-4. 전체 한 번에 (복사용)

```powershell
cd .\ForChat
.\start_local_llm.bat; .\warmup_local_llm.bat
.\start_local_tts.bat; .\warmup_local_tts.bat
Start-Process -FilePath "cmd.exe" -ArgumentList "/c","start_dateserver.bat" -WorkingDirectory (Get-Location)
powershell -ExecutionPolicy Bypass -File .\verify_live_sla.ps1
```

DateServer는 별도 cmd 창에서 계속 떠 있어야 합니다. 위 `Start-Process`는 새 창에서 기동합니다.

### 기동 확인 (한 줄)

```powershell
powershell -ExecutionPolicy Bypass -File .\verify_live_sla.ps1
```

기대 출력:

- `LLM health: 200`
- `TTS health: 200`
- `DateServer health: 200`
- RAM used/available GB, WARN if used > 24GB or available < 4GB
- RAM 28GB 이하, VRAM 7GB 이하 권장

### Unity Console 로그

Play 후 아래가 보이면 정상입니다.

- `[LocalLLM] warmup OK`
- `[LocalTTS] health OK`
- `[LocalTTS] warmup synthesis OK` (선택)

그 다음: **Unity → SampleScene → Play** → OBS 방송/녹화(선택)

---

## 3. 아키텍처 요약

```
[사용자/YouTube/SelfTalk]
        ↓
   SendMessage.cs ──HTTP──► llama-server :8080  (대화 LLM)
        ↓
   (이모지+대사) 청크 파싱
        ↓
   TTSRequester ──HTTP──► Supertonic 3 TTS :8081  (WAV)
        ↓
   원음 재생 (후처리 없음)
        ↓
   LipSync + motion + TTS 재생

[기억 검색 *대화검색*]
   SendMessage 2차 호출 → llama-server
   DateServer :5000 → Chroma + 검색 요약(로컬 LLM)
```

- **Vision**: 로컬 LLM 사용 시 비활성 (`local_llm_config.json` → `disableVisionWhenLocal: true`)
- **TTS**: Supertonic 3 CPU 전용 (VRAM 0) — LLM·Unity·OBS와 GPU 경쟁 방지
- **페르소나**: `이나(E-na).txt`, `role.txt`, `ChatPersonaDefense` 유지

---

## 4. 설정 파일

### `local_llm_config.json`

| 항목 | 현재값 | 설명 |
|------|--------|------|
| `enabled` | true | false면 Anthropic fallback |
| `baseUrl` | `http://127.0.0.1:8080/v1` | OpenAI 호환 API |
| `timeoutSeconds` | 120 | LLM 응답 대기 (초과 시 SLA fallback TTS) |
| `maxTokensUserTurn` | 512 | 사용자 턴 최대 토큰 |
| `maxTokensSelfTalkTurn` | 384 | SelfTalk 턴 |
| `maxTokensSearchFallback` | 512 | *대화검색* 2차 호출 |
| `apiFullHistoryRecentMessages` | 4 | LLM에 보내는 최근 메시지 수 |
| `slaFallbackDialogue` | `(😅 아 잠깐만! ...)` | timeout 시 재생 대사 |
| `enableBargeIn` | false | SelfTalk/TTS 중 YouTube·개발자 입력 끼어들기 |
| `bargeInDuringSelfTalk` | true | 혼잘말 LLM+TTS 중 끼어들기 (enableBargeIn=true일 때) |
| `bargeInDuringTts` | true | TTS 재생 중 끼어들기 (enableBargeIn=true일 때) |
| `bargeInDeveloperPriority` | true | 개발자 Enter도 끼어들기 (enableBargeIn=true일 때) |
| `llamaParallelSlots` | 1 | `start_local_llm.bat` `--parallel` 문서용 |

**barge-in 사용 시** `enableBargeIn: true` + `llamaParallelSlots: 2` + `start_local_llm.bat` 재기동. Console: `[BargeIn] interrupted: ...`

### `live_resource_profile.json` (OBS+Victus 16-s1xxx · 최소 RAM)

| 항목 | 현재값 | 설명 |
|------|--------|------|
| `obsLiveMode` | true | OBS 라이브 CPU/VRAM 프로파일 |
| `llamaThreads` | 3 | llama `--threads` |
| `llamaGpuLayers` | 12 | llama `-ngl` |
| `llamaParallelSlots` | 1 | LLM 동시 슬롯 (2=barge-in) |
| `llamaContextSize` | 4096 | llama `-c` (KV RAM) |
| `ttsTotalStepsObs` | 7 | OBS 중 TTS 합성 스텝 |
| `bargeInMinIntervalMs` | 2000 | 연속 끼어들기 최소 간격 |
| `ttsPostInterruptDebounceMs` | 400 | interrupt 후 첫 TTS fetch 지연 |
| `unityTargetFrameRate` | 45 | 0=미적용 |

### YouTube Live 컨텍스트·신규 시청자 인사

- `GetYoutubeLiveChat`: `videos.list` **60초**마다 `concurrentViewers` → LLM `[라이브방송] 시청자 N명 | 최근채팅: ...`
- 채팅 **링 버퍼 40건** — SelfTalk·YT 턴 프롬프트에 주입
- **channelId 첫 채팅** → `[YT신규시청자|닉]` 인사 턴 1회 (분당 최대 3건, barge-in 우선)
- 두 번째 채팅부터 일반 YT 턴

### 페르소나 다양화 (`Resources/persona_variation_pool.json`)

- 카테고리별 `(이모지+대사)` 예시 — `PersonaFewShotRotator`가 턴마다 3~4카테고리 회전
- JSON 편집 후 Unity Play 재시작
- anti-repeat: 직전 assistant와 동일 인사·감탄 반복 금지

### `local_tts_config.json`

| 항목 | 현재값 | 설명 |
|------|--------|------|
| `enabled` | true | false면 OpenAI TTS만 사용 |
| `baseUrl` | `http://127.0.0.1:8081` | Supertonic HTTP |
| `voice` | `F2` | 음색 (F1~F5, M1~M5) |
| `lang` | `ko` | 단일 합성 fallback 언어 (분할 모드에서는 구간별 ko/en) |
| `timeoutSeconds` | 20 | 합성 대기 |
| `fallbackToOpenAi` | false | 실패 시 OpenAI TTS |
| `koreanOnly` | false | true면 한글·숫자·기본 구두점만 (영문 TTS 제거) |
| `bilingualSegmentTts` | true | false가 아니고 koreanOnly=false일 때 한·영 구간 분할 합성 |
| `onnxThreads` | 1 | CPU 스레드 (OBS+LLM 공존) |
| `totalSteps` | 7 | 합성 품질 (5~12) |
| `synthesisSpeed` | 0.92 | 발화 속도 |

설정 변경 후 Unity **Play를 다시 시작**하세요.

---

## 5. 메모리 최적화 (32GB Victus · 최대 절약)

Unity·라이브 스택 기동 **전** RAM이 80% 이상이면 스왑·TTS 끊김 위험이 큽니다. **Phase 0 → 스택 → Unity → OBS** 순서를 지키세요.

### Phase 0 — 베이스라인 정리 (코드 변경 없음)

1. **작업 관리자 → 메모리** — 상위 프로세스 확인
2. **종료 권장**
   - `vmmemWSL` → PowerShell: `wsl --shutdown` (WSL 불필요 시)
   - Chrome, Discord, `obs-browser-page`
   - 이전 세션: `llama-server`, `NIN-LocalLLM`, `NIN-LocalTTS`, `python`(DateServer), Unity Editor, `obs64`
3. Cursor만 유지 — 불필요 탭·워크스페이스 닫기
4. **Committed 37GB+** 이면 재부팅 — 재부팅 후 사용 RAM **~8 GB 이하** 목표
5. **검증**: `verify_live_sla.ps1` 또는 작업 관리자 **사용 가능 ≥ 20 GB**

### Phase 1 — 최소 RAM 프로파일 (현재 기본값)

| 구성 | 설정 | 효과 |
|------|------|------|
| llama | `ctx 4096`, `parallel 1`, `ngl 12`, `threads 3` | KV·VRAM 절감 |
| LLM 프롬프트 | `maxTokens` 512/384, history **4** | 요청당 RAM 완화 |
| barge-in | `enableBargeIn: false` | parallel 1과 정합 — **끼어들기 큐 대기** |
| TTS | `totalSteps 7`, `onnxThreads 1` | CPU·버퍼 절감 |
| Unity | `unityTargetFrameRate 45` | GPU/RAM 완화 |

원클릭 기동:

```cmd
start_live_stack_min_ram.bat
```

barge-in이 필요하면 `local_llm_config.json`에서 `enableBargeIn: true`, `llamaParallelSlots: 2` + `live_resource_profile.json`에서 `llamaParallelSlots: 2`, `llamaGpuLayers: 20` 등으로 되돌린 뒤 `start_local_llm.bat` 재기동.

### Phase 2 — 기동 순서·RAM 예산

**순서**: Phase 0 정리 → llama → TTS → (Play 직전) DateServer → Unity Play → OBS

| 구성 | 예상 RAM | VRAM |
|------|----------|------|
| Windows + Cursor | 6–8 GB | — |
| llama ctx4096 parallel1 ngl12 | 3–4 GB | ~3–4 GB |
| TTS Supertonic | 0.4–0.8 GB | 0 |
| DateServer + Chroma | 0.8–1.5 GB | 0 |
| Unity Play | 2–3 GB | ~1 GB |
| OBS NVENC | 0.8–1.2 GB | ~0.5 GB |

**목표**: 스택+방송 **RAM ≤ 28 GB** (WARN **24 GB**), **VRAM ≤ 7 GB**

DateServer는 Chroma 임베딩 로드로 **+0.5~1.5 GB** — Unity Play 직전까지 미기동하면 피크 분산 가능.

---

## 6. OBS + 이나 동시 가동 (Victus 16-s1xxx · Ryzen 8645HS · 32GB · RTX 4060)

| 프로세스 | CPU | VRAM | 우선순위 |
|---------|-----|------|----------|
| llama-server | threads **3**, `--parallel 1`, `-c 4096` | ~3–4GB (`-ngl 12`) | BELOWNORMAL |
| Supertonic 3 TTS | onnxThreads **1**, steps **7** (OBS) | 0 (CPU) | BELOWNORMAL |
| Unity + OBS | targetFrameRate **45** | ~1~2GB | Normal |

**방송 전 권장**

- 불필요 앱 종료 (RAM 여유 확보)
- OBS 인코더: **NVENC H.264**
- 출력 해상도 = 캔버스 해상도 (불필요 스케일링 금지)

**목표 리소스**: RAM 28GB 이하, VRAM 7GB 이하

**OBS ON SLA 검증** (`verify_live_sla.ps1` + Unity Play + OBS 녹화):

1. SelfTalk 5턴 + YT 채팅 3턴 — TTS 무음/스킵 0회
2. bilingual `hello 아빠` 청크 10초 SLA
3. `verify_live_sla.ps1` — RAM available WARN 없음
4. (barge-in ON 시) 5회 끼어들기 — `[BargeIn] interrupted:` 로그

---

## 7. 생방송 SLA 체크리스트

`verify_live_sla.ps1` 실행 후 Unity Play에서 수동 확인:

1. 개발자 턴 20회: **TTS 재생 시작 10초 이내**
2. SelfTalk 10턴: 문맥·주제 연속
3. 청크 3개 이상 응답: prefetch로 끊김 없음
4. `*대화검색*` 5회: 검색 후 TTS 정상
5. YouTube 시청자 2회: 닉네임 호칭·TTS 정상
6. `TTS_Output/`에 `.wav` 저장
7. 턴 종료 후 `DateServer/YYDate.Json`, `session_history.json` 갱신

---

## 8. 문제 해결

| 증상 | 확인 |
|------|------|
| SLA fallback만 반복 | `start_local_llm.bat` 실행 여부, `timeoutSeconds` (60초) |
| `[LocalLLM] health check failed` | `Models/*.gguf` 존재, 8080 포트 충돌 |
| `[LocalTTS] health check failed` | `setup_local_tts.ps1` 후 `start_local_tts.bat` |
| TTS 무음 / OpenAI 과금 | 로컬 TTS 실패 → `fallbackToOpenAi` 동작, `key.txt` 확인 |
| 한국어 발음 이상 | `lang: ko`, `koreanOnly: true` 확인 후 `setup_local_tts.ps1` 재실행 |
| 영어가 TTS에서 안 들림 | `koreanOnly: false`, `bilingualSegmentTts: true` 확인 후 TTS 서버·Unity Play 재시작 |
| RAM 28GB 초과 / 스왑 | **섹션 5 Phase 0**, `start_live_stack_min_ram.bat`, `wsl --shutdown` |
| VRAM 부족 | llama `-ngl` 축소 (`live_resource_profile.json`), OBS 해상도 낮추기 |
| DateServer FAIL | `start_dateserver.bat`, 5000 포트, `.venv` |

### 수동 health 확인

```text
http://127.0.0.1:8080/health   ← LLM
http://127.0.0.1:8081/health   ← TTS
http://127.0.0.1:5000/health   ← DateServer
```

---

## 9. 스크립트·폴더 목록

| 파일/폴더 | 용도 |
|-----------|------|
| `start_local_llm.bat` | llama-server 기동 (:8080, profile ctx/ngl/parallel) |
| `start_live_stack_obs.bat` | OBS+Victus 프로파일 원클릭 기동 |
| `start_live_stack_min_ram.bat` | 최소 RAM 원클릭 + `verify_live_sla.ps1` |
| `live_resource_profile.json` | CPU/VRAM·ctx·TTS·Unity fps 튜닝 |
| `warmup_local_llm.bat` | LLM ping 워밍업 |
| `setup_local_llm.ps1` | llama-server 바이너리 설치 |
| `start_local_tts.bat` | Supertonic 3 TTS 기동 (:8081) |
| `warmup_local_tts.bat` | TTS 합성 워밍업 |
| `setup_local_tts.ps1` | Supertonic 3 venv + 모델 |
| `start_dateserver.bat` | DateServer 기동 (:5000) |
| `verify_live_sla.ps1` | health + RAM/VRAM·프로세스 점검 |
| `Models/` | GGUF (대화 LM) |
| `TTS_Models/` | TTS 설정·안내 |
| `TTS_Server/` | Supertonic FastAPI 서버 |
| `DateServer/` | 기억·검색 서버 |
| `Resources/persona_variation_pool.json` | few-shot 회전 풀 (편집 가능) |
| `Code/` | SendMessage, TTSRequester, PersonaFewShotRotator 등 |

---

## 10. 관련 문서

- API 키: `SECRETS.md`
- DateServer: `DateServer/README.md`
- GGUF 모델: `Models/README.txt`
- TTS 모델: `TTS_Models/README.txt`

---

## 11. 종료 순서

1. Unity Play 중지
2. OBS 종료
3. `NIN-LocalLLM`, `NIN-LocalTTS`, DateServer 터미널 창 닫기 (또는 작업 관리자에서 종료)

다음 방송 때 **섹션 2**부터 다시 실행하면 됩니다.
