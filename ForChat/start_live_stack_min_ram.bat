@echo off
setlocal EnableExtensions
REM 최소 RAM 프로파일 — Phase0 정리 후 실행 (live_resource_profile.json 기준)

set "ROOT=%~dp0"
echo [LiveStack-MinRAM] Phase0: wsl --shutdown, Chrome/OBS/Unity/llama 잔존 종료 권장
echo [LiveStack-MinRAM] Committed 37GB+ 이면 재부팅 후 실행
echo [LiveStack-MinRAM] parallel=1, ctx=4096, ngl=12 — barge-in OFF ^(local_llm_config^)
echo [LiveStack-MinRAM] 목표: 가용 RAM 20GB+, 스택 후 28GB 이하

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

if exist "%ROOT%verify_live_sla.ps1" (
  powershell -ExecutionPolicy Bypass -File "%ROOT%verify_live_sla.ps1"
)

echo [LiveStack-MinRAM] Unity Play 후 OBS — DateServer는 Play 직전 기동 권장
exit /b 0
