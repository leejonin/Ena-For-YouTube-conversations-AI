# 로컬 LLM 설치 — llama-server 바이너리만 자동 설치 (GGUF 모델은 사용자가 직접 배치)
# 실행: powershell -ExecutionPolicy Bypass -File setup_local_llm.ps1
# 모델: .\Models\ 폴더에 *.gguf 파일을 넣으세요.
#   권장: Qwen2.5-7B-Instruct Q4_K_M
#   HF: https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF

param(
    [switch]$InstallServer = $true
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$BinDir = Join-Path $Root "llama_bin"
$ModelsDir = Join-Path $Root "Models"

New-Item -ItemType Directory -Force -Path $BinDir | Out-Null
New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null

function Resolve-LlamaServerExe {
    param([string]$SearchRoot)
    Get-ChildItem -Path $SearchRoot -Filter "llama-server.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
}

$ServerExe = Join-Path $BinDir "llama-server.exe"
if ($InstallServer -and -not (Test-Path $ServerExe)) {
    Write-Host "[LocalLLM] llama.cpp release 조회 중..."
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/ggerganov/llama.cpp/releases/latest" -UseBasicParsing
    $asset = $release.assets | Where-Object { $_.name -match "bin-win.*cuda" -or $_.name -match "bin-win.*x64" } | Select-Object -First 1
    if (-not $asset) {
        throw "llama.cpp Windows 바이너리를 찾지 못했습니다. 수동으로 llama-server.exe 를 llama_bin 에 넣으세요."
    }

    Write-Host "[LocalLLM] download:" $asset.name
    $ZipPath = Join-Path $env:TEMP ("llama-cpp-" + $release.tag_name + ".zip")
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $ZipPath -UseBasicParsing
    Expand-Archive -Path $ZipPath -DestinationPath $BinDir -Force

    $found = Resolve-LlamaServerExe -SearchRoot $BinDir
    if (-not $found) {
        throw "llama-server.exe 설치 실패"
    }

    if ($found.FullName -ne $ServerExe) {
        Copy-Item $found.FullName $ServerExe -Force
        $Bdir = Split-Path $found.FullName
        Get-ChildItem $Bdir -Filter "*.dll" | ForEach-Object { Copy-Item $_.FullName $BinDir -Force -ErrorAction SilentlyContinue }
    }

    Write-Host "[LocalLLM] llama-server OK:" $ServerExe
}
elseif (Test-Path $ServerExe) {
    Write-Host "[LocalLLM] llama-server 이미 있음:" $ServerExe
}

$gguf = Get-ChildItem -Path $ModelsDir -Filter "*.gguf" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($gguf) {
    Write-Host "[LocalLLM] model OK:" $gguf.FullName
} else {
    Write-Host "[LocalLLM] Models\*.gguf 없음 — 사용자가 직접 GGUF 파일을 넣어주세요."
    Write-Host "  권장: Qwen2.5-7B-Instruct-Q4_K_M.gguf"
    Write-Host "  경로: $ModelsDir"
}

Write-Host "[LocalLLM] 다음 순서: start_local_llm.bat -> warmup_local_llm.bat -> start_dateserver.bat -> Unity Play"
