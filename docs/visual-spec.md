# Visual specification

Source of truth for how each tissue and thermal state must look. Each row needs a reference image
in `docs/reference/` and is accepted only when the surgeon-user agrees it reads correctly at a glance.

| Item | Appearance | Shader hook | Reference | Accepted |
|---|---|---|---|---|
| Skin at rest, prepped | pink-tan, pores, Langer's lines, chlorhexidine orange sheen | `baseMaterial T_EPI` | – | ☐ |
| Dermis cut face | pink-white collagen, red speckle, wet | `T_DERMIS` | – | ☐ |
| Fat | saturated yellow lobules, glossy, white septal lines | `T_FAT` | – | ☐ |
| Scarpa's fascia | thin cream membrane, translucent | `T_SFASCIA` | – | ☐ |
| Deep fascia | white, glistening, fibrous sheen | `T_DFASCIA` | – | ☐ |
| Muscle | deep red, striated, wet | `T_MUSCLE` | – | ☐ |
| Artery / vein | bright red / blue-purple | `T_ARTERY`, `T_VEIN` | – | ☐ |
| Lipoma + capsule | paler, brighter lobules; thin shiny capsule | `T_LIPOMA`, `T_CAPSULE` | – | ☐ |
| Pure-cut thermal rim | ≤0.5 mm white line | `applyThermalState blanch` | – | ☐ |
| Coag blanching | opaque white-grey spreading | `blanch` | – | ☐ |
| Desiccation | dull tan, matte | `S_DESIC` | – | ☐ |
| Carbonization | black, glossy then matte | `S_CARBON` | – | ☐ |
| Boiling surface | glinting micro-bubbles | `tempC > 98` | – | ☐ |
| Smoke / steam | plume ∝ vaporized mass | (sprint 1) | – | ☐ |
| Arcs | blue-white filaments | (sprint 1) | – | ☐ |
| Arterial jet / venous welling | pulsatile vs steady | (sprint 3) | – | ☐ |
