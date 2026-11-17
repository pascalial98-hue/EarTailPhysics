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
