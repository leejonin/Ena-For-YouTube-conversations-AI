Supertonic 3 TTS (자동 캐시)
============================

엔진: Supertonic 3 (CPU ONNX, VRAM 0)
음색: local_tts_config.json 의 voice (기본 F3, 여성 F1~F5 / 남성 M1~M5)
언어: lang=ko (한국어 전용)

모델 위치:
  첫 setup/합성 시 Hugging Face 캐시에 자동 다운로드 (~400MB)
  Windows 예: %USERPROFILE%\.cache\supertonic3

설치:
  powershell -ExecutionPolicy Bypass -File setup_local_tts.ps1

기동:
  start_local_tts.bat → warmup_local_tts.bat

OBS + 이나 동시 가동 (Victus 16 권장):
  - Supertonic CPU 전용 (VRAM 0)
  - onnxThreads: 2 (local_tts_config.json)
  - 프로세스 우선순위 BELOWNORMAL
  - RAM 28GB / VRAM 7GB 목표

출처:
  https://huggingface.co/Supertone/supertonic-3
