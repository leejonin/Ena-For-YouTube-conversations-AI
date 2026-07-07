# 생방송 SLA 검증 체크리스트 (수동)

Write-Host "=== NIN Local LLM + TTS Load Test Checklist ==="
Write-Host "- Phase0: wsl --shutdown, Chrome/OBS/Unity/llama 잔존 종료"
Write-Host "- start_local_llm.bat -> warmup_local_llm.bat"
Write-Host "- start_local_tts.bat -> warmup_local_tts.bat"
Write-Host "- start_dateserver.bat (port 5000)"
Write-Host "- Unity Play: LocalLLM + LocalTTS warmup OK log"
Write-Host "- OBS stream/recording ON: 20 turns TTS start within 10 sec"
Write-Host "- SelfTalk 10 turns: context maintained"
Write-Host "- Persona: emoji+dialogue format"
Write-Host "- YYDate.Json + session_history.json after each turn"
Write-Host "- Memory search 5x, YouTube viewer 2x"
Write-Host "- RAM under 28GB (warn 24GB), available 4GB+, VRAM under 7GB"

$health = "http://127.0.0.1:8080/health"
try {
  $r = Invoke-WebRequest -Uri $health -UseBasicParsing -TimeoutSec 5
  Write-Host "LLM health:" $r.StatusCode
} catch {
  Write-Host "LLM health: FAIL (install model, run start_local_llm.bat)"
}

$tts = "http://127.0.0.1:8081/health"
try {
  $rTts = Invoke-WebRequest -Uri $tts -UseBasicParsing -TimeoutSec 5
  Write-Host "TTS health:" $rTts.StatusCode
} catch {
  Write-Host "TTS health: FAIL (run setup_local_tts.ps1, start_local_tts.bat)"
}

$ds = "http://127.0.0.1:5000/health"
try {
  $r2 = Invoke-WebRequest -Uri $ds -UseBasicParsing -TimeoutSec 5
  Write-Host "DateServer health:" $r2.StatusCode
} catch {
  Write-Host "DateServer health: FAIL (run start_dateserver.bat)"
}

$usedRamGb = $null
$availRamGb = $null
try {
  $os = Get-CimInstance Win32_OperatingSystem
  $totalRamGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 2)
  $availRamGb = [math]::Round($os.FreePhysicalMemory / 1MB, 2)
  $usedRamGb = [math]::Round($totalRamGb - $availRamGb, 2)
  Write-Host "RAM total:" $totalRamGb "GB | used (approx):" $usedRamGb "GB | available:" $availRamGb "GB"
  if ($usedRamGb -gt 24) {
    Write-Host "WARN: RAM used > 24GB — Phase0 정리 또는 live_resource_profile 축소 확인" -ForegroundColor Yellow
  }
  if ($availRamGb -lt 4) {
    Write-Host "WARN: RAM available < 4GB — Unity/OBS 기동 전 정리 권장" -ForegroundColor Yellow
  }
} catch {
  Write-Host "RAM check: skipped"
}

$watchNames = @("llama-server", "llama", "Unity", "python", "obs64", "vmmemWSL", "Cursor")
$procs = Get-Process -ErrorAction SilentlyContinue | Where-Object { $watchNames -contains $_.Name }
if ($procs) {
  Write-Host "--- Process RAM (Working Set) ---"
  foreach ($p in ($procs | Sort-Object WorkingSet64 -Descending)) {
    $mb = [math]::Round($p.WorkingSet64 / 1MB, 0)
    Write-Host ("  {0} (pid {1}): {2} MB" -f $p.Name, $p.Id, $mb)
  }
}

try {
  $nv = & nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits 2>$null
  if ($nv) {
    $vramMb = [int]($nv | Select-Object -First 1)
    Write-Host "VRAM used:" $vramMb "MB"
    if ($vramMb -gt 7168) {
      Write-Host "WARN: VRAM > 7GB — llama -ngl 축소 확인" -ForegroundColor Yellow
    }
  }
} catch {
  Write-Host "VRAM check: skipped (nvidia-smi)"
}
