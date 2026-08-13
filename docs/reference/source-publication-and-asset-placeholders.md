# Source publication and asset placeholders

This repository publishes original runtime code, Unity editor builders,
package/design contracts, validation source, and a code-only decompiler
snapshot. It does not publish OPERATOR-owned payload data.

## Omitted inputs

- `[AUTHORIZED OPERATOR NATIVE ASSET INPUTS]`: meshes, textures, audio,
  materials, prefabs, LUT data, and donor hierarchy records extracted from the
  contributor's own installed game.
- `[NATIVE MATERIAL PROFILE JSON]`: serialized material-state records required
  as companion embedded resources.
- `[AUTHORED GENERATED SCENE ASSETS]`: ten Unity scenes emitted by the checked-in
  deterministic builders after authorized inputs are present.
- `[DEPENDENCY ASSETBUNDLE]` and `[SCENE ASSETBUNDLE]`: generated package content.
- `[PREVIEW IMAGE]`: generated 1600x900 briefing image.

Placeholder README files use different names from the files declared in the
manifest. They are documentation only and must never be packaged as payload.
The exact documentary manifest retains the real release-candidate lengths and
hashes so reviewers can verify an authorized build independently.

## Decompiled verification boundary

ILSpy `10.1.1.8388` processed the exact 434,176-byte companion DLL with SHA-256
`A3240FD73A269572A8421B3E8428D899CA4D8B91642691A4161BB7BCE33CD427`.
Its complete local output was 26 files / 611,692 bytes with tree SHA-256
`3A3DD330DADAC95F170C832A1D6292E2641CAA4121A200C822F96846335BD98A`.
Twenty-two embedded game-derived material JSON resources are intentionally
excluded. The published four-file code-only tree is 363,072 bytes with tree
SHA-256 `6CAB81E40397275AEE7766A96793DFEC756F9E50EEFDD208F4DE57D73842324E`.
