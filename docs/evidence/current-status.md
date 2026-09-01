# Current status

This repository is the source checkpoint for LOT 12 package `0.1.26` and
companion `0.1.22`, prepared against Modded Operations `0.3.33` and bundled
Operator Mod API `0.2.0-alpha.7`.

Static source, compiler, package, scene-authoring, PVE/PVP marker, and
identity-contract reviews pass in the private build workspace. The exact
companion identities are:

```text
OperatorKillHouse.dll                    471552  1AACB146E41929EE8C04B04326C1BB9B0A5BAC28A4C48F4FBC793CE03E3B7151
OperatorKillHouse.MelonLoader.dll        472576  B27DE25C42E5DDC52188304B457DA0EC99B082CD4E6FA45887D8A6B7CC78805F
```

The loader-neutral runtime-pair ID is
`a14a5498c15931933432f3f20d942eb25b2a12cb70dc0beb5f178f13cfc3a706`.
The package manifest is 5,403 bytes with SHA-256
`921318B659BE07CF97EB913298196C56D647B840C99D7A055CC1619FC55FA55B`.
Its computed package-content identity is
`CC39C1E00C73C56127D1B0063E200D83DA9678F137B9524A5760CA14E89350FF`.

Every one of the ten package scenes contains 72 tactical PVE candidates and
certifies the operation's private 10-60 enemy range. The selectable maximum of
60 native AI is an opt-in stress setting rather than a universal performance
claim. The rebuilt dependency bundle also preloads all 47 recovered door audio
clips to remove first-interaction decode work. Every runtime fixture
is mounted from the roof's interior underside with an exact 0.080 m rendered-top
gap, and its local light is held 0.18 m below the fixture top. The exact HDRP
volume contract uses bloom intensity 0.03.

All visible active fixtures continue to illuminate warehouse and kill-house
surfaces. Only the primary fixture in each lit room casts soft shadows, reducing
redundant indoor shadow-map work. The companion audits the owned native
weapon's muzzle particle/flash object, dynamic light, culling mask, and lit
surface receivers; it does not synthesize a fire effect or replace ballistics.

A fresh 2026-08-30 BepInEx lifecycle run passed all `18/18` assertions at a
selected count of 10. It retained the count across Restart, found 72 authored
and navigation-valid enemy markers with safe capacity 71, observed exactly 10
grounded native AI in each generation, removed the owned population, cleaned
the scene to zero runtime assets, and kept the package closure unchanged. A
separate current-byte sustained run sampled 4,579 frames with all 10 AI
grounded and network-ready, eight moving at least one metre, and no repeat
player placement. Its average was about 38.2 FPS with 28.79 ms p95 frame time
on the local test machine. These are single-machine results, not a promise of
identical performance on every system.

This checkpoint is not a public binary release. A real host plus separate
remote client must still pass separate PVE and PVP paired-log gates. PVE must
prove grounded owner-local placement, identical authoritative AI netIds/poses/
movement/health, reciprocal combat, extraction, Restart, and clean return.
PVP must prove opposing teams, movement, firearm-specific damage, scoring,
round respawn, Restart, zero PVE AI, and return. Late join is unsupported by
the fixed-roster agreement in either mode.

The repository excludes OPERATOR-derived assets and private test evidence.
The current BepInEx candidate has single-machine local runtime evidence. The
current MelonLoader candidate has matching source, a zero-warning build, and
archive/static proof, but its fresh lifecycle rerun remains open until the
machine is booted with MelonLoader's native shim and BepInEx's native shim is
disabled. These results do not promote either loader to online-supported
status. The selectable 60-enemy mode is an opt-in native-AI stress setting and
is not performance-qualified as lag-free across end-user hardware.
