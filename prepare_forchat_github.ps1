# ForChat -> GitHub용 사본 생성 (민감·대용량 제외)
param(
    [string]$Source = ""
)

$dst = Join-Path $PSScriptRoot "ForChat"

if ([string]::IsNullOrWhiteSpace($Source)) {
    $defaultSource = Join-Path $PSScriptRoot "..\NIN_AI_3DModel_Ver2\Assets\AI\ForChat"
    if (Test-Path $defaultSource) {
        $Source = (Resolve-Path $defaultSource).Path
    }
    else {
        throw "Source not found. Usage: .\prepare_forchat_github.ps1 -Source '<path-to>/Assets/AI/ForChat' (output: .\ForChat\)"
    }
}
elseif (-not (Test-Path $Source)) {
    throw "Source path not found: $Source"
}

if (Test-Path $dst) {
    Remove-Item $dst -Recurse -Force
}
New-Item -ItemType Directory -Path $dst -Force | Out-Null

$excludeDirs = @(".venv", "chroma_db", "llama_bin", "TTS_Output", "TTSoutput", "Obj", "__pycache__", "Plugins")
$excludeFileNames = @("YYDate.Json", "session_history.json", "ckey.txt", "key.txt", "yt key.txt")

function Copy-Filtered {
    param([string]$From, [string]$To)
    New-Item -ItemType Directory -Path $To -Force | Out-Null
    Get-ChildItem -LiteralPath $From -Force | ForEach-Object {
        $item = $_
        if ($excludeDirs -contains $item.Name) { return }
        if ($excludeFileNames -contains $item.Name) { return }
        if ($item.Name -match "\.(meta|wav|gguf|TMP|pdd~)$") { return }
        if ($item.Name -match "^CallGPT\.cs~") { return }
        $target = Join-Path $To $item.Name
        if ($item.PSIsContainer) {
            Copy-Filtered -From $item.FullName -To $target
        }
        else {
            Copy-Item -LiteralPath $item.FullName -Destination $target -Force
        }
    }
}

Copy-Filtered -From $Source -To $dst

Set-Content -Path (Join-Path $dst "ckey.txt.example") -Value "PUT_OUR_ANTHROPIC_API_KEY" -Encoding UTF8 -NoNewline
Set-Content -Path (Join-Path $dst "key.txt.example") -Value "PUT_OUR_OPENAI_API_KEY" -Encoding UTF8 -NoNewline
Set-Content -Path (Join-Path $dst "yt key.txt.example") -Value "PUT_OUR_YOUTUBE_DATA_API_KEY" -Encoding UTF8 -NoNewline

$gitignore = @"
# --- Secrets: copy .example -> local file, replace PUT_OUR_*; never commit real keys ---
ckey.txt
key.txt
yt key.txt

# --- Personal dialogue / session ---
DateServer/YYDate.Json
DateServer/session_history.json

# --- OpenCV native DLLs (exceed GitHub 25MB web limit; README.txt is committed) ---
Fcam/Plugins/*.dll

Models/*.gguf
llama_bin/
TTS_Server/.venv/
DateServer/.venv/
DateServer/chroma_db/
TTS_Output/
TTSoutput/
*.wav
*.meta
.DS_Store
Thumbs.db
"@
Set-Content -Path (Join-Path $dst ".gitignore") -Value $gitignore -Encoding UTF8

$pluginsReadme = @"
OpenCVSharp4 runtime DLLs (not in repo — over GitHub 25MB limit)

See Fcam/PhoneCameraStream.cs header or install from:
  https://github.com/shimat/opencvsharp/releases
  NuGet: OpenCvSharp4 + OpenCvSharp4.runtime.win.x64
"@
$pluginsDir = Join-Path $dst "Fcam\Plugins"
New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null
Set-Content -Path (Join-Path $pluginsDir "README.txt") -Value $pluginsReadme -Encoding UTF8

Write-Host "[OK] Source:" $Source
Write-Host "[OK] output: .\ForChat\" -NoNewline; Write-Host $dst
Write-Host "[OK] files:" (Get-ChildItem $dst -Recurse -File | Measure-Object).Count
