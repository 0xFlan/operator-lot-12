# LOT 12: Randomized Kill House

This repository contains the authored source for the OPERATOR map package shown
in game as **LOT 12: FALSE WALL**. It includes the map companion, deterministic
ten-layout Unity builders, package contract, design data, and source validators.

Related projects:

- [OPERATOR: Modded Operations](https://github.com/0xFlan/operator-modded-operations)
- [OPERATOR map-modding guide](https://github.com/0xFlan/operator-map-modding-guide)

## Current source checkpoint

| Component | Version/status |
| --- | --- |
| Map package | `0.1.28` |
| Runtime companion | `0.1.23` (BepInEx and MelonLoader variants) |
| Required Modded Operations | `0.3.35` |
| Bundled internal Operator Mod API | `0.2.0-alpha.8` |
| PVE | 1-4 players; selectable 10-60 enemies |
| PVP | 2-12 players; six authored spawns per team |
| Multiplayer status | BepInEx and MelonLoader `PROVEN-RUNTIME` locally; separate-PC online proof pending |
| Public release status | Source checkpoint only; no binary release from this repo |

Protocol v6 gives both PVE and PVP exact content, scene-generation, companion,
runtime-owner, owner-local grounded placement, Restart, and teardown barriers.
PVE adds the same authoritative server-spawned AI population receipt on every
peer. PVP uses the native round mode and zero PVE AI. Neither mode is labeled
supported until a real host and separate remote complete its paired-log matrix.

## Main features

- Ten premade single-floor kill-house layouts selected without immediate repeats.
- PVE clear-and-extract mission with a private vanilla-style 10-60 enemy selector.
- Separate PVP operation for 2-12 players with six spawns per team.
- Native OPERATOR doors, furniture, warehouse presentation, lighting, and post-processing.
- Visible overhead fixtures mounted 0.080 m below the measured interior roof underside.
- Every active fixture lights nearby surfaces; one primary fixture per lit room
  owns soft shadows to avoid redundant indoor shadow maps.
- Native weapon muzzle particles, flash objects, dynamic lights, and lit surface
  receivers are validated after local weapon authority becomes ready.
- Preloaded decompressed door audio to reduce the hitch on the first doors opened.
- One owner-local insertion placement per exact scene generation; no periodic
  teleport-to-spawn maintenance after gameplay begins.
- Bounded cached runtime-readiness checks instead of repeated full-scene scans.
- Restart keeps the selected layout for the current operation.
- Completed-operation teardown releases transition ownership so another packaged map can load next.
- Exact package, companion, framework, runtime, player, and PVE population checks for multiplayer testing.

## Repository layout

| Path | Purpose |
| --- | --- |
| `source/runtime` | Shared source for isolated BepInEx and MelonLoader companions. |
| `source/unity_project/Assets/VektorKillHouse/Editor` | Deterministic Unity authoring and validation code. |
| `source/design` | Layout, provenance, and native-asset contracts. |
| `source/operator_map_packages` | Exact documentary package manifest plus payload placeholders. |
| `schemas` | Closed map-package schema used by this checkpoint. |
| `decompiled` | Code-only ILSpy verification snapshot of the companion DLL. |
| `docs` | Source-publication boundary and current status. |

## Required local inputs

- A legally owned, installed copy of OPERATOR.
- exactly one supported BepInEx or MelonLoader runtime plus generated interop assemblies.
- OPERATOR: Modded Operations `0.3.35` with its bundled internal API alpha.8.
- Unity `6000.3.8f1` with HDRP `17.3.0`.
- Native asset inputs extracted from your own installed game.

The Git repository deliberately omits OPERATOR-owned meshes, textures, audio,
prefabs, material-state records, generated scenes, bundles, and preview media.
Read [Source publication and asset placeholders](docs/reference/source-publication-and-asset-placeholders.md)
before building. A clone is source, not an installable mod.

## Build

1. Populate the authorized local inputs described by
   `source/unity_project/Assets/VektorKillHouse/Native/README-ASSET-PLACEHOLDER.md`.
2. Open `source/unity_project` in Unity `6000.3.8f1`.
3. Run **Vektor Kill House > Build > Rebuild Everything Through Local Proof Bundles**.
4. Build the companion:

```powershell
dotnet build .\source\runtime\OperatorKillHouse.csproj `
  -c Release `
  -p:OperatorLoader=BepInEx `
  -p:OperatorGameDir='<OPERATOR_INSTALL>'

dotnet build .\source\runtime\OperatorKillHouse.csproj `
  -c Release `
  -p:OperatorLoader=MelonLoader `
  -p:OperatorGameDir='<OPERATOR_INSTALL>'
```

The companion project fails closed when the authorized native material profile
inputs are absent. `AllowMissingNativeMaterialProfiles=true` is only for a
compile-shape check and does not produce a functional runtime companion.

## Install layout

Install Modded Operations first. The LOT 12 archive then extracts directly into
`<OPERATOR_INSTALL>`:

```text
<OPERATOR_INSTALL>/
  BepInEx/
    plugins/
      OperatorKillHouse/
        OperatorKillHouse.dll
  Mods/
    OperatorKillHouse.MelonLoader.dll
  OperatorMods/
    community.vektor-modular-killhouse/
      operator-map-package.json
      content/operator_vektor_killhouse
      content/operator_vektor_killhouse_scenes
      media/vektor_modular_killhouse_preview.png
```

One dual-loader archive may carry both companion entries, but only the entry
whose loader is selected can execute. Both managed trees may remain installed;
the Modded Operations loader selector must leave exactly one approved native
bootstrap active before OPERATOR starts. The package under `OperatorMods` is
shared and is not owned by the executable-suite uninstaller.

## License and asset boundary

The MIT License covers original code and documentation in this repository. It
does not relicense OPERATOR, Unity, BepInEx, MelonLoader, Mirror, third-party libraries, or
any omitted game-derived asset. See [Third-party notices](THIRD_PARTY_NOTICES.md).
