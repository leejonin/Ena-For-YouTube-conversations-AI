@echo off
setlocal EnableExtensions
REM 로컬 대화 LM — llama-server 기동 (live_resource_profile.json threads/ngl/parallel/ctx)
REM llama-server: winget install llama.cpp (PATH) 또는 llama_bin\llama-server.exe

set "ROOT=%~dp0"
set "BIN=%ROOT%llama_bin"
set "MODELS=%ROOT%Models"
set "PROFILE=%ROOT%live_resource_profile.json"
set "SERVER="

set "LLAMA_THREADS=4"
set "LLAMA_NGL=20"
set "LLAMA_PARALLEL=2"
set "LLAMA_CTX=8192"

if exist "%PROFILE%" (
  for /f "delims=" %%T in ('powershell -NoProfile -Command "$p=Get-Content -Raw '%PROFILE%'|ConvertFrom-Json; if($p.llamaThreads){$p.llamaThreads}else{4}"') do set "LLAMA_THREADS=%%T"
  for /f "delims=" %%G in ('powershell -NoProfile -Command "$p=Get-Content -Raw '%PROFILE%'|ConvertFrom-Json; if($p.llamaGpuLayers){$p.llamaGpuLayers}else{20}"') do set "LLAMA_NGL=%%G"
  for /f "delims=" %%P in ('powershell -NoProfile -Command "$p=Get-Content -Raw '%PROFILE%'|ConvertFrom-Json; if($p.llamaParallelSlots){$p.llamaParallelSlots}else{2}"') do set "LLAMA_PARALLEL=%%P"
  for /f "delims=" %%C in ('powershell -NoProfile -Command "$p=Get-Content -Raw '%PROFILE%'|ConvertFrom-Json; if($p.llamaContextSize){$p.llamaContextSize}else{8192}"') do set "LLAMA_CTX=%%C"
  echo [LocalLLM] profile: threads=%LLAMA_THREADS% ngl=%LLAMA_NGL% parallel=%LLAMA_PARALLEL% ctx=%LLAMA_CTX%
)

where llama-server >nul 2>&1
if not errorlevel 1 (
  set "SERVER=llama-server"
  echo [LocalLLM] using PATH: llama-server
) else if exist "%BIN%\llama-server.exe" (
  set "SERVER=%BIN%\llama-server.exe"
  echo [LocalLLM] using: %SERVER%
) else (
  echo [LocalLLM] llama-server 없음.
  echo   winget install llama.cpp  후 터미널을 다시 열거나
  echo   setup_local_llm.ps1 로 llama_bin 에 설치하세요.
  exit /b 1
)

set "GGUF="
for %%F in ("%MODELS%\*.gguf") do set "GGUF=%%~fF"
if not defined GGUF (
  echo [LocalLLM] Models\*.gguf 없음.
  echo   GGUF 모델을 %MODELS% 에 넣으세요.
  echo   권장: Qwen2.5-14B-Instruct Q4_K_M
  exit /b 1
)

echo [LocalLLM] model=%GGUF%
echo [LocalLLM] http://127.0.0.1:8080 parallel=%LLAMA_PARALLEL% ctx=%LLAMA_CTX%

start "NIN-LocalLLM" /BELOWNORMAL %SERVER% ^
  -m "%GGUF%" ^
  --host 127.0.0.1 --port 8080 ^
  -c %LLAMA_CTX% -ngl %LLAMA_NGL% ^
  --threads %LLAMA_THREADS% ^
  --parallel %LLAMA_PARALLEL%

echo [LocalLLM] 서버 프로세스 시작됨. warmup_local_llm.bat 으로 ping 하세요.
exit /b 0
