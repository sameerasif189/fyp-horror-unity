# FYP Adaptive Horror — Unity Demo

Unity (URP) horror room demo driven by **stress level 0–5**. It uses iteration-2 fusion / texture ONNX models in-editor, and **MusicGen on CUDA** (via a local Python bridge) for ambience, with procedural audio as fallback.

## What’s in this repo

| Path | Purpose |
|------|---------|
| `Assets/Scripts/` | Stress, fusion, texture corruption, MusicGen audio runner, entities, FPS player |
| `Assets/FYP/` | Clean PBR textures, entity GLBs, Sentis ONNX models, ambient WAVs |
| `Packages/` / `ProjectSettings/` | URP + Input System + AI Inference (Sentis) + glTFast |
| `fyp iteration 2/` | Separate repo (clone — see below): MusicGen bridge + generators, and the curated audio the game plays (`assets/audio/0-5` per stress level, `assets/entity_audio` for the monsters) |

Large training dumps (`models/`, MusicGen weights, grain banks) are **not** committed. Download steps are below.

## Requirements

- **Unity 6000.5.x** (tested on `6000.5.7f1`) with Hub
- Windows + NVIDIA GPU recommended for MusicGen (CUDA)
- Python 3.10+ with PyTorch CUDA + `transformers` (miniconda works; see iteration-2 README)
- Git

## Import / open the project

1. Clone this repo:
   ```powershell
   git clone https://github.com/sameerasif189/fyp-horror-unity.git
   cd fyp-horror-unity
   ```
2. Run the setup script. It clones (or updates) fyp-iteration-2 into `fyp iteration 2`, which brings the curated
   audio, and downloads the MusicGen weights (~2.3 GB; add `-SkipMusicGenWeights` to skip them). Re-run it whenever
   the audio changes.
   ```powershell
   .\setup_deps.ps1
   ```
   An older `fyp iteration 2` clone keeps untracked copies of the audio, including clips that were curated out. The
   script moves those to `assets\_local_backup_<date>` on its first run, so only the curated set plays.
3. Open the folder in **Unity Hub → Open →** select this project root.
4. Let Unity import packages / assets (first open can take several minutes).
5. Open scene: `Assets/Scenes/SampleScene.unity` (or your active SampleScene).
6. Press Play. Use keys **0–5** to change stress.

### Optional: MusicGen (GPU ambience)

MusicGen weights are not in either repo (~2.3 GB). `setup_deps.ps1` downloads them into
`fyp iteration 2\models\musicgen-small`; by hand, from `fyp iteration 2`:

```powershell
hf download facebook/musicgen-small --local-dir "models/musicgen-small"
```

The editor starts the CUDA bridge by itself when it loads and before Play, replacing one stuck on the port (log:
`Logs/musicgen_bridge.log`; ready about 60 s after start). To run it by hand instead:

```powershell
cd "fyp iteration 2"
python musicgen_unity_bridge.py --port 8765 --require-cuda
```

Health check: [http://127.0.0.1:8765/health](http://127.0.0.1:8765/health) — expect `"device":"cuda"`.

Without the bridge, the game still plays the curated per-level clips.

### Python deps (iteration 2)

```powershell
cd "fyp iteration 2"
pip install -r requirements.txt
# CUDA torch must match your GPU driver; use the same env as gui_demo.py
```

## Controls

| Input | Action |
|-------|--------|
| WASD | Move |
| Mouse | Look |
| Shift | Sprint |
| Space | Jump |
| Esc | Unlock cursor |
| **0–5** | Stress: Calm → Terrified |

HUD shows stress + audio bridge / GPU / source (`procedural` then `musicgen`).

## Related repos

- Generators / training / MusicGen bridge: [fyp-iteration-2](https://github.com/sameerasif189/fyp-iteration-2)

## Notes for reviewers

- Entity meshes under `Assets/FYP/Entities` come from iteration-2 “new entities”.
- Texture + fusion use Sentis ONNX under `Assets/FYP/Models`.
- Audio quality path: **MusicGen (CUDA) → wait → crossfade**; procedural is the fallback while waiting or if the bridge is offline.
