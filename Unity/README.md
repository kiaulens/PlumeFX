# PlumeFX Unity project

Unity **2019.4.18f1** project (the Unity version of KSP 1.12) with:

- `Assets/Shaders/PlumeFXSmoke.shader`: the smoke shader. `Assets/Editor/BuildBundles.cs` packs it into
  `Build/plumefx.shaders` (`build.ps1 -Shaders` in the repository root does that and copies the bundle into
  `GameData/PlumeFX/Shaders`).
- `Assets/PlumeFX/Core/PlumeCore.cs`: the smoke logic. It has no KSP references and is compiled into the plugin
  together with `Source/PlumeFX.cs`.
- `Assets/PlumeFX/Preview/PlumePreviewRunner.cs`: an offline preview that launches a rocket in a simple scene (a
  planet body, a launch pad, a sun with shadows, wind) with the real core and shader, and saves stills and video
  frames. Started from `Assets/Editor/PlumePreviewMenu.cs`.

Open the project once in Unity 2019.4.18f1 (or run any batch command below) to create the `Library` folder.

## Preview

```bash
bash preview.sh                         # stills of all views at 0.5-20 s
bash preview.sh side,chase 40           # plus videos of two views, 40 s long (needs ffmpeg)
bash preview.sh "" 120 30,60,120        # stills at chosen times
```

Set `UNITY_EXE` if Unity is not in the default Hub path and `FFMPEG` if ffmpeg is not on the PATH. The preview runs
Unity in batch mode with the GPU (not `-nographics`). Output goes to `Preview/`: `shots/<view>_<t>s.png`, the contact
sheets `sheet_a.png` (0.5-5 s) and `sheet_b.png` (8-20 s), and `<view>.mp4`. `PLUMEFX_PREVIEW_DONE` in `preview.log`
reports the CPU time per frame.

Views: `side`, `chase` (like KSP's chase camera), `top`, `below`, `near` and `nozzle` (the jet close up), `padclose`,
`spectator` (130 m from the pad), `front` (300 m in front of the pad, across the trenches), `behind`, `behindhigh`,
`launch`, `wide` (450 m, the whole ground cloud), `trench` (onto a trench exit), `shadow` (straight down), `far`
(7 km, the whole trail), `orbit` (85 km up).

Environment variables for the scene:

| Variable | Meaning |
|----------|---------|
| `PFX_BOOST=4` | Four boosters around a core instead of one booster. |
| `PFX_TRENCH=1` | Launch pad modelled on the KSC: raised pad with flame trenches whose exits lie 11.5 m north and south. |
| `PFX_NOZD`, `PFX_THRUST` | Nozzle diameter (m) and thrust per booster (kN). |
| `PFX_ACC` | Thrust acceleration in m/s² (e.g. 11.5 for a slow launch). |
| `PFX_HOLD` | Seconds the rocket stays on the pad with engines running. |
| `PFX_BURN` | Burn time in s. |
| `PFX_SUNEL` | Sun elevation in degrees. |
| `PFX_SHOTVIEWS` | Render stills only for these views (faster). |
| `PFX_FLAMEBOOST`, `PFX_FIRE` | Override the flame settings for comparisons. |
| `PFX_DEBUG` | Shader debug modes: 1 = light without shadow, 2 = no pixel jitter, 3 = no entry search, 4 = noise source, 5 = element type. |
| `PFX_LOBES=10,30`, `PFX_CLOUDLOG=1` | Log the ground cloud parcels at these times, or cloud statistics every second. |
| `PFX_GPUWIN` | GPU timing window in s (log lines `PLUMEFX_GPU`). |

`framediff.py <view> [from] [to]` measures the frame-to-frame change of a rendered video, which finds jumps and
popping (spikes above 3x the median). `sheet.py [times] [views]` builds a contact sheet from the stills.

The final look still needs a check in game, where Scatterer, TUFX and Deferred change light and colours.
