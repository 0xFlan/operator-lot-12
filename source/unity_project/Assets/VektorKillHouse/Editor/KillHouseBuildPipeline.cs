#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class KillHouseBuildPipeline
{
    [MenuItem("Vektor Kill House/Build/Rebuild Everything Through Local Proof Bundles", priority = 1)]
    public static void RebuildEverything()
    {
        KillHouseGlbMeshImporter.ImportAll();
        KillHouseNativeMaterialBuilder.BuildAll();
        KillHouseNativePrefabBuilder.BuildAll();
        KillHouseDoorV2ShellBuilder.BuildAll();
        KillHouseVanillaIndoorRenderBuilder.Build();
        KillHouseVariantBuilder.BuildAll();
        KillHouseBundleBuilder.Build();
        Debug.Log("[Vektor Kill House] Full native-only ten-variant authoring pipeline completed. Live OPERATOR gameplay remains unverified.");
    }

    public static void RebuildEverythingBatch()
    {
        try
        {
            RebuildEverything();
            EditorApplication.Exit(0);
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    public static void RebuildScenesAndBundlesBatch()
    {
        try
        {
            // Scene-only source changes reuse the already validated native
            // mesh/material/prefab library. Rebuild all ten scenes and both
            // proof bundles without reimporting the proprietary source asset
            // closure or mutating any installed game file.
            KillHouseVariantBuilder.BuildAll();
            KillHouseBundleBuilder.Build();
            Debug.Log("[Vektor Kill House] Ten-scene and bundle-only rebuild completed.");
            EditorApplication.Exit(0);
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }
}
#endif
