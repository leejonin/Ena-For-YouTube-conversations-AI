@echo off
setlocal EnableExtensions
REM Play 전 워밍업 — health + 짧은 chat ping

set "URL=http://127.0.0.1:8080/health"
set "CHAT=http://127.0.0.1:8080/v1/chat/completions"

echo [LocalLLM] health check...
curl -s -o nul -w "HTTP %%{http_code}\n" "%URL%"
if errorlevel 1 (
  echo [LocalLLM] health 실패 — start_local_llm.bat 먼저 실행
  exit /b 1
)

echo [LocalLLM] warmup ping...
curl -s -X POST "%CHAT%" ^
  -H "Content-Type: application/json" ^
  -d "{\"model\":\"qwen2.5-14b-instruct\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],\"max_tokens\":8}" ^
  > nul

echo [LocalLLM] warmup OK
exit /b 0
