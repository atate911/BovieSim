# Third-party assets and attribution

Policy: only permissively licensed assets (CC0, CC-BY) are used. Every asset gets a row here
(name, source, licence, author, modifications) before it is used. Assets are **not committed**:
`Bovie Sim > Set Up Project` downloads them into `Assets/BovieSim/Generated/Downloads` (git-ignored)
and writes the exact items it fetched to `Assets/BovieSim/Generated/DOWNLOADED_ASSETS.md`.
CC-BY assets additionally need the credit shown in the app's About text before release.

| Use | Asset | Source | Licence | Author | Modifications |
|---|---|---|---|---|---|
| Sky and reflections | First available of `hospital_room`, `hospital_room_2`, `surgery`, `studio_small_08`, `studio_small_03` (2k HDR) | https://polyhaven.com/hdris | CC0 1.0 | Poly Haven contributors (per asset page) | Imported as cubemap; exposure set to ~EV 9.5 so the room is dim beside the operating light |
| Drape fabric | First available of `Fabric030`, `Fabric026`, `Fabric004` (1K JPG: Color, NormalGL) | https://ambientcg.com | CC0 1.0 | Lennart Demes / ambientCG | Tinted surgical blue in the drape material; its normal map reused on the reinforcement pad; triplanar mapping, 30 mm tile |

## Made in this project (no third-party content)
- Tissue textures (skin, dermis, fat, lipoma, fascia, muscle, vessels, and their coagulated,
  desiccated and carbonized states): baked procedurally by `Editor/TissueTextures.cs` from
  `Core/Noise.cs`. No suitable CC0 scans of surgical tissue exist.
- Instrument meshes (Bovie pencil and electrodes, Adson forceps, Frazier suction, skin hook), the folded
  fenestrated drape and the lamp domes: `Runtime/Rendering/MeshFactory.cs`.
- Fallbacks when downloads fail: gradient sky and a procedural plain-weave drape texture.
- Shaders, compute kernels, materials and scenarios: this repository.

Rendering uses Unity HDRP (Unity Companion License), installed from the Unity registry.
