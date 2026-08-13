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
}
#endif
