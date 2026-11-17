# 0.1.1.0 — 2026-11-17

- Five independently adjustable tail contact radii: root b, proximal c, middle d, rear e and virtual tip; 0 disables a node's direct terrain/scene contact.
- Separate root and proximal protection switches, both on by default. Disabling protection allows that node to participate in projection; native attachment stability remains unverified.
- In-game wire preview of contact spheres and link sweep capsules, with node labels, hovered-node highlighting and contact coloring. Local player only; visible while the settings window is open.
- Radius and protection edits apply without rebuilding the native physics simulation. Existing scalar-radius settings migrate with their previous middle/rear/tip proportions.
- Fix collision callback ordering: project before native pose construction, so corrected particles feed native node transforms. Previously projection ran too late at finalization.
- Existing ear/tail presets, animation attraction, wind tuning and independent strength controls are unchanged. Body collisions retain their separate middle/rear presets.
- Release build and managed tests passed; 576 local Mod parameter comparisons still pass. New tests cover all root-protection combinations, query radii, migration and preview geometry.

Still an installation test release: native calls, in-game appearance, preview alignment, root stability and gameplay performance have not been validated in-game.

# 0.1.0.0 — 2026-11-17

First independent CN Dalamud installation test release.

- Four ear response levels, three tail styles and two inertia levels, with independent controls and an original-tail comparison.
- Runtime native-physics descriptor copies; no physical Mod/PMP or Penumbra dependency.
- Preserve native animation attraction and wind, including the weighted fluffy-tail tuning.
- Optional middle/rear body proxies, terrain rays and scene sphere sweeps. All collision options default off.
- Local player only; standard tail chains and cat-ear bones. GPose pause defaults on.
- Exact-client gating, descriptor validation, original-resource ownership and source-physics restoration.
- Release build: zero compiler warnings/errors. Offline parameter comparisons against 576 local Mod combinations passed.

Not yet validated: actual plugin loading in-game, native resource lifecycle, appearance, contact stability, Customize+ interaction, posing tools, redraw/teleport behavior, or performance under gameplay load. This is a test release, not an assertion that these behaviors already pass.
