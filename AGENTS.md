# AGENTS.md — setting up and working on AdaPsych

Instructions for a coding agent (or a person) picking this project up on a new machine. It describes the project
as of 26 Sep 2026 (`main` of this repo, `main` of `fyp-iteration-2`). Read it top to bottom before changing
anything.

## 1. What you are setting up

AdaPsych is a Unity 6 (URP) first-person horror game. A stress level 0–5 (keys 0–5 in the editor) drives the
house textures, the monsters, the audio and a webcam "facecam". It spans **two repos**:

| Repo | Where it goes | What it holds |
|------|---------------|---------------|
| [`fyp-horror-unity`](https://github.com/sameerasif189/fyp-horror-unity) (this one) | any folder, e.g. `D:\University\unity` | The Unity project: scripts, scene, monsters, textures, ONNX models, facecam models |
| [`fyp-iteration-2`](https://github.com/sameerasif189/fyp-iteration-2) | **`fyp iteration 2\`** inside this repo (the space matters; this repo git-ignores it) | The MusicGen HTTP bridge, the Python generators, and the **curated audio the game plays**: `assets/audio/0`–`5` (per stress level) and `assets/entity_audio` (monsters) |

Not in either repo: the MusicGen weights (`facebook/musicgen-small`, ~2.3 GB), which `setup_deps.ps1` downloads.

## 2. Setup, step by step

Windows, PowerShell, from the root of this repo.

1. **Clone** (skip if you already have it; if you do, `git pull` on `main`):
   ```powershell
   git clone https://github.com/sameerasif189/fyp-horror-unity.git
   cd fyp-horror-unity
   ```
2. **Run the setup script.** It clones or updates `fyp iteration 2`, then downloads the MusicGen weights into
   `fyp iteration 2\models\musicgen-small`:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\setup_deps.ps1
   # or, without the 2.3 GB download (the game still plays the curated clips):
   powershell -ExecutionPolicy Bypass -File .\setup_deps.ps1 -SkipMusicGenWeights
   ```
   If `fyp iteration 2` is an **older clone** from before the audio was tracked, the script moves its untracked
   `assets\audio` and `assets\entity_audio` to `assets\_local_backup_<date>` before pulling. Otherwise git would
   refuse to pull, and clips that were curated out would keep playing (the game plays every clip in a level
   folder). Nothing is deleted.
3. **Python for MusicGen** (optional; the game runs without it). You need Python 3.10+ with a **CUDA** build of
   PyTorch, `transformers` and `huggingface_hub`:
   ```powershell
   cd "fyp iteration 2"
   pip install -r requirements.txt
   # then install the CUDA torch wheel that matches the GPU driver (see pytorch.org)
   ```
   The game finds Python in this order: the `MUSICGEN_PYTHON` environment variable (full path to `python.exe`),
   `%USERPROFILE%\miniconda3\python.exe`, `C:\Software\miniconda\python.exe`, `C:\ProgramData\miniconda3\python.exe`
   (`Assets/Scripts/MusicGenPython.cs`). Set `MUSICGEN_PYTHON` if yours is elsewhere.
4. **Open in Unity Hub** with **Unity 6000.5.7f1**: Open → this repo's root folder. The first import takes several
   minutes. Then open `Assets/Scenes/SampleScene.unity`.
5. **Press Play.** The start menu offers *Load Audio* (preloads MusicGen) and *Start Game*. In game: WASD, mouse,
   Shift sprint, Space jump, Esc unlock cursor, **0–5** set the stress level.

## 3. Check that it worked

- `git -C "fyp iteration 2" log --oneline -1` shows `860408a` (or later).
- Clip counts per level folder in `fyp iteration 2\assets\audio\0..5`: **29, 21, 32, 29, 35, 45**;
  `assets\entity_audio`: **100**. Anything extra is not part of the curated set.
- `fyp iteration 2\models\musicgen-small\model.safetensors` exists (~2.36 GB), unless you skipped it.
- In Play, the Console has no errors and shows lines like `[TextureCorruption] level=…`,
  `[WallDreadDecals] built … decals`, `[Stress] level=…`. Press 5: the house turns dark red with cracks and blood.
  Press 4, then 5 again: the cracks are in new places.
- MusicGen: the editor starts the bridge itself (`Assets/Scripts/Editor/MusicGenEditorBootstrap.cs`). It is ready
  about 60 s later; its log is `Logs/musicgen_bridge.log`. Health check: http://127.0.0.1:8765/health should show
  `"device":"cuda"`. To run it by hand: `cd "fyp iteration 2"` then
  `python musicgen_unity_bridge.py --port 8765 --require-cuda`.

## 4. Where things live

| Area | Scripts (`Assets/Scripts/`) | Data |
|------|-----------------------------|------|
| Stress | `StressController` (keys 0–5), `FusionDirector` | `Assets/FYP/Models/fusion_generator.onnx` |
| House + scene | `Editor/HauntedHouseBuilder` builds the whole house, the monster wiring and the NavMesh into `SampleScene` | `Assets/FYP/Materials/HauntedHouse` |
| Monsters | `MonsterAgent`, `MonsterDirector`, `EntityVisibilityController`, `MonsterVoice` | `Assets/FYP/Entities/<Name>/` (7 monsters) |
| Textures, stress 1–4 | `TextureCorruptionRunner`, `SurfaceTextureGenerator` (iteration-2 U-Net pipeline), `SurfaceTextureDeck`, `HorrorTextureCorruptor`, `CorruptionVariantSelector`, `HouseSurfaceGeometry` | `Assets/Resources/TextureDeck`, `Assets/FYP/Models/texture_generator.onnx` |
| Stress 5 "dread" look | `HouseDreadLook`, `WallDreadDecals` (new crack/blood set each visit), `HouseLighting`, `MonsterCorruptionController`, `MonsterDreadPainter` | `Assets/Resources/Dread` |
| Audio | `AudioGenerationRunner`, `ProceduralAssetBank` (curated level clips), `EntityAudioBank` / `EntityAudioDirector` / `EntityAudioSynth`, `MusicGenPython`, `Editor/MusicGenEditorBootstrap` | `fyp iteration 2/assets/audio`, `fyp iteration 2/assets/entity_audio` |
| Facecam | `WebcamSource`, `FeedbackCam`, `FeedVision`, `FeedAttention`, `FeedScene` | `Assets/Resources/FeedbackCam` (shaders, BlazeFace + selfie-segmentation ONNX) |
| Menu / HUD | `GameStartMenu`, `GameplayHud`, `TextureGenerationHud` | |
| Branding | | `docs/branding` (icon SVG = source; banner), `Assets/FYP/Branding/AdaPsych_Icon.png` (player icon) |

## 5. Rules and traps (learned the hard way)

- **Never open someone's webcam from an automated test.** The webcam opens only after *Start Game*. Before entering
  Play from a script, set `SessionState.SetBool("FYP.NoWebcamForTests", true)` (editor only;
  `FeedbackCam.WebcamBlockedForTests`), and erase it afterwards. Nothing reads webcam frames back, stores them or
  sends them anywhere; keep it that way.
- **Don't edit `.cs` files while the editor is in Play.** The compile is deferred and the domain reload drops
  editor-side state.
- **Unity-MCP** (`com.ivanmurzak.unity.mcp`, "AI Game Developer") lets an agent drive the editor. Its connection
  config is in `UserSettings/AI-Game-Developer-Config.json`, which is git-ignored, so set it up on each machine (the
  original used `http://localhost:22607`). Tool calls can run **twice**, so write editor scripts that are safe to
  repeat. Only one agent should change the editor through MCP at a time.
- **`HauntedHouseBuilder` rewrites `SampleScene`.** A rebuild re-creates the house, re-wires the monsters, re-bakes
  the NavMesh and runs `VerifyMonsterRoutes` (it logs an error if any spawn can't reach any patrol point). Change
  house or monster specs in the builder, not by hand in the scene.
- **Monster grounding needs Read/Write** on a mesh whose bounds are misleading: `MonsterAgent` seats the model on
  its actual lowest skinned vertex. Tillagemon's FBX has Read/Write on for this; the others don't need it.
- **Curating audio happens in `fyp-iteration-2`, not here.** Add or remove files in
  `fyp iteration 2\assets\audio\<level>` or `assets\entity_audio`, then commit and push *that* repo. Every `.wav`,
  `.mp3` or `.ogg` in a level folder gets played; empty files are skipped.
- **Don't switch branches with Unity open** in this repo. Switching removes the other branch's files for a moment,
  and Unity may import the gap. Merge or pull on the current branch, or close Unity first.
- `Assets/_Recovery/` is Unity's crash backup. Never commit it.
- No Git LFS: keep every file under GitHub's 100 MB limit (the largest is Tillagemon's 31 MB FBX).

## 6. Known issues

- 78 lights overflow the shadow atlas (Unity logs a resolution warning); lighting performance is unfinished.
- A stress change can hitch the frame for 1–3 s; the texture work is ~35 ms of that, and the rest has not been
  profiled yet.
- Jumping straight from stress 5 to 0 can show one repaint at level-5 procedural corruption, because the fusion
  corruption value is still high from stress 5.
- The MusicGen weights are CC BY-NC 4.0 (non-commercial). The audio clips are Freesound recordings; the number at
  the start of each file name is its Freesound sound ID.
