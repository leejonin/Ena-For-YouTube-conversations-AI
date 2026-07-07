# API 키 (GitHub 템플릿)

`.example` 파일을 복사해 로컬 키 파일을 만든 뒤, 플레이스홀더를 실제 키로 교체하세요.

```powershell
copy ckey.txt.example ckey.txt
copy key.txt.example key.txt
copy "yt key.txt.example" "yt key.txt"
```

| 파일 | 플레이스홀더 | 용도 |
|------|----------------|------|
| `ckey.txt` | `PUT_OUR_ANTHROPIC_API_KEY` | Claude fallback |
| `key.txt` | `PUT_OUR_OPENAI_API_KEY` | OpenAI TTS·Vision fallback |
| `yt key.txt` | `PUT_OUR_YOUTUBE_DATA_API_KEY` | YouTube Live 채팅 |

**`ckey.txt` / `key.txt` / `yt key.txt` 는 `.gitignore` 처리됩니다. 실키 커밋 금지.**

로컬 LLM/TTS만 사용할 때도 fallback 대비로 파일은 유지하는 것을 권장합니다.
