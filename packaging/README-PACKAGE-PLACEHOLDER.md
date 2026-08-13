# Package placeholder

This directory documents the install layout; it is not a release archive.

```text
<OPERATOR_INSTALL>/
  BepInEx/
    plugins/OperatorKillHouse/
      [MAP COMPANION DLL] OperatorKillHouse.dll
    OperatorMods/community.vektor-modular-killhouse/
      operator-map-package.json
      content/[DEPENDENCY ASSETBUNDLE]
      content/[SCENE ASSETBUNDLE]
      media/[PREVIEW IMAGE]
```

Install the separate Modded Operations download first. Do not place the
framework or API DLLs in the map archive, and do not publish a standalone alpha
API archive.
