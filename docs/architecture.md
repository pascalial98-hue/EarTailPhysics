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
| Chain final writeback | `0x1BE7560` |
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

Scene contact runs immediately before native chain writeback. Node positions are world-oriented offsets relative to the skeleton translation in this client; queries use world coordinates. Downward rays supply floor points and normals. Sphere sweeps cover temporal movement and the links between nodes. Queries use the game's material/layer filters (`0x4000`, layer 1), and only geometry present in that collision scene can respond.

Root b and proximal c are excluded from contact projection. Four bounded projection passes alternate length constraints and contact correction for d/e/the virtual tip. Contact removes inward velocity without adding restitution. Small residual length errors can remain when a fixed proximal chain and a surface conflict; native rotational writeback cannot guarantee every visible fur card stays above ground. Contact radius is manual and tapers toward the tip.

The plugin exposes query/contact counters. Collision switches default off to permit an initial movement/animation comparison. No claim of universal furniture, clothing mesh, other-character, or fur silhouette collision is made.

## Lifecycle and limitations

- Changing settings rebuilds the base physics at the next job-creation boundary, which resets transient simulation state.
- Native game scheduling controls the timestep; this release does not replace or globally alter the physics clock.
- Ownership is checked from live callback objects, not by dereferencing a cached actor pointer in worker callbacks.
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
