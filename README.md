# NIN AI ForChat (GitHub Distribution)

The `.\ForChat` module in this repository is a **public-facing copy** meant to be dropped into Unity's `Assets/AI/ForChat`.

## API Key Setup (Required)

Copy each `.example` file and **replace `PUT_OUR_*` with your own keys**. **Never push files containing real keys to Git.**

```powershell
cd .\ForChat
copy ckey.txt.example ckey.txt
copy key.txt.example key.txt
copy "yt key.txt.example" "yt key.txt"
```

| File | Purpose |
|------|---------|
| `.\ForChat\ckey.txt` | Anthropic Claude fallback (`PUT_OUR_ANTHROPIC_API_KEY`) |
| `.\ForChat\key.txt` | OpenAI TTS / Vision fallback (`PUT_OUR_OPENAI_API_KEY`) |
| `.\ForChat\yt key.txt` | YouTube Data API (`PUT_OUR_YOUTUBE_DATA_API_KEY`) |

If you're using YouTube Live, enter your own channel ID in the Unity Inspector under `GetYoutubeLiveChat.channelId`.

If you're using local LLM/TTS only, set `enabled: true` in `.\ForChat\local_llm_config.json` / `.\ForChat\local_tts_config.json` — the API keys above then serve only as a fallback.

## Attaching to Unity

1. Copy the entire `.\ForChat` folder from this repository into your project:
   ```
   <YourUnityProject>/Assets/AI/ForChat/
   ```
2. Place your GGUF model(s) in `.\ForChat\Models\` (see `Models\README.txt`)
3. Install the OpenCV DLL in `.\ForChat\Fcam\Plugins\` (see `Fcam\Plugins\README.txt` — **not included in Git**)
4. Run `.\ForChat\setup_local_llm.ps1` / `.\ForChat\setup_local_tts.ps1`
5. Replace the API key templates
6. Refer to `.\ForChat\README.md` for the startup sequence

## Updating the Public Copy (from the dev PC)

```powershell
powershell -ExecutionPolicy Bypass -File .\prepare_forchat_github.ps1 -Source "<original>/Assets/AI/ForChat"
```

If `-Source` is omitted, it will automatically look for `..\NIN_AI_3DModel_Ver2\Assets\AI\ForChat` in the same parent folder.

Sensitive files, `.venv`, `chroma_db`, `TTS_Output`, and `*.gguf` are automatically excluded. Output: `.\ForChat\`


# NIN AI ForChat (GitHub 배포용)

이 저장소의 `.\ForChat` 모듈은 Unity `Assets/AI/ForChat` 에 붙여 쓰는 **공개용** 사본입니다.

## API 키 설정 (필수)

`.example` 파일을 복사한 뒤 `PUT_OUR_*` 를 **본인 키로 교체**하세요. **실키가 들어간 파일은 Git에 push 하지 마세요.**

```powershell
cd .\ForChat
copy ckey.txt.example ckey.txt
copy key.txt.example key.txt
copy "yt key.txt.example" "yt key.txt"
```

| 파일 | 용도 |
|------|------|
| `.\ForChat\ckey.txt` | Anthropic Claude fallback (`PUT_OUR_ANTHROPIC_API_KEY`) |
| `.\ForChat\key.txt` | OpenAI TTS·Vision fallback (`PUT_OUR_OPENAI_API_KEY`) |
| `.\ForChat\yt key.txt` | YouTube Data API (`PUT_OUR_YOUTUBE_DATA_API_KEY`) |

YouTube Live 사용 시 Unity Inspector의 `GetYoutubeLiveChat.channelId`에 본인 채널 ID를 입력하세요.

로컬 LLM/TTS만 사용 시 `.\ForChat\local_llm_config.json` / `.\ForChat\local_tts_config.json` 에서 `enabled: true` 로 두면 위 키는 fallback 용도입니다.

## Unity에 붙이기

1. 이 저장소의 `.\ForChat` 폴더 전체를 프로젝트에 복사:
   ```
   <YourUnityProject>/Assets/AI/ForChat/
   ```
2. `.\ForChat\Models\` 에 GGUF 모델 배치 (`Models\README.txt` 참고)
3. `.\ForChat\Fcam\Plugins\` 에 OpenCV DLL 설치 (`Fcam\Plugins\README.txt` 참고, **Git 미포함**)
4. `.\ForChat\setup_local_llm.ps1` / `.\ForChat\setup_local_tts.ps1` 실행
5. API 키 템플릿 교체
6. `.\ForChat\README.md` 기동 순서 참고

## 사본 갱신 (개발 PC에서)

```powershell
powershell -ExecutionPolicy Bypass -File .\prepare_forchat_github.ps1 -Source "<원본>/Assets/AI/ForChat"
```

`-Source` 생략 시 같은 상위 폴더의 `..\NIN_AI_3DModel_Ver2\Assets\AI\ForChat` 을 자동 탐색합니다.

민감 파일·`.venv`·`chroma_db`·`TTS_Output`·`*.gguf` 는 자동 제외됩니다. 출력: `.\ForChat\`


