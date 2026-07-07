@echo off
setlocal EnableExtensions
REM DateServer — 로컬 LLM 연동 env 포함

set "ROOT=%~dp0"
set "LOCAL_LLM_BASE_URL=http://127.0.0.1:8080/v1"
set "LOCAL_LLM_API_KEY=local"
set "LOCAL_LLM_MODEL=qwen2.5-14b-instruct"

cd /d "%ROOT%DateServer"
if exist ".venv\Scripts\python.exe" (
  ".venv\Scripts\python.exe" -m uvicorn Sever:app --host 127.0.0.1 --port 5000
) else (
  python -m uvicorn Sever:app --host 127.0.0.1 --port 5000
)
