# Runtime design and evidence boundaries

## Installation independence

The plugin references only Dalamud-provided assemblies. It does not call Penumbra, import a PMP, redirect resource paths, distribute PHYB files, or edit game assets on disk. Cosmetic mods remain separate because physics cannot replace their meshes, textures, or skin weights.

The first release controls only the local player's base physics resource, slot 0. Other physics resources and other players are outside this release's control scope. Additional chains require explicit validation rather than name-based guessing.

## Native solver and descriptor ownership

CN `2026.11.03.0000.0000`, executable SHA-256:

```text
52042b60631cf2c48851b3aaee36e9ec4fedd6d7f2c68f0e9a8c3befb0af9f98
```

Hooks and native calls are enabled only for that exact executable. Addresses below are RVAs for this binary, not portable offsets:

| Operation | RVA |
|---|---|
| BonePhysicsModule.CreateJobData | `0x18B96D0` |
| BonePhysicsModule.Load | `0x18B9800` |
| Chain pose construction | `0x1BE66E0` |
| Chain finalization | `0x1BE7560` |
| Resource acquire / release | `0x2FEA70` / `0x2FEA40` |
| Skeleton bone lookup | `0x25A5B0` |
| BG ray / sphere sweep | `0x5F1670` / `0x5F1790` |

The loader reads parsed simulator descriptors (104 bytes), parsed chain descriptors (56 bytes), and PHYB node records (84 bytes). The plugin creates private descriptor copies, modifies only the selected ear/tail records, and reuses the game's native constructors/solver. Missing standard cat-ear simulators are constructed from explicit parameters. There are no third-party asset blobs in the assembly.

Replacement occurs before the module enqueues the current frame's physics jobs. A real resource reference is acquired first; native Load releases the module's old reference and builds from a private, non-owning view. The view has a nonzero synthetic reference count so the native acquire path never resolves it through the resource cache. Immediately after Load returns, the module's resource handle is restored to the real handle, adopting the held reference. The fabricated handle never becomes a native destructor's owner. Private descriptor storage is kept until replacement/restoration.

This ownership scheme was checked against local loader/acquire/unload disassembly. **It has not yet been exercised in-game.** Count/name validation and executable gating reduce accidental misuse; they do not establish runtime safety or visual equivalence.

No original animation is replaced. Animation attraction, native wind processing, chain lengths, and bone writeback remain in the original solver. Tail motion parameters reproduce the local v0.2.3 / combined v1.1.1 tuning. Terminal attraction retains the race-specific lower native limit. Ear strength and tail strength are independent.

For a standard tail with terrain/scene contact enabled, the simulator group becomes 142, outside the animation controller's locally observed group-stop range. This follows the already tested Mod fix for seated/lying poses. It is an ordinary group number, not a special high-bit flag. The native flat-ground flag is cleared for this path, because plugin terrain queries replace it.

## Contacts

Body contacts use copies of each race's native leg anchors plus a pelvis sphere. Only d/e/the virtual tip receive body contact masks. All unrelated source physics remain intact. A pre-existing custom collision routing is rejected instead of overwritten.

Scene contact runs before native chain pose construction in each substep, after integration and native collisions. Version 0.1.0 projected particles only at finalization, after native node matrices were already constructed; 0.1.1 fixes this ordering. Local disassembly shows pose construction consuming particle positions and writing node matrix translations, including node b. Finalization keeps native rotation/blending and skips the final fixed-length projection only when contact changed the chain. These call sites and signatures were audited against the supported binary, but the detours have not been exercised in-game.

Node positions are world-oriented offsets relative to the skeleton translation; queries use world coordinates. Downward rays supply floor points and normals. Sphere sweeps cover temporal movement and the links between nodes. Queries use the game's material/layer filters (`0x4000`, layer 1), and only geometry present in that collision scene can respond.

All five particles (b/c/d/e/virtual tip) have separately configured 0–0.30 m radii. Root b and proximal c each have an independent protection flag, default on. A protected node's effective radius is zero and its particle is never written by this projector. Opting out permits contact displacement, including the root; this can separate the tail attachment or jitter and is experimental. Four bounded passes alternate length constraints and contact projection. Contact removes inward velocity without adding restitution. A zero-radius unprotected node can still move through chain-length constraints, but performs no direct contact queries. Fixed anchors and a surface can conflict, leaving residual length error; there is no guarantee that all visible fur stays outside surfaces.

Each incoming-link sphere sweep uses its receiving node's radius, not a varying-radius cone. The root has temporal sweeps but no incoming link. Body proxies retain their independent preset and middle/rear masks. No model-mesh fitting is performed.

The v1 single radius migrates to the same d/e/tip factors (1, .7, .45). Protected b/c retain configurable .55/.85 defaults for optional activation. Configuration snapshots deep-copy the array. One immutable settings snapshot is used across each frame's substeps and preview capture, avoiding mixed profiles.

Preview data is copied in the live owning chain's finalization callback into immutable managed snapshots. UI rendering only uses those snapshots and Dalamud WorldToScreen, never game-memory pointers. Node spheres use the effective solver radii; active scene capsules show the latest actual link query extents. When contacts are off, gray capsules show the configured potential profile; ground-only mode has spheres and no misleading active capsules. Temporal sweeps are not drawn. Snapshots expire after 0.5 seconds, and capture/render are enabled only while the configuration window is open and preview is selected. Wire rendering has no depth test. It shows collision-query geometry rather than the final blended mesh silhouette.

The plugin exposes query/contact counters. Collision switches default off to permit an initial movement/animation comparison. No claim of universal furniture, clothing mesh, other-character, or fur silhouette collision is made.

## Lifecycle and limitations

- Ear/tail presets and collision modes rebuild base physics at the next job-creation boundary. Changing node radii, root protection or preview does not rebuild or reset the simulation. Manual reapply forces a rebuild.
- Native game scheduling controls the timestep; this release does not replace or globally alter the physics clock.
- Ownership is checked from live callback objects against the local player's draw object and physics module. Other players and NPCs are never selected for descriptor replacement, contact correction or preview. Worker callbacks do not dereference a cached actor pointer.
- GPose pauses plugin control by default. Detailed posing-tool and Customize+ coexistence still needs runtime testing.
- Disabling/unloading rebuilds from the original source descriptor; redraw remains the fallback after an error.
- Client updates disable runtime hooks until the new executable is audited and released.
- Original/custom standard tail coverage has been checked through data comparison, not all-race game tests.

## Upstream references

- [FFXIVClientStructs BonePhysicsModule](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Physics/BonePhysicsModule.cs)
- [FFXIVClientStructs BGCollisionModule](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Common/Component/BGCollision/BGCollisionModule.cs)
- [FFXIVClientStructs ResourceHandle](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/System/Resource/Handle/ResourceHandle.cs)
- [VFXEditor PHYB node format](https://github.com/0ceal0t/Dalamud-VFXEditor/blob/49012104303caae373ac4b37b385a3affedca472/VFXEditor/Formats/PhybFormat/Simulator/Chain/PhybNode.cs)

The current upstream names for native fields `0x447` / `0x448` differ from the local executable's flag loader. This plugin follows the audited local mapping; an upstream name alone is not treated as proof of local behavior.
