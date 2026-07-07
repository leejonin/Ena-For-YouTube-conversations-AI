@echo off
setlocal EnableExtensions
REM Local Supertonic 3 TTS — CPU, port 8081

set "ROOT=%~dp0"
set "SERVER_DIR=%ROOT%TTS_Server"
set "PY="

netstat -ano | findstr "127.0.0.1:8081" | findstr "LISTENING" >nul 2>&1
if not errorlevel 1 (
  echo [LocalTTS] already running on http://127.0.0.1:8081
  echo [LocalTTS] restart: close NIN-LocalTTS window first, then run this again.
  exit /b 0
)

if exist "%SERVER_DIR%\.venv\Scripts\python.exe" (
  set "PY=%SERVER_DIR%\.venv\Scripts\python.exe"
  echo [LocalTTS] using venv python
) else (
  where python >nul 2>&1
  if not errorlevel 1 (
    set "PY=python"
    echo [LocalTTS] using system python
  ) else (
    echo [LocalTTS] python not found. Run setup_local_tts.ps1
    exit /b 1
  )
)

echo [LocalTTS] Supertonic 3 http://127.0.0.1:8081
cd /d "%SERVER_DIR%"

start "NIN-LocalTTS" /BELOWNORMAL %PY% -m uvicorn tts_server:app --host 127.0.0.1 --port 8081

echo [LocalTTS] server started. Run warmup_local_tts.bat to ping.
exit /b 0
