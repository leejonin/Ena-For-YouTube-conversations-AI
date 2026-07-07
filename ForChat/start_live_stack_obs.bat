@echo off
setlocal EnableExtensions
REM OBS+이나 동시 가동 — live_resource_profile.json 기준 LLM/TTS/DateServer 원클릭 기동 (Victus 16 권장)

set "ROOT=%~dp0"
echo [LiveStack-OBS] live_resource_profile.json (ctx/ngl/parallel from profile)
echo [LiveStack-OBS] OBS: NVENC H.264, canvas=output, RAM 목표 ^<28GB, VRAM ^<7GB

call "%ROOT%start_local_llm.bat"
if errorlevel 1 exit /b 1
timeout /t 3 /nobreak >nul

call "%ROOT%start_local_tts.bat"
if errorlevel 1 exit /b 1
timeout /t 2 /nobreak >nul

if exist "%ROOT%start_dateserver.bat" (
  call "%ROOT%start_dateserver.bat"
  timeout /t 2 /nobreak >nul
)

if exist "%ROOT%warmup_local_tts.bat" (
  call "%ROOT%warmup_local_tts.bat"
)

echo [LiveStack-OBS] Unity Play 후 OBS 녹화/스트림 ON — verify_live_sla.ps1 로 SLA 확인
exit /b 0
