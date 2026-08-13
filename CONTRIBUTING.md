# Contributing

Keep changes source-only and map-specific.

- Do not commit OPERATOR assets, generated scenes, bundles, DLLs, interop
  assemblies, logs, screenshots, private QA, or machine-specific paths.
- Preserve package, map, operation, infiltration, and scene-variant IDs unless
  an explicit compatibility migration is part of the change.
- Keep PVE and PVP spawn-marker families isolated.
- Run `python eng/audit_repository.py` before opening a pull request.
- Do not describe multiplayer as supported without current-hash host/remote evidence.
