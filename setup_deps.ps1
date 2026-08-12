# Clones fyp-iteration-2 into the folder Unity/AudioGenerationRunner expects.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $root "fyp iteration 2"
$repo = "https://github.com/sameerasif189/fyp-iteration-2.git"

if (Test-Path (Join-Path $dest ".git")) {
    Write-Host "fyp iteration 2 already present — pulling..."
    git -C $dest pull --ff-only
} elseif (Test-Path $dest) {
    Write-Host "Folder exists without .git. Move it aside or clone manually into: $dest"
    exit 1
} else {
    Write-Host "Cloning $repo -> $dest"
    git clone $repo $dest
}

Write-Host "Done. Open this project in Unity Hub, then optionally:"
Write-Host "  cd `"$dest`""
Write-Host "  pip install -r requirements.txt"
Write-Host "  python musicgen_unity_bridge.py --port 8765 --require-cuda"
