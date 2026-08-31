#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

public static class KillHouseVariantBuilder
{
    public const string SceneFolder = "Assets/VektorKillHouse/Scenes";
    public const string MapMarker = "MAP_ID_community.vektor-modular-killhouse.modular-killhouse";
    public const string PveSpawnSetMarker = "SPAWN_SET_killhouse-pve";
    public const string PvpSpawnSetMarker = "SPAWN_SET_killhouse-pvp";
    private const float StandardRoomSize = 4f;
    private const float LargeRoomSize = 8f;
    private const float ExpansiveRoomSize = 12f;
    private const float WallModuleWidth = 2f;
    private const float PartitionHeight = 3.0f;
    private const float WarehouseRoofHeight = 11.35f;
    private const float WarehouseFixtureRoofGap = .08f;
    private const float WarehouseFixtureLightDrop = .18f;
    private static readonly Vector3 WarehouseFixtureVisualScale = new Vector3(1.75f, 2.5f, 1.5f);
    private const float WarehouseMargin = 4.0f;
    private const float WarehouseGroundElevation = -.015f;
    private const float WarehouseFixtureSpacing = 8.0f;
    private const float MaximumConnectorLength = 8f;
    private const float CenterRoomPropWallClearance = .82f;
    // Keep authoring candidate rejection byte-for-behaviour aligned with the
    // companion's live scene-contract gate.  A looser authoring tolerance can
    // serialize furniture that the player correctly rejects after loading.
    private const float CenterRoomSiblingOverlapTolerance = .02f;
    private const float CenterRoomCirculationStep = .5f;
    private const float CenterRoomCirculationRadius = .42f;
    private const int CertifiedPveMaximumEnemies = 60;
    // Author more than the declared maximum so the live navigation/grounding pass can
    // still certify sixty positions if a small number of markers normalize together.
    private const int PveAuthoredEnemyMarkerTarget = 72;
    private const int PveMinimumMarkersPerCombatRoom = 1;
    private const float PveEnemySpawnPairClearance = 2.05f;
    private const float PveEnemySpawnPortalClearance = 1.35f;
    private const int PvpSpawnsPerTeam = 6;
    private const int PvpRoomsPerSector = 3;
    private const float PvpSpawnCapsuleRadius = .42f;
    private const float PvpSpawnCapsuleBottom = .48f;
    private const float PvpSpawnCapsuleTop = 1.58f;
    private const float PvpSpawnGridStep = .5f;
    private const float PvpSpawnWallInset = .72f;
    private const float PvpSpawnPairClearance = 1.5f;
    private const float PvpSpawnPortalClearance = 1.05f;
    private const float PvpSpawnDoorClearance = 1.35f;
    private const float PvpMinimumOpposingDistance = 20f;
    private const int PvpMinimumSectorGraphDistance = 4;
    private const string PvpSpawnContract =
        "deterministic-two-disjoint-connected-three-room-graph-extreme-sectors; exact-6v6; " +
        "non-safe-room; 0.42m-standing-capsule; floor-supported; portal>=1.05m; " +
        "DoorV2-socket>=1.35m; same-team>=1.50m; opposing>=20m; " +
        "sector-graph-distance>=4; zero-direct-eye-level-LOS; faces-opposing-sector; " +
        "PVE-markers-unchanged";
    // Measured from the shipped Wall2MeterDoor collision aperture and residential DoorV2 graph.
    private const float DoorwayOpeningTangentOffset = -.0391f;
    private const float DoorHingeToLeafCenter = .50283f;

    private enum RoomType { Safe, Hallway, Living, Bedroom, Bathroom, Blank, Kitchen, Dining, Study, Storage, Junction }
    private enum RoomLightState { Lit, Dim, Dark }
    private enum PortalKind { Door, Open }
    private enum LayoutMotif
    {
        CenterHallResidential,
        FourSquareCore,
        OpenPlanPrivateWing,
        CourtyardCircuit,
        SplitSpineTraining,
        OfficeCore,
        LRoomTraining,
        DualCorridor,
        Pinwheel,
        HybridLabyrinth
    }

    private sealed class Variant
    {
        public readonly string Id;
        public readonly string SceneName;
        public readonly string Moves;
        public readonly RoomType[] Rooms;
        public readonly PortalKind[] Portals;
        public readonly LayoutMotif Motif;

        public Variant(string id, string sceneName, string moves, RoomType[] rooms, string portals, LayoutMotif motif)
        {
            Id = id;
            SceneName = sceneName;
            Moves = moves;
            Rooms = rooms;
            Portals = portals.Select(value => value == 'D' ? PortalKind.Door : PortalKind.Open).ToArray();
            Motif = motif;
            if (Moves.Length != Rooms.Length || Portals.Length != Rooms.Length)
                throw new InvalidDataException(id + " must provide one room and one portal for every cycle edge.");
            if (Rooms[0] != RoomType.Safe || Portals[0] != PortalKind.Door || Portals[Portals.Length - 1] != PortalKind.Door)
                throw new InvalidDataException(id + " does not preserve the fixed two-door safe-room contract.");
        }

        public string ScenePath => SceneFolder + "/" + SceneName + ".unity";
    }

    private sealed class Layout
    {
        public readonly Vector2Int[] Cells;
        public readonly RoomType[] Rooms;
        public readonly int BaseCycleCount;

        public Layout(Vector2Int[] cells, RoomType[] rooms, int baseCycleCount)
        {
            Cells = cells;
            Rooms = rooms;
            BaseCycleCount = baseCycleCount;
        }
    }

    private sealed class ConnectionPlan
    {
        public readonly int RoomA;
        public readonly int RoomB;
        public readonly Vector2Int DirectionFromA;
        public readonly PortalKind Portal;
        public readonly bool FixedSafeEdge;
        public readonly float PortalOffset;

        public ConnectionPlan(int roomA, int roomB, Vector2Int directionFromA, PortalKind portal,
            bool fixedSafeEdge, float portalOffset)
        {
            RoomA = roomA;
            RoomB = roomB;
            DirectionFromA = directionFromA;
            Portal = portal;
            FixedSafeEdge = fixedSafeEdge;
            PortalOffset = portalOffset;
        }

        public string Key => EdgeKey(RoomA, RoomB);
    }

    private sealed class WallBackedFurnitureContract
    {
        public readonly string MeshName;
        public readonly Vector3 LocalInteriorAxis;
        public readonly Vector3 LocalWallAxis;
        public readonly string[] OrderedMaterials;
        public readonly string InstalledProvenance;

        public WallBackedFurnitureContract(string meshName, Vector3 localInteriorAxis, Vector3 localWallAxis,
            string[] orderedMaterials, string installedProvenance)
        {
            MeshName = meshName;
            LocalInteriorAxis = localInteriorAxis;
            LocalWallAxis = localWallAxis;
            OrderedMaterials = orderedMaterials;
            InstalledProvenance = installedProvenance;
        }

        public string ProvenanceMarker => "WALL_BACKED_PROP_PROVENANCE_" +
                                          SanitizeMarkerName(InstalledProvenance);
        public string MeshAssetPath => "Assets/VektorKillHouse/Native/Residential/Meshes/Generated/" +
                                       MeshName + ".asset";
    }

    private sealed class CenterRoomFurnitureContract
    {
        public readonly string MeshName;
        public readonly string Role;
        public readonly Vector3 LocalFacingAxis;
        public readonly bool BidirectionalFacing;
        public readonly float Scale;
        public readonly string ProvenanceMarker;

        public CenterRoomFurnitureContract(string meshName, string role, Vector3 localFacingAxis,
            bool bidirectionalFacing, float scale, string provenanceMarker)
        {
            MeshName = meshName;
            Role = role;
            LocalFacingAxis = localFacingAxis;
            BidirectionalFacing = bidirectionalFacing;
            Scale = scale;
            ProvenanceMarker = provenanceMarker;
        }
    }

    private sealed class CenterRoomPlacementCandidate
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        public CenterRoomPlacementCandidate(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    private sealed class TacticalEnemyCandidate
    {
        public readonly int RoomIndex;
        public readonly Vector3 Position;
        public readonly Vector3 CoverPoint;
        public readonly Vector3 ThreatPoint;
        public readonly string Role;
        public readonly string CoverLabel;

        public TacticalEnemyCandidate(int roomIndex, Vector3 position, Vector3 coverPoint,
            Vector3 threatPoint, string role, string coverLabel)
        {
            RoomIndex = roomIndex;
            Position = position;
            CoverPoint = coverPoint;
            ThreatPoint = threatPoint;
            Role = role;
            CoverLabel = coverLabel;
        }
    }

    private sealed class PvpRoomSpawnPair
    {
        public readonly int RoomIndex;
        public readonly Vector3 First;
        public readonly Vector3 Second;

        public PvpRoomSpawnPair(int roomIndex, Vector3 first, Vector3 second)
        {
            RoomIndex = roomIndex;
            First = first;
            Second = second;
        }

        public IEnumerable<Vector3> Positions
        {
            get
            {
                yield return First;
                yield return Second;
            }
        }
    }

    private sealed class PvpSpawnPlan
    {
        public readonly int[] Team1Rooms;
        public readonly int[] Team2Rooms;
        public readonly Vector3[] Team1Positions;
        public readonly Vector3[] Team2Positions;
        public readonly int MinimumGraphDistance;
        public readonly float MinimumOpposingDistance;
        public readonly int DirectLineOfSightPairs;

        public PvpSpawnPlan(int[] team1Rooms, int[] team2Rooms, Vector3[] team1Positions,
            Vector3[] team2Positions, int minimumGraphDistance, float minimumOpposingDistance,
            int directLineOfSightPairs)
        {
            Team1Rooms = team1Rooms;
            Team2Rooms = team2Rooms;
            Team1Positions = team1Positions;
            Team2Positions = team2Positions;
            MinimumGraphDistance = minimumGraphDistance;
            MinimumOpposingDistance = minimumOpposingDistance;
            DirectLineOfSightPairs = directLineOfSightPairs;
        }
    }

    // These axes are pinned to direct installed level4 hierarchy/renderer evidence after undoing
    // AssetRipper's GLB X reflection. Every retained wall-backed family has local +Z toward the
    // usable room interior and local -Z against the owned wall. Bed_queen is not an exception:
    // its four shipped pillow children occupy local Z -0.645..-0.898, proving the headboard end is -Z.
    private static readonly Dictionary<string, WallBackedFurnitureContract> WallBackedFurnitureContracts =
        new Dictionary<string, WallBackedFurnitureContract>(StringComparer.Ordinal)
        {
            ["Bed_queen"] = new WallBackedFurnitureContract("Bed_queen", Vector3.forward, Vector3.back,
                new[] { "MAT_NATIVE_Bed" },
                "level4_GO360_MF20235_MR15441_sharedassets4_Mesh1141_MR_SHA256_ae0fb43c7902b028d279f8b4fd91d9cb0ab9603055f1093188ac6cf702dce51a"),
            ["Bookshelf"] = new WallBackedFurnitureContract("Bookshelf", Vector3.forward, Vector3.back,
                new[] { "MAT_NATIVE_Fireplace" },
                "level4_GO2136_MF21517_MR16725_sharedassets4_Mesh721_MR_SHA256_6bb929cf8371caaed03e9a1662c45cd0f65ca416b045418fee9acf1aa07e226c"),
            ["Kitcabinet_full_fridge"] = new WallBackedFurnitureContract("Kitcabinet_full_fridge", Vector3.forward,
                Vector3.back, new[] { "MAT_NATIVE_Kitchen_Cabinet_Wood" },
                "level4_GO2480_MF21764_MR16972_sharedassets4_Mesh1094_MR_SHA256_efadbb90c344d9c9e6caa3fbebbb0fe84c2de26780eed97ade67bde4aabdbf46"),
            ["Kitcabinet_low_1x_A"] = new WallBackedFurnitureContract("Kitcabinet_low_1x_A", Vector3.forward,
                Vector3.back, new[] { "MAT_NATIVE_Kitchen_Cabinet_Wood", "MAT_NATIVE_Kitchen_Cabinet_Marble" },
                "level4_GO1365_MF20950_MR16158_sharedassets4_Mesh1129_MR_SHA256_b0fa7469554a42d5da1ddcae9de8bb08183ae28e9ea4fb5445a8b2bbde7b8e2a"),
            ["Sidetable_A"] = new WallBackedFurnitureContract("Sidetable_A", Vector3.forward, Vector3.back,
                new[] { "MAT_NATIVE_Bedroom_Closets" },
                "level4_GO680_MF20466_MR15674_sharedassets4_Mesh1179_MR_SHA256_91c01d42fba3b158a452794ca9251f19c8bca785cc17daf3120d587471561b55"),
            ["T_sink"] = new WallBackedFurnitureContract("T_sink", Vector3.forward, Vector3.back,
                new[] { "MAT_NATIVE_Bedroom_Closets", "MAT_NATIVE_Toilet_House" },
                "level4_GO785_MF20544_MR15751_sharedassets4_Mesh890_MR_SHA256_8175fa8e22445eb202c17c40051ed7c91404653e4732804644cdbea973fe8fe7"),
            ["T_toilet"] = new WallBackedFurnitureContract("T_toilet", Vector3.forward, Vector3.back,
                new[] { "MAT_NATIVE_Toilet_House" },
                "level4_GO1702_MF21188_MR16396_sharedassets4_Mesh1052_MR_SHA256_d04bc81fc4fd15a7b8222df3eefbcf3f5709e55142026fb6f6031d6584f72609"),
            ["Workdesk_solo"] = new WallBackedFurnitureContract("Workdesk_solo", Vector3.forward, Vector3.back,
                new[] { "MAT_NATIVE_WorkDesk" },
                "level4_GO2401_MF21703_MR16911_sharedassets4_Mesh673_MR_SHA256_193630ef5ef5663d7dd6f6f7fd60e1aeb90782b7301c0b07076187620fb2b048")
        };

    // These meshes are real vanilla submeshes, but the extracted standalone objects are not a
    // proven complete furniture root. Loading any one directly would flatten/guess its native
    // assembly, so scene generation must fail closed if a future dressing rule reintroduces it.
    private static readonly HashSet<string> UnsupportedStandaloneFurnitureMeshes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Book_set_bookshelf_A", // child of a different BookShelf root at local yaw +90
            "Sofa_A", "Sofa_B",   // component pieces of the complete Sofa_large assembly
            "T_bathtub",           // active child/collider hierarchy is not transported
            "D_TV_standing"        // group/assembly root has not been proven or transported
        };

    // Kitchen_table_large is already a complete, exact-prefab-validated level4 root. Its local Z
    // axis is the 1.75 m tabletop axis recorded by the transported collider contract, so table
    // facing is a bidirectional long-axis contract rather than an invented "front". Couch_2seat
    // uses installed level4 GO578: local +Z is the seat/interior front and local -Z is the
    // backrest, with its exact four-collider root retained at the shipped unit scale.
    private static readonly Dictionary<string, CenterRoomFurnitureContract> CenterRoomFurnitureContracts =
        new Dictionary<string, CenterRoomFurnitureContract>(StringComparer.Ordinal)
        {
            ["TABLE"] = new CenterRoomFurnitureContract(
                "Kitchen_table_large", "TABLE", Vector3.forward, true, 1f,
                "CENTER_ROOM_PROP_PROVENANCE_LEVEL4_GO1878_KITCHEN_TABLE_LARGE_SCALE_1_EXACT_PREFAB_UV0UV1_" +
                "425779D06FD5E61A6C9C4C83359DBF35D81337C963F68B7BA46B704D8A069538"),
            ["SOFA"] = new CenterRoomFurnitureContract(
                "Couch_2seat", "SOFA", Vector3.forward, false, 1f,
                "CENTER_ROOM_PROP_PROVENANCE_LEVEL4_GO578_COUCH_2SEAT_SCALE_1_MESH_" +
                "C58D40C40D6B9F18BE6A883EA69581002C005630DF1194206A638896EF483586_UV_" +
                "CF3DB25EA907E15EF421DE6FC1D68C7031D0DA923DBF529383087B2DA55B6171_" +
                "PROBE_ANCHOR_GO2175_T9763_MR15599")
        };

    private static readonly Variant[] Variants =
    {
        V("kh01-circuit-house", "KH01_CircuitHouse", "EEEENNWWWWSS", "DODDOODODODD", LayoutMotif.CenterHallResidential,
            RoomType.Hallway, RoomType.Living, RoomType.Bedroom, RoomType.Bathroom, RoomType.Blank,
            RoomType.Kitchen, RoomType.Dining, RoomType.Study, RoomType.Storage, RoomType.Hallway, RoomType.Bedroom),
        V("kh02-offset-figure-eight", "KH02_OffsetFigureEight", "EENNNNWWSSSS", "DDOOODDODOOD", LayoutMotif.FourSquareCore,
            RoomType.Blank, RoomType.Hallway, RoomType.Kitchen, RoomType.Storage, RoomType.Living,
            RoomType.Junction, RoomType.Bedroom, RoomType.Study, RoomType.Dining, RoomType.Bathroom, RoomType.Hallway),
        V("kh03-serpentine-apartment", "KH03_SerpentineApartment", "EEENNNWWWSSS", "DOODDODOODDD", LayoutMotif.OpenPlanPrivateWing,
            RoomType.Hallway, RoomType.Bedroom, RoomType.Blank, RoomType.Living, RoomType.Hallway,
            RoomType.Bathroom, RoomType.Kitchen, RoomType.Dining, RoomType.Storage, RoomType.Bedroom, RoomType.Study),
        V("kh04-courtyard-ring", "KH04_CourtyardRing", "EEEENNWSWWWS", "DDODOO DODODD".Replace(" ", string.Empty), LayoutMotif.CourtyardCircuit,
            RoomType.Living, RoomType.Hallway, RoomType.Kitchen, RoomType.Dining, RoomType.Blank,
            RoomType.Bedroom, RoomType.Bathroom, RoomType.Storage, RoomType.Study, RoomType.Junction, RoomType.Hallway),
        V("kh05-split-spine", "KH05_SplitSpine", "EEENNNWSSWWS", "DODODDDOOODD", LayoutMotif.SplitSpineTraining,
            RoomType.Hallway, RoomType.Storage, RoomType.Living, RoomType.Bedroom, RoomType.Junction,
            RoomType.Kitchen, RoomType.Blank, RoomType.Bathroom, RoomType.Dining, RoomType.Study, RoomType.Bedroom),
        V("kh06-compressed-grid", "KH06_CompressedGrid", "EENNNNWSSWSS", "DDOODODODOOD", LayoutMotif.OfficeCore,
            RoomType.Blank, RoomType.Bedroom, RoomType.Hallway, RoomType.Kitchen, RoomType.Bathroom,
            RoomType.Living, RoomType.Study, RoomType.Junction, RoomType.Dining, RoomType.Storage, RoomType.Bedroom),
        V("kh07-broken-diamond", "KH07_BrokenDiamond", "EEEENNWWSWWS", "DODDOOODODDD", LayoutMotif.LRoomTraining,
            RoomType.Study, RoomType.Hallway, RoomType.Living, RoomType.Storage, RoomType.Bedroom,
            RoomType.Blank, RoomType.Kitchen, RoomType.Dining, RoomType.Bathroom, RoomType.Junction, RoomType.Hallway),
        V("kh08-double-back", "KH08_DoubleBack", "EEENNNWSWWSS", "DDODODDOOODD", LayoutMotif.DualCorridor,
            RoomType.Hallway, RoomType.Dining, RoomType.Kitchen, RoomType.Bedroom, RoomType.Blank,
            RoomType.Hallway, RoomType.Living, RoomType.Study, RoomType.Bathroom, RoomType.Storage, RoomType.Bedroom),
        V("kh09-pinwheel", "KH09_Pinwheel", "EEEENWNWSWWS", "DOODDDODOODD", LayoutMotif.Pinwheel,
            RoomType.Junction, RoomType.Kitchen, RoomType.Storage, RoomType.Hallway, RoomType.Living,
            RoomType.Bedroom, RoomType.Blank, RoomType.Dining, RoomType.Bathroom, RoomType.Study, RoomType.Hallway),
        V("kh10-wide-labyrinth", "KH10_WideLabyrinth", "EEENNWSWNWSS", "DDOO DODODDDD".Replace(" ", string.Empty), LayoutMotif.HybridLabyrinth,
            RoomType.Blank, RoomType.Hallway, RoomType.Living, RoomType.Bedroom, RoomType.Study,
            RoomType.Kitchen, RoomType.Storage, RoomType.Junction, RoomType.Bathroom, RoomType.Dining, RoomType.Bedroom)
    };

    [MenuItem("Vektor Kill House/Variants/Build All Ten Scenes", priority = 20)]
    public static void BuildAll()
    {
        EnsureFolder(SceneFolder);
        JArray reports = new JArray();
        for (int index = 0; index < Variants.Length; index++)
            reports.Add(BuildVariant(Variants[index], index));
        WriteAggregateReport(reports);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("[Vektor Kill House] Built and statically validated all ten native-only scenes.");
    }

    private static Variant V(string id, string sceneName, string moves, string portals, LayoutMotif motif, params RoomType[] rooms)
    {
        return new Variant(id, sceneName, moves, new[] { RoomType.Safe }.Concat(rooms).ToArray(), portals, motif);
    }

    private static JObject BuildVariant(Variant variant, int variantIndex)
    {
        Layout layout = BuildLayout(variant, variantIndex);
        Vector2Int[] cells = layout.Cells;
        Vector2[] roomSizes = BuildRoomSizes(layout.Rooms, variantIndex, cells);
        Vector3[] roomCenters = BuildPackedRoomCenters(cells, roomSizes);
        ConnectionPlan[] connections = BuildConnectionPlans(layout, roomSizes, roomCenters, variant, variantIndex);
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        RenderSettings.ambientIntensity = 0f;
        RenderSettings.reflectionIntensity = 0f;
        GameObject root = new GameObject("VEKTOR_KILLHOUSE_" + (variantIndex + 1).ToString("00"));
        BuildMetadata(root, variant, variantIndex);
        Transform warehouseRoot = Child(root, "05_HIGH_WAREHOUSE_SHELL").transform;
        Transform roomsRoot = Child(root, "10_ROOMS").transform;
        Transform boundariesRoot = Child(root, "20_BOUNDARIES").transform;
        Transform propsRoot = Child(root, "30_NATIVE_ROOM_DRESSING").transform;
        Transform runtimeDependenciesRoot = Child(root, "50_RUNTIME_DEPENDENCIES").transform;
        Transform markersRoot = Child(root, "60_GAMEPLAY_MARKERS").transform;
        Transform lightsRoot = Child(root, "70_LIGHTING").transform;
        Instantiate(KillHouseDoorV2ShellBuilder.LoadAudioBank(), runtimeDependenciesRoot, "NATIVE_DOORV2_AUDIO_BANK");
        BuildWarehouseShell(warehouseRoot, roomCenters, roomSizes);
        Physics.SyncTransforms();
        Collider[] warehouseRoofColliders = FindWarehouseRoofColliders(warehouseRoot);
        if (warehouseRoofColliders.Length == 0)
            throw new InvalidDataException("The exact warehouse roof has no enabled collision surface for fixture mounting.");

        var roomObjects = new List<GameObject>();
        for (int index = 0; index < cells.Length; index++)
        {
            Vector3 center = roomCenters[index];
            GameObject room = Child(roomsRoot.gameObject,
                index == 0 ? "ROOM_00_FIXED_SAFE_ROOM" : "ROOM_" + index.ToString("00") + "_" + layout.Rooms[index].ToString().ToUpperInvariant());
            room.transform.position = center;
            roomObjects.Add(room);
            BuildRoomFloor(room.transform, center, index, roomSizes[index]);
            if (index == 0) BuildFixedSafeRoomDressing(propsRoot, center, roomSizes[index], connections);
            else BuildRoomDressing(propsRoot, layout.Rooms[index], center, roomSizes[index], variantIndex, index,
                variant.Motif, connections);
            BuildRoomLights(lightsRoot, layout.Rooms[index], center, roomSizes[index], variantIndex, index,
                warehouseRoofColliders);
        }
        BuildCenterRoomDressingForScene(propsRoot, layout, roomCenters, roomSizes, variantIndex, variant.Motif,
            connections);
        BuildFallbackDirectionalLight(lightsRoot);
        BuildVanillaIndoorRenderVolume(lightsRoot);

        BuildBoundaries(boundariesRoot, layout, roomSizes, roomCenters, connections, variantIndex);
        Physics.SyncTransforms();
        BuildGameplayMarkers(markersRoot, propsRoot, layout, roomCenters, roomSizes, connections, variantIndex);
        Physics.SyncTransforms();

        if (!EditorSceneManager.SaveScene(scene, variant.ScenePath))
            throw new IOException("Could not save kill-house scene " + variant.ScenePath + ".");
        SceneAsset sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(variant.ScenePath);
        AssetDatabase.SetLabels(sceneAsset, new[]
        {
            "vektor-killhouse", "single-floor", "operator-native-only", variant.Id, "random-scene-variant"
        });
        JObject report = Validate(root, variant, variantIndex, layout, roomSizes, roomCenters, connections);
        WriteVariantReport(report, variantIndex);
        return report;
    }

    private static Vector2Int[] BuildCycle(string moves)
    {
        var cells = new List<Vector2Int> { Vector2Int.zero };
        Vector2Int cursor = Vector2Int.zero;
        for (int index = 0; index < moves.Length; index++)
        {
            Vector2Int delta = moves[index] == 'E' ? Vector2Int.right : moves[index] == 'W' ? Vector2Int.left :
                moves[index] == 'N' ? Vector2Int.up : Vector2Int.down;
            cursor += delta;
            if (index < moves.Length - 1)
            {
                if (cells.Contains(cursor)) throw new InvalidDataException("Cycle revisits " + cursor + " before closing.");
                cells.Add(cursor);
            }
        }
        if (cursor != Vector2Int.zero) throw new InvalidDataException("Cycle does not close.");
        if (cells[1] != Vector2Int.right || cells[cells.Count - 1] != Vector2Int.up)
            throw new InvalidDataException("Cycle changed the fixed safe-room east-out/north-return interfaces.");
        return cells.ToArray();
    }

    private static Layout BuildLayout(Variant variant, int variantIndex)
    {
        var cells = BuildCycle(variant.Moves).ToList();
        var rooms = variant.Rooms.ToList();
        int baseCycleCount = cells.Count;
        int targetRoomCount = 19 + variantIndex % 3;
        RoomType[] annexTypes =
        {
            RoomType.Bedroom, RoomType.Storage, RoomType.Bathroom, RoomType.Study,
            RoomType.Blank, RoomType.Kitchen, RoomType.Bedroom, RoomType.Dining
        };

        // Hallways are spaces in the graph, not anonymous gaps. Give every base hallway a lateral room first.
        int annexOrdinal = 0;
        foreach (int hallIndex in Enumerable.Range(1, baseCycleCount - 1).Where(index => rooms[index] == RoomType.Hallway))
        {
            int addedForHall = 0;
            foreach (Vector2Int direction in PreferredHallSideDirections(cells, hallIndex, baseCycleCount, variantIndex))
            {
                if (cells.Count >= targetRoomCount) break;
                if (TryAddAnnex(cells, rooms, hallIndex, direction, annexTypes[(variantIndex + annexOrdinal++) % annexTypes.Length]))
                {
                    addedForHall++;
                    if (addedForHall >= 2) break;
                }
            }
        }

        // Prefer cells that touch two existing spaces; these create secondary circulation loops and cross-links.
        while (cells.Count < targetRoomCount)
        {
            Vector2Int? bridge = FindBridgeCell(cells, variantIndex + cells.Count);
            if (bridge.HasValue)
            {
                cells.Add(bridge.Value);
                rooms.Add(annexTypes[(variantIndex * 3 + annexOrdinal++) % annexTypes.Length]);
                continue;
            }

            bool added = false;
            int[] anchorOrder = Enumerable.Range(1, cells.Count - 1)
                .OrderBy(index => AnnexAnchorPriority(rooms[index]))
                .ThenBy(index => (index * 17 + variantIndex * 11) % 31)
                .ToArray();
            foreach (int anchorIndex in anchorOrder)
            {
                foreach (Vector2Int direction in RotatedDirections(anchorIndex + variantIndex))
                {
                    if (!TryAddAnnex(cells, rooms, anchorIndex, direction,
                        annexTypes[(variantIndex * 5 + annexOrdinal++) % annexTypes.Length])) continue;
                    added = true;
                    break;
                }
                if (added) break;
            }
            if (!added) throw new InvalidDataException("Could not expand " + variant.Id + " to its required indoor footprint.");
        }

        return new Layout(cells.ToArray(), rooms.ToArray(), baseCycleCount);
    }

    private static IEnumerable<Vector2Int> PreferredHallSideDirections(List<Vector2Int> cells, int hallIndex,
        int baseCycleCount, int variantIndex)
    {
        Vector2Int incoming = cells[(hallIndex - 1 + baseCycleCount) % baseCycleCount] - cells[hallIndex];
        Vector2Int outgoing = cells[(hallIndex + 1) % baseCycleCount] - cells[hallIndex];
        var candidates = new List<Vector2Int>();
        if (incoming.x != 0 && outgoing.x != 0)
            candidates.AddRange(new[] { Vector2Int.up, Vector2Int.down });
        else if (incoming.y != 0 && outgoing.y != 0)
            candidates.AddRange(new[] { Vector2Int.left, Vector2Int.right });
        else
            candidates.AddRange(RotatedDirections(hallIndex + variantIndex).Where(direction => direction != incoming && direction != outgoing));
        if (((hallIndex + variantIndex) & 1) != 0) candidates.Reverse();
        return candidates;
    }

    private static int AnnexAnchorPriority(RoomType type)
    {
        if (type == RoomType.Hallway) return 0;
        if (type == RoomType.Junction) return 1;
        if (type == RoomType.Living || type == RoomType.Blank) return 2;
        return 3;
    }

    private static Vector2Int[] RotatedDirections(int seed)
    {
        Vector2Int[] source = { Vector2Int.right, Vector2Int.up, Vector2Int.left, Vector2Int.down };
        return Enumerable.Range(0, source.Length).Select(index => source[(index + seed) % source.Length]).ToArray();
    }

    private static bool TryAddAnnex(List<Vector2Int> cells, List<RoomType> rooms, int anchorIndex,
        Vector2Int direction, RoomType type)
    {
        Vector2Int candidate = cells[anchorIndex] + direction;
        if (cells.Contains(candidate) || IsForbiddenSafeNeighbor(candidate)) return false;
        cells.Add(candidate);
        rooms.Add(type);
        return true;
    }

    private static Vector2Int? FindBridgeCell(List<Vector2Int> cells, int seed)
    {
        var occupied = new HashSet<Vector2Int>(cells);
        var candidates = new HashSet<Vector2Int>();
        foreach (Vector2Int cell in cells)
            foreach (Vector2Int direction in RotatedDirections(seed))
                if (!occupied.Contains(cell + direction) && !IsForbiddenSafeNeighbor(cell + direction))
                    candidates.Add(cell + direction);
        return candidates
            .Where(candidate => RotatedDirections(0).Count(direction => occupied.Contains(candidate + direction)) >= 2)
            .OrderByDescending(candidate => RotatedDirections(0).Count(direction => occupied.Contains(candidate + direction)))
            .ThenBy(candidate => (Mathf.Abs(candidate.x) + Mathf.Abs(candidate.y) + seed) % 7)
            .Select(candidate => (Vector2Int?)candidate)
            .FirstOrDefault();
    }

    private static bool IsForbiddenSafeNeighbor(Vector2Int cell)
    {
        return Mathf.Abs(cell.x) + Mathf.Abs(cell.y) == 1 && cell != Vector2Int.right && cell != Vector2Int.up;
    }

    private static Vector2[] BuildRoomSizes(RoomType[] roomTypes, int variantIndex, Vector2Int[] cells)
    {
        var sizes = new Vector2[cells.Length];
        for (int index = 0; index < cells.Length; index++)
        {
            RoomType type = roomTypes[index];
            int choice = Mathf.Abs(variantIndex * 13 + index * 7) % 4;
            if (type == RoomType.Safe) sizes[index] = new Vector2(LargeRoomSize, LargeRoomSize);
            else if (type == RoomType.Hallway)
            {
                Vector2Int[] neighbors = RotatedDirections(0).Where(direction => cells.Contains(cells[index] + direction)).ToArray();
                bool longAlongX = neighbors.Count(direction => direction.x != 0) > neighbors.Count(direction => direction.y != 0) ||
                                  (neighbors.Count(direction => direction.x != 0) == neighbors.Count(direction => direction.y != 0) && ((variantIndex + index) & 1) == 0);
                float length = choice == 0 ? LargeRoomSize : ExpansiveRoomSize;
                sizes[index] = longAlongX ? new Vector2(length, StandardRoomSize) : new Vector2(StandardRoomSize, length);
            }
            else if (type == RoomType.Living || type == RoomType.Blank || type == RoomType.Junction)
                sizes[index] = choice == 0 ? new Vector2(LargeRoomSize, LargeRoomSize) :
                    choice == 1 ? new Vector2(ExpansiveRoomSize, LargeRoomSize) :
                    choice == 2 ? new Vector2(LargeRoomSize, ExpansiveRoomSize) :
                                  new Vector2(ExpansiveRoomSize, ExpansiveRoomSize);
            else if (type == RoomType.Bedroom)
                sizes[index] = choice == 0 ? new Vector2(StandardRoomSize, StandardRoomSize) :
                    choice == 1 ? new Vector2(StandardRoomSize, LargeRoomSize) :
                    choice == 2 ? new Vector2(LargeRoomSize, StandardRoomSize) :
                                  new Vector2(LargeRoomSize, LargeRoomSize);
            else if (type == RoomType.Bathroom || type == RoomType.Storage || type == RoomType.Study)
                sizes[index] = choice < 2 ? new Vector2(StandardRoomSize, StandardRoomSize) :
                    choice == 2 ? new Vector2(StandardRoomSize, LargeRoomSize) :
                                  new Vector2(LargeRoomSize, StandardRoomSize);
            else
                sizes[index] = choice == 0 ? new Vector2(StandardRoomSize, LargeRoomSize) :
                    choice == 1 ? new Vector2(LargeRoomSize, StandardRoomSize) :
                    choice == 2 ? new Vector2(LargeRoomSize, LargeRoomSize) :
                                  new Vector2(ExpansiveRoomSize, LargeRoomSize);

            int connectionSides = RotatedDirections(0).Count(direction => cells.Contains(cells[index] + direction));
            bool furnishedRoom = type != RoomType.Safe && type != RoomType.Hallway &&
                                  type != RoomType.Blank && type != RoomType.Junction;
            if (furnishedRoom && connectionSides >= 3)
                sizes[index] = new Vector2(Mathf.Max(LargeRoomSize, sizes[index].x),
                    Mathf.Max(LargeRoomSize, sizes[index].y));
        }

        // Keep every scene visibly mixed even when a variant's deterministic type/size choices skew square.
        int elongated = sizes.Count(size => !Mathf.Approximately(size.x, size.y));
        for (int index = 1; index < sizes.Length && elongated < 8; index++)
        {
            if (!Mathf.Approximately(sizes[index].x, sizes[index].y)) continue;
            if (roomTypes[index] == RoomType.Hallway) continue;
            float shortSide = sizes[index].x <= StandardRoomSize ? StandardRoomSize : LargeRoomSize;
            float longSide = sizes[index].x <= StandardRoomSize ? LargeRoomSize : ExpansiveRoomSize;
            sizes[index] = ((variantIndex + index) & 1) == 0 ? new Vector2(shortSide, longSide) :
                                                               new Vector2(longSide, shortSide);
            elongated++;
        }
        return sizes;
    }

    private static Vector3[] BuildPackedRoomCenters(Vector2Int[] cells, Vector2[] roomSizes)
    {
        var columnWidths = cells.Select((cell, index) => new { cell.x, Width = roomSizes[index].x })
            .GroupBy(item => item.x)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Width));
        var rowDepths = cells.Select((cell, index) => new { cell.y, Depth = roomSizes[index].y })
            .GroupBy(item => item.y)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Depth));

        Dictionary<int, float> xPositions = BuildPackedAxis(columnWidths);
        Dictionary<int, float> zPositions = BuildPackedAxis(rowDepths);
        return cells.Select(cell => new Vector3(xPositions[cell.x], 0f, zPositions[cell.y])).ToArray();
    }

    private static Dictionary<int, float> BuildPackedAxis(IReadOnlyDictionary<int, float> extents)
    {
        if (!extents.ContainsKey(0)) throw new InvalidDataException("Packed layout lost the fixed safe-room origin axis.");
        int minimum = extents.Keys.Min();
        int maximum = extents.Keys.Max();
        var positions = new Dictionary<int, float> { [0] = 0f };
        for (int coordinate = 1; coordinate <= maximum; coordinate++)
        {
            if (!extents.ContainsKey(coordinate) || !extents.ContainsKey(coordinate - 1))
                throw new InvalidDataException("Packed layout contains a disconnected positive-axis column or row.");
            positions[coordinate] = positions[coordinate - 1] +
                (extents[coordinate - 1] + extents[coordinate]) * .5f;
        }
        for (int coordinate = -1; coordinate >= minimum; coordinate--)
        {
            if (!extents.ContainsKey(coordinate) || !extents.ContainsKey(coordinate + 1))
                throw new InvalidDataException("Packed layout contains a disconnected negative-axis column or row.");
            positions[coordinate] = positions[coordinate + 1] -
                (extents[coordinate + 1] + extents[coordinate]) * .5f;
        }
        return positions;
    }

    private static void BuildMetadata(GameObject root, Variant variant, int index)
    {
        Transform parent = Child(root, "00_METADATA").transform;
        foreach (string marker in new[]
        {
            MapMarker, PveSpawnSetMarker, PvpSpawnSetMarker, "VARIANT_ID_" + variant.Id,
            "VARIANT_INDEX_" + (index + 1).ToString("00"),
            "SAFE_ROOM_MODULE_KH_SAFE_ROOM_V1", "PRIMARY_ROUTE_CLOSED_LOOP_ALL_ROOMS",
            "NATIVE_ASSETS_ONLY", "VISIBLE_BUILTIN_PRIMITIVES_0", "SINGLE_FLOOR", "ENEMY_COUNT_RANGE_10_TO_15",
            "PVP_SEPARATE_TEAM_SPAWN_SECTORS_6V6",
            "EXPANSIVE_VARIABLE_ROOMS_4M_8M_12M", "HALLWAYS_ARE_GRAPH_SPACES_WITH_SIDE_DOORS",
            "PACKED_FOOTPRINT_BY_ACTUAL_ROOM_EXTENTS", "SHARED_WALLS_AND_SHORT_CONNECTORS_MAX_8M",
            "DOORV2_OFFICIAL_PREFAB_REQUIRED", "SPATIAL_MOTIF_" + variant.Motif.ToString().ToUpperInvariant(),
            "LOW_PARTITIONS_USE_NATIVE_SUBURB_COUNTERS", "PILLAR_CANDIDATE_PENDING_COMPLETE_CLOSURE",
            "OPEN_TOP_KILLHOUSE_INSIDE_HIGH_WAREHOUSE", "WAREHOUSE_ROOF_HEIGHT_11_35M",
            "VANILLA_INDUSTRIAL_FLUORESCENT_FIXTURES", "NO_ROOM_HEIGHT_CEILINGS",
            "NO_SKY_NO_AMBIENT_NO_REFLECTION", "ONLY_VISIBLE_WAREHOUSE_FIXTURES_EMIT_LIGHT",
            "WALL_BACKED_FURNITURE_WITH_PORTAL_CLEARANCE", "CENTER_ROOM_FURNITURE_FULL_NATIVE_SCALE",
            "CENTER_ROOM_PROP_CAPSULE_GRID_CIRCULATION", "RUGGED_VANILLA_METAL_DOOR_VISUAL"
        }) Child(parent.gameObject, marker);
    }

    private static void BuildRoomFloor(Transform parent, Vector3 center, int index, Vector2 size)
    {
        GameObject floorPrefab = KillHouseNativePrefabBuilder.Load(index == 0 ? "Floor_5x_5x" :
            (index % 3 == 0 ? "Floor_12x_8x" : "Floor_5x_5x"));
        GameObject floor = Instantiate(floorPrefab, parent, "NATIVE_Floor");
        FitHorizontal(floor, center, 0f, size.x, size.y, false);
    }

    private static void BuildWarehouseShell(Transform parent, Vector3[] roomCenters, Vector2[] roomSizes)
    {
        float minimumX = Enumerable.Range(0, roomCenters.Length)
            .Min(index => roomCenters[index].x - roomSizes[index].x * .5f) - WarehouseMargin;
        float maximumX = Enumerable.Range(0, roomCenters.Length)
            .Max(index => roomCenters[index].x + roomSizes[index].x * .5f) + WarehouseMargin;
        float minimumZ = Enumerable.Range(0, roomCenters.Length)
            .Min(index => roomCenters[index].z - roomSizes[index].y * .5f) - WarehouseMargin;
        float maximumZ = Enumerable.Range(0, roomCenters.Length)
            .Max(index => roomCenters[index].z + roomSizes[index].y * .5f) + WarehouseMargin;
        float width = Mathf.Ceil((maximumX - minimumX) / WallModuleWidth) * WallModuleWidth;
        float depth = Mathf.Ceil((maximumZ - minimumZ) / WallModuleWidth) * WallModuleWidth;
        Vector3 center = new Vector3((minimumX + maximumX) * .5f, 0f, (minimumZ + maximumZ) * .5f);
        minimumX = center.x - width * .5f;
        maximumX = center.x + width * .5f;
        minimumZ = center.z - depth * .5f;
        maximumZ = center.z + depth * .5f;

        const float sourceWidth = 53.91834f;
        const float sourceDepth = 33.80742f;
        bool rotate = depth > width;
        Transform shell = Child(parent.gameObject, "NATIVE_WarehousePvpCompleteShell").transform;
        shell.position = center;
        shell.rotation = Quaternion.Euler(0f, rotate ? 90f : 0f, 0f);
        shell.localScale = rotate
            ? new Vector3(depth / sourceWidth, 1f, width / sourceDepth)
            : new Vector3(width / sourceWidth, 1f, depth / sourceDepth);
        Child(shell.gameObject, "WAREHOUSE_PREFAB_PVP_WOODS_EXACT_FOUR_PART");

        PlaceWarehousePart(shell, "Base Warehouse", "NATIVE_WarehouseBase",
            new Vector3(.1577339f, 6.365199f, .0034256f),
            new Quaternion(-.70710683f, 0f, 0f, .7071067f));
        PlaceWarehousePart(shell, "OverHead Support", "NATIVE_WarehouseOverHeadSupport",
            new Vector3(-26.2789f, 9.10916f, -.001052f),
            new Quaternion(-.70710683f, 0f, 0f, .7071067f));
        PlaceWarehousePart(shell, "Roof", "NATIVE_WarehouseRoof",
            new Vector3(.1577339f, 6.365199f, .0034256f),
            new Quaternion(-.70710683f, 0f, 0f, .7071067f));
        PlaceWarehousePart(shell, "Support 2", "NATIVE_WarehouseSupport2",
            new Vector3(.1577339f, 6.38f, .0034275f),
            new Quaternion(.5f, .49999997f, .5f, -.5f));

        // The shipped four-part Warehouse New shell has no filled walkable slab. Use the exact
        // non-primitive Floor mesh that is co-located with the active warehouse ground in level11,
        // set just below the residential room floors so only the surrounding apron remains visible.
        GameObject ground = Instantiate(KillHouseNativePrefabBuilder.Load("Floor"), parent,
            "NATIVE_WarehouseGroundApron");
        FitHorizontal(ground, center, WarehouseGroundElevation, width, depth, false);
        Child(ground, "WAREHOUSE_GROUND_LEVEL11_FLOOR_MESH152_MATERIAL26");
        Child(ground, "WAREHOUSE_GROUND_PROVENANCE_APPEARANCE_GO104_GEOMETRY_GO9601");
        // Every non-neighbour room side is closed by native wall modules and every connector is
        // closed by paired side walls. The apron is therefore warehouse scenery/collision outside
        // the sealed kill-house perimeter, not an AI navigation source.
        Child(ground, "WAREHOUSE_APRON_NAV_EXCLUDED_ENCLOSED_PERIMETER");
    }

    private static void PlaceWarehousePart(Transform parent, string prefabName, string instanceName,
        Vector3 localPosition, Quaternion localRotation)
    {
        GameObject instance = Instantiate(KillHouseNativePrefabBuilder.Load(prefabName), parent, instanceName);
        instance.transform.localPosition = localPosition;
        instance.transform.localRotation = localRotation;
        instance.transform.localScale = Vector3.one;
    }

    private static ConnectionPlan[] BuildConnectionPlans(Layout layout, Vector2[] roomSizes, Vector3[] roomCenters,
        Variant variant, int variantIndex)
    {
        var plans = new List<ConnectionPlan>();
        var lookup = layout.Cells.Select((cell, index) => new { cell, index })
            .ToDictionary(item => item.cell, item => item.index);
        var assignedByRoomAndAxis = new Dictionary<string, List<Tuple<int, float>>>();
        var axialCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int roomA = 0; roomA < layout.Cells.Length; roomA++)
        {
            foreach (Vector2Int direction in new[] { Vector2Int.right, Vector2Int.up })
            {
                if (!lookup.TryGetValue(layout.Cells[roomA] + direction, out int roomB)) continue;
                int edgeIndex = roomA < layout.BaseCycleCount && roomB < layout.BaseCycleCount
                    ? CycleEdge(roomA, roomB, layout.BaseCycleCount)
                    : -1;
                bool fixedSafeEdge = roomA == 0 || roomB == 0;
                if (fixedSafeEdge && edgeIndex < 0) continue;
                PortalKind portal = edgeIndex >= 0 ? variant.Portals[edgeIndex] :
                    DetermineSecondaryPortal(layout.Rooms[roomA], layout.Rooms[roomB], roomA, roomB, variantIndex);
                float sideA = direction.x != 0 ? roomSizes[roomA].y : roomSizes[roomA].x;
                float sideB = direction.x != 0 ? roomSizes[roomB].y : roomSizes[roomB].x;
                float minimumSide = Mathf.Min(sideA, sideB);
                float[] offsets = Enumerable.Range(0, Mathf.RoundToInt(minimumSide / WallModuleWidth))
                    .Select(slot => -minimumSide * .5f + WallModuleWidth * .5f + slot * WallModuleWidth)
                    .ToArray();
                if (offsets.Length == 0) throw new InvalidDataException("Connection has no native portal slot.");

                int seed = Mathf.Abs(variantIndex * 97 + roomA * 31 + roomB * 17 + (direction.x != 0 ? 11 : 23));
                float bestOffset = offsets[seed % offsets.Length];
                int bestScore = int.MaxValue;
                foreach (float candidate in offsets.Select((_, index) => offsets[(index + seed) % offsets.Length]))
                {
                    int score = PortalAlignmentPenalty(layout, assignedByRoomAndAxis, roomA, roomB, direction, candidate);
                    float tangentCoordinate = direction.x != 0 ? roomCenters[roomA].z + candidate : roomCenters[roomA].x + candidate;
                    string axialKey = PortalAxisKey(direction, tangentCoordinate);
                    score += axialCounts.TryGetValue(axialKey, out int count) ? count * 20 : 0;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    bestOffset = candidate;
                }

                var plan = new ConnectionPlan(roomA, roomB, direction, portal, fixedSafeEdge, bestOffset);
                plans.Add(plan);
                RecordPortalAssignment(assignedByRoomAndAxis, roomA, direction, bestOffset);
                RecordPortalAssignment(assignedByRoomAndAxis, roomB, -direction, bestOffset);
                float absoluteTangent = direction.x != 0 ? roomCenters[roomA].z + bestOffset : roomCenters[roomA].x + bestOffset;
                string selectedAxisKey = PortalAxisKey(direction, absoluteTangent);
                axialCounts[selectedAxisKey] = axialCounts.TryGetValue(selectedAxisKey, out int selectedCount)
                    ? selectedCount + 1
                    : 1;
            }
        }

        return plans.ToArray();
    }

    private static int PortalAlignmentPenalty(Layout layout,
        IReadOnlyDictionary<string, List<Tuple<int, float>>> assignedByRoomAndAxis,
        int roomA, int roomB, Vector2Int direction, float candidate)
    {
        int penalty = OpposingPortalPenalty(layout, assignedByRoomAndAxis, roomA, direction, candidate);
        penalty += OpposingPortalPenalty(layout, assignedByRoomAndAxis, roomB, -direction, candidate);
        return penalty;
    }

    private static int OpposingPortalPenalty(Layout layout,
        IReadOnlyDictionary<string, List<Tuple<int, float>>> assignedByRoomAndAxis,
        int room, Vector2Int direction, float candidate)
    {
        string key = RoomAxisKey(room, direction);
        if (!assignedByRoomAndAxis.TryGetValue(key, out List<Tuple<int, float>> existing)) return 0;
        int sign = direction.x != 0 ? Math.Sign(direction.x) : Math.Sign(direction.y);
        int aligned = existing.Count(value => value.Item1 == -sign && Mathf.Abs(value.Item2 - candidate) < .1f);
        if (aligned == 0) return 0;
        // A small minority of straight hallway passages is intentional; every other room strongly rejects them.
        return aligned * (layout.Rooms[room] == RoomType.Hallway ? 80 : 1000);
    }

    private static void RecordPortalAssignment(IDictionary<string, List<Tuple<int, float>>> assignedByRoomAndAxis,
        int room, Vector2Int direction, float offset)
    {
        string key = RoomAxisKey(room, direction);
        if (!assignedByRoomAndAxis.TryGetValue(key, out List<Tuple<int, float>> values))
        {
            values = new List<Tuple<int, float>>();
            assignedByRoomAndAxis[key] = values;
        }
        int sign = direction.x != 0 ? Math.Sign(direction.x) : Math.Sign(direction.y);
        values.Add(Tuple.Create(sign, offset));
    }

    private static string RoomAxisKey(int room, Vector2Int direction)
    {
        return room.ToString("00") + (direction.x != 0 ? "_X" : "_Z");
    }

    private static string PortalAxisKey(Vector2Int direction, float tangentCoordinate)
    {
        return (direction.x != 0 ? "X_" : "Z_") + Mathf.RoundToInt(tangentCoordinate * 10f).ToString();
    }

    private static string EdgeKey(int first, int second)
    {
        return Mathf.Min(first, second).ToString("00") + "_" + Mathf.Max(first, second).ToString("00");
    }

    private static void BuildBoundaries(Transform parent, Layout layout, Vector2[] roomSizes, Vector3[] roomCenters,
        ConnectionPlan[] connections, int variantIndex)
    {
        Vector2Int[] cells = layout.Cells;
        var lookup = cells.Select((cell, index) => new { cell, index }).ToDictionary(item => item.cell, item => item.index);
        Dictionary<string, ConnectionPlan> connectionLookup = connections.ToDictionary(plan => plan.Key, plan => plan);
        Vector2Int[] directions = { Vector2Int.right, Vector2Int.up, Vector2Int.left, Vector2Int.down };
        for (int roomIndex = 0; roomIndex < cells.Length; roomIndex++)
        {
            foreach (Vector2Int direction in directions)
            {
                Vector2Int neighborCell = cells[roomIndex] + direction;
                if (!lookup.TryGetValue(neighborCell, out int neighborIndex))
                {
                    BuildRoomSideBoundary(parent, roomCenters[roomIndex], roomSizes[roomIndex], direction,
                        roomIndex, -1, PortalKind.Open, false, false, false, false, false, 0f);
                    continue;
                }

                int edgeIndex = roomIndex < layout.BaseCycleCount && neighborIndex < layout.BaseCycleCount ?
                    CycleEdge(roomIndex, neighborIndex, layout.BaseCycleCount) : -1;
                bool fixedSafeEdge = roomIndex == 0 || neighborIndex == 0;
                if (fixedSafeEdge && edgeIndex < 0)
                {
                    BuildRoomSideBoundary(parent, roomCenters[roomIndex], roomSizes[roomIndex], direction,
                        roomIndex, neighborIndex, PortalKind.Open, false, false, false, false, false, 0f);
                    continue;
                }
                ConnectionPlan connection = connectionLookup[EdgeKey(roomIndex, neighborIndex)];
                PortalKind portal = connection.Portal;
                bool indexOwner = roomIndex < neighborIndex;
                float connectionGap = ConnectionGap(roomCenters[roomIndex], roomSizes[roomIndex],
                    roomCenters[neighborIndex], roomSizes[neighborIndex], direction);
                if (connectionGap < -.01f)
                    throw new InvalidDataException("Packed rooms overlap on graph edge " + roomIndex + "-" + neighborIndex + ".");
                bool directSharedWall = connectionGap < .1f;
                float sideLength = direction.x != 0 ? roomSizes[roomIndex].y : roomSizes[roomIndex].x;
                float neighborSideLength = direction.x != 0 ? roomSizes[neighborIndex].y : roomSizes[neighborIndex].x;
                bool directBoundaryOwner = sideLength > neighborSideLength ||
                    (Mathf.Approximately(sideLength, neighborSideLength) && indexOwner);
                if (directSharedWall && !directBoundaryOwner) continue;
                bool portalFeatureOwner = directSharedWall ? directBoundaryOwner : indexOwner;
                bool sightWindow = !fixedSafeEdge && (edgeIndex < 0 ||
                    (edgeIndex + variantIndex) % 4 == 0 || (edgeIndex + variantIndex * 2) % 7 == 0);
                bool hallwaySideDoor = edgeIndex < 0 && portal == PortalKind.Door &&
                    (layout.Rooms[roomIndex] == RoomType.Hallway || layout.Rooms[neighborIndex] == RoomType.Hallway);
                BuildRoomSideBoundary(parent, roomCenters[roomIndex], roomSizes[roomIndex], direction,
                    roomIndex, neighborIndex, portal, true, portalFeatureOwner && portal == PortalKind.Door,
                    portalFeatureOwner && sightWindow, fixedSafeEdge, portalFeatureOwner && hallwaySideDoor,
                    connection.PortalOffset);

                if (indexOwner && !directSharedWall)
                    BuildConnectionCorridor(parent, roomCenters[roomIndex], roomSizes[roomIndex],
                        roomCenters[neighborIndex], roomSizes[neighborIndex], direction, roomIndex, neighborIndex,
                        connection.PortalOffset);
            }
        }
    }

    private static PortalKind DetermineSecondaryPortal(RoomType first, RoomType second,
        int firstIndex, int secondIndex, int variantIndex)
    {
        if (first == RoomType.Hallway || second == RoomType.Hallway) return PortalKind.Door;
        if (first == RoomType.Bathroom || second == RoomType.Bathroom ||
            first == RoomType.Bedroom || second == RoomType.Bedroom ||
            first == RoomType.Storage || second == RoomType.Storage) return PortalKind.Door;
        return (firstIndex * 7 + secondIndex * 3 + variantIndex) % 3 == 0 ? PortalKind.Door : PortalKind.Open;
    }

    private static float ConnectionGap(Vector3 centerA, Vector2 sizeA, Vector3 centerB, Vector2 sizeB,
        Vector2Int direction)
    {
        float centerDistance = direction.x != 0 ? Mathf.Abs(centerB.x - centerA.x) : Mathf.Abs(centerB.z - centerA.z);
        float extentA = direction.x != 0 ? sizeA.x * .5f : sizeA.y * .5f;
        float extentB = direction.x != 0 ? sizeB.x * .5f : sizeB.y * .5f;
        return centerDistance - extentA - extentB;
    }

    private static int CycleEdge(int a, int b, int count)
    {
        if ((a + 1) % count == b) return a;
        if ((b + 1) % count == a) return b;
        return -1;
    }

    private static void BuildRoomSideBoundary(Transform parent, Vector3 roomCenter, Vector2 size, Vector2Int outward,
        int roomIndex, int neighborIndex, PortalKind portal, bool connected, bool ownsDoor,
        bool sightWindow, bool fixedSafeEdge, bool hallwaySideDoor, float portalOffset)
    {
        float outwardExtent = outward.x != 0 ? size.x * .5f : size.y * .5f;
        float sideLength = outward.x != 0 ? size.y : size.x;
        Vector3 sideCenter = roomCenter + new Vector3(outward.x, 0f, outward.y) * outwardExtent;
        Vector3 tangent = outward.x != 0 ? Vector3.forward : Vector3.right;
        float yaw = outward.x != 0 ? 0f : 90f;
        int slots = Mathf.RoundToInt(sideLength / WallModuleWidth);
        int portalSlot = Mathf.Clamp(Mathf.RoundToInt((portalOffset + sideLength * .5f - WallModuleWidth * .5f) /
                                                      WallModuleWidth), 0, slots - 1);
        float selectedOffset = -sideLength * .5f + WallModuleWidth * .5f + portalSlot * WallModuleWidth;
        if (connected && Mathf.Abs(selectedOffset - portalOffset) > .05f)
            throw new InvalidDataException("Staggered portal offset does not land on a native 2 m wall slot.");
        int windowSlot = slots - 1;
        string side = outward == Vector2Int.right ? "E" : outward == Vector2Int.left ? "W" :
                      outward == Vector2Int.up ? "N" : "S";
        string key = neighborIndex < 0 ? roomIndex.ToString("00") + "_" + side :
            Mathf.Min(roomIndex, neighborIndex).ToString("00") + "_" + Mathf.Max(roomIndex, neighborIndex).ToString("00");

        for (int slot = 0; slot < slots; slot++)
        {
            float offset = -sideLength * .5f + WallModuleWidth * .5f + slot * WallModuleWidth;
            Vector3 position = sideCenter + tangent * offset;
            if (connected && slot == portalSlot)
            {
                if (ownsDoor)
                {
                    PlaceWall(parent, "Wall2MeterDoor", "NATIVE_DoorWall_" + key, position, yaw);
                    CreateDoorV2Socket(parent, "DOORV2_SOCKET_" + key, position, yaw, fixedSafeEdge, hallwaySideDoor);
                }
                else
                {
                    GameObject opening = Child(parent.gameObject, "OPEN_CONNECTION_" + key + "_" + roomIndex.ToString("00"));
                    opening.transform.position = position;
                    opening.transform.rotation = Quaternion.Euler(0f, yaw + 90f, 0f);
                }
                continue;
            }

            bool useWindow = sightWindow && slot == windowSlot && slot != portalSlot;
            PlaceWall(parent, useWindow ? "Wall2MeterWindow" : "Wall2Meter",
                useWindow ? "NATIVE_SightWindow_" + key :
                    "NATIVE_RoomWall_" + roomIndex.ToString("00") + "_" + side + "_" + slot.ToString("00"),
                position, yaw);
        }
    }

    private static void BuildConnectionCorridor(Transform parent, Vector3 centerA, Vector2 sizeA,
        Vector3 centerB, Vector2 sizeB, Vector2Int direction, int roomA, int roomB, float portalOffset)
    {
        Vector3 vector = new Vector3(direction.x, 0f, direction.y);
        Vector3 tangent = direction.x != 0 ? Vector3.forward : Vector3.right;
        float extentA = direction.x != 0 ? sizeA.x * .5f : sizeA.y * .5f;
        float extentB = direction.x != 0 ? sizeB.x * .5f : sizeB.y * .5f;
        Vector3 start = centerA + vector * extentA + tangent * portalOffset;
        Vector3 end = centerB - vector * extentB + tangent * portalOffset;
        float length = Vector3.Distance(start, end);
        if (length < 1.9f || length > MaximumConnectorLength + .01f ||
            Mathf.Abs(length / WallModuleWidth - Mathf.Round(length / WallModuleWidth)) > .01f)
            throw new InvalidDataException("Connection corridor length must be 2-8 m in exact native 2 m modules.");

        string key = Mathf.Min(roomA, roomB).ToString("00") + "_" + Mathf.Max(roomA, roomB).ToString("00");
        Vector3 midpoint = (start + end) * .5f;
        GameObject floor = Instantiate(KillHouseNativePrefabBuilder.Load("Floor_5x_5x"), parent,
            "NATIVE_ConnectorFloor_" + key);
        FitHorizontal(floor, midpoint, 0f, direction.x != 0 ? length : WallModuleWidth,
            direction.x != 0 ? WallModuleWidth : length, false);

        int wallSegments = Mathf.RoundToInt(length / WallModuleWidth);
        float wallYaw = direction.x != 0 ? 90f : 0f;
        Vector3 unit = vector.normalized;
        for (int segment = 0; segment < wallSegments; segment++)
        {
            Vector3 segmentCenter = start + unit * (WallModuleWidth * .5f + segment * WallModuleWidth);
            PlaceWall(parent, "Wall2Meter", "NATIVE_ConnectorWall_" + key + "_L" + segment.ToString("00"),
                segmentCenter - tangent, wallYaw);
            PlaceWall(parent, "Wall2Meter", "NATIVE_ConnectorWall_" + key + "_R" + segment.ToString("00"),
                segmentCenter + tangent, wallYaw);
        }
    }

    private static void PlaceWall(Transform parent, string meshName, string name, Vector3 center, float yaw)
    {
        GameObject instance = Instantiate(KillHouseNativePrefabBuilder.Load(meshName), parent, name);
        AlignGrounded(instance, center, Quaternion.Euler(0f, yaw, 0f), Vector3.one);
    }

    private static void CreateDoorV2Socket(Transform parent, string name, Vector3 center, float wallYaw,
        bool fixedSafeEdge, bool hallwaySideDoor)
    {
        // The training doorway is authored in the Y-Z plane while the residential DoorV2 leaf is
        // authored in the X-Y plane. Matching their yaw values puts the closed leaf across the
        // opening. Rotate the complete vanilla door graph by 90 degrees so its leaf plane matches
        // the native doorway plane. The DoorV2 root is its hinge, not its leaf center, so place the
        // hinge one measured half-leaf away from the measured Wall2MeterDoor aperture center.
        float doorYaw = wallYaw + 90f;
        GameObject socket = Child(parent.gameObject, name);
        Vector3 wallTangent = Quaternion.Euler(0f, wallYaw, 0f) * Vector3.forward;
        socket.transform.position = center + wallTangent * DoorwayOpeningTangentOffset;
        socket.transform.rotation = Quaternion.Euler(0f, doorYaw, 0f);
        GameObject shell = Instantiate(KillHouseDoorV2ShellBuilder.LoadShell(), socket.transform, "NATIVE_DOORV2_SHELL");
        shell.transform.localPosition = Vector3.left * DoorHingeToLeafCenter;
        shell.transform.localRotation = Quaternion.identity;
        shell.transform.localScale = Vector3.one;
        Child(socket, "OFFICIAL_DOORV2_BASE_REQUIRED");
        if (fixedSafeEdge) Child(socket, "FIXED_SAFE_ROOM_DOOR_INTERFACE");
        if (hallwaySideDoor) Child(socket, "HALLWAY_SIDE_DOOR");
    }

    private static void BuildFixedSafeRoomDressing(Transform parent, Vector3 center, Vector2 size,
        IReadOnlyList<ConnectionPlan> connections)
    {
        Transform safe = Child(parent.gameObject, "FIXED_SAFE_ROOM_KH_SAFE_ROOM_V1").transform;
        PlaceWallBackedProp(safe, "Workdesk_solo", "NATIVE_SafeDesk", center, size, 0, connections, 3, -1.4f, .88f);
        // Keep the fixed bookcase comfortably clear of the offset DoorV2 socket approach capsule.
        // The previous +1.4 m bias cleared the authored aperture center by only millimetres and
        // overlapped the exact runtime socket-origin probe after its measured 3.91 cm tangent offset.
        PlaceWallBackedProp(safe, "Bookshelf", "NATIVE_SafeBookcase", center, size, 0, connections, 7, 1.0f, .82f);

        Vector3[] spawnOffsets =
        {
            new Vector3(-1.8f,.05f,1.6f), new Vector3(1.8f,.05f,1.6f),
            new Vector3(-1.8f,.05f,-.6f), new Vector3(1.8f,.05f,-.6f)
        };
        for (int index = 0; index < spawnOffsets.Length; index++)
        {
            GameObject marker = Child(safe.gameObject, "PVE_PlayerSpawn_" + (index + 1).ToString("00"));
            marker.transform.position = center + spawnOffsets[index];
            marker.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
        }
        GameObject exfil = Child(safe.gameObject, "PVE_ExfilZone_00");
        exfil.transform.position = center + new Vector3(0f, 1.1f, .1f);
        BoxCollider trigger = exfil.AddComponent<BoxCollider>();
        trigger.size = new Vector3(6.4f, 2.2f, 6.4f);
        trigger.isTrigger = true;
    }

    private static void BuildRoomDressing(Transform parent, RoomType type, Vector3 center, Vector2 size,
        int variantIndex, int roomIndex, LayoutMotif motif, IReadOnlyList<ConnectionPlan> connections)
    {
        Transform room = Child(parent.gameObject, "DRESSING_" + roomIndex.ToString("00") + "_" + type.ToString().ToUpperInvariant()).transform;
        float flip = ((variantIndex + roomIndex) & 1) == 0 ? 1f : -1f;
        switch (type)
        {
            case RoomType.Living:
                // Sofa_A/B and D_TV_standing are submeshes from untransported vanilla assemblies.
                // Use only complete, directly proven standalone donors until those roots are closed.
                PlaceWallBackedProp(room, "Bookshelf", "NATIVE_LivingBookcase", center, size, roomIndex,
                    connections, variantIndex + roomIndex, -1f, .7f);
                PlaceWallBackedProp(room, "Sidetable_A", "NATIVE_LivingConsole", center, size, roomIndex,
                    connections, variantIndex + roomIndex + 5, 1f, .68f);
                PlaceProp(room, "Carpet_hallway", "NATIVE_LivingCarpet", center, 90f, .75f);
                break;
            case RoomType.Bedroom:
                PlaceWallBackedProp(room, "Bed_queen", "NATIVE_Bed", center, size, roomIndex, connections,
                    variantIndex + roomIndex, 0f, .68f);
                PlaceWallBackedProp(room, "Sidetable_A", "NATIVE_SideTable", center, size, roomIndex, connections,
                    variantIndex + roomIndex + 3, flip * 1.1f, .72f);
                break;
            case RoomType.Bathroom:
                PlaceWallBackedProp(room, "T_toilet", "NATIVE_Toilet", center, size, roomIndex, connections,
                    variantIndex + roomIndex, -.9f, .72f);
                PlaceWallBackedProp(room, "T_sink", "NATIVE_Sink", center, size, roomIndex, connections,
                    variantIndex + roomIndex + 5, .9f, .64f);
                break;
            case RoomType.Kitchen:
                PlaceWallBackedProp(room, "Kitcabinet_low_1x_A", "NATIVE_KitchenCabinet", center, size,
                    roomIndex, connections, variantIndex + roomIndex, -1.2f, .78f);
                PlaceWallBackedProp(room, "Kitcabinet_full_fridge", "NATIVE_FridgeCabinet", center, size,
                    roomIndex, connections, variantIndex + roomIndex + 4, 1.2f, .74f);
                break;
            case RoomType.Dining:
                PlaceWallBackedProp(room, "Sidetable_A", "NATIVE_DiningSideboard", center, size, roomIndex,
                    connections, variantIndex + roomIndex, 0f, .74f);
                break;
            case RoomType.Study:
                PlaceWallBackedProp(room, "Workdesk_solo", "NATIVE_WorkDesk", center, size, roomIndex, connections,
                    variantIndex + roomIndex, -1f, .74f);
                PlaceWallBackedProp(room, "Bookshelf", "NATIVE_Bookcase", center, size,
                    roomIndex, connections, variantIndex + roomIndex + 5, 1f, .72f);
                break;
            case RoomType.Storage:
                PlaceWallBackedProp(room, "Bookshelf", "NATIVE_StorageShelf_A", center, size, roomIndex,
                    connections, variantIndex + roomIndex, -1.1f, .68f);
                if (size.x >= LargeRoomSize || size.y >= LargeRoomSize)
                    PlaceWallBackedProp(room, "Bookshelf", "NATIVE_StorageShelf_B", center, size, roomIndex,
                        connections, variantIndex + roomIndex + 4, 1.1f, .68f);
                PlaceWallBackedProp(room, "Kitcabinet_low_1x_A", "NATIVE_StorageCabinet", center, size, roomIndex,
                    connections, variantIndex + roomIndex + 7, 0f, .68f);
                break;
            case RoomType.Hallway:
                PlaceProp(room, "Carpet_hallway", "NATIVE_HallCarpet", center, roomIndex % 2 == 0 ? 0f : 90f, .85f);
                if (Mathf.Max(size.x, size.y) >= LargeRoomSize)
                    PlaceWallBackedProp(room, "Sidetable_A", "NATIVE_HallTable", center, size, roomIndex,
                        connections, variantIndex + roomIndex, 0f, .62f);
                break;
            case RoomType.Junction:
                PlaceProp(room, "Carpet_hallway", "NATIVE_JunctionCarpet", center, 90f, .7f);
                PlaceWallBackedProp(room, "Kitcabinet_low_1x_A", "NATIVE_JunctionStorage", center, size,
                    roomIndex, connections, variantIndex + roomIndex, 0f, .58f);
                break;
            case RoomType.Blank:
                Child(room.gameObject, "INTENTIONALLY_BLANK_TRAINING_ROOM").transform.position = center;
                break;
        }

        BuildSpatialMotif(room, type, center, size, variantIndex, roomIndex, motif, connections);
    }

    private static void BuildCenterRoomDressingForScene(Transform propsRoot, Layout layout,
        Vector3[] roomCenters, Vector2[] roomSizes, int variantIndex, LayoutMotif motif,
        IReadOnlyList<ConnectionPlan> connections)
    {
        if (!CenterRoomFurnitureContracts.TryGetValue("TABLE", out CenterRoomFurnitureContract tableContract))
            throw new InvalidDataException("The exact Kitchen_table_large center-room contract is missing.");
        if (!CenterRoomFurnitureContracts.TryGetValue("SOFA", out CenterRoomFurnitureContract sofaContract))
            throw new InvalidDataException("The complete installed sofa centre-room contract has not been registered.");

        // A centre prop is optional per room: failed candidates leave an explicit skip record and
        // never become a blocker. Give the full-size sofa first choice in each Living room, then
        // fit full-size tables around that accepted layout; furniture is never shrunk to pass.
        int sofaPlacements = 0;
        int[] livingRooms = Enumerable.Range(1, layout.Rooms.Length - 1)
            .Where(index => layout.Rooms[index] == RoomType.Living &&
                            CenterSofaRoomEligible(layout.Rooms[index], roomSizes[index], false))
            .OrderBy(index => (index * 23 + variantIndex * 13) % 41).ThenBy(index => index).ToArray();
        foreach (int roomIndex in livingRooms)
        {
            Transform dressing = FindRoomDressing(propsRoot, roomIndex);
            if (TryPlaceCenterRoomProp(dressing, sofaContract, "NATIVE_LivingSofa",
                    roomCenters[roomIndex], roomSizes[roomIndex], roomIndex, connections,
                    variantIndex * 131 + roomIndex * 29, out string failure))
                sofaPlacements++;
            else
                RecordCenterRoomPropSkip(dressing, sofaContract.Role, roomIndex, failure, roomCenters[roomIndex]);
        }

        // Every authored variant has a Living room. This fallback is deliberately narrow and runs
        // only when all Living placements were rejected. A large Blank room is treated as an open
        // training room only after the same portal, overlap and circulation gates pass.
        if (sofaPlacements == 0)
        {
            foreach (int roomIndex in Enumerable.Range(1, layout.Rooms.Length - 1)
                         .Where(index => layout.Rooms[index] == RoomType.Blank &&
                                         CenterSofaRoomEligible(layout.Rooms[index], roomSizes[index], true))
                         .OrderBy(index => (index * 31 + variantIndex * 7) % 43).ThenBy(index => index))
            {
                Transform dressing = FindRoomDressing(propsRoot, roomIndex);
                if (TryPlaceCenterRoomProp(dressing, sofaContract, "NATIVE_OpenSofa",
                        roomCenters[roomIndex], roomSizes[roomIndex], roomIndex, connections,
                        variantIndex * 149 + roomIndex * 43, out string failure))
                {
                    sofaPlacements++;
                    break;
                }
                RecordCenterRoomPropSkip(dressing, sofaContract.Role, roomIndex, failure, roomCenters[roomIndex]);
            }
        }

        if (sofaPlacements == 0)
            throw new InvalidDataException("No complete sofa placement preserved portal and room circulation in variant " +
                                           (variantIndex + 1).ToString("00") + ".");

        int tablePlacements = 0;
        IEnumerable<int> tableRooms = Enumerable.Range(1, layout.Rooms.Length - 1)
            .Where(index => CenterTableEligible(layout.Rooms[index], roomSizes[index], motif))
            .OrderBy(index => (index * 19 + variantIndex * 11) % 37)
            .ThenBy(index => index);
        foreach (int roomIndex in tableRooms)
        {
            Transform dressing = FindRoomDressing(propsRoot, roomIndex);
            if (dressing == null) throw new InvalidDataException("Missing dressing owner for room " + roomIndex + ".");
            string name = layout.Rooms[roomIndex] == RoomType.Kitchen ? "NATIVE_KitchenTable" :
                layout.Rooms[roomIndex] == RoomType.Dining ? "NATIVE_DiningTable" : "NATIVE_OpenTable";
            if (TryPlaceCenterRoomProp(dressing, tableContract, name, roomCenters[roomIndex],
                    roomSizes[roomIndex], roomIndex, connections, variantIndex * 101 + roomIndex * 17,
                    out string failure))
                tablePlacements++;
            else
                RecordCenterRoomPropSkip(dressing, tableContract.Role, roomIndex, failure, roomCenters[roomIndex]);
        }

        if (tablePlacements == 0)
            throw new InvalidDataException("No exact Kitchen_table_large centre placement preserved room circulation in " +
                                           "variant " + (variantIndex + 1).ToString("00") + ".");
    }

    private static Transform FindRoomDressing(Transform propsRoot, int roomIndex)
    {
        string prefix = "DRESSING_" + roomIndex.ToString("00") + "_";
        return propsRoot == null ? null : Enumerable.Range(0, propsRoot.childCount).Select(propsRoot.GetChild)
            .FirstOrDefault(child => child.name.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool CenterTableEligible(RoomType type, Vector2 size, LayoutMotif motif)
    {
        if (size.x < LargeRoomSize || size.y < LargeRoomSize) return false;
        if (type == RoomType.Kitchen || type == RoomType.Dining) return true;
        return type == RoomType.Living && IsResidentialOpenMotif(motif) &&
               Mathf.Max(size.x, size.y) >= ExpansiveRoomSize;
    }

    private static bool CenterSofaRoomEligible(RoomType type, Vector2 size, bool fallbackOpen)
    {
        if (size.x < LargeRoomSize || size.y < LargeRoomSize) return false;
        if (type == RoomType.Living) return true;
        return fallbackOpen && type == RoomType.Blank;
    }

    private static bool IsResidentialOpenMotif(LayoutMotif motif)
    {
        return motif == LayoutMotif.CenterHallResidential || motif == LayoutMotif.FourSquareCore ||
               motif == LayoutMotif.OpenPlanPrivateWing || motif == LayoutMotif.CourtyardCircuit ||
               motif == LayoutMotif.Pinwheel || motif == LayoutMotif.HybridLabyrinth;
    }

    private static bool TryPlaceCenterRoomProp(Transform dressing, CenterRoomFurnitureContract contract,
        string instanceName, Vector3 roomCenter, Vector2 roomSize, int roomIndex,
        IReadOnlyList<ConnectionPlan> connections, int seed, out string failure)
    {
        failure = "prefab-missing";
        if (dressing == null || contract == null) return false;
        string prefabPath = KillHouseNativePrefabBuilder.PrefabPath(contract.MeshName);
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) return false;

        GameObject instance = Instantiate(prefab, dressing, instanceName);
        int candidateIndex = 0;
        foreach (CenterRoomPlacementCandidate candidate in CenterRoomPlacementCandidates(contract, roomCenter,
                     roomSize, seed))
        {
            candidateIndex++;
            AlignGrounded(instance, candidate.Position, candidate.Rotation, Vector3.one * contract.Scale);
            Physics.SyncTransforms();
            failure = CenterRoomPropPlacementFailure(instance.transform, contract, dressing, roomCenter, roomSize,
                roomIndex, connections);
            if (!string.IsNullOrEmpty(failure)) continue;

            Child(instance, "CENTER_ROOM_PROP_ROLE_" + contract.Role);
            Child(instance, "CENTER_ROOM_PROP_ROOM_" + roomIndex.ToString("00"));
            Child(instance, contract.ProvenanceMarker);
            Child(instance, CenterRoomPropFacingMarker(instance.transform, contract));
            Child(instance, "CENTER_ROOM_PROP_DETERMINISTIC_CANDIDATE_" + candidateIndex.ToString("00"));
            Child(instance, "CENTER_ROOM_PROP_CLEARANCE_VALID");
            Child(instance, "CENTER_ROOM_PROP_CIRCULATION_VALID");
            return true;
        }

        UnityEngine.Object.DestroyImmediate(instance);
        Physics.SyncTransforms();
        return false;
    }

    private static IEnumerable<CenterRoomPlacementCandidate> CenterRoomPlacementCandidates(
        CenterRoomFurnitureContract contract, Vector3 center, Vector2 size, int seed)
    {
        if (contract.Role == "TABLE")
        {
            bool longAxisX = size.x > size.y || (Mathf.Approximately(size.x, size.y) && (seed & 1) == 0);
            float preferredYaw = longAxisX ? 90f : 0f;
            Vector3[] offsets =
            {
                Vector3.zero, new Vector3(.75f, 0f, 0f), new Vector3(-.75f, 0f, 0f),
                new Vector3(0f, 0f, .75f), new Vector3(0f, 0f, -.75f),
                new Vector3(1.25f, 0f, .65f), new Vector3(-1.25f, 0f, -.65f),
                new Vector3(1.25f, 0f, -.65f), new Vector3(-1.25f, 0f, .65f)
            };
            int start = Mathf.Abs(seed) % offsets.Length;
            for (int pass = 0; pass < 2; pass++)
                for (int index = 0; index < offsets.Length; index++)
                    yield return new CenterRoomPlacementCandidate(center + offsets[(index + start) % offsets.Length],
                        Quaternion.Euler(0f, preferredYaw + pass * 90f, 0f));
            yield break;
        }

        Vector3[] facings = { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };
        int facingStart = Mathf.Abs(seed) % facings.Length;
        for (int index = 0; index < facings.Length; index++)
        {
            Vector3 facing = facings[(index + facingStart) % facings.Length];
            Vector3 side = new Vector3(-facing.z, 0f, facing.x);
            Quaternion rotation = RotationForLocalHorizontalAxis(contract.LocalFacingAxis, facing);
            yield return new CenterRoomPlacementCandidate(center - facing * 1.15f, rotation);
            yield return new CenterRoomPlacementCandidate(center - facing * 1.15f + side * .65f, rotation);
            yield return new CenterRoomPlacementCandidate(center - facing * 1.15f - side * .65f, rotation);
            yield return new CenterRoomPlacementCandidate(center - facing * .55f, rotation);
        }
    }

    private static string CenterRoomPropFacingMarker(Transform prop, CenterRoomFurnitureContract contract)
    {
        Vector3 worldFacing = prop.TransformDirection(contract.LocalFacingAxis);
        worldFacing.y = 0f;
        worldFacing.Normalize();
        if (contract.BidirectionalFacing)
            return "CENTER_ROOM_PROP_FACING_LONG_AXIS_" +
                   (Mathf.Abs(Vector3.Dot(worldFacing, Vector3.right)) >= .999f ? "X" : "Z");
        string direction = Mathf.Abs(worldFacing.x) >= Mathf.Abs(worldFacing.z)
            ? (worldFacing.x >= 0f ? "E" : "W")
            : (worldFacing.z >= 0f ? "N" : "S");
        return "CENTER_ROOM_PROP_FACING_FRONT_" + direction;
    }

    private static void RecordCenterRoomPropSkip(Transform dressing, string role, int roomIndex, string failure,
        Vector3 roomCenter)
    {
        GameObject marker = Child(dressing.gameObject,
            "CENTER_ROOM_PROP_SKIP_" + role + "_ROOM_" + roomIndex.ToString("00"));
        marker.transform.position = roomCenter;
        Child(marker, "CENTER_ROOM_PROP_SKIP_REASON_" +
                      SanitizeMarkerName(string.IsNullOrEmpty(failure) ? "unknown" : failure));
    }

    private static string CenterRoomPropPlacementFailure(Transform prop, CenterRoomFurnitureContract contract,
        Transform dressing, Vector3 roomCenter, Vector2 roomSize, int roomIndex,
        IReadOnlyList<ConnectionPlan> connections)
    {
        if (prop == null || contract == null) return "provenance-null";
        if (!KillHouseNativePrefabBuilder.HasExactFurniturePrefabContract(prop.gameObject, out string prefabFailure))
            return "provenance-" + prefabFailure;
        if (!TryPhysicalBounds(prop.gameObject, out Bounds physicalBounds)) return "physical-collider-missing";
        Bounds renderBounds = RendererBounds(prop.gameObject);
        if (!BoundsInsideRoomWithInset(renderBounds, roomCenter, roomSize, CenterRoomPropWallClearance) ||
            !BoundsInsideRoomWithInset(physicalBounds, roomCenter, roomSize, CenterRoomPropWallClearance))
            return "room-perimeter-clearance";
        string overlap = CenterRoomSiblingOverlapFailure(prop, dressing);
        if (!string.IsNullOrEmpty(overlap)) return "sibling-overlap-" + overlap;
        if (PropBlocksAnyPortalApproach(prop, roomIndex, connections, roomCenter, roomSize))
            return "portal-approach";
        string circulation = CenterRoomCirculationFailure(dressing, roomIndex, connections, roomCenter, roomSize);
        if (!string.IsNullOrEmpty(circulation)) return "circulation-" + circulation;
        return string.Empty;
    }

    private static bool BoundsInsideRoomWithInset(Bounds bounds, Vector3 roomCenter, Vector2 roomSize, float inset)
    {
        float halfX = roomSize.x * .5f - inset;
        float halfZ = roomSize.y * .5f - inset;
        return halfX > 0f && halfZ > 0f &&
               bounds.min.x >= roomCenter.x - halfX && bounds.max.x <= roomCenter.x + halfX &&
               bounds.min.z >= roomCenter.z - halfZ && bounds.max.z <= roomCenter.z + halfZ;
    }

    private static string CenterRoomSiblingOverlapFailure(Transform candidate, Transform dressing)
    {
        if (!TryPhysicalBounds(candidate.gameObject, out Bounds candidateBounds)) return "candidate-collider";
        for (int index = 0; index < dressing.childCount; index++)
        {
            Transform sibling = dressing.GetChild(index);
            if (sibling == candidate || CenterRoomSurfaceDecoration(sibling) ||
                !TryPhysicalBounds(sibling.gameObject, out Bounds siblingBounds)) continue;
            if (BoundsOverlap(candidateBounds, siblingBounds, CenterRoomSiblingOverlapTolerance))
                return sibling.name;
        }
        return string.Empty;
    }

    private static bool CenterRoomSurfaceDecoration(Transform root)
    {
        return root != null && root.name.IndexOf("Carpet", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string CenterRoomCirculationFailure(Transform dressing, int roomIndex,
        IEnumerable<ConnectionPlan> connections, Vector3 roomCenter, Vector2 roomSize)
    {
        List<Vector3> portalEntries = connections
            .Where(connection => connection.RoomA == roomIndex || connection.RoomB == roomIndex)
            .SelectMany(connection => PortalProbeOffsets(connection).Select(offset =>
            {
                Vector2Int outward = ConnectionDirectionForRoom(connection, roomIndex);
                Vector3 outward3 = new Vector3(outward.x, 0f, outward.y);
                Vector3 tangent = outward.x != 0 ? Vector3.forward : Vector3.right;
                float extent = outward.x != 0 ? roomSize.x * .5f : roomSize.y * .5f;
                return roomCenter + outward3 * (extent - .8f) + tangent * offset;
            })).ToList();
        if (portalEntries.Count == 0) return "portal-entry-missing";

        float halfX = roomSize.x * .5f - CenterRoomCirculationRadius - .08f;
        float halfZ = roomSize.y * .5f - CenterRoomCirculationRadius - .08f;
        int countX = Mathf.FloorToInt(halfX * 2f / CenterRoomCirculationStep) + 1;
        int countZ = Mathf.FloorToInt(halfZ * 2f / CenterRoomCirculationStep) + 1;
        if (countX < 2 || countZ < 2) return "grid-too-small";
        var clear = new bool[countX, countZ];
        int clearCount = 0;
        for (int x = 0; x < countX; x++)
        {
            for (int z = 0; z < countZ; z++)
            {
                Vector3 point = new Vector3(roomCenter.x - halfX + x * CenterRoomCirculationStep, .05f,
                    roomCenter.z - halfZ + z * CenterRoomCirculationStep);
                clear[x, z] = !CenterRoomDressingBlocksCapsule(dressing, point);
                if (clear[x, z]) clearCount++;
            }
        }
        if (clearCount == 0) return "no-clear-grid-cell";

        var starts = new List<Vector2Int>();
        foreach (Vector3 portal in portalEntries)
        {
            Vector2Int nearest = default;
            float best = float.MaxValue;
            for (int x = 0; x < countX; x++)
            {
                for (int z = 0; z < countZ; z++)
                {
                    if (!clear[x, z]) continue;
                    Vector2 point = new Vector2(roomCenter.x - halfX + x * CenterRoomCirculationStep,
                        roomCenter.z - halfZ + z * CenterRoomCirculationStep);
                    float distance = Vector2.SqrMagnitude(point - new Vector2(portal.x, portal.z));
                    if (distance >= best) continue;
                    best = distance;
                    nearest = new Vector2Int(x, z);
                }
            }
            if (best > 1.15f * 1.15f) return "portal-grid-gap";
            if (!starts.Contains(nearest)) starts.Add(nearest);
        }

        var visited = new bool[countX, countZ];
        var queue = new Queue<Vector2Int>();
        queue.Enqueue(starts[0]);
        visited[starts[0].x, starts[0].y] = true;
        int visitedCount = 0;
        Vector2Int[] steps = { Vector2Int.right, Vector2Int.up, Vector2Int.left, Vector2Int.down };
        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();
            visitedCount++;
            foreach (Vector2Int step in steps)
            {
                Vector2Int next = current + step;
                if (next.x < 0 || next.y < 0 || next.x >= countX || next.y >= countZ ||
                    visited[next.x, next.y] || !clear[next.x, next.y]) continue;
                visited[next.x, next.y] = true;
                queue.Enqueue(next);
            }
        }
        if (starts.Any(start => !visited[start.x, start.y])) return "portal-components-disconnected";
        if (visitedCount < Mathf.Max(4, Mathf.CeilToInt(clearCount / 3f))) return "circulation-region-too-small";
        return string.Empty;
    }

    private static bool CenterRoomDressingBlocksCapsule(Transform dressing, Vector3 point)
    {
        Vector3 bottom = point + Vector3.up * .37f;
        Vector3 top = point + Vector3.up * 1.65f;
        Collider[] hits = Physics.OverlapCapsule(bottom, top, CenterRoomCirculationRadius, ~0,
            QueryTriggerInteraction.Ignore);
        foreach (Collider hit in hits)
        {
            if (hit == null || !IsDescendantOf(hit.transform, dressing)) continue;
            Transform owner = DirectChildBelow(hit.transform, dressing);
            if (owner != null && CenterRoomSurfaceDecoration(owner)) continue;
            return true;
        }
        return false;
    }

    private static Transform DirectChildBelow(Transform value, Transform ancestor)
    {
        if (value == null || ancestor == null || value == ancestor) return null;
        Transform cursor = value;
        while (cursor.parent != null && cursor.parent != ancestor) cursor = cursor.parent;
        return cursor.parent == ancestor ? cursor : null;
    }

    private static void BuildSpatialMotif(Transform room, RoomType type, Vector3 center, Vector2 size,
        int variantIndex, int roomIndex, LayoutMotif motif, IReadOnlyList<ConnectionPlan> connections)
    {
        bool expansive = size.x >= LargeRoomSize && size.y >= LargeRoomSize;
        if (type == RoomType.Blank && expansive)
            BuildBlankRoomPattern(room, center, variantIndex, roomIndex, motif);

        bool residentialOpenRoom = type == RoomType.Kitchen || type == RoomType.Living || type == RoomType.Dining;
        bool residentialDividerMotif = motif == LayoutMotif.CenterHallResidential ||
                                       motif == LayoutMotif.OpenPlanPrivateWing ||
                                       motif == LayoutMotif.CourtyardCircuit ||
                                       motif == LayoutMotif.Pinwheel ||
                                       motif == LayoutMotif.HybridLabyrinth;
        if (expansive && residentialOpenRoom && residentialDividerMotif)
            BuildNativeLowDivider(room, center, variantIndex, roomIndex, "RESIDENTIAL_OPEN_ZONE");

        CullPortalBlockingSpatialFeatures(room, roomIndex, connections, center, size);
    }

    private static void CullPortalBlockingSpatialFeatures(Transform room, int roomIndex,
        IReadOnlyList<ConnectionPlan> connections, Vector3 center, Vector2 size)
    {
        Physics.SyncTransforms();
        Transform[] features = room.GetComponentsInChildren<Transform>(true).Where(item => item != room &&
            (item.name.StartsWith("NATIVE_InteriorSplitWall_", StringComparison.Ordinal) ||
             item.name.StartsWith("NATIVE_LowDivider_", StringComparison.Ordinal) ||
             item.name.StartsWith("NATIVE_OfficePartition_", StringComparison.Ordinal))).ToArray();
        foreach (Transform feature in features)
        {
            if (feature == null || !PropBlocksAnyPortalApproach(feature, roomIndex, connections, center, size)) continue;
            UnityEngine.Object.DestroyImmediate(feature.gameObject);
            Physics.SyncTransforms();
        }
    }

    private static void BuildBlankRoomPattern(Transform room, Vector3 center, int variantIndex, int roomIndex,
        LayoutMotif motif)
    {
        string prefix = "R" + roomIndex.ToString("00") + "_";
        switch (motif)
        {
            case LayoutMotif.CenterHallResidential:
                PlaceWallLine(room, center + new Vector3(-1.35f, 0f, 0f), 90f, 2, prefix + "CENTER_HALL");
                break;
            case LayoutMotif.FourSquareCore:
                PlaceWallLine(room, center + new Vector3(-1f, 0f, .65f), 0f, 1, prefix + "FOUR_SQUARE_A");
                PlaceWallLine(room, center + new Vector3(0f, 0f, 1.65f), 90f, 1, prefix + "FOUR_SQUARE_B");
                break;
            case LayoutMotif.OpenPlanPrivateWing:
                PlaceWallLine(room, center + new Vector3(0f, 0f, -1.35f), 0f, 2, prefix + "PRIVATE_WING");
                BuildNativeLowDivider(room, center + new Vector3(0f, 0f, 1.6f), variantIndex, roomIndex,
                    "OPEN_PLAN_TRAINING_ZONE", false);
                break;
            case LayoutMotif.CourtyardCircuit:
                PlaceWallLine(room, center + new Vector3(-1.55f, 0f, -1.55f), 0f, 1, prefix + "COURTYARD_SOUTH");
                PlaceWallLine(room, center + new Vector3(1.55f, 0f, 1.55f), 0f, 1, prefix + "COURTYARD_NORTH");
                PlaceWallLine(room, center + new Vector3(-1.55f, 0f, 1.55f), 90f, 1, prefix + "COURTYARD_WEST");
                PlaceWallLine(room, center + new Vector3(1.55f, 0f, -1.55f), 90f, 1, prefix + "COURTYARD_EAST");
                break;
            case LayoutMotif.SplitSpineTraining:
                PlaceWallLine(room, center + new Vector3(-1.15f, 0f, -1.5f), 0f, 2, prefix + "SPLIT_SPINE_SOUTH");
                PlaceWallLine(room, center + new Vector3(1.15f, 0f, 1.5f), 0f, 2, prefix + "SPLIT_SPINE_NORTH");
                break;
            case LayoutMotif.OfficeCore:
                BuildNativeOfficeCore(room, center, roomIndex);
                break;
            case LayoutMotif.LRoomTraining:
                PlaceWallLine(room, center + new Vector3(-1f, 0f, .8f), 0f, 2, prefix + "L_ROOM_HORIZONTAL");
                PlaceWallLine(room, center + new Vector3(-2f, 0f, -.2f), 90f, 2, prefix + "L_ROOM_VERTICAL");
                break;
            case LayoutMotif.DualCorridor:
                PlaceWallLine(room, center + new Vector3(-1.75f, 0f, 0f), 90f, 2, prefix + "DUAL_CORRIDOR_WEST");
                PlaceWallLine(room, center + new Vector3(1.75f, 0f, 0f), 90f, 2, prefix + "DUAL_CORRIDOR_EAST");
                break;
            case LayoutMotif.Pinwheel:
                PlaceWallLine(room, center + new Vector3(-1.5f, 0f, -.75f), 0f, 1, prefix + "PINWHEEL_SOUTH");
                PlaceWallLine(room, center + new Vector3(.75f, 0f, -1.5f), 90f, 1, prefix + "PINWHEEL_EAST");
                PlaceWallLine(room, center + new Vector3(1.5f, 0f, .75f), 0f, 1, prefix + "PINWHEEL_NORTH");
                PlaceWallLine(room, center + new Vector3(-.75f, 0f, 1.5f), 90f, 1, prefix + "PINWHEEL_WEST");
                break;
            case LayoutMotif.HybridLabyrinth:
                PlaceWallLine(room, center + new Vector3(-1.25f, 0f, -1.2f), 0f, 2, prefix + "HYBRID_SOLID");
                BuildNativeLowDivider(room, center + new Vector3(0f, 0f, 1.65f), variantIndex, roomIndex,
                    "HYBRID_LOW", false);
                break;
        }
    }

    private static void PlaceWallLine(Transform parent, Vector3 center, float yaw, int segmentCount, string featureName)
    {
        Vector3 tangent = Mathf.Abs(Mathf.DeltaAngle(yaw, 0f)) < 1f ? Vector3.right : Vector3.forward;
        float firstOffset = -(segmentCount - 1) * WallModuleWidth * .5f;
        for (int index = 0; index < segmentCount; index++)
        {
            Vector3 position = center + tangent * (firstOffset + index * WallModuleWidth);
            PlaceWall(parent, "Wall2Meter", "NATIVE_InteriorSplitWall_" + featureName + "_" + index.ToString("00"),
                position, yaw);
        }
    }

    private static void BuildNativeLowDivider(Transform parent, Vector3 center, int variantIndex, int roomIndex,
        string featureName, bool applyOffset = true)
    {
        bool alongX = ((variantIndex + roomIndex) & 1) == 0;
        Vector3 dividerCenter = center;
        if (applyOffset) dividerCenter += alongX ? new Vector3(0f, 0f, 2.05f) : new Vector3(2.05f, 0f, 0f);
        Vector3 tangent = alongX ? Vector3.right : Vector3.forward;
        float yaw = alongX ? 0f : 90f;
        for (int index = 0; index < 3; index++)
        {
            PlaceProp(parent, "Kitcabinet_low_1x_A",
                "NATIVE_LowDivider_" + featureName + "_R" + roomIndex.ToString("00") + "_" + index.ToString("00"),
                dividerCenter + tangent * ((index - 1) * .92f), yaw, .9f);
        }
    }

    private static void BuildNativeOfficeCore(Transform parent, Vector3 center, int roomIndex)
    {
        string prefix = "NATIVE_OfficePartition_R" + roomIndex.ToString("00") + "_";
        // Office-core bookshelves previously formed central barriers and could block an entire
        // route.  The core now uses compact desks near opposite edges; bookcases exist only in
        // placements that carry a wall-backed orientation marker.
        PlaceProp(parent, "Workdesk_solo", prefix + "WEST_DESK", center + new Vector3(-2.8f, 0f, 0f), 90f, .62f);
        PlaceProp(parent, "Workdesk_solo", prefix + "EAST_DESK", center + new Vector3(2.8f, 0f, 0f), -90f, .62f);
    }

    private static void BuildRoomLights(Transform parent, RoomType type, Vector3 center, Vector2 size,
        int variantIndex, int index, Collider[] warehouseRoofColliders)
    {
        RoomLightState state = SelectRoomLightState(variantIndex, index);
        string stateName = state.ToString().ToUpperInvariant();
        Transform holder = Child(parent.gameObject, "ROOM_LIGHT_" + index.ToString("00") + "_" +
            type.ToString().ToUpperInvariant() + "_STATE_" + stateName).transform;
        Child(holder.gameObject, "ROOM_LIGHT_STATE_" + stateName);
        const string fixture = "Lamp_fluorescent_B";
        int columns = Mathf.Clamp(Mathf.CeilToInt(size.x / WarehouseFixtureSpacing), 1, 2);
        int rows = Mathf.Clamp(Mathf.CeilToInt(size.y / WarehouseFixtureSpacing), 1, 2);
        float cellWidth = size.x / columns;
        float cellDepth = size.y / rows;
        float colorTemperature = 4300f;
        float litLumens = columns * rows == 1 ? 1400f : 1100f;
        float lumens = state == RoomLightState.Lit ? litLumens :
            state == RoomLightState.Dim ? (columns * rows == 1 ? 160f : 120f) : 0f;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int ordinal = row * columns + column;
                Vector3 fixtureCenter = center + new Vector3(
                    -size.x * .5f + cellWidth * (column + .5f),
                    WarehouseRoofHeight,
                    -size.y * .5f + cellDepth * (row + .5f));
                GameObject visual = Instantiate(KillHouseNativePrefabBuilder.Load(fixture), holder,
                    "NATIVE_" + fixture + "_" + ordinal.ToString("00"));
                AlignCeiling(visual, fixtureCenter,
                    Quaternion.Euler(-90f, ((row + column + index) & 1) == 0 ? 0f : 90f, 0f),
                    WarehouseFixtureVisualScale);
                Bounds provisionalBounds = RendererBounds(visual);
                float roofUnderside = ResolveLowestWarehouseRoofUnderside(
                    warehouseRoofColliders,
                    provisionalBounds);
                fixtureCenter.y = roofUnderside - WarehouseFixtureRoofGap;
                AlignCeiling(visual, fixtureCenter,
                    Quaternion.Euler(-90f, ((row + column + index) & 1) == 0 ? 0f : 90f, 0f),
                    WarehouseFixtureVisualScale);
                GameObject lightObject = Child(holder.gameObject, "ROOM_LOCAL_FIXTURE_LIGHT_" + ordinal.ToString("00"));
                lightObject.transform.position = new Vector3(
                    fixtureCenter.x,
                    fixtureCenter.y - WarehouseFixtureLightDrop,
                    fixtureCenter.z);
                lightObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Spot;
                light.color = Color.white;
                light.useColorTemperature = true;
                light.colorTemperature = colorTemperature;
                light.intensity = lumens;
                light.range = 11.5f;
                light.spotAngle = 58f;
                light.innerSpotAngle = 38f;
                // Keep the room fully illuminated while limiting HDRP shadow-map
                // work to one fixture per lit room. Dim fixtures remain visible
                // and continue to light walls/floors, but do not spend a shadow
                // atlas entry; dark fixtures stay disabled.
                light.shadows = state == RoomLightState.Lit && ordinal == 0
                    ? LightShadows.Soft
                    : LightShadows.None;
                light.shadowBias = .035f;
                light.shadowNormalBias = .25f;
                light.shadowNearPlane = .1f;
                light.enabled = state != RoomLightState.Dark;
            }
        }
    }

    private static RoomLightState SelectRoomLightState(int variantIndex, int roomIndex)
    {
        if (roomIndex == 0) return RoomLightState.Lit;
        int value = (roomIndex * 5 + variantIndex * 3) % 11;
        if (value == 0 || value == 3 || value == 4 || value == 7 || value == 9) return RoomLightState.Dark;
        if (value == 2 || value == 5 || value == 8) return RoomLightState.Dim;
        return RoomLightState.Lit;
    }

    private static void BuildFallbackDirectionalLight(Transform parent)
    {
        GameObject lightObject = Child(parent.gameObject, "PACKAGE_FALLBACK_DIRECTIONAL_LIGHT");
        lightObject.transform.rotation = Quaternion.Euler(52f, -28f, 0f);
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Color.white;
        light.intensity = 0f;
        light.shadows = LightShadows.None;
        light.enabled = false;
    }

    private static void BuildVanillaIndoorRenderVolume(Transform parent)
    {
        GameObject volumeObject = Child(parent.gameObject, "VANILLA_OFFICE_GLOBAL_VOLUME");
        volumeObject.layer = 0;
        Volume volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = KillHouseVanillaIndoorRenderBuilder.GlobalVolumePriority;
        volume.weight = 1f;
        volume.sharedProfile = KillHouseVanillaIndoorRenderBuilder.LoadProfile();
    }

    private static bool FixtureLightValid(Light light, Collider[] warehouseRoofColliders)
    {
        if (light == null || light.transform.parent == null || light.type != LightType.Spot ||
            light.range < 11f ||
            light.spotAngle < 56f || light.spotAngle > 60f || !light.useColorTemperature ||
            light.colorTemperature < 4200f || light.colorTemperature > 4400f)
            return false;
        string suffix = light.name.Substring("ROOM_LOCAL_FIXTURE_LIGHT_".Length);
        bool litHolder = light.transform.parent.name.EndsWith(
            "_STATE_LIT", StringComparison.Ordinal);
        LightShadows expectedShadows = litHolder &&
            string.Equals(suffix, "00", StringComparison.Ordinal)
                ? LightShadows.Soft
                : LightShadows.None;
        if (light.shadows != expectedShadows)
            return false;
        Transform fixture = light.transform.parent.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(item => string.Equals(item.name,
                "NATIVE_Lamp_fluorescent_B_" + suffix, StringComparison.Ordinal));
        if (fixture == null || !TryFixtureRoofGap(fixture, warehouseRoofColliders, out float roofGap,
                out float fixtureTop) ||
            Mathf.Abs(roofGap - WarehouseFixtureRoofGap) > .015f ||
            Mathf.Abs((fixtureTop - light.transform.position.y) - WarehouseFixtureLightDrop) > .015f)
            return false;
        string holder = light.transform.parent.name;
        if (holder.EndsWith("_STATE_LIT", StringComparison.Ordinal))
            return light.enabled && light.intensity >= 1050f && light.intensity <= 1450f;
        if (holder.EndsWith("_STATE_DIM", StringComparison.Ordinal))
            return light.enabled && light.intensity >= 110f && light.intensity <= 170f;
        if (holder.EndsWith("_STATE_DARK", StringComparison.Ordinal))
            return !light.enabled && light.intensity <= .001f;
        return false;
    }

    private static bool FixtureVisualValid(Transform fixture, Collider[] warehouseRoofColliders)
    {
        if (fixture == null) return false;
        // Lamp_fluorescent_B is authored in XY; its local -Z is the visible underside.
        // Match the installed vanilla fixture orientation so that underside faces the player.
        if (Vector3.Dot(fixture.TransformDirection(Vector3.back).normalized, Vector3.down) < .98f ||
            Vector3.Distance(fixture.localScale, WarehouseFixtureVisualScale) > .01f) return false;
        Renderer[] renderers = fixture.GetComponentsInChildren<Renderer>(true);
        return TryFixtureRoofGap(fixture, warehouseRoofColliders, out float roofGap, out _) &&
            Mathf.Abs(roofGap - WarehouseFixtureRoofGap) <= .015f &&
            renderers.Length > 0 && renderers.All(renderer => renderer.sharedMaterials.Length > 0 &&
                renderer.sharedMaterials.All(KillHouseNativeMaterialBuilder.HasKillHouseFluorescentEmissionContract));
    }

    private static Collider[] FindWarehouseRoofColliders(Transform warehouseRoot)
    {
        Transform roof = warehouseRoot == null ? null : warehouseRoot.Find(
            "NATIVE_WarehousePvpCompleteShell/NATIVE_WarehouseRoof");
        return roof == null ? Array.Empty<Collider>() : roof.GetComponentsInChildren<Collider>(true)
            .Where(collider => collider != null && collider.enabled && !collider.isTrigger).ToArray();
    }

    private static bool TryFixtureRoofGap(Transform fixture, Collider[] roofColliders,
        out float gap, out float fixtureTop)
    {
        gap = float.NaN;
        fixtureTop = float.NaN;
        if (fixture == null || roofColliders == null || roofColliders.Length == 0)
            return false;
        Bounds bounds = RendererBounds(fixture.gameObject);
        fixtureTop = bounds.max.y;
        try
        {
            gap = ResolveLowestWarehouseRoofUnderside(roofColliders, bounds) - fixtureTop;
            return float.IsFinite(gap);
        }
        catch
        {
            return false;
        }
    }

    private static float ResolveLowestWarehouseRoofUnderside(Collider[] roofColliders, Bounds fixtureBounds)
    {
        const int subdivisions = 16;
        float lowest = float.PositiveInfinity;
        bool originalBackfaceSetting = Physics.queriesHitBackfaces;
        Physics.queriesHitBackfaces = true;
        try
        {
            for (int xIndex = 0; xIndex <= subdivisions; xIndex++)
            {
                float worldX = Mathf.Lerp(fixtureBounds.min.x, fixtureBounds.max.x,
                    xIndex / (float)subdivisions);
                for (int zIndex = 0; zIndex <= subdivisions; zIndex++)
                {
                    float worldZ = Mathf.Lerp(fixtureBounds.min.z, fixtureBounds.max.z,
                        zIndex / (float)subdivisions);
                    float underside = ResolveWarehouseRoofUnderside(roofColliders, worldX, worldZ);
                    if (underside < lowest) lowest = underside;
                }
            }
        }
        finally
        {
            Physics.queriesHitBackfaces = originalBackfaceSetting;
        }
        if (!float.IsFinite(lowest))
            throw new InvalidDataException("A fluorescent fixture has no exact warehouse-roof underside above it.");
        return lowest;
    }

    private static float ResolveWarehouseRoofUnderside(Collider[] roofColliders, float worldX, float worldZ)
    {
        Collider[] validColliders = roofColliders.Where(collider => collider != null).ToArray();
        if (validColliders.Length == 0)
            throw new InvalidDataException("The warehouse roof has no valid collider for underside sampling.");
        float rayStartY = validColliders.Min(collider => collider.bounds.min.y) - 1f;
        float rayEndY = validColliders.Max(collider => collider.bounds.max.y) + 1f;
        Ray ray = new Ray(new Vector3(worldX, rayStartY, worldZ), Vector3.up);
        float nearestDistance = float.PositiveInfinity;
        foreach (Collider collider in validColliders)
        {
            if (collider.Raycast(ray, out RaycastHit hit, rayEndY - rayStartY) &&
                hit.distance < nearestDistance)
                nearestDistance = hit.distance;
        }
        if (!float.IsFinite(nearestDistance))
            throw new InvalidDataException("No exact warehouse-roof underside covers fixture point " +
                worldX.ToString("F3") + "," + worldZ.ToString("F3") + ".");
        return ray.origin.y + nearestDistance;
    }

    private static bool IndoorVolumeValid(Volume volume)
    {
        if (volume == null || !volume.isGlobal || volume.gameObject.layer != 0 ||
            !Mathf.Approximately(volume.priority, KillHouseVanillaIndoorRenderBuilder.GlobalVolumePriority) ||
            !Mathf.Approximately(volume.weight, 1f) || volume.sharedProfile == null ||
            volume.sharedProfile.components.Count != 14)
            return false;
        VolumeProfile profile = volume.sharedProfile;
        if (!profile.TryGet(out Exposure exposure) || !profile.TryGet(out VisualEnvironment visualEnvironment) ||
            !profile.TryGet(out PhysicallyBasedSky physicallyBasedSky) || !profile.TryGet(out Fog fog) ||
            !profile.TryGet(out ProbeVolumesOptions probeVolumes) || !profile.TryGet(out Bloom bloom) ||
            !profile.TryGet(out ScreenSpaceLensFlare lensFlare) || !profile.TryGet(out MicroShadowing microShadowing) ||
            !profile.TryGet(out ContactShadows contactShadows) || !profile.TryGet(out HDShadowSettings shadowSettings) ||
            !profile.TryGet(out Tonemapping tonemapping) ||
            !profile.TryGet(out LiftGammaGain liftGammaGain) || !profile.TryGet(out WhiteBalance whiteBalance) ||
            !profile.TryGet(out ColorAdjustments colorAdjustments))
            return false;
        Texture3D lut = tonemapping.lutTexture.value as Texture3D;
        return exposure.active && exposure.mode.overrideState && exposure.mode.value == ExposureMode.AutomaticHistogram &&
               exposure.compensation.overrideState && Mathf.Abs(exposure.compensation.value) <= .001f &&
               exposure.limitMin.overrideState && Mathf.Abs(exposure.limitMin.value - 8.5f) <= .001f &&
               exposure.limitMax.overrideState && Mathf.Abs(exposure.limitMax.value - 11f) <= .001f &&
               !visualEnvironment.active && !physicallyBasedSky.active && !fog.active && probeVolumes.active &&
               bloom.active && bloom.intensity.overrideState && Mathf.Abs(bloom.intensity.value - .03f) <= .001f &&
               bloom.threshold.overrideState && Mathf.Abs(bloom.threshold.value - .9f) <= .001f &&
               bloom.scatter.overrideState && Mathf.Abs(bloom.scatter.value - .893f) <= .001f &&
               lensFlare.active && lensFlare.intensity.overrideState &&
               Mathf.Abs(lensFlare.intensity.value - .5f) <= .001f &&
               lensFlare.streaksIntensity.overrideState &&
               Mathf.Abs(lensFlare.streaksIntensity.value - 1.55f) <= .001f &&
               lensFlare.streaksLength.overrideState &&
               Mathf.Abs(lensFlare.streaksLength.value - .022f) <= .001f &&
               lensFlare.streaksOrientation.overrideState &&
               Mathf.Abs(lensFlare.streaksOrientation.value) <= .001f &&
               lensFlare.chromaticAbberationIntensity.overrideState &&
               Mathf.Abs(lensFlare.chromaticAbberationIntensity.value - .6f) <= .001f &&
               microShadowing.active && contactShadows.active && shadowSettings.active &&
               tonemapping.active && tonemapping.mode.overrideState && tonemapping.mode.value == TonemappingMode.External &&
               tonemapping.lutTexture.overrideState && lut != null && lut.width == 32 && lut.height == 32 && lut.depth == 32 &&
               string.Equals(lut.name, "AgX - PunchyPowerfulMix", StringComparison.Ordinal) &&
               !tonemapping.lutContribution.overrideState && Mathf.Abs(tonemapping.lutContribution.value - 1f) <= .001f &&
               liftGammaGain.active && liftGammaGain.lift.overrideState && liftGammaGain.gamma.overrideState &&
               liftGammaGain.gain.overrideState && whiteBalance.active &&
               colorAdjustments.active && colorAdjustments.postExposure.overrideState &&
               Mathf.Abs(colorAdjustments.postExposure.value + .3f) <= .001f &&
               colorAdjustments.contrast.overrideState && Mathf.Abs(colorAdjustments.contrast.value - 30f) <= .001f &&
               colorAdjustments.hueShift.overrideState && Mathf.Abs(colorAdjustments.hueShift.value) <= .001f &&
               colorAdjustments.saturation.overrideState && Mathf.Abs(colorAdjustments.saturation.value + 15f) <= .001f;
    }

    private static void BuildGameplayMarkers(Transform parent, Transform propsRoot, Layout layout,
        Vector3[] roomCenters, Vector2[] roomSizes, ConnectionPlan[] connections, int variantIndex)
    {
        int[] distancesFromSafe = BuildGraphDistances(layout.Cells.Length, connections);
        var candidatesByRoom = new Dictionary<int, List<TacticalEnemyCandidate>>();
        for (int roomIndex = 1; roomIndex < roomCenters.Length; roomIndex++)
        {
            List<Vector3> threatPoints = BuildThreatPoints(roomIndex, roomCenters, roomSizes, connections,
                distancesFromSafe);
            var roomCandidates = new List<TacticalEnemyCandidate>();
            for (int ordinal = 0; ordinal < 2; ordinal++)
            {
                int seed = variantIndex * 101 + roomIndex * 37 + ordinal * 17;
                Vector3 threatPoint = threatPoints[ordinal % threatPoints.Count];
                Transform cover = FindPreferredTacticalCover(propsRoot, roomIndex, layout.Rooms[roomIndex], ordinal);
                Vector3 markerPosition;
                Vector3 coverPoint;
                string role;
                string coverLabel;
                if (cover != null && TryBuildPropCoverPosition(cover, roomCenters[roomIndex], roomSizes[roomIndex],
                        threatPoint, seed, out markerPosition, out coverPoint))
                {
                    role = layout.Rooms[roomIndex] == RoomType.Hallway ? "HALL_INTERDICTION" :
                        (ordinal == 0 ? "PROP_AMBUSH" : "CROSS_COVER");
                    coverLabel = cover.name;
                }
                else
                {
                    role = layout.Rooms[roomIndex] == RoomType.Hallway ? "HALL_INTERDICTION" :
                        (ordinal == 0 ? "CORNER_GUARD" : "OFFSET_GUARD");
                    BuildArchitecturalCoverPosition(roomCenters[roomIndex], roomSizes[roomIndex], threatPoint, seed,
                        out markerPosition, out coverPoint);
                    coverLabel = "ROOM_BOUNDARY_WALL";
                }

                AddUniqueTacticalCandidate(roomCandidates, new TacticalEnemyCandidate(roomIndex, markerPosition,
                    coverPoint, threatPoint, role, coverLabel));
            }

            foreach (TacticalEnemyCandidate candidate in BuildSupplementalTacticalCandidates(roomIndex,
                         roomCenters[roomIndex], roomSizes[roomIndex], threatPoints, variantIndex))
                AddUniqueTacticalCandidate(roomCandidates, candidate);
            candidatesByRoom.Add(roomIndex, roomCandidates);
        }

        List<TacticalEnemyCandidate> selected = SelectCertifiedTacticalCandidates(candidatesByRoom,
            distancesFromSafe);
        for (int index = 0; index < selected.Count; index++)
            BuildTacticalEnemyMarker(parent, selected[index], index + 1);

        BuildPvpSpawnMarkers(parent, layout, roomCenters, roomSizes, connections);
    }

    private static IEnumerable<TacticalEnemyCandidate> BuildSupplementalTacticalCandidates(int roomIndex,
        Vector3 roomCenter, Vector2 roomSize, IReadOnlyList<Vector3> threatPoints, int variantIndex)
    {
        float halfX = Mathf.Max(.55f, roomSize.x * .5f - .82f);
        float halfZ = Mathf.Max(.55f, roomSize.y * .5f - .82f);
        float phaseX = ((variantIndex + roomIndex) & 1) == 0 ? 0f : .31f;
        float phaseZ = ((variantIndex * 3 + roomIndex) & 1) == 0 ? 0f : -.31f;
        var positions = new List<Vector3>();
        for (float x = -halfX + phaseX; x <= halfX + .01f; x += PveEnemySpawnPairClearance)
        {
            for (float z = -halfZ + phaseZ; z <= halfZ + .01f; z += PveEnemySpawnPairClearance)
            {
                Vector3 candidate = ClampInsideRoom(roomCenter + new Vector3(x, .05f, z), roomCenter, roomSize);
                if (threatPoints.Any(threat => HorizontalDistance(candidate, threat) <
                                               PveEnemySpawnPortalClearance))
                    continue;
                Vector3 bottom = candidate + Vector3.up * .42f;
                Vector3 top = candidate + Vector3.up * 1.58f;
                if (Physics.CheckCapsule(bottom, top, .3f, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (!TryFindBackedArchitecturalCoverPoint(candidate, roomCenter, roomSize, out Vector3 coverPoint))
                    continue;
                Vector3 threatPoint = threatPoints.OrderBy(threat => HorizontalDistance(candidate, threat))
                    .ThenBy(threat => threat.x).ThenBy(threat => threat.z).First();
                float coverDistance = HorizontalDistance(candidate, coverPoint);
                if (coverDistance < .35f || coverDistance > 4.25f ||
                    HorizontalDistance(candidate, threatPoint) < 1.25f)
                    continue;
                float edgeDistance = Mathf.Min(halfX - Mathf.Abs(candidate.x - roomCenter.x),
                    halfZ - Mathf.Abs(candidate.z - roomCenter.z));
                string role = edgeDistance <= 1.10f ? "PERIMETER_GUARD" : "DEPTH_GUARD";
                yield return new TacticalEnemyCandidate(roomIndex, candidate, coverPoint, threatPoint, role,
                    "ROOM_BOUNDARY_WALL");
            }
        }
    }

    private static List<TacticalEnemyCandidate> SelectCertifiedTacticalCandidates(
        IReadOnlyDictionary<int, List<TacticalEnemyCandidate>> candidatesByRoom, int[] distancesFromSafe)
    {
        var selected = new List<TacticalEnemyCandidate>();
        var selectedPerRoom = candidatesByRoom.Keys.ToDictionary(room => room, _ => 0);
        int[] roomOrder = candidatesByRoom.Keys.OrderByDescending(room => distancesFromSafe[room])
            .ThenBy(room => room).ToArray();

        for (int pass = 0; pass < PveMinimumMarkersPerCombatRoom; pass++)
        {
            foreach (int room in roomOrder)
            {
                TacticalEnemyCandidate candidate = candidatesByRoom[room].FirstOrDefault(item =>
                    !selected.Contains(item) && TacticalCandidateSeparated(item, selected));
                if (candidate == null)
                    throw new InvalidDataException("Room " + room.ToString("00") +
                        " cannot provide the minimum separated PVE tactical marker inventory.");
                selected.Add(candidate);
                selectedPerRoom[room]++;
            }
        }

        while (selected.Count < PveAuthoredEnemyMarkerTarget)
        {
            bool added = false;
            foreach (int room in roomOrder.OrderBy(room => selectedPerRoom[room]).ThenByDescending(room =>
                         distancesFromSafe[room]).ThenBy(room => room))
            {
                TacticalEnemyCandidate candidate = candidatesByRoom[room].FirstOrDefault(item =>
                    !selected.Contains(item) && TacticalCandidateSeparated(item, selected));
                if (candidate == null) continue;
                selected.Add(candidate);
                selectedPerRoom[room]++;
                added = true;
                if (selected.Count == PveAuthoredEnemyMarkerTarget) break;
            }
            if (!added) break;
        }
        if (selected.Count < PveAuthoredEnemyMarkerTarget)
            throw new InvalidDataException("Only " + selected.Count + " globally separated tactical enemy markers " +
                "were available; " + PveAuthoredEnemyMarkerTarget + " are required to certify a " +
                CertifiedPveMaximumEnemies + "-enemy PVE maximum.");
        return selected;
    }

    private static void AddUniqueTacticalCandidate(ICollection<TacticalEnemyCandidate> candidates,
        TacticalEnemyCandidate candidate)
    {
        if (candidate == null || candidates.Any(item => HorizontalDistance(item.Position, candidate.Position) < .20f))
            return;
        candidates.Add(candidate);
    }

    private static bool TacticalCandidateSeparated(TacticalEnemyCandidate candidate,
        IEnumerable<TacticalEnemyCandidate> selected)
    {
        return selected.All(other => HorizontalDistance(candidate.Position, other.Position) >=
                                     PveEnemySpawnPairClearance - .001f);
    }

    private static void BuildTacticalEnemyMarker(Transform parent, TacticalEnemyCandidate candidate, int markerIndex)
    {
        GameObject envelope = Child(parent.gameObject,
            "TACTICAL_POSITION_" + markerIndex.ToString("00") + "_" + candidate.Role);
        GameObject marker = Child(envelope, "PVE_EnemySpawn_" + markerIndex.ToString("00"));
        marker.transform.position = candidate.Position;
        Vector3 facing = candidate.ThreatPoint - candidate.Position;
        facing.y = 0f;
        marker.transform.rotation = facing.sqrMagnitude < .01f
            ? Quaternion.identity
            : Quaternion.LookRotation(facing.normalized, Vector3.up);
        GameObject roleMarker = Child(envelope, "TACTICAL_ROLE_" + candidate.Role);
        roleMarker.transform.position = candidate.Position;
        GameObject coverMarker = Child(envelope,
            "TACTICAL_COVER_POINT_" + SanitizeMarkerName(candidate.CoverLabel));
        coverMarker.transform.position = candidate.CoverPoint;
        GameObject threatMarker = Child(envelope,
            "TACTICAL_THREAT_POINT_ROOM_" + candidate.RoomIndex.ToString("00"));
        threatMarker.transform.position = new Vector3(candidate.ThreatPoint.x, 1.1f, candidate.ThreatPoint.z);
        Child(envelope, "TACTICAL_NATIVE_BRAINAI_WANDER_RADIUS_12M").transform.position = candidate.Position;
    }

    private static void BuildPvpSpawnMarkers(Transform parent, Layout layout, Vector3[] roomCenters,
        Vector2[] roomSizes, ConnectionPlan[] connections)
    {
        PvpSpawnPlan plan = CreatePvpSpawnPlan(parent.root, layout, roomCenters, roomSizes, connections);
        Vector3 team1Centroid = AveragePosition(plan.Team1Positions);
        Vector3 team2Centroid = AveragePosition(plan.Team2Positions);
        BuildPvpTeamMarkers(parent, 1, plan.Team1Rooms, plan.Team1Positions, team2Centroid);
        BuildPvpTeamMarkers(parent, 2, plan.Team2Rooms, plan.Team2Positions, team1Centroid);
    }

    private static void BuildPvpTeamMarkers(Transform parent, int team, int[] rooms, Vector3[] positions,
        Vector3 opposingCentroid)
    {
        if (rooms.Length != PvpRoomsPerSector || positions.Length != PvpSpawnsPerTeam)
            throw new InvalidDataException("PVP team " + team + " did not receive the exact 3-room/6-spawn contract.");
        GameObject sector = Child(parent.gameObject, "PVP_TEAM" + team + "_SPAWN_SECTOR");
        Child(sector, "PVP_TEAM" + team + "_SECTOR_ROOMS_" +
                      string.Join("_", rooms.Select(room => room.ToString("00"))));
        for (int index = 0; index < positions.Length; index++)
        {
            int roomIndex = rooms[index / 2];
            GameObject marker = Child(sector, "PVP_Team" + team + "Spawn_" + (index + 1).ToString("00"));
            marker.transform.position = positions[index];
            Vector3 facing = opposingCentroid - positions[index];
            facing.y = 0f;
            if (facing.sqrMagnitude < .01f)
                throw new InvalidDataException("PVP opposing-sector facing vector collapsed for Team " + team + ".");
            marker.transform.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
            Child(marker, "PVP_SPAWN_ROOM_" + roomIndex.ToString("00"));
            Child(marker, "PVP_SPAWN_CAPSULE_CLEAR");
            Child(marker, "PVP_SPAWN_PORTAL_CLEAR");
            Child(marker, "PVP_SPAWN_DOOR_CLEAR");
            Child(marker, "PVP_SPAWN_FURNITURE_CLEAR");
            Child(marker, "PVP_SPAWN_FACING_OPPOSING_SECTOR");
        }
    }

    private static PvpSpawnPlan CreatePvpSpawnPlan(Transform sceneRoot, Layout layout, Vector3[] roomCenters,
        Vector2[] roomSizes, ConnectionPlan[] connections)
    {
        var roomPairs = new Dictionary<int, PvpRoomSpawnPair>();
        for (int roomIndex = 1; roomIndex < roomCenters.Length; roomIndex++)
        {
            PvpRoomSpawnPair pair = TryBuildPvpRoomSpawnPair(sceneRoot, roomIndex, roomCenters, roomSizes,
                connections);
            if (pair != null) roomPairs[roomIndex] = pair;
        }

        int[][] sectors = BuildConnectedPvpSectors(roomPairs.Keys.OrderBy(value => value).ToArray(), connections);
        int[,] graphDistances = BuildAllPairsGraphDistances(layout.Cells.Length, connections);
        PvpSpawnPlan best = null;
        float bestCompactness = float.PositiveInfinity;
        string bestIdentity = string.Empty;
        for (int first = 0; first < sectors.Length; first++)
        {
            for (int second = first + 1; second < sectors.Length; second++)
            {
                int[] team1Rooms = sectors[first];
                int[] team2Rooms = sectors[second];
                if (team1Rooms.Intersect(team2Rooms).Any()) continue;
                int minimumGraphDistance = team1Rooms.Min(a => team2Rooms.Min(b => graphDistances[a, b]));
                if (minimumGraphDistance < PvpMinimumSectorGraphDistance) continue;
                Vector3[] team1Positions = team1Rooms.SelectMany(room => roomPairs[room].Positions).ToArray();
                Vector3[] team2Positions = team2Rooms.SelectMany(room => roomPairs[room].Positions).ToArray();
                if (!PvpSpawnPositionsHavePairClearance(team1Positions) ||
                    !PvpSpawnPositionsHavePairClearance(team2Positions)) continue;
                float minimumOpposingDistance = MinimumHorizontalDistance(team1Positions, team2Positions);
                if (minimumOpposingDistance < PvpMinimumOpposingDistance) continue;
                int directLineOfSightPairs = CountDirectPvpLineOfSightPairs(team1Positions, team2Positions);
                if (directLineOfSightPairs != 0) continue;
                float compactness = SectorSpan(team1Positions) + SectorSpan(team2Positions);
                string identity = string.Join(",", team1Rooms) + "|" + string.Join(",", team2Rooms);
                bool better = best == null || minimumGraphDistance > best.MinimumGraphDistance ||
                              minimumGraphDistance == best.MinimumGraphDistance &&
                              minimumOpposingDistance > best.MinimumOpposingDistance + .001f ||
                              minimumGraphDistance == best.MinimumGraphDistance &&
                              Mathf.Abs(minimumOpposingDistance - best.MinimumOpposingDistance) <= .001f &&
                              compactness < bestCompactness - .001f ||
                              minimumGraphDistance == best.MinimumGraphDistance &&
                              Mathf.Abs(minimumOpposingDistance - best.MinimumOpposingDistance) <= .001f &&
                              Mathf.Abs(compactness - bestCompactness) <= .001f &&
                              string.CompareOrdinal(identity, bestIdentity) < 0;
                if (!better) continue;
                best = new PvpSpawnPlan(team1Rooms, team2Rooms, team1Positions, team2Positions,
                    minimumGraphDistance, minimumOpposingDistance, directLineOfSightPairs);
                bestCompactness = compactness;
                bestIdentity = identity;
            }
        }
        if (best == null)
            throw new InvalidDataException("No two disjoint connected 3-room PVP sectors provide exact 6v6 " +
                                           "capsule clearance, four graph edges, 20 m separation, and zero direct spawn LOS.");
        return best;
    }

    private static PvpRoomSpawnPair TryBuildPvpRoomSpawnPair(Transform sceneRoot, int roomIndex,
        Vector3[] roomCenters, Vector2[] roomSizes, ConnectionPlan[] connections)
    {
        Vector3 center = roomCenters[roomIndex];
        Vector2 size = roomSizes[roomIndex];
        float minimumX = center.x - size.x * .5f + PvpSpawnWallInset;
        float maximumX = center.x + size.x * .5f - PvpSpawnWallInset;
        float minimumZ = center.z - size.y * .5f + PvpSpawnWallInset;
        float maximumZ = center.z + size.y * .5f - PvpSpawnWallInset;
        var clearGrid = new List<Vector3>();
        for (float x = minimumX; x <= maximumX + .01f; x += PvpSpawnGridStep)
        {
            for (float z = minimumZ; z <= maximumZ + .01f; z += PvpSpawnGridStep)
            {
                Vector3 candidate = new Vector3(x, .05f, z);
                if (PvpSpawnCapsuleBlocker(candidate, sceneRoot) != null || !PvpSpawnHasFloorSupport(candidate))
                    continue;
                clearGrid.Add(candidate);
            }
        }
        if (clearGrid.Count < 2) return null;

        List<ConnectionPlan> roomConnections = connections.Where(plan =>
            plan.RoomA == roomIndex || plan.RoomB == roomIndex).OrderBy(plan => plan.Key, StringComparer.Ordinal).ToList();
        if (roomConnections.Count == 0) return null;
        Vector3[] portalTargets = roomConnections.Select(plan =>
            RoomPortalPoint(plan, roomIndex, roomCenters, roomSizes)).ToArray();
        var targetIndexes = new List<int>();
        foreach (Vector3 target in portalTargets)
        {
            int nearest = Enumerable.Range(0, clearGrid.Count).OrderBy(index =>
                HorizontalDistance(clearGrid[index], target)).ThenBy(index => index).First();
            targetIndexes.Add(nearest);
        }

        var reachable = new HashSet<int>();
        var queue = new Queue<int>();
        reachable.Add(targetIndexes[0]);
        queue.Enqueue(targetIndexes[0]);
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            for (int candidate = 0; candidate < clearGrid.Count; candidate++)
            {
                if (reachable.Contains(candidate)) continue;
                float distance = HorizontalDistance(clearGrid[current], clearGrid[candidate]);
                if (distance > PvpSpawnGridStep + .01f) continue;
                if (Mathf.Abs(clearGrid[current].x - clearGrid[candidate].x) > .01f &&
                    Mathf.Abs(clearGrid[current].z - clearGrid[candidate].z) > .01f) continue;
                reachable.Add(candidate);
                queue.Enqueue(candidate);
            }
        }
        if (targetIndexes.Any(index => !reachable.Contains(index))) return null;

        Vector3[] candidates = reachable.Select(index => clearGrid[index])
            .Where(position => PvpPortalClearanceFailure(position, roomIndex, roomCenters, roomSizes,
                                   roomConnections) == null &&
                               PvpDoorClearanceFailure(position, roomConnections, sceneRoot) == null)
            .OrderBy(position => position.x).ThenBy(position => position.z).ToArray();
        Vector3 first = default;
        Vector3 second = default;
        float bestDistance = -1f;
        string bestIdentity = string.Empty;
        for (int left = 0; left < candidates.Length; left++)
        {
            for (int right = left + 1; right < candidates.Length; right++)
            {
                float distance = HorizontalDistance(candidates[left], candidates[right]);
                if (distance < PvpSpawnPairClearance) continue;
                string identity = candidates[left].x.ToString("F3") + "," + candidates[left].z.ToString("F3") +
                                  "|" + candidates[right].x.ToString("F3") + "," +
                                  candidates[right].z.ToString("F3");
                if (distance < bestDistance - .001f ||
                    Mathf.Abs(distance - bestDistance) <= .001f &&
                    string.CompareOrdinal(identity, bestIdentity) >= 0) continue;
                first = candidates[left];
                second = candidates[right];
                bestDistance = distance;
                bestIdentity = identity;
            }
        }
        return bestDistance < PvpSpawnPairClearance ? null : new PvpRoomSpawnPair(roomIndex, first, second);
    }

    private static int[][] BuildConnectedPvpSectors(int[] feasibleRooms, ConnectionPlan[] connections)
    {
        var adjacency = feasibleRooms.ToDictionary(room => room, _ => new HashSet<int>());
        foreach (ConnectionPlan plan in connections)
        {
            if (!adjacency.ContainsKey(plan.RoomA) || !adjacency.ContainsKey(plan.RoomB)) continue;
            adjacency[plan.RoomA].Add(plan.RoomB);
            adjacency[plan.RoomB].Add(plan.RoomA);
        }
        var sectors = new List<int[]>();
        for (int first = 0; first < feasibleRooms.Length - 2; first++)
        for (int second = first + 1; second < feasibleRooms.Length - 1; second++)
        for (int third = second + 1; third < feasibleRooms.Length; third++)
        {
            int[] rooms = { feasibleRooms[first], feasibleRooms[second], feasibleRooms[third] };
            var visited = new HashSet<int> { rooms[0] };
            var queue = new Queue<int>();
            queue.Enqueue(rooms[0]);
            while (queue.Count > 0)
            {
                int room = queue.Dequeue();
                foreach (int neighbor in adjacency[room])
                {
                    if (!rooms.Contains(neighbor) || !visited.Add(neighbor)) continue;
                    queue.Enqueue(neighbor);
                }
            }
            if (visited.Count == PvpRoomsPerSector) sectors.Add(rooms);
        }
        return sectors.ToArray();
    }

    private static int[,] BuildAllPairsGraphDistances(int roomCount, IEnumerable<ConnectionPlan> connections)
    {
        var adjacency = Enumerable.Range(0, roomCount).ToDictionary(index => index, _ => new List<int>());
        foreach (ConnectionPlan connection in connections)
        {
            adjacency[connection.RoomA].Add(connection.RoomB);
            adjacency[connection.RoomB].Add(connection.RoomA);
        }
        var distances = new int[roomCount, roomCount];
        for (int source = 0; source < roomCount; source++)
        {
            for (int target = 0; target < roomCount; target++) distances[source, target] = int.MaxValue;
            distances[source, source] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(source);
            while (queue.Count > 0)
            {
                int room = queue.Dequeue();
                foreach (int neighbor in adjacency[room])
                {
                    if (distances[source, neighbor] <= distances[source, room] + 1) continue;
                    distances[source, neighbor] = distances[source, room] + 1;
                    queue.Enqueue(neighbor);
                }
            }
        }
        return distances;
    }

    private static Collider PvpSpawnCapsuleBlocker(Vector3 position, Transform sceneRoot)
    {
        Collider[] hits = Physics.OverlapCapsule(position + Vector3.up * PvpSpawnCapsuleBottom,
            position + Vector3.up * PvpSpawnCapsuleTop, PvpSpawnCapsuleRadius, ~0,
            QueryTriggerInteraction.Ignore);
        return hits.FirstOrDefault(hit => hit != null && hit.enabled && !hit.isTrigger &&
            IsDescendantOf(hit.transform, sceneRoot) && !HasAncestorNamed(hit.transform, "NATIVE_Floor") &&
            !HasAncestorNamed(hit.transform, "NATIVE_WarehouseGroundApron"));
    }

    private static bool PvpSpawnHasFloorSupport(Vector3 position)
    {
        if (!Physics.Raycast(position + Vector3.up * .28f, Vector3.down, out RaycastHit hit, .4f, ~0,
                QueryTriggerInteraction.Ignore)) return false;
        return hit.collider != null && !hit.collider.isTrigger &&
               (HasAncestorNamed(hit.collider.transform, "NATIVE_Floor") ||
                HasAncestorNamed(hit.collider.transform, "NATIVE_WarehouseGroundApron")) &&
               hit.normal.y >= .95f;
    }

    private static string PvpPortalClearanceFailure(Vector3 position, int roomIndex, Vector3[] roomCenters,
        Vector2[] roomSizes, IEnumerable<ConnectionPlan> connections)
    {
        foreach (ConnectionPlan plan in connections)
        {
            Vector2Int outward = ConnectionDirectionForRoom(plan, roomIndex);
            if (outward == Vector2Int.zero) continue;
            Vector3 tangent = outward.x != 0 ? Vector3.forward : Vector3.right;
            Vector3 authored = RoomPortalPoint(plan, roomIndex, roomCenters, roomSizes);
            foreach (float offset in PortalProbeOffsets(plan))
            {
                Vector3 exact = authored + tangent * (offset - plan.PortalOffset);
                if (HorizontalDistance(position, exact) < PvpSpawnPortalClearance)
                    return plan.Key;
            }
        }
        return null;
    }

    private static string PvpDoorClearanceFailure(Vector3 position, IEnumerable<ConnectionPlan> connections,
        Transform sceneRoot)
    {
        foreach (ConnectionPlan plan in connections.Where(value => value.Portal == PortalKind.Door))
        {
            Transform socket = FindDescendantByExactName(sceneRoot, "DOORV2_SOCKET_" + plan.Key);
            if (socket != null && HorizontalDistance(position, socket.position) < PvpSpawnDoorClearance)
                return socket.name;
        }
        return null;
    }

    private static Transform FindDescendantByExactName(Transform root, string name)
    {
        return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(item =>
            string.Equals(item.name, name, StringComparison.Ordinal));
    }

    private static bool HasAncestorNamed(Transform transform, string name)
    {
        while (transform != null)
        {
            if (string.Equals(transform.name, name, StringComparison.Ordinal)) return true;
            transform = transform.parent;
        }
        return false;
    }

    private static int CountDirectPvpLineOfSightPairs(IEnumerable<Vector3> team1,
        IEnumerable<Vector3> team2)
    {
        int count = 0;
        foreach (Vector3 first in team1)
        foreach (Vector3 second in team2)
        {
            if (!Physics.Linecast(first + Vector3.up * 1.55f, second + Vector3.up * 1.55f, ~0,
                    QueryTriggerInteraction.Ignore)) count++;
        }
        return count;
    }

    private static float MinimumHorizontalDistance(IEnumerable<Vector3> first, IEnumerable<Vector3> second)
    {
        return first.Min(left => second.Min(right => HorizontalDistance(left, right)));
    }

    private static bool PvpSpawnPositionsHavePairClearance(IReadOnlyList<Vector3> positions)
    {
        for (int first = 0; first < positions.Count; first++)
        for (int second = first + 1; second < positions.Count; second++)
            if (HorizontalDistance(positions[first], positions[second]) < PvpSpawnPairClearance - .01f)
                return false;
        return true;
    }

    private static float SectorSpan(IReadOnlyList<Vector3> positions)
    {
        float maximum = 0f;
        for (int first = 0; first < positions.Count; first++)
        for (int second = first + 1; second < positions.Count; second++)
            maximum = Mathf.Max(maximum, HorizontalDistance(positions[first], positions[second]));
        return maximum;
    }

    private static float HorizontalDistance(Vector3 first, Vector3 second)
    {
        return Vector2.Distance(new Vector2(first.x, first.z), new Vector2(second.x, second.z));
    }

    private static int CountPveEnemySpawnClearanceFailures(IReadOnlyList<Transform> markers,
        out float minimumDistance)
    {
        int failures = 0;
        minimumDistance = float.PositiveInfinity;
        for (int first = 0; first < markers.Count; first++)
        for (int second = first + 1; second < markers.Count; second++)
        {
            float distance = HorizontalDistance(markers[first].position, markers[second].position);
            minimumDistance = Mathf.Min(minimumDistance, distance);
            if (distance < PveEnemySpawnPairClearance - .001f) failures++;
        }
        return failures;
    }

    private static Vector3 AveragePosition(IEnumerable<Vector3> positions)
    {
        Vector3[] values = positions.ToArray();
        return values.Aggregate(Vector3.zero, (sum, value) => sum + value) / values.Length;
    }

    private static string PvpSpawnMarkerFailure(Transform marker, int team, Vector3 opposingCentroid,
        Transform sceneRoot, Vector3[] roomCenters, Vector2[] roomSizes, ConnectionPlan[] connections)
    {
        if (marker == null) return "marker-null";
        string expectedPrefix = "PVP_Team" + team + "Spawn_";
        if (!marker.name.StartsWith(expectedPrefix, StringComparison.Ordinal)) return "identity-prefix";
        Transform[] roomMarkers = Enumerable.Range(0, marker.childCount).Select(marker.GetChild)
            .Where(child => child.name.StartsWith("PVP_SPAWN_ROOM_", StringComparison.Ordinal)).ToArray();
        if (roomMarkers.Length != 1 ||
            !int.TryParse(roomMarkers.FirstOrDefault()?.name.Substring("PVP_SPAWN_ROOM_".Length), out int roomIndex) ||
            roomIndex <= 0 || roomIndex >= roomCenters.Length) return "room-ownership-metadata";
        Vector3 center = roomCenters[roomIndex];
        Vector2 size = roomSizes[roomIndex];
        if (marker.position.x < center.x - size.x * .5f + PvpSpawnWallInset - .01f ||
            marker.position.x > center.x + size.x * .5f - PvpSpawnWallInset + .01f ||
            marker.position.z < center.z - size.y * .5f + PvpSpawnWallInset - .01f ||
            marker.position.z > center.z + size.y * .5f - PvpSpawnWallInset + .01f)
            return "room-ownership-bounds";
        Collider blocker = PvpSpawnCapsuleBlocker(marker.position, sceneRoot);
        if (blocker != null)
        {
            if (HasAncestorNamed(blocker.transform, "30_NATIVE_ROOM_DRESSING"))
                return "furniture-clearance:" + blocker.name;
            if (HasAncestorNamed(blocker.transform, "NATIVE_DOORV2_SHELL"))
                return "door-approach:" + blocker.name;
            return "capsule-clearance:" + blocker.name;
        }
        if (!PvpSpawnHasFloorSupport(marker.position)) return "capsule-support";
        List<ConnectionPlan> roomConnections = connections.Where(plan =>
            plan.RoomA == roomIndex || plan.RoomB == roomIndex).ToList();
        string portalFailure = PvpPortalClearanceFailure(marker.position, roomIndex, roomCenters, roomSizes,
            roomConnections);
        if (!string.IsNullOrEmpty(portalFailure)) return "portal-approach:" + portalFailure;
        string doorFailure = PvpDoorClearanceFailure(marker.position, roomConnections, sceneRoot);
        if (!string.IsNullOrEmpty(doorFailure)) return "door-approach:" + doorFailure;
        Vector3 expectedFacing = opposingCentroid - marker.position;
        expectedFacing.y = 0f;
        Vector3 actualFacing = marker.forward;
        actualFacing.y = 0f;
        if (expectedFacing.sqrMagnitude < .01f || actualFacing.sqrMagnitude < .01f ||
            Vector3.Dot(expectedFacing.normalized, actualFacing.normalized) < .999f)
            return "facing-opposing-sector";
        string[] requiredEvidence =
        {
            "PVP_SPAWN_CAPSULE_CLEAR", "PVP_SPAWN_PORTAL_CLEAR", "PVP_SPAWN_DOOR_CLEAR",
            "PVP_SPAWN_FURNITURE_CLEAR", "PVP_SPAWN_FACING_OPPOSING_SECTOR"
        };
        if (requiredEvidence.Any(name => Enumerable.Range(0, marker.childCount).Select(marker.GetChild)
                .Count(child => string.Equals(child.name, name, StringComparison.Ordinal)) != 1))
            return "metadata-closure";
        return string.Empty;
    }

    private static int PvpSpawnRoomIndex(Transform marker)
    {
        if (marker == null) return -1;
        Transform roomMarker = Enumerable.Range(0, marker.childCount).Select(marker.GetChild)
            .FirstOrDefault(child => child.name.StartsWith("PVP_SPAWN_ROOM_", StringComparison.Ordinal));
        return roomMarker != null && int.TryParse(roomMarker.name.Substring("PVP_SPAWN_ROOM_".Length),
            out int roomIndex) ? roomIndex : -1;
    }

    private static bool PvpSectorRoomsConnected(IEnumerable<int> rooms, IEnumerable<ConnectionPlan> connections)
    {
        int[] values = rooms.Distinct().OrderBy(value => value).ToArray();
        if (values.Length != PvpRoomsPerSector || values.Any(room => room <= 0)) return false;
        var visited = new HashSet<int> { values[0] };
        var queue = new Queue<int>();
        queue.Enqueue(values[0]);
        while (queue.Count > 0)
        {
            int room = queue.Dequeue();
            foreach (ConnectionPlan plan in connections.Where(plan => plan.RoomA == room || plan.RoomB == room))
            {
                int neighbor = plan.RoomA == room ? plan.RoomB : plan.RoomA;
                if (!values.Contains(neighbor) || !visited.Add(neighbor)) continue;
                queue.Enqueue(neighbor);
            }
        }
        return visited.Count == PvpRoomsPerSector;
    }

    private static int CountPvpSameTeamClearanceFailures(IReadOnlyList<Transform> markers)
    {
        int failures = 0;
        for (int first = 0; first < markers.Count; first++)
        for (int second = first + 1; second < markers.Count; second++)
            if (HorizontalDistance(markers[first].position, markers[second].position) <
                PvpSpawnPairClearance - .01f) failures++;
        return failures;
    }

    private static int[] BuildGraphDistances(int roomCount, IEnumerable<ConnectionPlan> connections)
    {
        var adjacency = Enumerable.Range(0, roomCount).ToDictionary(index => index, _ => new List<int>());
        foreach (ConnectionPlan connection in connections)
        {
            adjacency[connection.RoomA].Add(connection.RoomB);
            adjacency[connection.RoomB].Add(connection.RoomA);
        }
        int[] distances = Enumerable.Repeat(int.MaxValue, roomCount).ToArray();
        var queue = new Queue<int>();
        distances[0] = 0;
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            int room = queue.Dequeue();
            foreach (int neighbor in adjacency[room])
            {
                if (distances[neighbor] <= distances[room] + 1) continue;
                distances[neighbor] = distances[room] + 1;
                queue.Enqueue(neighbor);
            }
        }
        if (distances.Any(distance => distance == int.MaxValue))
            throw new InvalidDataException("Tactical spawn planning found a room disconnected from the safe room.");
        return distances;
    }

    private static List<Vector3> BuildThreatPoints(int roomIndex, Vector3[] roomCenters, Vector2[] roomSizes,
        IEnumerable<ConnectionPlan> connections, int[] distancesFromSafe)
    {
        return connections.Where(connection => connection.RoomA == roomIndex || connection.RoomB == roomIndex)
            .OrderBy(connection => distancesFromSafe[connection.RoomA == roomIndex ? connection.RoomB : connection.RoomA])
            .ThenBy(connection => connection.Key, StringComparer.Ordinal)
            .Select(connection => RoomPortalPoint(connection, roomIndex, roomCenters, roomSizes))
            .ToList();
    }

    private static Vector3 RoomPortalPoint(ConnectionPlan connection, int roomIndex, Vector3[] roomCenters,
        Vector2[] roomSizes)
    {
        Vector2Int outward = connection.RoomA == roomIndex ? connection.DirectionFromA : -connection.DirectionFromA;
        Vector3 normal = new Vector3(outward.x, 0f, outward.y);
        Vector3 tangent = outward.x != 0 ? Vector3.forward : Vector3.right;
        float halfSide = outward.x != 0 ? roomSizes[roomIndex].x * .5f : roomSizes[roomIndex].y * .5f;
        return roomCenters[roomIndex] + normal * (halfSide - .8f) + tangent * connection.PortalOffset + Vector3.up * .05f;
    }

    private static Transform FindPreferredTacticalCover(Transform propsRoot, int roomIndex, RoomType type, int ordinal)
    {
        string dressingPrefix = "DRESSING_" + roomIndex.ToString("00") + "_";
        Transform dressing = Enumerable.Range(0, propsRoot.childCount).Select(propsRoot.GetChild)
            .FirstOrDefault(child => child.name.StartsWith(dressingPrefix, StringComparison.Ordinal));
        if (dressing == null) return null;
        string[] preferred = type switch
        {
            RoomType.Living => new[] { "NATIVE_LivingSofa", "NATIVE_OpenTable", "NATIVE_LivingBookcase", "NATIVE_LivingConsole", "NATIVE_LowDivider_" },
            RoomType.Bedroom => new[] { "NATIVE_Bed", "NATIVE_SideTable" },
            RoomType.Bathroom => new[] { "NATIVE_Sink", "NATIVE_Toilet" },
            RoomType.Kitchen => new[] { "NATIVE_KitchenTable", "NATIVE_KitchenCabinet", "NATIVE_FridgeCabinet", "NATIVE_LowDivider_" },
            RoomType.Dining => new[] { "NATIVE_DiningTable", "NATIVE_DiningSideboard", "NATIVE_LowDivider_" },
            RoomType.Study => new[] { "NATIVE_WorkDesk", "NATIVE_Bookcase" },
            RoomType.Storage => new[] { "NATIVE_StorageShelf_A", "NATIVE_StorageShelf_B", "NATIVE_StorageCabinet" },
            RoomType.Hallway => new[] { "NATIVE_HallTable" },
            RoomType.Junction => new[] { "NATIVE_JunctionStorage" },
            RoomType.Blank => new[] { "NATIVE_OpenSofa", "NATIVE_InteriorSplitWall_", "NATIVE_LowDivider_", "NATIVE_OfficePartition_" },
            _ => Array.Empty<string>()
        };
        var candidates = new List<Transform>();
        foreach (string prefix in preferred)
        {
            candidates.AddRange(Enumerable.Range(0, dressing.childCount).Select(dressing.GetChild)
                .Where(child => child.name.StartsWith(prefix, StringComparison.Ordinal) &&
                                child.GetComponentsInChildren<Renderer>(true).Length > 0 &&
                                child.GetComponentsInChildren<Collider>(true).Any(collider => !collider.isTrigger)));
        }
        if (candidates.Count == 0) return null;
        return candidates[Mathf.Min(ordinal, candidates.Count - 1)];
    }

    private static bool TryBuildPropCoverPosition(Transform cover, Vector3 roomCenter, Vector2 roomSize,
        Vector3 threatPoint, int seed, out Vector3 markerPosition, out Vector3 coverPoint)
    {
        Bounds bounds = RendererBounds(cover.gameObject);
        coverPoint = new Vector3(bounds.center.x, Mathf.Clamp(bounds.center.y, .65f, 1.15f), bounds.center.z);
        Vector3 away = coverPoint - threatPoint;
        away.y = 0f;
        if (away.sqrMagnitude < .05f) away = (seed & 1) == 0 ? Vector3.right : Vector3.forward;
        away.Normalize();
        Vector3 side = new Vector3(-away.z, 0f, away.x);
        float coverRadius = Mathf.Abs(away.x) * bounds.extents.x + Mathf.Abs(away.z) * bounds.extents.z;
        // The IL2CPP runtime reconstructs several shipped furniture colliders slightly larger than
        // their Editor renderer bounds. Keep a full body-radius buffer beyond that observed drift so
        // a valid authored ambush never rehydrates with its standing capsule inside the cover prop.
        Vector3 desired = new Vector3(bounds.center.x, .05f, bounds.center.z) + away * (coverRadius + 1.22f) +
                          side * ((seed & 1) == 0 ? .55f : -.55f);
        if (!TryFindClearTacticalPosition(desired, away, side, roomCenter, roomSize, out markerPosition)) return false;
        float threatDistance = Vector2.Distance(new Vector2(markerPosition.x, markerPosition.z),
            new Vector2(threatPoint.x, threatPoint.z));
        if (threatDistance < 1.4f)
        {
            Vector3 farther = markerPosition + away * (1.4f - threatDistance + .25f);
            if (!TryFindClearTacticalPosition(farther, away, side, roomCenter, roomSize, out markerPosition)) return false;
            if (Vector2.Distance(new Vector2(markerPosition.x, markerPosition.z),
                    new Vector2(threatPoint.x, threatPoint.z)) < 1.4f) return false;
        }
        Collider[] physicalCover = cover.GetComponentsInChildren<Collider>(true)
            .Where(collider => collider.enabled && !collider.isTrigger).ToArray();
        if (physicalCover.Length == 0) return false;
        Vector3 probe = markerPosition + Vector3.up * .95f;
        // Bounds.ClosestPoint also works for the non-convex MeshColliders used by exact extracted
        // vanilla props; Collider.ClosestPoint rejects those meshes in the Editor.
        coverPoint = physicalCover.Select(collider => collider.bounds.ClosestPoint(probe))
            .OrderBy(point => (point - probe).sqrMagnitude).First();
        Vector3 markerToCover = coverPoint - markerPosition;
        markerToCover.y = 0f;
        // Measure against the physical collider, not the renderer. If a small room clamps the marker
        // back toward the prop, reject it and let the architectural-cover fallback choose a safe wall.
        if (markerToCover.magnitude < .78f) return false;
        Physics.SyncTransforms();
        return Physics.OverlapSphere(coverPoint, .72f, ~0, QueryTriggerInteraction.Ignore)
            .Any(collider => collider != null && IsDescendantOf(collider.transform, cover));
    }

    private static void BuildArchitecturalCoverPosition(Vector3 roomCenter, Vector2 roomSize, Vector3 threatPoint,
        int seed, out Vector3 markerPosition, out Vector3 coverPoint)
    {
        float halfX = roomSize.x * .5f;
        float halfZ = roomSize.y * .5f;
        Vector3 fromThreat = roomCenter - threatPoint;
        int preferredX = fromThreat.x >= 0f ? 1 : -1;
        int preferredZ = fromThreat.z >= 0f ? 1 : -1;
        if ((seed & 1) != 0) preferredX *= -1;
        if ((seed & 2) != 0) preferredZ *= -1;
        Vector3 desired = roomCenter + new Vector3(preferredX * Mathf.Max(.4f, halfX - .82f), .05f,
            preferredZ * Mathf.Max(.4f, halfZ - .82f));
        Vector3 inward = (roomCenter - desired).normalized;
        Vector3 side = new Vector3(-inward.z, 0f, inward.x);
        if (!TryFindClearTacticalPosition(desired, inward, side, roomCenter, roomSize, out markerPosition) &&
            !TryFindAnyClearTacticalPosition(roomCenter, roomSize, threatPoint, out markerPosition))
            throw new InvalidDataException("Room has no clear tactical standing position.");
        if (Vector2.Distance(new Vector2(markerPosition.x, markerPosition.z),
                new Vector2(threatPoint.x, threatPoint.z)) < 1.4f &&
            !TryFindAnyClearTacticalPosition(roomCenter, roomSize, threatPoint, out markerPosition))
            throw new InvalidDataException("Room has no tactical position separated from its ingress point.");
        Physics.SyncTransforms();
        if (!TryFindBackedArchitecturalCoverPoint(markerPosition, roomCenter, roomSize, out coverPoint))
            throw new InvalidDataException("Room has no collider-backed architectural cover point.");
    }

    private static bool TryFindBackedArchitecturalCoverPoint(Vector3 markerPosition, Vector3 roomCenter,
        Vector2 roomSize, out Vector3 coverPoint)
    {
        float halfX = roomSize.x * .5f;
        float halfZ = roomSize.y * .5f;
        float[] offsets = { 0f, -1f, 1f, -2f, 2f, -3f, 3f };
        var candidates = new List<Vector3>();
        foreach (float offset in offsets)
        {
            float z = Mathf.Clamp(markerPosition.z + offset, roomCenter.z - halfZ + .65f,
                roomCenter.z + halfZ - .65f);
            float x = Mathf.Clamp(markerPosition.x + offset, roomCenter.x - halfX + .65f,
                roomCenter.x + halfX - .65f);
            candidates.Add(new Vector3(roomCenter.x - halfX, 1f, z));
            candidates.Add(new Vector3(roomCenter.x + halfX, 1f, z));
            candidates.Add(new Vector3(x, 1f, roomCenter.z - halfZ));
            candidates.Add(new Vector3(x, 1f, roomCenter.z + halfZ));
        }
        foreach (Vector3 candidate in candidates.Distinct().OrderBy(value =>
                     Vector2.Distance(new Vector2(markerPosition.x, markerPosition.z), new Vector2(value.x, value.z))))
        {
            float distance = Vector2.Distance(new Vector2(markerPosition.x, markerPosition.z),
                new Vector2(candidate.x, candidate.z));
            if (distance < .35f || distance > 4.25f) continue;
            Collider[] hits = Physics.OverlapSphere(candidate, .72f, ~0, QueryTriggerInteraction.Ignore);
            if (!hits.Any(collider => collider != null && HasNativeCoverAncestor(collider.transform))) continue;
            coverPoint = candidate;
            return true;
        }
        coverPoint = default;
        return false;
    }

    private static bool TryFindClearTacticalPosition(Vector3 desired, Vector3 away, Vector3 side,
        Vector3 roomCenter, Vector2 roomSize, out Vector3 position)
    {
        Vector3[] candidates =
        {
            desired, desired + side * .55f, desired - side * .55f,
            desired + away * .45f, desired + away * .45f + side * .55f,
            desired + away * .45f - side * .55f, desired - away * .35f
        };
        foreach (Vector3 candidate in candidates.Select(value => ClampInsideRoom(value, roomCenter, roomSize)))
        {
            Vector3 bottom = candidate + Vector3.up * .42f;
            Vector3 top = candidate + Vector3.up * 1.58f;
            if (Physics.CheckCapsule(bottom, top, .3f, ~0, QueryTriggerInteraction.Ignore)) continue;
            position = new Vector3(candidate.x, .05f, candidate.z);
            return true;
        }
        position = default;
        return false;
    }

    private static bool TryFindAnyClearTacticalPosition(Vector3 roomCenter, Vector2 roomSize, Vector3 threatPoint,
        out Vector3 position)
    {
        var candidates = new List<Vector3>();
        float halfX = Mathf.Max(.5f, roomSize.x * .5f - .75f);
        float halfZ = Mathf.Max(.5f, roomSize.y * .5f - .75f);
        for (float x = -halfX; x <= halfX + .01f; x += 1f)
            for (float z = -halfZ; z <= halfZ + .01f; z += 1f)
                candidates.Add(roomCenter + new Vector3(x, .05f, z));
        foreach (Vector3 candidate in candidates.OrderByDescending(value =>
                     Vector2.SqrMagnitude(new Vector2(value.x - threatPoint.x, value.z - threatPoint.z))))
        {
            Vector3 bottom = candidate + Vector3.up * .42f;
            Vector3 top = candidate + Vector3.up * 1.58f;
            if (Physics.CheckCapsule(bottom, top, .3f, ~0, QueryTriggerInteraction.Ignore)) continue;
            position = candidate;
            return true;
        }
        position = default;
        return false;
    }

    private static Vector3 ClampInsideRoom(Vector3 position, Vector3 center, Vector2 size)
    {
        float insetX = Mathf.Max(.45f, size.x * .5f - .68f);
        float insetZ = Mathf.Max(.45f, size.y * .5f - .68f);
        return new Vector3(Mathf.Clamp(position.x, center.x - insetX, center.x + insetX), .05f,
            Mathf.Clamp(position.z, center.z - insetZ, center.z + insetZ));
    }

    private static string SanitizeMarkerName(string value)
    {
        return new string(value.Select(character => char.IsLetterOrDigit(character) || character == '_'
            ? character
            : '_').ToArray());
    }

    private static bool TacticalMarkerValid(Transform marker)
    {
        return string.IsNullOrEmpty(TacticalMarkerFailure(marker));
    }

    private static string TacticalMarkerFailure(Transform marker)
    {
        if (marker == null || marker.parent == null ||
            !marker.parent.name.StartsWith("TACTICAL_POSITION_", StringComparison.Ordinal)) return "envelope";
        Transform role = FindDirectChild(marker.parent, "TACTICAL_ROLE_");
        Transform cover = FindDirectChild(marker.parent, "TACTICAL_COVER_POINT_");
        Transform threat = FindDirectChild(marker.parent, "TACTICAL_THREAT_POINT_");
        Transform profile = FindDirectChild(marker.parent, "TACTICAL_NATIVE_BRAINAI_WANDER_RADIUS_12M");
        if (role == null || cover == null || threat == null || profile == null) return "metadata";
        Vector3 toThreat = threat.position - marker.position;
        toThreat.y = 0f;
        Vector3 facing = marker.forward;
        facing.y = 0f;
        float coverDistance = Vector2.Distance(new Vector2(marker.position.x, marker.position.z),
            new Vector2(cover.position.x, cover.position.z));
        float threatDistance = toThreat.magnitude;
        float facingAlignment = threatDistance < .01f ? -1f : Vector3.Dot(facing.normalized, toThreat.normalized);
        Vector3 capsuleBottom = marker.position + Vector3.up * .42f;
        Vector3 capsuleTop = marker.position + Vector3.up * 1.58f;
        bool standingClear = !Physics.CheckCapsule(capsuleBottom, capsuleTop, .3f, ~0,
            QueryTriggerInteraction.Ignore);
        Collider[] nearbyCover = Physics.OverlapSphere(cover.position, .72f, ~0, QueryTriggerInteraction.Ignore);
        bool coverBacked = nearbyCover.Any(collider => collider != null && HasNativeCoverAncestor(collider.transform));
        if (coverDistance < .35f || coverDistance > 4.25f) return "cover-distance";
        if (threatDistance < 1.25f) return "threat-distance";
        if (facingAlignment < .82f) return "facing";
        if (!standingClear) return "standing-clearance";
        if (!coverBacked) return "cover-collider:" + cover.name;
        return string.Empty;
    }

    private static Transform FindDirectChild(Transform parent, string prefix)
    {
        for (int index = 0; index < parent.childCount; index++)
        {
            Transform child = parent.GetChild(index);
            if (child.name.StartsWith(prefix, StringComparison.Ordinal)) return child;
        }
        return null;
    }

    private static bool HasNativeCoverAncestor(Transform transform)
    {
        while (transform != null)
        {
            if (transform.name.StartsWith("NATIVE_", StringComparison.Ordinal) &&
                transform.name != "NATIVE_Floor" && transform.name != "NATIVE_Ceiling") return true;
            transform = transform.parent;
        }
        return false;
    }

    private static GameObject PlaceWallBackedProp(Transform parent, string meshName, string name, Vector3 roomCenter,
        Vector2 roomSize, int roomIndex, IReadOnlyList<ConnectionPlan> connections, int seed, float tangentBias,
        float scale)
    {
        WallBackedFurnitureContract contract = GetWallBackedFurnitureContract(meshName);
        GameObject instance = Instantiate(KillHouseNativePrefabBuilder.Load(meshName), parent, name);
        Vector2Int selectedOutward = Vector2Int.zero;
        bool placed = false;
        Vector2Int[] directions = { Vector2Int.right, Vector2Int.up, Vector2Int.left, Vector2Int.down };
        IEnumerable<Vector2Int> walls = directions
            .OrderBy(direction => connections.Count(plan => ConnectionDirectionForRoom(plan, roomIndex) == direction))
            .ThenBy(direction => (Array.IndexOf(directions, direction) - seed % directions.Length + directions.Length) % directions.Length);
        foreach (Vector2Int outward in walls)
        {
            Vector3 outward3 = new Vector3(outward.x, 0f, outward.y);
            Vector3 tangent = outward.x != 0 ? Vector3.forward : Vector3.right;
            float outwardExtent = outward.x != 0 ? roomSize.x * .5f : roomSize.y * .5f;
            float sideLength = outward.x != 0 ? roomSize.y : roomSize.x;
            float maxTangent = Mathf.Max(0f, sideLength * .5f - 1.15f);
            float desired = Mathf.Clamp(tangentBias, -maxTangent, maxTangent);
            foreach (float tangentOffset in FurnitureTangentCandidates(roomIndex, outward, connections,
                         desired, maxTangent, seed))
            {
                Vector3 approximate = roomCenter + outward3 * Mathf.Max(.25f, outwardExtent - .35f) +
                                      tangent * tangentOffset;
                Quaternion rotation = RotationForLocalHorizontalAxis(contract.LocalInteriorAxis, -outward3);
                AlignGrounded(instance, approximate, rotation, Vector3.one * scale);
                Bounds bounds = RendererBounds(instance);
                float currentBack = Vector3.Dot(bounds.center - roomCenter, outward3) +
                    Mathf.Abs(outward3.x) * bounds.extents.x + Mathf.Abs(outward3.z) * bounds.extents.z;
                instance.transform.position += outward3 * (outwardExtent - .12f - currentBack);
                Physics.SyncTransforms();
                bounds = RendererBounds(instance);
                if (!BoundsInsideRoom(bounds, roomCenter, roomSize, .02f)) continue;
                if (!TryPhysicalBounds(instance, out Bounds physicalBounds) ||
                    !BoundsInsideRoom(physicalBounds, roomCenter, roomSize, .02f)) continue;
                if (PropBlocksAnyPortalApproach(instance.transform, roomIndex, connections, roomCenter, roomSize))
                    continue;
                if (OverlapsPlacedWallBackedFurniture(instance.transform, parent, .02f)) continue;
                selectedOutward = outward;
                placed = true;
                break;
            }
            if (placed) break;
        }
        if (!placed)
        {
            UnityEngine.Object.DestroyImmediate(instance);
            throw new InvalidDataException(name + " cannot be placed against a wall without blocking a portal approach in room " +
                                           roomIndex.ToString("00") + ".");
        }
        GameObject marker = Child(instance, "WALL_BACKED_PROP_OUTWARD_" +
            (selectedOutward == Vector2Int.right ? "E" : selectedOutward == Vector2Int.left ? "W" :
                selectedOutward == Vector2Int.up ? "N" : "S"));
        marker.transform.position = instance.transform.position;
        Child(instance, contract.ProvenanceMarker);
        return instance;
    }

    private static WallBackedFurnitureContract GetWallBackedFurnitureContract(string meshName)
    {
        if (UnsupportedStandaloneFurnitureMeshes.Contains(meshName ?? string.Empty))
            throw new InvalidDataException(meshName +
                " is an excluded standalone submesh/assembly and cannot be placed without a complete vanilla donor root.");
        if (!WallBackedFurnitureContracts.TryGetValue(meshName ?? string.Empty,
                out WallBackedFurnitureContract contract))
            throw new InvalidDataException("No direct installed front/back provenance exists for wall-backed mesh " +
                                           (meshName ?? "<null>") + ".");
        Vector3 interior = contract.LocalInteriorAxis;
        Vector3 wall = contract.LocalWallAxis;
        interior.y = 0f;
        wall.y = 0f;
        if (interior.sqrMagnitude < .999f || wall.sqrMagnitude < .999f ||
            Vector3.Dot(interior.normalized, wall.normalized) > -.999f ||
            contract.OrderedMaterials == null || contract.OrderedMaterials.Length == 0 ||
            string.IsNullOrWhiteSpace(contract.InstalledProvenance))
            throw new InvalidDataException("Malformed installed orientation/provenance contract for " + meshName + ".");
        return contract;
    }

    private static Quaternion RotationForLocalHorizontalAxis(Vector3 localAxis, Vector3 desiredWorldAxis)
    {
        localAxis.y = 0f;
        desiredWorldAxis.y = 0f;
        if (localAxis.sqrMagnitude < .999f || desiredWorldAxis.sqrMagnitude < .999f)
            throw new InvalidDataException("Furniture orientation axes must be unit horizontal vectors.");
        float localYaw = Mathf.Atan2(localAxis.x, localAxis.z) * Mathf.Rad2Deg;
        float desiredYaw = Mathf.Atan2(desiredWorldAxis.x, desiredWorldAxis.z) * Mathf.Rad2Deg;
        return Quaternion.Euler(0f, desiredYaw - localYaw, 0f);
    }

    private static bool BoundsInsideRoom(Bounds bounds, Vector3 roomCenter, Vector2 roomSize, float tolerance)
    {
        float halfX = roomSize.x * .5f + tolerance;
        float halfZ = roomSize.y * .5f + tolerance;
        return bounds.min.x >= roomCenter.x - halfX && bounds.max.x <= roomCenter.x + halfX &&
               bounds.min.z >= roomCenter.z - halfZ && bounds.max.z <= roomCenter.z + halfZ;
    }

    private static bool OverlapsPlacedWallBackedFurniture(Transform candidate, Transform parent, float tolerance)
    {
        if (!TryPhysicalBounds(candidate.gameObject, out Bounds candidateBounds)) return true;
        for (int index = 0; index < parent.childCount; index++)
        {
            Transform other = parent.GetChild(index);
            if (other == candidate || !HasDirectChildWithPrefix(other, "WALL_BACKED_PROP_OUTWARD_")) continue;
            if (!TryPhysicalBounds(other.gameObject, out Bounds otherBounds) ||
                BoundsOverlap(candidateBounds, otherBounds, tolerance)) return true;
        }
        return false;
    }

    private static bool BoundsOverlap(Bounds first, Bounds second, float tolerance)
    {
        float overlapX = Mathf.Min(first.max.x, second.max.x) - Mathf.Max(first.min.x, second.min.x);
        float overlapY = Mathf.Min(first.max.y, second.max.y) - Mathf.Max(first.min.y, second.min.y);
        float overlapZ = Mathf.Min(first.max.z, second.max.z) - Mathf.Max(first.min.z, second.min.z);
        return overlapX > tolerance && overlapY > tolerance && overlapZ > tolerance;
    }

    private static bool HasDirectChildWithPrefix(Transform parent, string prefix)
    {
        return parent != null && Enumerable.Range(0, parent.childCount).Select(parent.GetChild)
            .Any(child => child.name.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool TryPhysicalBounds(GameObject root, out Bounds bounds)
    {
        Collider[] colliders = root == null ? Array.Empty<Collider>() : root.GetComponentsInChildren<Collider>(true)
            .Where(collider => collider != null && collider.enabled && !collider.isTrigger).ToArray();
        if (colliders.Length == 0)
        {
            bounds = default;
            return false;
        }
        bounds = colliders[0].bounds;
        foreach (Collider collider in colliders.Skip(1)) bounds.Encapsulate(collider.bounds);
        return true;
    }

    private static IEnumerable<float> FurnitureTangentCandidates(int roomIndex, Vector2Int outward,
        IReadOnlyList<ConnectionPlan> connections, float desired, float maximum, int seed)
    {
        float[] portalOffsets = connections.Where(plan => ConnectionDirectionForRoom(plan, roomIndex) == outward)
            .SelectMany(PortalProbeOffsets).Distinct().ToArray();
        float[] candidates = { desired, -maximum, maximum, 0f, -maximum * .75f, maximum * .75f,
            -maximum * .5f, maximum * .5f, -maximum * .25f, maximum * .25f };
        return candidates.Distinct().OrderByDescending(candidate => portalOffsets.Length == 0
                ? (Mathf.Approximately(candidate, desired) ? float.MaxValue : 0f)
                : portalOffsets.Min(portal => Mathf.Abs(candidate - portal)))
            .ThenBy(candidate => (Mathf.RoundToInt((candidate + maximum) * 10f) + seed) % 17);
    }

    private static bool PropBlocksAnyPortalApproach(Transform prop, int roomIndex,
        IEnumerable<ConnectionPlan> connections, Vector3 roomCenter, Vector2 roomSize)
    {
        foreach (ConnectionPlan connection in connections)
        {
            Vector2Int outward = ConnectionDirectionForRoom(connection, roomIndex);
            if (outward == Vector2Int.zero) continue;
            Vector3 outward3 = new Vector3(outward.x, 0f, outward.y);
            Vector3 tangent = outward.x != 0 ? Vector3.forward : Vector3.right;
            float extent = outward.x != 0 ? roomSize.x * .5f : roomSize.y * .5f;
            foreach (float probeOffset in PortalProbeOffsets(connection))
            {
                Vector3 boundary = roomCenter + outward3 * extent + tangent * probeOffset;
                for (int sample = 0; sample < 5; sample++)
                {
                    Vector3 point = boundary - outward3 * (.35f + sample * .38f);
                    Collider[] hits = Physics.OverlapCapsule(point + Vector3.up * .35f,
                        point + Vector3.up * 1.75f, .42f, ~0, QueryTriggerInteraction.Ignore);
                    if (hits.Any(hit => hit != null && IsDescendantOf(hit.transform, prop))) return true;
                }
            }
        }
        return false;
    }

    private static Vector2Int ConnectionDirectionForRoom(ConnectionPlan plan, int roomIndex)
    {
        if (plan.RoomA == roomIndex) return plan.DirectionFromA;
        if (plan.RoomB == roomIndex) return -plan.DirectionFromA;
        return Vector2Int.zero;
    }

    private static IEnumerable<float> PortalProbeOffsets(ConnectionPlan plan)
    {
        // Runtime clearance is sampled from authored portal transforms. Gapped door connections own
        // a shifted DoorV2 socket at one endpoint and an aperture-centred OPEN_CONNECTION at the
        // other; direct shared walls own only the shifted socket. Test the union for every Door
        // endpoint so authoring is never less conservative than either exact runtime origin.
        yield return plan.PortalOffset;
        if (plan.Portal == PortalKind.Door)
            yield return plan.PortalOffset + DoorwayOpeningTangentOffset;
    }

    private static GameObject PlaceProp(Transform parent, string meshName, string name, Vector3 position, float yaw,
        float scale, bool ground = true)
    {
        if (UnsupportedStandaloneFurnitureMeshes.Contains(meshName ?? string.Empty))
            throw new InvalidDataException(meshName +
                " is an excluded standalone submesh/assembly and cannot be placed without a complete vanilla donor root.");
        GameObject instance = Instantiate(KillHouseNativePrefabBuilder.Load(meshName), parent, name);
        if (ground) AlignGrounded(instance, position, Quaternion.Euler(0f, yaw, 0f), Vector3.one * scale);
        else
        {
            instance.transform.position = position;
            instance.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            instance.transform.localScale = Vector3.one * scale;
        }
        return instance;
    }

    private static GameObject Instantiate(GameObject prefab, Transform parent, string name)
    {
        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        instance.name = name;
        instance.transform.SetParent(parent, true);
        return instance;
    }

    private static void OverrideRendererMaterials(GameObject instance, Material material)
    {
        foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = renderer.sharedMaterials;
            for (int index = 0; index < materials.Length; index++) materials[index] = material;
            renderer.sharedMaterials = materials;
        }
    }

    private static bool AllRendererSlotsUseMaterial(Transform root, string exactMaterialName)
    {
        if (root == null) return false;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        return renderers.Length > 0 && renderers.All(renderer => renderer.sharedMaterials.Length > 0 &&
            renderer.sharedMaterials.All(material => material != null &&
                string.Equals(material.name, exactMaterialName, StringComparison.Ordinal)));
    }

    private static int CountRendererSlotsUsingMaterial(Transform root, string exactMaterialName)
    {
        if (root == null) return 0;
        return root.GetComponentsInChildren<Renderer>(true).Sum(renderer => renderer.sharedMaterials.Count(material =>
            material != null && string.Equals(material.name, exactMaterialName, StringComparison.Ordinal)));
    }

    private static void AlignGrounded(GameObject instance, Vector3 desiredCenter, Quaternion rotation, Vector3 scale)
    {
        instance.transform.position = desiredCenter;
        instance.transform.rotation = rotation;
        instance.transform.localScale = scale;
        Bounds bounds = RendererBounds(instance);
        instance.transform.position += new Vector3(desiredCenter.x - bounds.center.x, desiredCenter.y - bounds.min.y,
            desiredCenter.z - bounds.center.z);
    }

    private static void AlignCeiling(GameObject instance, Vector3 desiredCenter, Quaternion rotation, Vector3 scale)
    {
        instance.transform.position = desiredCenter;
        instance.transform.rotation = rotation;
        instance.transform.localScale = scale;
        Bounds bounds = RendererBounds(instance);
        instance.transform.position += new Vector3(desiredCenter.x - bounds.center.x, desiredCenter.y - bounds.max.y,
            desiredCenter.z - bounds.center.z);
    }

    private static void FitHorizontal(GameObject instance, Vector3 center, float elevation, float width, float depth, bool ceiling)
    {
        MeshFilter filter = instance.GetComponentInChildren<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) throw new InvalidDataException(instance.name + " lacks a native floor mesh.");
        Bounds meshBounds = filter.sharedMesh.bounds;
        Vector3 scale = new Vector3(width / Mathf.Max(.01f, meshBounds.size.x), 1f, depth / Mathf.Max(.01f, meshBounds.size.z));
        if (ceiling) AlignCeiling(instance, new Vector3(center.x, elevation, center.z), Quaternion.Euler(180f, 0f, 0f), scale);
        else AlignGrounded(instance, new Vector3(center.x, elevation, center.z), Quaternion.identity, scale);
    }

    private static Bounds RendererBounds(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) throw new InvalidDataException(root.name + " has no native renderer.");
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
        return bounds;
    }

    private static GameObject Child(GameObject parent, string name)
    {
        GameObject child = new GameObject(name);
        child.transform.SetParent(parent.transform, false);
        return child;
    }

    private static bool WallBackedPropValid(Transform prop, Vector3[] roomCenters, Vector2[] roomSizes)
    {
        return string.IsNullOrEmpty(WallBackedPropFailure(prop, roomCenters, roomSizes));
    }

    private static string WallBackedPropFailure(Transform prop, Vector3[] roomCenters, Vector2[] roomSizes)
    {
        if (prop == null) return "provenance:prop-null";
        // Placement/facing belongs to the transported furniture root. Retained vanilla child
        // renderers (pillows, doors, lid/seat, drawer) are validated by the exact prefab hierarchy
        // contract and must never be mistaken for independently placed wall-backed families.
        MeshFilter filter = prop.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null ||
            !KillHouseNativePrefabBuilder.IsFurnitureRootMeshName(filter.sharedMesh.name))
            return "provenance:root-furniture-visual-missing";
        string meshName = filter.sharedMesh.name;
        if (UnsupportedStandaloneFurnitureMeshes.Contains(meshName))
            return "provenance:excluded-standalone-family=" + meshName;
        if (!WallBackedFurnitureContracts.TryGetValue(meshName, out WallBackedFurnitureContract contract))
            return "provenance:missing-family-contract=" + meshName;

        Transform[] outwardMarkers = Enumerable.Range(0, prop.childCount).Select(prop.GetChild)
            .Where(child => child.name.StartsWith("WALL_BACKED_PROP_OUTWARD_", StringComparison.Ordinal)).ToArray();
        if (outwardMarkers.Length != 1) return "placement:outward-marker-count=" + outwardMarkers.Length;
        Transform marker = outwardMarkers[0];
        Transform[] provenanceMarkers = Enumerable.Range(0, prop.childCount).Select(prop.GetChild)
            .Where(child => child.name.StartsWith("WALL_BACKED_PROP_PROVENANCE_", StringComparison.Ordinal)).ToArray();
        if (provenanceMarkers.Length != 1 || provenanceMarkers[0].name != contract.ProvenanceMarker)
            return "provenance:marker=" + (provenanceMarkers.Length == 1 ? provenanceMarkers[0].name :
                "count-" + provenanceMarkers.Length) + "/expected=" + contract.ProvenanceMarker;

        string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prop.gameObject);
        string expectedPrefabPath = KillHouseNativePrefabBuilder.PrefabPath(meshName);
        if (!string.Equals(prefabPath, expectedPrefabPath, StringComparison.Ordinal))
            return "provenance:prefab=" + prefabPath + "/expected=" + expectedPrefabPath;
        string meshPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
        if (!string.Equals(meshPath, contract.MeshAssetPath, StringComparison.Ordinal))
            return "provenance:mesh=" + meshPath + "/expected=" + contract.MeshAssetPath;
        MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
        Material[] materials = renderer == null ? Array.Empty<Material>() : renderer.sharedMaterials;
        if (renderer == null || materials.Length != contract.OrderedMaterials.Length)
            return "provenance:renderer-material-count=" + materials.Length +
                   "/expected=" + contract.OrderedMaterials.Length;
        for (int index = 0; index < materials.Length; index++)
        {
            string actual = materials[index] == null ? "<null>" : materials[index].name;
            if (!string.Equals(actual, contract.OrderedMaterials[index], StringComparison.Ordinal))
                return "provenance:slot-" + index + "=" + actual + "/expected=" +
                       contract.OrderedMaterials[index];
        }
        if (!KillHouseNativePrefabBuilder.HasExactFurniturePrefabContract(prop.gameObject,
                out string prefabFailure, requireAssetDatabaseIdentity: true, requireUnitRootScale: false))
            return "provenance:transport-closure=" + prefabFailure;

        int roomIndex = -1;
        Transform cursor = prop.parent;
        while (cursor != null)
        {
            if (cursor.name.StartsWith("DRESSING_", StringComparison.Ordinal) && cursor.name.Length >= 11 &&
                int.TryParse(cursor.name.Substring(9, 2), out roomIndex)) break;
            if (cursor.name == "FIXED_SAFE_ROOM_KH_SAFE_ROOM_V1") { roomIndex = 0; break; }
            cursor = cursor.parent;
        }
        if (roomIndex < 0 || roomIndex >= roomCenters.Length) return "placement:room-owner";
        string suffix = marker.name.Substring("WALL_BACKED_PROP_OUTWARD_".Length);
        Vector3 outward = suffix == "E" ? Vector3.right : suffix == "W" ? Vector3.left :
            suffix == "N" ? Vector3.forward : suffix == "S" ? Vector3.back : Vector3.zero;
        if (outward == Vector3.zero) return "placement:outward-marker-direction=" + suffix;
        Vector3 worldInterior = prop.TransformDirection(contract.LocalInteriorAxis).normalized;
        Vector3 worldWall = prop.TransformDirection(contract.LocalWallAxis).normalized;
        if (Vector3.Dot(worldInterior, -outward) < .999f)
            return "facing:interior-axis=" + Vector3.Dot(worldInterior, -outward).ToString("F4");
        if (Vector3.Dot(worldWall, outward) < .999f)
            return "facing:wall-axis=" + Vector3.Dot(worldWall, outward).ToString("F4");
        if (Vector3.Dot(prop.up.normalized, Vector3.up) < .999f)
            return "facing:up-axis=" + Vector3.Dot(prop.up.normalized, Vector3.up).ToString("F4");
        Bounds bounds = RendererBounds(prop.gameObject);
        if (!BoundsInsideRoom(bounds, roomCenters[roomIndex], roomSizes[roomIndex], .02f))
            return "clearance:outside-owned-room";
        if (!TryPhysicalBounds(prop.gameObject, out Bounds physicalBounds))
            return "provenance:physical-collider-missing";
        if (!BoundsInsideRoom(physicalBounds, roomCenters[roomIndex], roomSizes[roomIndex], .02f))
            return "clearance:physical-collider-outside-owned-room";
        float extent = Mathf.Abs(outward.x) > .5f ? roomSizes[roomIndex].x * .5f : roomSizes[roomIndex].y * .5f;
        float back = Vector3.Dot(bounds.center - roomCenters[roomIndex], outward) +
            Mathf.Abs(outward.x) * bounds.extents.x + Mathf.Abs(outward.z) * bounds.extents.z;
        if (Mathf.Abs((extent - .12f) - back) > .03f)
            return "placement:wall-standoff=" + (extent - back).ToString("F3") + "/expected=0.120";
        return string.Empty;
    }

    private static string[] WallBackedOverlapDetails(IEnumerable<Transform> wallBackedProps)
    {
        Transform[] props = wallBackedProps.Where(item => item != null).ToArray();
        var overlaps = new List<string>();
        for (int first = 0; first < props.Length; first++)
        {
            if (!TryPhysicalBounds(props[first].gameObject, out Bounds firstBounds))
            {
                overlaps.Add(props[first].name + "=physical-collider-missing");
                continue;
            }
            for (int second = first + 1; second < props.Length; second++)
            {
                if (!TryPhysicalBounds(props[second].gameObject, out Bounds secondBounds))
                {
                    overlaps.Add(props[second].name + "=physical-collider-missing");
                    continue;
                }
                if (!BoundsOverlap(firstBounds, secondBounds, .02f)) continue;
                overlaps.Add(props[first].name + "<->" + props[second].name);
            }
        }
        return overlaps.ToArray();
    }

    private static string CenterRoomPropFailure(Transform prop, Layout layout, Vector3[] roomCenters,
        Vector2[] roomSizes, LayoutMotif motif, IReadOnlyList<ConnectionPlan> connections,
        IEnumerable<Transform> tacticalMarkers)
    {
        if (prop == null) return "provenance:prop-null";
        Transform[] roleMarkers = Enumerable.Range(0, prop.childCount).Select(prop.GetChild)
            .Where(child => child.name.StartsWith("CENTER_ROOM_PROP_ROLE_", StringComparison.Ordinal)).ToArray();
        if (roleMarkers.Length != 1) return "provenance:role-marker-count=" + roleMarkers.Length;
        string role = roleMarkers[0].name.Substring("CENTER_ROOM_PROP_ROLE_".Length);
        if (!CenterRoomFurnitureContracts.TryGetValue(role, out CenterRoomFurnitureContract contract))
            return "provenance:unknown-role=" + role;

        if (!TryGetDressingRoomIndex(prop.parent, out int roomIndex) || roomIndex <= 0 ||
            roomIndex >= roomCenters.Length) return "placement:room-owner";
        Transform[] roomMarkers = Enumerable.Range(0, prop.childCount).Select(prop.GetChild)
            .Where(child => child.name.StartsWith("CENTER_ROOM_PROP_ROOM_", StringComparison.Ordinal)).ToArray();
        if (roomMarkers.Length != 1 || roomMarkers[0].name != "CENTER_ROOM_PROP_ROOM_" + roomIndex.ToString("00"))
            return "provenance:room-marker";
        if (role == "TABLE" && !CenterTableEligible(layout.Rooms[roomIndex], roomSizes[roomIndex], motif))
            return "placement:table-in-ineligible-room=" + layout.Rooms[roomIndex];
        if (role == "SOFA" && !CenterSofaRoomEligible(layout.Rooms[roomIndex], roomSizes[roomIndex],
                layout.Rooms[roomIndex] == RoomType.Blank))
            return "placement:sofa-in-ineligible-room=" + layout.Rooms[roomIndex];

        Transform[] provenance = Enumerable.Range(0, prop.childCount).Select(prop.GetChild)
            .Where(child => child.name.StartsWith("CENTER_ROOM_PROP_PROVENANCE_", StringComparison.Ordinal)).ToArray();
        if (provenance.Length != 1 || provenance[0].name != contract.ProvenanceMarker)
            return "provenance:marker=" + (provenance.Length == 1 ? provenance[0].name :
                "count-" + provenance.Length) + "/expected=" + contract.ProvenanceMarker;
        string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prop.gameObject);
        string expectedPrefabPath = KillHouseNativePrefabBuilder.PrefabPath(contract.MeshName);
        if (!string.Equals(prefabPath, expectedPrefabPath, StringComparison.Ordinal))
            return "provenance:prefab=" + prefabPath + "/expected=" + expectedPrefabPath;
        if (!KillHouseNativePrefabBuilder.HasExactFurniturePrefabContract(prop.gameObject, out string prefabFailure))
            return "provenance:transport-closure=" + prefabFailure;
        if (CountDirectChildren(prop, "CENTER_ROOM_PROP_DETERMINISTIC_CANDIDATE_") != 1)
            return "provenance:deterministic-candidate-marker";
        if (CountDirectChildren(prop, "CENTER_ROOM_PROP_CLEARANCE_VALID") != 1)
            return "provenance:clearance-marker";
        if (CountDirectChildren(prop, "CENTER_ROOM_PROP_CIRCULATION_VALID") != 1)
            return "provenance:circulation-marker";

        string facingFailure = CenterRoomPropFacingFailure(prop, contract);
        if (!string.IsNullOrEmpty(facingFailure)) return "facing:" + facingFailure;
        string placementFailure = CenterRoomPropPlacementFailure(prop, contract, prop.parent,
            roomCenters[roomIndex], roomSizes[roomIndex], roomIndex, connections);
        if (!string.IsNullOrEmpty(placementFailure)) return "clearance:" + placementFailure;
        string tacticalFailure = CenterRoomPropTacticalCapsuleFailure(prop, tacticalMarkers);
        if (!string.IsNullOrEmpty(tacticalFailure)) return "tactical:" + tacticalFailure;
        return string.Empty;
    }

    private static int CountDirectChildren(Transform parent, string prefix)
    {
        return parent == null ? 0 : Enumerable.Range(0, parent.childCount).Select(parent.GetChild)
            .Count(child => child.name.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool TryGetDressingRoomIndex(Transform dressing, out int roomIndex)
    {
        roomIndex = -1;
        return dressing != null && dressing.name.StartsWith("DRESSING_", StringComparison.Ordinal) &&
               dressing.name.Length >= 11 && int.TryParse(dressing.name.Substring(9, 2), out roomIndex);
    }

    private static string CenterRoomPropFacingFailure(Transform prop, CenterRoomFurnitureContract contract)
    {
        Transform[] markers = Enumerable.Range(0, prop.childCount).Select(prop.GetChild)
            .Where(child => child.name.StartsWith("CENTER_ROOM_PROP_FACING_", StringComparison.Ordinal)).ToArray();
        if (markers.Length != 1) return "marker-count=" + markers.Length;
        Vector3 worldFacing = prop.TransformDirection(contract.LocalFacingAxis);
        worldFacing.y = 0f;
        if (worldFacing.sqrMagnitude < .999f || Vector3.Dot(prop.up.normalized, Vector3.up) < .999f)
            return "invalid-axis";
        worldFacing.Normalize();
        string marker = markers[0].name;
        if (contract.BidirectionalFacing)
        {
            Vector3 expected = marker == "CENTER_ROOM_PROP_FACING_LONG_AXIS_X" ? Vector3.right :
                marker == "CENTER_ROOM_PROP_FACING_LONG_AXIS_Z" ? Vector3.forward : Vector3.zero;
            return expected == Vector3.zero || Mathf.Abs(Vector3.Dot(worldFacing, expected)) < .999f
                ? marker + "-axis=" + worldFacing.ToString("F3")
                : string.Empty;
        }
        Vector3 expectedFront = marker == "CENTER_ROOM_PROP_FACING_FRONT_E" ? Vector3.right :
            marker == "CENTER_ROOM_PROP_FACING_FRONT_W" ? Vector3.left :
            marker == "CENTER_ROOM_PROP_FACING_FRONT_N" ? Vector3.forward :
            marker == "CENTER_ROOM_PROP_FACING_FRONT_S" ? Vector3.back : Vector3.zero;
        return expectedFront == Vector3.zero || Vector3.Dot(worldFacing, expectedFront) < .999f
            ? marker + "-axis=" + worldFacing.ToString("F3")
            : string.Empty;
    }

    private static string CenterRoomPropTacticalCapsuleFailure(Transform prop,
        IEnumerable<Transform> tacticalMarkers)
    {
        foreach (Transform marker in tacticalMarkers ?? Array.Empty<Transform>())
        {
            Vector3 bottom = marker.position + Vector3.up * .42f;
            Vector3 top = marker.position + Vector3.up * 1.58f;
            Collider[] hits = Physics.OverlapCapsule(bottom, top, .3f, ~0, QueryTriggerInteraction.Ignore);
            if (hits.Any(hit => hit != null && IsDescendantOf(hit.transform, prop))) return marker.name;
        }
        return string.Empty;
    }

    private static string[] CenterRoomPropOutcomeFailures(Transform[] centerProps, Transform[] skipMarkers,
        Layout layout, Vector2[] roomSizes, LayoutMotif motif)
    {
        var failures = new List<string>();
        for (int roomIndex = 1; roomIndex < layout.Rooms.Length; roomIndex++)
        {
            if (!CenterTableEligible(layout.Rooms[roomIndex], roomSizes[roomIndex], motif)) continue;
            int placed = centerProps.Count(prop => CenterRoomPropHasRole(prop, "TABLE") &&
                TryGetDressingRoomIndex(prop.parent, out int owner) && owner == roomIndex);
            int skipped = skipMarkers.Count(marker => marker.name ==
                "CENTER_ROOM_PROP_SKIP_TABLE_ROOM_" + roomIndex.ToString("00"));
            if (placed + skipped != 1)
                failures.Add("table-room-" + roomIndex.ToString("00") + "-outcomes=" + placed + "+" + skipped);
        }

        int[] livingRooms = Enumerable.Range(1, layout.Rooms.Length - 1)
            .Where(index => layout.Rooms[index] == RoomType.Living &&
                            CenterSofaRoomEligible(layout.Rooms[index], roomSizes[index], false)).ToArray();
        foreach (int roomIndex in livingRooms)
        {
            int placed = centerProps.Count(prop => CenterRoomPropHasRole(prop, "SOFA") &&
                TryGetDressingRoomIndex(prop.parent, out int owner) && owner == roomIndex);
            int skipped = skipMarkers.Count(marker => marker.name ==
                "CENTER_ROOM_PROP_SKIP_SOFA_ROOM_" + roomIndex.ToString("00"));
            if (placed + skipped != 1)
                failures.Add("sofa-living-room-" + roomIndex.ToString("00") + "-outcomes=" + placed + "+" + skipped);
        }
        foreach (IGrouping<string, Transform> duplicate in centerProps.GroupBy(prop =>
                 {
                     TryGetDressingRoomIndex(prop.parent, out int owner);
                     string role = CenterRoomPropHasRole(prop, "TABLE") ? "TABLE" :
                         CenterRoomPropHasRole(prop, "SOFA") ? "SOFA" : "UNKNOWN";
                     return role + "@" + owner.ToString("00");
                 }).Where(group => group.Count() > 1))
            failures.Add("duplicate-placement-" + duplicate.Key + "=" + duplicate.Count());
        bool livingSofaPlaced = centerProps.Any(prop => CenterRoomPropHasRole(prop, "SOFA") &&
            TryGetDressingRoomIndex(prop.parent, out int owner) && layout.Rooms[owner] == RoomType.Living);
        bool fallbackSofaPlaced = centerProps.Any(prop => CenterRoomPropHasRole(prop, "SOFA") &&
            TryGetDressingRoomIndex(prop.parent, out int owner) && layout.Rooms[owner] == RoomType.Blank);
        if (livingSofaPlaced && fallbackSofaPlaced) failures.Add("open-room-sofa-used-despite-living-placement");
        foreach (Transform skip in skipMarkers)
        {
            if (!TryGetDressingRoomIndex(skip.parent, out int owner) ||
                (!skip.name.EndsWith("ROOM_" + owner.ToString("00"), StringComparison.Ordinal)))
                failures.Add(skip.name + "-owner-mismatch");
            int reasonCount = CountDirectChildren(skip, "CENTER_ROOM_PROP_SKIP_REASON_");
            if (reasonCount != 1) failures.Add(skip.name + "-reason-count=" + reasonCount);
        }
        return failures.ToArray();
    }

    private static bool CenterRoomPropHasRole(Transform prop, string role)
    {
        return CountDirectChildren(prop, "CENTER_ROOM_PROP_ROLE_" + role) == 1;
    }

    private static string[] CenterRoomPropOverlapDetails(IEnumerable<Transform> centerProps)
    {
        var details = new List<string>();
        foreach (Transform prop in centerProps.Where(prop => prop != null))
        {
            string overlap = CenterRoomSiblingOverlapFailure(prop, prop.parent);
            if (!string.IsNullOrEmpty(overlap)) details.Add(prop.name + "<->" + overlap);
        }
        return details.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] BlockedPortalApproachDetails(Transform propsRoot, Vector3[] roomCenters, Vector2[] roomSizes,
        IEnumerable<ConnectionPlan> connections)
    {
        if (propsRoot == null) return new[] { "props-root-missing" };
        var blocked = new List<string>();
        foreach (ConnectionPlan connection in connections)
        {
            string first = PortalApproachBlocker(propsRoot, connection.RoomA, connection.DirectionFromA,
                PortalProbeOffsets(connection), roomCenters, roomSizes);
            if (!string.IsNullOrEmpty(first)) blocked.Add(connection.Key + "/room" + connection.RoomA.ToString("00") +
                                                          "=" + first);
            string second = PortalApproachBlocker(propsRoot, connection.RoomB, -connection.DirectionFromA,
                PortalProbeOffsets(connection), roomCenters, roomSizes);
            if (!string.IsNullOrEmpty(second)) blocked.Add(connection.Key + "/room" + connection.RoomB.ToString("00") +
                                                           "=" + second);
        }
        return blocked.ToArray();
    }

    private static string PortalApproachBlocker(Transform propsRoot, int roomIndex, Vector2Int outward,
        IEnumerable<float> portalOffsets, Vector3[] roomCenters, Vector2[] roomSizes)
    {
        Vector3 outward3 = new Vector3(outward.x, 0f, outward.y);
        Vector3 tangent = outward.x != 0 ? Vector3.forward : Vector3.right;
        float extent = outward.x != 0 ? roomSizes[roomIndex].x * .5f : roomSizes[roomIndex].y * .5f;
        foreach (float portalOffset in portalOffsets)
        {
            Vector3 boundary = roomCenters[roomIndex] + outward3 * extent + tangent * portalOffset;
            for (int sample = 0; sample < 5; sample++)
            {
                Vector3 point = boundary - outward3 * (.35f + sample * .38f);
                Collider[] hits = Physics.OverlapCapsule(point + Vector3.up * .35f,
                    point + Vector3.up * 1.75f, .42f, ~0, QueryTriggerInteraction.Ignore);
                Collider blocker = hits.FirstOrDefault(hit => hit != null && IsDescendantOf(hit.transform, propsRoot));
                if (blocker != null)
                {
                    Transform owner = blocker.transform;
                    while (owner.parent != null && owner.parent != propsRoot) owner = owner.parent;
                    return owner.name + "/" + blocker.name + "@sample" + sample;
                }
            }
        }
        return string.Empty;
    }

    private static bool IsDescendantOf(Transform value, Transform ancestor)
    {
        Transform cursor = value;
        while (cursor != null)
        {
            if (cursor == ancestor) return true;
            cursor = cursor.parent;
        }
        return false;
    }

    private static bool WarehouseApronPerimeterIsSealed(Transform[] transforms, Layout layout,
        Vector2[] roomSizes, Vector3[] roomCenters, IReadOnlyList<ConnectionPlan> connections,
        out string failure)
    {
        failure = string.Empty;
        var cellLookup = new HashSet<Vector2Int>(layout.Cells);
        Vector2Int[] directions = { Vector2Int.right, Vector2Int.up, Vector2Int.left, Vector2Int.down };
        for (int roomIndex = 0; roomIndex < layout.Cells.Length; roomIndex++)
        {
            foreach (Vector2Int direction in directions)
            {
                if (cellLookup.Contains(layout.Cells[roomIndex] + direction)) continue;
                float sideLength = direction.x != 0 ? roomSizes[roomIndex].y : roomSizes[roomIndex].x;
                int slots = Mathf.RoundToInt(sideLength / WallModuleWidth);
                string side = direction == Vector2Int.right ? "E" : direction == Vector2Int.left ? "W" :
                              direction == Vector2Int.up ? "N" : "S";
                for (int slot = 0; slot < slots; slot++)
                {
                    string wallName = "NATIVE_RoomWall_" + roomIndex.ToString("00") + "_" + side + "_" +
                                      slot.ToString("00");
                    Transform[] matches = transforms.Where(item => item.name == wallName).ToArray();
                    if (matches.Length == 1 && SolidBoundaryColliderValid(matches[0])) continue;
                    failure = "exterior-wall:" + wallName + ":count=" + matches.Length;
                    return false;
                }
            }
        }

        foreach (ConnectionPlan connection in connections)
        {
            float gap = ConnectionGap(roomCenters[connection.RoomA], roomSizes[connection.RoomA],
                roomCenters[connection.RoomB], roomSizes[connection.RoomB], connection.DirectionFromA);
            if (gap < .1f) continue;
            int segments = Mathf.RoundToInt(gap / WallModuleWidth);
            for (int segment = 0; segment < segments; segment++)
            {
                foreach (string side in new[] { "L", "R" })
                {
                    string wallName = "NATIVE_ConnectorWall_" + connection.Key + "_" + side +
                                      segment.ToString("00");
                    Transform[] matches = transforms.Where(item => item.name == wallName).ToArray();
                    if (matches.Length == 1 && SolidBoundaryColliderValid(matches[0])) continue;
                    failure = "connector-wall:" + wallName + ":count=" + matches.Length;
                    return false;
                }
            }
        }
        return true;
    }

    private static bool SolidBoundaryColliderValid(Transform boundary)
    {
        if (boundary == null) return false;
        Collider[] colliders = boundary.GetComponentsInChildren<Collider>(true);
        return colliders.Length > 0 && colliders.All(collider =>
            collider != null && collider.enabled && !collider.isTrigger);
    }

    private static JObject Validate(GameObject root, Variant variant, int variantIndex, Layout layout, Vector2[] roomSizes,
        Vector3[] roomCenters, ConnectionPlan[] connections)
    {
        Vector2Int[] cells = layout.Cells;
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        string[] primitiveNames = { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };
        int primitiveMeshes = filters.Count(filter => filter.sharedMesh != null && primitiveNames.Contains(filter.sharedMesh.name));
        int doorSockets = transforms.Count(item => item.name.StartsWith("DOORV2_SOCKET_", StringComparison.Ordinal));
        int doorShells = transforms.Count(item => item.name == "NATIVE_DOORV2_SHELL");
        int doorAudioBanks = transforms.Count(item => item.name == "NATIVE_DOORV2_AUDIO_BANK");
        int misalignedDoorSockets = transforms
            .Where(item => item.name.StartsWith("DOORV2_SOCKET_", StringComparison.Ordinal))
            .Count(socket => !DoorSocketMatchesNativeOpening(socket));
        int staticDoorLeaves = transforms.Count(item => item.name.StartsWith("NATIVE_OpenDoorLeaf_", StringComparison.Ordinal));
        int openings = transforms.Count(item => item.name.StartsWith("OPEN_CONNECTION_", StringComparison.Ordinal));
        int windows = transforms.Count(item => item.name.StartsWith("NATIVE_SightWindow_", StringComparison.Ordinal));
        Transform[] enemyMarkerTransforms = transforms
            .Where(item => item.name.StartsWith("PVE_EnemySpawn_", StringComparison.Ordinal)).ToArray();
        int enemyMarkers = enemyMarkerTransforms.Length;
        int tacticalPositions = transforms.Count(item => item.name.StartsWith("TACTICAL_POSITION_", StringComparison.Ordinal));
        int invalidTacticalMarkers = enemyMarkerTransforms.Count(marker => !TacticalMarkerValid(marker));
        int invalidPveEnemySpawnPairClearance = CountPveEnemySpawnClearanceFailures(enemyMarkerTransforms,
            out float minimumPveEnemySpawnSeparation);
        string tacticalFailureSummary = string.Join(",", enemyMarkerTransforms
            .Select(TacticalMarkerFailure).Where(failure => !string.IsNullOrEmpty(failure))
            .GroupBy(failure => failure).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key + "=" + group.Count()));
        int tacticalRoleTypes = transforms.Where(item => item.name.StartsWith("TACTICAL_ROLE_", StringComparison.Ordinal))
            .Select(item => item.name).Distinct().Count();
        Transform[] pvePlayerMarkerTransforms = transforms.Where(item =>
            item.name.StartsWith("PVE_PlayerSpawn_", StringComparison.Ordinal)).ToArray();
        int playerMarkers = pvePlayerMarkerTransforms.Length;
        Transform[] pvpTeam1Markers = transforms.Where(item =>
            item.name.StartsWith("PVP_Team1Spawn_", StringComparison.Ordinal)).OrderBy(item => item.name,
            StringComparer.Ordinal).ToArray();
        Transform[] pvpTeam2Markers = transforms.Where(item =>
            item.name.StartsWith("PVP_Team2Spawn_", StringComparison.Ordinal)).OrderBy(item => item.name,
            StringComparer.Ordinal).ToArray();
        int pveSpawnSetMarkers = transforms.Count(item => item.name == PveSpawnSetMarker);
        int pvpSpawnSetMarkers = transforms.Count(item => item.name == PvpSpawnSetMarker);
        int spawnSetMarkers = transforms.Count(item => item.name.StartsWith("SPAWN_SET_", StringComparison.Ordinal));
        Vector3 pvpTeam1Centroid = pvpTeam1Markers.Length == 0 ? Vector3.zero :
            AveragePosition(pvpTeam1Markers.Select(item => item.position));
        Vector3 pvpTeam2Centroid = pvpTeam2Markers.Length == 0 ? Vector3.zero :
            AveragePosition(pvpTeam2Markers.Select(item => item.position));
        string[] pvpMarkerFailures = pvpTeam1Markers.Select(marker => marker.name + "=" +
                PvpSpawnMarkerFailure(marker, 1, pvpTeam2Centroid, root.transform, roomCenters, roomSizes,
                    connections))
            .Concat(pvpTeam2Markers.Select(marker => marker.name + "=" +
                PvpSpawnMarkerFailure(marker, 2, pvpTeam1Centroid, root.transform, roomCenters, roomSizes,
                    connections)))
            .Where(detail => !detail.EndsWith("=", StringComparison.Ordinal)).ToArray();
        int[] pvpTeam1Rooms = pvpTeam1Markers.Select(PvpSpawnRoomIndex).Distinct().OrderBy(value => value).ToArray();
        int[] pvpTeam2Rooms = pvpTeam2Markers.Select(PvpSpawnRoomIndex).Distinct().OrderBy(value => value).ToArray();
        int[,] pvpGraphDistances = BuildAllPairsGraphDistances(layout.Cells.Length, connections);
        int pvpBaseGraphDistance = pvpTeam1Rooms.Length == 0 || pvpTeam2Rooms.Length == 0 ? -1 :
            pvpTeam1Rooms.Min(first => pvpTeam2Rooms.Min(second => first >= 0 && second >= 0 &&
                first < layout.Cells.Length && second < layout.Cells.Length ? pvpGraphDistances[first, second] : -1));
        float pvpMinimumOpposingSpawnDistance = pvpTeam1Markers.Length == 0 || pvpTeam2Markers.Length == 0 ? -1f :
            MinimumHorizontalDistance(pvpTeam1Markers.Select(item => item.position),
                pvpTeam2Markers.Select(item => item.position));
        int pvpDirectOpposingLineOfSightPairs = pvpTeam1Markers.Length == 0 || pvpTeam2Markers.Length == 0 ? -1 :
            CountDirectPvpLineOfSightPairs(pvpTeam1Markers.Select(item => item.position),
                pvpTeam2Markers.Select(item => item.position));
        int invalidPvpSpawnPairClearance = CountPvpSameTeamClearanceFailures(pvpTeam1Markers) +
                                           CountPvpSameTeamClearanceFailures(pvpTeam2Markers);
        bool pvpTeam1RoomDistributionValid = pvpTeam1Rooms.Length == PvpRoomsPerSector &&
            pvpTeam1Rooms.All(room => pvpTeam1Markers.Count(marker => PvpSpawnRoomIndex(marker) == room) == 2);
        bool pvpTeam2RoomDistributionValid = pvpTeam2Rooms.Length == PvpRoomsPerSector &&
            pvpTeam2Rooms.All(room => pvpTeam2Markers.Count(marker => PvpSpawnRoomIndex(marker) == room) == 2);
        bool pvpSectorRoomsDisjoint = !pvpTeam1Rooms.Intersect(pvpTeam2Rooms).Any();
        bool pvpTeam1SectorConnected = PvpSectorRoomsConnected(pvpTeam1Rooms, connections);
        bool pvpTeam2SectorConnected = PvpSectorRoomsConnected(pvpTeam2Rooms, connections);
        string[] expectedPvpTeam1Names = Enumerable.Range(1, PvpSpawnsPerTeam)
            .Select(index => "PVP_Team1Spawn_" + index.ToString("00")).ToArray();
        string[] expectedPvpTeam2Names = Enumerable.Range(1, PvpSpawnsPerTeam)
            .Select(index => "PVP_Team2Spawn_" + index.ToString("00")).ToArray();
        bool pvpMarkerNamesValid = pvpTeam1Markers.Select(item => item.name).SequenceEqual(expectedPvpTeam1Names) &&
                                   pvpTeam2Markers.Select(item => item.name).SequenceEqual(expectedPvpTeam2Names);
        var pvpStructuralFailures = new List<string>();
        if (pvpTeam1Markers.Length != PvpSpawnsPerTeam) pvpStructuralFailures.Add("team1-count=" + pvpTeam1Markers.Length);
        if (pvpTeam2Markers.Length != PvpSpawnsPerTeam) pvpStructuralFailures.Add("team2-count=" + pvpTeam2Markers.Length);
        if (!pvpMarkerNamesValid) pvpStructuralFailures.Add("marker-name-sequence");
        if (!pvpTeam1RoomDistributionValid) pvpStructuralFailures.Add("team1-room-distribution");
        if (!pvpTeam2RoomDistributionValid) pvpStructuralFailures.Add("team2-room-distribution");
        if (!pvpSectorRoomsDisjoint) pvpStructuralFailures.Add("sector-room-overlap");
        if (!pvpTeam1SectorConnected) pvpStructuralFailures.Add("team1-sector-disconnected");
        if (!pvpTeam2SectorConnected) pvpStructuralFailures.Add("team2-sector-disconnected");
        if (pvpBaseGraphDistance < PvpMinimumSectorGraphDistance)
            pvpStructuralFailures.Add("base-graph-distance=" + pvpBaseGraphDistance);
        if (pvpMinimumOpposingSpawnDistance < PvpMinimumOpposingDistance)
            pvpStructuralFailures.Add("opposing-distance=" + pvpMinimumOpposingSpawnDistance.ToString("F2"));
        if (pvpDirectOpposingLineOfSightPairs != 0)
            pvpStructuralFailures.Add("direct-los-pairs=" + pvpDirectOpposingLineOfSightPairs);
        if (invalidPvpSpawnPairClearance != 0)
            pvpStructuralFailures.Add("same-team-clearance-pairs=" + invalidPvpSpawnPairClearance);
        if (transforms.Count(item => item.name == "PVP_TEAM1_SPAWN_SECTOR") != 1 ||
            transforms.Count(item => item.name == "PVP_TEAM2_SPAWN_SECTOR") != 1)
            pvpStructuralFailures.Add("sector-root-count");
        int invalidPvpSpawnCapsules = pvpMarkerFailures.Count(detail =>
            detail.IndexOf("=capsule-", StringComparison.Ordinal) >= 0);
        int invalidPvpSpawnPortalApproaches = pvpMarkerFailures.Count(detail =>
            detail.IndexOf("=portal-approach:", StringComparison.Ordinal) >= 0);
        int invalidPvpSpawnDoorApproaches = pvpMarkerFailures.Count(detail =>
            detail.IndexOf("=door-approach:", StringComparison.Ordinal) >= 0);
        int invalidPvpSpawnFurnitureClearance = pvpMarkerFailures.Count(detail =>
            detail.IndexOf("=furniture-clearance:", StringComparison.Ordinal) >= 0);
        int invalidPvpSpawnRoomOwnership = pvpMarkerFailures.Count(detail =>
            detail.IndexOf("=room-ownership-", StringComparison.Ordinal) >= 0);
        int invalidPvpSpawnFacing = pvpMarkerFailures.Count(detail =>
            detail.IndexOf("=facing-", StringComparison.Ordinal) >= 0);
        string[] pvpSpawnFailureDetails = pvpMarkerFailures.Concat(pvpStructuralFailures).ToArray();
        int invalidPvpSpawnMarkers = pvpSpawnFailureDetails.Length;
        int exfils = transforms.Count(item => item.name.StartsWith("PVE_ExfilZone_", StringComparison.Ordinal));
        int safeRoomMarkers = transforms.Count(item => item.name == "FIXED_SAFE_ROOM_KH_SAFE_ROOM_V1");
        int fixedSafeDoorInterfaces = transforms.Count(item => item.name == "FIXED_SAFE_ROOM_DOOR_INTERFACE");
        int hallwaySideDoors = transforms.Count(item => item.name == "HALLWAY_SIDE_DOOR");
        int spatialMotifMarkers = transforms.Count(item => item.name == "SPATIAL_MOTIF_" + variant.Motif.ToString().ToUpperInvariant());
        int interiorSplitWalls = transforms.Count(item => item.name.StartsWith("NATIVE_InteriorSplitWall_", StringComparison.Ordinal));
        int nativeLowDividers = transforms.Count(item => item.name.StartsWith("NATIVE_LowDivider_", StringComparison.Ordinal));
        int nativeOfficePartitions = transforms.Count(item => item.name.StartsWith("NATIVE_OfficePartition_", StringComparison.Ordinal));
        int spatialFeatureCount = interiorSplitWalls + nativeLowDividers + nativeOfficePartitions;
        int roomCeilings = transforms.Count(item => item.name == "NATIVE_Ceiling" ||
            item.name.StartsWith("NATIVE_ConnectorCeiling_", StringComparison.Ordinal));
        int warehouseShellGroups = transforms.Count(item => item.name == "NATIVE_WarehousePvpCompleteShell");
        string[] warehousePartNames =
        {
            "NATIVE_WarehouseBase", "NATIVE_WarehouseOverHeadSupport", "NATIVE_WarehouseRoof", "NATIVE_WarehouseSupport2"
        };
        Transform[] warehouseParts = transforms.Where(item => warehousePartNames.Contains(item.name)).ToArray();
        int warehouseFinishMarkers = transforms.Count(item =>
            item.name == "WAREHOUSE_PREFAB_PVP_WOODS_EXACT_FOUR_PART");
        const string warehouseMaterial = "MAT_NATIVE_RM_Steel_smooth";
        Transform warehouseShell = root.transform.Find("05_HIGH_WAREHOUSE_SHELL");
        int invalidWarehousePartFinish = warehouseParts.Count(part =>
            !AllRendererSlotsUseMaterial(part, warehouseMaterial));
        int warehouseSteelSlots = warehouseShell == null ? 0 :
            CountRendererSlotsUsingMaterial(warehouseShell, warehouseMaterial);
        int warehouseMeshColliders = warehouseParts.Sum(part => part.GetComponentsInChildren<MeshCollider>(true).Length);
        Transform[] warehouseGrounds = transforms.Where(item => item.name == "NATIVE_WarehouseGroundApron").ToArray();
        Transform warehouseGround = warehouseGrounds.FirstOrDefault();
        MeshFilter warehouseGroundFilter = warehouseGround == null ? null : warehouseGround.GetComponent<MeshFilter>();
        MeshRenderer warehouseGroundRenderer = warehouseGround == null ? null : warehouseGround.GetComponent<MeshRenderer>();
        MeshCollider warehouseGroundCollider = warehouseGround == null ? null : warehouseGround.GetComponent<MeshCollider>();
        int warehouseGroundMarkers = transforms.Count(item =>
            item.name == "WAREHOUSE_GROUND_LEVEL11_FLOOR_MESH152_MATERIAL26");
        int warehouseGroundProvenanceMarkers = transforms.Count(item =>
            item.name == "WAREHOUSE_GROUND_PROVENANCE_APPEARANCE_GO104_GEOMETRY_GO9601");
        int warehouseGroundNavigationPolicyMarkers = transforms.Count(item =>
            item.name == "WAREHOUSE_APRON_NAV_EXCLUDED_ENCLOSED_PERIMETER");
        string warehouseGroundFailure = "missing-instance";
        bool warehouseGroundPrefabValid = warehouseGround != null &&
            KillHouseNativePrefabBuilder.HasExactWarehouseFloorPrefabContract(
                warehouseGround.gameObject, true, out warehouseGroundFailure);

        float expectedGroundFootprintMinimumX = Enumerable.Range(0, roomCenters.Length)
            .Min(index => roomCenters[index].x - roomSizes[index].x * .5f);
        float expectedGroundFootprintMaximumX = Enumerable.Range(0, roomCenters.Length)
            .Max(index => roomCenters[index].x + roomSizes[index].x * .5f);
        float expectedGroundFootprintMinimumZ = Enumerable.Range(0, roomCenters.Length)
            .Min(index => roomCenters[index].z - roomSizes[index].y * .5f);
        float expectedGroundFootprintMaximumZ = Enumerable.Range(0, roomCenters.Length)
            .Max(index => roomCenters[index].z + roomSizes[index].y * .5f);
        float expectedGroundWidth = Mathf.Ceil((expectedGroundFootprintMaximumX - expectedGroundFootprintMinimumX +
                                                WarehouseMargin * 2f) / WallModuleWidth) * WallModuleWidth;
        float expectedGroundDepth = Mathf.Ceil((expectedGroundFootprintMaximumZ - expectedGroundFootprintMinimumZ +
                                                WarehouseMargin * 2f) / WallModuleWidth) * WallModuleWidth;
        Vector3 expectedGroundCenter = new Vector3(
            (expectedGroundFootprintMinimumX + expectedGroundFootprintMaximumX) * .5f,
            WarehouseGroundElevation,
            (expectedGroundFootprintMinimumZ + expectedGroundFootprintMaximumZ) * .5f);
        Bounds warehouseGroundBounds = warehouseGroundRenderer == null ? default : warehouseGroundRenderer.bounds;
        bool warehouseGroundBoundsValid = warehouseGroundRenderer != null &&
            Mathf.Abs(warehouseGroundBounds.center.x - expectedGroundCenter.x) <= .02f &&
            Mathf.Abs(warehouseGroundBounds.center.z - expectedGroundCenter.z) <= .02f &&
            Mathf.Abs(warehouseGroundBounds.size.x - expectedGroundWidth) <= .02f &&
            Mathf.Abs(warehouseGroundBounds.size.z - expectedGroundDepth) <= .02f &&
            warehouseGroundBounds.min.x <= expectedGroundFootprintMinimumX - WarehouseMargin + .02f &&
            warehouseGroundBounds.max.x >= expectedGroundFootprintMaximumX + WarehouseMargin - .02f &&
            warehouseGroundBounds.min.z <= expectedGroundFootprintMinimumZ - WarehouseMargin + .02f &&
            warehouseGroundBounds.max.z >= expectedGroundFootprintMaximumZ + WarehouseMargin - .02f;
        bool warehouseGroundElevationValid = warehouseGroundRenderer != null &&
            Mathf.Abs(warehouseGroundBounds.center.y - WarehouseGroundElevation) <= .002f;
        bool warehouseGroundColliderValid = warehouseGroundCollider != null && warehouseGroundFilter != null &&
            warehouseGround.GetComponents<Collider>().Length == 1 &&
            warehouseGroundCollider.sharedMesh == warehouseGroundFilter.sharedMesh &&
            warehouseGroundCollider.enabled && !warehouseGroundCollider.isTrigger && !warehouseGroundCollider.convex &&
            Mathf.Abs(warehouseGroundCollider.bounds.center.x - warehouseGroundBounds.center.x) <= .002f &&
            Mathf.Abs(warehouseGroundCollider.bounds.center.y - warehouseGroundBounds.center.y) <= .002f &&
            Mathf.Abs(warehouseGroundCollider.bounds.center.z - warehouseGroundBounds.center.z) <= .002f &&
            Mathf.Abs(warehouseGroundCollider.bounds.size.x - warehouseGroundBounds.size.x) <= .002f &&
            Mathf.Abs(warehouseGroundCollider.bounds.size.z - warehouseGroundBounds.size.z) <= .002f;
        bool warehouseApronPerimeterSealed = WarehouseApronPerimeterIsSealed(
            transforms, layout, roomSizes, roomCenters, connections, out string warehouseApronPerimeterFailure);
        bool warehouseGroundValid = warehouseGrounds.Length == 1 && warehouseGroundPrefabValid &&
            warehouseGround != null && warehouseGround.parent == warehouseShell &&
            warehouseGroundBoundsValid && warehouseGroundElevationValid && warehouseGroundColliderValid &&
            warehouseGroundMarkers == 1 && warehouseGroundProvenanceMarkers == 1 &&
            warehouseGroundNavigationPolicyMarkers == 1 && warehouseApronPerimeterSealed;
        int obsoleteWarehouseModules = transforms.Count(item =>
            item.name == "NATIVE_WarehouseRoof_9M" ||
            item.name.StartsWith("NATIVE_WarehouseRoofPanel_", StringComparison.Ordinal) ||
            item.name.StartsWith("NATIVE_WarehousePerimeterWall_", StringComparison.Ordinal) ||
            item.name == "WAREHOUSE_FINISH_VANILLA_CORRUGATED_METAL_SHAREDASSETS13");
        int obsoleteCorrugatedSlots = CountRendererSlotsUsingMaterial(root.transform,
            "MAT_NATIVE_Corrugated_Metal_Sheet_vb1lafx");
        Transform warehouseRoof = warehouseParts.FirstOrDefault(item => item.name == "NATIVE_WarehouseRoof");
        float warehouseRoofElevation = warehouseRoof == null ? float.NaN :
            warehouseRoof.GetComponentsInChildren<Renderer>(true).Max(renderer => renderer.bounds.max.y);
        Transform[] wallBackedProps = transforms.Where(item => Enumerable.Range(0, item.childCount)
            .Select(item.GetChild).Any(child => child.name.StartsWith("WALL_BACKED_PROP_OUTWARD_",
                StringComparison.Ordinal))).ToArray();
        string[] wallBackedFailureDetails = wallBackedProps.Select(item =>
                item.name + "=" + WallBackedPropFailure(item, roomCenters, roomSizes))
            .Where(detail => !detail.EndsWith("=", StringComparison.Ordinal)).ToArray();
        int invalidWallBackedFurniture = wallBackedFailureDetails.Length;
        int invalidFurnitureProvenance = wallBackedFailureDetails.Count(detail =>
            detail.IndexOf("=provenance:", StringComparison.Ordinal) >= 0);
        int invalidFurnitureFacing = wallBackedFailureDetails.Count(detail =>
            detail.IndexOf("=facing:", StringComparison.Ordinal) >= 0);
        int invalidFurniturePlacement = wallBackedFailureDetails.Count(detail =>
            detail.IndexOf("=placement:", StringComparison.Ordinal) >= 0 ||
            detail.IndexOf("=clearance:", StringComparison.Ordinal) >= 0);
        int wallBackedProvenanceMarkers = transforms.Count(item =>
            item.name.StartsWith("WALL_BACKED_PROP_PROVENANCE_", StringComparison.Ordinal));
        string[] wallBackedOverlapDetails = WallBackedOverlapDetails(wallBackedProps);
        int overlappingWallBackedFurniture = wallBackedOverlapDetails.Length;
        MeshFilter[] furnitureFilters = root.GetComponentsInChildren<MeshFilter>(true).Where(filter =>
            filter.sharedMesh != null && KillHouseNativePrefabBuilder.IsFurnitureMeshName(filter.sharedMesh.name)).ToArray();
        MeshFilter[] furnitureRootFilters = furnitureFilters.Where(filter =>
            KillHouseNativePrefabBuilder.IsFurnitureRootMeshName(filter.sharedMesh.name)).ToArray();
        int invalidFurnitureTextureClosures = furnitureRootFilters.Count(filter =>
            !KillHouseNativePrefabBuilder.HasExactFurniturePrefabContract(filter.gameObject, out _,
                requireAssetDatabaseIdentity: true, requireUnitRootScale: false));
        int furnitureMeshFamilies = furnitureFilters.Select(filter => filter.sharedMesh.name).Distinct().Count();
        string[] placedFurnitureFamilies = furnitureRootFilters.Select(filter => filter.sharedMesh.name)
            .Distinct(StringComparer.Ordinal).ToArray();
        string[] missingProvenWallFamilies = WallBackedFurnitureContracts.Keys
            .Where(meshName => !placedFurnitureFamilies.Contains(meshName, StringComparer.Ordinal)).ToArray();
        string[] placedExcludedStandaloneFamilies = placedFurnitureFamilies
            .Where(UnsupportedStandaloneFurnitureMeshes.Contains).ToArray();
        Transform[] beds = wallBackedProps.Where(item => item.name == "NATIVE_Bed").ToArray();
        int invalidWallBackedBeds = beds.Count(item => !WallBackedPropValid(item, roomCenters, roomSizes));
        Transform[] centerRoomProps = transforms.Where(item => CountDirectChildren(item,
            "CENTER_ROOM_PROP_ROLE_") == 1).ToArray();
        Transform[] centerRoomPropSkips = transforms.Where(item => item.parent != null &&
            TryGetDressingRoomIndex(item.parent, out _) &&
            (item.name.StartsWith("CENTER_ROOM_PROP_SKIP_TABLE_ROOM_", StringComparison.Ordinal) ||
             item.name.StartsWith("CENTER_ROOM_PROP_SKIP_SOFA_ROOM_", StringComparison.Ordinal))).ToArray();
        string[] centerRoomPropFailureDetails = centerRoomProps.Select(item => item.name + "=" +
                CenterRoomPropFailure(item, layout, roomCenters, roomSizes, variant.Motif, connections,
                    enemyMarkerTransforms))
            .Where(detail => !detail.EndsWith("=", StringComparison.Ordinal)).ToArray();
        int invalidCenterRoomProps = centerRoomPropFailureDetails.Length;
        int invalidCenterRoomPropProvenance = centerRoomPropFailureDetails.Count(detail =>
            detail.IndexOf("=provenance:", StringComparison.Ordinal) >= 0);
        int invalidCenterRoomPropFacing = centerRoomPropFailureDetails.Count(detail =>
            detail.IndexOf("=facing:", StringComparison.Ordinal) >= 0);
        int invalidCenterRoomPropClearance = centerRoomPropFailureDetails.Count(detail =>
            detail.IndexOf("=clearance:", StringComparison.Ordinal) >= 0);
        int centerRoomPropTacticalCapsuleConflicts = centerRoomPropFailureDetails.Count(detail =>
            detail.IndexOf("=tactical:", StringComparison.Ordinal) >= 0);
        int centerRoomPropPortalCorridorConflicts = centerRoomPropFailureDetails.Count(detail =>
            detail.IndexOf("portal-approach", StringComparison.Ordinal) >= 0);
        int centerRoomPropCirculationFailures = centerRoomPropFailureDetails.Count(detail =>
            detail.IndexOf("circulation-", StringComparison.Ordinal) >= 0);
        string[] centerRoomPropOverlapDetails = CenterRoomPropOverlapDetails(centerRoomProps);
        int overlappingCenterRoomProps = centerRoomPropOverlapDetails.Length;
        string[] centerRoomPropOutcomeFailures = CenterRoomPropOutcomeFailures(centerRoomProps,
            centerRoomPropSkips, layout, roomSizes, variant.Motif);
        int tableCenterRoomProps = centerRoomProps.Count(item => CenterRoomPropHasRole(item, "TABLE"));
        int sofaCenterRoomProps = centerRoomProps.Count(item => CenterRoomPropHasRole(item, "SOFA"));
        int[] eligibleTableCenterRoomIndexes = Enumerable.Range(1, layout.Rooms.Length - 1).Where(index =>
            CenterTableEligible(layout.Rooms[index], roomSizes[index], variant.Motif)).ToArray();
        int[] eligibleLivingSofaRoomIndexes = Enumerable.Range(1, layout.Rooms.Length - 1).Where(index =>
            layout.Rooms[index] == RoomType.Living &&
            CenterSofaRoomEligible(layout.Rooms[index], roomSizes[index], false)).ToArray();
        int eligibleTableCenterRooms = eligibleTableCenterRoomIndexes.Length;
        int eligibleLivingSofaRooms = eligibleLivingSofaRoomIndexes.Length;
        JObject centerRoomPropsByRoomType = new JObject();
        foreach (RoomType roomType in layout.Rooms.Distinct().OrderBy(type => type.ToString(),
                     StringComparer.Ordinal))
            centerRoomPropsByRoomType[roomType.ToString()] = centerRoomProps.Count(prop =>
                TryGetDressingRoomIndex(prop.parent, out int owner) && layout.Rooms[owner] == roomType);
        JObject centerRoomPropPlacementsByRoleAndRoomType = new JObject();
        foreach (string role in CenterRoomFurnitureContracts.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            var countsByRoomType = new JObject();
            foreach (RoomType roomType in layout.Rooms.Distinct().OrderBy(type => type.ToString(),
                         StringComparer.Ordinal))
                countsByRoomType[roomType.ToString()] = centerRoomProps.Count(prop =>
                    CenterRoomPropHasRole(prop, role) && TryGetDressingRoomIndex(prop.parent, out int owner) &&
                    layout.Rooms[owner] == roomType);
            centerRoomPropPlacementsByRoleAndRoomType[role] = countsByRoomType;
        }
        JObject centerRoomPropNativeContracts = new JObject();
        foreach (KeyValuePair<string, CenterRoomFurnitureContract> pair in CenterRoomFurnitureContracts
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            centerRoomPropNativeContracts[pair.Key] = new JObject
            {
                ["meshName"] = pair.Value.MeshName,
                ["prefabPath"] = KillHouseNativePrefabBuilder.PrefabPath(pair.Value.MeshName),
                ["installedRootScale"] = pair.Value.Scale,
                ["localFacingAxis"] = pair.Value.LocalFacingAxis.ToString("F3"),
                ["bidirectionalFacing"] = pair.Value.BidirectionalFacing,
                ["provenanceMarker"] = pair.Value.ProvenanceMarker
            };
        string[] blockedPortalDetails = BlockedPortalApproachDetails(root.transform.Find("30_NATIVE_ROOM_DRESSING"),
            roomCenters, roomSizes, connections);
        int blockedPortalApproaches = blockedPortalDetails.Length;
        int staticDoorShadowBlockers = transforms.Count(item =>
            item.name.StartsWith("SHADOW BLOCKER Door_interior", StringComparison.Ordinal));
        Light[] lights = root.GetComponentsInChildren<Light>(true);
        int fallbackDirectionalLights = lights.Count(light => light.type == LightType.Directional);
        int enabledDirectionalLights = lights.Count(light => light.type == LightType.Directional && light.enabled);
        Light[] fixtureLights = lights.Where(light =>
            light.name.StartsWith("ROOM_LOCAL_FIXTURE_LIGHT_", StringComparison.Ordinal)).ToArray();
        Transform[] fixtureVisuals = transforms.Where(item =>
            item.name.StartsWith("NATIVE_Lamp_fluorescent_B_", StringComparison.Ordinal)).ToArray();
        int pointLights = lights.Count(light => light.type == LightType.Point);
        int spotLights = lights.Count(light => light.type == LightType.Spot);
        int expectedFixtureLights = roomSizes.Sum(size =>
            Mathf.Clamp(Mathf.CeilToInt(size.x / WarehouseFixtureSpacing), 1, 2) *
            Mathf.Clamp(Mathf.CeilToInt(size.y / WarehouseFixtureSpacing), 1, 2));
        Transform[] lightHolders = transforms.Where(item => item.parent != null &&
            string.Equals(item.parent.name, "70_LIGHTING", StringComparison.Ordinal) &&
            item.name.StartsWith("ROOM_LIGHT_", StringComparison.Ordinal) &&
            item.name.IndexOf("_STATE_", StringComparison.Ordinal) >= 0).ToArray();
        int litRooms = lightHolders.Count(item => item.name.EndsWith("_STATE_LIT", StringComparison.Ordinal));
        int dimRooms = lightHolders.Count(item => item.name.EndsWith("_STATE_DIM", StringComparison.Ordinal));
        int darkRooms = lightHolders.Count(item => item.name.EndsWith("_STATE_DARK", StringComparison.Ordinal));
        int safeRoomLit = lightHolders.Count(item => item.name.StartsWith("ROOM_LIGHT_00_SAFE_", StringComparison.Ordinal) &&
            item.name.EndsWith("_STATE_LIT", StringComparison.Ordinal));
        int shadowCastingFixtureLights = fixtureLights.Count(light =>
            light.enabled && light.shadows != LightShadows.None);
        Collider[] warehouseRoofColliders = FindWarehouseRoofColliders(warehouseShell);
        int invalidFixtureLights = fixtureLights.Count(light => !FixtureLightValid(light, warehouseRoofColliders));
        int invalidFixtureVisuals = fixtureVisuals.Count(item => !FixtureVisualValid(item, warehouseRoofColliders));
        float[] fixtureRoofGaps = fixtureVisuals.Select(item =>
        {
            return TryFixtureRoofGap(item, warehouseRoofColliders, out float gap, out _) ? gap : float.NaN;
        }).ToArray();
        float minimumFixtureRoofGap = fixtureRoofGaps.Length == 0 || fixtureRoofGaps.Any(value => !float.IsFinite(value))
            ? float.NaN : fixtureRoofGaps.Min();
        float maximumFixtureRoofGap = fixtureRoofGaps.Length == 0 || fixtureRoofGaps.Any(value => !float.IsFinite(value))
            ? float.NaN : fixtureRoofGaps.Max();
        Volume[] volumes = root.GetComponentsInChildren<Volume>(true);
        bool indoorVolumeValid = volumes.Length == 1 && IndoorVolumeValid(volumes[0]);
        int distinctTypes = layout.Rooms.Skip(1).Distinct().Count();
        int distinctRoomSizes = roomSizes.Select(size => size.x.ToString("F1") + "x" + size.y.ToString("F1")).Distinct().Count();
        int elongatedRooms = roomSizes.Count(size => !Mathf.Approximately(size.x, size.y));
        int graphEdges = CountGraphEdges(layout);
        int graphLoopRank = graphEdges - cells.Length + 1;
        int alignedOpposingPortalPairs = CountAlignedOpposingPortalPairs(connections);
        int maximumAxialPortalRun = MaximumAxialPortalRun(connections, roomCenters);
        int distinctPortalOffsets = connections.Select(plan => plan.PortalOffset.ToString("F1")).Distinct().Count();
        float[] connectionGaps = GraphConnectionGaps(layout, roomSizes, roomCenters);
        int directSharedWallConnections = connectionGaps.Count(gap => gap < .1f);
        int shortConnectorConnections = connectionGaps.Count(gap => gap >= .1f && gap <= 4.01f);
        float maximumConnector = connectionGaps.Length == 0 ? 0f : connectionGaps.Max();
        float averageConnector = connectionGaps.Length == 0 ? 0f : connectionGaps.Average();
        float footprintMinimumX = Enumerable.Range(0, roomCenters.Length).Min(index => roomCenters[index].x - roomSizes[index].x * .5f);
        float footprintMaximumX = Enumerable.Range(0, roomCenters.Length).Max(index => roomCenters[index].x + roomSizes[index].x * .5f);
        float footprintMinimumZ = Enumerable.Range(0, roomCenters.Length).Min(index => roomCenters[index].z - roomSizes[index].y * .5f);
        float footprintMaximumZ = Enumerable.Range(0, roomCenters.Length).Max(index => roomCenters[index].z + roomSizes[index].y * .5f);
        bool blackWarehouseEnvironment = RenderSettings.skybox == null && RenderSettings.ambientMode == AmbientMode.Flat &&
                                         RenderSettings.ambientLight.maxColorComponent <= .001f &&
                                         RenderSettings.ambientIntensity <= .001f &&
                                         RenderSettings.reflectionIntensity <= .001f;
        bool passed = primitiveMeshes == 0 && cells.Length >= 19 && distinctRoomSizes >= 5 && elongatedRooms >= 8 &&
                      doorSockets >= 6 && doorShells == doorSockets && doorAudioBanks == 1 &&
                      misalignedDoorSockets == 0 &&
                      staticDoorLeaves == 0 && openings >= 5 && windows >= 4 &&
                       enemyMarkers == PveAuthoredEnemyMarkerTarget && tacticalPositions == enemyMarkers &&
                       invalidTacticalMarkers == 0 && invalidPveEnemySpawnPairClearance == 0 &&
                      tacticalRoleTypes >= 3 && playerMarkers == 4 &&
                      pvpTeam1Markers.Length == PvpSpawnsPerTeam &&
                      pvpTeam2Markers.Length == PvpSpawnsPerTeam && invalidPvpSpawnMarkers == 0 &&
                      pvpTeam1RoomDistributionValid && pvpTeam2RoomDistributionValid &&
                      pvpSectorRoomsDisjoint && pvpTeam1SectorConnected && pvpTeam2SectorConnected &&
                      pvpBaseGraphDistance >= PvpMinimumSectorGraphDistance &&
                      pvpMinimumOpposingSpawnDistance >= PvpMinimumOpposingDistance &&
                      pvpDirectOpposingLineOfSightPairs == 0 && invalidPvpSpawnPairClearance == 0 &&
                      exfils == 1 && distinctTypes >= 8 &&
                      hallwaySideDoors >= 1 && fixedSafeDoorInterfaces == 2 && graphLoopRank >= 2 &&
                      alignedOpposingPortalPairs <= 1 && maximumAxialPortalRun <= 3 && distinctPortalOffsets >= 3 &&
                      spatialMotifMarkers == 1 && spatialFeatureCount >= 2 && roomCeilings == 0 &&
                      warehouseShellGroups == 1 && warehouseParts.Length == 4 && warehouseFinishMarkers == 1 &&
                      invalidWarehousePartFinish == 0 && warehouseSteelSlots == 4 && warehouseMeshColliders == 4 &&
                      warehouseGrounds.Length == 1 && warehouseGroundValid &&
                      obsoleteWarehouseModules == 0 && obsoleteCorrugatedSlots == 0 &&
                      Mathf.Abs(warehouseRoofElevation - WarehouseRoofHeight) <= .2f &&
                       wallBackedProps.Length >= 12 && invalidWallBackedFurniture == 0 &&
                       wallBackedProvenanceMarkers == wallBackedProps.Length && overlappingWallBackedFurniture == 0 &&
                       furnitureFilters.Length >= 12 && furnitureMeshFamilies >= WallBackedFurnitureContracts.Count &&
                       missingProvenWallFamilies.Length == 0 && placedExcludedStandaloneFamilies.Length == 0 &&
                       invalidFurnitureTextureClosures == 0 &&
                       beds.Length >= 1 &&
                      invalidWallBackedBeds == 0 && centerRoomProps.Length >= 2 &&
                      tableCenterRoomProps >= 1 && sofaCenterRoomProps >= 1 &&
                      invalidCenterRoomProps == 0 && invalidCenterRoomPropProvenance == 0 &&
                      invalidCenterRoomPropFacing == 0 && invalidCenterRoomPropClearance == 0 &&
                      centerRoomPropTacticalCapsuleConflicts == 0 && centerRoomPropPortalCorridorConflicts == 0 &&
                      centerRoomPropCirculationFailures == 0 && overlappingCenterRoomProps == 0 &&
                      centerRoomPropOutcomeFailures.Length == 0 && eligibleTableCenterRooms >= 1 &&
                      eligibleLivingSofaRooms >= 1 && blockedPortalApproaches == 0 && staticDoorShadowBlockers == 0 &&
                      connectionGaps.Length == graphEdges && connectionGaps.All(gap => gap >= -.01f) &&
                      maximumConnector <= MaximumConnectorLength + .01f && averageConnector <= 4.01f &&
                      safeRoomMarkers == 1 && fallbackDirectionalLights == 1 && enabledDirectionalLights == 0 &&
                      fixtureLights.Length == expectedFixtureLights && pointLights == 0 && spotLights == fixtureLights.Length &&
                      invalidFixtureLights == 0 && fixtureVisuals.Length == expectedFixtureLights &&
                      invalidFixtureVisuals == 0 && lightHolders.Length == cells.Length && safeRoomLit == 1 &&
                       shadowCastingFixtureLights == litRooms &&
                       litRooms >= 5 && dimRooms >= 4 && darkRooms >= 7 && indoorVolumeValid &&
                      blackWarehouseEnvironment &&
                      transforms.Count(item => item.name == MapMarker) == 1 &&
                      pveSpawnSetMarkers == 1 && pvpSpawnSetMarkers == 1 && spawnSetMarkers == 2;
        if (!passed)
            throw new InvalidDataException("Static validation failed for " + variant.Id +
                ": rooms=" + cells.Length + ", distinctSizes=" + distinctRoomSizes +
                ", elongated=" + elongatedRooms + ", doors=" + doorSockets + ", shells=" + doorShells +
                ", misalignedDoors=" + misalignedDoorSockets +
                ", opens=" + openings + ", windows=" + windows + ", enemies=" + enemyMarkers +
                ", tacticalPositions=" + tacticalPositions + ", invalidTactical=" + invalidTacticalMarkers +
                ", invalidPvePairClearance=" + invalidPveEnemySpawnPairClearance +
                ", minimumPveSeparation=" + minimumPveEnemySpawnSeparation.ToString("F3") +
                ", tacticalFailures=" + tacticalFailureSummary + ", tacticalRoleTypes=" + tacticalRoleTypes +
                ", pvePlayerSpawns=" + playerMarkers + ", pvpSpawns=" + pvpTeam1Markers.Length + "/" +
                pvpTeam2Markers.Length + ", pvpRooms=" + string.Join("|", pvpTeam1Rooms) + "/" +
                string.Join("|", pvpTeam2Rooms) + ", pvpGraphDistance=" + pvpBaseGraphDistance +
                ", pvpOpposingDistance=" + pvpMinimumOpposingSpawnDistance.ToString("F2") +
                ", pvpDirectLosPairs=" + pvpDirectOpposingLineOfSightPairs +
                ", invalidPvpSpawns=" + invalidPvpSpawnMarkers +
                (pvpSpawnFailureDetails.Length == 0 ? string.Empty :
                    "[" + string.Join("|", pvpSpawnFailureDetails) + "]") +
                ", spawnSets=" + pveSpawnSetMarkers + "/" + pvpSpawnSetMarkers + "/" + spawnSetMarkers +
                ", furnitureRenderers=" + furnitureFilters.Length + ", furnitureFamilies=" + furnitureMeshFamilies +
                ", invalidFurnitureTextureClosures=" + invalidFurnitureTextureClosures +
                ", missingProvenWallFamilies=" + string.Join("|", missingProvenWallFamilies) +
                ", excludedStandalonePlacements=" + string.Join("|", placedExcludedStandaloneFamilies) +
                ", distinctTypes=" + distinctTypes + ", hallwaySideDoors=" + hallwaySideDoors +
                ", fixedSafeDoors=" + fixedSafeDoorInterfaces + ", graphLoopRank=" + graphLoopRank +
                ", alignedOpposingPortals=" + alignedOpposingPortalPairs +
                ", maxAxialPortalRun=" + maximumAxialPortalRun +
                ", distinctPortalOffsets=" + distinctPortalOffsets +
                ", motifMarkers=" + spatialMotifMarkers + ", spatialFeatures=" + spatialFeatureCount +
                ", roomCeilings=" + roomCeilings + ", warehouseShellGroups=" + warehouseShellGroups +
                ", warehouseParts=" + warehouseParts.Length + ", warehouseSteelSlots=" + warehouseSteelSlots +
                ", invalidWarehousePartFinish=" + invalidWarehousePartFinish +
                ", warehouseMeshColliders=" + warehouseMeshColliders +
                ", warehouseGrounds=" + warehouseGrounds.Length + ", warehouseGroundValid=" + warehouseGroundValid +
                ", warehouseGroundPrefabValid=" + warehouseGroundPrefabValid +
                ", warehouseGroundFailure=" + warehouseGroundFailure +
                ", warehouseGroundBoundsValid=" + warehouseGroundBoundsValid +
                ", warehouseGroundElevationValid=" + warehouseGroundElevationValid +
                ", warehouseGroundColliderValid=" + warehouseGroundColliderValid +
                ", warehouseGroundMarkers=" + warehouseGroundMarkers + "/" +
                warehouseGroundProvenanceMarkers + "/" + warehouseGroundNavigationPolicyMarkers +
                ", warehouseApronPerimeterSealed=" + warehouseApronPerimeterSealed +
                ", warehouseApronPerimeterFailure=" + warehouseApronPerimeterFailure +
                ", obsoleteWarehouseModules=" + obsoleteWarehouseModules +
                ", obsoleteCorrugatedSlots=" + obsoleteCorrugatedSlots +
                ", warehouseRoofElevation=" + warehouseRoofElevation.ToString("F2") +
                ", wallBackedFurniture=" + (wallBackedProps.Length - invalidWallBackedFurniture) + "/" +
                wallBackedProps.Length + ", wallBackedBeds=" + (beds.Length - invalidWallBackedBeds) + "/" + beds.Length +
                ", furnitureProvenanceMarkers=" + wallBackedProvenanceMarkers +
                ", invalidFurnitureProvenance=" + invalidFurnitureProvenance +
                ", invalidFurnitureFacing=" + invalidFurnitureFacing +
                ", invalidFurniturePlacement=" + invalidFurniturePlacement +
                ", overlappingFurniture=" + overlappingWallBackedFurniture +
                (wallBackedFailureDetails.Length == 0 ? string.Empty :
                    "[" + string.Join("|", wallBackedFailureDetails) + "]") +
                (wallBackedOverlapDetails.Length == 0 ? string.Empty :
                    "[" + string.Join("|", wallBackedOverlapDetails) + "]") +
                ", centerRoomProps=" + centerRoomProps.Length + ", centerTables=" + tableCenterRoomProps +
                ", centerSofas=" + sofaCenterRoomProps + ", eligibleCenterTables=" + eligibleTableCenterRooms +
                ", eligibleLivingSofas=" + eligibleLivingSofaRooms +
                ", invalidCenterRoomProps=" + invalidCenterRoomProps +
                ", invalidCenterPropProvenance=" + invalidCenterRoomPropProvenance +
                ", invalidCenterPropFacing=" + invalidCenterRoomPropFacing +
                ", invalidCenterPropClearance=" + invalidCenterRoomPropClearance +
                ", centerPropPortalConflicts=" + centerRoomPropPortalCorridorConflicts +
                ", centerPropCirculationFailures=" + centerRoomPropCirculationFailures +
                ", centerPropTacticalConflicts=" + centerRoomPropTacticalCapsuleConflicts +
                ", overlappingCenterProps=" + overlappingCenterRoomProps +
                (centerRoomPropFailureDetails.Length == 0 ? string.Empty :
                    "[" + string.Join("|", centerRoomPropFailureDetails) + "]") +
                (centerRoomPropOverlapDetails.Length == 0 ? string.Empty :
                    "[" + string.Join("|", centerRoomPropOverlapDetails) + "]") +
                (centerRoomPropOutcomeFailures.Length == 0 ? string.Empty :
                    "[" + string.Join("|", centerRoomPropOutcomeFailures) + "]") +
                ", blockedPortalApproaches=" + blockedPortalApproaches +
                (blockedPortalDetails.Length == 0 ? string.Empty : "[" + string.Join("|", blockedPortalDetails) + "]") +
                ", staticDoorShadowBlockers=" + staticDoorShadowBlockers +
                ", fixtureLights=" + fixtureLights.Length + "/" + expectedFixtureLights +
                ", fixtureVisuals=" + fixtureVisuals.Length + "/" + expectedFixtureLights +
                ", invalidFixtureVisuals=" + invalidFixtureVisuals +
                ", roomLightStates=" + litRooms + "/" + dimRooms + "/" + darkRooms +
                ", indoorVolume=" + indoorVolumeValid + ", blackWarehouseEnvironment=" + blackWarehouseEnvironment +
                ", enabledDirectional=" + enabledDirectionalLights +
                ", maxConnector=" + maximumConnector.ToString("F1") +
                ", avgConnector=" + averageConnector.ToString("F1") + ".");
        return new JObject
        {
            ["schema"] = "vektor-killhouse/scene-validation@19",
            ["generatedUtc"] = DateTime.UtcNow.ToString("O"),
            ["variantId"] = variant.Id,
            ["variantIndex"] = variantIndex + 1,
            ["scenePath"] = variant.ScenePath,
            ["cycleMoves"] = variant.Moves,
            ["orderedRoomTypeSequence"] = string.Join(">", layout.Rooms.Skip(1).Select(room => room.ToString())),
            ["cyclePortalPattern"] = string.Join(string.Empty, variant.Portals.Select(portal => portal == PortalKind.Door ? "D" : "O")),
            ["spatialMotif"] = variant.Motif.ToString(),
            ["spatialMotifMarkers"] = spatialMotifMarkers,
            ["interiorSplitWallSegments"] = interiorSplitWalls,
            ["nativeLowDividerModules"] = nativeLowDividers,
            ["nativeOfficePartitionModules"] = nativeOfficePartitions,
            ["spatialFeatureModules"] = spatialFeatureCount,
            ["roomCountIncludingSafe"] = cells.Length,
            ["baseCycleRoomCountIncludingSafe"] = layout.BaseCycleCount,
            ["secondaryRoomCount"] = cells.Length - layout.BaseCycleCount,
            ["distinctRoomTypesExcludingSafe"] = distinctTypes,
            ["packingMode"] = "actual-column-and-row-extents",
            ["fixedCellSpacing"] = false,
            ["directSharedWallConnections"] = directSharedWallConnections,
            ["shortConnectorConnectionsUpTo4m"] = shortConnectorConnections,
            ["maximumConnectorLengthMeters"] = maximumConnector,
            ["averageConnectorLengthMeters"] = averageConnector,
            ["packedFootprintWidthMeters"] = footprintMaximumX - footprintMinimumX,
            ["packedFootprintDepthMeters"] = footprintMaximumZ - footprintMinimumZ,
            ["roomModuleSizesMeters"] = new JArray(roomSizes.Select(size => size.x.ToString("F1") + "x" + size.y.ToString("F1"))),
            ["distinctRoomModuleSizes"] = distinctRoomSizes,
            ["elongatedRoomModules"] = elongatedRooms,
            ["partitionHeightMeters"] = PartitionHeight,
            ["warehouseRoofHeightMeters"] = WarehouseRoofHeight,
            ["roomHeightCeilings"] = roomCeilings,
            ["warehouseShellGroups"] = warehouseShellGroups,
            ["warehouseExactPrefabParts"] = warehouseParts.Length,
            ["warehouseMeshColliders"] = warehouseMeshColliders,
            ["warehouseRoofElevationMeters"] = warehouseRoofElevation,
            ["warehouseFinishMarkers"] = warehouseFinishMarkers,
            ["warehouseSteelMaterialSlots"] = warehouseSteelSlots,
            ["warehouseGroundAprons"] = warehouseGrounds.Length,
            ["warehouseGroundValid"] = warehouseGroundValid,
            ["warehouseGroundPrefabValid"] = warehouseGroundPrefabValid,
            ["warehouseGroundPrefabFailure"] = warehouseGroundFailure,
            ["warehouseGroundBoundsValid"] = warehouseGroundBoundsValid,
            ["warehouseGroundElevationValid"] = warehouseGroundElevationValid,
            ["warehouseGroundColliderValid"] = warehouseGroundColliderValid,
            ["warehouseApronPerimeterSealed"] = warehouseApronPerimeterSealed,
            ["warehouseApronPerimeterFailure"] = warehouseApronPerimeterFailure,
            ["warehouseGroundWidthMeters"] = warehouseGroundBounds.size.x,
            ["warehouseGroundDepthMeters"] = warehouseGroundBounds.size.z,
            ["warehouseGroundElevationMeters"] = warehouseGroundBounds.center.y,
            ["warehouseGroundExpectedWidthMeters"] = expectedGroundWidth,
            ["warehouseGroundExpectedDepthMeters"] = expectedGroundDepth,
            ["warehouseGroundSourceChannels"] = "4-vertices/6-indices/1-triangle-submesh/up-normal/tangent/uv0/uv1",
            ["warehouseGroundAppearanceDonor"] = "level11 GO104 Cube renderer19268 -> sharedassets11:26 Floor",
            ["warehouseGroundGeometryDonor"] = "level11 inactive GO9601 Floor meshFilter28534+meshCollider37204 -> sharedassets11:152; renderer27730 -> sharedassets11:26",
            ["warehouseGroundNavigationPolicy"] = "excluded-from-runtime-grid-sources; native perimeter walls and paired connector walls seal the kill-house interior",
            ["invalidWarehousePartFinishModules"] = invalidWarehousePartFinish,
            ["obsoleteReconstructedWarehouseModules"] = obsoleteWarehouseModules,
            ["obsoleteCorrugatedMaterialSlots"] = obsoleteCorrugatedSlots,
            ["warehouseGeometryDonor"] = "Assets/Scenes/PVP Woods Warehouse.unity::Warehouse New/Base Warehouse+OverHead Support+Roof+Support 2",
            ["warehouseMaterialDonor"] = "sharedassets11.assets::RM Steel smooth",
            ["wallBackedFurniture"] = wallBackedProps.Length,
            ["invalidWallBackedFurniture"] = invalidWallBackedFurniture,
            ["wallBackedFurnitureFailureDetails"] = new JArray(wallBackedFailureDetails),
            ["wallBackedFurnitureProvenanceMarkers"] = wallBackedProvenanceMarkers,
            ["invalidWallBackedFurnitureProvenance"] = invalidFurnitureProvenance,
            ["invalidWallBackedFurnitureFacing"] = invalidFurnitureFacing,
            ["invalidWallBackedFurniturePlacementOrClearance"] = invalidFurniturePlacement,
            ["overlappingWallBackedFurniture"] = overlappingWallBackedFurniture,
            ["overlappingWallBackedFurnitureDetails"] = new JArray(wallBackedOverlapDetails),
            ["nativeFurnitureRenderers"] = furnitureFilters.Length,
            ["nativeFurnitureMeshFamilies"] = furnitureMeshFamilies,
            ["nativeFurnitureRootInstances"] = furnitureRootFilters.Length,
            ["placedFurnitureFamilies"] = new JArray(placedFurnitureFamilies.OrderBy(value => value,
                StringComparer.Ordinal)),
            ["requiredProvenWallBackedFamilies"] = new JArray(WallBackedFurnitureContracts.Keys.OrderBy(value => value,
                StringComparer.Ordinal)),
            ["missingProvenWallBackedFamilies"] = new JArray(missingProvenWallFamilies),
            ["excludedUnprovenStandaloneFamilies"] = new JArray(UnsupportedStandaloneFurnitureMeshes.OrderBy(value =>
                value, StringComparer.Ordinal)),
            ["placedExcludedUnprovenStandaloneFamilies"] = new JArray(placedExcludedStandaloneFamilies),
            ["placedExcludedUnprovenStandaloneFamilyCount"] = placedExcludedStandaloneFamilies.Length,
            ["invalidFurnitureTextureClosures"] = invalidFurnitureTextureClosures,
            ["furnitureTextureContract"] = "exact-active-level4-root-and-retained-child-renderers-in-native-order; exact-mesh-path-submesh-slot-base-normal-mask-and-source-vertex-channels",
            ["furnitureColliderContract"] = "exact-level4-root-plus-active-child-collider-count-type-enabled-trigger-convex-cooking-and-dedicated-collision-mesh; disabled-drawer-collider-preserved",
            ["furnitureHierarchyContract"] = "level4-sha256-3b6d1546e8196aeb9b2230818b4787c4cd5f53aab2d6b49bd32452e0b9626e27; active-children-only; exact-order-name-local-transform-renderer-and-collider; MonoBehaviours-explicitly-stripped",
            ["wallBackedBeds"] = beds.Length,
            ["invalidWallBackedBeds"] = invalidWallBackedBeds,
            ["wallBackedFurnitureOrientationContract"] = "direct-installed-level4-per-family: retained-local-positive-z-to-interior/local-negative-z-to-owned-wall; Bed_queen-pillows-prove-headboard-negative-z",
            ["wallBackedFurnitureProvenanceContract"] = "exact-prefab-path-plus-generated-mesh-path-plus-ordered-material-slots-plus-installed-level4-GO-MF-MR-mesh-and-renderer-SHA256-marker",
            ["centerRoomProps"] = centerRoomProps.Length,
            ["centerRoomTableProps"] = tableCenterRoomProps,
            ["centerRoomSofaProps"] = sofaCenterRoomProps,
            ["eligibleCenterRoomTableRooms"] = eligibleTableCenterRooms,
            ["eligibleLivingSofaRooms"] = eligibleLivingSofaRooms,
            ["eligibleCenterRoomTableRoomIndexes"] = new JArray(eligibleTableCenterRoomIndexes),
            ["eligibleLivingSofaRoomIndexes"] = new JArray(eligibleLivingSofaRoomIndexes),
            ["centerRoomPropSkippedCandidates"] = centerRoomPropSkips.Length,
            ["centerRoomTableSkippedCandidates"] = centerRoomPropSkips.Count(item =>
                item.name.StartsWith("CENTER_ROOM_PROP_SKIP_TABLE_ROOM_", StringComparison.Ordinal)),
            ["centerRoomSofaSkippedCandidates"] = centerRoomPropSkips.Count(item =>
                item.name.StartsWith("CENTER_ROOM_PROP_SKIP_SOFA_ROOM_", StringComparison.Ordinal)),
            ["centerRoomPropSkipMarkers"] = new JArray(centerRoomPropSkips.Select(item => item.name)
                .OrderBy(value => value, StringComparer.Ordinal)),
            ["centerRoomPropSkipDetails"] = new JArray(centerRoomPropSkips.OrderBy(item => item.name,
                StringComparer.Ordinal).Select(item =>
            {
                TryGetDressingRoomIndex(item.parent, out int owner);
                Transform reason = Enumerable.Range(0, item.childCount).Select(item.GetChild).First(child =>
                    child.name.StartsWith("CENTER_ROOM_PROP_SKIP_REASON_", StringComparison.Ordinal));
                return new JObject
                {
                    ["roomIndex"] = owner,
                    ["roomType"] = layout.Rooms[owner].ToString(),
                    ["role"] = item.name.StartsWith("CENTER_ROOM_PROP_SKIP_TABLE_ROOM_", StringComparison.Ordinal)
                        ? "TABLE" : "SOFA",
                    ["marker"] = item.name,
                    ["reasonMarker"] = reason.name
                };
            })),
            ["centerRoomPropsByRoomType"] = centerRoomPropsByRoomType,
            ["centerRoomPropPlacementsByRoleAndRoomType"] = centerRoomPropPlacementsByRoleAndRoomType,
            ["centerRoomPropPlacements"] = new JArray(centerRoomProps.OrderBy(item => item.parent.name,
                StringComparer.Ordinal).ThenBy(item => item.name, StringComparer.Ordinal).Select(item =>
            {
                TryGetDressingRoomIndex(item.parent, out int owner);
                Transform role = Enumerable.Range(0, item.childCount).Select(item.GetChild).First(child =>
                    child.name.StartsWith("CENTER_ROOM_PROP_ROLE_", StringComparison.Ordinal));
                Transform facing = Enumerable.Range(0, item.childCount).Select(item.GetChild).First(child =>
                    child.name.StartsWith("CENTER_ROOM_PROP_FACING_", StringComparison.Ordinal));
                Transform candidate = Enumerable.Range(0, item.childCount).Select(item.GetChild).First(child =>
                    child.name.StartsWith("CENTER_ROOM_PROP_DETERMINISTIC_CANDIDATE_", StringComparison.Ordinal));
                return new JObject
                {
                    ["roomIndex"] = owner,
                    ["roomType"] = layout.Rooms[owner].ToString(),
                    ["instance"] = item.name,
                    ["role"] = role.name.Substring("CENTER_ROOM_PROP_ROLE_".Length),
                    ["facingMarker"] = facing.name,
                    ["candidateMarker"] = candidate.name,
                    ["position"] = item.position.x.ToString("F3") + "," + item.position.y.ToString("F3") + "," +
                                   item.position.z.ToString("F3"),
                    ["yawDegrees"] = item.eulerAngles.y
                };
            })),
            ["invalidCenterRoomProps"] = invalidCenterRoomProps,
            ["centerRoomPropFailureDetails"] = new JArray(centerRoomPropFailureDetails),
            ["invalidCenterRoomPropProvenance"] = invalidCenterRoomPropProvenance,
            ["invalidCenterRoomPropFacing"] = invalidCenterRoomPropFacing,
            ["invalidCenterRoomPropClearance"] = invalidCenterRoomPropClearance,
            ["overlappingCenterRoomProps"] = overlappingCenterRoomProps,
            ["overlappingCenterRoomPropDetails"] = new JArray(centerRoomPropOverlapDetails),
            ["centerRoomPropPortalCorridorConflicts"] = centerRoomPropPortalCorridorConflicts,
            ["centerRoomPropDoorSocketApproachConflicts"] = centerRoomPropPortalCorridorConflicts,
            ["centerRoomPropCirculationFailures"] = centerRoomPropCirculationFailures,
            ["centerRoomPropPrimaryWalkwayFailures"] = centerRoomPropCirculationFailures,
            ["centerRoomPropTacticalCapsuleConflicts"] = centerRoomPropTacticalCapsuleConflicts,
            ["centerRoomPropTacticalMarkerCapsuleConflicts"] = centerRoomPropTacticalCapsuleConflicts,
            ["centerRoomPropOutcomeFailures"] = new JArray(centerRoomPropOutcomeFailures),
            ["centerRoomPropRequiredDirectMarkerPrefixes"] = new JArray(new[]
            {
                "CENTER_ROOM_PROP_ROLE_", "CENTER_ROOM_PROP_ROOM_", "CENTER_ROOM_PROP_PROVENANCE_",
                "CENTER_ROOM_PROP_FACING_", "CENTER_ROOM_PROP_DETERMINISTIC_CANDIDATE_",
                "CENTER_ROOM_PROP_CLEARANCE_VALID", "CENTER_ROOM_PROP_CIRCULATION_VALID"
            }),
            ["centerRoomPropNativeContracts"] = centerRoomPropNativeContracts,
            ["centerRoomPropContract"] = "full-installed-unit-scale-native-roots-only; deterministic-candidate-order; exact-prefab-provenance-and-facing; 0.82m-room-perimeter-clearance; 0.42m-capsule-grid-portal-connectivity; no-door-approach-sibling-prop-or-tactical-capsule-overlap; reject-or-skip-never-shrink",
            ["blockedPortalApproaches"] = blockedPortalApproaches,
            ["blockedPortalApproachDetails"] = new JArray(blockedPortalDetails),
            ["doorConnections"] = doorSockets,
            ["doorV2Sockets"] = doorSockets,
            ["doorV2PortableShells"] = doorShells,
            ["doorV2AudioBanks"] = doorAudioBanks,
            ["misalignedDoorV2Sockets"] = misalignedDoorSockets,
            ["doorPlaneContract"] = "residential-door-plane-plus-visible-and-physical-leaf-centres-fit-measured-training-wall-aperture",
            ["hallwaySideDoorSockets"] = hallwaySideDoors,
            ["fixedSafeRoomDoorInterfaces"] = fixedSafeDoorInterfaces,
            ["staticDoorLeaves"] = staticDoorLeaves,
            ["doorSourceContract"] = "official-_DoorV2_BASE-prefab-or-validated-resident-template",
            ["openConnections"] = openings,
            ["interiorSightWindows"] = windows,
            ["enemySpawnMarkers"] = enemyMarkers,
            ["tacticalEnemyPositions"] = tacticalPositions,
            ["invalidTacticalEnemyPositions"] = invalidTacticalMarkers,
            ["invalidPveEnemySpawnPairClearance"] = invalidPveEnemySpawnPairClearance,
            ["minimumPveEnemySpawnSeparationMeters"] = minimumPveEnemySpawnSeparation,
            ["certifiedPveMaximumEnemies"] = CertifiedPveMaximumEnemies,
            ["authoredPveEnemyMarkerTarget"] = PveAuthoredEnemyMarkerTarget,
            ["tacticalEnemyFailureSummary"] = tacticalFailureSummary,
            ["distinctTacticalRoleTypes"] = tacticalRoleTypes,
            ["tacticalEnemyContract"] = "native-cover-backed-and-facing-likely-ingress-plus-12m-vanilla-wander-profile",
            ["playerSpawnMarkers"] = playerMarkers,
            ["pvePlayerSpawnMarkers"] = playerMarkers,
            ["pvpTeam1SpawnMarkers"] = pvpTeam1Markers.Length,
            ["pvpTeam2SpawnMarkers"] = pvpTeam2Markers.Length,
            ["pveSpawnSetMarkers"] = pveSpawnSetMarkers,
            ["pvpSpawnSetMarkers"] = pvpSpawnSetMarkers,
            ["spawnSetMarkers"] = spawnSetMarkers,
            ["pvpTeam1BaseRoomIndex"] = pvpTeam1Rooms.Length == 0 ? -1 : pvpTeam1Rooms[0],
            ["pvpTeam2BaseRoomIndex"] = pvpTeam2Rooms.Length == 0 ? -1 : pvpTeam2Rooms[0],
            ["pvpTeam1BaseRoomIndexes"] = new JArray(pvpTeam1Rooms),
            ["pvpTeam2BaseRoomIndexes"] = new JArray(pvpTeam2Rooms),
            ["pvpBaseGraphDistance"] = pvpBaseGraphDistance,
            ["pvpMinimumOpposingSpawnDistanceMeters"] = pvpMinimumOpposingSpawnDistance,
            ["pvpDirectOpposingLineOfSightPairs"] = pvpDirectOpposingLineOfSightPairs,
            ["invalidPvpSpawnMarkers"] = invalidPvpSpawnMarkers,
            ["invalidPvpSpawnCapsules"] = invalidPvpSpawnCapsules,
            ["invalidPvpSpawnPortalApproaches"] = invalidPvpSpawnPortalApproaches,
            ["invalidPvpSpawnDoorApproaches"] = invalidPvpSpawnDoorApproaches,
            ["invalidPvpSpawnFurnitureClearance"] = invalidPvpSpawnFurnitureClearance,
            ["invalidPvpSpawnRoomOwnership"] = invalidPvpSpawnRoomOwnership,
            ["invalidPvpSpawnFacing"] = invalidPvpSpawnFacing,
            ["invalidPvpSpawnPairClearance"] = invalidPvpSpawnPairClearance,
            ["pvpTeam1RoomDistributionValid"] = pvpTeam1RoomDistributionValid,
            ["pvpTeam2RoomDistributionValid"] = pvpTeam2RoomDistributionValid,
            ["pvpSectorRoomsDisjoint"] = pvpSectorRoomsDisjoint,
            ["pvpTeam1SectorConnected"] = pvpTeam1SectorConnected,
            ["pvpTeam2SectorConnected"] = pvpTeam2SectorConnected,
            ["pvpSpawnFailureDetails"] = new JArray(pvpSpawnFailureDetails),
            ["pvpSpawnPlacements"] = new JArray(pvpTeam1Markers.Select(marker => new JObject
            {
                ["team"] = 1,
                ["name"] = marker.name,
                ["roomIndex"] = PvpSpawnRoomIndex(marker),
                ["position"] = marker.position.x.ToString("F3") + "," + marker.position.y.ToString("F3") + "," +
                               marker.position.z.ToString("F3"),
                ["yawDegrees"] = marker.eulerAngles.y
            }).Concat(pvpTeam2Markers.Select(marker => new JObject
            {
                ["team"] = 2,
                ["name"] = marker.name,
                ["roomIndex"] = PvpSpawnRoomIndex(marker),
                ["position"] = marker.position.x.ToString("F3") + "," + marker.position.y.ToString("F3") + "," +
                               marker.position.z.ToString("F3"),
                ["yawDegrees"] = marker.eulerAngles.y
            }))),
            ["pvpSpawnContract"] = PvpSpawnContract,
            ["exfilMarkers"] = exfils,
            ["fixedSafeRoomMarkers"] = safeRoomMarkers,
            ["fallbackDirectionalLights"] = fallbackDirectionalLights,
            ["enabledDirectionalLights"] = enabledDirectionalLights,
            ["fixtureSpotLights"] = fixtureLights.Length,
            ["expectedFixtureSpotLights"] = expectedFixtureLights,
            ["fluorescentFixtureVisuals"] = fixtureVisuals.Length,
            ["invalidFluorescentFixtureVisuals"] = invalidFixtureVisuals,
            ["fixtureRoofMountGapMeters"] = WarehouseFixtureRoofGap,
            ["minimumFixtureRoofGapMeters"] = minimumFixtureRoofGap,
            ["maximumFixtureRoofGapMeters"] = maximumFixtureRoofGap,
            ["fixtureLightDropBelowTopMeters"] = WarehouseFixtureLightDrop,
            ["fluorescentLitEmission"] = KillHouseNativeMaterialBuilder.KillHouseFluorescentLitEmission,
            ["fluorescentDimEmission"] = KillHouseNativeMaterialBuilder.KillHouseFluorescentDimEmission,
            ["fluorescentExposureWeight"] = KillHouseNativeMaterialBuilder.KillHouseFluorescentExposureWeight,
            ["nonFixturePointLights"] = pointLights,
            ["invalidFixtureLights"] = invalidFixtureLights,
            ["shadowCastingFixtureLights"] = shadowCastingFixtureLights,
            ["expectedShadowCastingFixtureLights"] = litRooms,
            ["litRoomCount"] = litRooms,
            ["dimRoomCount"] = dimRooms,
            ["darkRoomCount"] = darkRooms,
            ["safeRoomLit"] = safeRoomLit == 1,
            ["indoorVolumeValid"] = indoorVolumeValid,
            ["blackWarehouseEnvironment"] = blackWarehouseEnvironment,
            ["indoorVolumeDonor"] = "Assets/Scenes/PVP Woods Warehouse.unity::Global Volume Profile 1",
            ["indoorLutDonor"] = "sharedassets2.assets::AgX - PunchyPowerfulMix",
            ["lightingContract"] = "black-sky-zero-ambient-zero-reflection-plus-disabled-directional-sentinel-plus-visible-downward-facing-vanilla-fluorescent-tubes-at-307.2-lit-9.6-dim-zero-dark-surface-emission-plus-lit-dim-dark-spot-fixtures-plus-one-soft-shadow-owner-per-lit-room-plus-exact-unchanged-pvp-warehouse-bloom-scatter-and-screen-space-lens-flare-streak-contract",
            ["visibleBuiltinPrimitiveMeshes"] = primitiveMeshes,
            ["nativeAssetOnly"] = true,
            ["fixedSafeRoomModule"] = "KH_SAFE_ROOM_V1",
            ["closedPrimaryLoop"] = true,
            ["allSecondaryRoomsReachableFromPrimaryLoop"] = true,
            ["graphConnectionCount"] = graphEdges,
            ["graphLoopRank"] = graphLoopRank,
            ["staggeredPortalOffsets"] = true,
            ["distinctPortalOffsetsMeters"] = distinctPortalOffsets,
            ["alignedOpposingPortalPairs"] = alignedOpposingPortalPairs,
            ["maximumAxialPortalRun"] = maximumAxialPortalRun,
            ["portalOffsetMetersByConnection"] = new JArray(connections.Select(plan => new JObject
            {
                ["connection"] = plan.Key,
                ["axis"] = plan.DirectionFromA.x != 0 ? "X" : "Z",
                ["offset"] = plan.PortalOffset,
                ["portal"] = plan.Portal.ToString()
            })),
            ["passed"] = passed,
            ["liveGameplayVerified"] = false,
            ["releaseAllowed"] = false
        };
    }

    private static bool DoorSocketMatchesNativeOpening(Transform socket)
    {
        if (socket == null || socket.parent == null ||
            !socket.name.StartsWith("DOORV2_SOCKET_", StringComparison.Ordinal)) return false;
        string key = socket.name.Substring("DOORV2_SOCKET_".Length);
        Transform wall = socket.parent.Find("NATIVE_DoorWall_" + key);
        Transform shell = socket.Find("NATIVE_DOORV2_SHELL");
        if (wall == null || shell == null) return false;
        float normalAlignment = Mathf.Abs(Vector3.Dot(socket.forward.normalized, wall.right.normalized));
        float shellAlignment = Mathf.Abs(Vector3.Dot(shell.forward.normalized, wall.right.normalized));
        Transform placeholder = shell.Find("Door Pivot and rigidbody/Door Model/cubey model/PLACEHOLDER DOOR MODEL");
        Transform interior = shell.Find("Door Pivot and rigidbody/Door Model/Door_interior");
        Transform pivot = shell.Find("Door Pivot and rigidbody");
        BoxCollider physicalLeaf = placeholder == null ? null : placeholder.GetComponent<BoxCollider>();
        MeshFilter visibleLeaf = interior == null ? null : interior.GetComponent<MeshFilter>();
        MeshFilter wallMesh = wall.GetComponentInChildren<MeshFilter>(true);
        if (physicalLeaf == null || visibleLeaf == null || visibleLeaf.sharedMesh == null ||
            wallMesh == null || wallMesh.sharedMesh == null) return false;
        bool animatedRuggedLeaf = pivot != null && interior.IsChildOf(pivot) &&
                                  visibleLeaf.sharedMesh.name.StartsWith("SM_Door_2_LOD0", StringComparison.Ordinal) &&
                                  shell.GetComponentsInChildren<Transform>(true).All(value =>
                                      !value.name.StartsWith("SHADOW BLOCKER Door_interior", StringComparison.Ordinal));
        Vector3 physicalCenter = physicalLeaf.transform.TransformPoint(physicalLeaf.center);
        Vector3 visibleCenter = visibleLeaf.transform.TransformPoint(visibleLeaf.sharedMesh.bounds.center);
        Vector3 openingCenter = wallMesh.transform.TransformPoint(wallMesh.sharedMesh.bounds.center) +
                                wall.forward * DoorwayOpeningTangentOffset;
        float physicalError = HorizontalDistanceInWallPlane(physicalCenter, openingCenter, wall);
        float visibleError = HorizontalDistanceInWallPlane(visibleCenter, openingCenter, wall);
        float visiblePhysicalError = Vector2.Distance(
            new Vector2(visibleCenter.x, visibleCenter.z), new Vector2(physicalCenter.x, physicalCenter.z));
        bool hingeOffset = Mathf.Abs(shell.localPosition.x + DoorHingeToLeafCenter) <= .005f &&
                           Mathf.Abs(shell.localPosition.y) <= .005f && Mathf.Abs(shell.localPosition.z) <= .005f;
        return animatedRuggedLeaf && normalAlignment >= .999f && shellAlignment >= .999f &&
               Quaternion.Angle(socket.rotation, shell.rotation) <= .1f && hingeOffset &&
               physicalError <= .035f && visibleError <= .035f && visiblePhysicalError <= .035f;
    }

    private static float HorizontalDistanceInWallPlane(Vector3 value, Vector3 expected, Transform wall)
    {
        Vector3 delta = value - expected;
        float normal = Vector3.Dot(delta, wall.right.normalized);
        float tangent = Vector3.Dot(delta, wall.forward.normalized);
        return Mathf.Sqrt(normal * normal + tangent * tangent);
    }

    private static int CountAlignedOpposingPortalPairs(IEnumerable<ConnectionPlan> connections)
    {
        var records = new Dictionary<string, List<Tuple<int, float>>>();
        foreach (ConnectionPlan plan in connections)
        {
            RecordPortalAssignment(records, plan.RoomA, plan.DirectionFromA, plan.PortalOffset);
            RecordPortalAssignment(records, plan.RoomB, -plan.DirectionFromA, plan.PortalOffset);
        }
        int aligned = 0;
        foreach (List<Tuple<int, float>> values in records.Values)
        {
            for (int first = 0; first < values.Count; first++)
                for (int second = first + 1; second < values.Count; second++)
                    if (values[first].Item1 == -values[second].Item1 &&
                        Mathf.Abs(values[first].Item2 - values[second].Item2) < .1f)
                        aligned++;
        }
        return aligned;
    }

    private static int MaximumAxialPortalRun(IEnumerable<ConnectionPlan> connections, Vector3[] roomCenters)
    {
        int[] counts = connections.GroupBy(plan =>
        {
            float tangent = plan.DirectionFromA.x != 0
                ? roomCenters[plan.RoomA].z + plan.PortalOffset
                : roomCenters[plan.RoomA].x + plan.PortalOffset;
            return PortalAxisKey(plan.DirectionFromA, tangent);
        }).Select(group => group.Count()).ToArray();
        return counts.Length == 0 ? 0 : counts.Max();
    }

    private static int CountGraphEdges(Layout layout)
    {
        var lookup = layout.Cells.Select((cell, index) => new { cell, index }).ToDictionary(item => item.cell, item => item.index);
        int edges = 0;
        for (int index = 0; index < layout.Cells.Length; index++)
        {
            foreach (Vector2Int direction in new[] { Vector2Int.right, Vector2Int.up })
            {
                if (!lookup.TryGetValue(layout.Cells[index] + direction, out int neighbor)) continue;
                bool fixedSafeCrossEdge = (index == 0 || neighbor == 0) &&
                    !(index < layout.BaseCycleCount && neighbor < layout.BaseCycleCount &&
                      CycleEdge(index, neighbor, layout.BaseCycleCount) >= 0);
                if (!fixedSafeCrossEdge) edges++;
            }
        }
        return edges;
    }

    private static float[] GraphConnectionGaps(Layout layout, Vector2[] roomSizes, Vector3[] roomCenters)
    {
        var lookup = layout.Cells.Select((cell, index) => new { cell, index }).ToDictionary(item => item.cell, item => item.index);
        var gaps = new List<float>();
        for (int index = 0; index < layout.Cells.Length; index++)
        {
            foreach (Vector2Int direction in new[] { Vector2Int.right, Vector2Int.up })
            {
                if (!lookup.TryGetValue(layout.Cells[index] + direction, out int neighbor)) continue;
                bool fixedSafeCrossEdge = (index == 0 || neighbor == 0) &&
                    !(index < layout.BaseCycleCount && neighbor < layout.BaseCycleCount &&
                      CycleEdge(index, neighbor, layout.BaseCycleCount) >= 0);
                if (fixedSafeCrossEdge) continue;
                gaps.Add(ConnectionGap(roomCenters[index], roomSizes[index], roomCenters[neighbor],
                    roomSizes[neighbor], direction));
            }
        }
        return gaps.ToArray();
    }

    private static void WriteVariantReport(JObject report, int index)
    {
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "evidence", "scene-validation"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "KH" + (index + 1).ToString("00") + "_validation.json"), report.ToString() + Environment.NewLine);
    }

    private static void WriteAggregateReport(JArray reports)
    {
        string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "evidence", "killhouse_scene_validation.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        JObject document = new JObject
        {
            ["schema"] = "vektor-killhouse/aggregate-scene-validation@19",
            ["generatedUtc"] = DateTime.UtcNow.ToString("O"),
            ["sceneCount"] = reports.Count,
            ["uniqueCycleCount"] = reports.Select(item => item.Value<string>("cycleMoves")).Distinct().Count(),
            ["uniqueOrderedRoomSequenceCount"] = reports.Select(item => item.Value<string>("orderedRoomTypeSequence")).Distinct().Count(),
            ["uniquePortalPatternCount"] = reports.Select(item => item.Value<string>("cyclePortalPattern")).Distinct().Count(),
            ["uniqueSpatialMotifCount"] = reports.Select(item => item.Value<string>("spatialMotif")).Distinct().Count(),
            ["allPassed"] = reports.All(item => item.Value<bool>("passed")) &&
                             reports.All(item => item.Value<int>("nativeFurnitureRenderers") >= 12 &&
                                                item.Value<int>("enemySpawnMarkers") ==
                                                PveAuthoredEnemyMarkerTarget &&
                                                item.Value<int>("invalidPveEnemySpawnPairClearance") == 0 &&
                                                item.Value<float>("minimumPveEnemySpawnSeparationMeters") >=
                                                PveEnemySpawnPairClearance - .001f &&
                                                item.Value<int>("certifiedPveMaximumEnemies") ==
                                                CertifiedPveMaximumEnemies &&
                                                item.Value<int>("nativeFurnitureMeshFamilies") >= 10 &&
                                                item.Value<int>("invalidFurnitureTextureClosures") == 0 &&
                                                item.Value<int>("centerRoomTableProps") >= 1 &&
                                                item.Value<int>("centerRoomSofaProps") >= 1 &&
                                                item.Value<int>("invalidCenterRoomProps") == 0 &&
                                                item.Value<int>("centerRoomPropPortalCorridorConflicts") == 0 &&
                                                item.Value<int>("centerRoomPropCirculationFailures") == 0 &&
                                                item.Value<int>("centerRoomPropTacticalCapsuleConflicts") == 0 &&
                                                item.Value<int>("pvePlayerSpawnMarkers") == 4 &&
                                                item.Value<int>("pvpTeam1SpawnMarkers") == PvpSpawnsPerTeam &&
                                                item.Value<int>("pvpTeam2SpawnMarkers") == PvpSpawnsPerTeam &&
                                                item.Value<int>("pveSpawnSetMarkers") == 1 &&
                                                item.Value<int>("pvpSpawnSetMarkers") == 1 &&
                                                item.Value<int>("spawnSetMarkers") == 2 &&
                                                item.Value<int>("invalidPvpSpawnMarkers") == 0 &&
                                                item.Value<int>("invalidPvpSpawnCapsules") == 0 &&
                                                item.Value<int>("invalidPvpSpawnPortalApproaches") == 0 &&
                                                item.Value<int>("invalidPvpSpawnDoorApproaches") == 0 &&
                                                item.Value<int>("invalidPvpSpawnFurnitureClearance") == 0 &&
                                                 item.Value<int>("invalidPvpSpawnRoomOwnership") == 0 &&
                                                 item.Value<int>("invalidPvpSpawnFacing") == 0 &&
                                                 item.Value<int>("invalidPvpSpawnPairClearance") == 0 &&
                                                 item.Value<bool>("pvpTeam1RoomDistributionValid") &&
                                                 item.Value<bool>("pvpTeam2RoomDistributionValid") &&
                                                 item.Value<bool>("pvpSectorRoomsDisjoint") &&
                                                 item.Value<bool>("pvpTeam1SectorConnected") &&
                                                 item.Value<bool>("pvpTeam2SectorConnected") &&
                                                 item.Value<int>("pvpBaseGraphDistance") >=
                                                 PvpMinimumSectorGraphDistance &&
                                                 item.Value<float>("pvpMinimumOpposingSpawnDistanceMeters") >=
                                                 PvpMinimumOpposingDistance &&
                                                 item.Value<int>("pvpDirectOpposingLineOfSightPairs") == 0 &&
                                                 (item["pvpSpawnPlacements"] as JArray)?.Count ==
                                                 PvpSpawnsPerTeam * 2 &&
                                                 string.Equals(item.Value<string>("pvpSpawnContract"),
                                                     PvpSpawnContract, StringComparison.Ordinal)) &&
                            reports.Select(item => item.Value<string>("spatialMotif")).Distinct().Count() == reports.Count,
            ["variants"] = reports,
            ["liveGameplayVerified"] = false,
            ["releaseAllowed"] = false
        };
        File.WriteAllText(path, document.ToString() + Environment.NewLine);
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
