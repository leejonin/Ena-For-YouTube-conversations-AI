# 로컬 Supertonic 3 TTS 설치 — venv + 모델 자동 다운로드(첫 합성 시 HF 캐시)
# 실행: powershell -ExecutionPolicy Bypass -File setup_local_tts.ps1

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$TtsServerDir = Join-Path $Root "TTS_Server"
$VenvDir = Join-Path $TtsServerDir ".venv"

if (-not (Test-Path (Join-Path $VenvDir "Scripts\python.exe"))) {
    Write-Host "[LocalTTS] creating venv..."
    python -m venv $VenvDir
}

$Py = Join-Path $VenvDir "Scripts\python.exe"
$Pip = Join-Path $VenvDir "Scripts\pip.exe"

Write-Host "[LocalTTS] installing Supertonic 3 requirements..."
& $Pip install --upgrade pip | Out-Null
& $Pip install -r (Join-Path $TtsServerDir "requirements.txt")

Write-Host "[LocalTTS] downloading Supertonic 3 model (first run, ~400MB)..."
& $Py -c @"
from supertonic import TTS
tts = TTS(auto_download=True, intra_op_num_threads=2, inter_op_num_threads=1)
style = tts.get_voice_style('F2')
wav, _ = tts.synthesize('안녕하세요', voice_style=style, lang='ko', speed=1.04, total_steps=8)
print('[LocalTTS] model OK sample_rate=', tts.sample_rate)
"@

Write-Host "[LocalTTS] OK. 다음: start_local_tts.bat -> warmup_local_tts.bat"
