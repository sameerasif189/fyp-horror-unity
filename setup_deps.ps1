# Sets up what this Unity project needs from outside its own repo, so a fresh clone plays out of the box:
#   1. fyp-iteration-2 in "fyp iteration 2": the MusicGen bridge and the curated audio the game plays
#      (assets/audio/0-5 per stress level, assets/entity_audio for the monsters)
#   2. the MusicGen weights (facebook/musicgen-small, ~2.3 GB) in "fyp iteration 2\models\musicgen-small"
# Safe to re-run: it pulls instead of cloning and skips weights that are already there.
#   .\setup_deps.ps1                        everything
#   .\setup_deps.ps1 -SkipMusicGenWeights   no 2.3 GB download (the game still plays the curated clips)
param([switch]$SkipMusicGenWeights)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $root "fyp iteration 2"
$repo = "https://github.com/sameerasif189/fyp-iteration-2.git"

# ---------------------------------------------------------------- 1. iteration-2 repo + curated audio
if (Test-Path (Join-Path $dest ".git")) {
    # The curated audio is tracked since 26 Sep 2026. An older clone has untracked copies of these folders, including
    # clips the curation removed: git would refuse to pull over them, and the game plays every clip it finds in a
    # level folder. Move them aside (kept, not deleted) so the pull brings exactly the curated set.
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    foreach ($sub in @("assets/audio", "assets/entity_audio")) {
        $path = Join-Path $dest $sub
        $tracked = git -C $dest ls-files -- $sub
        if ((Test-Path $path) -and -not $tracked) {
            $backup = Join-Path $dest ("assets\_local_backup_" + $stamp)
            New-Item -ItemType Directory -Force $backup | Out-Null
            Move-Item $path (Join-Path $backup (Split-Path $sub -Leaf))
            Write-Host "Moved your old untracked $sub to $backup"
        }
    }
    Write-Host "fyp iteration 2 already present - pulling..."
    git -C $dest pull --ff-only
    if ($LASTEXITCODE -ne 0) { Write-Error "git pull failed in '$dest' - commit or stash local changes there, then re-run." }
} elseif (Test-Path $dest) {
    Write-Host "Folder exists without .git. Move it aside or clone manually into: $dest"
    exit 1
} else {
    Write-Host "Cloning $repo -> $dest"
    git clone $repo $dest
    if ($LASTEXITCODE -ne 0) { Write-Error "git clone failed." }
}

# ---------------------------------------------------------------- 2. MusicGen weights
$weights = Join-Path $dest "models\musicgen-small"
if (Test-Path (Join-Path $weights "model.safetensors")) {
    Write-Host "MusicGen weights already present."
} elseif ($SkipMusicGenWeights) {
    Write-Host "Skipped the MusicGen weights (-SkipMusicGenWeights)."
} else {
    # Same lookup order as MusicGenPython.cs, so the weights land where the Python the game uses can load them.
    $candidates = @(
        $env:MUSICGEN_PYTHON,
        (Join-Path $env:USERPROFILE "miniconda3\python.exe"),
        "C:\Software\miniconda\python.exe",
        "C:\ProgramData\miniconda3\python.exe"
    )
    $py = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $py) {
        $cmd = Get-Command python -ErrorAction SilentlyContinue
        if ($cmd) { $py = $cmd.Source }
    }
    if (-not $py) {
        Write-Warning "No Python found (set MUSICGEN_PYTHON to its python.exe). The game runs without MusicGen; re-run this script later for the weights."
    } else {
        Write-Host "Downloading facebook/musicgen-small (~2.3 GB) with $py ..."
        & $py -c "from huggingface_hub import snapshot_download; snapshot_download('facebook/musicgen-small', local_dir=r'$weights')"
        if ($LASTEXITCODE -ne 0) {
            Write-Warning ("Download failed. Install the Python deps first (see below), then re-run this script, or: " +
                           "hf download facebook/musicgen-small --local-dir `"$weights`"")
        }
    }
}

Write-Host ""
Write-Host "Done. Open this folder in Unity Hub (Unity 6000.5.7f1), open Assets/Scenes/SampleScene.unity and press Play."
Write-Host "MusicGen needs a CUDA build of PyTorch in that Python (the editor starts the bridge itself):"
Write-Host ("  cd `"{0}`"" -f $dest)
Write-Host "  pip install -r requirements.txt   # then install the CUDA torch wheel matching your GPU driver"
