@echo off
setlocal EnableExtensions
REM Play 전 워밍업 — Supertonic 3 health + 짧은 합성 ping

set "HEALTH=http://127.0.0.1:8081/health"
set "SPEECH=http://127.0.0.1:8081/v1/audio/speech"

echo [LocalTTS] health check...
curl -s -o nul -w "HTTP %%{http_code}\n" "%HEALTH%"
if errorlevel 1 (
  echo [LocalTTS] health 실패 — start_local_tts.bat 먼저 실행
  exit /b 1
)

echo [LocalTTS] warmup synthesis...
curl -s -X POST "%SPEECH%" ^
  -H "Content-Type: application/json" ^
  -d "{\"input\":\"hello 안녕\",\"voice\":\"F2\",\"speed\":0.92}" ^
  -o nul

echo [LocalTTS] warmup OK
exit /b 0
