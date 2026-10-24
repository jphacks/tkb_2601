# Restaurant priority A kit

Original geometry and flat-color materials authored for this project. No downloaded
mesh, photo, texture, font, paid API or external generation service is used.

| Folder | Deliverable |
|---|---|
| `Source/generate_restaurant.py` | Reproducible Blender 5.1.1 Python source; geometry in meters |
| `Source/mesh-manifest.json` | Per-model object, vertex and triangle counts and source bounds |
| `Source/generate_people.py` | Original three seated adult variants and standing apron/cap staff generator |
| `Source/people-manifest.json` | People topology, bounds and seat-height design anchors |
| `Source/generate_food.py` | Original soup, curry/rice, dry noodles and hamburg generator, with food-manifest.json |
| `Models/` | 21 FBX files, without embedded textures or animation |
| `Materials/` | 33 shared Unity Built-in Standard materials |
| `Textures/` | 2 original images generated from analytic grain and hand-coded menu graphics |
| `Prefabs/` | 21 independently placeable Unity prefabs; furniture/people floor pivots, tabletop detail support pivot |
| `Resources/RestaurantCatalog.asset` | Player-compatible direct references to every prefab |

Furniture: a rounded oak table with apron and tapered legs, slatted chairs with
stretchers and sage cushions, upholstered booth, stainless prep shelves, depressed
sink and curved faucet, oven/hob and tapered extractor hood, fluted pickup shelf,
open three-tier return trolley with casters, and hollow ceramic tableware. The room
shell keeps the existing room dimensions, wall positions and plank rhythm, adding
oak wainscoting. These are visual meshes without navigation/physics colliders.

The sink cabinet is a panel shell with an open top, so the recessed basin and drain
remain visible. Unity validation checks downward ray intersections with the actual
imported mesh and rejects the old closed-top shape in a regression probe.

The B finish adds planar wood-grain UVs and a menu/condiment/napkin prefab on each
table. `original_textures()` generates every texture pixel without reading any image
or font file. Existing A furniture geometry and layout are retained. The shared
Unity materials are the final presentation settings; FBX authoring colors are defaults.

The people phase adds four seated adults using three variants and one kitchen
employee. Their shared materials and geometry are original; no external character
art is used. They are static meshes without rigs or behavior. Run `generate_people.py` separately to regenerate
only the four character FBX files; the furniture generator is import-safe.

Regenerate with Unity closed:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.1\blender.exe' --factory-startup -b --python Assets/Art/Restaurant/Source/generate_restaurant.py
```

Then open the project in Unity and run **Guardian > Restaurant > Validate Layout**.

No `.blend` file is imported by Unity. FBX regeneration needs Blender; using the
checked-in FBX/prefabs needs only the pinned Unity Editor. Geometry is deterministic;
FBX export metadata and Unity serialization can differ between runs. The Unity
pipeline normalizes the Blender-to-Unity horizontal axis conversion inside each
prefab, preserves FBX unit transforms, and assigns explicit shared materials. Keep
the prefab wrapper scale at one. World placement is in `RestaurantVisuals.cs`.

## Provenance and licensing

- All files in this folder originate from the included source and the project-owned
  Unity generation code. The meshes do not derive from BOOTH Nekobot or reference
  video assets. No third-party license grant is claimed or required for these inputs.
- Blender and Unity are authoring tools; their installed software is not copied into
  the repository. Standard shaders are referenced from the user's Unity installation.
- See the repository [LICENSE](../../../LICENSE) for repository-level terms.
  This notice does not grant permission to redistribute third-party models.
- The supplied Gemini video was reviewed for design cues only. Neither that video
  nor extracted frames are included. Captured evidence uses project-owned robot
  placeholders. The local BOOTH model, textures, materials, ZIP and unitypackage remain
  excluded. See [public scope](../../../docs/PUBLIC_SCOPE.md). Third-party redistribution permission remains unconfirmed.

No Nekobot texture or mesh is used by this art kit.
