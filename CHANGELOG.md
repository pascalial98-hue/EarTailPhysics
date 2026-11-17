# 0.1.4.0 — 2026-11-17

- Add an opt-in random idle-motion switch, off by default, without rebuilding native physics when toggled.
- Independent ear/tail schedules: slow tail sway, tip lift, single flick, left/right ear twitch and a brief paired-ear perk. Vary timing, amplitude, duration and tail direction; avoid consecutive identical events per channel.
- Wait for stillness; fade motion while moving/turning. Suspend in combat, casting, mounted, unconscious, area-transition and cutscene states, and obey the existing GPose pause setting. Respect ear-off and original-tail choices.
- Feed bounded, length-preserving additive targets into the native attraction/constraint pass, then restore exact original targets after every call. Keep root/proximal targets, physics presets, live wind, animation resources and final animation blending intact. Existing collision processing runs after this input.
- Expose current action, scheduled-event count and actual target-application count. Only validated local-player standard tail and cat-ear chains participate.
- Offline checks cover six actions, event boundaries, frame-rate variation, movement/pause gates, exact restoration, ownership and 576 old Mod parameter combinations with the new switch enabled.

This is an experimental motion test release. The new native constraint detour, visible motion strength and collision interaction have not been exercised in-game here. The 0.1.3 collision fixes remain awaiting tester feedback. Mesh compression remains separate and paused.

# 0.1.3.0 — 2026-11-17

- Fix collision-query and preview coordinates: native particles are in skeleton model space, not world-oriented offsets. Apply the full actor affine transform, including rotation and scale; invert it for corrected positions and velocities.
- Measure constrained segment lengths in world space. Per-node radii remain world metres. Sanitize unused native matrix padding and reject invalid/singular transforms before querying or writing particles.
- Handle valid mesh hits that return triangle vertices without an optional normal. Recover a normal from the returned world-space triangle; reject degenerate geometry and invalid travel distances.
- Expose native scene/ground hits, invalid results and triangle-normal recovery separately from queries/adjusted contacts. Preview diagnostics report physical-point versus finalized animation-blend position differences without relocating query geometry.
- Preserve the 0.1.2 takeover/wind path, all ear/tail preset coefficients, animation blending, damping and root-protection choices. No mesh compression is included.
- Offline regression coverage includes rotated/scaled actors, transformed wall/floor response, matching query/preview coordinates, missing-normal hits, invalid results and 576 old Mod parameter comparisons.

Users confirmed 0.1.2 takeover, wind and query activity, but reported zero contacts and badly displaced preview geometry. The 0.1.3 corrections have passed offline checks; actual client contact response and visual alignment still require game testing.

# 0.1.2.0 — 2026-11-17

- Candidate fix for remaining in waiting state with no wind, collision preview or queries: apply through the local character's native environment-wind callback as well as the existing job-creation fallback.
- Preserve live wind input and smoothing during descriptor replacement; always continue the game's original environment-wind calculation. Old Mod wind coefficients, animation attraction, damping and inertia presets are unchanged.
- Rebind the live tail chain after native replacement, including reused simulator-vector storage. Keep ownership restricted to the local player.
- Add readable runtime status: local character/scene availability, both entry counters, applied count, tail callbacks, wind input and contact prerequisite failures.
- Release build and managed checks pass, including 576 old Mod parameter comparisons and new ownership, wind-handoff and chain-rebinding checks.

This is a diagnostic installation test release. The reported waiting failure has not been reproduced with a live game; takeover, wind and scene contacts still require in-game confirmation. Mesh compression is not included.

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
