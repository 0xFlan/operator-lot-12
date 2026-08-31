# Changelog

## 0.1.25 / companion 0.1.21 performance and lighting candidate — 2026-08-30

- Keep every visible ceiling fixture and its contribution to walls and floors,
  while limiting dynamic soft-shadow ownership to one primary fixture per lit
  room. Dim and secondary fixtures remain real lights without redundant shadow
  maps.
- Audit the equipped owned firearm's native `MuzzleFlash` graph, including its
  particle/flash object, non-directional dynamic light, light range/culling
  mask, and HDRP/Lit receivers. The companion does not create a synthetic
  effect or alter OPERATOR's bullets, hits, damage, or recoil.
- Time each exact scene-preparation stage so cold bundle, material, navigation,
  spawn, and ready-marker cost can be distinguished in logs.
- Require Modded Operations `0.3.32`, which throttles stable maintenance,
  retires resolved laptop/weapon scans, and preserves per-frame PVE readiness
  barriers.
- Retain the private 10-60 selector. Sixty native AI remains an opt-in stress
  setting; it is not described as lag-free on every CPU/GPU.
- Rebuild and load-verify all ten scenes and both bundles; pass the repository
  audit and zero-warning BepInEx/MelonLoader companion builds.

## 0.1.24 / companion 0.1.20 runtime-fix candidate — 2026-08-30

- Corrected every overhead fixture to mount from the warehouse roof's interior
  underside rather than its exterior top face. The rendered fixture top now
  keeps an exact 0.080 m underside gap and cannot remain embedded in the roof.
- Rebuilt and load-verified all ten layouts and both bundles. Fresh local
  BepInEx and MelonLoader 60-enemy PVE runs passed initial load, alive Restart,
  fixture/material/lighting gates, grounded population validation, count
  retention, and clean teardown. Separate-PC online proof remains pending.
- Removed periodic player repositioning. Each owner places its own character at
  the assigned insertion once per exact scene generation, acknowledges it, and
  retires the placement path for normal gameplay.
- Kept remote player transforms, AI transforms, bullets, hits, health, damage,
  animation, and death under OPERATOR/Mirror ownership.
- Required Modded Operations `0.3.31`, which caches the companion READY marker
  and bounds steady-state membership/readiness work instead of repeatedly
  scanning the complete scene.
- Retained the separate 1-4 player PVE operation with a 10-60 enemy selector and
  the separate 2-12 player PVP operation with six spawns per team.
- Passed the repository and dual-loader binary audits, a fresh native Restart
  lifecycle, and a 120-second local PVE run: 10/10 AI grounded and network-ready,
  six moved at least one metre, no repeated placement, p99 32.08 ms, and only
  9.4 MiB private-memory growth. The current BepInEx RED CELL briefing run also
  passed all 9 local assertions and kept the PVE enemy selector hidden.
- Passed a fresh 18/18 BepInEx PVE/restart regression: selector 12 retained,
  72 authored/navigation markers, safe capacity 71, exactly 12 AI spawned and
  observed, owned population removed, zero framework runtime assets after
  teardown, and unchanged package closure.
  Separate-PC host/client PVE and PVP proof is
  still mandatory before online support is claimed.

## 0.1.22 / companion 0.1.18 test candidate — 2026-08-29

- Preloaded all 47 wooden-door clips from the shared dependency bundle, using
  decompressed-on-load audio with background loading disabled. This removes
  first-use audio decoding from the first doors opened during a mission.
- Rebuilt and statically validated all ten randomized layouts and both asset
  bundles.
- Expanded and retained the private LOT 12 PVE selector at 10-60 enemies, with
  72 authored candidates per scene and a companion-certified capacity of 60.
  PVP, Tier 1, vanilla missions, and other maps remain untouched.
- Required the hash-pinned Modded Operations `0.3.30` hotfix, which releases a
  completed packaged mission's transition ownership after returning to the
  Operation Room. The stale owner could block a later LOT 12, Ukrainian Forest,
  or Whiteout/Winter Confirm action.
- Kept the release fail-closed for online support until separate host/client PVE
  and PVP runs prove the corrected sequence.

## 0.1.18 — 2026-08-12

- Added a second, distinct operation: `OPERATION FALSE WALL: RED CELL`
  (`community.vektor-modular-killhouse.pvp`). The existing `OPERATION FALSE WALL` PVE mission,
  its 10-15 enemies, four player markers, AI profile, safe-room insertion, and extraction remain unchanged.
- Set RED CELL to the installed game's native 2-12 player range and a balanced 6v6 authored maximum.
  The installed level1 lobby slider at MonoBehaviour path 5963 is whole-number 2-12 and writes its
  rounded value to `Mirror.NetworkManager.maxConnections`; Steam reserves two additional lobby slots.
- Added one isolated `killhouse-pvp` spawn set per variant, exactly six Team 1 and six Team 2 markers,
  and an `OPPOSING ENTRIES` infiltration capped at 12 players. PVE and PVP markers are mode-strict.
- Placed each PVP team across three connected rooms in disjoint sectors. Opposing spawns are at least
  four room-graph steps and 20 metres apart, have zero direct eye-level line-of-sight pairs, clear
  doors, portals, furniture, and standing capsules, and face the opposing sector.
- Kept PVE-only enemy/profile fields out of the closed PVP manifest record. RED CELL ignores PVE enemy
  markers and requires a zero-PVE-AI live result under native standalone `PvpGameode` ownership.
- Added fail-closed release gates for exact-payload host and remote client proof: opposite teams,
  authored first spawns, native freeze release, reciprocal bullet damage, score update, both-team
  round respawn, alive-Restart variant retention, zero PVE AI, and clean operation unload.
- Prepared package `0.1.18`, companion `0.1.16`, and required Modded Operations `0.3.29`. Operator
  Mod API is the bundled internal `0.2.0-alpha.6` preview runtime; there is no separate API download.
- Added the optional closed schema-v2 `runtimeCompanion` declaration. It pins the exact companion
  GUID, version, DLL SHA-256, and shared ready/failure markers; Modded Operations must observe exactly
  one ready marker and zero failure markers in the active generation scene before PVP SceneReady.
- Pinned the final alpha.6 Core/host binaries, Modded Operations `0.3.29` binary, and companion
  `0.1.16` binary. The framework/API source-state publication identities plus real host/remote PVP proof remain pending, so the
  Nexus packager remains fail-closed and no standalone API archive is produced.

## 0.1.17 — 2026-08-11

- Normalized the package, catalog/map, companion, Nexus, and archive display name to
  `LOT 12: FALSE WALL`; the operation remains `OPERATION FALSE WALL`. Stable technical IDs,
  bundle names, DLL names, and scene paths are unchanged for upgrade compatibility.
- Replaced the debrief/scene-selection preview with an exact 1600x900 harsh black-and-white
  surveillance view of the Lot 12 warehouse service bay and its modular interior.
- Reduced overhead fluorescent tube-surface emission by 40 percent: lit `512` to `307.2` and dim
  `16` to `9.6`. Fixture lumens and the native Warehouse Bloom/lens-flare stack remain unchanged.
- Corrected Modded Operations' native spawn-validation timing. It now captures the exact Mirror-owned
  bot identities synchronously and validates their native team graph after `BrainAI.Start` registers
  them, instead of mistaking the deferred registration for a zero-enemy spawn.
- Keeps the 10–15 native enemy population, single hostile team cohort, Offensive disposition,
  0.25-second maximum reaction window, and deterministic 3–6 second initial movement window.
- Prepared package `0.1.17`, companion `0.1.15`, and required Modded Operations `0.3.28` as new
  immutable candidate identities. Operator Mod API remains the bundled internal alpha.5 runtime.

## 0.1.16 — 2026-08-11

- Prepared package `0.1.16`, companion `0.1.14`, and required Modded Operations `0.3.27` as a new
  immutable release identity; the published `0.1.15` files remain unchanged.
- Normalized the public package, catalog/map, Nexus, archive-metadata, and companion display name to
  `LOT 12: FALSE WALL`; the mission/operation title remains `OPERATION FALSE WALL`.
- Set the area to `NORTH CAROLINA, REDACTED "LOT 12"` from the installed PVP Woods Warehouse
  target-package/opboard data and the insertion label to its exact donor value, `ALPHA 2`.
- Rewrote the SITREP as a concise tactical briefing without implementation details.
- Preserved all stable package, map, operation, infiltration, variant, and scene-path identities.
- Fixed native enemy-on-enemy targeting at its source. Modded Operations now supplies
  `RaidManager.standardAI` from one unique strict-largest hostile `TeamIdentifier`/
  `StartingTeamStats` cohort, excludes the live player's team dynamically, and validates the
  spawned native team/reference/target-pool closure. It does not patch damage or edit target lists.
- Added the opt-in `killhouse-indoor-assault-v3` response profile. Native `BotSpawnDetails` now uses
  Offensive disposition, and newly spawned operation bots receive a server-only one-time cap of
  0.25 seconds on both native current and base reaction time without slowing a faster native value.
- Kept the 10-15 population, 36 m detection, 125-degree FOV, 40 m effective range, 12 m wander
  radius, and deterministic 3-6 second first-wander window.
- Updated the bundled internal Operator Mod API preview contract to `0.2.0-alpha.5`. It remains included
  inside the Modded Operations download and is not published as a standalone API.

## 0.1.15 — 2026-08-10

- Added deterministic, full-scale center-room dressing using the exact installed-game
  `Couch_2seat` and `Kitchen_table_large` donors.
- Preserved the couch's vanilla mesh, `Couch_Fabric` material and BaseColor/Normal/Mask textures,
  four ordered colliders, layer 24, renderer state, and retained GO2175/T9763 probe anchor.
- Added fail-closed room-perimeter, door/socket-approach, sibling-overlap, tactical-marker, and
  0.42 m capsule-circulation gates. Rejected candidates are skipped; furniture is never shrunk.
- Corrected retained residential-root UV0/UV1 orientation against the pinned installed `level4`
  vertex streams. Retained hierarchy children keep their already-correct UV orientation.
- Reduced the optional first-wander response cap from 12 seconds to 6 seconds. Modded Operations
  applies a deterministic 50–100% stagger, so eligible newly spawned native enemies begin their
  first wander response in the bounded 3–6 second range.
- Kept native OPERATOR combat, cover, suppression, reaction, door, and later wander-cycle logic.
- Kept the native PVE population range at 10–15 enemies.
- Removed local-test labels from the public map and operation names.
- Raised scene and aggregate validation evidence to schema 17 and the native-asset allowlist to
  schema 4.
- Companion version: 0.1.13. Required Modded Operations version: 0.3.26. Required Operator Mod
  API version: 0.2.0-alpha.4.
- Restored the established two-download Nexus layout. Modded Operations now bundles the pinned
  Operator Mod API alpha.4 runtime and its notice, checksums, and license; no standalone API archive
  is published. A separate Operator Mod API release is deferred until the full API.
- Release companion builds now omit CodeView/PDB records so published DLLs do not expose private
  build paths.

## 0.1.14 — 2026-08-10

- Added the versioned `killhouse-indoor-guard-v2` profile with 36 m detection, 125-degree field of
  view, 40 m maximum effective range, 12 m wandering, communications, and counter-suppression.
- Changed the native PVE population to 10–15 enemies.
- Added the optional framework-owned initial-wander-delay contract with a 12-second cap.
- Rebuilt all ten variants after the exact residential-root UV correction.

## 0.1.13 and earlier

- Established ten framework-selected, restart-pinned kill-house variants inside the native PVP
  Woods Warehouse shell.
- Added exact native warehouse flooring, industrial fluorescent fixtures, rugged DoorV2 doors,
  wall-backed residential furniture closure, indoor navigation, extraction, and lifecycle gates.
