# Third-party assets and attribution

Policy: only permissively licensed assets (CC0, CC-BY) are used. Every asset gets a row here
(name, source, licence, author, modifications) before it is used. Assets are **not committed**:
`Bovie Sim > Set Up Project` downloads them into `Assets/BovieSim/Generated/Downloads` (git-ignored)
and writes the exact items it fetched to `Assets/BovieSim/Generated/DOWNLOADED_ASSETS.md`.
CC-BY assets additionally need the credit shown in the app's About text before release.

| Use | Asset | Source | Licence | Author | Modifications |
|---|---|---|---|---|---|
| Sky and reflections | First available of `hospital_room`, `hospital_room_2`, `surgery`, `studio_small_08`, `studio_small_03` (2k HDR) | https://polyhaven.com/hdris | CC0 1.0 | Poly Haven contributors (per asset page) | Imported as cubemap; exposure set to ~EV 9.5 so the room is dim beside the operating light |
| Drape fabric | First ambientCG material matching "fabric nonwoven", "fabric felt" or "fabric", else `Fabric030`, `Fabric026`, `Fabric004` (1K JPG: Color, NormalGL, Roughness) | https://ambientcg.com | CC0 1.0 | Lennart Demes / ambientCG | Tinted surgical blue when grey; reused lighter on the reinforcement pad; triplanar, 30 mm tile |
| Instrument steel | First ambientCG material matching "brushed metal" or "brushed steel", else `Metal007`, `Metal006`, `Metal032` | https://ambientcg.com | CC0 1.0 | Lennart Demes / ambientCG | Normalised to stainless grey, satin smoothness; triplanar, 12 mm tile |
| Pencil plastic | First ambientCG material matching "plastic", else `Plastic010`, `Plastic006` | https://ambientcg.com | CC0 1.0 | Lennart Demes / ambientCG | Tinted off-white; triplanar, 20 mm tile |
| Instrument pack | FBX + textures in `Assets/BovieSim/Models` (purchased, e.g. CGTrader "Surgical Instruments") | marketplace | royalty-free per its licence; not redistributed | pack author | tweezers split into forceps arms; scissors, haemostat, scalpel laid on the drape; scratch map as mask / normal |
| Your own maps | `Generated/Downloads/<Slot>_Color.jpg` (+ `_NormalGL`, `_Roughness`) for Drape, Pad, Steel, Plastic | you | your responsibility | you | Used in place of the downloads; log the source here before release |

## Made in this project (no third-party content)
- Tissue textures (skin, dermis, fat, lipoma, fascia, muscle, vessels, and their coagulated,
  desiccated and carbonized states): baked procedurally by `Editor/TissueTextures.cs` from
  `Core/Noise.cs`. No suitable CC0 scans of surgical tissue exist.
- Instrument meshes (Bovie pencil and electrodes, Adson forceps, Frazier suction, skin hook), the folded
  fenestrated drape and the lamp domes: `Runtime/Rendering/MeshFactory.cs`.
- Fallbacks when downloads fail: gradient sky and a procedural plain-weave drape texture.
- Shaders, compute kernels, materials and scenarios: this repository.

Rendering uses Unity HDRP (Unity Companion License), installed from the Unity registry.
