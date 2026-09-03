from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path
from urllib.parse import unquote


ROOT = Path(__file__).resolve().parents[1]
TEXT_SUFFIXES = {
    ".asset", ".cs", ".csproj", ".json", ".md", ".meta", ".py", ".txt", ".yaml", ".yml"
}
FORBIDDEN_SUFFIXES = {
    ".bytes", ".dll", ".exe", ".glb", ".jpg", ".jpeg", ".log", ".obj",
    ".ogg", ".pdb", ".png", ".prefab", ".unity", ".zip",
}
PRIVATE_PATTERNS = {
    "Windows account path": re.compile(r"(?i)[A-Z]:[\\/]Users[\\/](?!<)"),
    "private LOT 12 workspace": re.compile(r"(?i)D:[\\/]Operator_KillHouse_Mod"),
    "private related workspace": re.compile(r"(?i)D:[\\/]Operator_GroceryStore_Mod"),
    "private application-data path": re.compile(r"(?i)AppData[\\/]Local"),
}
LINK_PATTERN = re.compile(r"(?<!!)\[[^\]]+\]\((?P<target>[^)]+)\)")


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def repository_files() -> list[Path]:
    return [
        path
        for path in ROOT.rglob("*")
        if path.is_file()
        and ".git" not in path.parts
        and "bin" not in path.parts
        and "obj" not in path.parts
        and "__pycache__" not in path.parts
    ]


def decompiled_tree_identity(root: Path) -> tuple[int, int, str]:
    files = sorted(
        (path for path in root.rglob("*") if path.is_file()),
        key=lambda path: path.relative_to(root).as_posix(),
    )
    total = 0
    records: list[str] = []
    for path in files:
        data = path.read_bytes()
        total += len(data)
        records.append(
            f"{path.relative_to(root).as_posix()}\0{len(data)}\0{sha256_bytes(data)}\n"
        )
    return len(files), total, sha256_bytes("".join(records).encode("utf-8"))


def check_markdown_links(path: Path, text: str, errors: list[str]) -> None:
    for match in LINK_PATTERN.finditer(text):
        target = match.group("target").strip().strip("<>")
        if target.startswith(("http://", "https://", "mailto:", "#", "<")):
            continue
        relative = unquote(target.split("#", 1)[0])
        if relative and not (path.parent / relative).resolve().exists():
            errors.append(f"broken Markdown link: {path.relative_to(ROOT)} -> {target}")


def main() -> int:
    errors: list[str] = []
    required = (
        "README.md",
        "LICENSE",
        "CHANGELOG.md",
        "THIRD_PARTY_NOTICES.md",
        "CONTRIBUTING.md",
        "SECURITY.md",
        "source/runtime/OperatorKillHousePlugin.cs",
        "source/runtime/OperatorKillHouse.csproj",
        "source/package/package_blueprint.json",
        "source/design/killhouse_variant_matrix.json",
        "source/design/native_asset_allowlist.json",
        "source/operator_map_packages/community.vektor-modular-killhouse/operator-map-package.json",
        "source/unity_project/Assets/VektorKillHouse/Editor/KillHouseBuildPipeline.cs",
        "source/unity_project/Assets/VektorKillHouse/Editor/KillHouseVariantBuilder.cs",
        "source/unity_project/Assets/VektorKillHouse/Native/README-ASSET-PLACEHOLDER.md",
        "source/unity_project/Assets/VektorKillHouse/Scenes/README-ASSET-PLACEHOLDER.md",
        "source/unity_project/Packages/manifest.json",
        "source/unity_project/ProjectSettings/ProjectVersion.txt",
        "decompiled/README.md",
        "decompiled/release-0.1.17/OperatorKillHouse/OperatorKillHousePlugin.cs",
        "schemas/operator-map-package-v2.schema.json",
        "packaging/README-PACKAGE-PLACEHOLDER.md",
    )
    for relative in required:
        if not (ROOT / relative).is_file():
            errors.append(f"missing required source file: {relative}")

    source_path = ROOT / "source/runtime/OperatorKillHousePlugin.cs"
    if source_path.is_file():
        data = source_path.read_bytes()
        if len(data) != 381857 or sha256_bytes(data) != (
            "37BAAE1B4A417D164685301DF78E2B89AAA90FD7BFF4E3A1414DC07DB966DA5B"
        ):
            errors.append("authored companion source identity does not match checkpoint 0.1.23")
        text = data.decode("utf-8", errors="replace")
        for fragment in (
            '[BepInDependency("operator.modded-operations")]',
            '[BepInDependency("operator.modapi")]',
            'public const string PluginGuid = "operator.vektor-killhouse";',
            'public const string PluginVersion = "0.1.23";',
            'private const string ModdedOperationsReadyMarkerName = "MODDED_OPERATIONS_RUNTIME_CONTRACT_READY";',
            'private const string ModdedOperationsFailureMarkerName = "MODDED_OPERATIONS_RUNTIME_CONTRACT_FAILED";',
            "AuditNativeMuzzleFlashContract(",
            "activeRoot.GetComponentsInChildren<MuzzleFlash>(true)",
            "flash.m_muzzleFlashParticleSystem",
            "light.type != LightType.Directional",
            "renderer.sharedMaterials.Any",
            "AppendRuntimeStageTiming(runtimeStageTimings, \"publish-ready\"",
        ):
            if fragment not in text:
                errors.append(f"companion runtime contract is missing: {fragment}")
        for forbidden in (
            "AddComponent<MuzzleFlash>",
            "AddComponent<Projectile>",
            "AddComponent<Bullet>",
        ):
            if forbidden in text:
                errors.append(f"companion must not synthesize native weapon effects or ballistics: {forbidden}")

    builder_path = (
        ROOT
        / "source/unity_project/Assets/VektorKillHouse/Editor/KillHouseVariantBuilder.cs"
    )
    if builder_path.is_file():
        builder = builder_path.read_text(encoding="utf-8")
        for fragment in (
            "state == RoomLightState.Lit && ordinal == 0",
            "shadowCastingFixtureLights == litRooms",
            '"expectedShadowCastingFixtureLights"',
            "one-soft-shadow-owner-per-lit-room",
        ):
            if fragment not in builder:
                errors.append(f"authored lighting performance contract is missing: {fragment}")

    manifest_path = (
        ROOT
        / "source/operator_map_packages/community.vektor-modular-killhouse/operator-map-package.json"
    )

    matrix_path = ROOT / "source/design/killhouse_variant_matrix.json"
    if matrix_path.is_file():
        matrix = json.loads(matrix_path.read_text(encoding="utf-8"))
        framework = matrix.get("runtimeCompatibility", {}).get("framework", {})
        expected_framework = {
            "pluginGuid": "operator.modded-operations",
            "version": "0.3.35",
            "fileName": "OperatorModdedOperations.dll",
            "bytes": 644608,
            "sha256": "ce6d28f478f2563709d7933dfc8a4f8dabd215e0b678555fe39d1a0e1b616f55",
            "melonLoaderFileName": "OperatorModdedOperations.MelonLoader.dll",
            "melonLoaderBytes": 646144,
            "melonLoaderSha256": "297bf4fb086f5fc7296563060c05d95c153d316579a6f57ee64905896dd30f60",
        }
        if framework != expected_framework:
            errors.append("design matrix framework identity does not match checkpoint 0.3.35")

    if manifest_path.is_file():
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if manifest.get("packageId") != "community.vektor-modular-killhouse":
            errors.append("manifest package ID changed")
        if manifest.get("version") != "0.1.28":
            errors.append("manifest package version changed")
        maps = manifest.get("maps", [])
        if len(maps) != 1 or len(maps[0].get("sceneVariants", [])) != 10:
            errors.append("manifest must contain one map with ten scene variants")
        elif maps[0].get("operations", [{}])[0].get("minEnemies") != 10 or (
            maps[0].get("operations", [{}])[0].get("maxEnemies") != 60
        ):
            errors.append("LOT 12 PVE enemy range must remain 10..60")
        operations = maps[0].get("operations", []) if maps else []
        modes = {operation.get("mode") for operation in operations}
        if modes != {"pve", "pvp"}:
            errors.append("manifest must contain distinct PVE and PVP operations")
        pvp = next((operation for operation in operations if operation.get("mode") == "pvp"), {})
        if pvp.get("maxPlayers") != 12:
            errors.append("PVP maximum player contract must remain 12")
        for row in manifest.get("files", []):
            if (manifest_path.parent / row.get("path", "")).exists():
                errors.append(f"declared payload must not be committed: {row.get('path')}")

    expected_tree = (
        4,
        414637,
        "31D8541D5B0B574D1A2E35078584FB644D6E0A034FD6BEA7DF74EAFD0B0D76EF",
    )
    decompiled_root = ROOT / "decompiled/release-0.1.17"
    if decompiled_root.is_dir():
        actual_tree = decompiled_tree_identity(decompiled_root)
        if actual_tree != expected_tree:
            errors.append(
                f"code-only decompiler tree mismatch: expected={expected_tree!r}; actual={actual_tree!r}"
            )

    placeholder_text = "\n".join(
        path.read_text(encoding="utf-8", errors="replace")
        for path in ROOT.rglob("README*PLACEHOLDER.md")
    )
    for token in (
        "[AUTHORIZED OPERATOR NATIVE ASSET INPUTS]",
        "[NATIVE MATERIAL PROFILE JSON]",
        "[AUTHORED GENERATED SCENE ASSETS]",
        "[DEPENDENCY ASSETBUNDLE]",
        "[SCENE ASSETBUNDLE]",
        "[PREVIEW IMAGE]",
        "[MAP COMPANION DLL]",
    ):
        if token not in placeholder_text:
            errors.append(f"asset placeholder class is missing: {token}")

    for path in repository_files():
        relative = path.relative_to(ROOT)
        if path.suffix.lower() in FORBIDDEN_SUFFIXES:
            errors.append(f"forbidden binary or payload file: {relative}")
        if path.stat().st_size >= 90 * 1024 * 1024:
            errors.append(f"file exceeds normal Git blob policy: {relative}")
        if path.suffix.lower() not in TEXT_SUFFIXES and path.name not in {
            ".gitattributes", ".gitignore", "LICENSE"
        }:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for label, pattern in PRIVATE_PATTERNS.items():
            if pattern.search(text):
                errors.append(f"{label}: {relative}")
        if path.suffix.lower() == ".json":
            try:
                json.loads(path.read_text(encoding="utf-8-sig"))
            except json.JSONDecodeError as exc:
                errors.append(f"invalid JSON: {relative}:{exc.lineno}:{exc.colno}: {exc.msg}")
        if path.suffix.lower() == ".md":
            check_markdown_links(path, text, errors)

    if errors:
        print("repository audit failed:")
        for error in errors:
            print(f"- {error}")
        return 1

    print(f"repository audit passed: files={len(repository_files())}; required={len(required)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
