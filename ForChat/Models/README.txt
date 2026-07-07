로컬 대화 LM — GGUF 모델 배치 폴더

0. llama-server (이미 winget install llama.cpp 완료 시 생략)
   winget install llama.cpp
   설치 후 PowerShell/터미널을 한 번 닫았다가 다시 여세요.

1. 이 폴더에 *.gguf 파일을 1개만 넣으세요.
   (7B 등 이전 모델 파일은 제거 — start_local_llm.bat 이 첫 *.gguf 를 사용)

2. 현재 권장 모델: Qwen2.5-14B-Instruct Q4_K_M (~8.5GB)
   https://huggingface.co/Qwen/Qwen2.5-14B-Instruct-GGUF
   파일명 예: qwen2.5-14b-instruct-q4_k_m.gguf

3. SLA 미달·VRAM 부족 시 대안: Qwen2.5-7B-Instruct Q4_K_M (~4.4GB)
   https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF

4. 기동 순서
   start_local_llm.bat
   warmup_local_llm.bat
   start_dateserver.bat
   Unity Play
