# Current status

This repository is the source checkpoint for LOT 12 package `0.1.22` and
companion `0.1.18`, prepared against the Modded Operations `0.3.30` hotfix and bundled
Operator Mod API `0.2.0-alpha.7`.

Static source, compiler, package, scene-authoring, PVE/PVP marker, and
identity-contract reviews pass in the private build workspace. The exact
companion identities are:

```text
OperatorKillHouse.dll                    466944  E0ACEDEA31203009E74BC631110B81ABD581D80FB68AA979B41119578DFD9E34
OperatorKillHouse.MelonLoader.dll        467968  5FD24A36D93D8D7F40DF37CE3ABC1D32024EBDC88B3C42BEC8D6F53D5437EEB6
```

Every one of the ten package scenes contains 72 tactical PVE candidates and
certifies the operation's private 10-60 enemy range. A current-hash local
BepInEx lifecycle run launched and grounded 60/60 native server-owned enemies,
performed alive Restart, removed the prior 60 roots, and validated a fresh
60/60 population. The rebuilt dependency bundle also preloads all 47 recovered
door audio clips to remove first-interaction decode work. The required framework
hotfix clears completed transition ownership after package-scene release so a
later packaged map can accept Confirm.

This checkpoint is not a public binary release. A real host plus separate
remote client must still pass separate PVE and PVP paired-log gates. PVE must
prove grounded owner-local placement, identical authoritative AI netIds/poses/
movement/health, reciprocal combat, extraction, Restart, and clean return.
PVP must prove opposing teams, movement, firearm-specific damage, scoring,
round respawn, Restart, zero PVE AI, and return. Late join is unsupported by
the fixed-roster agreement in either mode.

The repository excludes OPERATOR-derived assets and private test evidence.
The current BepInEx lifecycle result is local evidence only. The MelonLoader
binary has compiled and passed static/package checks but still needs live
gameplay. Neither result promotes these hashes to online-supported status.
