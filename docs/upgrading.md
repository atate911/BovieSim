# Upgrading Bovie Sim

How updates reach a PC, how to go back, and the rules developers follow so an update never breaks an
existing install.

## For users

### Update an editor install

1. Close Unity.
2. In `E:\claude\bovie-sim` run `maintain update` (or double-click `maintain.cmd` → *Update*). It fetches
   from GitHub and, before changing anything, shows:
   - the new commits and the changelog entries since your version;
   - whether **setup** must be re-run (the setup version changes), and whether a **different Unity
     editor** is needed (with a `unityhub://` link that opens Hub's install dialog for it);
   - which of your locally edited files the update changes. Those are copied to
     `%LOCALAPPDATA%\BovieSim\backups\<time>-update` and then replaced; edits the update does not touch
     stay as they are. Files Unity writes itself (`Packages/manifest.json` after the packages install,
     `ProjectSettings/*`, `.meta` files) count as expected, not as edits.

   Enter updates (`--dry-run` only shows, `--yes` skips the question). It refuses while Unity has the
   project open, on a branch other than `main`, or when `main` has local commits GitHub does not have.
3. Do what it lists under *Next*: usually `maintain open`, and `maintain setup` (or *Set up* when Unity
   asks) when setup changed.
4. Open `Assets/BovieSim/Generated/Main.unity` and press Play.

`git pull --ff-only` with Unity closed does the same without the preview and checks.

Your settings (`settings.json`, see below) carry over. Anything under `Assets/BovieSim/Generated` is
rebuilt; do not keep your own files there.

### A ZIP download

A ZIP copy has no git history and cannot be updated in place. Double-click `install.cmd` in it: it renames
the folder to `<name>-zip-<date>`, clones in its place, and moves Unity's `Library` and copies the files
Unity made (`.meta`, project settings, installed packages, setup output) so nothing re-imports.

### Update a built app

`maintain build` (Unity closed) builds into `Builds\BovieSim-<version>`, running setup first if it is
out of date; or *File > Build Profiles > Build* in the editor. Copy the whole folder to the other PCs.
Settings live outside the app folder, so they survive.

### Go back to an earlier version

`maintain rollback list` shows every version and the commit used for it (its `vX.Y.Z` tag where one
exists). `maintain rollback 0.3.0` switches to it (a detached checkout; local files in the way are backed
up as for an update), `maintain rollback previous` undoes the last update or rollback (from
`%LOCALAPPDATA%\BovieSim\history.txt`), and `maintain rollback latest` or `maintain update` returns to
`main`. Run `maintain setup` after switching: generated assets differ between versions, and an older
version cannot tell that its assets are newer.

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
- **CI must pass** before tagging (browser tests, shader compile, C# compile check, EditMode tests, and the
  maintenance scripts under Windows PowerShell 5.1 and PowerShell 7).
- **Tag releases on GitHub** (*Releases → Draft a new release*, tag `vX.Y.Z` on `main`): `maintain rollback`
  fetches tags and prefers them. Versions before 0.4.0 are listed in `scripts/maintain.ps1`
  (`$EarlyVersions`); later ones are found from `BovieVersion.cs` history when untagged.
- **Batch entry points** (`maintain setup/test/build`) are `BovieSim.Bootstrap.BatchEntry.*`, which compile
  without HDRP and forward to `BovieSim.EditorTools.BatchCommands`. Keep `ProjectSetup.RunSetup(false)`
  free of dialogs.
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
