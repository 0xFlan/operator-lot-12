#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class KillHouseNativePrefabBuilder
{
    public const string PrefabFolder = "Assets/VektorKillHouse/Native/Prefabs";

    private sealed class BoxColliderProfile
    {
        public readonly Vector3 Center;
        public readonly Vector3 Size;
        public readonly bool Enabled;

        public BoxColliderProfile(Vector3 center, Vector3 size, bool enabled = true)
        {
            Center = center;
            Size = size;
            Enabled = enabled;
        }
    }

    private sealed class ChildDefinition
    {
        public readonly string Name;
        public readonly string Mesh;
        public readonly string[] Materials;
        public readonly Vector3 LocalPosition;
        public readonly Quaternion LocalRotation;
        public readonly Vector3 LocalScale;
        public readonly bool Active;
        public readonly string Collision;
        public readonly bool CollisionEnabled;
        public readonly bool CollisionConvex;
        public readonly BoxColliderProfile[] BoxColliders;
        public readonly ChildDefinition[] Children;
        public readonly int VertexCount;
        public readonly int IndexCount;
        public readonly int Layer;
        public readonly string InstalledProvenance;

        public ChildDefinition(string name, string mesh, string[] materials, Vector3 localPosition,
            Quaternion localRotation, Vector3 localScale, string collision = null,
            bool collisionEnabled = true, bool collisionConvex = false,
            BoxColliderProfile[] boxColliders = null, ChildDefinition[] children = null,
            int vertexCount = 0, int indexCount = 0, int layer = 0,
            string installedProvenance = null)
        {
            Name = name;
            Mesh = mesh;
            Materials = materials ?? Array.Empty<string>();
            LocalPosition = localPosition;
            LocalRotation = localRotation;
            LocalScale = localScale;
            Active = true;
            Collision = collision;
            CollisionEnabled = collisionEnabled;
            CollisionConvex = collisionConvex;
            BoxColliders = boxColliders ?? Array.Empty<BoxColliderProfile>();
            Children = children ?? Array.Empty<ChildDefinition>();
            VertexCount = vertexCount;
            IndexCount = indexCount;
            Layer = layer;
            InstalledProvenance = installedProvenance ?? string.Empty;
        }
    }

    private sealed class Definition
    {
        public readonly string Mesh;
        public readonly string Collision;
        public readonly string[] Materials;
        public readonly bool Collider;
        public readonly bool CollisionConvex;
        public readonly BoxColliderProfile[] BoxColliders;
        public readonly ChildDefinition[] Children;
        public readonly string RootMeshFolder;
        public readonly int VertexCount;
        public readonly int IndexCount;
        public readonly int Layer;
        public readonly string InstalledProvenance;

        public Definition(string mesh, string[] materials, string collision = null, bool collider = true,
            bool collisionConvex = false, BoxColliderProfile[] boxColliders = null,
            ChildDefinition[] children = null, string rootMeshFolder = "Residential",
            int vertexCount = 0, int indexCount = 0, int layer = 0,
            string installedProvenance = null)
        {
            Mesh = mesh;
            Materials = materials;
            Collision = collision;
            Collider = collider;
            CollisionConvex = collisionConvex;
            BoxColliders = boxColliders ?? Array.Empty<BoxColliderProfile>();
            Children = children ?? Array.Empty<ChildDefinition>();
            RootMeshFolder = rootMeshFolder;
            VertexCount = vertexCount;
            IndexCount = indexCount;
            Layer = layer;
            InstalledProvenance = installedProvenance ?? string.Empty;
        }
    }

    private static BoxColliderProfile Box(Vector3 center, Vector3 size, bool enabled = true)
    {
        return new BoxColliderProfile(center, size, enabled);
    }

    private static ChildDefinition ChildMesh(string name, string mesh, string[] materials,
        Vector3 localPosition, Quaternion localRotation, string collision = null,
        bool collisionEnabled = true, bool collisionConvex = false,
        BoxColliderProfile[] boxColliders = null, ChildDefinition[] children = null,
        int vertexCount = 0, int indexCount = 0, int layer = 0,
        string installedProvenance = null)
    {
        return new ChildDefinition(name, mesh, materials, localPosition, localRotation, Vector3.one,
            collision, collisionEnabled, collisionConvex, boxColliders, children, vertexCount,
            indexCount, layer, installedProvenance);
    }

    private static ChildDefinition TransformChild(string name, Vector3 localPosition,
        Quaternion localRotation, int layer, string installedProvenance)
    {
        return new ChildDefinition(name, null, Array.Empty<string>(), localPosition, localRotation,
            Vector3.one, layer: layer, installedProvenance: installedProvenance);
    }

    private static readonly Definition[] Definitions =
    {
        new Definition("Wall2Meter", new[] { "ChipBoardShader", "PlyWoodShader" }),
        new Definition("Wall2MeterDoor", new[] { "ChipBoardShader", "PlyWoodShader" }, "Wall2MeterDoorCollision"),
        new Definition("Wall2MeterWindow", new[] { "ChipBoardShader", "PlyWoodShader" }, "Wall2MeterWindowCollision"),
        new Definition("Wall4MeterWindow", new[] { "ChipBoardShader", "PlyWoodShader" }, "Wall4MeterWindowCollision"),
        // Exact active child order and local state from installed level4 GO360 on the pinned build.
        // Only Transform/MeshFilter/MeshRenderer/Collider state is retained; every shipped
        // MonoBehaviour is deliberately stripped because its gameplay dependency closure is not transported.
        new Definition("Bed_queen", new[] { "Bed" }, "Bed_COL", children: new[]
        {
            ChildMesh("Pillow_small", "Pillow_small", new[] { "PillowSmall" },
                new Vector3(.307000011f, .707000017f, -.644999981f),
                new Quaternion(.251538515f, -.058406256f, -.012960499f, .965996504f),
                "PillowSmall_COL", vertexCount: 233, indexCount: 768,
                installedProvenance: "level4_GO358_T7948_MF20236_MR15443_MC25210_mesh747_mat96_col1009"),
            ChildMesh("Pillow_small", "Pillow_small", new[] { "PillowSmall" },
                new Vector3(-.180000007f, .707000017f, -.649999976f),
                new Quaternion(.317480564f, .097556531f, -.053890042f, .941692472f),
                "PillowSmall_COL", vertexCount: 233, indexCount: 768,
                installedProvenance: "level4_GO357_T7947_MF20234_MR15444_MC25212_mesh747_mat96_col1009"),
            ChildMesh("Pillow_large", "Pillow_large", new[] { "PillowLarge" },
                new Vector3(-.309999943f, .608999968f, -.897999763f),
                new Quaternion(.063810684f, -.005018107f, -.022382082f, .997698367f),
                "PillowLarge_COL", vertexCount: 334, indexCount: 1332,
                installedProvenance: "level4_GO356_T7945_MF20232_MR15440_MC25211_mesh769_mat95_col1010"),
            ChildMesh("Pillow_large", "Pillow_large", new[] { "PillowLarge" },
                new Vector3(.404000014f, .621000051f, -.85799998f),
                new Quaternion(.063628756f, .006954471f, -.012878145f, .997866333f),
                "PillowLarge_COL", vertexCount: 334, indexCount: 1332,
                installedProvenance: "level4_GO359_T7946_MF20233_MR15442_MC25209_mesh769_mat95_col1010")
        }),
        new Definition("Book_set_bookshelf_A", new[] { "Books" }, collider: false),
        new Definition("Bookshelf", new[] { "Fireplace" }, "BookShelf_COL"),
        new Definition("Carpet_hallway", new[] { "Carpet_B" }, collider: false),
        // Complete active residential couch root from installed Suburb Day level4 GO578.
        // The renderer/collider subtree is root-only. MR15599 uses the otherwise renderer-free
        // GO2175/T9763 child as its explicit probe anchor, so the active Transform is retained
        // exactly while PolyFewHost and the root Surface behaviours are deliberately stripped.
        new Definition("Couch_2seat", new[] { "Couch_Fabric" }, boxColliders: new[]
        {
            Box(new Vector3(.0015151650877669454f, .23126330971717834f, .056184977293014526f),
                new Vector3(1.5813075304031372f, .46063679456710815f, .8079850673675537f)),
            Box(new Vector3(.0015150876715779305f, .4757115840911865f, -.23268039524555206f),
                new Vector3(1.5813075304031372f, .9495333433151245f, .23025444149971008f)),
            Box(new Vector3(.7339684963226318f, .3626925051212311f, .05618477985262871f),
                new Vector3(.11640128493309021f, .7234951853752136f, .8079850673675537f)),
            Box(new Vector3(-.7227199077606201f, .3560544550418854f, .056185171008110046f),
                new Vector3(.13283774256706238f, .7102190852165222f, .8079850673675537f))
        }, children: new[]
        {
            TransformChild("GameObject", new Vector3(-0f, 1.3220000267028809f, -.3490000069141388f),
                Quaternion.identity, 24,
                "level4_GO2175_T9763_layer24_active_MR15599_probeAnchor_MB34004_PolyFewHost_stripped")
        }, rootMeshFolder: "ResidentialComplete", vertexCount: 844, indexCount: 2286,
            layer: 24,
            installedProvenance:
                "level4_GO578_T8167_MF20391_MR15599_sharedassets3_Mesh962_sharedassets2_Mat174_layer24_scale1"),
        new Definition("D_TV_standing", new[] { "Devices_On" }, collider: false),
        new Definition("Door_interior", new[] { "Door_White" }),
        new Definition("SM_Door_2_LOD0", new[] { "MI_DoorsWindows" }, collider: false),
        new Definition("Floor_12x_8x", new[] { "In_Floor_Carpet" }),
        new Definition("Floor_5x_5x", new[] { "In_Floor_Basement" }),
        new Definition("Kitcabinet_full_fridge", new[] { "Kitchen_Cabinet_Wood" }, boxColliders: new[]
        {
            Box(new Vector3(.4f, 1.1859446f, .3384462f), new Vector3(.8f, 2.3829341f, .6791087f)),
            Box(new Vector3(1.48f, 1.1859446f, .3384462f), new Vector3(.1f, 2.3829341f, .6791087f)),
            Box(new Vector3(.7495269f, 2.1400001f, .3384462f), new Vector3(1.6002039f, .47f, .6791087f))
        }),
        // Exact level4 renderer order: the cabinet body is wood in slot 0 and the worktop is marble in slot 1.
        new Definition("Kitcabinet_low_1x_A", new[] { "Kitchen_Cabinet_Wood", "Kitchen_Cabinet_Marble" },
            boxColliders: new[]
            {
                Box(new Vector3(.50000006f, .45941916f, .33795837f),
                    new Vector3(1.00000036f, .92988235f, .67591673f))
            }),
        new Definition("Kitchen_table_large", new[] { "Kitchen_TableChair" }, boxColliders: new[]
        {
            Box(new Vector3(0f, .36249366f, -2.9802322e-08f), new Vector3(.2f, .7262458f, .2f)),
            Box(new Vector3(0f, .68f, -2.9802322e-08f), new Vector3(1f, .07f, 1.75f))
        }),
        new Definition("Lamp_bedroom", new[] { "Lamps_House_Off" }, collider: false),
        new Definition("Lamp_ceiling_circle", new[] { "Lamps_House_Off" }, collider: false),
        new Definition("Lamp_kitchen_A", new[] { "Lamps_House_Off" }, collider: false),
        new Definition("Lamp_fluorescent_B", new[] { "Lamps_C_on _cagville" }, collider: false),
        new Definition("Base Warehouse", new[] { "RM Steel smooth" }),
        new Definition("OverHead Support", new[] { "RM Steel smooth" }),
        new Definition("Roof", new[] { "RM Steel smooth" }),
        new Definition("Support 2", new[] { "RM Steel smooth" }),
        new Definition("Floor", new[] { "Floor" }),
        new Definition("Sidetable_A", new[] { "Bedroom_Closets" }, "SideTable_A_COL", collisionConvex: true,
            children: new[]
            {
                ChildMesh("Sidetable_A_drawer", "Sidetable_A_drawer", new[] { "Bedroom_Closets" },
                    new Vector3(-.000006561279f, .549451709f, .02767208f), Quaternion.identity,
                    "Drawer_SideTableA_COL", collisionEnabled: false, collisionConvex: true,
                    vertexCount: 439, indexCount: 1068,
                    installedProvenance: "level4_GO1737_T9325_MF21215_MR16423_MC25380_mesh1111_mat171_col1022")
            }),
        new Definition("Sofa_A", new[] { "Sofa_House" }, collider: false),
        new Definition("Sofa_B", new[] { "Sofa_House" }, collider: false),
        new Definition("T_bathtub", new[] { "Toilet_House" }, collider: false),
        new Definition("T_sink", new[] { "Bedroom_Closets", "Toilet_House" }, "T_Sink_COL", children: new[]
        {
            ChildMesh("T_sink_door_L (1)", "T_sink_door_L", new[] { "Bedroom_Closets" },
                new Vector3(.466553777f, .466425627f, .531496406f),
                new Quaternion(0f, .000000238419f, 0f, 1f), boxColliders: new[]
                {
                    Box(new Vector3(-.230998382f, 0f, .014692759f),
                        new Vector3(.470443398f, .684271514f, .050654199f))
                }, vertexCount: 290, indexCount: 690,
                installedProvenance: "level4_GO2410_T9997_MF21715_MR16923_BC27393_mesh660_mat171"),
            ChildMesh("T_sink_door_R (1)", "T_sink_door_R", new[] { "Bedroom_Closets" },
                new Vector3(-.469173878f, .466425627f, .531496406f),
                new Quaternion(0f, .000000238419f, .000000087423f, 1f), boxColliders: new[]
                {
                    Box(new Vector3(.232103765f, .000000461936f, .014692919f),
                        new Vector3(.470443428f, .684271574f, .050654192f))
                }, vertexCount: 282, indexCount: 690,
                installedProvenance: "level4_GO2074_T9662_MF21468_MR16676_BC27149_mesh1112_mat171")
        }),
        new Definition("T_toilet", new[] { "Toilet_House" }, boxColliders: new[]
        {
            Box(new Vector3(-3.7252903e-08f, .22f, -.006511614f),
                new Vector3(.39312702f, .44f, .61581385f)),
            Box(new Vector3(-3.7252903e-08f, .3703465f, -.25f),
                new Vector3(.39312702f, .742007f, .13f))
        }, children: new[]
        {
            ChildMesh("T_toilet_lid (1)", "T_toilet_lid", new[] { "Toilet_House" },
                new Vector3(-.000156097f, .409894168f, -.105191417f),
                new Quaternion(-.725540996f, 0f, .000000029802f, .688179016f),
                boxColliders: new[]
                {
                    Box(new Vector3(.001000941f, .017943554f, .184148327f),
                        new Vector3(.355356365f, .022520866f, .425430149f))
                }, children: new[]
                {
                    ChildMesh("T_toilet_seat (1)", "T_toilet_seat", new[] { "Toilet_House" },
                        Vector3.zero,
                        new Quaternion(.005043178f, -.0000000195f, -.000000063864f, .999987304f),
                        boxColliders: new[]
                        {
                            Box(new Vector3(0f, -.015444206f, .183325782f),
                                new Vector3(.360641479f, .052449051f, .430035204f))
                        }, vertexCount: 248, indexCount: 1116,
                        installedProvenance: "level4_GO2824_T13183_MF24205_MR19230_BC28939_mesh1076_mat111")
                }, vertexCount: 210, indexCount: 1008,
                installedProvenance: "level4_GO5966_T10254_MF22678_MR17289_BC29156_mesh753_mat111")
        }),
        new Definition("Workdesk_solo", new[] { "WorkDesk" }, "WorkDesk_Solo_COL", children: new[]
        {
            ChildMesh("Workdesk_door_L", "Workdesk_door_L", new[] { "WorkDesk" },
                new Vector3(-.834760129f, .409088165f, .457503468f), Quaternion.identity,
                boxColliders: new[]
                {
                    Box(new Vector3(.205938876f, 0f, .000000004657f),
                        new Vector3(.410812676f, .550970018f, .04496894f))
                }, vertexCount: 158, indexCount: 300,
                installedProvenance: "level4_GO2399_T9988_MF21705_MR16912_BC27382_mesh680_mat188"),
            ChildMesh("Workdesk_door_R", "Workdesk_door_R", new[] { "WorkDesk" },
                new Vector3(.831830859f, .409088165f, .457503468f), Quaternion.identity,
                boxColliders: new[]
                {
                    Box(new Vector3(-.204264224f, 0f, .000000004657f),
                        new Vector3(.410812676f, .550970018f, .04496894f))
                }, vertexCount: 158, indexCount: 300,
                installedProvenance: "level4_GO2400_T9986_MF21704_MR16913_BC27383_mesh1134_mat188")
        })
    };

    private static readonly HashSet<string> FurnitureMeshes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Bed_queen", "Book_set_bookshelf_A", "Bookshelf", "Couch_2seat", "D_TV_standing",
        "Kitcabinet_full_fridge", "Kitcabinet_low_1x_A", "Kitchen_table_large", "Sidetable_A",
        "Sofa_A", "Sofa_B", "T_bathtub", "T_sink", "T_toilet", "Workdesk_solo"
    };

    // Real vanilla meshes whose complete active parent/child assembly is not transported. Keep their
    // identities for fail-closed scans, but delete/refuse the old flattened prefab representation.
    private static readonly HashSet<string> ForbiddenFlattenedFurnitureMeshes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Book_set_bookshelf_A", "D_TV_standing", "Sofa_A", "Sofa_B", "T_bathtub"
        };

    private static readonly HashSet<string> FurnitureHierarchyMeshes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Pillow_small", "Pillow_large", "Sidetable_A_drawer", "T_sink_door_L", "T_sink_door_R",
        "T_toilet_lid", "T_toilet_seat", "Workdesk_door_L", "Workdesk_door_R"
    };

    // Only complete/proven standalone roots may be explicit dependency-bundle furniture assets.
    // The remaining FurnitureMeshes stay recognizable so scene validation can reject any accidental
    // placement, but are not release dependencies.
    private static readonly HashSet<string> ReleaseFurnitureMeshes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Bed_queen", "Bookshelf", "Couch_2seat", "Kitcabinet_full_fridge", "Kitcabinet_low_1x_A",
        "Kitchen_table_large", "Sidetable_A", "T_sink", "T_toilet", "Workdesk_solo"
    };

    // SHA-256 over UV0 followed by UV1, with each Vector2 written as little-endian IEEE-754
    // x/y in vertex order. These fingerprints are derived from the pinned installed level4
    // meshes after reversing only AssetRipper's direct-root glTF V inversion. The separately
    // transported active child meshes already match their installed UV streams and are gated by
    // their own exact channel/count hierarchy contract.
    private static readonly IReadOnlyDictionary<string, string> InstalledRootFurnitureUvFingerprints =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Bed_queen"] = "92A1DB2B2570182C9F01D301078D2954102660E139E2E1583B6F9829053F8C0C",
            ["Bookshelf"] = "FA5C11FA072BD32385EB46A1AACE17A82F8BA0AE3278EB7B4A113AA6FAAEF961",
            ["Kitcabinet_full_fridge"] = "64A0C1C0128684454E3A6629053B8478FE332FDAC37F3EC479E340AE2A883A17",
            ["Kitcabinet_low_1x_A"] = "FE201FD101273B1F5AE11508BA130A735495909ACAAA138475BB3F6CE6272D24",
            ["Kitchen_table_large"] = "425779D06FD5E61A6C9C4C83359DBF35D81337C963F68B7BA46B704D8A069538",
            ["Sidetable_A"] = "E134FAA0ABEDA304569700A9E19F3D6281410C7E2D902075D47C7D53C120A973",
            ["T_sink"] = "6A2A8DABBDB26414FAF8C77E082BCDD71CAB06E4EDDE21A4AF693CBA8ED3DEF5",
            ["T_toilet"] = "703B46CFB09A514E29DBDCE80BC8E1ACD0473072B8C8B88B37E94963EC68F3A1",
            ["Workdesk_solo"] = "0C633ABDB08EEF54DA371F29B931F8DE81BA3D0139F10379B0C58F0AEC65A1D2",
            ["Couch_2seat"] = "CF3DB25EA907E15EF421DE6FC1D68C7031D0DA923DBF529383087B2DA55B6171"
        };

    // SHA-256 over installed POSITION/NORMAL/TANGENT/UV0/UV1/COLOR_0 followed by
    // submesh count, per-submesh index count, and uint32 indices. This closes the
    // complete newly transported couch, rather than accepting only bounds/counts.
    private static readonly IReadOnlyDictionary<string, string> InstalledRootFurnitureMeshFingerprints =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Couch_2seat"] = "C58D40C40D6B9F18BE6A883EA69581002C005630DF1194206A638896EF483586"
        };

    public const string CouchFrontMarkerName = "NATIVE_FURNITURE_FRONT_LOCAL_POSITIVE_Z";
    public const string CouchProvenanceMarkerName =
        "NATIVE_FURNITURE_PROVENANCE_level4_GO578_Couch_2seat_Mesh962_Mat174";
    public const string CouchProbeAnchorChildName = "GameObject";

    [MenuItem("Vektor Kill House/Native/Rebuild Native Prefabs", priority = 12)]
    public static void BuildAll()
    {
        EnsureFolder(PrefabFolder);
        int built = 0;
        foreach (Definition definition in Definitions)
        {
            if (ForbiddenFlattenedFurnitureMeshes.Contains(definition.Mesh))
            {
                AssetDatabase.DeleteAsset(PrefabPath(definition.Mesh));
                continue;
            }
            Build(definition);
            built++;
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("[Vektor Kill House] Rebuilt " + built + " native-only prefabs and refused " +
                  ForbiddenFlattenedFurnitureMeshes.Count + " incomplete flattened furniture families.");
    }

    public static GameObject Load(string meshName)
    {
        if (ForbiddenFlattenedFurnitureMeshes.Contains(meshName ?? string.Empty))
            throw new InvalidDataException(meshName +
                " is a recognition-only vanilla submesh and has no authorized flattened prefab.");
        string path = PrefabPath(meshName);
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) throw new FileNotFoundException("Native prefab has not been built: " + path);
        return prefab;
    }

    public static string PrefabPath(string meshName) => PrefabFolder + "/PF_NATIVE_" + Sanitize(meshName) + ".prefab";

    private const string WarehouseFloorMeshAssetPath =
        "Assets/VektorKillHouse/Native/WarehousePvp/Meshes/Generated/Floor.asset";
    private const float WarehouseFloorSourceWidth = 5.425254f;
    private const float WarehouseFloorSourceDepth = 4.129904f;

    private static void Build(Definition definition)
    {
        Mesh mesh = FindGeneratedMesh(definition.Mesh);
        GameObject root = new GameObject("PF_NATIVE_" + Sanitize(definition.Mesh));
        try
        {
            root.layer = definition.Layer;
            MeshFilter filter = root.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = root.AddComponent<MeshRenderer>();
            Material[] sourceMaterials = definition.Materials.Select(KillHouseNativeMaterialBuilder.Load).ToArray();
            Material[] assigned = new Material[Math.Max(1, mesh.subMeshCount)];
            for (int i = 0; i < assigned.Length; i++)
                assigned[i] = sourceMaterials[Math.Min(i, sourceMaterials.Length - 1)];
            renderer.sharedMaterials = assigned;

            if (definition.Collider)
            {
                if (definition.BoxColliders.Length > 0)
                {
                    foreach (BoxColliderProfile profile in definition.BoxColliders)
                    {
                        BoxCollider collider = root.AddComponent<BoxCollider>();
                        collider.enabled = profile.Enabled;
                        collider.isTrigger = false;
                        collider.sharedMaterial = null;
                        collider.center = profile.Center;
                        collider.size = profile.Size;
                        if (string.Equals(definition.Mesh, "Couch_2seat", StringComparison.Ordinal))
                            ApplyExactCouchColliderAuxiliaryState(collider);
                    }
                }
                else
                {
                    MeshCollider collider = root.AddComponent<MeshCollider>();
                    collider.sharedMesh = string.IsNullOrEmpty(definition.Collision)
                        ? mesh
                        : FindGeneratedMesh(definition.Collision);
                    collider.enabled = true;
                    collider.isTrigger = false;
                    collider.sharedMaterial = null;
                    collider.convex = definition.CollisionConvex;
                    collider.cookingOptions = (MeshColliderCookingOptions)30;
                }
            }

            foreach (ChildDefinition child in definition.Children) BuildChild(root.transform, child);
            if (string.Equals(definition.Mesh, "Couch_2seat", StringComparison.Ordinal))
            {
                if (root.transform.childCount != 1 ||
                    !string.Equals(root.transform.GetChild(0).name, CouchProbeAnchorChildName,
                        StringComparison.Ordinal))
                    throw new InvalidDataException("Couch_2seat retained probe-anchor child is missing.");
                ApplyExactCouchRendererState(renderer, root.transform.GetChild(0));
                AddFurnitureMetadataMarker(root.transform, CouchFrontMarkerName, Vector3.forward,
                    definition.Layer);
                AddFurnitureMetadataMarker(root.transform, CouchProvenanceMarkerName, Vector3.zero,
                    definition.Layer);
            }

            string path = PrefabPath(definition.Mesh);
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            if (saved == null) throw new IOException("Could not save native prefab " + path + ".");
            if (FurnitureMeshes.Contains(definition.Mesh) &&
                !HasExactFurniturePrefabContract(saved, out string furnitureFailure))
                throw new InvalidDataException("Native furniture prefab lost its vanilla renderer closure: " +
                                               definition.Mesh + "; " + furnitureFailure + ".");
            if (string.Equals(definition.Mesh, "Floor", StringComparison.Ordinal) &&
                !HasExactWarehouseFloorPrefabContract(saved, true, out string warehouseFloorFailure))
                throw new InvalidDataException("Native warehouse floor prefab lost its exact level11 donor closure: " +
                                               warehouseFloorFailure + ".");
            AssetDatabase.SetLabels(saved, new[]
            {
                "vektor-killhouse", "operator-native-reconstruction", "no-authored-primitive", "source-mesh-" + Sanitize(definition.Mesh)
            });
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static void AddFurnitureMetadataMarker(Transform parent, string name, Vector3 localPosition,
        int layer)
    {
        GameObject marker = new GameObject(name) { layer = layer };
        marker.transform.SetParent(parent, false);
        marker.transform.localPosition = localPosition;
        marker.transform.localRotation = Quaternion.identity;
        marker.transform.localScale = Vector3.one;
    }

    private static void ApplyExactCouchRendererState(MeshRenderer renderer, Transform probeAnchor)
    {
        if (renderer == null || probeAnchor == null)
            throw new InvalidDataException("Couch_2seat renderer/probe-anchor contract is incomplete.");
        var serialized = new SerializedObject(renderer);
        serialized.Update();
        SetRequiredBool(serialized, "m_Enabled", true);
        SetRequiredInt(serialized, "m_CastShadows", 1);
        SetRequiredBool(serialized, "m_ReceiveShadows", true);
        SetRequiredBool(serialized, "m_DynamicOccludee", true);
        SetRequiredBool(serialized, "m_StaticShadowCaster", false);
        SetRequiredInt(serialized, "m_MotionVectors", 1);
        SetRequiredInt(serialized, "m_LightProbeUsage", 1);
        SetRequiredInt(serialized, "m_ReflectionProbeUsage", 1);
        SetRequiredInt(serialized, "m_RayTracingMode", 2);
        SetRequiredBool(serialized, "m_RayTraceProcedural", false);
        SetRequiredInt(serialized, "m_RayTracingAccelStructBuildFlagsOverride", 0);
        SetRequiredInt(serialized, "m_RayTracingAccelStructBuildFlags", 1);
        SetRequiredBool(serialized, "m_SmallMeshCulling", true);
        SetRequiredInt(serialized, "m_ForceMeshLod", -1);
        SetRequiredFloat(serialized, "m_MeshLodSelectionBias", 0f);
        SetRequiredInt(serialized, "m_RenderingLayerMask", 257);
        SetRequiredInt(serialized, "m_RendererPriority", 0);
        SetRequiredInt(serialized, "m_SortingLayerID", 0);
        SetRequiredInt(serialized, "m_SortingLayer", 0);
        SetRequiredInt(serialized, "m_SortingOrder", 0);
        SetRequiredInt(serialized, "m_MaskInteraction", 0);
        SetRequiredObject(serialized, "m_AdditionalVertexStreams", null);
        SetRequiredObject(serialized, "m_LightProbeVolumeOverride", null);
        SetRequiredObject(serialized, "m_ProbeAnchor", probeAnchor);
        SetRequiredObject(serialized, "m_StaticBatchRoot", null);
        SetRequiredInt(serialized, "m_StaticBatchInfo.firstSubMesh", 0);
        SetRequiredInt(serialized, "m_StaticBatchInfo.subMeshCount", 0);
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(renderer);
    }

    private static void ApplyExactCouchColliderAuxiliaryState(BoxCollider collider)
    {
        var serialized = new SerializedObject(collider);
        serialized.Update();
        SetRequiredObject(serialized, "m_Material", null);
        SetRequiredInt(serialized, "m_IncludeLayers.m_Bits", 0);
        SetRequiredInt(serialized, "m_ExcludeLayers.m_Bits", 0);
        SetRequiredInt(serialized, "m_LayerOverridePriority", 0);
        SetRequiredBool(serialized, "m_IsTrigger", false);
        SetRequiredBool(serialized, "m_ProvidesContacts", false);
        SetRequiredBool(serialized, "m_Enabled", true);
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(collider);
    }

    private static SerializedProperty RequiredProperty(SerializedObject serialized, string path)
    {
        SerializedProperty property = serialized.FindProperty(path);
        if (property == null)
            throw new InvalidDataException("Unity serialization no longer exposes required native field " +
                                           path + " on " + serialized.targetObject.GetType().Name + ".");
        return property;
    }

    private static void SetRequiredBool(SerializedObject serialized, string path, bool value) =>
        RequiredProperty(serialized, path).boolValue = value;

    private static void SetRequiredInt(SerializedObject serialized, string path, int value) =>
        RequiredProperty(serialized, path).intValue = value;

    private static void SetRequiredFloat(SerializedObject serialized, string path, float value) =>
        RequiredProperty(serialized, path).floatValue = value;

    private static void SetRequiredObject(SerializedObject serialized, string path,
        UnityEngine.Object value) => RequiredProperty(serialized, path).objectReferenceValue = value;

    private static void BuildChild(Transform parent, ChildDefinition definition)
    {
        GameObject child = new GameObject(definition.Name) { layer = definition.Layer };
        child.transform.SetParent(parent, false);
        child.transform.localPosition = definition.LocalPosition;
        child.transform.localRotation = definition.LocalRotation;
        child.transform.localScale = definition.LocalScale;

        if (!string.IsNullOrEmpty(definition.Mesh))
        {
            Mesh mesh = FindGeneratedMesh(definition.Mesh);
            MeshFilter filter = child.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = child.AddComponent<MeshRenderer>();
            Material[] sourceMaterials = definition.Materials.Select(KillHouseNativeMaterialBuilder.Load)
                .ToArray();
            Material[] assigned = new Material[Math.Max(1, mesh.subMeshCount)];
            for (int index = 0; index < assigned.Length; index++)
                assigned[index] = sourceMaterials[Math.Min(index, sourceMaterials.Length - 1)];
            renderer.sharedMaterials = assigned;

            if (definition.BoxColliders.Length > 0)
            {
                foreach (BoxColliderProfile profile in definition.BoxColliders)
                {
                    BoxCollider collider = child.AddComponent<BoxCollider>();
                    collider.enabled = profile.Enabled;
                    collider.isTrigger = false;
                    collider.sharedMaterial = null;
                    collider.center = profile.Center;
                    collider.size = profile.Size;
                }
            }
            else if (!string.IsNullOrEmpty(definition.Collision))
            {
                MeshCollider collider = child.AddComponent<MeshCollider>();
                collider.sharedMesh = FindGeneratedMesh(definition.Collision);
                collider.enabled = definition.CollisionEnabled;
                collider.isTrigger = false;
                collider.sharedMaterial = null;
                collider.convex = definition.CollisionConvex;
                collider.cookingOptions = (MeshColliderCookingOptions)30;
            }
        }
        else if (definition.Materials.Length != 0 || !string.IsNullOrEmpty(definition.Collision) ||
                 definition.BoxColliders.Length != 0)
        {
            throw new InvalidDataException("Transform-only native child has visual/collider state: " +
                                           definition.Name + ".");
        }

        foreach (ChildDefinition nested in definition.Children) BuildChild(child.transform, nested);
        child.SetActive(definition.Active);
    }

    public static bool HasExactFurniturePrefabContract(GameObject prefab, out string failure,
        bool requireAssetDatabaseIdentity = true, bool requireUnitRootScale = true)
    {
        failure = string.Empty;
        if (prefab == null)
        {
            failure = "prefab-null";
            return false;
        }
        MeshFilter rootFilter = prefab.GetComponent<MeshFilter>();
        if (rootFilter == null || rootFilter.sharedMesh == null)
        {
            failure = "root-visual-mesh-missing";
            return false;
        }
        Mesh mesh = rootFilter.sharedMesh;
        Definition definition = Definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Mesh, mesh.name, StringComparison.Ordinal));
        if (definition == null || !FurnitureMeshes.Contains(definition.Mesh))
        {
            failure = "unknown-furniture-mesh:" + mesh.name;
            return false;
        }
        if (ForbiddenFlattenedFurnitureMeshes.Contains(definition.Mesh))
        {
            failure = "forbidden-flattened-family:" + definition.Mesh;
            return false;
        }
        if (prefab.layer != definition.Layer ||
            (requireUnitRootScale && !Approximately(prefab.transform.localScale, Vector3.one, 1e-6f)))
        {
            failure = "root-layer-or-scale:" + prefab.layer + "/" +
                      prefab.transform.localScale.ToString("F7") + "/expected-layer-" + definition.Layer;
            return false;
        }
        if (!HasExactFurnitureVisualNode(prefab, definition.Mesh, definition.Materials,
                RootFurnitureMeshAssetPath(definition), expectedUvChannels: 2, expectedColor: true,
                expectedVertexCount: definition.VertexCount, expectedIndexCount: definition.IndexCount,
                requireAssetDatabaseIdentity, out failure))
            return false;
        if (!HasExactColliderContract(prefab, definition, mesh, requireAssetDatabaseIdentity,
                out string colliderFailure))
        {
            failure = "root-collider:" + colliderFailure;
            return false;
        }
        if (!HasExactActiveChildHierarchy(prefab.transform, definition.Children, true,
                requireAssetDatabaseIdentity,
                out string hierarchyFailure))
        {
            failure = "hierarchy:" + hierarchyFailure;
            return false;
        }
        if (!HasOnlyRetainedFurnitureComponents(prefab, out string componentFailure))
        {
            failure = "stripped-component-policy:" + componentFailure;
            return false;
        }
        if (!HasExactRootFurnitureMetadata(prefab.transform, definition, out string metadataFailure))
        {
            failure = "metadata:" + metadataFailure;
            return false;
        }
        int expectedRenderNodes = 1 + definition.Children.Sum(CountChildRenderNodes);
        if (prefab.GetComponentsInChildren<MeshFilter>(true).Length != expectedRenderNodes ||
            prefab.GetComponentsInChildren<MeshRenderer>(true).Length != expectedRenderNodes)
        {
            failure = "render-node-count:" + prefab.GetComponentsInChildren<MeshFilter>(true).Length + "/" +
                      prefab.GetComponentsInChildren<MeshRenderer>(true).Length + "/expected-" +
                      expectedRenderNodes;
            return false;
        }
        return true;
    }

    private static bool HasExactFurnitureVisualNode(GameObject node, string meshName, string[] materialNames,
        string expectedMeshAssetPath, int expectedUvChannels, bool expectedColor,
        int expectedVertexCount, int expectedIndexCount, bool requireAssetDatabaseIdentity,
        out string failure)
    {
        failure = string.Empty;
        MeshFilter[] filters = node.GetComponents<MeshFilter>();
        MeshRenderer[] renderers = node.GetComponents<MeshRenderer>();
        if (filters.Length != 1 || renderers.Length != 1 || filters[0].sharedMesh == null)
        {
            failure = "filter-renderer-count:" + filters.Length + "/" + renderers.Length;
            return false;
        }
        Mesh mesh = filters[0].sharedMesh;
        MeshRenderer renderer = renderers[0];
        if (!string.Equals(mesh.name, meshName, StringComparison.Ordinal) || !renderer.enabled)
        {
            failure = "mesh-or-renderer-state:" + mesh.name + "/" + renderer.enabled;
            return false;
        }
        if (string.Equals(meshName, "Couch_2seat", StringComparison.Ordinal) &&
            !HasExactCouchRendererState(renderer, out string rendererFailure))
        {
            failure = "installed-renderer-state:" + rendererFailure;
            return false;
        }
        if (requireAssetDatabaseIdentity &&
            !string.Equals(AssetDatabase.GetAssetPath(mesh), expectedMeshAssetPath, StringComparison.Ordinal))
        {
            failure = "mesh-asset-path:" + AssetDatabase.GetAssetPath(mesh) + "/expected-" +
                      expectedMeshAssetPath;
            return false;
        }
        if (expectedVertexCount > 0 && mesh.vertexCount != expectedVertexCount)
        {
            failure = "vertex-count:" + mesh.vertexCount + "/expected-" + expectedVertexCount;
            return false;
        }
        if (expectedIndexCount > 0 && (mesh.subMeshCount != 1 || mesh.GetIndexCount(0) != expectedIndexCount))
        {
            failure = "index-count:" + (mesh.subMeshCount == 0 ? 0 : mesh.GetIndexCount(0)) +
                      "/expected-" + expectedIndexCount;
            return false;
        }
        Material[] slots = renderer.sharedMaterials;
        if (mesh.subMeshCount <= 0 || slots.Length != mesh.subMeshCount || materialNames.Length != slots.Length)
        {
            failure = "submesh-slot-count:" + mesh.subMeshCount + "/" + slots.Length + "/expected-" +
                      materialNames.Length;
            return false;
        }
        for (int channel = 0; channel < expectedUvChannels; channel++)
        {
            VertexAttribute attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
            if (mesh.HasVertexAttribute(attribute)) continue;
            failure = "uv" + channel + "-missing";
            return false;
        }
        if (mesh.HasVertexAttribute(VertexAttribute.Color) != expectedColor)
        {
            failure = "color0-presence:" + mesh.HasVertexAttribute(VertexAttribute.Color) +
                      "/expected-" + expectedColor;
            return false;
        }
        if (InstalledRootFurnitureUvFingerprints.TryGetValue(meshName, out string expectedUvFingerprint))
        {
            string actualUvFingerprint = ComputeUvFingerprint(mesh);
            if (!string.Equals(actualUvFingerprint, expectedUvFingerprint, StringComparison.Ordinal))
            {
                failure = "installed-uv-fingerprint:" + actualUvFingerprint + "/expected-" +
                          expectedUvFingerprint;
                return false;
            }
        }
        if (InstalledRootFurnitureMeshFingerprints.TryGetValue(meshName,
                out string expectedMeshFingerprint))
        {
            string actualMeshFingerprint = ComputeInstalledMeshFingerprint(mesh);
            if (!string.Equals(actualMeshFingerprint, expectedMeshFingerprint, StringComparison.Ordinal))
            {
                failure = "installed-mesh-fingerprint:" + actualMeshFingerprint + "/expected-" +
                          expectedMeshFingerprint;
                return false;
            }
        }
        for (int index = 0; index < slots.Length; index++)
        {
            string expected = materialNames[index];
            Material actual = slots[index];
            if (actual == null || !string.Equals(NormalizeMaterialName(actual.name), Sanitize(expected),
                    StringComparison.Ordinal))
            {
                failure = "slot-" + index + ":expected-" + expected + "/actual-" +
                          (actual == null ? "null" : actual.name);
                return false;
            }
            if (!KillHouseNativeMaterialBuilder.HasExactFurnitureTransportContract(actual, expected,
                    out string materialFailure, requireAssetDatabaseIdentity))
            {
                failure = "slot-" + index + ":" + materialFailure;
                return false;
            }
        }
        return true;
    }

    private static string ComputeUvFingerprint(Mesh mesh)
    {
        if (mesh == null || !BitConverter.IsLittleEndian) return string.Empty;
        var bytes = new byte[checked(mesh.vertexCount * 2 * 2 * sizeof(float))];
        int offset = 0;
        for (int channel = 0; channel < 2; channel++)
        {
            var values = new List<Vector2>(mesh.vertexCount);
            mesh.GetUVs(channel, values);
            if (values.Count != mesh.vertexCount) return string.Empty;
            foreach (Vector2 value in values)
            {
                byte[] x = BitConverter.GetBytes(value.x);
                byte[] y = BitConverter.GetBytes(value.y);
                Buffer.BlockCopy(x, 0, bytes, offset, x.Length);
                offset += x.Length;
                Buffer.BlockCopy(y, 0, bytes, offset, y.Length);
                offset += y.Length;
            }
        }
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
    }

    private static string ComputeInstalledMeshFingerprint(Mesh mesh)
    {
        if (mesh == null || !BitConverter.IsLittleEndian) return string.Empty;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            foreach (Vector3 value in mesh.vertices)
            {
                writer.Write(value.x); writer.Write(value.y); writer.Write(value.z);
            }
            foreach (Vector3 value in mesh.normals)
            {
                writer.Write(value.x); writer.Write(value.y); writer.Write(value.z);
            }
            foreach (Vector4 value in mesh.tangents)
            {
                writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w);
            }
            for (int channel = 0; channel < 2; channel++)
            {
                var values = new List<Vector2>(mesh.vertexCount);
                mesh.GetUVs(channel, values);
                if (values.Count != mesh.vertexCount) return string.Empty;
                foreach (Vector2 value in values) { writer.Write(value.x); writer.Write(value.y); }
            }
            Color32[] colors = mesh.colors32;
            if (colors.Length != mesh.vertexCount) return string.Empty;
            foreach (Color32 value in colors)
            {
                writer.Write(value.r); writer.Write(value.g); writer.Write(value.b); writer.Write(value.a);
            }
            writer.Write((uint)mesh.subMeshCount);
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                int[] indices = mesh.GetIndices(submesh);
                writer.Write((uint)indices.Length);
                foreach (int index in indices) writer.Write((uint)index);
            }
        }
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", string.Empty);
    }

    private static bool HasExactActiveChildHierarchy(Transform parent, ChildDefinition[] expected,
        bool allowPlacementMarkers, bool requireAssetDatabaseIdentity, out string failure)
    {
        failure = string.Empty;
        if (parent.childCount < expected.Length)
        {
            failure = "child-count:" + parent.childCount + "/expected-at-least-" + expected.Length;
            return false;
        }
        for (int index = 0; index < expected.Length; index++)
        {
            ChildDefinition contract = expected[index];
            Transform actual = parent.GetChild(index);
            if (!string.Equals(actual.name, contract.Name, StringComparison.Ordinal) ||
                actual.gameObject.activeSelf != contract.Active || actual.gameObject.layer != contract.Layer)
            {
                failure = "child-" + index + "-identity:" + actual.name + "/" +
                          actual.gameObject.activeSelf + "/layer-" + actual.gameObject.layer + "/expected-" +
                          contract.Name + "/" + contract.Active + "/layer-" + contract.Layer;
                return false;
            }
            if (!Approximately(actual.localPosition, contract.LocalPosition, 1e-5f) ||
                !Approximately(actual.localScale, contract.LocalScale, 1e-5f) ||
                !Approximately(actual.localRotation, contract.LocalRotation, 1e-5f))
            {
                failure = "child-" + index + "-transform:" + actual.localPosition.ToString("F7") + "/" +
                          actual.localRotation.ToString("F7") + "/" + actual.localScale.ToString("F7");
                return false;
            }
            if (string.IsNullOrWhiteSpace(contract.InstalledProvenance))
            {
                failure = "child-" + index + "-installed-provenance-missing";
                return false;
            }
            if (string.IsNullOrEmpty(contract.Mesh))
            {
                if (contract.Materials.Length != 0 || !string.IsNullOrEmpty(contract.Collision) ||
                    contract.BoxColliders.Length != 0 || actual.GetComponents<Component>().Length != 1)
                {
                    failure = "child-" + index + "-transform-only-contract";
                    return false;
                }
            }
            else
            {
                if (!HasExactFurnitureVisualNode(actual.gameObject, contract.Mesh, contract.Materials,
                        "Assets/VektorKillHouse/Native/ResidentialHierarchy/Meshes/Generated/" +
                        contract.Mesh + ".asset", expectedUvChannels: 2, expectedColor: false,
                        expectedVertexCount: contract.VertexCount, expectedIndexCount: contract.IndexCount,
                        requireAssetDatabaseIdentity, out string visualFailure))
                {
                    failure = "child-" + index + "-visual:" + visualFailure;
                    return false;
                }
                if (!HasExactChildColliderContract(actual.gameObject, contract, requireAssetDatabaseIdentity,
                        out string colliderFailure))
                {
                    failure = "child-" + index + "-collider:" + colliderFailure;
                    return false;
                }
            }
            if (!HasExactActiveChildHierarchy(actual, contract.Children, false,
                    requireAssetDatabaseIdentity, out string nestedFailure))
            {
                failure = "child-" + index + "/" + nestedFailure;
                return false;
            }
        }

        for (int index = expected.Length; index < parent.childCount; index++)
        {
            Transform extra = parent.GetChild(index);
            bool marker = allowPlacementMarkers && IsFurniturePlacementMarkerName(extra.name);
            Vector3 expectedMarkerPosition = string.Equals(extra.name, CouchFrontMarkerName,
                StringComparison.Ordinal) ? Vector3.forward : Vector3.zero;
            if (!marker || !extra.gameObject.activeSelf || extra.childCount != 0 ||
                extra.GetComponents<Component>().Length != 1 ||
                !Approximately(extra.localPosition, expectedMarkerPosition, 1e-6f) ||
                !Approximately(extra.localRotation, Quaternion.identity, 1e-6f) ||
                !Approximately(extra.localScale, Vector3.one, 1e-6f))
            {
                failure = "unmanifested-child-" + index + ":" + extra.name;
                return false;
            }
        }
        return true;
    }

    private static bool HasExactRootFurnitureMetadata(Transform root, Definition definition,
        out string failure)
    {
        failure = string.Empty;
        if (!string.Equals(definition.Mesh, "Couch_2seat", StringComparison.Ordinal)) return true;
        Component[] components = root.GetComponents<Component>();
        if (!root.gameObject.activeSelf || components.Length != 7 ||
            !(components[0] is Transform) || !(components[1] is MeshFilter) ||
            !(components[2] is MeshRenderer) || components.Skip(3).Any(component =>
                !(component is BoxCollider)))
        {
            failure = "retained-root-component-order-or-active-state";
            return false;
        }
        if (string.IsNullOrWhiteSpace(definition.InstalledProvenance))
        {
            failure = "installed-provenance-missing";
            return false;
        }
        Transform[] frontMarkers = root.Cast<Transform>().Where(child =>
            string.Equals(child.name, CouchFrontMarkerName, StringComparison.Ordinal)).ToArray();
        Transform[] provenanceMarkers = root.Cast<Transform>().Where(child =>
            string.Equals(child.name, CouchProvenanceMarkerName, StringComparison.Ordinal)).ToArray();
        if (frontMarkers.Length != 1 || provenanceMarkers.Length != 1 ||
            !HasExactMetadataTransform(frontMarkers[0], Vector3.forward, definition.Layer) ||
            !HasExactMetadataTransform(provenanceMarkers[0], Vector3.zero, definition.Layer))
        {
            failure = "front-or-provenance-marker";
            return false;
        }
        return true;
    }

    private static bool HasExactMetadataTransform(Transform marker, Vector3 localPosition, int layer)
    {
        return marker != null && marker.childCount == 0 && marker.gameObject.layer == layer &&
               marker.GetComponents<Component>().Length == 1 &&
               Approximately(marker.localPosition, localPosition, 1e-6f) &&
               Approximately(marker.localRotation, Quaternion.identity, 1e-6f) &&
               Approximately(marker.localScale, Vector3.one, 1e-6f);
    }

    private static bool HasExactCouchRendererState(MeshRenderer renderer, out string failure)
    {
        failure = string.Empty;
        if (renderer == null)
        {
            failure = "renderer-null";
            return false;
        }
        Transform[] anchors = renderer.transform.Cast<Transform>().Where(child =>
            string.Equals(child.name, CouchProbeAnchorChildName, StringComparison.Ordinal)).ToArray();
        if (anchors.Length != 1 || renderer.probeAnchor != anchors[0])
        {
            failure = "probe-anchor:" + anchors.Length + "/" +
                      (renderer.probeAnchor == null ? "null" : renderer.probeAnchor.name);
            return false;
        }
        var serialized = new SerializedObject(renderer);
        serialized.Update();
        if (!SerializedBoolEquals(serialized, "m_Enabled", true, out failure) ||
            !SerializedIntEquals(serialized, "m_CastShadows", 1, out failure) ||
            !SerializedBoolEquals(serialized, "m_ReceiveShadows", true, out failure) ||
            !SerializedBoolEquals(serialized, "m_DynamicOccludee", true, out failure) ||
            !SerializedBoolEquals(serialized, "m_StaticShadowCaster", false, out failure) ||
            !SerializedIntEquals(serialized, "m_MotionVectors", 1, out failure) ||
            !SerializedIntEquals(serialized, "m_LightProbeUsage", 1, out failure) ||
            !SerializedIntEquals(serialized, "m_ReflectionProbeUsage", 1, out failure) ||
            !SerializedIntEquals(serialized, "m_RayTracingMode", 2, out failure) ||
            !SerializedBoolEquals(serialized, "m_RayTraceProcedural", false, out failure) ||
            !SerializedIntEquals(serialized, "m_RayTracingAccelStructBuildFlagsOverride", 0,
                out failure) ||
            !SerializedIntEquals(serialized, "m_RayTracingAccelStructBuildFlags", 1, out failure) ||
            !SerializedBoolEquals(serialized, "m_SmallMeshCulling", true, out failure) ||
            !SerializedIntEquals(serialized, "m_ForceMeshLod", -1, out failure) ||
            !SerializedFloatEquals(serialized, "m_MeshLodSelectionBias", 0f, out failure) ||
            !SerializedIntEquals(serialized, "m_RenderingLayerMask", 257, out failure) ||
            !SerializedIntEquals(serialized, "m_RendererPriority", 0, out failure) ||
            !SerializedIntEquals(serialized, "m_SortingLayerID", 0, out failure) ||
            !SerializedIntEquals(serialized, "m_SortingLayer", 0, out failure) ||
            !SerializedIntEquals(serialized, "m_SortingOrder", 0, out failure) ||
            !SerializedIntEquals(serialized, "m_MaskInteraction", 0, out failure) ||
            !SerializedObjectEquals(serialized, "m_AdditionalVertexStreams", null, out failure) ||
            !SerializedObjectEquals(serialized, "m_LightProbeVolumeOverride", null, out failure) ||
            !SerializedObjectEquals(serialized, "m_ProbeAnchor", anchors[0], out failure) ||
            !SerializedObjectEquals(serialized, "m_StaticBatchRoot", null, out failure) ||
            !SerializedIntEquals(serialized, "m_StaticBatchInfo.firstSubMesh", 0, out failure) ||
            !SerializedIntEquals(serialized, "m_StaticBatchInfo.subMeshCount", 0, out failure))
            return false;
        return true;
    }

    private static bool SerializedBoolEquals(SerializedObject serialized, string path, bool expected,
        out string failure)
    {
        SerializedProperty property = serialized.FindProperty(path);
        bool valid = property != null && property.boolValue == expected;
        failure = valid ? string.Empty : path + ":expected-" + expected;
        return valid;
    }

    private static bool SerializedIntEquals(SerializedObject serialized, string path, int expected,
        out string failure)
    {
        SerializedProperty property = serialized.FindProperty(path);
        bool valid = property != null && property.intValue == expected;
        failure = valid ? string.Empty : path + ":expected-" + expected;
        return valid;
    }

    private static bool SerializedFloatEquals(SerializedObject serialized, string path, float expected,
        out string failure)
    {
        SerializedProperty property = serialized.FindProperty(path);
        bool valid = property != null && Mathf.Abs(property.floatValue - expected) <= 1e-6f;
        failure = valid ? string.Empty : path + ":expected-" + expected.ToString("R");
        return valid;
    }

    private static bool SerializedObjectEquals(SerializedObject serialized, string path,
        UnityEngine.Object expected, out string failure)
    {
        SerializedProperty property = serialized.FindProperty(path);
        bool valid = property != null && property.objectReferenceValue == expected;
        failure = valid ? string.Empty : path + ":object-reference";
        return valid;
    }

    private static bool IsFurniturePlacementMarkerName(string name)
    {
        string value = name ?? string.Empty;
        return value.StartsWith("WALL_BACKED_PROP_OUTWARD_", StringComparison.Ordinal) ||
               value.StartsWith("WALL_BACKED_PROP_PROVENANCE_", StringComparison.Ordinal) ||
               value.StartsWith("CENTER_ROOM_PROP_ROLE_", StringComparison.Ordinal) ||
               value.StartsWith("CENTER_ROOM_PROP_ROOM_", StringComparison.Ordinal) ||
               value.StartsWith("CENTER_ROOM_PROP_PROVENANCE_", StringComparison.Ordinal) ||
               value.StartsWith("CENTER_ROOM_PROP_FACING_", StringComparison.Ordinal) ||
               value.StartsWith("CENTER_ROOM_PROP_DETERMINISTIC_CANDIDATE_", StringComparison.Ordinal) ||
               string.Equals(value, "CENTER_ROOM_PROP_CLEARANCE_VALID", StringComparison.Ordinal) ||
               string.Equals(value, "CENTER_ROOM_PROP_CIRCULATION_VALID", StringComparison.Ordinal) ||
               string.Equals(value, CouchFrontMarkerName, StringComparison.Ordinal) ||
               string.Equals(value, CouchProvenanceMarkerName, StringComparison.Ordinal);
    }

    private static bool HasExactChildColliderContract(GameObject node, ChildDefinition definition,
        bool requireAssetDatabaseIdentity, out string failure)
    {
        failure = string.Empty;
        Collider[] colliders = node.GetComponents<Collider>();
        if (definition.BoxColliders.Length > 0)
        {
            BoxCollider[] boxes = node.GetComponents<BoxCollider>();
            if (boxes.Length != definition.BoxColliders.Length || colliders.Length != boxes.Length)
            {
                failure = "box-count:" + boxes.Length + "/" + colliders.Length + "/expected-" +
                          definition.BoxColliders.Length;
                return false;
            }
            for (int index = 0; index < boxes.Length; index++)
            {
                BoxCollider actual = boxes[index];
                BoxColliderProfile expected = definition.BoxColliders[index];
                if (actual.enabled != expected.Enabled || actual.isTrigger || actual.sharedMaterial != null ||
                    !Approximately(actual.center, expected.Center, 1e-6f) ||
                    !Approximately(actual.size, expected.Size, 1e-6f))
                {
                    failure = "box-" + index + "-state";
                    return false;
                }
            }
            return true;
        }

        if (string.IsNullOrEmpty(definition.Collision))
        {
            if (colliders.Length == 0) return true;
            failure = "unexpected-collider-count:" + colliders.Length;
            return false;
        }
        MeshCollider[] meshColliders = node.GetComponents<MeshCollider>();
        if (meshColliders.Length != 1 || colliders.Length != 1)
        {
            failure = "mesh-count:" + meshColliders.Length + "/" + colliders.Length;
            return false;
        }
        MeshCollider collider = meshColliders[0];
        Mesh expectedMesh = FindGeneratedMesh(definition.Collision);
        string expectedPath = "Assets/VektorKillHouse/Native/ResidentialHierarchy/Meshes/Generated/" +
                              definition.Collision + ".asset";
        if (collider.enabled != definition.CollisionEnabled || collider.isTrigger ||
            collider.sharedMaterial != null ||
            !MeshIdentityMatches(collider.sharedMesh, expectedMesh, expectedPath,
                requireAssetDatabaseIdentity) ||
            collider.convex != definition.CollisionConvex || (int)collider.cookingOptions != 30)
        {
            failure = "mesh-state:" + (collider.sharedMesh == null ? "null" : collider.sharedMesh.name) +
                      "/enabled-" + collider.enabled + "/convex-" + collider.convex + "/cooking-" +
                      (int)collider.cookingOptions;
            return false;
        }
        return true;
    }

    private static int CountChildRenderNodes(ChildDefinition child)
    {
        return (string.IsNullOrEmpty(child.Mesh) ? 0 : 1) + child.Children.Sum(CountChildRenderNodes);
    }

    private static bool HasOnlyRetainedFurnitureComponents(GameObject root, out string failure)
    {
        failure = string.Empty;
        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
        {
            bool marker = transform != root.transform && IsFurniturePlacementMarkerName(transform.name);
            foreach (Component component in transform.GetComponents<Component>())
            {
                if (component == null)
                {
                    failure = transform.name + ":missing-script-component";
                    return false;
                }
                if (component is Transform || (!marker && (component is MeshFilter || component is MeshRenderer ||
                                                            component is MeshCollider || component is BoxCollider)))
                    continue;
                failure = transform.name + ":component-" + component.GetType().Name;
                return false;
            }
        }
        return true;
    }

    private static bool HasExactColliderContract(GameObject prefab, Definition definition, Mesh visualMesh,
        bool requireAssetDatabaseIdentity, out string failure)
    {
        failure = string.Empty;
        Collider[] allColliders = prefab.GetComponents<Collider>();
        if (!definition.Collider)
        {
            if (allColliders.Length == 0) return true;
            failure = "excluded-or-nonphysical-family-has-colliders=" + allColliders.Length;
            return false;
        }

        if (definition.BoxColliders.Length > 0)
        {
            BoxCollider[] actual = prefab.GetComponents<BoxCollider>();
            if (actual.Length != definition.BoxColliders.Length || allColliders.Length != actual.Length)
            {
                failure = "box-count=" + actual.Length + "/expected=" + definition.BoxColliders.Length +
                          "/all=" + allColliders.Length;
                return false;
            }
            for (int index = 0; index < actual.Length; index++)
            {
                BoxColliderProfile expected = definition.BoxColliders[index];
                BoxCollider collider = actual[index];
                if (collider.enabled != expected.Enabled || collider.isTrigger || collider.sharedMaterial != null ||
                    !Approximately(collider.center, expected.Center, 1e-6f) ||
                    !Approximately(collider.size, expected.Size, 1e-6f))
                {
                    failure = "box-" + index + "-state";
                    return false;
                }
                if (string.Equals(definition.Mesh, "Couch_2seat", StringComparison.Ordinal) &&
                    !HasExactCouchColliderAuxiliaryState(collider, out string auxiliaryFailure))
                {
                    failure = "box-" + index + "-installed-auxiliary-state:" + auxiliaryFailure;
                    return false;
                }
            }
            return true;
        }

        MeshCollider[] meshColliders = prefab.GetComponents<MeshCollider>();
        if (meshColliders.Length != 1 || allColliders.Length != 1)
        {
            failure = "mesh-count=" + meshColliders.Length + "/all=" + allColliders.Length;
            return false;
        }
        Mesh expectedMesh = string.IsNullOrEmpty(definition.Collision)
            ? visualMesh
            : FindGeneratedMesh(definition.Collision);
        string expectedMeshPath = string.IsNullOrEmpty(definition.Collision)
            ? AssetDatabase.GetAssetPath(visualMesh)
            : "Assets/VektorKillHouse/Native/ResidentialCollision/Meshes/Generated/" +
              definition.Collision + ".asset";
        MeshCollider meshCollider = meshColliders[0];
        if (!meshCollider.enabled || meshCollider.isTrigger || meshCollider.sharedMaterial != null ||
            !MeshIdentityMatches(meshCollider.sharedMesh, expectedMesh, expectedMeshPath,
                requireAssetDatabaseIdentity) || meshCollider.convex != definition.CollisionConvex ||
            (int)meshCollider.cookingOptions != 30)
        {
            failure = "mesh-state=" + (meshCollider.sharedMesh == null ? "null" : meshCollider.sharedMesh.name) +
                      "/expected=" + expectedMesh.name + "/convex=" + meshCollider.convex +
                      "/cooking=" + (int)meshCollider.cookingOptions;
            return false;
        }
        return true;
    }

    private static bool HasExactCouchColliderAuxiliaryState(BoxCollider collider, out string failure)
    {
        failure = string.Empty;
        var serialized = new SerializedObject(collider);
        serialized.Update();
        return SerializedObjectEquals(serialized, "m_Material", null, out failure) &&
               SerializedIntEquals(serialized, "m_IncludeLayers.m_Bits", 0, out failure) &&
               SerializedIntEquals(serialized, "m_ExcludeLayers.m_Bits", 0, out failure) &&
               SerializedIntEquals(serialized, "m_LayerOverridePriority", 0, out failure) &&
               SerializedBoolEquals(serialized, "m_IsTrigger", false, out failure) &&
               SerializedBoolEquals(serialized, "m_ProvidesContacts", false, out failure) &&
               SerializedBoolEquals(serialized, "m_Enabled", true, out failure);
    }

    private static bool MeshIdentityMatches(Mesh actual, Mesh expected, string expectedAssetPath,
        bool requireAssetDatabaseIdentity)
    {
        if (actual == null || expected == null) return false;
        if (requireAssetDatabaseIdentity)
            return actual == expected && string.Equals(AssetDatabase.GetAssetPath(actual), expectedAssetPath,
                StringComparison.Ordinal);
        if (!string.Equals(actual.name, expected.name, StringComparison.Ordinal) ||
            actual.vertexCount != expected.vertexCount || actual.subMeshCount != expected.subMeshCount ||
            !Approximately(actual.bounds.center, expected.bounds.center, 1e-5f) ||
            !Approximately(actual.bounds.size, expected.bounds.size, 1e-5f)) return false;
        for (int index = 0; index < actual.subMeshCount; index++)
            if (actual.GetTopology(index) != expected.GetTopology(index) ||
                actual.GetIndexCount(index) != expected.GetIndexCount(index)) return false;
        return true;
    }

    private static bool Approximately(Vector3 first, Vector3 second, float epsilon)
    {
        return Mathf.Abs(first.x - second.x) <= epsilon && Mathf.Abs(first.y - second.y) <= epsilon &&
               Mathf.Abs(first.z - second.z) <= epsilon;
    }

    private static bool Approximately(Quaternion first, Quaternion second, float epsilon)
    {
        bool same = Mathf.Abs(first.x - second.x) <= epsilon && Mathf.Abs(first.y - second.y) <= epsilon &&
                    Mathf.Abs(first.z - second.z) <= epsilon && Mathf.Abs(first.w - second.w) <= epsilon;
        bool negated = Mathf.Abs(first.x + second.x) <= epsilon && Mathf.Abs(first.y + second.y) <= epsilon &&
                       Mathf.Abs(first.z + second.z) <= epsilon && Mathf.Abs(first.w + second.w) <= epsilon;
        return same || negated;
    }

    public static bool HasExactWarehouseFloorPrefabContract(GameObject prefab,
        bool requireAssetDatabaseIdentity, out string failure)
    {
        failure = string.Empty;
        if (prefab == null)
        {
            failure = "prefab-null";
            return false;
        }

        MeshFilter[] filters = prefab.GetComponentsInChildren<MeshFilter>(true);
        MeshRenderer[] renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
        MeshCollider[] meshColliders = prefab.GetComponentsInChildren<MeshCollider>(true);
        Collider[] colliders = prefab.GetComponentsInChildren<Collider>(true);
        if (filters.Length != 1 || filters[0].sharedMesh == null)
        {
            failure = "mesh-filter-count:" + filters.Length;
            return false;
        }
        if (renderers.Length != 1 || renderers[0].sharedMaterials.Length != 1)
        {
            failure = "renderer-or-slot-count:" + renderers.Length + "/" +
                      (renderers.Length == 0 ? 0 : renderers[0].sharedMaterials.Length);
            return false;
        }

        Mesh mesh = filters[0].sharedMesh;
        string[] primitiveNames = { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };
        if (!string.Equals(mesh.name, "Floor", StringComparison.Ordinal) || primitiveNames.Contains(mesh.name))
        {
            failure = "mesh-identity:" + mesh.name;
            return false;
        }
        if (requireAssetDatabaseIdentity &&
            !string.Equals(AssetDatabase.GetAssetPath(mesh), WarehouseFloorMeshAssetPath,
                StringComparison.Ordinal))
        {
            failure = "mesh-asset-path:" + AssetDatabase.GetAssetPath(mesh);
            return false;
        }
        if (mesh.vertexCount != 4 || mesh.subMeshCount != 1 || mesh.GetIndexCount(0) != 6 ||
            mesh.GetTopology(0) != MeshTopology.Triangles)
        {
            failure = "mesh-topology:" + mesh.vertexCount + "/" + mesh.subMeshCount + "/" +
                      (mesh.subMeshCount == 0 ? 0 : mesh.GetIndexCount(0));
            return false;
        }
        if (!mesh.HasVertexAttribute(VertexAttribute.Normal) ||
            !mesh.HasVertexAttribute(VertexAttribute.Tangent) ||
            !mesh.HasVertexAttribute(VertexAttribute.TexCoord0) ||
            !mesh.HasVertexAttribute(VertexAttribute.TexCoord1) ||
            mesh.normals.Length != 4 || mesh.tangents.Length != 4 ||
            mesh.uv.Length != 4 || mesh.uv2.Length != 4)
        {
            failure = "vertex-channel-closure";
            return false;
        }
        if (mesh.normals.Any(normal => Vector3.Dot(normal.normalized, Vector3.up) < .9999f))
        {
            failure = "non-upward-normal";
            return false;
        }
        Bounds sourceBounds = mesh.bounds;
        if (Mathf.Abs(sourceBounds.size.x - WarehouseFloorSourceWidth) > .001f ||
            Mathf.Abs(sourceBounds.size.z - WarehouseFloorSourceDepth) > .001f ||
            sourceBounds.size.y > .001f)
        {
            failure = "source-bounds:" + sourceBounds.size.ToString("F6");
            return false;
        }

        if (!KillHouseNativeMaterialBuilder.HasExactWarehouseFloorTransportContract(
                renderers[0].sharedMaterial, out string materialFailure, requireAssetDatabaseIdentity))
        {
            failure = "material:" + materialFailure;
            return false;
        }
        if (meshColliders.Length != 1 || colliders.Length != 1 ||
            meshColliders[0].sharedMesh != mesh || !meshColliders[0].enabled ||
            meshColliders[0].isTrigger || meshColliders[0].convex)
        {
            failure = "mesh-collider-closure";
            return false;
        }
        return true;
    }

    public static IEnumerable<string> FurniturePrefabPaths()
    {
        return Definitions.Where(definition => ReleaseFurnitureMeshes.Contains(definition.Mesh))
            .Select(definition => PrefabPath(definition.Mesh));
    }

    public static bool IsFurnitureMeshName(string meshName)
    {
        string value = meshName ?? string.Empty;
        return FurnitureMeshes.Contains(value) || FurnitureHierarchyMeshes.Contains(value);
    }

    public static bool IsFurnitureRootMeshName(string meshName) =>
        FurnitureMeshes.Contains(meshName ?? string.Empty);

    private static string NormalizeMaterialName(string value)
    {
        const string prefix = "MAT_NATIVE_";
        string result = value ?? string.Empty;
        if (result.StartsWith(prefix, StringComparison.Ordinal)) result = result.Substring(prefix.Length);
        const string instance = " (Instance)";
        if (result.EndsWith(instance, StringComparison.Ordinal))
            result = result.Substring(0, result.Length - instance.Length);
        return result;
    }

    private static Mesh FindGeneratedMesh(string exactName)
    {
        string[] guids = AssetDatabase.FindAssets(exactName + " t:Mesh", new[] { "Assets/VektorKillHouse/Native" });
        string path = guids.Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(candidate =>
                candidate.IndexOf("/Generated/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                string.Equals(Path.GetFileNameWithoutExtension(candidate), exactName, StringComparison.Ordinal));
        Mesh mesh = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>()
            .FirstOrDefault(candidate => string.Equals(candidate.name, exactName, StringComparison.Ordinal));
        if (mesh == null && !string.IsNullOrEmpty(path))
            mesh = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault();
        if (mesh == null) throw new FileNotFoundException("Generated native mesh is missing: " + exactName);
        return mesh;
    }

    private static string RootFurnitureMeshAssetPath(Definition definition)
    {
        return "Assets/VektorKillHouse/Native/" + definition.RootMeshFolder +
               "/Meshes/Generated/" + definition.Mesh + ".asset";
    }

    private static string Sanitize(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value.Replace(' ', '_');
    }

    private static void EnsureFolder(string assetPath)
    {
        string current = "Assets";
        foreach (string segment in assetPath.Split('/').Skip(1))
        {
            string next = current + "/" + segment;
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, segment);
            current = next;
        }
    }
}
#endif
