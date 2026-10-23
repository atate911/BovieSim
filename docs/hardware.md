# Running on different hardware

Bovie Sim runs most of its work on the graphics card (field solve, heating, tissue damage, bleeding,
surface extraction). The detail it can afford depends mainly on the GPU, so it picks a **detail level**
per PC and checks the frame rate while running.

## Detail levels

| Level | Voxel | Field region (max) | Target | Typical card |
|---|---|---|---|---|
| Low | 0.35 mm | 48³ | 60 fps | integrated graphics, GTX 1050, under 3.5 GB |
| Medium | 0.25 mm | 64³ | 60 fps | GTX 1060 / RX 580, 4-6 GB |
| High | 0.20 mm | 80³ | 60 fps | RTX 3060-4060 / RX 6600-7600, 8-11 GB |
| Ultra | 0.16 mm | 96³ | 30 fps | RTX 4070+ / RX 7800 XT, RX 9070 XT, 12 GB+ |

Each level also sets solver iterations, how many surface bricks are rebuilt per frame, ambient occlusion,
shadow-map size, screen-space reflections (High and Ultra), the default ray-tracing tier and the replay length. Finer voxels show the thin thermal margins that matter
(a 0.5 mm skin-edge rim is 2 voxels at 0.25 mm, 3 at 0.16 mm) but cost roughly the cube of the refinement.

**Auto** (the default) chooses from dedicated video memory and whether the GPU is integrated
(`Runtime/App/HardwareProfile.cs`). Override it in **Settings (F9)**; the new level applies on restart (F5).
The inspector's *Voxel Override* on the `Bovie Sim` object still wins, for development.

## Ray tracing

With **DirectX 12** and a card that supports DirectX Raytracing (NVIDIA RTX 20-series or newer, AMD RX 6000 or
newer, Intel Arc), the lights, reflections and bounce light are ray traced. *Settings (F9) > Ray tracing*:

| Setting | Effects | Typical cost |
|---|---|---|
| Off | PCSS soft shadows, SSAO, screen-space reflections (High+) | none |
| Shadows (Auto at Medium) | ray-traced shadows from the 50 cm light domes, ray-traced ambient occlusion | small |
| Reflections (Auto at High) | + ray-traced reflections on steel instruments, wet tissue and blood | moderate |
| Full (Auto at Ultra) | + ray-traced global illumination (blue drape and red wound bounce) and subsurface scattering | large: RTX 4070 / RX 7800 XT class |

Set Up Project puts DirectX 12 first in the graphics API list (restart the editor once) and enables ray tracing on
the HDRP asset; if *Window > Rendering > HDRP Wizard > HDRP + DXR* lists problems, press *Fix All*. Without DXR
the setting shows "not available" and the raster effects are used. The tissue surface is rebuilt on the GPU, so
it is registered as dynamic geometry; its acceleration structure is rebuilt each frame, which is the main
ray-tracing cost at Ultra.

## When the PC cannot keep up

While running, Bovie Sim compares the frame time with the level's budget (16.7 ms for 60 fps, 33 ms for
30 fps at Ultra). If it stays more than 15 % over budget for about 4 seconds, a banner offers to restart at
the next level down. **F3** shows frame, CPU and GPU time.

## Minimum requirements

- Windows 10/11 64-bit (macOS/Metal should work but is untested).
- A DirectX 11 or 12 class GPU with compute shaders and asynchronous readback, 1.5 GB video memory or more.
  If these are missing, Bovie Sim shows the reason instead of starting.
- 8 GB RAM (16 GB for Ultra: the tissue grid is also kept in system memory).

## Common setups

- **Laptops with two GPUs** (integrated + discrete): Windows may run Unity or `BovieSim.exe` on the
  integrated GPU. In *Settings > System > Display > Graphics*, add the Unity editor and `BovieSim.exe`
  and choose **High performance**. F9 shows which GPU is in use.
- **AMD, NVIDIA and Intel Arc** are all supported; keep the driver current.
- **DirectX 11 vs 12:** both work (every compute kernel stays within DirectX 11's 8 writable buffers).
- **High-DPI and large monitors:** the interface scales with the screen; the 3D view renders at the
  display's native resolution.
- **High refresh-rate monitors:** frame pacing follows vsync; Bovie Sim keeps one frame queued to limit lag.
- **Remote desktop / virtual machines:** often lack compute support or a real GPU; run locally.

## Reporting performance

Send the F9 line (GPU, video memory, level) and the F3 readout during cutting, plus the scenario.
