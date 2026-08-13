#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

public static class KillHouseBundleBuilder
{
    public const string OutputFolder = "Builds/VektorKillHouse";
    public const string DependencyBundleName = "operator_vektor_killhouse";
    public const string SceneBundleName = "operator_vektor_killhouse_scenes";

    private static readonly string[] ScenePaths =
    {
        "Assets/VektorKillHouse/Scenes/KH01_CircuitHouse.unity",
        "Assets/VektorKillHouse/Scenes/KH02_OffsetFigureEight.unity",
        "Assets/VektorKillHouse/Scenes/KH03_SerpentineApartment.unity",
        "Assets/VektorKillHouse/Scenes/KH04_CourtyardRing.unity",
        "Assets/VektorKillHouse/Scenes/KH05_SplitSpine.unity",
        "Assets/VektorKillHouse/Scenes/KH06_CompressedGrid.unity",
        "Assets/VektorKillHouse/Scenes/KH07_BrokenDiamond.unity",
        "Assets/VektorKillHouse/Scenes/KH08_DoubleBack.unity",
        "Assets/VektorKillHouse/Scenes/KH09_Pinwheel.unity",
        "Assets/VektorKillHouse/Scenes/KH10_WideLabyrinth.unity"
    };

    [MenuItem("Vektor Kill House/Build/Build Ten-Scene Local Proof Bundles", priority = 30)]
    public static void Build()
    {
        JObject validation = LoadValidation();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        foreach (string scene in ScenePaths)
        {
            AssetDatabase.ImportAsset(scene,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scene) == null)
                throw new FileNotFoundException("Kill-house scene is missing.", scene);
        }

        string[] dependencies = ScenePaths.SelectMany(scene => AssetDatabase.GetDependencies(scene, true))
            .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            .Where(path => !ScenePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Where(path => path.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) < 0)
            .Where(path => !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !AssetDatabase.IsValidFolder(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (dependencies.Length == 0) throw new InvalidDataException("Kill-house dependency closure is empty.");
        if (!dependencies.Any(path => path.Contains("/Native/Prefabs/")) ||
            !dependencies.Any(path => path.Contains("/Native/Materials/")) ||
            !dependencies.Any(path => path.Contains("/Native/Residential/Textures/")))
            throw new InvalidDataException("Kill-house dependency closure omits required native prefab/material/texture families.");

        Directory.CreateDirectory(OutputFolder);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            OutputFolder,
            new[]
            {
                new AssetBundleBuild { assetBundleName = DependencyBundleName, assetNames = dependencies },
                new AssetBundleBuild { assetBundleName = SceneBundleName, assetNames = ScenePaths }
            },
            BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);
        if (manifest == null) throw new InvalidOperationException("Unity returned no kill-house AssetBundle manifest.");

        string dependencyPath = Path.GetFullPath(Path.Combine(OutputFolder, DependencyBundleName));
        string scenePath = Path.GetFullPath(Path.Combine(OutputFolder, SceneBundleName));
        Verify(dependencyPath, scenePath, dependencies);
        WriteReport(validation, manifest, dependencies, dependencyPath, scenePath);
        Debug.Log("[Vektor Kill House] Ten-scene local proof bundles built and load-verified at " + Path.GetFullPath(OutputFolder) + ".");
    }

    private static JObject LoadValidation()
    {
        string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "evidence", "killhouse_scene_validation.json"));
        if (!File.Exists(path)) throw new FileNotFoundException("Aggregate scene validation report is missing.", path);
        JObject report = JObject.Parse(File.ReadAllText(path));
        if (report.Value<int>("sceneCount") != 10 ||
            report.Value<int>("uniqueCycleCount") != 10 ||
            report.Value<int>("uniqueOrderedRoomSequenceCount") != 10 ||
            report.Value<int>("uniquePortalPatternCount") != 10 ||
            report.Value<int>("uniqueSpatialMotifCount") != 10 ||
            !report.Value<bool>("allPassed"))
            throw new InvalidDataException("Aggregate scene validation does not authorize a local proof build.");
        return report;
    }

    private static void Verify(string dependencyPath, string scenePath, IReadOnlyCollection<string> dependencyAssets)
    {
        if (!File.Exists(dependencyPath) || !File.Exists(scenePath))
            throw new FileNotFoundException("One or both kill-house bundle files are missing after build.");
        AssetBundle.UnloadAllAssetBundles(false);
        AssetBundle dependency = AssetBundle.LoadFromFile(dependencyPath);
        if (dependency == null) throw new InvalidDataException("Kill-house dependency bundle cannot be loaded.");
        AssetBundle scenes = AssetBundle.LoadFromFile(scenePath);
        if (scenes == null)
        {
            dependency.Unload(false);
            throw new InvalidDataException("Kill-house scene bundle cannot be loaded.");
        }
        try
        {
            string[] loadedScenes = scenes.GetAllScenePaths();
            if (loadedScenes.Length != ScenePaths.Length ||
                ScenePaths.Any(expected => !loadedScenes.Contains(expected, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("Kill-house scene bundle does not expose the exact ten-scene set.");
            var furniturePaths = new HashSet<string>(KillHouseNativePrefabBuilder.FurniturePrefabPaths(),
                StringComparer.OrdinalIgnoreCase);
            foreach (string prefabPath in dependencyAssets.Where(path => path.Contains("/Native/Prefabs/") && path.EndsWith(".prefab")))
            {
                GameObject prefab = dependency.LoadAsset<GameObject>(prefabPath);
                if (prefab == null)
                    throw new InvalidDataException("Dependency bundle cannot load native prefab " + prefabPath + ".");
                if (furniturePaths.Contains(prefabPath) &&
                    !KillHouseNativePrefabBuilder.HasExactFurniturePrefabContract(prefab,
                        out string furnitureFailure, false))
                    throw new InvalidDataException("Dependency bundle furniture closure failed for " +
                                                   prefabPath + ": " + furnitureFailure + ".");
            }
            if (!furniturePaths.All(path => dependencyAssets.Contains(path, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("Dependency bundle omits one or more audited vanilla furniture prefabs.");
            Material fluorescentMaterial = dependency.LoadAsset<Material>(
                KillHouseNativeMaterialBuilder.MaterialPath("Lamps_C_on _cagville"));
            if (!KillHouseNativeMaterialBuilder.HasKillHouseFluorescentEmissionContract(fluorescentMaterial))
                throw new InvalidDataException("Dependency bundle lost the vanilla-derived bright warehouse fluorescent contract.");
            GameObject fluorescentFixture = dependency.LoadAsset<GameObject>(
                KillHouseNativePrefabBuilder.PrefabPath("Lamp_fluorescent_B"));
            Renderer[] fixtureRenderers = fluorescentFixture == null
                ? Array.Empty<Renderer>()
                : fluorescentFixture.GetComponentsInChildren<Renderer>(true);
            if (fixtureRenderers.Length == 0 || fixtureRenderers.Any(renderer =>
                    renderer.sharedMaterials.Length == 0 ||
                    renderer.sharedMaterials.Any(material =>
                        !KillHouseNativeMaterialBuilder.HasKillHouseFluorescentEmissionContract(material))))
                throw new InvalidDataException("Dependency bundle fluorescent prefab does not expose its visible emissive tube material.");
            Material warehouseFloorMaterial = dependency.LoadAsset<Material>(
                KillHouseNativeMaterialBuilder.MaterialPath("Floor"));
            if (!KillHouseNativeMaterialBuilder.HasExactWarehouseFloorTransportContract(
                    warehouseFloorMaterial, out string warehouseFloorFailure, false))
                throw new InvalidDataException("Dependency bundle lost the exact PVP Woods Warehouse floor material: " +
                                               warehouseFloorFailure + ".");
            GameObject warehouseFloor = dependency.LoadAsset<GameObject>(
                KillHouseNativePrefabBuilder.PrefabPath("Floor"));
            if (!KillHouseNativePrefabBuilder.HasExactWarehouseFloorPrefabContract(
                    warehouseFloor, false, out string warehouseFloorPrefabFailure))
                throw new InvalidDataException("Dependency bundle lost the exact non-primitive level11 warehouse floor prefab: " +
                                               warehouseFloorPrefabFailure + ".");
            VolumeProfile indoorProfile = dependency.LoadAsset<VolumeProfile>(KillHouseVanillaIndoorRenderBuilder.ProfileAssetPath);
            if (indoorProfile == null || !indoorProfile.TryGet(out Tonemapping tonemapping) ||
                tonemapping.mode.value != TonemappingMode.External ||
                !(tonemapping.lutTexture.value is Texture3D lut) || lut.width != 32 || lut.height != 32 ||
                lut.depth != 32 || !string.Equals(lut.name, "AgX - PunchyPowerfulMix", StringComparison.Ordinal))
                throw new InvalidDataException("Dependency bundle cannot resolve the audited indoor profile's native 32x32x32 AgX LUT.");
        }
        finally
        {
            scenes.Unload(false);
            dependency.Unload(false);
        }
    }

    private static void WriteReport(JObject validation, AssetBundleManifest manifest, string[] assets,
        string dependencyPath, string scenePath)
    {
        string[] sceneDependencies = manifest.GetAllDependencies(SceneBundleName);
        if (sceneDependencies.Length != 1 || !string.Equals(sceneDependencies[0], DependencyBundleName, StringComparison.Ordinal))
            throw new InvalidDataException("Scene bundle dependency closure changed: " + string.Join(", ", sceneDependencies));
        JObject report = new JObject
        {
            ["schema"] = "vektor-killhouse/local-proof-bundle-build@1",
            ["generatedUtc"] = DateTime.UtcNow.ToString("O"),
            ["unityVersion"] = Application.unityVersion,
            ["buildTarget"] = BuildTarget.StandaloneWindows64.ToString(),
            ["sceneCount"] = ScenePaths.Length,
            ["scenePaths"] = new JArray(ScenePaths),
            ["dependencyAssetCount"] = assets.Length,
            ["sceneBundleDependencies"] = new JArray(sceneDependencies),
            ["dependencyBundle"] = FileRecord(dependencyPath),
            ["sceneBundle"] = FileRecord(scenePath),
            ["bundleLoadVerified"] = true,
            ["aggregateSceneValidationGeneratedUtc"] = validation.Value<string>("generatedUtc"),
            ["randomSceneVariantDeclarationRequired"] = true,
            ["liveGameplayVerified"] = false,
            ["releaseAllowed"] = false
        };
        string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "evidence", "killhouse_bundle_build.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, report.ToString() + Environment.NewLine);
    }

    private static JObject FileRecord(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            return new JObject
            {
                ["path"] = path,
                ["bytes"] = stream.Length,
                ["sha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant()
            };
        }
    }
}
#endif
