# Upgrading Bovie Sim

How updates reach a PC, how to go back, and the rules developers follow so an update never breaks an
existing install.

## For users

### Update an editor install (git)

1. Close Unity.
2. In `E:\claude\bovie-sim` run `git pull`.
3. Open the project from Unity Hub. If the update changed generated assets, Unity asks to
   **re-run setup**: choose *Set up*. (Or run *Bovie Sim > Set Up Project* yourself.)
4. Open `Assets/BovieSim/Generated/Main.unity` and press Play.

Your settings (`settings.json`, see below) carry over. Anything under `Assets/BovieSim/Generated` is
rebuilt; do not keep your own files there.

### Update a built app

Build again (*File > Build Profiles > Build*) into a new folder, or overwrite the old one, and copy the
whole folder to the other PCs. Settings live outside the app folder, so they survive.

### Go back to an earlier version

Every release is tagged. `git tag` lists them; `git checkout v0.3.0` switches to one, `git checkout main`
returns to the latest. Re-run setup after switching (generated assets differ between versions).

### Where settings are kept

`%USERPROFILE%\AppData\LocalLow\Bovie Sim\BovieSim\settings.json` (Unity's `persistentDataPath`; set
up by Set Up Project). Delete the file to return to defaults. It stores the detail level,
display and teaching options.

## For developers: what to bump

| Change | Bump | Where |
|---|---|---|
| Any release | version, changelog entry, git tag `vX.Y.Z` | `Core/BovieVersion.cs`, `CHANGELOG.md` |
| Generated assets change (materials, shaders the setup references, theme, scene, texture bake, vertex layout) | `SimAssets.CurrentSetupVersion` | `Runtime/Rendering/SimAssets.cs`; the editor then offers to re-run setup, and Play refuses to start until it has run |
| Meaning of a settings field changes, or a field is renamed | `UserSettings.CurrentVersion` and a step in `Migrate()` | `Core/UserSettings.cs`; new fields with defaults need no bump |
| Incompatible scenario format change | `Scenario.SupportedSchema`, and `schemaVersion` in the new files | `Core/Scenario.cs`, `scenarios/*.json`; older files keep loading, newer files give a clear "update Bovie Sim" message |
| Scenario or teaching text edits | none; run `npm run sync:unity` | CI fails if the Unity copies drift |

Rules:
- **CI must pass** before tagging (browser tests, shader compile, C# compile check, EditMode tests).
- **Never hand-write `.meta` files**; commit the ones Unity generates.
- **Keep migrations additive**: an old `settings.json` or scenario must load in the new version.
- Releases with *Setup required* say so in the changelog heading.

## Unity and render-pipeline upgrades

- **Unity version.** The project targets Unity 6.6 (`ProjectSettings/ProjectVersion.txt`). To move to a
  newer release (e.g. 6.7 LTS): open the project in the new editor on a branch, fix Console errors, run the
  EditMode tests, play each scenario, then update `ProjectVersion.txt`, the README and the manual, and
  bump `CurrentSetupVersion`. The asmdefs accept any HDRP 17 or newer.
- **HDRP is in maintenance mode.** It keeps working through Unity 6 but gets no new features. The physics,
  tissue model and UI do not depend on it; only the materials, lights, post-processing volume, camera
  settings and the particle and teaching-overlay shaders do (`Editor/ProjectSetup.cs`,
  `Runtime/App/SimulationRunner.cs` render setup, `Resources/BovieSim/Shaders/*.shader`). A move to URP
  would replace those and add a skin shader (URP has no built-in subsurface scattering).
- **Legacy Input Manager.** Mouse and keys use it; Unity 6.6 marks it for future removal. Moving them to
  the Input System (already used for the pen) touches `InputState`, `OrbitCamera`, the key handling in
  `SimulationRunner` and the panel's Shift check.
