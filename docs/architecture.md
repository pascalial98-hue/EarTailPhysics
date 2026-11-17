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
| Per-character environment wind update | `0x5FC130` |
| BonePhysicsModule.Load | `0x18B9800` |
| Chain pose construction | `0x1BE66E0` |
| Chain attraction / length constraints (idle target scope) | `0x1BE6030` |
| Chain finalization | `0x1BE7560` |
| Resource acquire / release | `0x2FEA70` / `0x2FEA40` |
| Skeleton bone lookup | `0x25A5B0` |
| BG ray / sphere sweep | `0x5F1670` / `0x5F1790` |

The loader reads parsed simulator descriptors (104 bytes), parsed chain descriptors (56 bytes), and PHYB node records (84 bytes). The plugin creates private descriptor copies, modifies only the selected ear/tail records, and reuses the game's native constructors/solver. Missing standard cat-ear simulators are constructed from explicit parameters. There are no third-party asset blobs in the assembly.

Replacement runs at the local character's native environment-wind callback, before its original wind computation. CreateJobData remains a fallback before enqueuing physics jobs. The wind callback has five arguments: controller, actor context, live draw object, float delta and byte flags. The draw argument must equal the captured local player's draw; the live module and skeleton owner must agree. Other characters pass directly to the original function. A real resource reference is acquired first; native Load releases the module's old reference and builds from a private, non-owning view. The view has a nonzero synthetic reference count so the native acquire path never resolves it through the resource cache. Immediately after Load returns, the module's resource handle is restored to the real handle, adopting the held reference. The fabricated handle never becomes a native destructor's owner. Private descriptor storage is kept until replacement/restoration.

This ownership scheme was checked against local loader/acquire/unload disassembly. Users subsequently confirmed successful takeover and wind in 0.1.2. Count/name validation and executable gating reduce accidental misuse; that limited feedback does not establish complete lifecycle safety or visual equivalence.

No original animation is replaced. Animation attraction, native wind processing, chain lengths, and bone writeback remain in the original solver. Tail motion parameters reproduce the local v0.2.3 / combined v1.1.1 tuning. Terminal attraction retains the race-specific lower native limit. Ear strength and tail strength are independent.

### 0.1.2 takeover and wind handoff

The reported 0.1.1 state was still waiting, with no preview and zero queries/contacts. That establishes that the successful replacement path had not been reached; it does not establish which native callback/ownership condition failed. The additional wind entry addresses dependence on one job callback, with counters exposing both routes. The user confirmed takeover, normal wind and query activity in 0.1.2; contacts remained zero and the preview was visibly displaced.

Local disassembly independently confirms that Load writes the descriptor's fixed wind into new simulators at `0x18BA216–0x18BA222`. The native per-character routine writes live environment wind at `0x5FC357–0x5FC361` and module wind settings at `0x5FC3E6–0x5FC3EF`, skips inactive simulators and includes group-dependent variation. Therefore late replacement loses that frame's input; this alone does not explain an indefinitely waiting plugin. Before replacement, the plugin snapshots slot-0 input at simulator `+0x40` and smoothed wind at `+0x430`, then restores finite values to matching indices/groups (including the explicit tail 14↔142 contact remap). No pointers, clocks, velocities, animation controls or global weather are copied. Appended ear simulators are left for the native wind update. The original wind callback always runs and continues to supply changing wind; no frozen substitute or synthetic oscillation is added.

Each owned boundary refreshes the current tail-chain binding instead of assuming a previously cached chain survives native rebuilds. First-simulator identity also participates in resource replacement detection when vector storage is reused. The settings expose entry, application, tail-update and contact counters plus the last contact prerequisite. UI diagnostics consume managed values, not native pointers. User confirmation covers takeover, wind and queries; it does not cover all races, lifecycle transitions or successful contacts.

For a standard tail with terrain/scene contact enabled, the simulator group becomes 142, outside the animation controller's locally observed group-stop range. This follows the already tested Mod fix for seated/lying poses. It is an ordinary group number, not a special high-bit flag. The native flat-ground flag is cleared for this path, because plugin terrain queries replace it.

## Contacts

Body contacts use copies of each race's native leg anchors plus a pelvis sphere. Only d/e/the virtual tip receive body contact masks. All unrelated source physics remain intact. A pre-existing custom collision routing is rejected instead of overwritten.

Scene contact runs before native chain pose construction in each substep, after integration and native collisions. Version 0.1.0 projected particles only at finalization, after native node matrices were already constructed; 0.1.1 fixes this ordering. Local disassembly shows pose construction consuming particle positions and writing node matrix translations, including node b. Finalization keeps native rotation/blending and skips the final fixed-length projection only when contact changed the chain. These call sites and signatures were audited against the supported binary; user feedback confirms queries run, but successful corrections remain unverified in-game.

Native particle positions, animation targets and velocities are in skeleton model space. The old translation-only conversion was incorrect. Version 0.1.3 uses the module's affine world matrix at `+0x10` for queries, lengths and previews, and its computed inverse for correction writeback. XYZ in each 16-byte row is defined; W padding is replaced with (0,0,0,1) before inversion. Invalid/singular matrices disable this contact update. Radii remain world metres. Velocity is transformed to world space before removing inward motion, then transformed back without translation. This also handles nonuniform scale without treating a local velocity as a world vector.

Exact-client evidence: simulator update transforms forces with the module inverse world matrix at `0x193A8B5` and `0x193A959–0x193A974`; the scalar vector transform at `0x338F60` confirms row-vector axis ordering. Finalization blends raw particle positions with skeleton model-pose translations (`0x1BE779E–0x1BE78EE`); skeleton lookup `0x25AEE0` forwards to partial-skeleton model pose access at `0x2F2E40`. No actor world transform is applied inside this position blend.

Downward rays supply floor points and normals. Sphere sweeps cover temporal movement and the links between nodes. Queries use the game's material/layer filters (`0x4000`, layer 1), and only geometry present in that collision scene can respond. Native hit normals are optional. When absent, 0.1.3 derives a normalized, incoming-ray-opposing normal from a finite, nondegenerate returned triangle, rejecting invalid distances or empty geometry. The exact-client mesh Raycast at `0x206EC00` transforms V1/V2/V3 to world space at `0x206EF23–0x206F0FC`, then the point and distance through `0x206F196`; its type-2 collider vtable is at `0x25916E8`. Thus the fallback consumes world geometry, not local mesh vertices. No collider pointer is dereferenced by this fallback.

All five particles (b/c/d/e/virtual tip) have separately configured 0–0.30 m radii. Root b and proximal c each have an independent protection flag, default on. A protected node's effective radius is zero and its particle is never written by this projector. Opting out permits contact displacement, including the root; this can separate the tail attachment or jitter and is experimental. Four bounded passes alternate length constraints and contact projection. Contact removes inward velocity without adding restitution. A zero-radius unprotected node can still move through chain-length constraints, but performs no direct contact queries. Fixed anchors and a surface can conflict, leaving residual length error; there is no guarantee that all visible fur stays outside surfaces.

Each incoming-link sphere sweep uses its receiving node's radius, not a varying-radius cone. The root has temporal sweeps but no incoming link. Body proxies retain their independent preset and middle/rear masks. No model-mesh fitting is performed.

The v1 single radius migrates to the same d/e/tip factors (1, .7, .45). Protected b/c retain configurable .55/.85 defaults for optional activation. Configuration snapshots deep-copy the array. One immutable settings snapshot is used across each frame's substeps and preview capture, avoiding mixed profiles.

Preview data is copied in the live owning chain's finalization callback into immutable managed snapshots. UI rendering only uses those snapshots and Dalamud WorldToScreen, never game-memory pointers. Node spheres use the effective solver radii; active scene capsules show the latest actual link query extents. When contacts are off, gray capsules show the configured potential profile; ground-only mode has spheres and no misleading active capsules. Temporal sweeps are not drawn. Snapshots expire after 0.5 seconds, and capture/render are enabled only while the configuration window is open and preview is selected. Wire rendering has no depth test. It shows collision-query geometry rather than the final blended mesh silhouette.

The plugin exposes query/contact counters plus separate raw scene/ground hits, invalid results and triangle-normal recoveries. With preview active, it also measures the maximum world-space difference between the four physical bone points and finalized node-matrix translations (node stride 160, translation `+0x70`); the virtual tip has no final matrix. This diagnostic does not move the preview or alter animation blending. Collision switches default off to permit an initial movement/animation comparison. No claim of universal furniture, clothing mesh, other-character, or fur silhouette collision is made.

## Optional random idle motion (0.1.4–0.1.6)

The default-off `RandomIdleMotions` setting is independent of native descriptor parameters and does not force a rebuild. The framework thread owns an elapsed-time scheduler with two independent channels and publishes immutable samples; worker callbacks never advance randomness or time. Six bounded event curves have zero displacement and first derivative at their endpoints. Each channel has a quiet interval, randomized duration/amplitude and no immediate repeated kind. Fluffy-tail duration is scaled by 1.25; the former 0.85 angle reduction is removed in 0.1.6. Dragon-tail angles retain their 0.7 scale. These are procedural target scales, not changes to the passive native preset.

Eligibility requires 1.8 seconds below 0.08 m/s and 0.18 rad/s, followed by a randomized initial wait of 0.8–2.5 seconds. Movement/turning fades the active input over 0.35 seconds; blocked states reset scheduling. Framework gaps greater than 0.5 seconds and player draw changes reset rather than catch up. Combat, casting, mounting, unconsciousness, zone transitions and cutscenes are gated through Dalamud conditions. GPose follows the user's existing pause option. Samples older than 0.5 seconds are ignored. Disabled ears and original-comparison tails are excluded independently.

The exact supported client calls `0x1BE6030` at `0x193B148` with `(chain, context, byte flags)`. Its length pass operates on particle positions; at `0x1BE634F–0x1BE63F1` the attraction pass reads particle target XYZ at `+0x40` and pulls toward it with the existing native coefficient. This routine does not write the target fields or replace particle storage. Native body/collision work and chain pose construction follow it. The detour has this audited three-argument ABI; it does not hook the nine-argument integrator.

For selected local chains only, stack-backed originals are captured, every input is validated, and a complete candidate target pose is built before any write. Cumulative bounded joint rotations preserve segment lengths; tail target b/c and ear roots remain exact. Only d/e/tip or an ear's virtual endpoint receive target XYZ changes. In 0.1.5, tail angles are larger and base durations become 4.8/3.4/2.1 seconds before type/random scaling. Ear curves and base durations are unchanged.

Live read-only sampling confirmed fluffy-strong d/e attraction of 0.0025/0.00125, explaining why transient target offsets can have little visible effect. During tail events only, `Pulse(t) * movementWeight` blends each changed d/e/tip node's attraction toward `max(original, limit)`: 0.04 for short/dragon tails and 0.12 for fluffy tails in 0.1.6. The audited instruction at `0x1BE638D` reads this coefficient at native node `+0x88` (stride 160). Existing stronger attraction is never reduced; unchanged target nodes, b/c and all ear coefficients are excluded. Finite ranges are validated before any write. The `finally` scope restores exact targets and attraction after every native call, including repeated iterations and exceptions. Drive is zero between events, on original-tail mode, or after movement fade-out. No particle position/velocity/force, rest length, animation resource, final bone pose, solver clock or weather input is directly edited by idle motion. Collision code sees native solver output; finalization retains the original blend. This does not force dormant simulators awake or guarantee visible amplitude for arbitrary custom bone axes.

After user feedback on 0.1.5, fluffy tails get three dedicated single-lobe arcs in 0.1.6. All include middle pitch and yaw, addressing thick fur masking tiny endpoint motion and model-Y yaw doing little to an upright chain. Rear/tip pulse phases start at 6%/12% of event duration and are rescaled to finish at the same endpoint. Joint-vector bounds are 0.85 radians for fluffy input, versus unchanged 0.5 for other tails and ears; the validator rejects any larger/nonfinite profile bounds before writing. Angles and drive fade smoothly to zero. No extra oscillation, lower damping or persistent stiffness is added. Ear sampling, non-fluffy curves, schedule timings and passive presets are unchanged. Upright, rearward and sideways synthetic chains verify meaningful middle/endpoint target travel, finite accepted samples, one-signed arcs and exact scope restoration; these are target-space checks, not game visual acceptance.

Descriptor construction records indices for existing standard two-node `j_mimi_l/r` chains or the appended pair. Binding refresh uses live module/skeleton identity and chain counts; cached chain addresses are only identity tokens. The hook rejects unrelated chain pointers before dereferencing, and then confirms live skeleton ownership and its current module. Bindings are cleared before descriptor replacement and restoration. The option works for supported ears with the tail in original mode and for a tail with ears disabled. New validation errors disable the optional target feature while allowing original constraints to run; existing physics remains separate.

Offline tests verify schedule variety, finite bounds, frame-rate stability at 30/60/144 FPS, movement/blocked-state cancellation, no catch-up bursts, independent switches, root/length preservation, complete target/coefficient restoration, unchanged other native bytes and self-only binding. Target excursions and a simplified attraction recurrence cover weak-tail response; they are not a native physics simulation. All 576 local Mod combinations are compared with idle enabled, accepting only the intentional 50% ear-wind coefficient change in 0.1.5. Ear damping, speed and base attraction remain unchanged, preserving movement tuning separately from wind. **0.1.4 ran in game: the user found ears visible and tails too subtle. The user found 0.1.5 fluffy motion still too subtle; 0.1.6 broader fluffy arcs and drive remain awaiting visual acceptance. Reduced ear-wind parameters are retained from 0.1.5.** A stone-wall contact was observed on 0.1.4, while user-reported misses on other player-solid obstacles remain deferred; no collision coverage fix is claimed.

## Lifecycle and limitations

- Ear/tail presets and collision modes rebuild base physics at the next owned native boundary. Changing node radii, root protection, preview or the idle-motion toggle does not rebuild or reset the native simulation. Manual reapply forces a rebuild.
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
