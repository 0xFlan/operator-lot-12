using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OperatorModAPI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using Mirror;
using InteractionObject = RootMotion.FinalIK.InteractionObject;
using InteractionTarget = RootMotion.FinalIK.InteractionTarget;
using FullBodyBipedEffector = RootMotion.FinalIK.FullBodyBipedEffector;
using GridGraph = Pathfinding.GridGraph;
using GraphMask = Pathfinding.GraphMask;
using NavmeshCut = Pathfinding.NavmeshCut;
using NNConstraint = Pathfinding.NNConstraint;
using NodeLink2 = Pathfinding.NodeLink2;
using NumNeighbours = Pathfinding.NumNeighbours;
using PathfindingTag = Pathfinding.PathfindingTag;
using RVOSimulator = Pathfinding.RVO.RVOSimulator;

namespace OperatorKillHouse;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("operator.modded-operations", "0.3.30")]
[BepInDependency("operator.modapi", "0.2.0-alpha.7")]
public sealed class OperatorKillHousePlugin : BasePlugin
{
    public const string PluginGuid = "operator.vektor-killhouse";
    public const string PluginName = "LOT 12: FALSE WALL";
    public const string PluginVersion = "0.1.18";

    private const string ExactUnityVersion = "6000.3.8f1";
    private const float IndoorFlashlightMultiplier = 6f;
    private const float IndoorVisibleLaserMultiplier = 6f;
    private const float IndoorVisibleLaserBeamEmissionMultiplier = 4f;
    // Normal vision needs a stronger source than NVG because the NVG compositor already
    // amplifies the dot. Keep both paths baseline-backed and below the rejected 2.5x washout.
    private const float IndoorReticleNormalBrightnessMultiplier = 2f;
    private const float IndoorReticleNvgBrightnessMultiplier = 1f;
    private const float IndoorReticleNormalBrightnessCap = 3840f;
    private const float IndoorReticleNvgBrightnessCap = 900f;
    // Ultimate Scope Shaders misspells this exact property. Increasing it linearly enlarges
    // the sampled dot. Only normal vision is enlarged; the CompM4 NVG donor is already 16.5.
    private const string HwsReticleShaderName = "Ultimate Scope Shaders/HolographicSight";
    private const string HwsReticleSizeProperty = "_Retical_Size";
    private const float IndoorReticleNormalSizeMultiplier = 1.5f;
    private const float HwsReticleEligibleSizeMinimum = 5f;
    private const float HwsReticleEligibleSizeMaximum = 25f;
    private const float HwsReticleNormalSizeCap = 22.56f;
    private const float KillHouseFluorescentLitEmission = 307.2f;
    private const float KillHouseFluorescentDimEmission = 9.6f;
    private const float KillHouseFluorescentExposureWeight = 0f;
    private const float KillHouseFluorescentIntensityUnit = 1f;
    private const string MapMarker = "MAP_ID_community.vektor-modular-killhouse.modular-killhouse";
    private const string PveSpawnSetMarker = "SPAWN_SET_killhouse-pve";
    private const string PvpSpawnSetMarker = "SPAWN_SET_killhouse-pvp";
    private const string SafeRoomMarker = "FIXED_SAFE_ROOM_KH_SAFE_ROOM_V1";
    private const string ResidentShaderName = "HDRP/Lit";
    private const string MilkLitTemplateShaderName = "MilkShaders/Lit-Template";
    private const string RuntimeGraphName = "MOD_VektorKillHouse_RuntimeNavigation";
    private const string ReadyMarkerName = "RUNTIME_VEKTOR_KILLHOUSE_READY";
    private const string FailureMarkerName = "RUNTIME_VEKTOR_KILLHOUSE_GATE_FAILED";
    private const string ModdedOperationsReadyMarkerName = "MODDED_OPERATIONS_RUNTIME_CONTRACT_READY";
    private const string ModdedOperationsFailureMarkerName = "MODDED_OPERATIONS_RUNTIME_CONTRACT_FAILED";
    private const int CertifiedPveMaximumEnemies = 60;
    private const int MinimumAuthoredPveEnemyMarkers = 72;
    private const float MinimumPveMarkerPlanarSeparationMeters = 2f;
    private const string DoorShellName = "NATIVE_DOORV2_SHELL";
    private const string DoorAudioBankName = "NATIVE_DOORV2_AUDIO_BANK";
    private const int NativeOpaqueRenderQueue = 2225;
    private const float DoorwayOpeningTangentOffset = -.0391f;
    private const float DoorHingeToLeafCenter = .50283f;
    private const float DoorCenterTolerance = .035f;
    private const float WarehouseRoofHeight = 11.35f;
    private const float WarehouseFixtureRoofGap = .04f;
    private const float WarehouseFixtureLightDrop = .18f;
    private const float WarehouseGroundElevation = -.015f;
    private const float WarehouseGroundSourceWidth = 5.425254f;
    private const float WarehouseGroundSourceDepth = 4.129904f;
    private const float WarehouseGroundMinimumApron = 3.98f;
    private const float NavigationNodeSize = .4f;
    private const float NavigationMarkerClearance = 1.35f;
    private const float CenterRoomFurnitureWallInset = .82f;
    private const float CenterRoomTacticalCapsuleRadius = .3f;
    private const int ApplyDelayFrames = 2;
    private const int NavigationTeardownAuditRetryCadenceFrames = 15;
    private const int NavigationTeardownAuditHardDeadlineFrames = 900;
    private const int OpticIdentityProbeFrames = 120;
    private const uint OfficialDoorV2AssetId = 3964291274u;
    private static readonly string[] ExactDonorShaderPasses =
    {
        "DistortionVectors", "MOTIONVECTORS", "TransparentDepthPrepass",
        "TransparentDepthPostpass", "TransparentBackface", "RayTracingPrepass"
    };
    private static readonly string[] ExactDonorOverrideTags = { "MotionVector" };

    private static readonly HashSet<string> ScenePaths = new HashSet<string>(StringComparer.Ordinal)
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

    private static readonly IReadOnlyDictionary<string, NativeMaterialProfile> NativeMaterialProfiles =
        new Dictionary<string, NativeMaterialProfile>(StringComparer.Ordinal)
        {
            ["Bed"] = P(.8455882f, 1f, .5f, 1f),
            ["PillowLarge"] = P(.88235295f, 1f, .22400001f, 1f),
            ["PillowSmall"] = P(.9117647f, 1f, .234f, 1f),
            ["Bedroom_Closets"] = P(1f, 1f, .5f, 1f),
            ["Carpet_B"] = P(1f, 0f, .124f, 1f),
            ["ChipBoardShader"] = P(.8f, 0f, .18f, 1.35f, matteArchitectural: true),
            ["Couch_Fabric"] = P(.8897059f, 1f, .5f, 1f,
                residentShaderName: MilkLitTemplateShaderName),
            ["Door_Breached"] = P(.79073066f, 0f, .14142138f, 1f),
            ["MI_DoorsWindows"] = P(1f, 0f, .28f, 1f),
            ["Fireplace"] = P(1f, 1f, .5f, 1f),
            ["Floor"] = P(1f, 0f, .5f, 1f),
            ["In_Floor_Basement"] = P(1f, 0f, .14f, .8f, matteArchitectural: true,
                residentShaderName: MilkLitTemplateShaderName),
            ["In_Floor_Carpet"] = P(.9632353f, 0f, 0f, 1f, true, false),
            ["Kitchen_Cabinet_Marble"] = P(1f, 1f, .5f, 1f, false, true),
            ["Kitchen_Cabinet_Wood"] = P(1f, 1f, .5f, 1f),
            ["Kitchen_TableChair"] = P(.83823526f, 1f, .5f, 1f,
                residentShaderName: MilkLitTemplateShaderName),
            ["Lamps_C_on__cagville"] = P(1f, .08f, .22f, 1f, true, true,
                KillHouseFluorescentLitEmission, true),
            ["RM_Steel_smooth"] = P(.58431375f, .12f, .33f, 0f, false, true,
                baseGreen: .6156863f, baseBlue: .6431373f),
            ["PlyWoodShader"] = P(.8f, 0f, .18f, 1.35f, matteArchitectural: true),
            ["Toilet_House"] = P(1f, 1f, .5f, 1f),
            ["WorkDesk"] = P(1f, 1f, .5f, 1f)
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedBaseTextureNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Bed"] = "Bed_BaseColor",
            ["PillowLarge"] = "PillowLarge_BaseColor",
            ["PillowSmall"] = "PillowSmall_BaseColor",
            ["Bedroom_Closets"] = "Bedroom_Closets_BaseColor",
            ["Carpet_B"] = "Carpet_B_BaseColor",
            ["ChipBoardShader"] = "Chipboard_D",
            ["Couch_Fabric"] = "Couch_Fabric_BaseColor",
            ["Door_Breached"] = "breached_low_Door_Breached_BaseMap",
            ["MI_DoorsWindows"] = "T_Doors_Windows_BC",
            ["Fireplace"] = "Fireplace_BaseColor",
            ["Floor"] = "Albedo_4K__wckscdz",
            ["In_Floor_Basement"] = "Floor_Basement_BaseColor",
            ["In_Floor_Carpet"] = "In_Floorcarpet_BaseColor",
            ["Kitchen_Cabinet_Marble"] = "Kitchen_Cabinet_Marble_BaseColor",
            ["Kitchen_Cabinet_Wood"] = "Kitchen_Cabinet_Wood_BaseColor",
            ["Kitchen_TableChair"] = "Kitchen_TableChair_BaseColor",
            ["Lamps_C_on__cagville"] = "Lamps_C_BaseColor",
            ["RM_Steel_smooth"] = "RM steel oxidized distant D",
            ["PlyWoodShader"] = "Plywood_D",
            ["Toilet_House"] = "Toilet_House_BaseColor",
            ["WorkDesk"] = "WorkDesk_BaseColor"
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedNormalTextureNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Bed"] = "Bed_Normal",
            ["PillowLarge"] = "PillowLarge_Normal",
            ["PillowSmall"] = "PillowSmall_Normal",
            ["Bedroom_Closets"] = "Bedroom_Closets_Normal",
            ["Carpet_B"] = "Carpet_A_Normal",
            ["ChipBoardShader"] = "Chipboard_N",
            ["Couch_Fabric"] = "Couch_Fabric_Normal",
            ["Door_Breached"] = "breached_low_Door_Breached_Normal",
            ["MI_DoorsWindows"] = "T_Doors_Windows_N_ConvertedOglNormal",
            ["Fireplace"] = "Fireplace_Normal",
            ["Floor"] = "Normal_4K__wckscdz",
            ["In_Floor_Basement"] = "Floor_Basement_Normal",
            ["In_Floor_Carpet"] = "In_Floorcarpet_Normal",
            ["Kitchen_Cabinet_Wood"] = "Kitchen_Cabinet_Wood_Normal",
            ["Kitchen_TableChair"] = "Kitchen_TableChair_Normal",
            ["Lamps_C_on__cagville"] = "Lamps_C_Normal",
            ["PlyWoodShader"] = "Plywood_N",
            ["Toilet_House"] = "Toilet_House_Normal",
            ["WorkDesk"] = "WorkDesk_Normal"
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedMaskTextureNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Bed"] = "Bed_MaskMap",
            ["PillowLarge"] = "PillowLarge_MaskMap",
            ["PillowSmall"] = "PillowSmall_MaskMap",
            ["Bedroom_Closets"] = "Bedroom_Closets_MaskMap",
            ["Carpet_B"] = "Carpet_A_MaskMap",
            ["ChipBoardShader"] = "ChipBoardShader_MaskMap",
            ["Couch_Fabric"] = "Couch_Fabric_MaskMap",
            ["Door_Breached"] = "breached_low_Door_Breached_MaskMap",
            ["MI_DoorsWindows"] = "T_Doors_Windows_ORM_ConvertedMask",
            ["Fireplace"] = "Fireplace_MaskMap",
            ["Floor"] = "Masks_4K__wckscdz",
            ["In_Floor_Basement"] = "Floor_Basement_MaskMap",
            ["Kitchen_Cabinet_Marble"] = "Kitchen_Cabinet_Marble_MaskMap",
            ["Kitchen_Cabinet_Wood"] = "Kitchen_Cabinet_Wood_MaskMap",
            ["Kitchen_TableChair"] = "Kitchen_TableChair_MaskMap",
            ["Lamps_C_on__cagville"] = "Lamps_C_MaskMap",
            ["RM_Steel_smooth"] = "RM steel oxidized G",
            ["PlyWoodShader"] = "PlyWoodShader_MaskMap",
            ["Toilet_House"] = "Toilet_House_MaskMap",
            ["WorkDesk"] = "WorkDesk_MaskMap"
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedEmissiveTextureNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Lamps_C_on__cagville"] = "Lamps_C_Emissive"
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedDetailTextureNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["In_Floor_Basement"] = "Concrete1_DetailMap"
        };

    private static readonly IReadOnlyDictionary<string, string[]> FurnitureMaterialSlotsByMesh =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Bed_queen"] = new[] { "Bed" },
            ["Pillow_small"] = new[] { "PillowSmall" },
            ["Pillow_large"] = new[] { "PillowLarge" },
            ["Bookshelf"] = new[] { "Fireplace" },
            ["Couch_2seat"] = new[] { "Couch_Fabric" },
            ["Kitcabinet_full_fridge"] = new[] { "Kitchen_Cabinet_Wood" },
            ["Kitcabinet_low_1x_A"] = new[] { "Kitchen_Cabinet_Wood", "Kitchen_Cabinet_Marble" },
            ["Kitchen_table_large"] = new[] { "Kitchen_TableChair" },
            ["Sidetable_A"] = new[] { "Bedroom_Closets" },
            ["Sidetable_A_drawer"] = new[] { "Bedroom_Closets" },
            ["T_sink_door_L"] = new[] { "Bedroom_Closets" },
            ["T_sink_door_R"] = new[] { "Bedroom_Closets" },
            ["T_sink"] = new[] { "Bedroom_Closets", "Toilet_House" },
            ["T_toilet"] = new[] { "Toilet_House" },
            ["T_toilet_lid"] = new[] { "Toilet_House" },
            ["T_toilet_seat"] = new[] { "Toilet_House" },
            ["Workdesk_door_L"] = new[] { "WorkDesk" },
            ["Workdesk_door_R"] = new[] { "WorkDesk" },
            ["Workdesk_solo"] = new[] { "WorkDesk" }
        };

    private static readonly HashSet<string> FurnitureMaterialProfileNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Bed", "PillowLarge", "PillowSmall", "Bedroom_Closets", "Couch_Fabric", "Fireplace",
        "Kitchen_Cabinet_Marble", "Kitchen_Cabinet_Wood", "Kitchen_TableChair",
        "Toilet_House", "WorkDesk"
    };

    private static readonly IReadOnlyDictionary<string, FurnitureSurfaceProfile> FurnitureSurfaceProfiles =
        new Dictionary<string, FurnitureSurfaceProfile>(StringComparer.Ordinal)
        {
            ["Bed"] = Fsp(1f, 1f, 0f, 1f, 1f),
            ["PillowLarge"] = Fsp(1f, 1f, 0f, 1f, 1f),
            ["PillowSmall"] = Fsp(1f, 1f, 0f, 1f, 1f),
            ["Bedroom_Closets"] = Fsp(0f, .22741756f, 0f, 1f, .919f),
            ["Couch_Fabric"] = Fsp(.33685863f, .588395f, 0f, 1f, .3f),
            ["Fireplace"] = Fsp(1f, .541779f, .5f, 1f, .5f),
            ["Kitchen_Cabinet_Marble"] = Fsp(0f, .62526333f, 0f, 1f, 1f),
            ["Kitchen_Cabinet_Wood"] = Fsp(0f, .29595914f, 0f, 1f, 1f),
            ["Kitchen_TableChair"] = Fsp(1f, .5f, 0f, 1f, .5f, receivesSsr: 0f),
            ["Toilet_House"] = Fsp(1f, .83155084f, 0f, 1f, .814f),
            ["WorkDesk"] = Fsp(0f, .69251335f, 0f, 1f, 1f)
        };

    private static readonly IReadOnlyDictionary<string, FileFingerprint> ExactFiles =
        new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPERATOR.exe"] = new FileFingerprint(667648, "F8158D7939937CB26C2DBEF0A127E82F969E5CC72BE58E62BB78F39B179FF53D"),
            ["GameAssembly.dll"] = new FileFingerprint(115185152, "D4347448524D79A7E367F2B22D66BB3A21E1F3733D32ABA37FC7E3E270A620DE"),
            ["UnityPlayer.dll"] = new FileFingerprint(35734960, "D935627D3AC843293F1C51EE9D85538191F0CB98DEF4271C1A29302B52A021D4")
        };

    private enum RuntimeContractState
    {
        None,
        Pending,
        Ready,
        Failed
    }

    private enum NavigationTeardownAuditResult
    {
        Passed,
        Survivors,
        Ambiguous
    }

    private readonly Dictionary<int, Material> runtimeMaterialsBySourceInstance = new Dictionary<int, Material>();
    private readonly HashSet<int> ownedRuntimeMaterialIds = new HashSet<int>();
    private readonly Dictionary<int, SuspendedDirectionalLight> suspendedDirectionalLights =
        new Dictionary<int, SuspendedDirectionalLight>();
    private ManualLogSource log;
    private GameObject driverObject;
    private UnityAction<Scene, LoadSceneMode> sceneLoadedCallback;
    private UnityAction<Scene> sceneUnloadedCallback;
    private int pendingSceneHandle;
    private int applyNotBeforeFrame = -1;
    private RuntimeContractState runtimeContractState;
    private int runtimeContractSceneHandle;
    private GameObject ownedRuntimeReadyMarker;
    private GameObject ownedFrameworkReadyMarker;
    private GameObject ownedRuntimeFailureMarker;
    private GameObject ownedFrameworkFailureMarker;
    private GridGraph runtimeNavigationGraph;
    private AstarPath runtimeNavigationAstar;
    private GameObject runtimeAstarHost;
    private RVOSimulator runtimeRvoSimulator;
    private bool runtimeOwnsAstarHost;
    private int runtimeNavigationOwnerSceneHandle;
    private int runtimeNavigationAstarHostInstanceId;
    private int runtimeNavigationRvoInstanceId;
    private bool runtimeOwnsRvoSimulator;
    private bool runtimeNavigationAstarHostReferenceCaptured;
    private bool runtimeNavigationRvoReferenceCaptured;
    private long pendingNavigationTeardownAuditFrame = -1;
    private int navigationTeardownSceneHandle;
    private int navigationTeardownAstarHostInstanceId;
    private int navigationTeardownRvoInstanceId;
    private bool navigationTeardownOwnedAstarHost;
    private bool navigationTeardownOwnedRvoSimulator;
    private bool navigationTeardownHadRuntimeGraph;
    private GridGraph navigationTeardownRuntimeGraph;
    private GameObject navigationTeardownAstarHost;
    private RVOSimulator navigationTeardownRvoSimulator;
    private bool navigationTeardownRuntimeGraphReferenceCaptured;
    private bool navigationTeardownAstarHostReferenceCaptured;
    private bool navigationTeardownRvoReferenceCaptured;
    private int navigationTeardownAuditAttempts;
    private long navigationTeardownAuditStartedFrame = -1;
    private long navigationTeardownAuditDeadlineFrame = -1;
    private long navigationTeardownAuditGeneration;
    private long navigationTeardownAuditGenerationCounter;
    private string navigationTeardownAuditLastDetail = string.Empty;
    private string runtimeContractFailurePublicationErrors = string.Empty;
    private bool exactEnvironmentAccepted;
    private bool lifecycleStopping;
    private bool residentDoorAuditLogged;
    private bool aiSightOcclusionPending;
    private bool aiSightOcclusionPassed;
    private int nextAiSightAuditFrame;
    private bool opticAuditPending;
    private int nextOpticAuditFrame;
    private int nextOpticIdentityProbeFrame;
    private ulong equippedWeaponIdentityFingerprint;
    private bool equippedWeaponIdentityInitialized;
    private string lastOpticAuditSignature = string.Empty;
    private readonly HashSet<int> enhancedHwsReticles = new HashSet<int>();
    private readonly HashSet<int> enhancedHwsReticleSizeRenderers = new HashSet<int>();
    private readonly HashSet<int> enhancedLaserLights = new HashSet<int>();
    private readonly HashSet<int> enhancedVisibleLaserLights = new HashSet<int>();
    private readonly HashSet<int> enhancedVisibleLaserBeams = new HashSet<int>();
    private readonly List<HwsReticleBoostState> hwsReticleBoostStates = new List<HwsReticleBoostState>();
    private readonly List<HwsReticleSizeState> hwsReticleSizeStates = new List<HwsReticleSizeState>();
    private readonly List<VisibleIrLaserBoostState> visibleIrLaserBoostStates = new List<VisibleIrLaserBoostState>();
    private readonly List<VisibleLaserLightBoostState> visibleLaserLightBoostStates = new List<VisibleLaserLightBoostState>();
    private readonly List<BoostedLaserBeamState> boostedLaserBeamStates = new List<BoostedLaserBeamState>();
    private GlobalFlashLightMultiplier globalFlashlightMultiplier;
    private bool warehouseEnvironmentOverrideApplied;
    private Material savedSkybox;
    private AmbientMode savedAmbientMode;
    private Color savedAmbientLight;
    private float savedAmbientIntensity;
    private float savedReflectionIntensity;

    private static readonly HashSet<string> ForbiddenStandaloneFurnitureMeshes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Book_set", "Book_set_bookshelf_A", "Sofa_A", "Sofa_B", "T_bathtub", "D_TV_standing"
        };

    // These exact active child meshes carry UV0/UV1 but no vertex-color channel in sharedassets4.
    private static readonly HashSet<string> FurnitureMeshesWithoutVertexColor =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Pillow_small", "Pillow_large", "Sidetable_A_drawer", "T_sink_door_L", "T_sink_door_R",
            "T_toilet_lid", "T_toilet_seat", "Workdesk_door_L", "Workdesk_door_R"
        };

    private static readonly IReadOnlyDictionary<string, RuntimeWallFurnitureContract> RuntimeWallFurnitureContracts =
        new Dictionary<string, RuntimeWallFurnitureContract>(StringComparer.Ordinal)
        {
            ["Bed_queen"] = new RuntimeWallFurnitureContract("Bed_queen",
                "WALL_BACKED_PROP_PROVENANCE_level4_GO360_MF20235_MR15441_sharedassets4_Mesh1141_MR_SHA256_ae0fb43c7902b028d279f8b4fd91d9cb0ab9603055f1093188ac6cf702dce51a",
                "Bed_COL", false, new[]
                {
                    RuntimeChild("Pillow_small", "Pillow_small", new[] { "PillowSmall" },
                        new Vector3(.307000011f, .707000017f, -.644999981f),
                        new Quaternion(.251538515f, -.058406256f, -.012960499f, .965996504f),
                        collisionMeshName: "PillowSmall_COL", vertexCount: 233, indexCount: 768),
                    RuntimeChild("Pillow_small", "Pillow_small", new[] { "PillowSmall" },
                        new Vector3(-.180000007f, .707000017f, -.649999976f),
                        new Quaternion(.317480564f, .097556531f, -.053890042f, .941692472f),
                        collisionMeshName: "PillowSmall_COL", vertexCount: 233, indexCount: 768),
                    RuntimeChild("Pillow_large", "Pillow_large", new[] { "PillowLarge" },
                        new Vector3(-.309999943f, .608999968f, -.897999763f),
                        new Quaternion(.063810684f, -.005018107f, -.022382082f, .997698367f),
                        collisionMeshName: "PillowLarge_COL", vertexCount: 334, indexCount: 1332),
                    RuntimeChild("Pillow_large", "Pillow_large", new[] { "PillowLarge" },
                        new Vector3(.404000014f, .621000051f, -.85799998f),
                        new Quaternion(.063628756f, .006954471f, -.012878145f, .997866333f),
                        collisionMeshName: "PillowLarge_COL", vertexCount: 334, indexCount: 1332)
                }),
            ["Bookshelf"] = new RuntimeWallFurnitureContract("Bookshelf",
                "WALL_BACKED_PROP_PROVENANCE_level4_GO2136_MF21517_MR16725_sharedassets4_Mesh721_MR_SHA256_6bb929cf8371caaed03e9a1662c45cd0f65ca416b045418fee9acf1aa07e226c",
                "BookShelf_COL", false),
            ["Kitcabinet_full_fridge"] = new RuntimeWallFurnitureContract("Kitcabinet_full_fridge",
                "WALL_BACKED_PROP_PROVENANCE_level4_GO2480_MF21764_MR16972_sharedassets4_Mesh1094_MR_SHA256_efadbb90c344d9c9e6caa3fbebbb0fe84c2de26780eed97ade67bde4aabdbf46",
                RuntimeBox(new Vector3(.4f, 1.1859446f, .3384462f), new Vector3(.8f, 2.3829341f, .6791087f)),
                RuntimeBox(new Vector3(1.48f, 1.1859446f, .3384462f), new Vector3(.1f, 2.3829341f, .6791087f)),
                RuntimeBox(new Vector3(.7495269f, 2.1400001f, .3384462f),
                    new Vector3(1.6002039f, .47f, .6791087f))),
            ["Kitcabinet_low_1x_A"] = new RuntimeWallFurnitureContract("Kitcabinet_low_1x_A",
                "WALL_BACKED_PROP_PROVENANCE_level4_GO1365_MF20950_MR16158_sharedassets4_Mesh1129_MR_SHA256_b0fa7469554a42d5da1ddcae9de8bb08183ae28e9ea4fb5445a8b2bbde7b8e2a",
                RuntimeBox(new Vector3(.50000006f, .45941916f, .33795837f),
                    new Vector3(1.00000036f, .92988235f, .67591673f))),
            ["Sidetable_A"] = new RuntimeWallFurnitureContract("Sidetable_A",
                "WALL_BACKED_PROP_PROVENANCE_level4_GO680_MF20466_MR15674_sharedassets4_Mesh1179_MR_SHA256_91c01d42fba3b158a452794ca9251f19c8bca785cc17daf3120d587471561b55",
                "SideTable_A_COL", true, new[]
                {
                    RuntimeChild("Sidetable_A_drawer", "Sidetable_A_drawer",
                        new[] { "Bedroom_Closets" },
                        new Vector3(-.000006561279f, .549451709f, .02767208f), Quaternion.identity,
                        collisionMeshName: "Drawer_SideTableA_COL", collisionEnabled: false,
                        collisionConvex: true, vertexCount: 439, indexCount: 1068)
                }),
            ["T_sink"] = new RuntimeWallFurnitureContract("T_sink",
                "WALL_BACKED_PROP_PROVENANCE_level4_GO785_MF20544_MR15751_sharedassets4_Mesh890_MR_SHA256_8175fa8e22445eb202c17c40051ed7c91404653e4732804644cdbea973fe8fe7",
                "T_Sink_COL", false, new[]
                {
                    RuntimeChild("T_sink_door_L (1)", "T_sink_door_L",
                        new[] { "Bedroom_Closets" },
                        new Vector3(.466553777f, .466425627f, .531496406f),
                        new Quaternion(0f, .000000238419f, 0f, 1f),
                        boxColliders: new[]
                        {
                            RuntimeBox(new Vector3(-.230998382f, 0f, .014692759f),
                                new Vector3(.470443398f, .684271514f, .050654199f))
                        }, vertexCount: 290, indexCount: 690),
                    RuntimeChild("T_sink_door_R (1)", "T_sink_door_R",
                        new[] { "Bedroom_Closets" },
                        new Vector3(-.469173878f, .466425627f, .531496406f),
                        new Quaternion(0f, .000000238419f, .000000087423f, 1f),
                        boxColliders: new[]
                        {
                            RuntimeBox(new Vector3(.232103765f, .000000461936f, .014692919f),
                                new Vector3(.470443428f, .684271574f, .050654192f))
                        }, vertexCount: 282, indexCount: 690)
                }),
            ["T_toilet"] = new RuntimeWallFurnitureContract("T_toilet",
                "WALL_BACKED_PROP_PROVENANCE_level4_GO1702_MF21188_MR16396_sharedassets4_Mesh1052_MR_SHA256_d04bc81fc4fd15a7b8222df3eefbcf3f5709e55142026fb6f6031d6584f72609",
                new[]
                {
                    RuntimeBox(new Vector3(-3.7252903e-08f, .22f, -.006511614f),
                        new Vector3(.39312702f, .44f, .61581385f)),
                    RuntimeBox(new Vector3(-3.7252903e-08f, .3703465f, -.25f),
                        new Vector3(.39312702f, .742007f, .13f))
                }, new[]
                {
                    RuntimeChild("T_toilet_lid (1)", "T_toilet_lid", new[] { "Toilet_House" },
                        new Vector3(-.000156097f, .409894168f, -.105191417f),
                        new Quaternion(-.725540996f, 0f, .000000029802f, .688179016f),
                        boxColliders: new[]
                        {
                            RuntimeBox(new Vector3(.001000941f, .017943554f, .184148327f),
                                new Vector3(.355356365f, .022520866f, .425430149f))
                        }, children: new[]
                        {
                            RuntimeChild("T_toilet_seat (1)", "T_toilet_seat",
                                new[] { "Toilet_House" }, Vector3.zero,
                                new Quaternion(.005043178f, -.0000000195f, -.000000063864f, .999987304f),
                                boxColliders: new[]
                                {
                                    RuntimeBox(new Vector3(0f, -.015444206f, .183325782f),
                                        new Vector3(.360641479f, .052449051f, .430035204f))
                                }, vertexCount: 248, indexCount: 1116)
                        }, vertexCount: 210, indexCount: 1008)
                }),
            ["Workdesk_solo"] = new RuntimeWallFurnitureContract("Workdesk_solo",
                "WALL_BACKED_PROP_PROVENANCE_level4_GO2401_MF21703_MR16911_sharedassets4_Mesh673_MR_SHA256_193630ef5ef5663d7dd6f6f7fd60e1aeb90782b7301c0b07076187620fb2b048",
                "WorkDesk_Solo_COL", false, new[]
                {
                    RuntimeChild("Workdesk_door_L", "Workdesk_door_L", new[] { "WorkDesk" },
                        new Vector3(-.834760129f, .409088165f, .457503468f), Quaternion.identity,
                        boxColliders: new[]
                        {
                            RuntimeBox(new Vector3(.205938876f, 0f, .000000004657f),
                                new Vector3(.410812676f, .550970018f, .04496894f))
                        }, vertexCount: 158, indexCount: 300),
                    RuntimeChild("Workdesk_door_R", "Workdesk_door_R", new[] { "WorkDesk" },
                        new Vector3(.831830859f, .409088165f, .457503468f), Quaternion.identity,
                        boxColliders: new[]
                        {
                            RuntimeBox(new Vector3(-.204264224f, 0f, .000000004657f),
                                new Vector3(.410812676f, .550970018f, .04496894f))
                        }, vertexCount: 158, indexCount: 300)
                })
        };

    private static readonly IReadOnlyDictionary<string, RuntimeCenterFurnitureContract>
        RuntimeCenterFurnitureContracts =
            new Dictionary<string, RuntimeCenterFurnitureContract>(StringComparer.Ordinal)
            {
                ["TABLE"] = new RuntimeCenterFurnitureContract(
                    "TABLE", "Kitchen_table_large",
                    "CENTER_ROOM_PROP_PROVENANCE_LEVEL4_GO1878_KITCHEN_TABLE_LARGE_SCALE_1_EXACT_PREFAB_UV0UV1_" +
                    "425779D06FD5E61A6C9C4C83359DBF35D81337C963F68B7BA46B704D8A069538",
                    Vector3.forward, true, 0, 995, 0,
                    RuntimeBox(new Vector3(0f, .36249366f, -2.9802322e-08f),
                        new Vector3(.2f, .7262458f, .2f)),
                    RuntimeBox(new Vector3(0f, .68f, -2.9802322e-08f),
                        new Vector3(1f, .07f, 1.75f))),
                ["SOFA"] = new RuntimeCenterFurnitureContract(
                    "SOFA", "Couch_2seat",
                    "CENTER_ROOM_PROP_PROVENANCE_LEVEL4_GO578_COUCH_2SEAT_SCALE_1_MESH_" +
                    "C58D40C40D6B9F18BE6A883EA69581002C005630DF1194206A638896EF483586_UV_" +
                    "CF3DB25EA907E15EF421DE6FC1D68C7031D0DA923DBF529383087B2DA55B6171_" +
                    "PROBE_ANCHOR_GO2175_T9763_MR15599",
                    Vector3.forward, false, 24, 844, 2286,
                    RuntimeBox(new Vector3(.0015151650877669454f, .23126330971717834f,
                            .056184977293014526f),
                        new Vector3(1.5813075304031372f, .46063679456710815f,
                            .8079850673675537f)),
                    RuntimeBox(new Vector3(.0015150876715779305f, .4757115840911865f,
                            -.23268039524555206f),
                        new Vector3(1.5813075304031372f, .9495333433151245f,
                            .23025444149971008f)),
                    RuntimeBox(new Vector3(.7339684963226318f, .3626925051212311f,
                            .05618477985262871f),
                        new Vector3(.11640128493309021f, .7234951853752136f,
                            .8079850673675537f)),
                    RuntimeBox(new Vector3(-.7227199077606201f, .3560544550418854f,
                            .056185171008110046f),
                        new Vector3(.13283774256706238f, .7102190852165222f,
                            .8079850673675537f)))
            };

    private static RuntimeBoxColliderProfile RuntimeBox(Vector3 center, Vector3 size)
    {
        return new RuntimeBoxColliderProfile(center, size);
    }

    private static RuntimeChildFurnitureContract RuntimeChild(string name, string meshName,
        string[] materialSlots, Vector3 localPosition, Quaternion localRotation,
        string collisionMeshName = null, bool collisionEnabled = true, bool collisionConvex = false,
        RuntimeBoxColliderProfile[] boxColliders = null, RuntimeChildFurnitureContract[] children = null,
        int vertexCount = 0, int indexCount = 0)
    {
        return new RuntimeChildFurnitureContract(name, meshName, materialSlots, localPosition,
            localRotation, Vector3.one, collisionMeshName, collisionEnabled, collisionConvex,
            boxColliders, children, vertexCount, indexCount);
    }

    public override void Load()
    {
        lifecycleStopping = false;
        string requiredLoader =
#if MELONLOADER
            "melonloader";
#else
            "bepinex";
#endif
        if (!string.Equals(OperatorApi.LoaderKind, requiredLoader, StringComparison.Ordinal))
        {
            Log.LogError("Kill House refused the duplicate or missing loader host; Core owner=" +
                OperatorApi.LoaderKind + ", required=" + requiredLoader + ".");
            return;
        }

        log = Log;
        exactEnvironmentAccepted = VerifyExactEnvironment();
        if (!exactEnvironmentAccepted)
        {
            log.LogError("Vektor Kill House runtime disabled: the installed OPERATOR build does not match the audited donor build.");
            return;
        }

        RuntimeTypeRegistrationResult typeRegistration =
            OperatorApi.RegisterIl2CppType(typeof(KillHouseUpdateDriver));
        if (typeRegistration is not RuntimeTypeRegistrationResult.Registered and
            not RuntimeTypeRegistrationResult.AlreadyRegistered)
            throw new InvalidOperationException("Kill House driver IL2CPP registration failed: " + typeRegistration + ".");
        KillHouseUpdateDriver.Tick = OnDriverTick;
        driverObject = new GameObject("MOD_VektorKillHouse_ExactSceneRuntime");
        UnityEngine.Object.DontDestroyOnLoad(driverObject);
        driverObject.AddComponent<KillHouseUpdateDriver>();

        sceneLoadedCallback = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityAction<Scene, LoadSceneMode>>(
            new Action<Scene, LoadSceneMode>(OnSceneLoaded));
        sceneUnloadedCallback = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityAction<Scene>>(
            new Action<Scene>(OnSceneUnloaded));
        SceneManager.sceneLoaded += sceneLoadedCallback;
        SceneManager.sceneUnloaded += sceneUnloadedCallback;
        for (int index = 0; index < SceneManager.sceneCount; index++)
        {
            Scene scene = SceneManager.GetSceneAt(index);
            if (IsKillHouseScene(scene)) OnSceneLoaded(scene, LoadSceneMode.Additive);
        }
        AuditResidentDoorTemplates("plugin-load", false);
        log.LogInfo(PluginName + " " + PluginVersion +
                    " loaded; framework-selected exact-scene material and indoor GridGraph reconstruction is armed.");
    }

    public override bool Unload()
    {
        lifecycleStopping = true;
        exactEnvironmentAccepted = false;
        var failures = new List<string>();
        bool globalFlashlightRestored = globalFlashlightMultiplier == null;
        AttemptUnloadStep("runtime-ready-marker", () =>
            RetireOwnedRuntimeContractMarker(ref ownedRuntimeReadyMarker, "plugin_unload"), failures);
        AttemptUnloadStep("framework-ready-marker", () =>
            RetireOwnedRuntimeContractMarker(ref ownedFrameworkReadyMarker, "plugin_unload"), failures);
        AttemptUnloadStep("runtime-contract-failure-wins", () =>
        {
            if (runtimeContractSceneHandle == 0 ||
                runtimeContractState is RuntimeContractState.None or RuntimeContractState.Failed) return;
            Scene contractScene = FindLoadedSceneByHandle(runtimeContractSceneHandle);
            GameObject contractRoot = contractScene.IsValid() && contractScene.isLoaded
                ? FindOwnedRoot(contractScene)
                : null;
            MarkFailure(contractScene, contractRoot, "plugin-unload");
        }, failures);
        AttemptUnloadStep("scene-loaded-callback", () =>
        {
            if (sceneLoadedCallback == null) return;
            SceneManager.sceneLoaded -= sceneLoadedCallback;
            sceneLoadedCallback = null;
        }, failures);
        AttemptUnloadStep("scene-unloaded-callback", () =>
        {
            if (sceneUnloadedCallback == null) return;
            SceneManager.sceneUnloaded -= sceneUnloadedCallback;
            sceneUnloadedCallback = null;
        }, failures);
        AttemptUnloadStep("update-driver-callback", () => KillHouseUpdateDriver.Tick = null, failures);
        AttemptUnloadStep("pending-runtime-state", () =>
        {
            pendingSceneHandle = 0;
            applyNotBeforeFrame = -1;
            aiSightOcclusionPending = false;
            aiSightOcclusionPassed = false;
            opticAuditPending = false;
            nextOpticIdentityProbeFrame = -1;
            equippedWeaponIdentityFingerprint = 0;
            equippedWeaponIdentityInitialized = false;
        }, failures);
        AttemptUnloadStep("warehouse-lighting", RestoreWarehouseOnlyLighting, failures);
        AttemptUnloadStep("weapon-illumination", () => RestoreWeaponIlluminationBoosts(true), failures);
        AttemptUnloadStep("global-flashlight", () =>
        {
            RestoreGlobalFlashlightMultiplierStrict();
            globalFlashlightRestored = true;
        }, failures);
        AttemptUnloadStep("runtime-navigation", () =>
        {
            if (!HasNavigationTeardownSnapshot() && HasRuntimeNavigationOwnership())
            {
                if (runtimeNavigationOwnerSceneHandle == 0 ||
                    !ArmNavigationTeardownAudit(runtimeNavigationOwnerSceneHandle))
                    throw new InvalidOperationException(
                        "owned navigation cannot be given an exact unload-audit generation");
            }

            bool immediateRelease = ReleaseRuntimeNavigation("plugin unload");
            if (HasNavigationTeardownSnapshot())
            {
                NavigationTeardownAuditResult auditResult = CompleteNavigationTeardownAudit();
                if (auditResult == NavigationTeardownAuditResult.Passed &&
                    !HasNavigationTeardownSnapshot()) return;
                throw new InvalidOperationException("post-unload navigation absence remains " +
                                                    auditResult.ToString().ToLowerInvariant());
            }
            if (!immediateRelease)
                throw new InvalidOperationException("owned navigation state remains after release attempt");
        }, failures);
        AttemptUnloadStep("runtime-materials", ReleaseOwnedMaterials, failures);
        AttemptUnloadStep("driver-object", () =>
        {
            if (globalFlashlightMultiplier != null && !globalFlashlightRestored)
                throw new InvalidOperationException("global flashlight baseline was not restored");
            if (driverObject != null)
            {
                driverObject.SetActive(false);
                UnityEngine.Object.Destroy(driverObject);
            }
            driverObject = null;
            globalFlashlightMultiplier = null;
        }, failures);
        AttemptUnloadStep("runtime-failure-marker", () =>
            RetireOwnedRuntimeContractMarker(ref ownedRuntimeFailureMarker, "plugin_unload"), failures);
        AttemptUnloadStep("framework-failure-marker", () =>
            RetireOwnedRuntimeContractMarker(ref ownedFrameworkFailureMarker, "plugin_unload"), failures);
        AttemptUnloadStep("runtime-contract-state", () =>
        {
            runtimeContractSceneHandle = 0;
            runtimeContractState = RuntimeContractState.None;
            runtimeContractFailurePublicationErrors = string.Empty;
        }, failures);
        string[] residues = DescribeUnloadResidues();
        bool passed = failures.Count == 0 && residues.Length == 0;
        string message = "Vektor Kill House unload: passed=" + passed +
                         ", failures=[" + string.Join(" | ", failures) + "]" +
                         ", residues=[" + string.Join(" | ", residues) + "].";
        if (passed) log?.LogInfo(message);
        else log?.LogError(message);
        return passed;
    }

    private void AttemptUnloadStep(string name, Action step, List<string> failures)
    {
        try
        {
            step();
        }
        catch (Exception exception)
        {
            failures.Add(name + "=" + exception.GetType().Name + ":" + exception.Message);
            log?.LogError("Vektor Kill House unload step failed: step=" + name + ", exception=" + exception + ".");
        }
    }

    private string[] DescribeUnloadResidues()
    {
        var residues = new List<string>();
        if (ownedRuntimeReadyMarker != null) residues.Add("runtime-ready-marker");
        if (ownedFrameworkReadyMarker != null) residues.Add("framework-ready-marker");
        if (ownedRuntimeFailureMarker != null) residues.Add("runtime-failure-marker");
        if (ownedFrameworkFailureMarker != null) residues.Add("framework-failure-marker");
        if (runtimeContractSceneHandle != 0 || runtimeContractState != RuntimeContractState.None)
            residues.Add("runtime-contract-state");
        if (sceneLoadedCallback != null) residues.Add("scene-loaded-callback");
        if (sceneUnloadedCallback != null) residues.Add("scene-unloaded-callback");
        if (KillHouseUpdateDriver.Tick != null) residues.Add("update-driver-callback");
        if (driverObject != null) residues.Add("driver-object");
        if (runtimeNavigationGraph != null) residues.Add("navigation-graph");
        if (runtimeNavigationAstar != null) residues.Add("navigation-astar");
        if (runtimeAstarHost != null || runtimeOwnsAstarHost) residues.Add("navigation-astar-host");
        if (runtimeRvoSimulator != null || runtimeOwnsRvoSimulator) residues.Add("navigation-rvo");
        if (HasNavigationTeardownSnapshot()) residues.Add("navigation-teardown-audit");
        if (runtimeMaterialsBySourceInstance.Count != 0 || ownedRuntimeMaterialIds.Count != 0)
            residues.Add("runtime-materials");
        if (suspendedDirectionalLights.Count != 0 || warehouseEnvironmentOverrideApplied)
            residues.Add("warehouse-lighting");
        if (hwsReticleBoostStates.Count != 0 || hwsReticleSizeStates.Count != 0 ||
            visibleIrLaserBoostStates.Count != 0 || visibleLaserLightBoostStates.Count != 0 ||
            boostedLaserBeamStates.Count != 0)
            residues.Add("weapon-illumination");
        if (globalFlashlightMultiplier != null) residues.Add("global-flashlight");
        if (pendingSceneHandle != 0 || applyNotBeforeFrame != -1 || aiSightOcclusionPending || opticAuditPending)
            residues.Add("pending-runtime-state");
        return residues.ToArray();
    }

    private bool VerifyExactEnvironment()
    {
        if (!string.Equals(Application.unityVersion, ExactUnityVersion, StringComparison.Ordinal))
        {
            log.LogError("Vektor Kill House exact-build rejection: unityVersion=" + Application.unityVersion +
                         ", expected=" + ExactUnityVersion + ".");
            return false;
        }
        foreach (KeyValuePair<string, FileFingerprint> pair in ExactFiles)
        {
            string path = Path.Combine(Paths.GameRootPath, pair.Key);
            if (!File.Exists(path) || new FileInfo(path).Length != pair.Value.Bytes)
            {
                log.LogError("Vektor Kill House exact-build rejection: missing or wrong-sized " + pair.Key + ".");
                return false;
            }
            using SHA256 sha = SHA256.Create();
            using FileStream stream = File.OpenRead(path);
            string actual = Convert.ToHexString(sha.ComputeHash(stream));
            if (!string.Equals(actual, pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log.LogError("Vektor Kill House exact-build rejection: SHA-256 mismatch for " + pair.Key + ".");
                return false;
            }
        }
        log.LogInfo("Vektor Kill House exact-build fingerprint passed: Unity plus executable, GameAssembly, and UnityPlayer.");
        return true;
    }

    private bool BeginRuntimeContractGeneration(Scene scene)
    {
        if (runtimeContractSceneHandle == scene.handle && runtimeContractState != RuntimeContractState.None)
            return runtimeContractState != RuntimeContractState.Failed;

        RetireOwnedRuntimeContractMarkers("replacement");
        runtimeContractSceneHandle = scene.handle;
        runtimeContractState = RuntimeContractState.Pending;
        runtimeContractFailurePublicationErrors = string.Empty;

        GameObject root = FindOwnedRoot(scene);
        Transform[] reserved = FindRuntimeContractMarkers(scene);
        if (reserved.Length == 0) return true;

        foreach (Transform marker in reserved.Where(item =>
                     string.Equals(item.name, ReadyMarkerName, StringComparison.Ordinal) ||
                     string.Equals(item.name, ModdedOperationsReadyMarkerName, StringComparison.Ordinal)))
        {
            marker.name = "REJECTED_PREEXISTING_" + marker.name;
            marker.gameObject.SetActive(false);
        }
        MarkFailure(scene, root, "preexisting-runtime-contract-markers=" + reserved.Length);
        return false;
    }

    private static Transform[] FindRuntimeContractMarkers(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return Array.Empty<Transform>();
        var markers = new List<Transform>();
        foreach (GameObject sceneRoot in scene.GetRootGameObjects())
        {
            if (sceneRoot == null) continue;
            foreach (Transform item in sceneRoot.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(item.name, ReadyMarkerName, StringComparison.Ordinal) ||
                    string.Equals(item.name, FailureMarkerName, StringComparison.Ordinal) ||
                    string.Equals(item.name, ModdedOperationsReadyMarkerName, StringComparison.Ordinal) ||
                    string.Equals(item.name, ModdedOperationsFailureMarkerName, StringComparison.Ordinal))
                    markers.Add(item);
            }
        }
        return markers.ToArray();
    }

    private static GameObject CreateRuntimeContractMarker(Scene scene, GameObject root, string name)
    {
        var marker = new GameObject(name);
        if (root != null) marker.transform.SetParent(root.transform, false);
        else SceneManager.MoveGameObjectToScene(marker, scene);
        return marker;
    }

    private static void RetireOwnedRuntimeContractMarker(ref GameObject marker, string reason)
    {
        GameObject owned = marker;
        if (owned == null)
        {
            marker = null;
            return;
        }
        if (!owned.name.StartsWith("RETIRED_", StringComparison.Ordinal))
            owned.name = "RETIRED_" + reason + "_" + owned.name;
        owned.SetActive(false);
        UnityEngine.Object.Destroy(owned);
        marker = null;
    }

    private void RetireOwnedRuntimeContractMarkers(string reason)
    {
        RetireOwnedRuntimeContractMarker(ref ownedFrameworkFailureMarker, reason);
        RetireOwnedRuntimeContractMarker(ref ownedRuntimeFailureMarker, reason);
        RetireOwnedRuntimeContractMarker(ref ownedFrameworkReadyMarker, reason);
        RetireOwnedRuntimeContractMarker(ref ownedRuntimeReadyMarker, reason);
        runtimeContractSceneHandle = 0;
        runtimeContractState = RuntimeContractState.None;
        runtimeContractFailurePublicationErrors = string.Empty;
    }

    private void ForgetRuntimeContractGeneration(int sceneHandle)
    {
        if (runtimeContractSceneHandle != sceneHandle) return;
        ownedRuntimeReadyMarker = null;
        ownedFrameworkReadyMarker = null;
        ownedRuntimeFailureMarker = null;
        ownedFrameworkFailureMarker = null;
        runtimeContractSceneHandle = 0;
        runtimeContractState = RuntimeContractState.None;
        runtimeContractFailurePublicationErrors = string.Empty;
    }

    private bool PublishRuntimeContractReady(Scene scene, GameObject root)
    {
        if (runtimeContractSceneHandle != scene.handle ||
            runtimeContractState != RuntimeContractState.Pending)
        {
            MarkFailure(scene, root, "runtime-contract-state=" + runtimeContractState +
                                     "/owner=" + runtimeContractSceneHandle);
            return false;
        }

        Transform[] preexisting = FindRuntimeContractMarkers(scene);
        if (preexisting.Length != 0)
        {
            MarkFailure(scene, root, "runtime-contract-marker-race=" + preexisting.Length);
            return false;
        }

        try
        {
            ownedRuntimeReadyMarker = CreateRuntimeContractMarker(scene, root, ReadyMarkerName);
            ownedFrameworkReadyMarker = CreateRuntimeContractMarker(scene, root,
                ModdedOperationsReadyMarkerName);
            runtimeContractState = RuntimeContractState.Ready;
            return true;
        }
        catch (Exception exception)
        {
            MarkFailure(scene, root, "runtime-contract-ready-exception=" + exception.GetType().Name);
            return false;
        }
    }

    private bool EnforceRuntimeContractFailureWins()
    {
        if (runtimeContractState is RuntimeContractState.None or RuntimeContractState.Failed ||
            runtimeContractSceneHandle == 0) return runtimeContractState != RuntimeContractState.Failed;
        Scene scene = FindLoadedSceneByHandle(runtimeContractSceneHandle);
        if (!scene.IsValid() || !scene.isLoaded) return true;
        Transform[] markers = FindRuntimeContractMarkers(scene);
        bool failurePresent = markers.Any(item =>
            string.Equals(item.name, FailureMarkerName, StringComparison.Ordinal) ||
            string.Equals(item.name, ModdedOperationsFailureMarkerName, StringComparison.Ordinal));
        GameObject root = FindOwnedRoot(scene);
        if (failurePresent)
        {
            MarkFailure(scene, root, "runtime-contract-failure-marker-observed");
            return false;
        }
        if (runtimeContractState != RuntimeContractState.Ready) return true;

        int runtimeReady = markers.Count(item =>
            string.Equals(item.name, ReadyMarkerName, StringComparison.Ordinal));
        int frameworkReady = markers.Count(item =>
            string.Equals(item.name, ModdedOperationsReadyMarkerName, StringComparison.Ordinal));
        if (runtimeReady == 1 && frameworkReady == 1 && ownedRuntimeReadyMarker != null &&
            ownedFrameworkReadyMarker != null) return true;
        MarkFailure(scene, root, "runtime-contract-ready-marker-lost-or-duplicated=" +
                                 runtimeReady + "/" + frameworkReady);
        return false;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (lifecycleStopping || !exactEnvironmentAccepted) return;
        if (!IsKillHouseScene(scene)) return;
        if (pendingSceneHandle == scene.handle && runtimeContractSceneHandle == scene.handle &&
            runtimeContractState != RuntimeContractState.None)
        {
            log.LogWarning("Vektor Kill House ignored a duplicate scene-loaded callback for handle=" +
                           scene.handle + ", state=" + runtimeContractState + ".");
            return;
        }
        int supersededSceneHandle = pendingSceneHandle != 0 && pendingSceneHandle != scene.handle
            ? pendingSceneHandle
            : 0;
        // Restart Operation can reload the pinned variant before every old-scene
        // callback has completed. Restore exact baselines before arming a new generation.
        if (supersededSceneHandle != 0) RestoreWarehouseOnlyLighting();
        RestoreWeaponIlluminationBoosts();
        if (supersededSceneHandle != 0) RestoreGlobalFlashlightMultiplier();
        pendingSceneHandle = scene.handle;
        applyNotBeforeFrame = Time.frameCount + ApplyDelayFrames;
        aiSightOcclusionPending = false;
        aiSightOcclusionPassed = false;
        nextAiSightAuditFrame = Time.frameCount + 30;
        opticAuditPending = true;
        nextOpticAuditFrame = Time.frameCount + 120;
        nextOpticIdentityProbeFrame = Time.frameCount + OpticIdentityProbeFrames;
        equippedWeaponIdentityFingerprint = 0;
        equippedWeaponIdentityInitialized = false;
        lastOpticAuditSignature = string.Empty;
        if (!BeginRuntimeContractGeneration(scene))
        {
            applyNotBeforeFrame = -1;
            aiSightOcclusionPending = false;
            opticAuditPending = false;
            nextOpticIdentityProbeFrame = -1;
            return;
        }
        bool staleNavigationGeneration = runtimeNavigationOwnerSceneHandle != 0 &&
                                         runtimeNavigationOwnerSceneHandle != scene.handle;
        if (staleNavigationGeneration)
        {
            int staleOwner = runtimeNavigationOwnerSceneHandle;
            if (!ArmNavigationTeardownAudit(staleOwner))
            {
                MarkFailure(scene, FindOwnedRoot(scene),
                    "conflicting-navigation-teardown-owner=" + staleOwner);
                return;
            }
            bool immediateNavigationRelease =
                ReleaseRuntimeNavigation("replacement kill-house scene takeover");
            if (!immediateNavigationRelease)
                log.LogWarning("Vektor Kill House replacement generation is awaiting the exact " +
                               "post-unload navigation absence proof for owner=" + staleOwner + ".");
        }
        else if (HasNavigationTeardownSnapshot() && pendingNavigationTeardownAuditFrame < 0 &&
                 !ArmNavigationTeardownAudit(navigationTeardownSceneHandle))
        {
            MarkFailure(scene, FindOwnedRoot(scene), "navigation-teardown-audit-could-not-resume");
            return;
        }
        if (supersededSceneHandle != 0 || staleNavigationGeneration)
        {
            try
            {
                ReleaseOwnedMaterials();
            }
            catch (Exception exception)
            {
                MarkFailure(scene, FindOwnedRoot(scene),
                    "stale-runtime-material-release=" + exception.GetType().Name);
                return;
            }
        }
        log.LogInfo("Vektor Kill House variant observed: path=" + scene.path + ", mode=" + mode +
                    ", applyFrame=" + applyNotBeforeFrame +
                    ", navigationAuditPending=" + HasNavigationTeardownSnapshot() + ".");
    }

    private void OnSceneUnloaded(Scene scene)
    {
        if (lifecycleStopping) return;
        ForgetRuntimeContractGeneration(scene.handle);
        if (scene.handle != pendingSceneHandle)
        {
            // Restart may report the replacement sceneLoaded callback before the prior sceneUnloaded
            // callback. Release only state whose recorded owner is the old handle; never touch the
            // newly armed generation.
            if (scene.handle == runtimeNavigationOwnerSceneHandle)
            {
                ArmNavigationTeardownAudit(scene.handle);
                bool navigationReleased = ReleaseRuntimeNavigation("superseded kill-house scene unload");
                ReleaseOwnedMaterials();
                string message = "Vektor Kill House superseded generation teardown: passed=" +
                                 navigationReleased + ", replacementHandle=" + pendingSceneHandle + ".";
                if (navigationReleased) log.LogInfo(message);
                else log.LogError(message);
            }
            return;
        }
        ArmNavigationTeardownAudit(scene.handle);
        pendingSceneHandle = 0;
        applyNotBeforeFrame = -1;
        aiSightOcclusionPending = false;
        aiSightOcclusionPassed = false;
        opticAuditPending = false;
        nextOpticIdentityProbeFrame = -1;
        equippedWeaponIdentityFingerprint = 0;
        equippedWeaponIdentityInitialized = false;
        lastOpticAuditSignature = string.Empty;
        RestoreWarehouseOnlyLighting();
        RestoreWeaponIlluminationBoosts();
        RestoreGlobalFlashlightMultiplier();
        bool navigationReleasedForUnload = ReleaseRuntimeNavigation("kill-house scene unload");
        ReleaseOwnedMaterials();
        if (navigationReleasedForUnload)
        {
            log.LogInfo("Vektor Kill House scene teardown: passed=True, deferred=False, sceneHandle=" +
                        scene.handle + ".");
        }
        else if (HasNavigationTeardownSnapshot())
        {
            // Unity destroys the scene-owned Astar/RVO objects at end of frame. A retained
            // absence-proof snapshot is expected here and is resolved by OnDriverTick; it is
            // neither a teardown failure nor a reason to reject the replacement generation.
            log.LogInfo("Vektor Kill House scene teardown: passed=pending, deferred=True, sceneHandle=" +
                        scene.handle + ".");
        }
        else
        {
            log.LogError("Vektor Kill House scene teardown: passed=False, deferred=False, sceneHandle=" +
                         scene.handle + ".");
        }
    }

    private void OnDriverTick()
    {
        if (!exactEnvironmentAccepted) return;
        bool runtimeContractAllowsProgress = EnforceRuntimeContractFailureWins();
        if (pendingNavigationTeardownAuditFrame >= 0)
        {
            long currentFrame = Time.frameCount;
            if (currentFrame < pendingNavigationTeardownAuditFrame) return;
            NavigationTeardownAuditResult auditResult = CompleteNavigationTeardownAudit();
            if (auditResult != NavigationTeardownAuditResult.Passed)
            {
                if (currentFrame < navigationTeardownAuditDeadlineFrame)
                {
                    pendingNavigationTeardownAuditFrame = Math.Min(
                        currentFrame + NavigationTeardownAuditRetryCadenceFrames,
                        navigationTeardownAuditDeadlineFrame);
                    return;
                }

                pendingNavigationTeardownAuditFrame = -1;
                if (pendingSceneHandle != 0)
                {
                    Scene scene = FindLoadedSceneByHandle(pendingSceneHandle);
                    GameObject root = scene.IsValid() && scene.isLoaded ? FindOwnedRoot(scene) : null;
                    MarkFailure(scene, root, "navigation-teardown-" +
                                               auditResult.ToString().ToLowerInvariant() + "=" +
                                               navigationTeardownAuditLastDetail +
                                               "/generation=" + navigationTeardownAuditGeneration +
                                               "/deadlineFrame=" + navigationTeardownAuditDeadlineFrame);
                }
                return;
            }
        }
        if (!runtimeContractAllowsProgress || HasNavigationTeardownSnapshot()) return;
        if (pendingSceneHandle == 0 ||
            (applyNotBeforeFrame < 0 && !aiSightOcclusionPending && !opticAuditPending &&
             nextOpticIdentityProbeFrame < 0)) return;
        if (runtimeNavigationOwnerSceneHandle != 0 &&
            runtimeNavigationOwnerSceneHandle != pendingSceneHandle) return;
        if (applyNotBeforeFrame >= 0 && Time.frameCount >= applyNotBeforeFrame)
        {
            applyNotBeforeFrame = -1;
            try
            {
                ApplyRuntimeContract();
            }
            catch (Exception exception)
            {
                Scene scene = FindLoadedSceneByHandle(pendingSceneHandle);
                GameObject root = scene.IsValid() && scene.isLoaded ? FindOwnedRoot(scene) : null;
                log.LogError("Vektor Kill House runtime gate threw before readiness: " + exception);
                MarkFailure(scene, root, "runtime-contract-exception=" + exception.GetType().Name);
            }
        }
        if (aiSightOcclusionPending && Time.frameCount >= nextAiSightAuditFrame)
        {
            nextAiSightAuditFrame = Time.frameCount + 120;
            TryCompleteDeferredAiSightAudit();
        }
        if (nextOpticIdentityProbeFrame >= 0 && Time.frameCount >= nextOpticIdentityProbeFrame)
        {
            nextOpticIdentityProbeFrame = Time.frameCount + OpticIdentityProbeFrames;
            ProbeEquippedWeaponIdentity();
        }
        if (opticAuditPending && Time.frameCount >= nextOpticAuditFrame)
        {
            nextOpticAuditFrame = Time.frameCount + 120;
            if (AuditLiveWeaponIllumination()) opticAuditPending = false;
        }
    }

    private static Type FindManagedType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(fullName, false);
            if (type != null) return type;
        }
        return null;
    }

    private void ApplyRuntimeContract()
    {
        Scene scene = FindLoadedSceneByHandle(pendingSceneHandle);
        if (!IsKillHouseScene(scene)) return;
        GameObject root = FindOwnedRoot(scene);
        AuditResidentDoorTemplates("kill-house-scene", true);
        if (root == null || !ValidateSceneContract(root))
        {
            MarkFailure(scene, root, "ownership-or-scene-contract");
            return;
        }
        bool warehouseOnlyLightingPassed = ApplyWarehouseOnlyLighting(root);
        bool renderPassed = EnsureIndoorRenderContract(root);
        bool fixtureMountsPassed = RepairIndoorFixtureRoofMounts(root);
        bool lightingPassed = warehouseOnlyLightingPassed && renderPassed && fixtureMountsPassed &&
                              ValidateIndoorLighting(root);
        bool doorsPassed = lightingPassed && EnsureNativeDoorV2Runtime(root);
        bool materialsPassed = doorsPassed && RebindSceneMaterials(root);
        bool sightPassed = false;
        bool sightDeferred = false;
        if (materialsPassed) sightPassed = ValidateAiSightOcclusion(root, out sightDeferred);
        int preparedTacticalEnemyMarkers = 0;
        bool tacticalPreparationPassed = materialsPassed && (sightPassed || sightDeferred) &&
            ValidateTacticalEnemyPlacement(root, true, out preparedTacticalEnemyMarkers, out _);
        bool navigationPassed = tacticalPreparationPassed && EnsureRuntimeNavigationGraph(root);
        bool playerSpawnContractPassed = navigationPassed && ValidateRuntimePlayerSpawnAndColliderContract(root);
        int tacticalEnemyMarkers = 0;
        int certifiedSafeCapacity = 0;
        bool tacticalEnemyPlacementPassed = playerSpawnContractPassed &&
            ValidateTacticalEnemyPlacement(root, false, out tacticalEnemyMarkers, out certifiedSafeCapacity) &&
            tacticalEnemyMarkers == preparedTacticalEnemyMarkers;
        if (!warehouseOnlyLightingPassed || !renderPassed || !lightingPassed || !doorsPassed || !materialsPassed ||
            (!sightPassed && !sightDeferred) ||
            !tacticalPreparationPassed || !navigationPassed || !playerSpawnContractPassed ||
            !tacticalEnemyPlacementPassed)
        {
            string failureReason = !warehouseOnlyLightingPassed ? "warehouse-light-isolation" :
                !renderPassed ? "indoor-render-contract" :
                !lightingPassed ? "indoor-lighting" :
                !doorsPassed ? "doorv2-reconstruction" :
                !materialsPassed ? "material-rebind" :
                (!sightPassed && !sightDeferred) ? "ai-sight-occlusion" :
                !tacticalPreparationPassed ? "tactical-enemy-preparation" :
                !navigationPassed ? "navigation" :
                !playerSpawnContractPassed ? "player-spawn-collider-contract" : "tactical-enemy-post-snap";
            MarkFailure(scene, root, failureReason);
            return;
        }
        aiSightOcclusionPassed = sightPassed;
        aiSightOcclusionPending = sightDeferred;
        if (sightDeferred) nextAiSightAuditFrame = Time.frameCount + 30;
        if (!PublishRuntimeContractReady(scene, root)) return;
        log.LogInfo("Vektor Kill House runtime gate passed: scene=" + scene.name +
                    ", nativeOnly=true, fullDoorV2=true, stateAwareFixtureLighting=true, vanillaIndoorRender=true, fixedSafeRoom=true, tacticalEnemyPositions=" +
                    tacticalEnemyMarkers + "/" + tacticalEnemyMarkers +
                    ", certifiedSafeEnemyCapacity=" + certifiedSafeCapacity +
                    ", vanillaIdleBehavior=Wander12m, frameworkInitialResponseDelay=3-6s, aiSightOcclusion=" +
                    (sightPassed ? "passed" : "deferred-until-resident-ai") + ".");
    }

    private bool EnsureIndoorRenderContract(GameObject root)
    {
        Volume[] volumes = root.GetComponentsInChildren<Volume>(true);
        Volume volume = volumes.SingleOrDefault(candidate =>
            string.Equals(candidate.name, "VANILLA_OFFICE_GLOBAL_VOLUME", StringComparison.Ordinal));
        string volumeDetail = "volume-count=" + volumes.Length;
        bool volumeValid = volumes.Length == 1 && VolumeHasPvpWarehouseContract(volume, out volumeDetail);
        bool multiplierValid = false;
        try
        {
            if (globalFlashlightMultiplier == null)
                globalFlashlightMultiplier = driverObject.AddComponent<GlobalFlashLightMultiplier>();
            globalFlashlightMultiplier.MultiplierValue = IndoorFlashlightMultiplier;
            globalFlashlightMultiplier.UpdateFlashLightMultiplier();
            multiplierValid = Mathf.Abs(globalFlashlightMultiplier.MultiplierValue - IndoorFlashlightMultiplier) <= .001f;
        }
        catch (Exception exception)
        {
            log.LogError("Vektor Kill House global flashlight multiplier failed: " +
                         exception.GetType().Name + ": " + exception.Message + ".");
        }
        bool passed = volumeValid && multiplierValid;
        log.LogInfo("Vektor Kill House indoor render gate: passed=" + passed +
                    ", volumes=" + volumes.Length + ", officeVolume=" + volumeValid +
                    ", globalFlashlightMultiplier=" +
                    (globalFlashlightMultiplier == null ? "missing" :
                        globalFlashlightMultiplier.MultiplierValue.ToString("F2", CultureInfo.InvariantCulture)) +
                    ", reticleNormalBrightnessMultiplier=" +
                    IndoorReticleNormalBrightnessMultiplier.ToString("F2", CultureInfo.InvariantCulture) +
                    ", reticleNvgBrightnessMultiplier=" +
                    IndoorReticleNvgBrightnessMultiplier.ToString("F2", CultureInfo.InvariantCulture) +
                    ", reticleNormalSizeMultiplier=" +
                    IndoorReticleNormalSizeMultiplier.ToString("F2", CultureInfo.InvariantCulture) +
                    ", visibleLaserMultiplier=" + IndoorVisibleLaserMultiplier.ToString("F2", CultureInfo.InvariantCulture) +
                    ", donor=PVP-Woods-Warehouse, exposureCompensation=0.00, exposureRangeEV=8.50-11.00, bloom=0.03, lensFlare=0.50, externalLut=AgX-PunchyPowerfulMix" +
                    (volumeValid ? string.Empty : ", volumeDetail=" + volumeDetail) + ".");
        return passed;
    }

    private static bool VolumeHasPvpWarehouseContract(Volume volume, out string detail)
    {
        detail = "volume=<null>";
        if (volume == null) return false;
        VolumeProfile profile = volume.sharedProfile;
        if (!volume.isGlobal || volume.gameObject.layer != 0 || Mathf.Abs(volume.priority - 100010f) > .01f ||
            Mathf.Abs(volume.weight - 1f) > .001f || profile == null)
        {
            detail = "global=" + volume.isGlobal + ",layer=" + volume.gameObject.layer +
                     ",priority=" + volume.priority.ToString("F2", CultureInfo.InvariantCulture) +
                     ",weight=" + volume.weight.ToString("F2", CultureInfo.InvariantCulture) +
                     ",profile=" + (profile == null ? "missing" : profile.name);
            return false;
        }
        if (!profile.TryGet(out Exposure exposure) || !profile.TryGet(out VisualEnvironment visualEnvironment) ||
            !profile.TryGet(out PhysicallyBasedSky physicallyBasedSky) || !profile.TryGet(out Fog fog) ||
            !profile.TryGet(out ProbeVolumesOptions probeVolumes) || !profile.TryGet(out Bloom bloom) ||
            !profile.TryGet(out ScreenSpaceLensFlare lensFlare) || !profile.TryGet(out MicroShadowing microShadowing) ||
            !profile.TryGet(out ContactShadows contactShadows) || !profile.TryGet(out HDShadowSettings shadowSettings) ||
            !profile.TryGet(out Tonemapping tonemapping) ||
            !profile.TryGet(out LiftGammaGain liftGammaGain) || !profile.TryGet(out WhiteBalance whiteBalance) ||
            !profile.TryGet(out ColorAdjustments colorAdjustments))
        {
            detail = "profile-components=" + profile.components.Count;
            return false;
        }
        Texture rawLut = tonemapping.lutTexture.value;
        Texture3D lut = rawLut == null ? null : rawLut.TryCast<Texture3D>();
        bool passed = profile.components.Count == 14 && exposure.active && exposure.mode.overrideState &&
                      exposure.mode.value == ExposureMode.AutomaticHistogram && exposure.compensation.overrideState &&
                      Mathf.Abs(exposure.compensation.value) <= .001f && exposure.limitMin.overrideState &&
                      Mathf.Abs(exposure.limitMin.value - 8.5f) <= .001f && exposure.limitMax.overrideState &&
                      Mathf.Abs(exposure.limitMax.value - 11f) <= .001f &&
                      !visualEnvironment.active && !physicallyBasedSky.active && !fog.active && probeVolumes.active &&
                       bloom.active && bloom.intensity.overrideState &&
                       Mathf.Abs(bloom.intensity.value - .03f) <= .001f && bloom.threshold.overrideState &&
                       Mathf.Abs(bloom.threshold.value - .9f) <= .001f && bloom.scatter.overrideState &&
                       Mathf.Abs(bloom.scatter.value - .893f) <= .001f && lensFlare.active &&
                       lensFlare.intensity.overrideState && Mathf.Abs(lensFlare.intensity.value - .5f) <= .001f &&
                       lensFlare.streaksIntensity.overrideState &&
                       Mathf.Abs(lensFlare.streaksIntensity.value - 1.55f) <= .001f &&
                       lensFlare.streaksLength.overrideState &&
                       Mathf.Abs(lensFlare.streaksLength.value - .022f) <= .001f &&
                       lensFlare.streaksOrientation.overrideState &&
                       Mathf.Abs(lensFlare.streaksOrientation.value) <= .001f &&
                       lensFlare.chromaticAbberationIntensity.overrideState &&
                       Mathf.Abs(lensFlare.chromaticAbberationIntensity.value - .6f) <= .001f &&
                      microShadowing.active && contactShadows.active && shadowSettings.active && tonemapping.active &&
                      tonemapping.mode.overrideState && tonemapping.mode.value == TonemappingMode.External &&
                      tonemapping.lutTexture.overrideState && lut != null && lut.width == 32 && lut.height == 32 &&
                      lut.depth == 32 && string.Equals(lut.name, "AgX - PunchyPowerfulMix", StringComparison.Ordinal) &&
                      !tonemapping.lutContribution.overrideState &&
                      Mathf.Abs(tonemapping.lutContribution.value - 1f) <= .001f && liftGammaGain.active &&
                      liftGammaGain.lift.overrideState && liftGammaGain.gamma.overrideState &&
                      liftGammaGain.gain.overrideState && whiteBalance.active && colorAdjustments.active &&
                      colorAdjustments.postExposure.overrideState &&
                      Mathf.Abs(colorAdjustments.postExposure.value + .3f) <= .001f &&
                      colorAdjustments.contrast.overrideState && Mathf.Abs(colorAdjustments.contrast.value - 30f) <= .001f &&
                      colorAdjustments.hueShift.overrideState && Mathf.Abs(colorAdjustments.hueShift.value) <= .001f &&
                      colorAdjustments.saturation.overrideState &&
                      Mathf.Abs(colorAdjustments.saturation.value + 15f) <= .001f;
        detail = "components=" + profile.components.Count + ",exposure=" + exposure.mode.value + "/" +
                 exposure.limitMin.value.ToString("F2", CultureInfo.InvariantCulture) + "-" +
                 exposure.limitMax.value.ToString("F2", CultureInfo.InvariantCulture) + ",tonemap=" +
                 tonemapping.mode.value + ",lut=" + (lut == null ? "missing" : lut.name) +
                 ",rawLut=" + (rawLut == null ? "missing" : rawLut.name + "/" + rawLut.GetType().Name) +
                  ",bloom=" + bloom.intensity.value.ToString("F2", CultureInfo.InvariantCulture) +
                  ",bloomScatter=" + bloom.scatter.value.ToString("F3", CultureInfo.InvariantCulture) +
                  ",lensFlare=" + lensFlare.intensity.value.ToString("F2", CultureInfo.InvariantCulture) +
                  ",flareStreaks=" + lensFlare.streaksIntensity.value.ToString("F2", CultureInfo.InvariantCulture) +
                  "/" + lensFlare.streaksLength.value.ToString("F3", CultureInfo.InvariantCulture) +
                  ",chromatic=" + lensFlare.chromaticAbberationIntensity.value.ToString("F2", CultureInfo.InvariantCulture) +
                  ",whiteBalanceActive=" + whiteBalance.active;
        return passed;
    }

    private void RestoreGlobalFlashlightMultiplier()
    {
        if (globalFlashlightMultiplier == null) return;
        try
        {
            globalFlashlightMultiplier.MultiplierValue = 1f;
            globalFlashlightMultiplier.UpdateFlashLightMultiplier();
        }
        catch (Exception exception)
        {
            log?.LogWarning("Vektor Kill House could not restore the global flashlight multiplier: " +
                            exception.GetType().Name + ": " + exception.Message + ".");
        }
    }

    private void RestoreGlobalFlashlightMultiplierStrict()
    {
        if (globalFlashlightMultiplier == null) return;
        globalFlashlightMultiplier.MultiplierValue = 1f;
        globalFlashlightMultiplier.UpdateFlashLightMultiplier();
        if (Mathf.Abs(globalFlashlightMultiplier.MultiplierValue - 1f) > .001f)
            throw new InvalidOperationException("global flashlight multiplier did not return to its baseline");
    }

    private bool ApplyWarehouseOnlyLighting(GameObject root)
    {
        try
        {
            if (!warehouseEnvironmentOverrideApplied)
            {
                savedSkybox = RenderSettings.skybox;
                savedAmbientMode = RenderSettings.ambientMode;
                savedAmbientLight = RenderSettings.ambientLight;
                savedAmbientIntensity = RenderSettings.ambientIntensity;
                savedReflectionIntensity = RenderSettings.reflectionIntensity;
                warehouseEnvironmentOverrideApplied = true;
            }

            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.ambientIntensity = 0f;
            RenderSettings.reflectionIntensity = 0f;

            foreach (Light light in FindLoadedDirectionalLights())
            {
                if (light == null || light.transform.IsChildOf(root.transform) ||
                    suspendedDirectionalLights.ContainsKey(light.GetInstanceID())) continue;
                suspendedDirectionalLights[light.GetInstanceID()] = new SuspendedDirectionalLight(light,
                    light.enabled, light.intensity, light.shadows);
                light.enabled = false;
                light.intensity = 0f;
                light.shadows = LightShadows.None;
            }

            bool passed = RenderSettings.skybox == null && RenderSettings.ambientMode == AmbientMode.Flat &&
                          RenderSettings.ambientLight.maxColorComponent <= .001f &&
                          RenderSettings.ambientIntensity <= .001f && RenderSettings.reflectionIntensity <= .001f &&
                          FindLoadedDirectionalLights().All(light => !light.enabled && light.intensity <= .001f);
            log.LogInfo("Vektor Kill House warehouse-only global lighting: passed=" + passed +
                        ", suspendedExternalDirectionals=" + suspendedDirectionalLights.Count +
                        ", skybox=none, ambient=black/0, reflectionIntensity=0, weapon-local-lights-preserved=true.");
            return passed;
        }
        catch (Exception exception)
        {
            log.LogError("Vektor Kill House warehouse-only global lighting failed: " + exception);
            return false;
        }
    }

    private void RestoreWarehouseOnlyLighting()
    {
        foreach (SuspendedDirectionalLight state in suspendedDirectionalLights.Values)
        {
            if (state.Light == null) continue;
            state.Light.enabled = state.Enabled;
            state.Light.intensity = state.Intensity;
            state.Light.shadows = state.Shadows;
        }
        suspendedDirectionalLights.Clear();
        if (!warehouseEnvironmentOverrideApplied) return;
        RenderSettings.skybox = savedSkybox;
        RenderSettings.ambientMode = savedAmbientMode;
        RenderSettings.ambientLight = savedAmbientLight;
        RenderSettings.ambientIntensity = savedAmbientIntensity;
        RenderSettings.reflectionIntensity = savedReflectionIntensity;
        savedSkybox = null;
        warehouseEnvironmentOverrideApplied = false;
    }

    private static Light[] FindLoadedDirectionalLights()
    {
        var result = new List<Light>();
        Il2CppReferenceArray<UnityEngine.Object> objects = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Light>());
        foreach (UnityEngine.Object value in objects)
        {
            Light light = value == null ? null : value.TryCast<Light>();
            if (light == null || light.type != LightType.Directional || light.gameObject == null) continue;
            Scene scene = light.gameObject.scene;
            if (scene.IsValid() && scene.isLoaded) result.Add(light);
        }
        return result.ToArray();
    }

    private bool RepairIndoorFixtureRoofMounts(GameObject root)
    {
        Transform lightingRoot = root == null ? null : root.transform.Find("70_LIGHTING");
        Transform warehouseRoof = root == null ? null : root.transform.Find(
            "05_HIGH_WAREHOUSE_SHELL/NATIVE_WarehousePvpCompleteShell/NATIVE_WarehouseRoof");
        Collider[] roofColliders = warehouseRoof == null ? Array.Empty<Collider>() :
            warehouseRoof.GetComponentsInChildren<Collider>(true)
                .Where(collider => collider != null && collider.enabled && !collider.isTrigger).ToArray();
        Transform[] fixtures = lightingRoot == null ? Array.Empty<Transform>() :
            lightingRoot.GetComponentsInChildren<Transform>(true)
                .Where(item => item.name.StartsWith("NATIVE_Lamp_fluorescent_B_", StringComparison.Ordinal))
                .ToArray();
        if (roofColliders.Length == 0 || fixtures.Length == 0)
        {
            log.LogError("Vektor Kill House fixture roof-mount preparation failed: roofColliders=" +
                roofColliders.Length + ", fixtures=" + fixtures.Length + ".");
            return false;
        }

        int repaired = 0;
        int invalid = 0;
        float maximumDisplacement = 0f;
        foreach (Transform fixture in fixtures)
        {
            if (!TryRuntimeFixtureRoofGap(fixture, roofColliders, out float existingGap,
                    out float existingTop) ||
                existingGap < -WarehouseFixtureRoofGap || existingGap > WarehouseRoofHeight)
            {
                invalid++;
                continue;
            }
            float displacement = existingGap - WarehouseFixtureRoofGap;
            if (Mathf.Abs(displacement) > .001f)
            {
                fixture.position += Vector3.up * displacement;
                repaired++;
                maximumDisplacement = Mathf.Max(maximumDisplacement, Mathf.Abs(displacement));
            }

            string suffix = fixture.name.Substring("NATIVE_Lamp_fluorescent_B_".Length);
            Transform holder = fixture.parent;
            Light light = holder == null ? null : holder.GetComponentsInChildren<Light>(true)
                .FirstOrDefault(item => string.Equals(item.name,
                    "ROOM_LOCAL_FIXTURE_LIGHT_" + suffix, StringComparison.Ordinal));
            if (light == null)
            {
                invalid++;
                continue;
            }
            float fixtureTop = existingTop + displacement;
            light.transform.position = new Vector3(
                light.transform.position.x,
                fixtureTop - WarehouseFixtureLightDrop,
                light.transform.position.z);
        }
        Physics.SyncTransforms();

        int postInvalid = fixtures.Count(fixture =>
            !TryRuntimeFixtureRoofGap(fixture, roofColliders, out float gap, out _) ||
            Mathf.Abs(gap - WarehouseFixtureRoofGap) > .015f);
        bool passed = invalid == 0 && postInvalid == 0;
        log.LogInfo("Vektor Kill House fixture roof-mount preparation: passed=" + passed +
            ", fixtures=" + fixtures.Length + ", repaired=" + repaired +
            ", invalid=" + invalid + ", postInvalid=" + postInvalid +
            ", targetGap=" + WarehouseFixtureRoofGap.ToString("F3", CultureInfo.InvariantCulture) +
            ", maximumDisplacement=" + maximumDisplacement.ToString("F3", CultureInfo.InvariantCulture) + ".");
        return passed;
    }

    private bool ValidateIndoorLighting(GameObject root)
    {
        Light[] lights = root.GetComponentsInChildren<Light>(true);
        Light[] directional = lights.Where(light => light.type == LightType.Directional).ToArray();
        Light[] local = lights.Where(light => light.name.StartsWith("ROOM_LOCAL_FIXTURE_LIGHT_", StringComparison.Ordinal)).ToArray();
        Transform roomsRoot = root.transform.Find("10_ROOMS");
        Transform lightingRoot = root.transform.Find("70_LIGHTING");
        Transform warehouseRoof = root.transform.Find(
            "05_HIGH_WAREHOUSE_SHELL/NATIVE_WarehousePvpCompleteShell/NATIVE_WarehouseRoof");
        Collider[] warehouseRoofColliders = warehouseRoof == null ? Array.Empty<Collider>() :
            warehouseRoof.GetComponentsInChildren<Collider>(true)
                .Where(collider => collider != null && collider.enabled && !collider.isTrigger).ToArray();
        Transform[] fixtureVisuals = lightingRoot == null ? Array.Empty<Transform>() :
            lightingRoot.GetComponentsInChildren<Transform>(true)
                .Where(item => item.name.StartsWith("NATIVE_Lamp_fluorescent_B_", StringComparison.Ordinal))
                .ToArray();
        int roomCount = roomsRoot == null ? 0 : roomsRoot.childCount;
        Transform[] roomLightHolders = lightingRoot == null ? Array.Empty<Transform>() :
            Enumerable.Range(0, lightingRoot.childCount).Select(lightingRoot.GetChild)
                .Where(item => item.name.StartsWith("ROOM_LIGHT_", StringComparison.Ordinal) &&
                               item.name.IndexOf("_STATE_", StringComparison.Ordinal) >= 0).ToArray();
        int litRooms = roomLightHolders.Count(item => item.name.EndsWith("_STATE_LIT", StringComparison.Ordinal));
        int dimRooms = roomLightHolders.Count(item => item.name.EndsWith("_STATE_DIM", StringComparison.Ordinal));
        int darkRooms = roomLightHolders.Count(item => item.name.EndsWith("_STATE_DARK", StringComparison.Ordinal));
        int litSafeRooms = roomLightHolders.Count(item => item.name.StartsWith("ROOM_LIGHT_00_SAFE_", StringComparison.Ordinal) &&
            item.name.EndsWith("_STATE_LIT", StringComparison.Ordinal));
        foreach (Light light in directional.Where(light =>
                     string.Equals(light.name, "PACKAGE_FALLBACK_DIRECTIONAL_LIGHT", StringComparison.Ordinal)))
        {
            light.enabled = false;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
        }
        int addedHdrpData = 0;
        foreach (Light light in local)
        {
            Transform holder = light.transform.parent;
            int holderFixtureCount = holder == null ? 0 : holder.GetComponentsInChildren<Light>(true)
                .Count(candidate => candidate.name.StartsWith("ROOM_LOCAL_FIXTURE_LIGHT_", StringComparison.Ordinal));
            string holderName = holder == null ? string.Empty : holder.name;
            bool lit = holderName.EndsWith("_STATE_LIT", StringComparison.Ordinal);
            bool dim = holderName.EndsWith("_STATE_DIM", StringComparison.Ordinal);
            bool dark = holderName.EndsWith("_STATE_DARK", StringComparison.Ordinal);
            // Reapply the authored state profile after HDRP has initialized. HDAdditionalLightData can
            // synchronize its previous serialized value back onto Light during additive scene loading.
            float intendedLumens = lit ? (holderFixtureCount == 1 ? 1400f : 1100f) :
                dim ? (holderFixtureCount == 1 ? 160f : 120f) : 0f;
            const float intendedTemperature = 4300f;

            light.type = LightType.Spot;
            light.color = Color.white;
            light.range = 11.5f;
            light.spotAngle = 58f;
            light.innerSpotAngle = 38f;
            light.shadows = LightShadows.Soft;
            light.useColorTemperature = true;
            light.colorTemperature = intendedTemperature;
            light.intensity = intendedLumens;
            light.enabled = !dark && (lit || dim);

            HDAdditionalLightData[] pairedData = light.GetComponents<HDAdditionalLightData>();
            HDAdditionalLightData hd = pairedData.Length == 0 ? null : pairedData[0];
            if (hd == null)
            {
                hd = light.gameObject.AddComponent<HDAdditionalLightData>();
                addedHdrpData++;
            }
            hd.lightUnit = LightUnit.Lumen;
            hd.intensity = intendedLumens;
            hd.range = 11.5f;
        }

        var localFixtureLightIds = new HashSet<int>(local.Select(light => light.GetInstanceID()));
        var fixtureTreeHdrpDataList = new List<HDAdditionalLightData>();
        var fixtureTreeHdrpDataIds = new HashSet<int>();
        foreach (Transform holder in roomLightHolders)
        {
            foreach (HDAdditionalLightData data in holder.GetComponentsInChildren<HDAdditionalLightData>(true))
                if (data != null && fixtureTreeHdrpDataIds.Add(data.GetInstanceID()))
                    fixtureTreeHdrpDataList.Add(data);
        }
        HDAdditionalLightData[] fixtureTreeHdrpData = fixtureTreeHdrpDataList.ToArray();
        int orphanFixtureHdrpData = fixtureTreeHdrpData.Count(data =>
            data.legacyLight == null || !localFixtureLightIds.Contains(data.legacyLight.GetInstanceID()) ||
            data.gameObject != data.legacyLight.gameObject);
        int invalidFixtureLights = 0;
        int invalidFixtureMounts = 0;
        int pairedFixtureHdrpData = 0;
        int duplicateFixtureHdrpData = 0;
        var fixtureRoofGaps = new List<float>();
        List<string> invalidSamples = new List<string>();
        foreach (Light light in local)
        {
            Light[] pairedLights = light.gameObject.GetComponents<Light>();
            HDAdditionalLightData[] pairedData = light.gameObject.GetComponents<HDAdditionalLightData>();
            pairedFixtureHdrpData += pairedData.Length;
            if (pairedData.Length != 1) duplicateFixtureHdrpData += Math.Abs(pairedData.Length - 1);
            HDAdditionalLightData hd = pairedData.Length == 1 ? pairedData[0] : null;
            float mapLocalY = root.transform.InverseTransformPoint(light.transform.position).y;
            string holderName = light.transform.parent == null ? string.Empty : light.transform.parent.name;
            string suffix = light.name.Substring("ROOM_LOCAL_FIXTURE_LIGHT_".Length);
            Transform fixture = light.transform.parent == null ? null :
                light.transform.parent.GetComponentsInChildren<Transform>(true)
                    .FirstOrDefault(item => string.Equals(item.name,
                        "NATIVE_Lamp_fluorescent_B_" + suffix, StringComparison.Ordinal));
            bool fixtureMountValid = TryRuntimeFixtureRoofGap(
                    fixture,
                    warehouseRoofColliders,
                    out float fixtureRoofGap,
                    out float fixtureTop) &&
                Mathf.Abs(fixtureRoofGap - WarehouseFixtureRoofGap) <= .015f &&
                Mathf.Abs((fixtureTop - light.transform.position.y) - WarehouseFixtureLightDrop) <= .015f;
            if (float.IsFinite(fixtureRoofGap)) fixtureRoofGaps.Add(fixtureRoofGap);
            if (!fixtureMountValid) invalidFixtureMounts++;
            bool stateValid = holderName.EndsWith("_STATE_LIT", StringComparison.Ordinal)
                ? light.enabled && hd != null && hd.intensity >= 1050f && hd.intensity <= 1450f
                : holderName.EndsWith("_STATE_DIM", StringComparison.Ordinal)
                    ? light.enabled && hd != null && hd.intensity >= 110f && hd.intensity <= 170f
                    : holderName.EndsWith("_STATE_DARK", StringComparison.Ordinal) && !light.enabled &&
                      hd != null && hd.intensity <= .001f;
            bool valid = pairedLights.Length == 1 && pairedLights[0] == light && pairedData.Length == 1 &&
                          hd != null && hd.enabled && hd.gameObject == light.gameObject &&
                          hd.legacyLight != null && hd.legacyLight == light &&
                          stateValid && light.type == LightType.Spot && light.shadows == LightShadows.Soft &&
                          fixtureMountValid &&
                          light.range >= 11f && light.spotAngle >= 56f && light.spotAngle <= 60f &&
                         light.useColorTemperature && light.colorTemperature >= 4200f &&
                         light.colorTemperature <= 4400f && hd != null && hd.lightUnit == LightUnit.Lumen &&
                         hd.range >= 11f;
            if (valid) continue;
            invalidFixtureLights++;
            if (invalidSamples.Count < 4)
                invalidSamples.Add(light.name + "{mapY=" + mapLocalY.ToString("F2", CultureInfo.InvariantCulture) +
                                   ",roofGap=" + (float.IsFinite(fixtureRoofGap)
                                       ? fixtureRoofGap.ToString("F3", CultureInfo.InvariantCulture) : "missing") +
                                   ",fixtureTop=" + (float.IsFinite(fixtureTop)
                                       ? fixtureTop.ToString("F3", CultureInfo.InvariantCulture) : "missing") +
                                   ",enabled=" + light.enabled + ",type=" + light.type +
                                   ",shadows=" + light.shadows + ",range=" +
                                   light.range.ToString("F2", CultureInfo.InvariantCulture) +
                                   ",temperature=" + light.colorTemperature.ToString("F0", CultureInfo.InvariantCulture) +
                                   ",lightComponents=" + pairedLights.Length +
                                   ",hdComponents=" + pairedData.Length +
                                   ",hd=" + (hd == null ? "missing-or-duplicate" : hd.lightUnit + "/" +
                                       hd.intensity.ToString("F1", CultureInfo.InvariantCulture) + "/" +
                                       hd.range.ToString("F2", CultureInfo.InvariantCulture)) + "}");
        }
        int pointLights = lights.Count(light => light.type == LightType.Point);
        int spotLights = lights.Count(light => light.type == LightType.Spot);
        int nonFixtureSpotLights = lights.Count(light => light.type == LightType.Spot &&
            !light.name.StartsWith("ROOM_LOCAL_FIXTURE_LIGHT_", StringComparison.Ordinal));
        Light[] loadedDirectionals = FindLoadedDirectionalLights();
        int enabledLoadedDirectionals = loadedDirectionals.Count(light => light.enabled || light.intensity > .001f);
        bool warehouseEnvironmentContract = warehouseEnvironmentOverrideApplied && RenderSettings.skybox == null &&
                                            RenderSettings.ambientMode == AmbientMode.Flat &&
                                            RenderSettings.ambientLight.maxColorComponent <= .001f &&
                                            RenderSettings.ambientIntensity <= .001f &&
                                            RenderSettings.reflectionIntensity <= .001f;
        bool localContract = local.Length >= roomCount && invalidFixtureLights == 0 &&
                             fixtureVisuals.Length == local.Length && invalidFixtureMounts == 0 &&
                             warehouseRoofColliders.Length > 0 && fixtureRoofGaps.Count == local.Length &&
                             pairedFixtureHdrpData == local.Length && duplicateFixtureHdrpData == 0 &&
                             fixtureTreeHdrpData.Length == local.Length && orphanFixtureHdrpData == 0 &&
                             roomLightHolders.Length == roomCount && litSafeRooms == 1 &&
                             litRooms >= 5 && dimRooms >= 4 && darkRooms >= 7;
        bool passed = directional.Length == 1 && !directional[0].enabled && directional[0].intensity <= .001f &&
                      enabledLoadedDirectionals == 0 && warehouseEnvironmentContract &&
                      pointLights == 0 && spotLights == local.Length && localContract;
        string lightingScenePathBase64 = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(root.scene.path ?? string.Empty));
        log.LogInfo("Vektor Kill House indoor lighting gate: passed=" + passed +
                    ", sceneHandle=" + root.scene.handle +
                    ", scenePathBase64=" + lightingScenePathBase64 +
                    ", roomSpaces=" + roomCount + ", fixtureLights=" + local.Length +
                    ", roomLightStates=lit:" + litRooms + "/dim:" + dimRooms + "/dark:" + darkRooms +
                    ", litSafeRooms=" + litSafeRooms +
                    ", invalidFixtures=" + invalidFixtureLights +
                    ", invalidFixtureMounts=" + invalidFixtureMounts +
                    ", fixtureRoofGap=" + (fixtureRoofGaps.Count == 0 ? "missing" :
                        fixtureRoofGaps.Min().ToString("F3", CultureInfo.InvariantCulture) + ".." +
                        fixtureRoofGaps.Max().ToString("F3", CultureInfo.InvariantCulture)) +
                    ", roofColliders=" + warehouseRoofColliders.Length +
                    ", hdrpDataAdded=" + addedHdrpData +
                    ", pairedFixtureHdrpData=" + pairedFixtureHdrpData +
                    ", duplicateFixtureHdrpData=" + duplicateFixtureHdrpData +
                    ", fixtureTreeHdrpData=" + fixtureTreeHdrpData.Length +
                    ", orphanFixtureHdrpData=" + orphanFixtureHdrpData +
                    ", pointLights=" + pointLights + ", spotLights=" + spotLights +
                    ", nonFixtureSpotLights=" + nonFixtureSpotLights +
                    ", enabledDirectional=" + directional.Count(light => light.enabled) +
                    ", loadedDirectionals=" + loadedDirectionals.Length +
                    ", enabledLoadedDirectionals=" + enabledLoadedDirectionals +
                    ", warehouseEnvironment=" + warehouseEnvironmentContract +
                    ", disabledSentinelDirectional=" + directional.Count(light => !light.enabled) +
                    (invalidSamples.Count == 0 ? string.Empty : ", invalidSamples=[" + string.Join(" | ", invalidSamples) + "]") + ".");
        return passed;
    }

    private static bool TryRuntimeFixtureRoofGap(Transform fixture, Collider[] roofColliders,
        out float gap, out float fixtureTop)
    {
        gap = float.NaN;
        fixtureTop = float.NaN;
        if (fixture == null || roofColliders == null || roofColliders.Length == 0 ||
            !TryRuntimeRendererBounds(fixture.gameObject, out Bounds bounds))
            return false;
        fixtureTop = bounds.max.y;
        float minX = bounds.min.x;
        float maxX = bounds.max.x;
        float minZ = bounds.min.z;
        float maxZ = bounds.max.z;
        Vector2[] samples =
        {
            new Vector2(bounds.center.x, bounds.center.z),
            new Vector2(minX, minZ), new Vector2(minX, maxZ),
            new Vector2(maxX, minZ), new Vector2(maxX, maxZ),
            new Vector2(minX, bounds.center.z), new Vector2(maxX, bounds.center.z),
            new Vector2(bounds.center.x, minZ), new Vector2(bounds.center.x, maxZ)
        };
        float lowest = float.PositiveInfinity;
        foreach (Vector2 sample in samples)
        {
            if (!TryRuntimeWarehouseRoofSurface(roofColliders, sample.x, sample.y, out float surface))
                return false;
            if (surface < lowest) lowest = surface;
        }
        gap = lowest - fixtureTop;
        return float.IsFinite(gap);
    }

    private static bool TryRuntimeWarehouseRoofSurface(Collider[] roofColliders, float worldX,
        float worldZ, out float surfaceY)
    {
        surfaceY = float.NaN;
        Ray ray = new Ray(new Vector3(worldX, WarehouseRoofHeight + 1f, worldZ), Vector3.down);
        float nearestDistance = float.PositiveInfinity;
        foreach (Collider collider in roofColliders)
        {
            if (collider != null && collider.Raycast(ray, out RaycastHit hit, WarehouseRoofHeight + 3f) &&
                hit.distance < nearestDistance)
                nearestDistance = hit.distance;
        }
        if (!float.IsFinite(nearestDistance)) return false;
        surfaceY = ray.origin.y - nearestDistance;
        return float.IsFinite(surfaceY);
    }

    private bool ValidateTacticalEnemyPlacement(GameObject root, bool allowRepair, out int markerCount,
        out int certifiedSafeCapacity)
    {
        Transform[] markers = root.GetComponentsInChildren<Transform>(true)
            .Where(item => item.name.StartsWith("PVE_EnemySpawn_", StringComparison.Ordinal)).ToArray();
        markerCount = markers.Length;
        certifiedSafeCapacity = 0;
        int valid = 0;
        int repaired = 0;
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var invalidSamples = new List<string>();
        foreach (Transform marker in markers)
        {
            Transform envelope = marker.parent;
            if (envelope == null || !envelope.name.StartsWith("TACTICAL_POSITION_", StringComparison.Ordinal)) continue;
            Transform role = FindDirectChild(envelope, "TACTICAL_ROLE_");
            Transform cover = FindDirectChild(envelope, "TACTICAL_COVER_POINT_");
            Transform threat = FindDirectChild(envelope, "TACTICAL_THREAT_POINT_");
            Transform profile = FindDirectChild(envelope, "TACTICAL_NATIVE_BRAINAI_WANDER_RADIUS_12M");
            if (role == null || cover == null || threat == null || profile == null) continue;
            roles.Add(role.name);
            if (allowRepair && TryRepairRuntimeTacticalStandingPosition(marker, role, cover, threat)) repaired++;

            Vector3 toThreat = threat.position - marker.position;
            toThreat.y = 0f;
            Vector3 facing = marker.forward;
            facing.y = 0f;
            float authoredCoverDistance = Vector2.Distance(new Vector2(role.position.x, role.position.z),
                new Vector2(cover.position.x, cover.position.z));
            float coverDistance = Vector2.Distance(new Vector2(marker.position.x, marker.position.z),
                new Vector2(cover.position.x, cover.position.z));
            float threatDistance = toThreat.magnitude;
            float facingAlignment = threatDistance < .01f ? -1f : Vector3.Dot(facing.normalized, toThreat.normalized);
            Vector3 capsuleBottom = marker.position + Vector3.up * .42f;
            Vector3 capsuleTop = marker.position + Vector3.up * 1.58f;
            int standingObstructionMask = Physics.DefaultRaycastLayers;
            foreach (string dynamicLayerName in new[] { "LocalPlayer", "Character", "Hitbox" })
            {
                int dynamicLayer = LayerMask.NameToLayer(dynamicLayerName);
                if (dynamicLayer >= 0) standingObstructionMask &= ~(1 << dynamicLayer);
            }
            Collider[] standingHits = Physics.OverlapCapsule(capsuleBottom, capsuleTop, .3f,
                standingObstructionMask, QueryTriggerInteraction.Ignore);
            bool standingClear = standingHits.Length == 0;
            Collider[] nearbyCover = Physics.OverlapSphere(cover.position, .72f, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            bool coverBacked = nearbyCover.Any(collider => collider != null && HasNativeCoverAncestor(collider.transform));
            if (authoredCoverDistance >= .35f && authoredCoverDistance <= 4.25f &&
                coverDistance >= .15f && coverDistance <= 4.50f && threatDistance >= 1.15f &&
                facingAlignment >= .78f && standingClear && coverBacked) valid++;
            else if (invalidSamples.Count < 8)
                invalidSamples.Add(marker.name + "{authoredCover=" + authoredCoverDistance.ToString("F2", CultureInfo.InvariantCulture) +
                    ",liveCover=" + coverDistance.ToString("F2", CultureInfo.InvariantCulture) +
                    ",threat=" + threatDistance.ToString("F2", CultureInfo.InvariantCulture) +
                    ",facing=" + facingAlignment.ToString("F2", CultureInfo.InvariantCulture) +
                    ",standingClear=" + standingClear + ",standingMask=" + DescribeLayers(standingObstructionMask) +
                    ",standingHits=" + string.Join(";", standingHits.Take(6).Select(collider =>
                        collider.name + "@" + LayerMask.LayerToName(collider.gameObject.layer) +
                        "/" + (collider.transform.parent == null ? "<root>" : collider.transform.parent.name))) +
                    ",coverBacked=" + coverBacked + "}");
        }

        int tacticalPositions = root.GetComponentsInChildren<Transform>(true)
            .Count(item => item.name.StartsWith("TACTICAL_POSITION_", StringComparison.Ordinal));
        int uniqueMarkerNames = markers.Select(item => item.name).Distinct(StringComparer.Ordinal).Count();
        int markersOnGraph = 0;
        int tightlyGroundedMarkers = 0;
        float certifiedMinimumSeparation = float.PositiveInfinity;
        if (!allowRepair)
        {
            var accepted = new List<Transform>();
            foreach (Transform marker in markers.OrderBy(item => item.name, StringComparer.Ordinal))
            {
                bool onGraph = runtimeNavigationAstar != null &&
                               runtimeNavigationAstar.IsPointOnNavmesh(marker.position);
                bool grounded = HasTightMarkerGroundSupport(marker, root.scene);
                if (onGraph) markersOnGraph++;
                if (grounded) tightlyGroundedMarkers++;
                if (!onGraph || !grounded) continue;
                if (accepted.Any(other => Vector2.Distance(
                        new Vector2(marker.position.x, marker.position.z),
                        new Vector2(other.position.x, other.position.z)) <
                    MinimumPveMarkerPlanarSeparationMeters)) continue;
                foreach (Transform other in accepted)
                {
                    float separation = Vector2.Distance(new Vector2(marker.position.x, marker.position.z),
                        new Vector2(other.position.x, other.position.z));
                    certifiedMinimumSeparation = Mathf.Min(certifiedMinimumSeparation, separation);
                }
                accepted.Add(marker);
            }
            certifiedSafeCapacity = accepted.Count;
        }

        bool finalized = allowRepair ||
                         (uniqueMarkerNames == markerCount && markersOnGraph == markerCount &&
                          tightlyGroundedMarkers == markerCount &&
                          certifiedSafeCapacity >= CertifiedPveMaximumEnemies);
        bool passed = markerCount >= MinimumAuthoredPveEnemyMarkers && tacticalPositions == markerCount &&
                      valid == markerCount &&
                      roles.Count >= 3 && finalized;
        log.LogInfo("Vektor Kill House tactical enemy placement: passed=" + passed +
                    ", phase=" + (allowRepair ? "repair" : "post-snap-final") +
                    ", markers=" + markerCount + ", validCoverFacingAndClearance=" + valid +
                    ", runtimeColliderRepairs=" + repaired + ", distinctRoles=" + roles.Count +
                    ", uniqueMarkerNames=" + uniqueMarkerNames + ", markersOnGraph=" + markersOnGraph +
                    ", tightlyGroundedMarkers=" + tightlyGroundedMarkers +
                    ", certifiedSafeCapacity=" + certifiedSafeCapacity + "/" + CertifiedPveMaximumEnemies +
                    ", minimumAcceptedPlanarSeparationMeters=" +
                    (float.IsPositiveInfinity(certifiedMinimumSeparation) ? "n/a" :
                        certifiedMinimumSeparation.ToString("F3", CultureInfo.InvariantCulture)) +
                    ", nativeIdleBehavior=Wander, wanderRadiusMeters=12" +
                    ", comms=true, counterSuppression=true" +
                    (invalidSamples.Count == 0 ? string.Empty : ", invalidSamples=[" + string.Join(" | ", invalidSamples) + "]") + ".");
        return passed;
    }

    private static bool HasTightMarkerGroundSupport(Transform marker, Scene expectedScene)
    {
        if (marker == null || !expectedScene.IsValid() || !expectedScene.isLoaded) return false;
        Vector3 origin = marker.position + Vector3.up * .18f;
        if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, .50f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
        Collider collider = hit.collider;
        return collider != null && collider.enabled && !collider.isTrigger &&
               collider.gameObject.scene.IsValid() && collider.gameObject.scene.handle == expectedScene.handle &&
               hit.normal.y >= .65f && Mathf.Abs(marker.position.y - hit.point.y) <= .12f;
    }

    private bool ValidateRuntimePlayerSpawnAndColliderContract(GameObject root)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        Transform[] pve = transforms.Where(item =>
                item.name.StartsWith("PVE_PlayerSpawn_", StringComparison.Ordinal))
            .OrderBy(item => item.name, StringComparer.Ordinal).ToArray();
        Transform[] team1 = transforms.Where(item =>
                item.name.StartsWith("PVP_Team1Spawn_", StringComparison.Ordinal))
            .OrderBy(item => item.name, StringComparer.Ordinal).ToArray();
        Transform[] team2 = transforms.Where(item =>
                item.name.StartsWith("PVP_Team2Spawn_", StringComparison.Ordinal))
            .OrderBy(item => item.name, StringComparer.Ordinal).ToArray();
        Transform[] all = pve.Concat(team1).Concat(team2).ToArray();

        int obstructionMask = Physics.DefaultRaycastLayers;
        foreach (string dynamicLayerName in new[] { "LocalPlayer", "Character", "Hitbox" })
        {
            int dynamicLayer = LayerMask.NameToLayer(dynamicLayerName);
            if (dynamicLayer >= 0) obstructionMask &= ~(1 << dynamicLayer);
        }

        int finite = all.Count(marker => MarkerTransformIsFinite(marker));
        int grounded = all.Count(marker => HasTightMarkerGroundSupport(marker, root.scene));
        int capsuleClear = all.Count(marker => Physics.OverlapCapsule(
            marker.position + Vector3.up * .42f, marker.position + Vector3.up * 1.58f, .30f,
            obstructionMask, QueryTriggerInteraction.Ignore).Length == 0);
        bool exactNames = pve.Select(item => item.name).SequenceEqual(
                              Enumerable.Range(1, 4).Select(index =>
                                  "PVE_PlayerSpawn_" + index.ToString("00", CultureInfo.InvariantCulture))) &&
                          team1.Select(item => item.name).SequenceEqual(
                              Enumerable.Range(1, 6).Select(index =>
                                  "PVP_Team1Spawn_" + index.ToString("00", CultureInfo.InvariantCulture))) &&
                          team2.Select(item => item.name).SequenceEqual(
                              Enumerable.Range(1, 6).Select(index =>
                                  "PVP_Team2Spawn_" + index.ToString("00", CultureInfo.InvariantCulture)));
        bool teamSpacing = MinimumPairwisePlanarSeparation(team1) >= 1.5f &&
                           MinimumPairwisePlanarSeparation(team2) >= 1.5f;
        bool passed = pve.Length == 4 && team1.Length == 6 && team2.Length == 6 && exactNames &&
                      finite == all.Length && grounded == all.Length && capsuleClear == all.Length && teamSpacing;
        log.LogInfo("Vektor Kill House player-spawn collider gate: passed=" + passed +
                    ", counts=" + pve.Length + "/" + team1.Length + "/" + team2.Length +
                    ", exactNames=" + exactNames + ", finite=" + finite + "/" + all.Length +
                    ", grounded=" + grounded + "/" + all.Length + ", capsuleClear=" + capsuleClear +
                    "/" + all.Length + ", teamSpacing=" + teamSpacing + ".");
        return passed;
    }

    private static bool MarkerTransformIsFinite(Transform marker)
    {
        if (marker == null) return false;
        Vector3 position = marker.position;
        Quaternion rotation = marker.rotation;
        return !(float.IsNaN(position.x) || float.IsInfinity(position.x) ||
                 float.IsNaN(position.y) || float.IsInfinity(position.y) ||
                 float.IsNaN(position.z) || float.IsInfinity(position.z) ||
                 float.IsNaN(rotation.x) || float.IsInfinity(rotation.x) ||
                 float.IsNaN(rotation.y) || float.IsInfinity(rotation.y) ||
                 float.IsNaN(rotation.z) || float.IsInfinity(rotation.z) ||
                 float.IsNaN(rotation.w) || float.IsInfinity(rotation.w));
    }

    private static float MinimumPairwisePlanarSeparation(IReadOnlyList<Transform> markers)
    {
        if (markers.Count < 2) return float.PositiveInfinity;
        float minimum = float.PositiveInfinity;
        for (int first = 0; first < markers.Count; first++)
        for (int second = first + 1; second < markers.Count; second++)
            minimum = Mathf.Min(minimum, Vector2.Distance(
                new Vector2(markers[first].position.x, markers[first].position.z),
                new Vector2(markers[second].position.x, markers[second].position.z)));
        return minimum;
    }

    private static bool TryRepairRuntimeTacticalStandingPosition(Transform marker, Transform role, Transform cover,
        Transform threat)
    {
        int obstructionMask = Physics.DefaultRaycastLayers;
        foreach (string dynamicLayerName in new[] { "LocalPlayer", "Character", "Hitbox" })
        {
            int dynamicLayer = LayerMask.NameToLayer(dynamicLayerName);
            if (dynamicLayer >= 0) obstructionMask &= ~(1 << dynamicLayer);
        }
        Vector3 initialBottom = marker.position + Vector3.up * .42f;
        Vector3 initialTop = marker.position + Vector3.up * 1.58f;
        if (Physics.OverlapCapsule(initialBottom, initialTop, .3f, obstructionMask,
                QueryTriggerInteraction.Ignore).Length == 0) return false;

        Vector3 away = marker.position - cover.position;
        away.y = 0f;
        if (away.sqrMagnitude < .01f) away = marker.position - threat.position;
        away.y = 0f;
        if (away.sqrMagnitude < .01f) away = Vector3.forward;
        away.Normalize();
        Vector3 side = new Vector3(-away.z, 0f, away.x);
        Vector3[] offsets =
        {
            away * .45f, away * .75f, away * 1.05f, away * 1.35f,
            away * .75f + side * .45f, away * .75f - side * .45f,
            away * 1.05f + side * .65f, away * 1.05f - side * .65f
        };
        foreach (Vector3 offset in offsets)
        {
            Vector3 candidate = marker.position + offset;
            Vector3 bottom = candidate + Vector3.up * .42f;
            Vector3 top = candidate + Vector3.up * 1.58f;
            if (Physics.OverlapCapsule(bottom, top, .3f, obstructionMask,
                    QueryTriggerInteraction.Ignore).Length != 0) continue;
            float coverDistance = Vector2.Distance(new Vector2(candidate.x, candidate.z),
                new Vector2(cover.position.x, cover.position.z));
            float threatDistance = Vector2.Distance(new Vector2(candidate.x, candidate.z),
                new Vector2(threat.position.x, threat.position.z));
            if (coverDistance < .45f || coverDistance > 4.25f || threatDistance < 1.25f) continue;
            marker.position = candidate;
            role.position = candidate;
            Vector3 facing = threat.position - candidate;
            facing.y = 0f;
            if (facing.sqrMagnitude >= .01f) marker.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
            Physics.SyncTransforms();
            return true;
        }
        return false;
    }

    private static Transform FindDirectChild(Transform parent, string prefix)
    {
        if (parent == null) return null;
        for (int index = 0; index < parent.childCount; index++)
        {
            Transform child = parent.GetChild(index);
            if (child != null && child.name.StartsWith(prefix, StringComparison.Ordinal)) return child;
        }
        return null;
    }

    private static bool HasNativeCoverAncestor(Transform transform)
    {
        while (transform != null)
        {
            if (transform.name.StartsWith("NATIVE_", StringComparison.Ordinal) &&
                !string.Equals(transform.name, "NATIVE_Floor", StringComparison.Ordinal) &&
                !string.Equals(transform.name, "NATIVE_Ceiling", StringComparison.Ordinal)) return true;
            transform = transform.parent;
        }
        return false;
    }

    private bool EnsureNativeDoorV2Runtime(GameObject root)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        Transform[] sockets = transforms.Where(item => item.name.StartsWith("DOORV2_SOCKET_", StringComparison.Ordinal)).ToArray();
        Transform[] shells = transforms.Where(item => string.Equals(item.name, DoorShellName, StringComparison.Ordinal)).ToArray();
        Transform[] banks = transforms.Where(item => string.Equals(item.name, DoorAudioBankName, StringComparison.Ordinal)).ToArray();
        if (sockets.Length < 1 || shells.Length != sockets.Length || banks.Length != 1)
        {
            log.LogError("Vektor Kill House DoorV2 gate failed: sockets=" + sockets.Length +
                         ", shells=" + shells.Length + ", audioBanks=" + banks.Length + ".");
            return false;
        }

        Dictionary<string, AudioClip> clips = BuildDoorAudioLibrary(banks[0]);
        if (!ValidateDoorAudioLibrary(clips)) return false;

        GameObject registeredPrefab = null;
        bool registered = NetworkClient.GetPrefab(OfficialDoorV2AssetId, out registeredPrefab) && registeredPrefab != null;
        DoorV2 registeredDoor = registered ? registeredPrefab.GetComponent<DoorV2>() : null;
        bool registeredComplete = IsCompleteDoorV2(registeredDoor);

        // A non-authoritative client must not activate the portable shells. Mirror will instantiate
        // OPERATOR's registered DoorV2 prefab when the authoritative reconstruction is spawned.
        if (NetworkClient.active && !NetworkServer.active)
        {
            log.LogInfo("Vektor Kill House DoorV2 client provisioning: registered=" + registered +
                        ", complete=" + registeredComplete + ", assetId=" + OfficialDoorV2AssetId +
                        ", localShellsKeptInactive=" + shells.Length + ".");
            return registeredComplete && shells.All(shell => !shell.gameObject.activeSelf);
        }

        int configured = 0;
        int spawned = 0;
        foreach (Transform shell in shells)
        {
            try
            {
                DoorV2 door = shell.GetComponent<DoorV2>();
                NetworkIdentity identity;
                if (door == null)
                {
                    door = ConfigureNativeDoorV2Shell(shell, clips, out identity);
                    configured++;
                }
                else
                {
                    identity = shell.GetComponent<NetworkIdentity>();
                }

                if (!ValidateReconstructedDoorV2Shell(shell, door) || !DoorMatchesNativeOpening(shell) || identity == null)
                    throw new InvalidOperationException("DoorV2 graph or closed-leaf doorway alignment did not close after reconstruction.");

                if (!shell.gameObject.activeSelf) shell.gameObject.SetActive(true);
                if (NetworkServer.active && identity.netId == 0)
                {
                    NetworkServer.Spawn(shell.gameObject, OfficialDoorV2AssetId, (NetworkConnection)null);
                    if (identity.netId == 0)
                        throw new InvalidOperationException("Mirror did not assign a netId to the reconstructed DoorV2.");
                    spawned++;
                }
            }
            catch (Exception exception)
            {
                if (shell != null) shell.gameObject.SetActive(false);
                log.LogError("Vektor Kill House DoorV2 shell failed: socket=" +
                             (shell == null || shell.parent == null ? "<unknown>" : shell.parent.name) + ", " +
                             exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        Physics.SyncTransforms();
        foreach (Transform shell in shells.Where(item =>
                     !ValidateReconstructedDoorV2Shell(item, item.GetComponent<DoorV2>()) || !DoorMatchesNativeOpening(item)))
            log.LogError("Vektor Kill House DoorV2 final validation detail: " + DescribeDoorValidation(shell));
        int completeDoors = shells.Count(shell => ValidateReconstructedDoorV2Shell(shell, shell.GetComponent<DoorV2>()) &&
                                                  DoorMatchesNativeOpening(shell));
        int activeDoors = shells.Count(shell => shell.gameObject.activeSelf);
        int primitiveMeshes = shells.Sum(shell => shell.GetComponentsInChildren<MeshFilter>(true).Count(filter =>
            filter.sharedMesh != null && IsBuiltinPrimitiveName(filter.sharedMesh.name)));
        bool passed = completeDoors == shells.Length && activeDoors == shells.Length && primitiveMeshes == 0 &&
                      (!NetworkServer.active || shells.All(shell => shell.GetComponent<NetworkIdentity>().netId != 0));
        log.LogInfo("Vektor Kill House DoorV2 reconstruction: passed=" + passed + ", shells=" + shells.Length +
                    ", configured=" + configured + ", complete=" + completeDoors + ", active=" + activeDoors +
                    ", mirrorSpawned=" + spawned + ", registeredVanilla=" + registeredComplete +
                    ", primitiveMeshes=" + primitiveMeshes + ".");
        return passed;
    }

    private static Dictionary<string, AudioClip> BuildDoorAudioLibrary(Transform bank)
    {
        var clips = new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        foreach (AudioSource source in bank.GetComponentsInChildren<AudioSource>(true))
        {
            if (source == null || source.clip == null) continue;
            clips[source.clip.name] = source.clip;
        }
        return clips;
    }

    private bool ValidateDoorAudioLibrary(IReadOnlyDictionary<string, AudioClip> clips)
    {
        string[] prefixes =
        {
            "wooden door opening ", "wooden door locked ", "wooden door closing ",
            "wooden door thud ", "wooden door breach "
        };
        int[] counts = { 10, 10, 10, 15, 2 };
        bool passed = clips.Count == 47;
        for (int group = 0; group < prefixes.Length; group++)
            for (int index = 1; index <= counts[group]; index++)
                passed &= clips.ContainsKey(prefixes[group] + index.ToString(CultureInfo.InvariantCulture));
        if (!passed)
            log.LogError("Vektor Kill House DoorV2 audio closure failed: uniqueClips=" + clips.Count + ".");
        return passed;
    }

    private DoorV2 ConfigureNativeDoorV2Shell(Transform shell, IReadOnlyDictionary<string, AudioClip> clips,
        out NetworkIdentity identity)
    {
        shell.gameObject.SetActive(false);
        TrySetTag(shell.gameObject, "Door");

        // Component order intentionally mirrors the official _DoorV2_BASE root.
        identity = shell.gameObject.AddComponent<NetworkIdentity>();
        identity.assetId = OfficialDoorV2AssetId;
        DoorV2 door = shell.gameObject.AddComponent<DoorV2>();
        shell.gameObject.AddComponent<ExcludeFromMirrorSpawnable>();
        MilkRigidbodySync milk = shell.gameObject.AddComponent<MilkRigidbodySync>();

        Transform pivot = RequirePath(shell, "Door Pivot and rigidbody");
        Transform center = RequirePath(pivot, "Center");
        Transform doorModel = RequirePath(pivot, "Door Model");
        Transform placeholder = RequirePath(doorModel, "cubey model/PLACEHOLDER DOOR MODEL");
        Transform interior = RequirePath(doorModel, "Door_interior");
        Transform handle01 = RequirePath(pivot, "Handle01");
        Transform handle02 = RequirePath(pivot, "Handle02");
        Transform hingeTop = RequirePath(pivot, "Hinge Top");
        Transform hingeBottom = RequirePath(pivot, "Hinge Bottom");
        Transform doorLock = RequirePath(pivot, "Lock");
        Transform destroyed = RequirePath(pivot, "Suburb Door Exploded");
        Transform openableSource = RequirePath(shell, "Openable NavMesh Link Source");
        Transform walkableSource = RequirePath(shell, "Walkable NavMeshLink Source");
        Transform navmeshCutTransform = RequirePath(pivot, "NavmeshCut");

        InteractionObject interaction4 = ConfigureInteractionObject(RequirePath(pivot, "InteractionL (4)"));
        InteractionObject interaction3 = ConfigureInteractionObject(RequirePath(pivot, "InteractionL (3)"));
        InteractionObject interactionCenter = ConfigureInteractionObject(RequirePath(pivot, "InteractionL Centre"));
        InteractionObject interactionCenter2 = ConfigureInteractionObject(RequirePath(pivot, "InteractionL Centre 2"));

        DoorHandleV2 front = handle01.gameObject.AddComponent<DoorHandleV2>();
        DoorHandleV2 back = handle02.gameObject.AddComponent<DoorHandleV2>();
        front.doorV2 = door;
        back.doorV2 = door;
        front.RivalDoorHandle = back;
        back.RivalDoorHandle = front;
        front.IsFrontHandle = true;
        back.IsFrontHandle = false;
        front.myPushObject = center;
        back.myPushObject = center;
        front.Handle = interaction4;
        front.Center = interactionCenter;
        back.Handle = interaction3;
        back.Center = interactionCenter2;

        ShootableDoorPart lockPart = doorLock.gameObject.AddComponent<ShootableDoorPart>();
        ShootableDoorPart topPart = hingeTop.gameObject.AddComponent<ShootableDoorPart>();
        ShootableDoorPart bottomPart = hingeBottom.gameObject.AddComponent<ShootableDoorPart>();
        lockPart.Door = door;
        lockPart.PartID = 1;
        topPart.Door = door;
        topPart.PartID = 2;
        bottomPart.Door = door;
        bottomPart.PartID = 3;

        DoorHitBox placeholderHitBox = placeholder.gameObject.AddComponent<DoorHitBox>();
        DoorHitBox interiorHitBox = interior.gameObject.AddComponent<DoorHitBox>();
        placeholderHitBox.Door = door;
        interiorHitBox.Door = door;

        foreach (MeshFilter filter in shell.GetComponentsInChildren<MeshFilter>(true))
            if (filter != null && filter.sharedMesh != null && !IsBuiltinPrimitiveName(filter.sharedMesh.name) &&
                filter.GetComponent<BrainFailProductions.PolyFew.PolyFewHost>() == null)
                filter.gameObject.AddComponent<BrainFailProductions.PolyFew.PolyFewHost>();

        NodeLink2 openable = ConfigureNodeLink(openableSource, 2u);
        NodeLink2 walkable = ConfigureNodeLink(walkableSource, 1u);
        NavmeshCut navmeshCut = ConfigureNavmeshCut(navmeshCutTransform);

        Rigidbody pivotBody = RequireComponent<Rigidbody>(pivot);
        BoxCollider hitCollider = RequireComponent<BoxCollider>(placeholder);
        BoxCollider lockCollider = RequireComponent<BoxCollider>(doorLock);
        BoxCollider topCollider = RequireComponent<BoxCollider>(hingeTop);
        BoxCollider bottomCollider = RequireComponent<BoxCollider>(hingeBottom);
        PhysicsMaterial doorMaterial = hitCollider.material;
        Rigidbody[] destroyedBodies = destroyed.GetComponentsInChildren<Rigidbody>(true);
        if (destroyedBodies.Length != 30)
            throw new InvalidOperationException("Vanilla breach-body closure is " + destroyedBodies.Length + ", expected 30.");

        milk.syncDirection = (SyncDirection)0;
        milk.syncMode = (SyncMode)0;
        milk.syncInterval = 0f;
        milk.Active = false;
        milk.UpdatesPerSecond = 30f;
        milk.TransformToSync = pivot;
        milk.SyncPosition = true;
        milk.SyncRotation = true;
        milk.UseLocalSpace = false;
        milk.RB = pivotBody;
        milk.releaseOwnershipDelay = 2f;

        door.syncDirection = (SyncDirection)0;
        door.syncMode = (SyncMode)0;
        door.syncInterval = .1f;
        door.PivotTransform = pivot;
        door.HandleFront = front;
        door.HandleBack = back;
        door.DoorModelParent = doorModel.gameObject;
        door.rb = pivotBody;
        door.DoorMask = (LayerMask)4545;
        door.PlayerMovementLayerMask = (LayerMask)33554436;
        door.DoorPhysicsMaterial = doorMaterial;
        door.DoorPhysicsSync = milk;
        door.DoorHitBox = hitCollider;
        door.maxRotationY = 110f;
        door.Invert = false;
        door.Damping = .5f;
        door.StartLocked = false;
        door.StartLockedChance = 0f;
        door.lockedMesh = new Il2CppReferenceArray<GameObject>(0);
        door.unlockedMesh = new Il2CppReferenceArray<GameObject>(0);
        door.AiCantOpen = false;
        door.AiCantOpenChance = 0f;
        door.DoorOpenableNavLink = openable;
        door.DoorWalkableNavLink = walkable;
        door.NavMeshCut = navmeshCut;
        door.navCutOpenSize = Vector3.zero;
        door.navCutCloseSize = Vector3.zero;
        door.LatchHealth = 400f;
        door.hinge01_Health = 400f;
        door.hinge02_Health = 400f;
        door.latchCollider = lockCollider;
        door.HingeTopCollider = topCollider;
        door.HingeBottomCollider = bottomCollider;
        door.audioSource = RequireComponent<AudioSource>(RequirePath(pivot, "AudioSource"));
        door.doorUnlock = AudioSeries(clips, "wooden door opening ", 10);
        door.doorLocked = AudioSeries(clips, "wooden door locked ", 10);
        door.doorClose = AudioSeries(clips, "wooden door closing ", 10);
        door.doorThud = AudioSeries(clips, "wooden door thud ", 15);
        door.doorBreach = AudioSeries(clips, "wooden door breach ", 2);
        door.deadDoorSpringStrength = 400f;
        door.deadDoorDamping = 8f;
        door.deadDoorAngularDamping = 5f;
        door.deadDoorScrollForce = 200f;
        door.deadDoorWalkForce = 200f;
        door.canBlowup = false;
        door.DestroyedDoor = destroyed.gameObject;
        door.DestroyedDoorRB = ToReferenceArray(destroyedBodies);
        door.canLatch = true;
        door.IsLatched = true;
        return door;
    }

    private static InteractionObject ConfigureInteractionObject(Transform interactionTransform)
    {
        InteractionObject interaction = interactionTransform.gameObject.AddComponent<InteractionObject>();
        var weightCurve = new InteractionObject.WeightCurve
        {
            type = (InteractionObject.WeightCurve.Type)0,
            curve = CreateDoorInteractionCurve()
        };
        interaction.weightCurves = new Il2CppReferenceArray<InteractionObject.WeightCurve>(1);
        interaction.weightCurves[0] = weightCurve;

        interaction.multipliers = new Il2CppReferenceArray<InteractionObject.Multiplier>(2);
        interaction.multipliers[0] = new InteractionObject.Multiplier
        {
            curve = (InteractionObject.WeightCurve.Type)0,
            multiplier = 1f,
            result = (InteractionObject.WeightCurve.Type)7
        };
        interaction.multipliers[1] = new InteractionObject.Multiplier
        {
            curve = (InteractionObject.WeightCurve.Type)0,
            multiplier = 1f,
            result = (InteractionObject.WeightCurve.Type)10
        };

        var interactionEvent = new InteractionObject.InteractionEvent
        {
            time = .5f,
            pause = true,
            pickUp = false,
            animations = new Il2CppReferenceArray<InteractionObject.AnimatorEvent>(0),
            messages = new Il2CppReferenceArray<InteractionObject.Message>(0),
            unityEvent = new UnityEvent()
        };
        interaction.events = new Il2CppReferenceArray<InteractionObject.InteractionEvent>(1);
        interaction.events[0] = interactionEvent;

        Transform hand = RequirePath(interactionTransform, "hand_l");
        InteractionTarget target = hand.gameObject.AddComponent<InteractionTarget>();
        target.effectorType = (FullBodyBipedEffector)5;
        target.multipliers = new Il2CppReferenceArray<InteractionTarget.Multiplier>(0);
        target.interactionSpeedMlp = 1f;
        target.pivot = null;
        target.rotationMode = (InteractionTarget.RotationMode)0;
        target.twistAxis = Vector3.up;
        target.twistWeight = 1f;
        target.swingWeight = 0f;
        target.threeDOFWeight = 1f;
        target.rotateOnce = true;
        return interaction;
    }

    private static AnimationCurve CreateDoorInteractionCurve()
    {
        Keyframe first = new Keyframe(0f, .026881754f, 1.9333475f, 1.9333475f, 0f, .33333334f);
        Keyframe middle = new Keyframe(.50333333f, 1f, -.012975514f, -.012975514f, .33333334f, .33333334f);
        Keyframe last = new Keyframe(1f, .026881754f, -1.9592985f, -1.9592985f, .33333334f, 0f);
        first.tangentMode = 34;
        middle.tangentMode = 34;
        last.tangentMode = 34;
        first.weightedMode = WeightedMode.None;
        middle.weightedMode = WeightedMode.None;
        last.weightedMode = WeightedMode.None;
        var curve = new AnimationCurve(new[] { first, middle, last });
        curve.preWrapMode = (WrapMode)2;
        curve.postWrapMode = (WrapMode)2;
        return curve;
    }

    private static NodeLink2 ConfigureNodeLink(Transform source, uint tag)
    {
        NodeLink2 link = source.gameObject.AddComponent<NodeLink2>();
        link.version = 1073741824;
        link.end = RequirePath(source, "NavMesh Link Dest");
        link.costFactor = 1f;
        link.oneWay = false;
        link.pathfindingTag = new PathfindingTag(tag);
        GraphMask mask = new GraphMask();
        mask.value = -1;
        link.graphMask = mask;
        return link;
    }

    private static NavmeshCut ConfigureNavmeshCut(Transform source)
    {
        NavmeshCut cut = source.gameObject.AddComponent<NavmeshCut>();
        cut.version = 1073741824;
        GraphMask mask = new GraphMask();
        mask.value = 1;
        cut.graphMask = mask;
        cut.type = (NavmeshCut.MeshType)3;
        cut.mesh = null;
        cut.rectangleSize = new Vector2(1.2f, .145f);
        cut.circleRadius = 1f;
        cut.circleResolution = 6;
        cut.height = 2.19f;
        cut.meshScale = 1f;
        cut.center = new Vector3(.63f, .94f, 0f);
        cut.updateDistance = .4f;
        cut.isDual = false;
        cut.radiusExpansionMode = (NavmeshCut.RadiusExpansionMode)1;
        cut.cutsAddedGeom = true;
        cut.updateRotationDistance = 10f;
        cut.useRotationAndScale = true;
        return cut;
    }

    private static Il2CppReferenceArray<AudioClip> AudioSeries(IReadOnlyDictionary<string, AudioClip> clips,
        string prefix, int count)
    {
        var result = new Il2CppReferenceArray<AudioClip>(count);
        for (int index = 1; index <= count; index++)
            result[index - 1] = clips[prefix + index.ToString(CultureInfo.InvariantCulture)];
        return result;
    }

    private static Il2CppReferenceArray<Rigidbody> ToReferenceArray(IReadOnlyList<Rigidbody> values)
    {
        var result = new Il2CppReferenceArray<Rigidbody>(values.Count);
        for (int index = 0; index < values.Count; index++) result[index] = values[index];
        return result;
    }

    private static bool IsCompleteDoorV2(DoorV2 door)
    {
        return door != null && door.PivotTransform != null && door.HandleFront != null && door.HandleBack != null &&
               door.DoorModelParent != null && door.rb != null && door.DoorPhysicsMaterial != null &&
               door.DoorPhysicsSync != null && door.DoorHitBox != null && door.latchCollider != null &&
               door.HingeTopCollider != null && door.HingeBottomCollider != null && door.DoorOpenableNavLink != null &&
               door.DoorWalkableNavLink != null && door.NavMeshCut != null && door.audioSource != null &&
               door.DestroyedDoor != null && door.DestroyedDoorRB != null && door.DestroyedDoorRB.Length == 30 &&
               door.doorUnlock != null && door.doorUnlock.Length == 10 && door.doorLocked != null &&
               door.doorLocked.Length == 10 && door.doorClose != null && door.doorClose.Length == 10 &&
               door.doorThud != null && door.doorThud.Length == 15 && door.doorBreach != null &&
               door.doorBreach.Length == 2;
    }

    private static bool ValidateReconstructedDoorV2Shell(Transform shell, DoorV2 door)
    {
        if (!IsCompleteDoorV2(door)) return false;
        Component[] rootComponents = shell.GetComponents<Component>();
        bool rootOrder = rootComponents.Length == 5 &&
                         SameNativeComponent(rootComponents[0], shell) &&
                         SameNativeComponent(rootComponents[1], shell.GetComponent<NetworkIdentity>()) &&
                         SameNativeComponent(rootComponents[2], door) &&
                         SameNativeComponent(rootComponents[3], shell.GetComponent<ExcludeFromMirrorSpawnable>()) &&
                         SameNativeComponent(rootComponents[4], shell.GetComponent<MilkRigidbodySync>());
        int primitiveMeshes = shell.GetComponentsInChildren<MeshFilter>(true).Count(filter =>
            filter.sharedMesh != null && IsBuiltinPrimitiveName(filter.sharedMesh.name));
        Transform pivot = shell.Find("Door Pivot and rigidbody");
        Transform intactLeaf = shell.Find("Door Pivot and rigidbody/Door Model/Door_interior");
        MeshFilter intactFilter = intactLeaf == null ? null : intactLeaf.GetComponent<MeshFilter>();
        Renderer intactRenderer = intactLeaf == null ? null : intactLeaf.GetComponent<Renderer>();
        int staticShadowBlockers = shell.GetComponentsInChildren<Transform>(true).Count(value =>
            value.name.StartsWith("SHADOW BLOCKER Door_interior", StringComparison.Ordinal));
        bool animatedRuggedLeaf = pivot != null && intactLeaf != null && intactLeaf.IsChildOf(pivot) &&
                                  intactFilter != null && intactFilter.sharedMesh != null &&
                                  intactFilter.sharedMesh.name.StartsWith("SM_Door_2_LOD0", StringComparison.Ordinal) &&
                                  intactRenderer != null && intactRenderer.enabled && staticShadowBlockers == 0;
        return rootOrder && animatedRuggedLeaf && shell.GetComponentsInChildren<Transform>(true).Length == 118 &&
               shell.GetComponentsInChildren<MeshFilter>(true).Length == 31 &&
               shell.GetComponentsInChildren<Renderer>(true).Length == 31 &&
               shell.GetComponentsInChildren<BoxCollider>(true).Length == 35 &&
               shell.GetComponentsInChildren<Rigidbody>(true).Length == 31 &&
               shell.GetComponentsInChildren<AudioSource>(true).Length == 1 &&
               shell.GetComponentsInChildren<DoorHandleV2>(true).Length == 2 &&
               shell.GetComponentsInChildren<InteractionObject>(true).Length == 4 &&
               shell.GetComponentsInChildren<InteractionTarget>(true).Length == 4 &&
               shell.GetComponentsInChildren<ShootableDoorPart>(true).Length == 3 &&
               shell.GetComponentsInChildren<DoorHitBox>(true).Length == 2 &&
               shell.GetComponentsInChildren<NodeLink2>(true).Length == 2 &&
               shell.GetComponentsInChildren<NavmeshCut>(true).Length == 1 &&
               shell.GetComponentsInChildren<BrainFailProductions.PolyFew.PolyFewHost>(true).Length == 31 &&
               primitiveMeshes == 0;
    }

    private static bool DoorMatchesNativeOpening(Transform shell)
    {
        if (shell == null || shell.parent == null || shell.parent.parent == null ||
            !shell.parent.name.StartsWith("DOORV2_SOCKET_", StringComparison.Ordinal)) return false;
        string key = shell.parent.name.Substring("DOORV2_SOCKET_".Length);
        Transform wall = shell.parent.parent.Find("NATIVE_DoorWall_" + key);
        if (wall == null) return false;

        // Wall2MeterDoor is authored in Y-Z (local right is its normal). Door_interior is authored
        // in X-Y (shell forward is its normal). Plane alignment alone previously accepted a hinge
        // placed at the aperture centre. Validate the physical and visible leaf centres as well.
        float socketAlignment = Mathf.Abs(Vector3.Dot(shell.parent.forward.normalized, wall.right.normalized));
        float shellAlignment = Mathf.Abs(Vector3.Dot(shell.forward.normalized, wall.right.normalized));
        DoorV2 door = shell.GetComponent<DoorV2>();
        bool closedPivot = door != null && door.PivotTransform != null &&
                           Quaternion.Angle(door.PivotTransform.localRotation, Quaternion.identity) <= .5f;
        Transform placeholder = shell.Find("Door Pivot and rigidbody/Door Model/cubey model/PLACEHOLDER DOOR MODEL");
        Transform interior = shell.Find("Door Pivot and rigidbody/Door Model/Door_interior");
        BoxCollider physicalLeaf = placeholder == null ? null : placeholder.GetComponent<BoxCollider>();
        MeshFilter visibleLeaf = interior == null ? null : interior.GetComponent<MeshFilter>();
        MeshFilter wallMesh = wall.GetComponentInChildren<MeshFilter>(true);
        if (physicalLeaf == null || visibleLeaf == null || visibleLeaf.sharedMesh == null ||
            wallMesh == null || wallMesh.sharedMesh == null) return false;
        Vector3 physicalCenter = physicalLeaf.transform.TransformPoint(physicalLeaf.center);
        Vector3 visibleCenter = visibleLeaf.transform.TransformPoint(visibleLeaf.sharedMesh.bounds.center);
        Vector3 openingCenter = wallMesh.transform.TransformPoint(wallMesh.sharedMesh.bounds.center) +
                                wall.forward * DoorwayOpeningTangentOffset;
        float physicalCenterError = HorizontalDistanceInWallPlane(physicalCenter, openingCenter, wall);
        float visibleCenterError = HorizontalDistanceInWallPlane(visibleCenter, openingCenter, wall);
        float visiblePhysicalError = Vector2.Distance(
            new Vector2(visibleCenter.x, visibleCenter.z), new Vector2(physicalCenter.x, physicalCenter.z));
        bool hingeOffset = Mathf.Abs(shell.localPosition.x + DoorHingeToLeafCenter) <= .005f &&
                           Mathf.Abs(shell.localPosition.y) <= .005f && Mathf.Abs(shell.localPosition.z) <= .005f;
        return socketAlignment >= .999f && shellAlignment >= .999f &&
               Quaternion.Angle(shell.parent.rotation, shell.rotation) <= .1f && closedPivot && hingeOffset &&
               physicalCenterError <= DoorCenterTolerance && visibleCenterError <= DoorCenterTolerance &&
               visiblePhysicalError <= DoorCenterTolerance;
    }

    private static float HorizontalDistanceInWallPlane(Vector3 value, Vector3 expected, Transform wall)
    {
        Vector3 delta = value - expected;
        float normal = Vector3.Dot(delta, wall.right.normalized);
        float tangent = Vector3.Dot(delta, wall.forward.normalized);
        return Mathf.Sqrt(normal * normal + tangent * tangent);
    }

    private static bool SameNativeComponent(Component left, Component right)
    {
        return left != null && right != null && left.GetInstanceID() == right.GetInstanceID();
    }

    private static string DescribeDoorValidation(Transform shell)
    {
        if (shell == null) return "shell=<null>";
        DoorV2 door = shell.GetComponent<DoorV2>();
        Component[] roots = shell.GetComponents<Component>();
        return "socket=" + (shell.parent == null ? "<none>" : shell.parent.name) +
               ", core=" + IsCompleteDoorV2(door) +
               ", rootComponents=" + string.Join("|", roots.Select(item => item == null ? "<null>" : item.GetType().Name)) +
               ", transforms=" + shell.GetComponentsInChildren<Transform>(true).Length +
               ", meshFilters=" + shell.GetComponentsInChildren<MeshFilter>(true).Length +
               ", renderers=" + shell.GetComponentsInChildren<Renderer>(true).Length +
               ", boxes=" + shell.GetComponentsInChildren<BoxCollider>(true).Length +
               ", bodies=" + shell.GetComponentsInChildren<Rigidbody>(true).Length +
               ", audio=" + shell.GetComponentsInChildren<AudioSource>(true).Length +
               ", handles=" + shell.GetComponentsInChildren<DoorHandleV2>(true).Length +
               ", interactions=" + shell.GetComponentsInChildren<InteractionObject>(true).Length +
               ", targets=" + shell.GetComponentsInChildren<InteractionTarget>(true).Length +
               ", shootable=" + shell.GetComponentsInChildren<ShootableDoorPart>(true).Length +
               ", hitBoxes=" + shell.GetComponentsInChildren<DoorHitBox>(true).Length +
               ", links=" + shell.GetComponentsInChildren<NodeLink2>(true).Length +
               ", cuts=" + shell.GetComponentsInChildren<NavmeshCut>(true).Length +
               ", polyFew=" + shell.GetComponentsInChildren<BrainFailProductions.PolyFew.PolyFewHost>(true).Length +
               ", alignedWithOpening=" + DoorMatchesNativeOpening(shell) +
               ", primitives=" + shell.GetComponentsInChildren<MeshFilter>(true).Count(filter =>
                   filter.sharedMesh != null && IsBuiltinPrimitiveName(filter.sharedMesh.name)) +
               ", physicsMaterial=" + (door != null && door.DoorPhysicsMaterial != null) +
               ", unlock=" + (door == null || door.doorUnlock == null ? -1 : door.doorUnlock.Length) +
               ", locked=" + (door == null || door.doorLocked == null ? -1 : door.doorLocked.Length) +
               ", close=" + (door == null || door.doorClose == null ? -1 : door.doorClose.Length) +
               ", thud=" + (door == null || door.doorThud == null ? -1 : door.doorThud.Length) +
               ", breach=" + (door == null || door.doorBreach == null ? -1 : door.doorBreach.Length) + ".";
    }

    private static Transform RequirePath(Transform root, string path)
    {
        Transform value = root.Find(path);
        if (value == null) throw new InvalidOperationException("DoorV2 transform is missing: " + path + ".");
        return value;
    }

    private static T RequireComponent<T>(Transform transform) where T : Component
    {
        T value = transform.GetComponent<T>();
        if (value == null) throw new InvalidOperationException("DoorV2 component is missing: " + typeof(T).Name +
                                                               " on " + transform.name + ".");
        return value;
    }

    private static bool IsBuiltinPrimitiveName(string name)
    {
        return string.Equals(name, "Cube", StringComparison.Ordinal) ||
               string.Equals(name, "Sphere", StringComparison.Ordinal) ||
               string.Equals(name, "Capsule", StringComparison.Ordinal) ||
               string.Equals(name, "Cylinder", StringComparison.Ordinal) ||
               string.Equals(name, "Plane", StringComparison.Ordinal) ||
               string.Equals(name, "Quad", StringComparison.Ordinal);
    }

    private static void TrySetTag(GameObject gameObject, string tag)
    {
        try { gameObject.tag = tag; }
        catch (Exception) { gameObject.tag = "Untagged"; }
    }

    private void AuditResidentDoorTemplates(string phase, bool force)
    {
        if (residentDoorAuditLogged && !force) return;
        try
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Object> objects =
                Resources.FindObjectsOfTypeAll(Il2CppType.Of<DoorV2>());
            int complete = 0;
            var records = new List<string>();
            foreach (UnityEngine.Object value in objects)
            {
                DoorV2 door = value == null ? null : value.TryCast<DoorV2>();
                if (door == null || door.gameObject == null) continue;
                bool graphComplete = door.PivotTransform != null && door.HandleFront != null && door.HandleBack != null &&
                                     door.rb != null && door.DoorPhysicsSync != null && door.DoorHitBox != null &&
                                     door.latchCollider != null && door.HingeTopCollider != null &&
                                     door.HingeBottomCollider != null && door.DoorOpenableNavLink != null &&
                                     door.DoorWalkableNavLink != null && door.NavMeshCut != null &&
                                     door.audioSource != null && door.DestroyedDoor != null;
                if (graphComplete) complete++;
                Scene owner = door.gameObject.scene;
                records.Add("name=" + door.gameObject.name + ", scene=" +
                            (owner.IsValid() ? owner.path : "<asset-or-persistent>") +
                            ", active=" + door.gameObject.activeInHierarchy + ", complete=" + graphComplete);
            }
            GameObject registeredPrefab = null;
            bool registered = NetworkClient.GetPrefab(OfficialDoorV2AssetId, out registeredPrefab) && registeredPrefab != null;
            DoorV2 registeredDoor = registered ? registeredPrefab.GetComponent<DoorV2>() : null;
            bool registeredComplete = registeredDoor != null && registeredDoor.PivotTransform != null &&
                                      registeredDoor.HandleFront != null && registeredDoor.HandleBack != null &&
                                      registeredDoor.rb != null && registeredDoor.DoorPhysicsSync != null &&
                                      registeredDoor.DoorOpenableNavLink != null && registeredDoor.DoorWalkableNavLink != null &&
                                      registeredDoor.NavMeshCut != null && registeredDoor.DestroyedDoor != null;
            residentDoorAuditLogged = true;
            log.LogInfo("Vektor Kill House resident DoorV2 audit: phase=" + phase + ", total=" + records.Count +
                        ", complete=" + complete + ", officialAssetId=" + OfficialDoorV2AssetId +
                        ", registered=" + registered + ", registeredComplete=" + registeredComplete +
                        ", registeredName=" + (registeredPrefab == null ? "<null>" : registeredPrefab.name) +
                        ", records=[" + string.Join(" | ", records.Take(24)) + "].");
        }
        catch (Exception exception)
        {
            log.LogWarning("Vektor Kill House resident DoorV2 audit failed: phase=" + phase + ", " +
                           exception.GetType().Name + ": " + exception.Message);
        }
    }

    private bool ValidateSceneContract(GameObject root)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        string[] primitiveNames = { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };
        int mapMarkers = transforms.Count(item => string.Equals(item.name, MapMarker, StringComparison.Ordinal));
        int pveSpawnSetMarkers = transforms.Count(item => string.Equals(item.name, PveSpawnSetMarker, StringComparison.Ordinal));
        int pvpSpawnSetMarkers = transforms.Count(item => string.Equals(item.name, PvpSpawnSetMarker, StringComparison.Ordinal));
        int safeRooms = transforms.Count(item => string.Equals(item.name, SafeRoomMarker, StringComparison.Ordinal));
        int players = transforms.Count(item => item.name.StartsWith("PVE_PlayerSpawn_", StringComparison.Ordinal));
        int pvpTeam1Players = transforms.Count(item => item.name.StartsWith("PVP_Team1Spawn_", StringComparison.Ordinal));
        int pvpTeam2Players = transforms.Count(item => item.name.StartsWith("PVP_Team2Spawn_", StringComparison.Ordinal));
        int enemies = transforms.Count(item => item.name.StartsWith("PVE_EnemySpawn_", StringComparison.Ordinal));
        Transform[] exfils = transforms.Where(item => item.name.StartsWith("PVE_ExfilZone_", StringComparison.Ordinal)).ToArray();
        Transform[] roomFloorTransforms = transforms.Where(item =>
            string.Equals(item.name, "NATIVE_Floor", StringComparison.Ordinal)).ToArray();
        int roomFloors = roomFloorTransforms.Length;
        int roomCeilings = transforms.Count(item => string.Equals(item.name, "NATIVE_Ceiling", StringComparison.Ordinal));
        Transform[] connectorFloorTransforms = transforms.Where(item =>
            item.name.StartsWith("NATIVE_ConnectorFloor_", StringComparison.Ordinal)).ToArray();
        int connectorFloors = connectorFloorTransforms.Length;
        int connectorCeilings = transforms.Count(item => item.name.StartsWith("NATIVE_ConnectorCeiling_", StringComparison.Ordinal));
        int warehouseShellGroups = transforms.Count(item =>
            string.Equals(item.name, "NATIVE_WarehousePvpCompleteShell", StringComparison.Ordinal));
        string[] warehousePartNames =
        {
            "NATIVE_WarehouseBase", "NATIVE_WarehouseOverHeadSupport", "NATIVE_WarehouseRoof", "NATIVE_WarehouseSupport2"
        };
        Transform[] warehouseParts = transforms.Where(item => warehousePartNames.Contains(item.name)).ToArray();
        int warehouseFinishMarkers = transforms.Count(item =>
            string.Equals(item.name, "WAREHOUSE_PREFAB_PVP_WOODS_EXACT_FOUR_PART",
                StringComparison.Ordinal));
        int invalidWarehousePartFinish = warehouseParts.Count(part =>
            !AllRendererSlotsUseNativeProfile(part, "RM_Steel_smooth"));
        int warehouseSteelSlots = CountRendererSlotsUsingNativeProfile(
            transforms.FirstOrDefault(item => item.name == "NATIVE_WarehousePvpCompleteShell"), "RM_Steel_smooth");
        int warehouseMeshColliders = warehouseParts.Sum(part => part.GetComponentsInChildren<MeshCollider>(true).Length);
        Transform[] warehouseGrounds = transforms.Where(item =>
            string.Equals(item.name, "NATIVE_WarehouseGroundApron", StringComparison.Ordinal)).ToArray();
        Transform warehouseGround = warehouseGrounds.FirstOrDefault();
        MeshFilter[] warehouseGroundFilters = warehouseGround == null
            ? Array.Empty<MeshFilter>()
            : warehouseGround.GetComponentsInChildren<MeshFilter>(true);
        MeshRenderer[] warehouseGroundRenderers = warehouseGround == null
            ? Array.Empty<MeshRenderer>()
            : warehouseGround.GetComponentsInChildren<MeshRenderer>(true);
        MeshFilter warehouseGroundFilter = warehouseGroundFilters.FirstOrDefault();
        MeshRenderer warehouseGroundRenderer = warehouseGroundRenderers.FirstOrDefault();
        MeshCollider warehouseGroundCollider = warehouseGround == null ? null : warehouseGround.GetComponent<MeshCollider>();
        int warehouseGroundMarkers = transforms.Count(item => string.Equals(item.name,
            "WAREHOUSE_GROUND_LEVEL11_FLOOR_MESH152_MATERIAL26", StringComparison.Ordinal));
        int warehouseGroundProvenanceMarkers = transforms.Count(item => string.Equals(item.name,
            "WAREHOUSE_GROUND_PROVENANCE_APPEARANCE_GO104_GEOMETRY_GO9601", StringComparison.Ordinal));
        int warehouseGroundNavigationPolicyMarkers = transforms.Count(item => string.Equals(item.name,
            "WAREHOUSE_APRON_NAV_EXCLUDED_ENCLOSED_PERIMETER", StringComparison.Ordinal));
        bool warehouseGroundMeshValid = WarehouseGroundMeshContractValid(
            warehouseGroundFilter == null ? null : warehouseGroundFilter.sharedMesh,
            out string warehouseGroundMeshFailure);
        Renderer[] playableFloorRenderers = roomFloorTransforms.Concat(connectorFloorTransforms)
            .Select(item => item.GetComponentInChildren<Renderer>(true))
            .Where(item => item != null).ToArray();
        bool playableFloorBoundsComplete = playableFloorRenderers.Length == roomFloors + connectorFloors;
        Bounds playableFloorBounds = playableFloorRenderers.Length == 0 ? default : playableFloorRenderers[0].bounds;
        foreach (Renderer floorRenderer in playableFloorRenderers.Skip(1)) playableFloorBounds.Encapsulate(floorRenderer.bounds);
        Bounds warehouseGroundBounds = warehouseGroundRenderer == null ? default : warehouseGroundRenderer.bounds;
        bool warehouseGroundBoundsValid = warehouseGroundRenderer != null && playableFloorBoundsComplete &&
            warehouseGroundBounds.min.x <= playableFloorBounds.min.x - WarehouseGroundMinimumApron &&
            warehouseGroundBounds.max.x >= playableFloorBounds.max.x + WarehouseGroundMinimumApron &&
            warehouseGroundBounds.min.z <= playableFloorBounds.min.z - WarehouseGroundMinimumApron &&
            warehouseGroundBounds.max.z >= playableFloorBounds.max.z + WarehouseGroundMinimumApron;
        float warehouseGroundElevation = warehouseGroundRenderer == null
            ? float.NaN
            : root.transform.InverseTransformPoint(warehouseGroundBounds.center).y;
        bool warehouseGroundElevationValid = Mathf.Abs(warehouseGroundElevation - WarehouseGroundElevation) <= .002f;
        bool warehouseGroundColliderValid = warehouseGround != null && warehouseGroundCollider != null &&
            warehouseGroundFilter != null && warehouseGround.GetComponentsInChildren<Collider>(true).Length == 1 &&
            warehouseGroundCollider.sharedMesh == warehouseGroundFilter.sharedMesh && warehouseGroundCollider.enabled &&
            !warehouseGroundCollider.isTrigger && !warehouseGroundCollider.convex &&
            Mathf.Abs(warehouseGroundCollider.bounds.center.x - warehouseGroundBounds.center.x) <= .002f &&
            Mathf.Abs(warehouseGroundCollider.bounds.center.y - warehouseGroundBounds.center.y) <= .002f &&
            Mathf.Abs(warehouseGroundCollider.bounds.center.z - warehouseGroundBounds.center.z) <= .002f &&
            Mathf.Abs(warehouseGroundCollider.bounds.size.x - warehouseGroundBounds.size.x) <= .002f &&
            Mathf.Abs(warehouseGroundCollider.bounds.size.z - warehouseGroundBounds.size.z) <= .002f;
        Transform warehouseShellRoot = root.transform.Find("05_HIGH_WAREHOUSE_SHELL");
        bool warehouseGroundValid = warehouseGrounds.Length == 1 && warehouseGround != null &&
            warehouseGround.parent == warehouseShellRoot && warehouseGroundFilters.Length == 1 &&
            warehouseGroundRenderers.Length == 1 && warehouseGroundRenderer.sharedMaterials.Length == 1 &&
            AllRendererSlotsUseNativeProfile(warehouseGround, "Floor") && warehouseGroundMeshValid &&
            warehouseGroundBoundsValid && warehouseGroundElevationValid && warehouseGroundColliderValid &&
            warehouseGroundMarkers == 1 && warehouseGroundProvenanceMarkers == 1 &&
            warehouseGroundNavigationPolicyMarkers == 1;
        int obsoleteWarehouseModules = transforms.Count(item =>
            item.name == "NATIVE_WarehouseRoof_9M" ||
            item.name.StartsWith("NATIVE_WarehouseRoofPanel_", StringComparison.Ordinal) ||
            item.name.StartsWith("NATIVE_WarehousePerimeterWall_", StringComparison.Ordinal));
        int obsoleteCorrugatedSlots = CountRendererSlotsUsingNativeProfile(root.transform,
            "Corrugated_Metal_Sheet_vb1lafx");
        int openTopMarkers = transforms.Count(item =>
            string.Equals(item.name, "OPEN_TOP_KILLHOUSE_INSIDE_HIGH_WAREHOUSE", StringComparison.Ordinal));
        int hallwaySideDoors = transforms.Count(item => string.Equals(item.name, "HALLWAY_SIDE_DOOR", StringComparison.Ordinal));
        int primitiveMeshes = filters.Count(filter => filter.sharedMesh != null && primitiveNames.Contains(filter.sharedMesh.name));
        Transform warehouseRoof = warehouseParts.FirstOrDefault(item => item.name == "NATIVE_WarehouseRoof");
        Renderer warehouseRoofRenderer = warehouseRoof != null
            ? warehouseRoof.GetComponentInChildren<Renderer>(true)
            : null;
        float warehouseRoofElevation = warehouseRoofRenderer == null
            ? float.NaN
            : root.transform.InverseTransformPoint(warehouseRoofRenderer.bounds.max).y;
        BoxCollider[] exfilColliders = exfils.Length == 1
            ? exfils[0].GetComponents<BoxCollider>()
            : Array.Empty<BoxCollider>();
        BoxCollider exfil = exfilColliders.Length == 1 ? exfilColliders[0] : null;
        bool exfilValid = exfil != null && exfil.enabled && exfil.isTrigger &&
                          exfil.size.x > .01f && exfil.size.y > .01f && exfil.size.z > .01f &&
                          MarkerTransformIsFinite(exfils[0]) &&
                          exfil.gameObject.scene.handle == root.scene.handle;
        int expectedEnemies = MinimumAuthoredPveEnemyMarkers;
        bool furniturePlacementValid = ValidateRuntimeFurniturePlacement(root, out int wallBackedFurniture,
            out int furnitureProvenanceMarkers, out int forbiddenFurniture, out int invalidFurniturePlacement,
            out int missingWallFurnitureFamilies, out int overlappingFurniture, out int blockedFurniturePortals,
            out int centerRoomFurniture, out int centerRoomTables, out int centerRoomSofas,
            out int invalidCenterRoomFurniture, out int centerRoomTacticalConflicts,
            out string furniturePlacementFailure);
        bool passed = mapMarkers == 1 && pveSpawnSetMarkers == 1 && pvpSpawnSetMarkers == 1 && safeRooms == 1 &&
                      players == 4 && pvpTeam1Players == 6 && pvpTeam2Players == 6 &&
                      roomFloors >= 19 && roomFloors <= 21 && enemies == expectedEnemies &&
                       exfils.Length == 1 && exfilColliders.Length == 1 && exfilValid && roomCeilings == 0 &&
                      connectorFloors >= 1 && connectorFloors <= 32 && connectorCeilings == 0 && warehouseShellGroups == 1 &&
                       warehouseParts.Length == 4 && warehouseMeshColliders == 4 &&
                       warehouseGrounds.Length == 1 && warehouseGroundValid &&
                      warehouseRoofRenderer != null && Mathf.Abs(warehouseRoofElevation - WarehouseRoofHeight) <= .15f &&
                      warehouseFinishMarkers == 1 && invalidWarehousePartFinish == 0 && warehouseSteelSlots == 4 &&
                       obsoleteWarehouseModules == 0 && obsoleteCorrugatedSlots == 0 && openTopMarkers == 1 &&
                       hallwaySideDoors >= 1 && primitiveMeshes == 0 && furniturePlacementValid;
        if (!passed)
            log.LogError("Vektor Kill House scene contract failed: mapMarkers=" + mapMarkers +
                         ", pveSpawnSetMarkers=" + pveSpawnSetMarkers +
                         ", pvpSpawnSetMarkers=" + pvpSpawnSetMarkers + ", safeRooms=" + safeRooms +
                         ", pvePlayers=" + players + ", pvpTeam1Players=" + pvpTeam1Players +
                         ", pvpTeam2Players=" + pvpTeam2Players + ", enemies=" + enemies + ", exfils=" + exfils.Length +
                          ", expectedEnemies=" + expectedEnemies + ", exfilColliders=" + exfilColliders.Length +
                          ", exfilValid=" + exfilValid +
                         ", roomFloors=" + roomFloors + ", roomCeilings=" + roomCeilings +
                         ", connectorFloors=" + connectorFloors + ", connectorCeilings=" + connectorCeilings +
                         ", warehouseShellGroups=" + warehouseShellGroups +
                         ", warehouseParts=" + warehouseParts.Length +
                         ", warehouseRoofElevation=" + warehouseRoofElevation.ToString("F2", CultureInfo.InvariantCulture) +
                         ", warehouseFinishMarkers=" + warehouseFinishMarkers +
                         ", invalidWarehousePartFinish=" + invalidWarehousePartFinish +
                         ", warehouseSteelSlots=" + warehouseSteelSlots +
                         ", warehouseMeshColliders=" + warehouseMeshColliders +
                         ", warehouseGrounds=" + warehouseGrounds.Length +
                         ", warehouseGroundValid=" + warehouseGroundValid +
                         ", warehouseGroundMeshValid=" + warehouseGroundMeshValid +
                         ", warehouseGroundMeshFailure=" + warehouseGroundMeshFailure +
                         ", warehouseGroundBoundsValid=" + warehouseGroundBoundsValid +
                         ", warehouseGroundElevation=" + warehouseGroundElevation.ToString("F3", CultureInfo.InvariantCulture) +
                         ", warehouseGroundElevationValid=" + warehouseGroundElevationValid +
                         ", warehouseGroundColliderValid=" + warehouseGroundColliderValid +
                         ", warehouseGroundMarkers=" + warehouseGroundMarkers + "/" +
                         warehouseGroundProvenanceMarkers + "/" + warehouseGroundNavigationPolicyMarkers +
                         ", obsoleteWarehouseModules=" + obsoleteWarehouseModules +
                         ", obsoleteCorrugatedSlots=" + obsoleteCorrugatedSlots +
                          ", openTopMarkers=" + openTopMarkers +
                          ", hallwaySideDoors=" + hallwaySideDoors + ", primitiveMeshes=" + primitiveMeshes +
                          ", wallBackedFurniture=" + wallBackedFurniture +
                          ", furnitureProvenanceMarkers=" + furnitureProvenanceMarkers +
                          ", forbiddenFurniture=" + forbiddenFurniture +
                          ", invalidFurniturePlacement=" + invalidFurniturePlacement +
                          ", missingWallFurnitureFamilies=" + missingWallFurnitureFamilies +
                          ", centerRoomFurniture=" + centerRoomFurniture +
                          ", centerRoomTables=" + centerRoomTables +
                          ", centerRoomSofas=" + centerRoomSofas +
                          ", invalidCenterRoomFurniture=" + invalidCenterRoomFurniture +
                          ", centerRoomTacticalConflicts=" + centerRoomTacticalConflicts +
                          ", overlappingFurniture=" + overlappingFurniture +
                          ", blockedFurniturePortals=" + blockedFurniturePortals +
                          ", furniturePlacementFailure=" + furniturePlacementFailure + ".");
        return passed;
    }

    private static bool ValidateRuntimeFurniturePlacement(GameObject root, out int wallBackedCount,
        out int provenanceMarkerCount, out int forbiddenCount, out int invalidPlacementCount,
        out int missingFamilyCount, out int overlappingCount, out int blockedPortalCount,
        out int centerRoomCount, out int centerTableCount, out int centerSofaCount,
        out int invalidCenterCount, out int centerTacticalConflictCount,
        out string firstFailure)
    {
        wallBackedCount = 0;
        provenanceMarkerCount = 0;
        forbiddenCount = 0;
        invalidPlacementCount = 0;
        missingFamilyCount = RuntimeWallFurnitureContracts.Count;
        overlappingCount = 0;
        blockedPortalCount = 0;
        centerRoomCount = 0;
        centerTableCount = 0;
        centerSofaCount = 0;
        invalidCenterCount = 0;
        centerTacticalConflictCount = 0;
        firstFailure = string.Empty;
        if (root == null)
        {
            firstFailure = "root-null";
            return false;
        }

        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        provenanceMarkerCount = transforms.Count(item => item.name.StartsWith(
            "WALL_BACKED_PROP_PROVENANCE_", StringComparison.Ordinal));
        forbiddenCount = filters.Count(filter => filter != null && filter.sharedMesh != null &&
            ForbiddenStandaloneFurnitureMeshes.Contains(filter.sharedMesh.name));
        Transform[] wallBacked = transforms.Where(item =>
            RuntimeHasDirectChildWithPrefix(item, "WALL_BACKED_PROP_OUTWARD_")).ToArray();
        wallBackedCount = wallBacked.Length;
        var families = new HashSet<string>(StringComparer.Ordinal);
        foreach (Transform prop in wallBacked)
        {
            string failure = RuntimeWallFurnitureFailure(root, prop, out string family);
            if (!string.IsNullOrEmpty(family)) families.Add(family);
            if (string.IsNullOrEmpty(failure)) continue;
            invalidPlacementCount++;
            if (string.IsNullOrEmpty(firstFailure)) firstFailure = HierarchyPath(prop) + ":" + failure;
        }
        string[] missing = RuntimeWallFurnitureContracts.Keys.Where(key => !families.Contains(key)).ToArray();
        missingFamilyCount = missing.Length;
        if (missing.Length > 0 && string.IsNullOrEmpty(firstFailure))
            firstFailure = "missing-families=" + string.Join("|", missing);
        if (forbiddenCount > 0 && string.IsNullOrEmpty(firstFailure))
            firstFailure = "forbidden-standalone-meshes=" + forbiddenCount;

        Transform[] centerRoom = transforms.Where(item =>
            RuntimeDirectChildrenWithPrefix(item, "CENTER_ROOM_PROP_ROLE_").Length == 1).ToArray();
        centerRoomCount = centerRoom.Length;
        foreach (Transform prop in centerRoom)
        {
            string failure = RuntimeCenterFurnitureFailure(root, prop, out string role);
            if (string.Equals(role, "TABLE", StringComparison.Ordinal)) centerTableCount++;
            else if (string.Equals(role, "SOFA", StringComparison.Ordinal)) centerSofaCount++;
            if (string.IsNullOrEmpty(failure)) continue;
            invalidCenterCount++;
            if (string.IsNullOrEmpty(firstFailure)) firstFailure = HierarchyPath(prop) + ":" + failure;
        }
        string[] centerTacticalConflicts = RuntimeCenterFurnitureTacticalBlockerDetails(centerRoom, transforms);
        centerTacticalConflictCount = centerTacticalConflicts.Length;
        if (centerTacticalConflicts.Length > 0 && string.IsNullOrEmpty(firstFailure))
            firstFailure = centerTacticalConflicts[0];

        Transform[] physicalFurniture = filters.Where(filter => filter != null && filter.sharedMesh != null &&
                (RuntimeWallFurnitureContracts.ContainsKey(filter.sharedMesh.name) ||
                 string.Equals(filter.sharedMesh.name, "Kitchen_table_large", StringComparison.Ordinal) ||
                 string.Equals(filter.sharedMesh.name, "Couch_2seat", StringComparison.Ordinal)))
            .Select(filter => filter.transform).Distinct().ToArray();
        string[] overlaps = RuntimeFurnitureOverlapDetails(physicalFurniture);
        overlappingCount = overlaps.Length;
        if (overlaps.Length > 0 && string.IsNullOrEmpty(firstFailure)) firstFailure = overlaps[0];
        string[] blockedPortals = RuntimeFurniturePortalBlockerDetails(physicalFurniture, transforms);
        blockedPortalCount = blockedPortals.Length;
        if (blockedPortals.Length > 0 && string.IsNullOrEmpty(firstFailure)) firstFailure = blockedPortals[0];

        bool passed = wallBackedCount >= 12 && provenanceMarkerCount == wallBackedCount && forbiddenCount == 0 &&
                      invalidPlacementCount == 0 && missingFamilyCount == 0 && overlappingCount == 0 &&
                      blockedPortalCount == 0 && centerRoomCount >= 2 && centerTableCount >= 1 &&
                      centerSofaCount >= 1 && invalidCenterCount == 0 && centerTacticalConflictCount == 0;
        if (!passed && string.IsNullOrEmpty(firstFailure))
            firstFailure = "counts=wall:" + wallBackedCount + "/provenance:" + provenanceMarkerCount +
                           "/forbidden:" + forbiddenCount + "/invalid:" + invalidPlacementCount +
                           "/missing:" + missingFamilyCount + "/overlap:" + overlappingCount +
                           "/portal:" + blockedPortalCount + "/center:" + centerRoomCount +
                           "/table:" + centerTableCount + "/sofa:" + centerSofaCount +
                           "/centerInvalid:" + invalidCenterCount +
                           "/centerTactical:" + centerTacticalConflictCount;
        return passed;
    }

    private static string RuntimeWallFurnitureFailure(GameObject root, Transform prop, out string family)
    {
        family = string.Empty;
        if (prop == null) return "prop-null";
        MeshFilter filter = prop.GetComponent<MeshFilter>();
        Mesh mesh = filter == null ? null : filter.sharedMesh;
        if (mesh == null) return "root-mesh-missing";
        family = mesh.name;
        if (ForbiddenStandaloneFurnitureMeshes.Contains(family)) return "forbidden-family=" + family;
        if (!RuntimeWallFurnitureContracts.TryGetValue(family, out RuntimeWallFurnitureContract contract))
            return "unproven-wall-family=" + family;

        Transform[] outwardMarkers = RuntimeDirectChildrenWithPrefix(prop, "WALL_BACKED_PROP_OUTWARD_");
        if (outwardMarkers.Length != 1) return "outward-marker-count=" + outwardMarkers.Length;
        Transform[] provenanceMarkers = RuntimeDirectChildrenWithPrefix(prop, "WALL_BACKED_PROP_PROVENANCE_");
        if (provenanceMarkers.Length != 1 ||
            !string.Equals(provenanceMarkers[0].name, contract.ProvenanceMarker, StringComparison.Ordinal))
            return "provenance-marker=" + (provenanceMarkers.Length == 1 ? provenanceMarkers[0].name :
                "count-" + provenanceMarkers.Length);

        string suffix = outwardMarkers[0].name.Substring("WALL_BACKED_PROP_OUTWARD_".Length);
        Vector3 outward = suffix == "E" ? Vector3.right : suffix == "W" ? Vector3.left :
            suffix == "N" ? Vector3.forward : suffix == "S" ? Vector3.back : Vector3.zero;
        if (outward == Vector3.zero) return "outward-marker-direction=" + suffix;
        float interiorAlignment = Vector3.Dot(prop.forward.normalized, -outward);
        float wallAlignment = Vector3.Dot((-prop.forward).normalized, outward);
        float upAlignment = Vector3.Dot(prop.up.normalized, Vector3.up);
        if (interiorAlignment < .999f || wallAlignment < .999f || upAlignment < .999f)
            return "axis-alignment=" + interiorAlignment.ToString("F4", CultureInfo.InvariantCulture) + "/" +
                   wallAlignment.ToString("F4", CultureInfo.InvariantCulture) + "/" +
                    upAlignment.ToString("F4", CultureInfo.InvariantCulture);
        if (!RuntimeColliderContractValid(prop, contract, out string colliderFailure))
            return "collider=" + colliderFailure;
        if (!RuntimeFurnitureHierarchyContractValid(prop, contract.Children, true,
                out string hierarchyFailure))
            return "hierarchy=" + hierarchyFailure;
        if (prop.GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
            return "hierarchy=retained-node-monobehaviour-present";
        if (!TryRuntimeRoomFloorBounds(root, prop, out Bounds roomBounds)) return "room-owner-or-floor-bounds";
        if (!TryRuntimeRendererBounds(prop.gameObject, out Bounds rendererBounds) ||
            !RuntimeBoundsInside(rendererBounds, roomBounds, .02f)) return "renderer-outside-room";
        if (!TryRuntimePhysicalBounds(prop.gameObject, out Bounds physicalBounds) ||
            !RuntimeBoundsInside(physicalBounds, roomBounds, .02f)) return "collider-outside-room";
        float extent = Mathf.Abs(outward.x) > .5f ? roomBounds.extents.x : roomBounds.extents.z;
        float back = Vector3.Dot(rendererBounds.center - roomBounds.center, outward) +
                     Mathf.Abs(outward.x) * rendererBounds.extents.x +
                     Mathf.Abs(outward.z) * rendererBounds.extents.z;
        if (Mathf.Abs((extent - .12f) - back) > .04f)
            return "wall-standoff=" + (extent - back).ToString("F3", CultureInfo.InvariantCulture);
        return string.Empty;
    }

    private static string RuntimeCenterFurnitureFailure(GameObject root, Transform prop, out string role)
    {
        role = string.Empty;
        if (root == null || prop == null) return "center-prop-null";
        Transform[] roleMarkers = RuntimeDirectChildrenWithPrefix(prop, "CENTER_ROOM_PROP_ROLE_");
        if (roleMarkers.Length != 1) return "center-role-marker-count=" + roleMarkers.Length;
        role = roleMarkers[0].name.Substring("CENTER_ROOM_PROP_ROLE_".Length);
        if (!RuntimeCenterFurnitureContracts.TryGetValue(role, out RuntimeCenterFurnitureContract contract))
            return "center-role-unproven=" + role;

        MeshFilter[] filters = prop.GetComponents<MeshFilter>();
        MeshRenderer[] renderers = prop.GetComponents<MeshRenderer>();
        Mesh mesh = filters.Length == 1 ? filters[0].sharedMesh : null;
        if (filters.Length != 1 || renderers.Length != 1 || mesh == null || !renderers[0].enabled ||
            !string.Equals(mesh.name, contract.MeshName, StringComparison.Ordinal))
            return "center-root-visual=" + filters.Length + "/" + renderers.Length + "/" +
                   (mesh == null ? "null" : mesh.name) + "/expected=" + contract.MeshName;
        if (prop.gameObject.layer != contract.RootLayer ||
            !RuntimeVectorApproximately(prop.localScale, Vector3.one, 1e-5f))
            return "center-root-layer-scale=" + prop.gameObject.layer + "/" +
                   prop.localScale.ToString("F5") + "/expected-layer=" + contract.RootLayer;
        if (mesh.subMeshCount != 1 || mesh.GetIndexCount(0) == 0 ||
            (contract.VertexCount > 0 && mesh.vertexCount != contract.VertexCount) ||
            (contract.IndexCount > 0 && mesh.GetIndexCount(0) != contract.IndexCount) ||
            !mesh.HasVertexAttribute(VertexAttribute.TexCoord0) ||
            !mesh.HasVertexAttribute(VertexAttribute.TexCoord1) ||
            !mesh.HasVertexAttribute(VertexAttribute.Color))
            return "center-mesh-closure=" + mesh.vertexCount + "/" + mesh.subMeshCount + "/" +
                   (mesh.subMeshCount == 0 ? 0 : mesh.GetIndexCount(0));
        if (!FurnitureMaterialSlotsByMesh.TryGetValue(contract.MeshName, out string[] expectedSlots))
            return "center-material-contract-missing=" + contract.MeshName;
        Material[] actualSlots = renderers[0].sharedMaterials;
        if (actualSlots.Length != expectedSlots.Length || actualSlots.Length != mesh.subMeshCount)
            return "center-material-count=" + actualSlots.Length + "/expected=" + expectedSlots.Length;
        for (int index = 0; index < actualSlots.Length; index++)
        {
            string actual = actualSlots[index] == null ? string.Empty :
                NormalizeNativeMaterialName(actualSlots[index].name);
            if (string.Equals(actual, expectedSlots[index], StringComparison.Ordinal)) continue;
            return "center-material-slot-" + index + "=" + actual + "/expected=" + expectedSlots[index];
        }
        if (!RuntimeBoxColliderContractValid(prop, contract.BoxColliders, out string colliderFailure))
            return "center-collider=" + colliderFailure;
        if (string.Equals(contract.Role, "SOFA", StringComparison.Ordinal))
        {
            if (!RuntimeExactCouchRendererState(renderers[0], prop, out string rendererFailure))
                return "center-sofa-renderer=" + rendererFailure;
            BoxCollider[] couchBoxes = prop.GetComponents<BoxCollider>();
            for (int index = 0; index < couchBoxes.Length; index++)
            {
                BoxCollider box = couchBoxes[index];
                if (box != null && !box.providesContacts && box.includeLayers.value == 0 &&
                    box.excludeLayers.value == 0 && box.layerOverridePriority == 0) continue;
                return "center-sofa-box-auxiliary=" + index;
            }
        }
        if (prop.GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
            return "center-retained-monobehaviour";

        if (!TryRuntimeDressingRoomIndex(prop.parent, out int roomIndex) || roomIndex <= 0)
            return "center-room-owner";
        Transform[] roomMarkers = RuntimeDirectChildrenWithPrefix(prop, "CENTER_ROOM_PROP_ROOM_");
        string expectedRoomMarker = "CENTER_ROOM_PROP_ROOM_" +
                                    roomIndex.ToString("00", CultureInfo.InvariantCulture);
        if (roomMarkers.Length != 1 ||
            !string.Equals(roomMarkers[0].name, expectedRoomMarker, StringComparison.Ordinal))
            return "center-room-marker=" + (roomMarkers.Length == 1 ? roomMarkers[0].name :
                "count-" + roomMarkers.Length) + "/expected=" + expectedRoomMarker;
        Transform[] provenance = RuntimeDirectChildrenWithPrefix(prop, "CENTER_ROOM_PROP_PROVENANCE_");
        if (provenance.Length != 1 ||
            !string.Equals(provenance[0].name, contract.ProvenanceMarker, StringComparison.Ordinal))
            return "center-provenance=" + (provenance.Length == 1 ? provenance[0].name :
                "count-" + provenance.Length);
        Transform[] facing = RuntimeDirectChildrenWithPrefix(prop, "CENTER_ROOM_PROP_FACING_");
        string expectedFacing = RuntimeCenterFacingMarker(prop, contract);
        if (facing.Length != 1 || string.IsNullOrEmpty(expectedFacing) ||
            !string.Equals(facing[0].name, expectedFacing, StringComparison.Ordinal))
            return "center-facing=" + (facing.Length == 1 ? facing[0].name : "count-" + facing.Length) +
                   "/expected=" + expectedFacing;
        Transform[] candidates = RuntimeDirectChildrenWithPrefix(prop,
            "CENTER_ROOM_PROP_DETERMINISTIC_CANDIDATE_");
        if (candidates.Length != 1 ||
            !int.TryParse(candidates[0].name.Substring("CENTER_ROOM_PROP_DETERMINISTIC_CANDIDATE_".Length),
                NumberStyles.None, CultureInfo.InvariantCulture, out int candidateIndex) || candidateIndex <= 0)
            return "center-candidate-marker";
        Transform[] clearance = RuntimeDirectChildrenNamed(prop, "CENTER_ROOM_PROP_CLEARANCE_VALID");
        Transform[] circulation = RuntimeDirectChildrenNamed(prop, "CENTER_ROOM_PROP_CIRCULATION_VALID");
        if (clearance.Length != 1 || circulation.Length != 1)
            return "center-clearance-circulation-markers";

        Transform[] placementMarkers =
        {
            roleMarkers[0], roomMarkers[0], provenance[0], facing[0], candidates[0],
            clearance[0], circulation[0]
        };
        if (!RuntimeCenterFurnitureMarkersValid(prop, contract, placementMarkers, out string markerFailure))
            return "center-markers=" + markerFailure;
        if (Vector3.Dot(prop.up.normalized, Vector3.up) < .999f)
            return "center-up-axis";
        if (!TryRuntimeRoomFloorBounds(root, prop, out Bounds roomBounds))
            return "center-room-floor-bounds";
        if (!TryRuntimeRendererBounds(prop.gameObject, out Bounds rendererBounds) ||
            !RuntimeBoundsInsideInset(rendererBounds, roomBounds, CenterRoomFurnitureWallInset))
            return "center-renderer-perimeter-clearance";
        if (!TryRuntimePhysicalBounds(prop.gameObject, out Bounds physicalBounds) ||
            !RuntimeBoundsInsideInset(physicalBounds, roomBounds, CenterRoomFurnitureWallInset))
            return "center-collider-perimeter-clearance";
        string siblingOverlap = RuntimeCenterSiblingOverlapFailure(prop);
        if (!string.IsNullOrEmpty(siblingOverlap)) return "center-sibling-overlap=" + siblingOverlap;
        return string.Empty;
    }

    private static bool RuntimeBoxColliderContractValid(Transform root,
        RuntimeBoxColliderProfile[] expected, out string failure)
    {
        failure = string.Empty;
        Collider[] all = root == null ? Array.Empty<Collider>() : root.GetComponents<Collider>();
        BoxCollider[] boxes = root == null ? Array.Empty<BoxCollider>() : root.GetComponents<BoxCollider>();
        if (boxes.Length != expected.Length || all.Length != boxes.Length)
        {
            failure = "count=" + boxes.Length + "/all=" + all.Length + "/expected=" + expected.Length;
            return false;
        }
        for (int index = 0; index < boxes.Length; index++)
        {
            BoxCollider actual = boxes[index];
            RuntimeBoxColliderProfile profile = expected[index];
            if (actual != null && actual.enabled && !actual.isTrigger && actual.sharedMaterial == null &&
                RuntimeVectorApproximately(actual.center, profile.Center, 1e-5f) &&
                RuntimeVectorApproximately(actual.size, profile.Size, 1e-5f)) continue;
            failure = "box-" + index + "-state";
            return false;
        }
        return true;
    }

    private static bool RuntimeCenterFurnitureMarkersValid(Transform prop,
        RuntimeCenterFurnitureContract contract, Transform[] placementMarkers, out string failure)
    {
        failure = string.Empty;
        if (placementMarkers.Length != 7 || placementMarkers.Any(marker =>
                !RuntimeMetadataMarkerValid(marker, Vector3.zero, 0)))
        {
            failure = "placement-marker-transform";
            return false;
        }
        var allowed = new HashSet<string>(placementMarkers.Select(marker => marker.name), StringComparer.Ordinal);
        if (string.Equals(contract.Role, "SOFA", StringComparison.Ordinal))
        {
            const string frontName = "NATIVE_FURNITURE_FRONT_LOCAL_POSITIVE_Z";
            const string donorName = "NATIVE_FURNITURE_PROVENANCE_level4_GO578_Couch_2seat_Mesh962_Mat174";
            const string probeAnchorName = "GameObject";
            Transform[] front = RuntimeDirectChildrenNamed(prop, frontName);
            Transform[] donor = RuntimeDirectChildrenNamed(prop, donorName);
            Transform[] probeAnchors = RuntimeDirectChildrenNamed(prop, probeAnchorName);
            if (front.Length != 1 || donor.Length != 1 ||
                !RuntimeMetadataMarkerValid(front[0], Vector3.forward, 24) ||
                !RuntimeMetadataMarkerValid(donor[0], Vector3.zero, 24) ||
                probeAnchors.Length != 1 || !probeAnchors[0].gameObject.activeSelf ||
                !RuntimeMetadataMarkerValid(probeAnchors[0],
                    new Vector3(0f, 1.3220000267028809f, -.3490000069141388f), 24))
            {
                failure = "sofa-native-marker-or-probe-transform";
                return false;
            }
            allowed.Add(frontName);
            allowed.Add(donorName);
            allowed.Add(probeAnchorName);
        }
        if (prop.childCount != allowed.Count)
        {
            failure = "child-count=" + prop.childCount + "/expected=" + allowed.Count;
            return false;
        }
        for (int index = 0; index < prop.childCount; index++)
        {
            Transform child = prop.GetChild(index);
            if (allowed.Contains(child.name)) continue;
            failure = "unexpected-child=" + child.name;
            return false;
        }
        return true;
    }

    private static bool RuntimeExactCouchRendererState(MeshRenderer renderer, Transform root,
        out string failure)
    {
        failure = string.Empty;
        Transform[] anchors = RuntimeDirectChildrenNamed(root, "GameObject");
        if (renderer == null || anchors.Length != 1 || renderer.probeAnchor != anchors[0])
        {
            failure = "probe-anchor=" + anchors.Length + "/" +
                      (renderer == null || renderer.probeAnchor == null ? "null" :
                          renderer.probeAnchor.name);
            return false;
        }
        // Ray-tracing, GPU small-mesh culling, and forced-LOD getters are
        // runtime/device-normalized state. Their exact serialized donor values
        // remain enforced by the authoring and bundle validators, but they are
        // not portable fatal invariants in OPERATOR's D3D11 player.
        if (!renderer.enabled)
        {
            failure = "enabled=false/expected=true";
            return false;
        }
        if ((int)renderer.shadowCastingMode != 1)
        {
            failure = "shadow-casting=" + (int)renderer.shadowCastingMode + "/expected=1";
            return false;
        }
        if (!renderer.receiveShadows)
        {
            failure = "receive-shadows=false/expected=true";
            return false;
        }
        if (!renderer.allowOcclusionWhenDynamic)
        {
            failure = "dynamic-occlusion=false/expected=true";
            return false;
        }
        if (renderer.staticShadowCaster)
        {
            failure = "static-shadow-caster=true/expected=false";
            return false;
        }
        if ((int)renderer.motionVectorGenerationMode != 1)
        {
            failure = "motion-vectors=" + (int)renderer.motionVectorGenerationMode + "/expected=1";
            return false;
        }
        if ((int)renderer.lightProbeUsage != 1)
        {
            failure = "light-probe-usage=" + (int)renderer.lightProbeUsage + "/expected=1";
            return false;
        }
        if ((int)renderer.reflectionProbeUsage != 1)
        {
            failure = "reflection-probe-usage=" + (int)renderer.reflectionProbeUsage + "/expected=1";
            return false;
        }
        if (renderer.renderingLayerMask != 257u)
        {
            failure = "rendering-layer-mask=" + renderer.renderingLayerMask + "/expected=257";
            return false;
        }
        if (renderer.rendererPriority != 0)
        {
            failure = "renderer-priority=" + renderer.rendererPriority + "/expected=0";
            return false;
        }
        if (renderer.sortingLayerID != 0)
        {
            failure = "sorting-layer-id=" + renderer.sortingLayerID + "/expected=0";
            return false;
        }
        if (renderer.sortingOrder != 0)
        {
            failure = "sorting-order=" + renderer.sortingOrder + "/expected=0";
            return false;
        }
        if (renderer.additionalVertexStreams != null)
        {
            failure = "additional-vertex-streams=" + renderer.additionalVertexStreams.name + "/expected=null";
            return false;
        }
        if (renderer.lightProbeProxyVolumeOverride != null)
        {
            failure = "light-probe-proxy-volume=" + renderer.lightProbeProxyVolumeOverride.name +
                      "/expected=null";
            return false;
        }
        if (renderer.staticBatchRootTransform != null)
        {
            failure = "static-batch-root=" + renderer.staticBatchRootTransform.name + "/expected=null";
            return false;
        }
        int lightmapIndex = renderer.lightmapIndex;
        int realtimeLightmapIndex = renderer.realtimeLightmapIndex;
        if ((lightmapIndex >= 0 && lightmapIndex != 65535) ||
            (realtimeLightmapIndex >= 0 && realtimeLightmapIndex != 65535))
        {
            failure = "scene-baked-lightmap=" + lightmapIndex + "/" + realtimeLightmapIndex;
            return false;
        }
        Component[] rootComponents = root.GetComponents<Component>();
        if (rootComponents.Length != 7 || !(rootComponents[0] is Transform) ||
            !(rootComponents[1] is MeshFilter) || !(rootComponents[2] is MeshRenderer) ||
            rootComponents.Skip(3).Any(component => !(component is BoxCollider)))
        {
            failure = "root-component-order-count=" + rootComponents.Length;
            return false;
        }
        return true;
    }

    private static bool RuntimeMetadataMarkerValid(Transform marker, Vector3 localPosition, int layer)
    {
        return marker != null && marker.gameObject.activeSelf && marker.childCount == 0 && marker.gameObject.layer == layer &&
               marker.GetComponents<Component>().Length == 1 &&
               RuntimeVectorApproximately(marker.localPosition, localPosition, 1e-5f) &&
               RuntimeQuaternionApproximately(marker.localRotation, Quaternion.identity, 1e-5f) &&
               RuntimeVectorApproximately(marker.localScale, Vector3.one, 1e-5f);
    }

    private static Transform[] RuntimeDirectChildrenNamed(Transform parent, string name)
    {
        if (parent == null) return Array.Empty<Transform>();
        return Enumerable.Range(0, parent.childCount).Select(parent.GetChild)
            .Where(child => string.Equals(child.name, name, StringComparison.Ordinal)).ToArray();
    }

    private static string RuntimeCenterFacingMarker(Transform prop, RuntimeCenterFurnitureContract contract)
    {
        Vector3 facing = prop.TransformDirection(contract.LocalFacingAxis);
        facing.y = 0f;
        if (facing.sqrMagnitude < .999f) return string.Empty;
        facing.Normalize();
        if (contract.BidirectionalFacing)
            return "CENTER_ROOM_PROP_FACING_LONG_AXIS_" +
                   (Mathf.Abs(Vector3.Dot(facing, Vector3.right)) >= .999f ? "X" :
                       Mathf.Abs(Vector3.Dot(facing, Vector3.forward)) >= .999f ? "Z" : string.Empty);
        string suffix = Mathf.Abs(facing.x) >= Mathf.Abs(facing.z)
            ? (facing.x >= 0f ? "E" : "W")
            : (facing.z >= 0f ? "N" : "S");
        Vector3 expected = suffix == "E" ? Vector3.right : suffix == "W" ? Vector3.left :
            suffix == "N" ? Vector3.forward : Vector3.back;
        return Vector3.Dot(facing, expected) >= .999f ? "CENTER_ROOM_PROP_FACING_FRONT_" + suffix : string.Empty;
    }

    private static bool TryRuntimeDressingRoomIndex(Transform dressing, out int roomIndex)
    {
        roomIndex = -1;
        return dressing != null && dressing.name.StartsWith("DRESSING_", StringComparison.Ordinal) &&
               dressing.name.Length >= 11 && int.TryParse(dressing.name.Substring(9, 2),
                   NumberStyles.None, CultureInfo.InvariantCulture, out roomIndex);
    }

    private static bool RuntimeBoundsInsideInset(Bounds value, Bounds room, float inset)
    {
        return room.extents.x > inset && room.extents.z > inset &&
               value.min.x >= room.min.x + inset && value.max.x <= room.max.x - inset &&
               value.min.z >= room.min.z + inset && value.max.z <= room.max.z - inset;
    }

    private static string RuntimeCenterSiblingOverlapFailure(Transform prop)
    {
        Transform dressing = prop == null ? null : prop.parent;
        if (dressing == null || !TryRuntimePhysicalBounds(prop.gameObject, out Bounds propBounds))
            return "owner-or-physical-bounds";
        for (int index = 0; index < dressing.childCount; index++)
        {
            Transform sibling = dressing.GetChild(index);
            if (sibling == prop || sibling.name.IndexOf("Carpet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                !TryRuntimePhysicalBounds(sibling.gameObject, out Bounds siblingBounds)) continue;
            float overlapX = Mathf.Min(propBounds.max.x, siblingBounds.max.x) -
                             Mathf.Max(propBounds.min.x, siblingBounds.min.x);
            float overlapY = Mathf.Min(propBounds.max.y, siblingBounds.max.y) -
                             Mathf.Max(propBounds.min.y, siblingBounds.min.y);
            float overlapZ = Mathf.Min(propBounds.max.z, siblingBounds.max.z) -
                             Mathf.Max(propBounds.min.z, siblingBounds.min.z);
            if (overlapX > .02f && overlapY > .02f && overlapZ > .02f) return sibling.name;
        }
        return string.Empty;
    }

    private static string[] RuntimeCenterFurnitureTacticalBlockerDetails(Transform[] centerFurniture,
        Transform[] transforms)
    {
        if (centerFurniture.Length == 0) return Array.Empty<string>();
        Transform[] markers = transforms.Where(item =>
            item.name.StartsWith("PVE_EnemySpawn_", StringComparison.Ordinal)).ToArray();
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        Physics.SyncTransforms();
        foreach (Transform marker in markers)
        {
            Collider[] hits = Physics.OverlapCapsule(marker.position + Vector3.up * .42f,
                marker.position + Vector3.up * 1.58f, CenterRoomTacticalCapsuleRadius, ~0,
                QueryTriggerInteraction.Ignore);
            foreach (Transform prop in centerFurniture)
            {
                if (!hits.Any(hit => hit != null && RuntimeIsDescendantOf(hit.transform, prop))) continue;
                blocked.Add("center-tactical-blocked=" + HierarchyPath(marker) + "<-" + HierarchyPath(prop));
            }
        }
        return blocked.ToArray();
    }

    private static bool RuntimeColliderContractValid(Transform prop, RuntimeWallFurnitureContract contract,
        out string failure)
    {
        failure = string.Empty;
        Collider[] all = prop.GetComponents<Collider>();
        if (contract.BoxColliders.Length > 0)
        {
            BoxCollider[] boxes = prop.GetComponents<BoxCollider>();
            if (boxes.Length != contract.BoxColliders.Length || all.Length != boxes.Length)
            {
                failure = "box-count=" + boxes.Length + "/expected=" + contract.BoxColliders.Length +
                          "/all=" + all.Length;
                return false;
            }
            for (int index = 0; index < boxes.Length; index++)
            {
                RuntimeBoxColliderProfile expected = contract.BoxColliders[index];
                BoxCollider actual = boxes[index];
                if (actual == null || !actual.enabled || actual.isTrigger || actual.sharedMaterial != null ||
                    !RuntimeVectorApproximately(actual.center, expected.Center, 1e-5f) ||
                    !RuntimeVectorApproximately(actual.size, expected.Size, 1e-5f))
                {
                    failure = "box-" + index + "-state";
                    return false;
                }
            }
            return true;
        }

        MeshCollider[] meshColliders = prop.GetComponents<MeshCollider>();
        if (meshColliders.Length != 1 || all.Length != 1)
        {
            failure = "mesh-count=" + meshColliders.Length + "/all=" + all.Length;
            return false;
        }
        MeshCollider meshCollider = meshColliders[0];
        Mesh collisionMesh = meshCollider == null ? null : meshCollider.sharedMesh;
        if (meshCollider == null || !meshCollider.enabled || meshCollider.isTrigger ||
            meshCollider.sharedMaterial != null || collisionMesh == null ||
            !string.Equals(collisionMesh.name, contract.CollisionMeshName, StringComparison.Ordinal) ||
            meshCollider.convex != contract.CollisionConvex || (int)meshCollider.cookingOptions != 30)
        {
            failure = "mesh-state=" + (collisionMesh == null ? "null" : collisionMesh.name) +
                      "/expected=" + contract.CollisionMeshName + "/convex=" +
                      (meshCollider != null && meshCollider.convex) + "/cooking=" +
                      (meshCollider == null ? -1 : (int)meshCollider.cookingOptions);
            return false;
        }
        return true;
    }

    private static bool RuntimeFurnitureHierarchyContractValid(Transform parent,
        RuntimeChildFurnitureContract[] expected, bool allowPlacementMarkers, out string failure)
    {
        failure = string.Empty;
        if (parent == null)
        {
            failure = "parent-null";
            return false;
        }
        if (parent.childCount < expected.Length)
        {
            failure = "child-count=" + parent.childCount + "/expected-at-least-" + expected.Length;
            return false;
        }
        for (int index = 0; index < expected.Length; index++)
        {
            RuntimeChildFurnitureContract contract = expected[index];
            Transform actual = parent.GetChild(index);
            if (!string.Equals(actual.name, contract.Name, StringComparison.Ordinal) ||
                actual.gameObject.activeSelf != contract.Active)
            {
                failure = "child-" + index + "-identity=" + actual.name + "/" + actual.gameObject.activeSelf;
                return false;
            }
            if (!RuntimeVectorApproximately(actual.localPosition, contract.LocalPosition, 1e-5f) ||
                !RuntimeVectorApproximately(actual.localScale, contract.LocalScale, 1e-5f) ||
                !RuntimeQuaternionApproximately(actual.localRotation, contract.LocalRotation, 1e-5f))
            {
                failure = "child-" + index + "-transform";
                return false;
            }
            MeshFilter[] filters = actual.GetComponents<MeshFilter>();
            MeshRenderer[] renderers = actual.GetComponents<MeshRenderer>();
            Mesh mesh = filters.Length == 1 ? filters[0].sharedMesh : null;
            if (filters.Length != 1 || renderers.Length != 1 || mesh == null || !renderers[0].enabled ||
                !string.Equals(mesh.name, contract.MeshName, StringComparison.Ordinal))
            {
                failure = "child-" + index + "-visual=" + filters.Length + "/" + renderers.Length + "/" +
                          (mesh == null ? "null" : mesh.name);
                return false;
            }
            if (mesh.vertexCount != contract.VertexCount || mesh.subMeshCount != 1 ||
                mesh.GetIndexCount(0) != contract.IndexCount ||
                !mesh.HasVertexAttribute(VertexAttribute.TexCoord0) ||
                !mesh.HasVertexAttribute(VertexAttribute.TexCoord1) ||
                mesh.HasVertexAttribute(VertexAttribute.Color))
            {
                failure = "child-" + index + "-mesh-closure=" + mesh.vertexCount + "/" +
                          mesh.subMeshCount + "/" + (mesh.subMeshCount == 0 ? 0 : mesh.GetIndexCount(0));
                return false;
            }
            Material[] materials = renderers[0].sharedMaterials;
            if (materials.Length != contract.MaterialSlots.Length || materials.Length != mesh.subMeshCount)
            {
                failure = "child-" + index + "-material-count=" + materials.Length + "/expected-" +
                          contract.MaterialSlots.Length;
                return false;
            }
            for (int slot = 0; slot < materials.Length; slot++)
            {
                string actualName = materials[slot] == null ? string.Empty :
                    NormalizeNativeMaterialName(materials[slot].name);
                if (string.Equals(actualName, contract.MaterialSlots[slot], StringComparison.Ordinal)) continue;
                failure = "child-" + index + "-slot-" + slot + "=" + actualName + "/expected-" +
                          contract.MaterialSlots[slot];
                return false;
            }
            if (!RuntimeChildColliderContractValid(actual, contract, out string colliderFailure))
            {
                failure = "child-" + index + "-collider=" + colliderFailure;
                return false;
            }
            if (!RuntimeFurnitureHierarchyContractValid(actual, contract.Children, false,
                    out string nestedFailure))
            {
                failure = "child-" + index + "/" + nestedFailure;
                return false;
            }
        }

        for (int index = expected.Length; index < parent.childCount; index++)
        {
            Transform extra = parent.GetChild(index);
            bool marker = allowPlacementMarkers &&
                          (extra.name.StartsWith("WALL_BACKED_PROP_OUTWARD_", StringComparison.Ordinal) ||
                           extra.name.StartsWith("WALL_BACKED_PROP_PROVENANCE_", StringComparison.Ordinal));
            if (!marker || extra.childCount != 0 || extra.GetComponents<Component>().Length != 1)
            {
                failure = "unmanifested-child-" + index + "=" + extra.name;
                return false;
            }
        }
        return true;
    }

    private static bool RuntimeChildColliderContractValid(Transform node,
        RuntimeChildFurnitureContract contract, out string failure)
    {
        failure = string.Empty;
        Collider[] all = node.GetComponents<Collider>();
        if (contract.BoxColliders.Length > 0)
        {
            BoxCollider[] boxes = node.GetComponents<BoxCollider>();
            if (boxes.Length != contract.BoxColliders.Length || all.Length != boxes.Length)
            {
                failure = "box-count=" + boxes.Length + "/all=" + all.Length + "/expected=" +
                          contract.BoxColliders.Length;
                return false;
            }
            for (int index = 0; index < boxes.Length; index++)
            {
                BoxCollider actual = boxes[index];
                RuntimeBoxColliderProfile expected = contract.BoxColliders[index];
                if (actual == null || !actual.enabled || actual.isTrigger || actual.sharedMaterial != null ||
                    !RuntimeVectorApproximately(actual.center, expected.Center, 1e-5f) ||
                    !RuntimeVectorApproximately(actual.size, expected.Size, 1e-5f))
                {
                    failure = "box-" + index + "-state";
                    return false;
                }
            }
            return true;
        }
        if (string.IsNullOrEmpty(contract.CollisionMeshName))
        {
            if (all.Length == 0) return true;
            failure = "unexpected-collider-count=" + all.Length;
            return false;
        }
        MeshCollider[] meshColliders = node.GetComponents<MeshCollider>();
        MeshCollider collider = meshColliders.Length == 1 ? meshColliders[0] : null;
        Mesh collisionMesh = collider == null ? null : collider.sharedMesh;
        if (meshColliders.Length != 1 || all.Length != 1 || collider == null ||
            collider.enabled != contract.CollisionEnabled || collider.isTrigger ||
            collider.sharedMaterial != null || collisionMesh == null ||
            !string.Equals(collisionMesh.name, contract.CollisionMeshName, StringComparison.Ordinal) ||
            collider.convex != contract.CollisionConvex || (int)collider.cookingOptions != 30)
        {
            failure = "mesh-state=" + (collisionMesh == null ? "null" : collisionMesh.name) +
                      "/enabled=" + (collider != null && collider.enabled) + "/convex=" +
                      (collider != null && collider.convex) + "/cooking=" +
                      (collider == null ? -1 : (int)collider.cookingOptions);
            return false;
        }
        return true;
    }

    private static bool RuntimeQuaternionApproximately(Quaternion first, Quaternion second, float epsilon)
    {
        bool same = Mathf.Abs(first.x - second.x) <= epsilon && Mathf.Abs(first.y - second.y) <= epsilon &&
                    Mathf.Abs(first.z - second.z) <= epsilon && Mathf.Abs(first.w - second.w) <= epsilon;
        bool negated = Mathf.Abs(first.x + second.x) <= epsilon && Mathf.Abs(first.y + second.y) <= epsilon &&
                       Mathf.Abs(first.z + second.z) <= epsilon && Mathf.Abs(first.w + second.w) <= epsilon;
        return same || negated;
    }

    private static bool TryRuntimeRoomFloorBounds(GameObject root, Transform prop, out Bounds bounds)
    {
        bounds = default;
        int roomIndex = -1;
        Transform cursor = prop == null ? null : prop.parent;
        while (cursor != null)
        {
            if (cursor.name.StartsWith("DRESSING_", StringComparison.Ordinal) && cursor.name.Length >= 11 &&
                int.TryParse(cursor.name.Substring(9, 2), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out roomIndex)) break;
            if (string.Equals(cursor.name, SafeRoomMarker, StringComparison.Ordinal))
            {
                roomIndex = 0;
                break;
            }
            cursor = cursor.parent;
        }
        if (roomIndex < 0) return false;
        Transform rooms = root.transform.Find("10_ROOMS");
        if (rooms == null) return false;
        string roomPrefix = "ROOM_" + roomIndex.ToString("00", CultureInfo.InvariantCulture) + "_";
        Transform room = Enumerable.Range(0, rooms.childCount).Select(rooms.GetChild)
            .FirstOrDefault(item => item.name.StartsWith(roomPrefix, StringComparison.Ordinal));
        if (room == null) return false;
        Transform floor = room.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(item => string.Equals(item.name, "NATIVE_Floor", StringComparison.Ordinal));
        return floor != null && TryRuntimeRendererBounds(floor.gameObject, out bounds);
    }

    private static bool TryRuntimeRendererBounds(GameObject root, out Bounds bounds)
    {
        Renderer[] renderers = root == null ? Array.Empty<Renderer>() : root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            bounds = default;
            return false;
        }
        bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
        return true;
    }

    private static bool TryRuntimePhysicalBounds(GameObject root, out Bounds bounds)
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

    private static bool RuntimeBoundsInside(Bounds value, Bounds room, float tolerance)
    {
        return value.min.x >= room.min.x - tolerance && value.max.x <= room.max.x + tolerance &&
               value.min.z >= room.min.z - tolerance && value.max.z <= room.max.z + tolerance;
    }

    private static string[] RuntimeFurnitureOverlapDetails(IEnumerable<Transform> furniture)
    {
        Transform[] items = furniture.Where(item => item != null).ToArray();
        var failures = new List<string>();
        for (int first = 0; first < items.Length; first++)
        {
            if (!TryRuntimePhysicalBounds(items[first].gameObject, out Bounds firstBounds))
            {
                failures.Add("physical-bounds-missing=" + HierarchyPath(items[first]));
                continue;
            }
            for (int second = first + 1; second < items.Length; second++)
            {
                if (!TryRuntimePhysicalBounds(items[second].gameObject, out Bounds secondBounds))
                {
                    failures.Add("physical-bounds-missing=" + HierarchyPath(items[second]));
                    continue;
                }
                float overlapX = Mathf.Min(firstBounds.max.x, secondBounds.max.x) -
                                 Mathf.Max(firstBounds.min.x, secondBounds.min.x);
                float overlapY = Mathf.Min(firstBounds.max.y, secondBounds.max.y) -
                                 Mathf.Max(firstBounds.min.y, secondBounds.min.y);
                float overlapZ = Mathf.Min(firstBounds.max.z, secondBounds.max.z) -
                                 Mathf.Max(firstBounds.min.z, secondBounds.min.z);
                if (overlapX <= .02f || overlapY <= .02f || overlapZ <= .02f) continue;
                failures.Add("furniture-overlap=" + HierarchyPath(items[first]) + "<->" +
                             HierarchyPath(items[second]));
            }
        }
        return failures.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] RuntimeFurniturePortalBlockerDetails(Transform[] furniture, Transform[] transforms)
    {
        Transform[] portals = transforms.Where(item =>
            item.name.StartsWith("DOORV2_SOCKET_", StringComparison.Ordinal) ||
            item.name.StartsWith("OPEN_CONNECTION_", StringComparison.Ordinal)).ToArray();
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        Physics.SyncTransforms();
        foreach (Transform portal in portals)
        {
            Vector3 normal = portal.forward;
            normal.y = 0f;
            if (normal.sqrMagnitude < .99f)
            {
                blocked.Add("portal-axis-invalid=" + HierarchyPath(portal));
                continue;
            }
            normal.Normalize();
            foreach (float sign in new[] { -1f, 1f })
            {
                for (int sample = 0; sample < 5; sample++)
                {
                    Vector3 point = portal.position + normal * sign * (.35f + sample * .38f);
                    Collider[] hits = Physics.OverlapCapsule(point + Vector3.up * .35f,
                        point + Vector3.up * 1.75f, .42f, ~0, QueryTriggerInteraction.Ignore);
                    Collider hit = hits.FirstOrDefault(candidate => candidate != null &&
                        furniture.Any(owner => RuntimeIsDescendantOf(candidate.transform, owner)));
                    if (hit == null) continue;
                    Transform owner = furniture.First(item => RuntimeIsDescendantOf(hit.transform, item));
                    blocked.Add("portal-blocked=" + HierarchyPath(portal) + "<-" + HierarchyPath(owner));
                }
            }
        }
        return blocked.ToArray();
    }

    private static bool RuntimeHasDirectChildWithPrefix(Transform parent, string prefix)
    {
        return RuntimeDirectChildrenWithPrefix(parent, prefix).Length > 0;
    }

    private static Transform[] RuntimeDirectChildrenWithPrefix(Transform parent, string prefix)
    {
        if (parent == null) return Array.Empty<Transform>();
        return Enumerable.Range(0, parent.childCount).Select(parent.GetChild)
            .Where(child => child.name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    private static bool RuntimeIsDescendantOf(Transform value, Transform ancestor)
    {
        Transform cursor = value;
        while (cursor != null)
        {
            if (cursor == ancestor) return true;
            cursor = cursor.parent;
        }
        return false;
    }

    private static bool RuntimeVectorApproximately(Vector3 first, Vector3 second, float epsilon)
    {
        return Mathf.Abs(first.x - second.x) <= epsilon && Mathf.Abs(first.y - second.y) <= epsilon &&
               Mathf.Abs(first.z - second.z) <= epsilon;
    }

    private static bool WarehouseGroundMeshContractValid(Mesh mesh, out string failure)
    {
        failure = string.Empty;
        if (mesh == null)
        {
            failure = "mesh-null";
            return false;
        }
        string[] primitiveNames = { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };
        if (!string.Equals(mesh.name, "Floor", StringComparison.Ordinal) || primitiveNames.Contains(mesh.name))
        {
            failure = "mesh-identity:" + mesh.name;
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
            !mesh.HasVertexAttribute(VertexAttribute.TexCoord1))
        {
            failure = "vertex-channel-closure";
            return false;
        }
        Bounds sourceBounds = mesh.bounds;
        if (Mathf.Abs(sourceBounds.size.x - WarehouseGroundSourceWidth) > .001f ||
            Mathf.Abs(sourceBounds.size.z - WarehouseGroundSourceDepth) > .001f ||
            sourceBounds.size.y > .001f)
        {
            failure = "source-bounds:" + sourceBounds.size.ToString("F6");
            return false;
        }
        if (!mesh.isReadable)
        {
            failure = "mesh-not-readable";
            return false;
        }
        try
        {
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uv0 = mesh.uv;
            var uv1 = mesh.uv2;
            if (normals.Length != 4 || tangents.Length != 4 || uv0.Length != 4 || uv1.Length != 4)
            {
                failure = "vertex-channel-lengths:" + normals.Length + "/" + tangents.Length + "/" +
                          uv0.Length + "/" + uv1.Length;
                return false;
            }
            for (int index = 0; index < normals.Length; index++)
            {
                if (Vector3.Dot(normals[index].normalized, Vector3.up) >= .9999f) continue;
                failure = "non-upward-normal:" + index;
                return false;
            }
        }
        catch (Exception exception)
        {
            failure = "vertex-channel-read:" + exception.GetType().Name;
            return false;
        }
        return true;
    }

    private static bool AllRendererSlotsUseNativeProfile(Transform root, string profileName)
    {
        if (root == null) return false;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        return renderers.Length > 0 && renderers.All(renderer => renderer.sharedMaterials.Length > 0 &&
            renderer.sharedMaterials.All(material => material != null &&
                string.Equals(NormalizeNativeMaterialName(material.name), profileName, StringComparison.Ordinal)));
    }

    private static int CountRendererSlotsUsingNativeProfile(Transform root, string profileName)
    {
        if (root == null) return 0;
        return root.GetComponentsInChildren<Renderer>(true).Sum(renderer => renderer.sharedMaterials.Count(material =>
            material != null && string.Equals(NormalizeNativeMaterialName(material.name), profileName,
                StringComparison.Ordinal)));
    }

    private bool RebindSceneMaterials(GameObject root)
    {
        Shader hdrpResident = Shader.Find(ResidentShaderName);
        Shader milkResident = Shader.Find(MilkLitTemplateShaderName);
        if (hdrpResident == null || milkResident == null)
        {
            log.LogError("Vektor Kill House material gate failed: required resident shaders are unavailable; hdrp=" +
                         (hdrpResident != null) + ", milkLitTemplate=" + (milkResident != null) + ".");
            return false;
        }
        int slots = 0;
        int litFixtureRenderers = 0;
        int dimFixtureRenderers = 0;
        int darkFixtureRenderers = 0;
        int invalidFixtureVisualRenderers = 0;
        var usedProfiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = renderer.sharedMaterials;
            bool changed = false;
            for (int index = 0; index < materials.Length; index++)
            {
                Material source = materials[index];
                if (source == null) return false;
                if (ownedRuntimeMaterialIds.Contains(source.GetInstanceID())) continue;
                if (!runtimeMaterialsBySourceInstance.TryGetValue(source.GetInstanceID(), out Material destination) || destination == null)
                {
                    string profileName = NormalizeNativeMaterialName(source.name);
                    if (!NativeMaterialProfiles.TryGetValue(profileName, out NativeMaterialProfile profile))
                    {
                        log.LogError("Vektor Kill House material gate failed: no audited native profile for " + source.name + ".");
                        return false;
                    }
                    Shader resident = string.Equals(profile.ResidentShaderName, MilkLitTemplateShaderName,
                        StringComparison.Ordinal) ? milkResident : hdrpResident;
                    destination = CreateResidentNativeMaterial(source, resident, profileName, profile);
                    if (destination == null) return false;
                    runtimeMaterialsBySourceInstance[source.GetInstanceID()] = destination;
                    ownedRuntimeMaterialIds.Add(destination.GetInstanceID());
                    usedProfiles.Add(profileName);
                }
                materials[index] = destination;
                changed = true;
                slots++;
            }
            if (changed) renderer.sharedMaterials = materials;
            int fixtureState = ApplyFixtureVisualState(renderer);
            if (fixtureState == 1) litFixtureRenderers++;
            else if (fixtureState == 2) dimFixtureRenderers++;
            else if (fixtureState == 3) darkFixtureRenderers++;
            if (fixtureState > 0 && !FixtureVisualStateValid(renderer, fixtureState))
                invalidFixtureVisualRenderers++;
        }
        bool furnitureClosurePassed = ValidateFurnitureRendererClosure(root, out int furnitureRenderers,
            out int furnitureFamilies, out int invalidFurnitureRenderers, out string furnitureFailure);
        string[] invalidResidentProfiles = runtimeMaterialsBySourceInstance.Values
            .Where(material => !MaterialHasResidentProfileContract(material))
            .Select(DescribeResidentProfileValidation)
            .ToArray();
        bool passed = slots > 0 && runtimeMaterialsBySourceInstance.Count == NativeMaterialProfiles.Count &&
                      usedProfiles.SetEquals(NativeMaterialProfiles.Keys) &&
                      invalidResidentProfiles.Length == 0 &&
                      furnitureClosurePassed &&
                      litFixtureRenderers > 0 && dimFixtureRenderers > 0 && darkFixtureRenderers > 0 &&
                      invalidFixtureVisualRenderers == 0;
        log.LogInfo("Vektor Kill House material rebind: passed=" + passed + ", slots=" + slots +
                    ", uniqueNativeMaterials=" + runtimeMaterialsBySourceInstance.Count +
                    ", nativeProfiles=" + usedProfiles.Count + ", matteArchitecturalProfiles=3" +
                    (invalidResidentProfiles.Length == 0 ? string.Empty :
                        ", invalidResidentProfiles=[" + string.Join(" | ", invalidResidentProfiles) + "]") +
                    ", furnitureRenderers=" + furnitureRenderers + ", furnitureFamilies=" + furnitureFamilies +
                    ", invalidFurnitureRenderers=" + invalidFurnitureRenderers +
                    (string.IsNullOrEmpty(furnitureFailure) ? string.Empty : ", furnitureFailure=" + furnitureFailure) +
                    ", fixtureVisualStates=lit:" + litFixtureRenderers + "/dim:" + dimFixtureRenderers +
                    "/dark:" + darkFixtureRenderers + ", invalidFixtureVisuals=" +
                    invalidFixtureVisualRenderers + ", fluorescentEmission=307.2/dim9.6/exposureWeight0" +
                    ", copyPropertiesUsed=false.");
        return passed;
    }

    private static string DescribeResidentProfileValidation(Material material)
    {
        const string prefix = "RUNTIME_NATIVE_";
        string profileName = material != null && material.name != null &&
                             material.name.StartsWith(prefix, StringComparison.Ordinal)
            ? material.name.Substring(prefix.Length)
            : "<unknown>";
        if (!NativeMaterialProfiles.TryGetValue(profileName, out NativeMaterialProfile profile))
            return profileName + "{profile=false}";
        bool surfaceValid = FurnitureSurfaceContractValid(material, profileName);
        return profileName + "{resident=" + MaterialHasResidentContract(material, profile) +
               ",surface=" + surfaceValid +
               ",texture=" + MaterialHasExactTextureClosure(material, profileName) +
               (surfaceValid ? string.Empty : ",surfaceValues=" + DescribeFurnitureSurfaceValues(material)) +
               "," + DescribeMaterialContract(material) + "}";
    }

    private static string DescribeFurnitureSurfaceValues(Material material)
    {
        string[] names =
        {
            "_MetallicRemapMin", "_MetallicRemapMax", "_SmoothnessRemapMin", "_SmoothnessRemapMax",
            "_AORemapMin", "_AORemapMax", "_OcclusionStrength", "_ReceivesSSR", "_MaterialID",
            "_TransmissionEnable", "_TransmissionMask"
        };
        return string.Join("/", names.Select(name => name + "=" +
            (material != null && material.HasProperty(name)
                ? material.GetFloat(name).ToString("0.###", CultureInfo.InvariantCulture)
                : "<missing>")));
    }

    private static bool ValidateFurnitureRendererClosure(GameObject root, out int rendererCount,
        out int familyCount, out int invalidCount, out string firstFailure)
    {
        rendererCount = 0;
        invalidCount = 0;
        firstFailure = string.Empty;
        var families = new HashSet<string>(StringComparer.Ordinal);
        if (root == null)
        {
            familyCount = 0;
            firstFailure = "root-null";
            return false;
        }

        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter == null ? null : filter.sharedMesh;
            if (mesh == null || !FurnitureMaterialSlotsByMesh.TryGetValue(mesh.name, out string[] expectedSlots))
                continue;
            rendererCount++;
            families.Add(mesh.name);
            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
            Material[] actualSlots = renderer == null ? Array.Empty<Material>() : renderer.sharedMaterials;
            string failure = string.Empty;
            if (renderer == null) failure = "renderer-missing";
            else if (mesh.subMeshCount <= 0 || actualSlots.Length != mesh.subMeshCount)
                failure = "submesh-slot-count:" + mesh.subMeshCount + "/" + actualSlots.Length;
            else if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) failure = "uv0-missing";
            else if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) failure = "uv1-missing";
            else if (string.Equals(mesh.name, "T_bathtub", StringComparison.Ordinal) &&
                     !mesh.HasVertexAttribute(VertexAttribute.TexCoord2))
                failure = "bathtub-uv2-missing";
            else if (FurnitureMeshesWithoutVertexColor.Contains(mesh.name) &&
                     mesh.HasVertexAttribute(VertexAttribute.Color)) failure = "unexpected-color0";
            else if (!FurnitureMeshesWithoutVertexColor.Contains(mesh.name) &&
                     !mesh.HasVertexAttribute(VertexAttribute.Color)) failure = "color0-missing";
            else
            {
                for (int index = 0; index < actualSlots.Length; index++)
                {
                    string expected = expectedSlots[Math.Min(index, expectedSlots.Length - 1)];
                    Material actual = actualSlots[index];
                    if (actual == null ||
                        !string.Equals(NormalizeNativeMaterialName(actual.name), expected, StringComparison.Ordinal))
                    {
                        failure = "slot-" + index + ":expected-" + expected + "/actual-" +
                                  (actual == null ? "null" : NormalizeNativeMaterialName(actual.name));
                        break;
                    }
                    if (!MaterialHasExactTextureClosure(actual, expected))
                    {
                        failure = "slot-" + index + ":texture-closure-" + expected;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(failure)) continue;
            invalidCount++;
            if (string.IsNullOrEmpty(firstFailure))
                firstFailure = HierarchyPath(filter.transform) + ":" + failure;
        }
        familyCount = families.Count;
        return rendererCount > 0 && invalidCount == 0;
    }

    private static int ApplyFixtureVisualState(Renderer renderer)
    {
        Transform cursor = renderer == null ? null : renderer.transform;
        string holderName = string.Empty;
        while (cursor != null)
        {
            if (cursor.name.StartsWith("ROOM_LIGHT_", StringComparison.Ordinal) &&
                cursor.name.IndexOf("_STATE_", StringComparison.Ordinal) >= 0)
            {
                holderName = cursor.name;
                break;
            }
            cursor = cursor.parent;
        }
        int state = holderName.EndsWith("_STATE_LIT", StringComparison.Ordinal) ? 1 :
            holderName.EndsWith("_STATE_DIM", StringComparison.Ordinal) ? 2 :
            holderName.EndsWith("_STATE_DARK", StringComparison.Ordinal) ? 3 : 0;
        if (state == 0) return 0;
        float emission = state == 1 ? KillHouseFluorescentLitEmission :
            state == 2 ? KillHouseFluorescentDimEmission : 0f;
        Color emissionColor = emission <= .001f ? Color.black : new Color(emission, emission, emission, 1f);
        Color ldrColor = emission <= .001f ? Color.black : Color.white;
        MaterialPropertyBlock block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block);
        block.SetColor("_EmissiveColor", emissionColor);
        block.SetColor("_EmissiveColorLDR", ldrColor);
        block.SetColor("_EmissionColor", ldrColor);
        block.SetFloat("_EmissiveIntensity", emission);
        block.SetFloat("_UseEmissiveIntensity", 1f);
        block.SetFloat("_EmissiveColorMode", 1f);
        block.SetFloat("_AlbedoAffectEmissive", 1f);
        block.SetFloat("_EmissiveIntensityUnit", KillHouseFluorescentIntensityUnit);
        block.SetFloat("_EmissiveExposureWeight", KillHouseFluorescentExposureWeight);
        renderer.SetPropertyBlock(block);
        return state;
    }

    private static bool FixtureVisualStateValid(Renderer renderer, int state)
    {
        if (renderer == null || state < 1 || state > 3 || renderer.sharedMaterials.Length == 0) return false;
        Transform fixture = renderer.transform;
        while (fixture != null && !fixture.name.StartsWith("NATIVE_Lamp_fluorescent_B_", StringComparison.Ordinal))
            fixture = fixture.parent;
        if (fixture == null ||
            Vector3.Dot(fixture.TransformDirection(Vector3.back).normalized, Vector3.down) < .98f) return false;
        foreach (Material material in renderer.sharedMaterials)
        {
            if (material == null ||
                !string.Equals(NormalizeNativeMaterialName(material.name), "Lamps_C_on__cagville",
                    StringComparison.Ordinal) ||
                !material.HasProperty("_EmissiveColor") || !material.HasProperty("_EmissiveColorMap") ||
                !material.HasProperty("_EmissiveIntensity") || !material.HasProperty("_UseEmissiveIntensity") ||
                !material.HasProperty("_EmissiveIntensityUnit") || !material.HasProperty("_AlbedoAffectEmissive") ||
                !material.HasProperty("_EmissiveExposureWeight")) return false;
            Texture emissiveMap = material.GetTexture("_EmissiveColorMap");
            if (emissiveMap == null || !string.Equals(emissiveMap.name, "Lamps_C_Emissive", StringComparison.Ordinal) ||
                !material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") ||
                Mathf.Abs(material.GetFloat("_AlbedoAffectEmissive") - 1f) > .001f)
                return false;
        }

        float expected = state == 1 ? KillHouseFluorescentLitEmission :
            state == 2 ? KillHouseFluorescentDimEmission : 0f;
        MaterialPropertyBlock block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block);
        Color emissive = block.GetColor("_EmissiveColor");
        return Mathf.Abs(block.GetFloat("_EmissiveIntensity") - expected) <= .01f &&
               Mathf.Abs(block.GetFloat("_UseEmissiveIntensity") - 1f) <= .001f &&
               Mathf.Abs(block.GetFloat("_AlbedoAffectEmissive") - 1f) <= .001f &&
               Mathf.Abs(block.GetFloat("_EmissiveIntensityUnit") - KillHouseFluorescentIntensityUnit) <= .001f &&
               Mathf.Abs(block.GetFloat("_EmissiveExposureWeight") - KillHouseFluorescentExposureWeight) <= .001f &&
               Mathf.Abs(emissive.maxColorComponent - expected) <= .01f;
    }

    private Material CreateResidentNativeMaterial(Material source, Shader resident, string profileName,
        NativeMaterialProfile profile)
    {
        try
        {
            // Read the transported texture objects without mutating the transport material. Scalar,
            // color, keyword, and texture-transform state is rehydrated from the exact embedded
            // installed-game record below, so proxy shader defaults can never leak into furniture.
            Color sourceBaseColor = GetColor(source, "_BaseColor", profile.BaseColor);
            Texture baseMap = GetTexture(source, "_BaseColorMap");
            Texture normalMap = profile.HasNormal ? GetTexture(source, "_NormalMap") : null;
            Texture maskMap = profile.HasMask ? GetTexture(source, "_MaskMap") : null;
            Texture emissiveMap = profile.HasEmissiveMap ? GetTexture(source, "_EmissiveColorMap") : null;
            bool hasDetailMap = ExpectedDetailTextureNames.TryGetValue(profileName, out string expectedDetailName);
            Texture detailMap = hasDetailMap ? GetTexture(source, "_DetailMap") : null;
            string expectedBaseTexture = ExpectedBaseTextureNames.TryGetValue(profileName, out string expected)
                ? expected
                : string.Empty;
            if (baseMap == null || (profile.HasNormal && normalMap == null) ||
                ReferenceEquals(baseMap, Texture2D.whiteTexture) ||
                (!string.IsNullOrEmpty(expectedBaseTexture) &&
                 !string.Equals(baseMap.name, expectedBaseTexture, StringComparison.Ordinal)) ||
                (profile.HasMask && maskMap == null) || (profile.HasEmissiveMap && emissiveMap == null) ||
                !TextureNameMatches(normalMap, ExpectedNormalTextureNames, profileName, profile.HasNormal) ||
                !TextureNameMatches(maskMap, ExpectedMaskTextureNames, profileName, profile.HasMask) ||
                !TextureNameMatches(emissiveMap, ExpectedEmissiveTextureNames, profileName,
                    profile.HasEmissiveMap) ||
                (hasDetailMap && !TextureNameEquals(detailMap, expectedDetailName)))
            {
                log.LogError("Vektor Kill House material gate failed: transported vanilla texture closure is incomplete for " +
                             profileName + "; expectedBase=" + expectedBaseTexture +
                             ", actualBase=" + (baseMap == null ? "<null>" : baseMap.name) + ".");
                return null;
            }

            Material destination = new Material(resident)
            {
                name = "RUNTIME_NATIVE_" + profileName,
                enableInstancing = true,
                renderQueue = NativeOpaqueRenderQueue
            };
            destination.shader = resident;
            // Assigning MilkShaders/Lit-Template resets the material to the shader's default
            // transparent-range queue. Reassert the exact opaque donor queue after shader binding.
            destination.renderQueue = NativeOpaqueRenderQueue;
            SetColor(destination, "_BaseColor", sourceBaseColor);
            SetColor(destination, "_Color", sourceBaseColor);
            SetTexture(destination, "_BaseColorMap", baseMap);
            SetTexture(destination, "_BaseMap", baseMap);
            SetTexture(destination, "_MainTex", baseMap);
            SetFloat(destination, "_Metallic", profile.Metallic);
            SetFloat(destination, "_Smoothness", profile.Smoothness);
            SetFloat(destination, "_Glossiness", profile.Smoothness);
            SetFloat(destination, "_NormalScale", profile.NormalScale);
            SetFloat(destination, "_BumpScale", profile.NormalScale);
            SetFloat(destination, "_SurfaceType", 0f);
            SetFloat(destination, "_Surface", 0f);
            SetFloat(destination, "_AlphaCutoffEnable", 0f);
            SetFloat(destination, "_DoubleSidedEnable", 0f);
            SetFloat(destination, "_CullMode", 2f);
            SetFloat(destination, "_CullModeForward", 2f);
            SetFloat(destination, "_Cull", 2f);
            SetFloat(destination, "_ZWrite", 1f);
            if (FurnitureSurfaceProfiles.TryGetValue(profileName, out FurnitureSurfaceProfile furnitureSurface))
            {
                SetFloat(destination, "_MetallicRemapMin", 0f);
                SetFloat(destination, "_MetallicRemapMax", furnitureSurface.MetallicRemapMax);
                SetFloat(destination, "_SmoothnessRemapMin", 0f);
                SetFloat(destination, "_SmoothnessRemapMax", furnitureSurface.SmoothnessRemapMax);
                SetFloat(destination, "_AORemapMin", furnitureSurface.AoRemapMin);
                SetFloat(destination, "_AORemapMax", furnitureSurface.AoRemapMax);
                SetFloat(destination, "_OcclusionStrength", furnitureSurface.OcclusionStrength);
                SetFloat(destination, "_ReceivesSSR", furnitureSurface.ReceivesSsr);
                SetFloat(destination, "_MaterialID", 1f);
                SetFloat(destination, "_TransmissionEnable", 1f);
                SetFloat(destination, "_TransmissionMask", 1f);
            }
            if (profile.MatteArchitectural)
            {
                SetFloat(destination, "_MetallicRemapMin", 0f);
                SetFloat(destination, "_MetallicRemapMax", 0f);
                SetFloat(destination, "_SmoothnessRemapMin", 0f);
                SetFloat(destination, "_SmoothnessRemapMax", profile.Smoothness);
                SetFloat(destination, "_ReceivesSSR", 0f);
                SetFloat(destination, "_EnvironmentReflections", 0f);
                SetFloat(destination, "_CoatMask", 0f);
                SetFloat(destination, "_ClearCoatMask", 0f);
            }

            if (profile.HasNormal)
            {
                SetTexture(destination, "_NormalMap", normalMap);
                SetTexture(destination, "_BumpMap", normalMap);
                destination.EnableKeyword("_NORMALMAP");
                destination.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
            else
            {
                destination.DisableKeyword("_NORMALMAP");
                destination.DisableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
            if (profile.HasMask)
            {
                SetTexture(destination, "_MaskMap", maskMap);
                destination.EnableKeyword("_MASKMAP");
            }
            else
            {
                SetTexture(destination, "_MaskMap", null);
                destination.DisableKeyword("_MASKMAP");
            }

            Color emissiveColor = profile.EmissiveIntensity > .001f
                ? new Color(profile.EmissiveIntensity, profile.EmissiveIntensity, profile.EmissiveIntensity, 1f)
                : Color.black;
            SetColor(destination, "_EmissiveColor", emissiveColor);
            SetColor(destination, "_EmissionColor", emissiveColor);
            SetTexture(destination, "_EmissiveColorMap", emissiveMap);
            SetTexture(destination, "_EmissionMap", emissiveMap);
            if (hasDetailMap) SetTexture(destination, "_DetailMap", detailMap);
            if (profile.HasEmissiveMap && profile.EmissiveIntensity > .001f)
            {
                destination.EnableKeyword("_EMISSIVE_COLOR_MAP");
                destination.EnableKeyword("_EMISSION");
            }
            else
            {
                destination.DisableKeyword("_EMISSIVE_COLOR_MAP");
                destination.DisableKeyword("_EMISSION");
            }

            if (string.Equals(profileName, "Lamps_C_on__cagville", StringComparison.Ordinal))
            {
                Color exactTubeEmission = new Color(KillHouseFluorescentLitEmission,
                    KillHouseFluorescentLitEmission, KillHouseFluorescentLitEmission, 1f);
                SetColor(destination, "_EmissiveColor", exactTubeEmission);
                SetColor(destination, "_EmissiveColorLDR", Color.white);
                SetColor(destination, "_EmissionColor", Color.white);
                SetFloat(destination, "_UseEmissiveIntensity", 1f);
                SetFloat(destination, "_EmissiveColorMode", 1f);
                SetFloat(destination, "_AlbedoAffectEmissive", 1f);
                SetFloat(destination, "_EmissiveIntensity", KillHouseFluorescentLitEmission);
                SetFloat(destination, "_EmissiveIntensityUnit", KillHouseFluorescentIntensityUnit);
                SetFloat(destination, "_EmissiveExposureWeight", KillHouseFluorescentExposureWeight);
                destination.EnableKeyword("_EMISSIVE_COLOR_MAP");
                destination.DisableKeyword("_EMISSION");
            }
            else if (string.Equals(profileName, "Kitchen_TableChair", StringComparison.Ordinal))
            {
                // This vanilla material is the one residential exception to HDRP/Lit. Its installed
                // shader is MilkShaders/Lit-Template and its exact keyword state intentionally does
                // not use the HDRP normal/mask keywords even though those texture properties exist.
                SetFloat(destination, "_BASE_LAYER_TRIPLANAR", 0f);
                SetFloat(destination, "_DETAIL_TRIPLANAR_UV", 0f);
                SetFloat(destination, "_ConservativeDepthOffsetEnable", 0f);
                SetFloat(destination, "_ExcludeFromTUAndAA", 0f);
                SetFloat(destination, "_MaterialTypeMask", 2f);
                SetFloat(destination, "_RenderQueueType", 1f);
                SetFloat(destination, "_RequireSplitLighting", 0f);
                SetFloat(destination, "_USE_TRANSPARENT_SHADOWS", 0f);
                SetFloat(destination, "_saturation", 1f);
                SetFloat(destination, "_ReceivesSSR", 0f);
                destination.EnableKeyword("_DISABLE_SSR");
                destination.EnableKeyword("_DISABLE_SSR_TRANSPARENT");
                destination.DisableKeyword("_MASKMAP");
                destination.DisableKeyword("_NORMALMAP");
                destination.DisableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
            else if (string.Equals(profileName, "In_Floor_Basement", StringComparison.Ordinal))
            {
                if (destination.HasProperty("_DetailMap"))
                {
                    destination.SetTextureScale("_DetailMap", new Vector2(5f, 5f));
                    destination.SetTextureOffset("_DetailMap", Vector2.zero);
                }
                SetFloat(destination, "_BASE_LAYER_TRIPLANAR", 0f);
                SetFloat(destination, "_DETAIL_TRIPLANAR_UV", 1f);
                SetFloat(destination, "_ConservativeDepthOffsetEnable", 0f);
                SetFloat(destination, "_ExcludeFromTUAndAA", 0f);
                SetFloat(destination, "_MaterialTypeMask", 2f);
                SetFloat(destination, "_RenderQueueType", 1f);
                SetFloat(destination, "_RequireSplitLighting", 0f);
                SetFloat(destination, "_USE_TRANSPARENT_SHADOWS", 0f);
                SetFloat(destination, "_saturation", 1f);
                SetFloat(destination, "_ReceivesSSR", 0f);
                destination.EnableKeyword("_DETAIL_MAP");
                destination.EnableKeyword("_DETAIL_TRIPLANAR_UV");
                destination.EnableKeyword("_DISABLE_SSR_TRANSPARENT");
                destination.DisableKeyword("_DISABLE_SSR");
                destination.DisableKeyword("_MASKMAP");
                destination.DisableKeyword("_NORMALMAP");
                destination.DisableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
            else if (string.Equals(profileName, "Kitchen_Cabinet_Marble", StringComparison.Ordinal))
            {
                // Exact HDRP/Lit donor state: tangent-space mode remains selected even though this
                // marble slot has no normal texture and therefore no _NORMALMAP keyword.
                destination.DisableKeyword("_NORMALMAP");
                destination.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
            else if (string.Equals(profileName, "Floor", StringComparison.Ordinal))
            {
                // Exact level11 Woods Warehouse ground contract. The material is world-space
                // triplanar, so stretching the native four-vertex donor to each warehouse apron
                // preserves texture density instead of stretching UVs.
                SetFloat(destination, "_UVBase", 5f);
                SetFloat(destination, "_TexWorldScale", .25f);
                SetFloat(destination, "_ObjectSpaceUVMapping", 0f);
                destination.EnableKeyword("_MAPPING_TRIPLANAR");
                destination.EnableKeyword("_DISABLE_SSR_TRANSPARENT");
                destination.EnableKeyword("_MASKMAP");
                destination.EnableKeyword("_NORMALMAP");
                destination.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }

            if (string.Equals(resident.name, ResidentShaderName, StringComparison.Ordinal))
                ValidateResidentHdrpMaterial(destination);
            if ((FurnitureMaterialProfileNames.Contains(profileName) ||
                 string.Equals(profileName, "Floor", StringComparison.Ordinal)) &&
                !ApplyEmbeddedExactDonorState(destination, profileName, out string donorFailure))
            {
                UnityEngine.Object.Destroy(destination);
                log.LogError("Vektor Kill House material gate failed: exact embedded donor state could not be applied for " +
                             profileName + "; " + donorFailure + ".");
                return null;
            }
            if (!MaterialHasResidentContract(destination, profile))
            {
                string detail = DescribeMaterialContract(destination);
                UnityEngine.Object.Destroy(destination);
                log.LogError("Vektor Kill House material gate failed: explicit resident state did not validate for " +
                             profileName + "; " + detail + ".");
                return null;
            }
            return destination;
        }
        catch (Exception exception)
        {
            log.LogError("Vektor Kill House material gate failed for " + profileName + ": " +
                         exception.GetType().Name + ": " + exception.Message);
            return null;
        }
    }

    private static string NormalizeNativeMaterialName(string name)
    {
        string value = name ?? string.Empty;
        if (value.StartsWith("MAT_NATIVE_", StringComparison.Ordinal)) value = value.Substring("MAT_NATIVE_".Length);
        if (value.StartsWith("RUNTIME_NATIVE_", StringComparison.Ordinal))
            value = value.Substring("RUNTIME_NATIVE_".Length);
        const string instanceSuffix = " (Instance)";
        if (value.EndsWith(instanceSuffix, StringComparison.Ordinal))
            value = value.Substring(0, value.Length - instanceSuffix.Length);
        return value;
    }

    private static string HierarchyPath(Transform item)
    {
        if (item == null) return "<null>";
        var names = new List<string>();
        Transform cursor = item;
        while (cursor != null)
        {
            names.Add(cursor.name);
            cursor = cursor.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private static Texture GetTexture(Material material, string property)
    {
        return material != null && material.HasProperty(property) ? material.GetTexture(property) : null;
    }

    private static Color GetColor(Material material, string property, Color fallback)
    {
        return material != null && material.HasProperty(property) ? material.GetColor(property) : fallback;
    }

    private static void SetTexture(Material material, string property, Texture value)
    {
        if (material.HasProperty(property)) material.SetTexture(property, value);
    }

    private static void SetFloat(Material material, string property, float value)
    {
        if (material.HasProperty(property)) material.SetFloat(property, value);
    }

    private static void SetColor(Material material, string property, Color value)
    {
        if (material.HasProperty(property)) material.SetColor(property, value);
    }

    private static bool ApplyEmbeddedExactDonorState(Material material, string profileName, out string failure)
    {
        failure = string.Empty;
        if (material == null)
        {
            failure = "material-null";
            return false;
        }
        string resourceName = typeof(OperatorKillHousePlugin).Assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("." + profileName + ".json",
                StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(resourceName))
        {
            failure = "embedded-record-missing";
            return false;
        }

        try
        {
            using Stream stream = typeof(OperatorKillHousePlugin).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                failure = "embedded-stream-missing";
                return false;
            }
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            long shaderPathId = root.GetProperty("m_Shader").GetProperty("m_PathID").GetInt64();
            string expectedShader = shaderPathId == 210L ? MilkLitTemplateShaderName :
                shaderPathId == 354L ? ResidentShaderName : string.Empty;
            if (string.IsNullOrEmpty(expectedShader) || material.shader == null ||
                !string.Equals(material.shader.name, expectedShader, StringComparison.Ordinal))
            {
                failure = "resident-shader:" + shaderPathId + "/" + material.shader?.name;
                return false;
            }

            JsonElement saved = root.GetProperty("m_SavedProperties");
            int floatCount = 0;
            foreach (JsonProperty property in saved.GetProperty("m_Floats").EnumerateObject())
            {
                if (!material.HasProperty(property.Name)) continue;
                material.SetFloat(property.Name, property.Value.GetSingle());
                floatCount++;
            }
            int colorCount = 0;
            foreach (JsonProperty property in saved.GetProperty("m_Colors").EnumerateObject())
            {
                if (!material.HasProperty(property.Name)) continue;
                JsonElement value = property.Value;
                material.SetColor(property.Name, new Color(value.GetProperty("m_R").GetSingle(),
                    value.GetProperty("m_G").GetSingle(), value.GetProperty("m_B").GetSingle(),
                    value.GetProperty("m_A").GetSingle()));
                colorCount++;
            }
            int transformCount = 0;
            foreach (JsonProperty property in saved.GetProperty("m_TexEnvs").EnumerateObject())
            {
                if (!material.HasProperty(property.Name)) continue;
                JsonElement scale = property.Value.GetProperty("m_Scale");
                JsonElement offset = property.Value.GetProperty("m_Offset");
                material.SetTextureScale(property.Name, new Vector2(scale.GetProperty("m_X").GetSingle(),
                    scale.GetProperty("m_Y").GetSingle()));
                material.SetTextureOffset(property.Name, new Vector2(offset.GetProperty("m_X").GetSingle(),
                    offset.GetProperty("m_Y").GetSingle()));
                transformCount++;
            }

            foreach (string keyword in material.shaderKeywords.ToArray()) material.DisableKeyword(keyword);
            foreach (JsonElement keyword in root.GetProperty("m_ValidKeywords").EnumerateArray())
                material.EnableKeyword(keyword.GetString());
            var disabledPasses = new HashSet<string>(
                root.GetProperty("m_DisabledShaderPasses").EnumerateArray()
                    .Select(value => value.GetString()), StringComparer.Ordinal);
            foreach (string passName in ExactDonorShaderPasses)
                material.SetShaderPassEnabled(passName, !disabledPasses.Contains(passName));
            foreach (string tagName in ExactDonorOverrideTags)
                material.SetOverrideTag(tagName, string.Empty);
            foreach (JsonProperty tag in root.GetProperty("m_StringTagMap").EnumerateObject())
                material.SetOverrideTag(tag.Name, tag.Value.GetString());
            material.globalIlluminationFlags = (MaterialGlobalIlluminationFlags)
                root.GetProperty("m_LightmapFlags").GetInt32();
            material.enableInstancing = root.GetProperty("m_EnableInstancingVariants").GetBoolean();
            material.doubleSidedGI = root.GetProperty("m_DoubleSidedGI").GetBoolean();
            material.renderQueue = root.GetProperty("m_CustomRenderQueue").GetInt32();
            if (floatCount == 0 || colorCount == 0 || transformCount == 0)
            {
                failure = "empty-applicable-state:" + floatCount + "/" + colorCount + "/" + transformCount;
                return false;
            }
            return EmbeddedExactDonorStateValid(material, profileName, out failure);
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name + ":" + exception.Message;
            return false;
        }
    }

    private static bool EmbeddedExactDonorStateValid(Material material, string profileName, out string failure)
    {
        failure = string.Empty;
        string resourceName = typeof(OperatorKillHousePlugin).Assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("." + profileName + ".json",
                StringComparison.OrdinalIgnoreCase));
        if (material == null || string.IsNullOrEmpty(resourceName))
        {
            failure = "material-or-record-missing";
            return false;
        }
        try
        {
            using Stream stream = typeof(OperatorKillHousePlugin).Assembly.GetManifestResourceStream(resourceName);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (material.renderQueue != root.GetProperty("m_CustomRenderQueue").GetInt32())
            {
                failure = "render-queue";
                return false;
            }
            var expectedKeywords = new HashSet<string>(root.GetProperty("m_ValidKeywords").EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
            if (!expectedKeywords.SetEquals(material.shaderKeywords))
            {
                failure = "keywords:expected=" + string.Join("/", expectedKeywords.OrderBy(value => value)) +
                          "/actual=" + string.Join("/", material.shaderKeywords.OrderBy(value => value));
                return false;
            }
            var expectedDisabledPasses = new HashSet<string>(
                root.GetProperty("m_DisabledShaderPasses").EnumerateArray()
                    .Select(value => value.GetString()), StringComparer.Ordinal);
            foreach (string passName in ExactDonorShaderPasses)
            {
                bool disabled = !material.GetShaderPassEnabled(passName);
                if (disabled == expectedDisabledPasses.Contains(passName)) continue;
                failure = "shader-pass:" + passName + "/expectedDisabled=" +
                          expectedDisabledPasses.Contains(passName) + "/actualDisabled=" + disabled;
                return false;
            }
            var expectedTags = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.GetProperty("m_StringTagMap").EnumerateObject())
                expectedTags[property.Name] = property.Value.GetString() ?? string.Empty;
            foreach (string tagName in ExactDonorOverrideTags)
            {
                string expectedTag = expectedTags.TryGetValue(tagName, out string value) ? value : string.Empty;
                string actualTag = material.GetTag(tagName, false, string.Empty);
                if (string.Equals(actualTag, expectedTag, StringComparison.Ordinal)) continue;
                failure = "tag:" + tagName + "/expected=" + expectedTag + "/actual=" + actualTag;
                return false;
            }
            if ((int)material.globalIlluminationFlags != root.GetProperty("m_LightmapFlags").GetInt32() ||
                material.enableInstancing != root.GetProperty("m_EnableInstancingVariants").GetBoolean() ||
                material.doubleSidedGI != root.GetProperty("m_DoubleSidedGI").GetBoolean())
            {
                failure = "material-render-flags";
                return false;
            }
            JsonElement saved = root.GetProperty("m_SavedProperties");
            int checkedFloats = 0;
            foreach (JsonProperty property in saved.GetProperty("m_Floats").EnumerateObject())
            {
                if (!material.HasProperty(property.Name)) continue;
                checkedFloats++;
                if (Mathf.Abs(material.GetFloat(property.Name) - property.Value.GetSingle()) <= .001f) continue;
                failure = "float:" + property.Name;
                return false;
            }
            int checkedColors = 0;
            foreach (JsonProperty property in saved.GetProperty("m_Colors").EnumerateObject())
            {
                if (!material.HasProperty(property.Name)) continue;
                checkedColors++;
                JsonElement value = property.Value;
                Color expected = new Color(value.GetProperty("m_R").GetSingle(),
                    value.GetProperty("m_G").GetSingle(), value.GetProperty("m_B").GetSingle(),
                    value.GetProperty("m_A").GetSingle());
                if (ColorApproximately(material.GetColor(property.Name), expected, .001f)) continue;
                failure = "color:" + property.Name;
                return false;
            }
            int checkedTransforms = 0;
            foreach (JsonProperty property in saved.GetProperty("m_TexEnvs").EnumerateObject())
            {
                if (!material.HasProperty(property.Name)) continue;
                checkedTransforms++;
                JsonElement scale = property.Value.GetProperty("m_Scale");
                JsonElement offset = property.Value.GetProperty("m_Offset");
                Vector2 expectedScale = new Vector2(scale.GetProperty("m_X").GetSingle(),
                    scale.GetProperty("m_Y").GetSingle());
                Vector2 expectedOffset = new Vector2(offset.GetProperty("m_X").GetSingle(),
                    offset.GetProperty("m_Y").GetSingle());
                if (Vector2.Distance(material.GetTextureScale(property.Name), expectedScale) <= .001f &&
                    Vector2.Distance(material.GetTextureOffset(property.Name), expectedOffset) <= .001f) continue;
                failure = "texture-transform:" + property.Name;
                return false;
            }
            if (checkedFloats == 0 || checkedColors == 0 || checkedTransforms == 0)
            {
                failure = "empty-validated-state:" + checkedFloats + "/" + checkedColors + "/" + checkedTransforms;
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name + ":" + exception.Message;
            return false;
        }
    }

    private static void ValidateResidentHdrpMaterial(Material material)
    {
        Type type = FindManagedType("UnityEngine.Rendering.HighDefinition.HDMaterial");
        MethodInfo method = type?.GetMethod("ValidateMaterial", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(Material) }, null);
        method?.Invoke(null, new object[] { material });
    }

    private static bool MaterialHasResidentContract(Material material, string residentShaderName)
    {
        return material != null && material.shader != null &&
               string.Equals(material.shader.name, residentShaderName, StringComparison.Ordinal) &&
               material.renderQueue == NativeOpaqueRenderQueue && GetTexture(material, "_BaseColorMap") != null &&
               (!material.HasProperty("_SurfaceType") || Mathf.Approximately(material.GetFloat("_SurfaceType"), 0f)) &&
               (!material.HasProperty("_ZWrite") || material.GetFloat("_ZWrite") >= .99f);
    }

    private static bool MaterialHasResidentContract(Material material, NativeMaterialProfile profile)
    {
        if (profile == null || !MaterialHasResidentContract(material, profile.ResidentShaderName)) return false;
        Color baseColor = GetColor(material, "_BaseColor", Color.clear);
        bool exactCore = Mathf.Abs(baseColor.r - profile.BaseColor.r) <= .001f &&
                         Mathf.Abs(baseColor.g - profile.BaseColor.g) <= .001f &&
                         Mathf.Abs(baseColor.b - profile.BaseColor.b) <= .001f &&
                         (!material.HasProperty("_Metallic") ||
                          Mathf.Abs(material.GetFloat("_Metallic") - profile.Metallic) <= .001f) &&
                         (!material.HasProperty("_Smoothness") ||
                          Mathf.Abs(material.GetFloat("_Smoothness") - profile.Smoothness) <= .001f) &&
                         (!material.HasProperty("_NormalScale") ||
                          Mathf.Abs(material.GetFloat("_NormalScale") - profile.NormalScale) <= .001f);
        if (!exactCore) return false;
        if (!profile.MatteArchitectural) return true;
        return (!material.HasProperty("_Metallic") || material.GetFloat("_Metallic") <= profile.Metallic + .001f) &&
               (!material.HasProperty("_MetallicRemapMax") ||
                material.GetFloat("_MetallicRemapMax") <= profile.Metallic + .001f) &&
               (!material.HasProperty("_SmoothnessRemapMax") ||
                material.GetFloat("_SmoothnessRemapMax") <= profile.Smoothness + .001f) &&
               (!material.HasProperty("_ReceivesSSR") || material.GetFloat("_ReceivesSSR") <= .001f) &&
               (!material.HasProperty("_EnvironmentReflections") ||
                material.GetFloat("_EnvironmentReflections") <= .001f);
    }

    private static bool MaterialHasResidentProfileContract(Material material)
    {
        if (material == null) return false;
        const string prefix = "RUNTIME_NATIVE_";
        string profileName = material.name != null && material.name.StartsWith(prefix, StringComparison.Ordinal)
            ? material.name.Substring(prefix.Length)
            : string.Empty;
        return NativeMaterialProfiles.TryGetValue(profileName, out NativeMaterialProfile profile) &&
               MaterialHasResidentContract(material, profile) &&
               FurnitureSurfaceContractValid(material, profileName) &&
               MaterialHasExactTextureClosure(material, profileName);
    }

    private static bool FurnitureSurfaceContractValid(Material material, string profileName)
    {
        if (!FurnitureSurfaceProfiles.TryGetValue(profileName, out FurnitureSurfaceProfile expected)) return true;
        return FloatPropertyMatches(material, "_MetallicRemapMin", 0f) &&
               FloatPropertyMatches(material, "_MetallicRemapMax", expected.MetallicRemapMax) &&
               FloatPropertyMatches(material, "_SmoothnessRemapMin", 0f) &&
               FloatPropertyMatches(material, "_SmoothnessRemapMax", expected.SmoothnessRemapMax) &&
               FloatPropertyMatches(material, "_AORemapMin", expected.AoRemapMin) &&
               FloatPropertyMatches(material, "_AORemapMax", expected.AoRemapMax) &&
               OptionalFloatPropertyMatches(material, "_OcclusionStrength", expected.OcclusionStrength) &&
               FloatPropertyMatches(material, "_ReceivesSSR", expected.ReceivesSsr) &&
               FloatPropertyMatches(material, "_MaterialID", 1f) &&
               FloatPropertyMatches(material, "_TransmissionEnable", 1f) &&
               OptionalFloatPropertyMatches(material, "_TransmissionMask", 1f);
    }

    private static bool FloatPropertyMatches(Material material, string property, float expected)
    {
        return material != null && material.HasProperty(property) &&
               Mathf.Abs(material.GetFloat(property) - expected) <= .001f;
    }

    private static bool OptionalFloatPropertyMatches(Material material, string property, float expected)
    {
        return material != null && (!material.HasProperty(property) ||
               Mathf.Abs(material.GetFloat(property) - expected) <= .001f);
    }

    private static bool MaterialHasExactTextureClosure(Material material, string profileName)
    {
        if (material == null || !ExpectedBaseTextureNames.TryGetValue(profileName, out string expectedBase) ||
            !TextureNameEquals(GetTexture(material, "_BaseColorMap"), expectedBase)) return false;
        if (ExpectedNormalTextureNames.TryGetValue(profileName, out string expectedNormal) &&
            !TextureNameEquals(GetTexture(material, "_NormalMap"), expectedNormal)) return false;
        if (ExpectedMaskTextureNames.TryGetValue(profileName, out string expectedMask) &&
            !TextureNameEquals(GetTexture(material, "_MaskMap"), expectedMask)) return false;
        if (ExpectedEmissiveTextureNames.TryGetValue(profileName, out string expectedEmissive) &&
            !TextureNameEquals(GetTexture(material, "_EmissiveColorMap"), expectedEmissive)) return false;
        if (ExpectedDetailTextureNames.TryGetValue(profileName, out string expectedDetail) &&
            !TextureNameEquals(GetTexture(material, "_DetailMap"), expectedDetail)) return false;
        if ((FurnitureMaterialProfileNames.Contains(profileName) ||
             string.Equals(profileName, "Floor", StringComparison.Ordinal)) &&
            !EmbeddedExactDonorStateValid(material, profileName, out _)) return false;
        if (FurnitureMaterialProfileNames.Contains(profileName) &&
            (!TextureTransformIsDefault(material, "_BaseColorMap") ||
             (ExpectedNormalTextureNames.ContainsKey(profileName) &&
              !TextureTransformIsDefault(material, "_NormalMap")) ||
             (ExpectedMaskTextureNames.ContainsKey(profileName) &&
              !TextureTransformIsDefault(material, "_MaskMap")) ||
             (ExpectedEmissiveTextureNames.ContainsKey(profileName) &&
              !TextureTransformIsDefault(material, "_EmissiveColorMap")))) return false;
        if (FurnitureMaterialProfileNames.Contains(profileName) &&
            !FurnitureKeywordContractValid(material, profileName)) return false;
        if (string.Equals(profileName, "Devices_On", StringComparison.Ordinal))
        {
            Color emission = GetColor(material, "_EmissiveColor", Color.black);
            if (!material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") ||
                Mathf.Abs(emission.r - 1.720795f) > .001f ||
                Mathf.Abs(emission.g - 1.720795f) > .001f ||
                Mathf.Abs(emission.b - 1.720795f) > .001f) return false;
        }
        if (string.Equals(profileName, "Kitchen_Cabinet_Marble", StringComparison.Ordinal) &&
            (material.IsKeywordEnabled("_NORMALMAP") ||
             !material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE"))) return false;
        if (string.Equals(profileName, "Kitchen_TableChair", StringComparison.Ordinal) &&
            (!string.Equals(material.shader?.name, MilkLitTemplateShaderName, StringComparison.Ordinal) ||
             !material.IsKeywordEnabled("_DISABLE_SSR") ||
             !material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") ||
             material.IsKeywordEnabled("_MASKMAP") || material.IsKeywordEnabled("_NORMALMAP") ||
             material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") ||
             (material.HasProperty("_MaterialTypeMask") &&
              Mathf.Abs(material.GetFloat("_MaterialTypeMask") - 2f) > .001f) ||
              (material.HasProperty("_saturation") &&
               Mathf.Abs(material.GetFloat("_saturation") - 1f) > .001f))) return false;
        if (string.Equals(profileName, "Couch_Fabric", StringComparison.Ordinal) &&
            (!string.Equals(material.shader?.name, MilkLitTemplateShaderName, StringComparison.Ordinal) ||
             !material.IsKeywordEnabled("_DETAIL_TRIPLANAR_UV") ||
             !material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") ||
             material.IsKeywordEnabled("_DISABLE_SSR") || material.IsKeywordEnabled("_MASKMAP") ||
             material.IsKeywordEnabled("_NORMALMAP") ||
             material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") ||
             (material.HasProperty("_MaterialTypeMask") &&
              Mathf.Abs(material.GetFloat("_MaterialTypeMask") - 2f) > .001f) ||
             (material.HasProperty("_saturation") &&
              Mathf.Abs(material.GetFloat("_saturation") - 1f) > .001f))) return false;
        if (string.Equals(profileName, "In_Floor_Basement", StringComparison.Ordinal) &&
            (!string.Equals(material.shader?.name, MilkLitTemplateShaderName, StringComparison.Ordinal) ||
             !TextureTransformMatches(material, "_DetailMap", new Vector2(5f, 5f), Vector2.zero) ||
             !material.IsKeywordEnabled("_DETAIL_MAP") ||
             !material.IsKeywordEnabled("_DETAIL_TRIPLANAR_UV") ||
             !material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") ||
             material.IsKeywordEnabled("_DISABLE_SSR") || material.IsKeywordEnabled("_MASKMAP") ||
             material.IsKeywordEnabled("_NORMALMAP") ||
              material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE"))) return false;
        if (string.Equals(profileName, "Floor", StringComparison.Ordinal) &&
            (!string.Equals(material.shader?.name, ResidentShaderName, StringComparison.Ordinal) ||
             !material.IsKeywordEnabled("_MAPPING_TRIPLANAR") ||
             !material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") ||
             !material.IsKeywordEnabled("_MASKMAP") || !material.IsKeywordEnabled("_NORMALMAP") ||
             !material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") ||
             !FloatPropertyMatches(material, "_UVBase", 5f) ||
             !FloatPropertyMatches(material, "_TexWorldScale", .25f))) return false;
        return true;
    }

    private static bool FurnitureKeywordContractValid(Material material, string profileName)
    {
        if (material == null) return false;
        if (string.Equals(profileName, "Kitchen_TableChair", StringComparison.Ordinal))
        {
            return material.IsKeywordEnabled("_DISABLE_SSR") &&
                   material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") &&
                   !material.IsKeywordEnabled("_MASKMAP") &&
                   !material.IsKeywordEnabled("_NORMALMAP") &&
                   !material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") &&
                   !material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") &&
                   !material.IsKeywordEnabled("_EMISSION");
        }
        if (string.Equals(profileName, "Couch_Fabric", StringComparison.Ordinal))
        {
            return !material.IsKeywordEnabled("_DISABLE_SSR") &&
                   material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") &&
                   material.IsKeywordEnabled("_DETAIL_TRIPLANAR_UV") &&
                   !material.IsKeywordEnabled("_MASKMAP") &&
                   !material.IsKeywordEnabled("_NORMALMAP") &&
                   !material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") &&
                   !material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") &&
                   !material.IsKeywordEnabled("_EMISSION");
        }

        bool expectsNormal = ExpectedNormalTextureNames.ContainsKey(profileName);
        bool expectsEmissive = string.Equals(profileName, "Devices_On", StringComparison.Ordinal);
        return !material.IsKeywordEnabled("_DISABLE_SSR") &&
               material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") &&
               material.IsKeywordEnabled("_MASKMAP") &&
               material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") &&
               material.IsKeywordEnabled("_NORMALMAP") == expectsNormal &&
               material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") == expectsEmissive &&
               !material.IsKeywordEnabled("_EMISSION");
    }

    private static bool TextureNameMatches(Texture texture, IReadOnlyDictionary<string, string> expectedNames,
        string profileName, bool required)
    {
        if (!required) return texture == null || !expectedNames.ContainsKey(profileName);
        return expectedNames.TryGetValue(profileName, out string expected) && TextureNameEquals(texture, expected);
    }

    private static bool TextureNameEquals(Texture texture, string expected)
    {
        return texture != null && string.Equals(texture.name, expected, StringComparison.Ordinal);
    }

    private static bool ColorApproximately(Color actual, Color expected, float tolerance)
    {
        return Mathf.Abs(actual.r - expected.r) <= tolerance &&
               Mathf.Abs(actual.g - expected.g) <= tolerance &&
               Mathf.Abs(actual.b - expected.b) <= tolerance &&
               Mathf.Abs(actual.a - expected.a) <= tolerance;
    }

    private static bool TextureTransformIsDefault(Material material, string property)
    {
        return material != null && material.HasProperty(property) &&
               Vector2.Distance(material.GetTextureScale(property), Vector2.one) <= .001f &&
               Vector2.Distance(material.GetTextureOffset(property), Vector2.zero) <= .001f;
    }

    private static bool TextureTransformMatches(Material material, string property, Vector2 scale, Vector2 offset)
    {
        return material != null && material.HasProperty(property) &&
               Vector2.Distance(material.GetTextureScale(property), scale) <= .001f &&
               Vector2.Distance(material.GetTextureOffset(property), offset) <= .001f;
    }

    private static string DescribeMaterialContract(Material material)
    {
        if (material == null) return "material=<null>";
        return "shader=" + (material.shader == null ? "<null>" : material.shader.name) +
               ", queue=" + material.renderQueue +
               ", expectedQueue=" + NativeOpaqueRenderQueue +
               ", baseMap=" + (GetTexture(material, "_BaseColorMap") != null) +
               ", surfaceType=" + (material.HasProperty("_SurfaceType") ?
                   material.GetFloat("_SurfaceType").ToString("F3", CultureInfo.InvariantCulture) : "<absent>") +
               ", zWrite=" + (material.HasProperty("_ZWrite") ?
                   material.GetFloat("_ZWrite").ToString("F3", CultureInfo.InvariantCulture) : "<absent>");
    }

    private void ProbeEquippedWeaponIdentity()
    {
        try
        {
            bool hasEquippedWeapon = TryBuildEquippedWeaponIdentityFingerprint(out ulong fingerprint);
            if (equippedWeaponIdentityInitialized && fingerprint == equippedWeaponIdentityFingerprint) return;

            bool hadPriorIdentity = equippedWeaponIdentityInitialized && equippedWeaponIdentityFingerprint != 0;
            if (hadPriorIdentity) RestoreWeaponIlluminationBoosts();
            equippedWeaponIdentityFingerprint = fingerprint;
            equippedWeaponIdentityInitialized = true;
            lastOpticAuditSignature = string.Empty;
            if (!hasEquippedWeapon)
            {
                opticAuditPending = false;
                nextOpticAuditFrame = -1;
                return;
            }
            opticAuditPending = true;
            nextOpticAuditFrame = Time.frameCount + 10;
            log.LogInfo("Vektor Kill House equipped-weapon identity changed; one-shot optic audit rearmed: " +
                        fingerprint.ToString("X16", CultureInfo.InvariantCulture) + ".");
        }
        catch (Exception exception)
        {
            string signature = exception.GetType().Name + ":" + exception.Message;
            if (!string.Equals(signature, lastOpticAuditSignature, StringComparison.Ordinal))
            {
                lastOpticAuditSignature = signature;
                log.LogWarning("Vektor Kill House equipped-weapon identity probe deferred: " + signature + ".");
            }
        }
    }

    private static bool TryBuildEquippedWeaponIdentityFingerprint(out ulong fingerprint)
    {
        fingerprint = 0;
        if (!TryGetActiveEquippedWeapon(out PlayerNetworking player, out GameObject activeRoot,
                out WeaponV3 weapon))
            return false;

        ulong value = 1469598103934665603UL;
        value = MixWeaponIdentity(value, player.GetInstanceID());
        value = MixWeaponIdentity(value, activeRoot.GetInstanceID());
        value = MixWeaponIdentity(value, weapon.GetInstanceID());
        value = MixWeaponIdentity(value, player.CurrentWeaponSlot.GetHashCode());
        value = MixWeaponIdentity(value, weapon.laserIndex);
        value = MixWeaponIdentity(value, weapon.flashlightIndex);
        Transform rootTransform = activeRoot.transform;
        value = MixWeaponIdentity(value, rootTransform == null ? 0 : rootTransform.childCount);
        if (rootTransform != null)
        {
            for (int index = 0; index < rootTransform.childCount; index++)
            {
                Transform child = rootTransform.GetChild(index);
                value = MixWeaponIdentity(value, child == null ? 0 : child.GetInstanceID());
                value = MixWeaponIdentity(value, child == null ? 0 : child.childCount);
                value = MixWeaponIdentity(value, child != null && child.gameObject.activeSelf ? 1 : 0);
            }
        }
        value = MixWeaponIdentity(value, weapon.Lasers == null ? 0 : weapon.Lasers.Count);
        if (weapon.Lasers != null)
            for (int index = 0; index < weapon.Lasers.Count; index++)
                value = MixWeaponIdentity(value,
                    weapon.Lasers[index] == null ? 0 : weapon.Lasers[index].GetInstanceID());
        value = MixWeaponIdentity(value, weapon.Flashlights == null ? 0 : weapon.Flashlights.Count);
        if (weapon.Flashlights != null)
            for (int index = 0; index < weapon.Flashlights.Count; index++)
                value = MixWeaponIdentity(value,
                    weapon.Flashlights[index] == null ? 0 : weapon.Flashlights[index].GetInstanceID());
        fingerprint = value == 0 ? 1UL : value;
        return true;
    }

    private static ulong MixWeaponIdentity(ulong fingerprint, int value)
    {
        unchecked
        {
            fingerprint ^= (uint)value;
            return fingerprint * 1099511628211UL;
        }
    }

    private static bool TryGetActiveEquippedWeapon(out PlayerNetworking player,
        out GameObject activeRoot, out WeaponV3 weapon)
    {
        player = GameManager.instance == null ? null : GameManager.myPlayerNetworking;
        activeRoot = player == null ? null : player.activeWeapon;
        weapon = activeRoot == null ? null : activeRoot.GetComponent<WeaponV3>();
        return player != null && activeRoot != null && activeRoot.scene.isLoaded &&
               weapon != null && weapon.gameObject.scene.isLoaded && weapon.isEquiped;
    }

    private bool AuditLiveWeaponIllumination()
    {
        try
        {
            if (!TryGetActiveEquippedWeapon(out PlayerNetworking _, out GameObject activeRoot,
                    out WeaponV3 activeWeapon)) return false;
            EnhanceLiveWeaponIllumination(activeRoot, activeWeapon);
            var details = new List<string>();
            int hwsCount = 0;
            int reflexCount = 0;
            int irCount = 0;
            int weaponCount = 0;

            HWSReticleBrightness[] hwsObjects =
                activeRoot.GetComponentsInChildren<HWSReticleBrightness>(true);
            foreach (HWSReticleBrightness reticle in hwsObjects)
            {
                if (reticle == null || !reticle.gameObject.scene.isLoaded) continue;
                hwsCount++;
                if (details.Count < 3)
                {
                    Material material = reticle.ReticleRenderer == null ? null : reticle.ReticleRenderer.sharedMaterial;
                    int selectedIndex = reticle.reticleSettings == null || reticle.reticleSettings.Length == 0 ? -1 :
                        Mathf.Clamp(reticle.CurrentBrightnessSetting, 0, reticle.reticleSettings.Length - 1);
                    ReticleSetting selected = selectedIndex < 0 ? null : reticle.reticleSettings[selectedIndex];
                    Material nvgMaterial = reticle.ReticleRendererNVG == null ? null :
                        reticle.ReticleRendererNVG.sharedMaterial;
                    details.Add("HWS{" + reticle.name + ",current=" + reticle.CurrentBrightnessSetting +
                                ",default=" + reticle.DefaultBrightnessSetting + ",settings=" +
                                (reticle.reticleSettings == null ? 0 : reticle.reticleSettings.Length) +
                                ",selected=" + (selected == null ? "missing" :
                                    selected.ReticleBrightness.ToString("F3", CultureInfo.InvariantCulture) + "/" +
                                    selected.ReticleBrightness_NVG.ToString("F3", CultureInfo.InvariantCulture)) +
                                ",sizeNormalNvg=" + DescribeHwsReticleSize(material) + "/" +
                                DescribeHwsReticleSize(nvgMaterial) +
                                ",material=" + DescribeEmission(material) + "}");
                }
            }

            ReflexSightV2[] reflexObjects = activeRoot.GetComponentsInChildren<ReflexSightV2>(true);
            foreach (ReflexSightV2 reflex in reflexObjects)
            {
                if (reflex == null || !reflex.gameObject.scene.isLoaded) continue;
                reflexCount++;
                if (details.Count < 4)
                {
                    ReticleIllumnation illumination = reflex.reticleIllumnation;
                    details.Add("REFLEX{" + reflex.name + ",illumination=" +
                                (illumination == null ? "missing" :
                                    illumination.currentIllumnation.ToString("F3", CultureInfo.InvariantCulture) + "/" +
                                    illumination.MinIllumnation.ToString("F3", CultureInfo.InvariantCulture) + "-" +
                                    illumination.MaxIllumnation.ToString("F3", CultureInfo.InvariantCulture) +
                                    ",steps=" + illumination.illumnationSteps.ToString("F3", CultureInfo.InvariantCulture)) +
                                ",material=" + DescribeEmission(reflex.ReticleMaterial) + "}");
                }
            }

            IRLaserLight[] irObjects = activeRoot.GetComponentsInChildren<IRLaserLight>(true);
            foreach (IRLaserLight ir in irObjects)
            {
                if (ir == null || !ir.gameObject.scene.isLoaded) continue;
                irCount++;
                if (details.Count < 5)
                    details.Add("IR{" + ir.name + ",light=" + (ir._light != null && ir._light.enabled) +
                                ",lumens=" + (ir._lightData == null ? "missing" :
                                    ir._lightData.intensity.ToString("F2", CultureInfo.InvariantCulture)) +
                                ",line=" + (ir.lineRenderer != null && ir.lineRenderer.enabled) +
                                ",range=" + ir.range.ToString("F2", CultureInfo.InvariantCulture) + "}");
            }

            foreach (WeaponV3 weapon in new[] { activeWeapon })
            {
                if (weapon == null || !weapon.gameObject.scene.isLoaded || !weapon.isEquiped) continue;
                weaponCount++;
                if (details.Count < 6)
                {
                    var flashlightStates = new List<string>();
                    if (weapon.Flashlights != null)
                    {
                        for (int index = 0; index < weapon.Flashlights.Count; index++)
                        {
                            GameObject flashlight = weapon.Flashlights[index];
                            if (flashlight == null) continue;
                            HDAdditionalLightData[] lightData = flashlight.GetComponentsInChildren<HDAdditionalLightData>(true);
                            flashlightStates.Add(index + ":" + flashlight.activeSelf + "/" + flashlight.activeInHierarchy +
                                                 "/" + string.Join(",", lightData.Select(data =>
                                                     data.intensity.ToString("F1", CultureInfo.InvariantCulture))));
                        }
                    }
                    var laserStates = new List<string>();
                    if (weapon.Lasers != null)
                    {
                        for (int index = 0; index < weapon.Lasers.Count; index++)
                        {
                            GameObject laser = weapon.Lasers[index];
                            if (laser == null) continue;
                            HDAdditionalLightData[] lightData = laser.GetComponentsInChildren<HDAdditionalLightData>(true);
                            Light[] unityLights = laser.GetComponentsInChildren<Light>(true);
                            LineRenderer[] lines = laser.GetComponentsInChildren<LineRenderer>(true);
                            string[] controllerTypes = laser.GetComponentsInChildren<Component>(true)
                                .Select(component => component == null ? string.Empty : component.GetType().Name)
                                .Where(type => type.IndexOf("Laser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               type.IndexOf("Beam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               type.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0)
                                .Distinct().Take(12).ToArray();
                            laserStates.Add(index + ":" + laser.activeSelf + "/" + laser.activeInHierarchy +
                                            "/lights=" + string.Join(",", lightData.Select(data =>
                                                data.intensity.ToString("F1", CultureInfo.InvariantCulture))) +
                                            "/unityLights=" + string.Join(",", unityLights.Select(light =>
                                                light.name + ":" + light.enabled + "/" + light.gameObject.activeInHierarchy +
                                                "/i=" + light.intensity.ToString("F1", CultureInfo.InvariantCulture) +
                                                "/r=" + light.range.ToString("F1", CultureInfo.InvariantCulture) +
                                                "/p=" + light.transform.position.ToString("F2") +
                                                "/layer=" + light.gameObject.layer)) +
                                            "/lines=" + string.Join(",", lines.Select(line =>
                                                line.enabled + ":" + DescribeEmission(line.sharedMaterial))) +
                                            "/controllers=" + string.Join(",", controllerTypes));
                        }
                    }
                    details.Add("WEAPON{" + weapon.displayName + ",flashlights=" +
                                (weapon.Flashlights == null ? 0 : weapon.Flashlights.Count) + ",flashlightIndex=" +
                                weapon.flashlightIndex + ",lasers=" + (weapon.Lasers == null ? 0 : weapon.Lasers.Count) +
                                ",laserIndex=" + weapon.laserIndex + ",flashlightStates=" +
                                string.Join(";", flashlightStates) + ",laserStates=" + string.Join(";", laserStates) + "}");
                }
            }

            string signature = "hws=" + hwsCount + ",reflex=" + reflexCount + ",ir=" + irCount +
                               ",weapons=" + weaponCount + ",globalMultiplier=" +
                               (globalFlashlightMultiplier == null ? "missing" :
                                   globalFlashlightMultiplier.MultiplierValue.ToString("F2", CultureInfo.InvariantCulture)) +
                               ",boosts=reticleNormal:" +
                               IndoorReticleNormalBrightnessMultiplier.ToString("F1", CultureInfo.InvariantCulture) +
                               "/reticleNvg:" +
                               IndoorReticleNvgBrightnessMultiplier.ToString("F1", CultureInfo.InvariantCulture) +
                               "/reticleNormalSize:" +
                               IndoorReticleNormalSizeMultiplier.ToString("F1", CultureInfo.InvariantCulture) +
                               "/visibleLaser:" + IndoorVisibleLaserMultiplier.ToString("F1", CultureInfo.InvariantCulture) +
                               "/beamEmission:" + IndoorVisibleLaserBeamEmissionMultiplier.ToString("F1", CultureInfo.InvariantCulture) +
                               ",enhanced=hws:" + enhancedHwsReticles.Count + "/hwsSize:" +
                               enhancedHwsReticleSizeRenderers.Count + "/reflex:stock" +
                               "/laserControllers:" + enhancedLaserLights.Count + "/laserLights:" +
                               enhancedVisibleLaserLights.Count + "/laserBeams:" + boostedLaserBeamStates.Count +
                               ",baselines=hws:" + hwsReticleBoostStates.Count + "/hwsSize:" +
                               hwsReticleSizeStates.Count + "/laserControllers:" +
                               visibleIrLaserBoostStates.Count + "/laserLights:" + visibleLaserLightBoostStates.Count +
                               ",details=[" + string.Join(" | ", details) + "]";
            if (string.Equals(signature, lastOpticAuditSignature, StringComparison.Ordinal)) return true;
            lastOpticAuditSignature = signature;
            log.LogInfo("Vektor Kill House live optic/illuminator audit: " + signature + ".");
            return true;
        }
        catch (Exception exception)
        {
            string signature = exception.GetType().Name + ":" + exception.Message;
            if (string.Equals(signature, lastOpticAuditSignature, StringComparison.Ordinal)) return false;
            lastOpticAuditSignature = signature;
            log.LogWarning("Vektor Kill House live optic/illuminator audit deferred: " + signature + ".");
            return false;
        }
    }

    private void EnhanceLiveWeaponIllumination(GameObject activeRoot, WeaponV3 activeWeapon)
    {
        RestoreStaleHwsReticleBoosts();
        HWSReticleBrightness[] hwsObjects = activeRoot.GetComponentsInChildren<HWSReticleBrightness>(true);
        foreach (HWSReticleBrightness reticle in hwsObjects)
        {
            if (reticle == null || !reticle.gameObject.scene.isLoaded || reticle.reticleSettings == null ||
                reticle.reticleSettings.Length == 0 || !ReticleBelongsToEquippedWeapon(reticle) ||
                enhancedHwsReticles.Contains(reticle.GetInstanceID())) continue;
            var baseline = new HwsReticleBoostState(reticle);
            if (!baseline.HasRecognizedVanillaRange) continue;
            enhancedHwsReticles.Add(reticle.GetInstanceID());
            hwsReticleBoostStates.Add(baseline);
            for (int index = 0; index < reticle.reticleSettings.Length; index++)
            {
                ReticleSetting setting = reticle.reticleSettings[index];
                if (setting == null) continue;
                setting.ReticleBrightness = Mathf.Min(
                    baseline.Normal[index] * IndoorReticleNormalBrightnessMultiplier,
                    IndoorReticleNormalBrightnessCap);
                setting.ReticleBrightness_NVG = Mathf.Min(
                    baseline.Nvg[index] * IndoorReticleNvgBrightnessMultiplier,
                    IndoorReticleNvgBrightnessCap);
            }
            TryEnhanceHwsReticleNormalSize(reticle, reticle.ReticleRenderer);
            // Ultimate Scope Shaders is the exact vanilla HWS shader. Reapply the already-scaled
            // current setting through its real normal/NVG properties so the initial dot and arrow input agree.
            ApplyHwsSelectedBrightness(reticle);
        }

        // Only inspect descendants of the currently equipped weapon. A process-global IRLaserLight
        // search also reaches layer-16 IR emitters and was the source of the old compounded restart bug.
        foreach (WeaponV3 weapon in new[] { activeWeapon })
        {
            if (weapon == null || !weapon.gameObject.scene.isLoaded || !weapon.isEquiped || weapon.Lasers == null)
                continue;
            for (int index = 0; index < weapon.Lasers.Count; index++)
            {
                GameObject laser = weapon.Lasers[index];
                if (laser == null) continue;

                IRLaserLight[] controllers = laser.GetComponentsInChildren<IRLaserLight>(true);
                var controllerOwnedLightIds = new HashSet<int>(controllers
                    .Where(controller => controller != null && controller._light != null)
                    .Select(controller => controller._light.GetInstanceID()));
                bool hasVisibleController = controllers.Any(controller => controller != null && !controller.IRonly &&
                    controller.gameObject.layer != 16 &&
                    ((controller._light != null && controller._light.gameObject.layer == 0) ||
                     (controller._lightData != null && controller._lightData.gameObject.layer == 0)));
                bool irOnlyBranch = controllers.Length > 0 && !hasVisibleController;
                foreach (IRLaserLight controller in controllers)
                {
                    if (controller == null || controller.IRonly || controller.gameObject.layer == 16) continue;
                    GameObject emitterObject = controller._light != null ? controller._light.gameObject :
                        controller._lightData == null ? null : controller._lightData.gameObject;
                    if (emitterObject == null || emitterObject.layer != 0 ||
                        !enhancedLaserLights.Add(controller.GetInstanceID())) continue;
                    var state = new VisibleIrLaserBoostState(controller);
                    visibleIrLaserBoostStates.Add(state);
                    controller.minBrighness = state.MinBrightness * IndoorVisibleLaserMultiplier;
                    controller.maxBrighness = state.MaxBrightness * IndoorVisibleLaserMultiplier;
                    if (state.LightData != null)
                        state.LightData.intensity = Mathf.Max(
                            state.LightDataIntensity * IndoorVisibleLaserMultiplier, controller.minBrighness);
                    if (state.Light != null)
                        state.Light.intensity = Mathf.Max(
                            state.LightIntensity * IndoorVisibleLaserMultiplier, controller.minBrighness);
                }

                Light[] lights = laser.GetComponentsInChildren<Light>(true);
                foreach (Light light in lights)
                {
                    // Controller-owned lights were handled from the same baseline above. Layer 16 is
                    // OPERATOR's IR path and must remain untouched by a visible-light request.
                    if (light == null || light.gameObject.layer != 0 ||
                        controllerOwnedLightIds.Contains(light.GetInstanceID()) ||
                        light.GetComponentInParent<IRLaserLight>() != null ||
                        !enhancedVisibleLaserLights.Add(light.GetInstanceID())) continue;
                    HDAdditionalLightData data = light.GetComponent<HDAdditionalLightData>();
                    var state = new VisibleLaserLightBoostState(light, data);
                    visibleLaserLightBoostStates.Add(state);
                    if (data != null)
                        data.intensity = state.LightDataIntensity * IndoorVisibleLaserMultiplier;
                    light.intensity = state.LightIntensity * IndoorVisibleLaserMultiplier;
                }

                LineRenderer[] lines = laser.GetComponentsInChildren<LineRenderer>(true);
                foreach (LineRenderer line in lines)
                {
                    if (line == null || irOnlyBranch || line.gameObject.layer == 16) continue;
                    IRLaserLight controller = line.GetComponentInParent<IRLaserLight>();
                    if ((controller != null && controller.IRonly) ||
                        !enhancedVisibleLaserBeams.Add(line.GetInstanceID())) continue;
                    Material source = line.sharedMaterial;
                    if (source == null) continue;
                    Material boosted = new Material(source)
                    {
                        name = source.name + "_KH_BRIGHT_BEAM",
                        hideFlags = HideFlags.DontSave
                    };
                    BoostHdrColor(boosted, "_EmissiveColor", IndoorVisibleLaserBeamEmissionMultiplier);
                    BoostHdrColor(boosted, "_EmissionColor", IndoorVisibleLaserBeamEmissionMultiplier);
                    BoostHdrColor(boosted, "_UnlitColor", IndoorVisibleLaserBeamEmissionMultiplier);
                    BoostHdrColor(boosted, "_Color", IndoorVisibleLaserBeamEmissionMultiplier);
                    if (boosted.HasProperty("_UseEmissiveIntensity") &&
                        boosted.GetFloat("_UseEmissiveIntensity") >= .5f &&
                        boosted.HasProperty("_EmissiveIntensity"))
                        boosted.SetFloat("_EmissiveIntensity",
                            boosted.GetFloat("_EmissiveIntensity") * IndoorVisibleLaserBeamEmissionMultiplier);
                    line.sharedMaterial = boosted;
                    boostedLaserBeamStates.Add(new BoostedLaserBeamState(line, source, boosted));
                }
            }
        }
    }

    private static bool ReticleBelongsToEquippedWeapon(HWSReticleBrightness reticle)
    {
        if (reticle == null || !reticle.gameObject.scene.isLoaded) return false;
        WeaponV3 weapon = reticle.GetComponentInParent<WeaponV3>();
        return weapon != null && weapon.gameObject.scene.isLoaded && weapon.isEquiped;
    }

    private void TryEnhanceHwsReticleNormalSize(HWSReticleBrightness owner, Renderer renderer)
    {
        if (owner == null || renderer == null ||
            enhancedHwsReticleSizeRenderers.Contains(renderer.GetInstanceID())) return;
        Material source = renderer.sharedMaterial;
        if (source == null || source.shader == null ||
            !string.Equals(source.shader.name, HwsReticleShaderName, StringComparison.Ordinal) ||
            !source.HasProperty(HwsReticleSizeProperty)) return;
        float baseline = source.GetFloat(HwsReticleSizeProperty);
        if (baseline < HwsReticleEligibleSizeMinimum || baseline > HwsReticleEligibleSizeMaximum) return;
        float target = Mathf.Min(baseline * IndoorReticleNormalSizeMultiplier, HwsReticleNormalSizeCap);
        if (target <= baseline + .001f) return;

        Material boosted = new Material(source)
        {
            name = source.name + "_KH_NORMAL_RETICLE_SIZE",
            hideFlags = HideFlags.DontSave
        };
        boosted.SetFloat(HwsReticleSizeProperty, target);
        renderer.sharedMaterial = boosted;
        enhancedHwsReticleSizeRenderers.Add(renderer.GetInstanceID());
        hwsReticleSizeStates.Add(new HwsReticleSizeState(owner, renderer, source, boosted, baseline, target));
    }

    private void RestoreStaleHwsReticleBoosts()
    {
        for (int index = hwsReticleSizeStates.Count - 1; index >= 0; index--)
        {
            HwsReticleSizeState state = hwsReticleSizeStates[index];
            if (ReticleBelongsToEquippedWeapon(state.Owner)) continue;
            if (!RestoreHwsReticleSizeState(state)) continue;
            hwsReticleSizeStates.RemoveAt(index);
        }
        for (int index = hwsReticleBoostStates.Count - 1; index >= 0; index--)
        {
            HwsReticleBoostState state = hwsReticleBoostStates[index];
            if (ReticleBelongsToEquippedWeapon(state.Reticle)) continue;
            if (!RestoreHwsReticleBrightnessState(state)) continue;
            enhancedHwsReticles.Remove(state.Reticle == null ? 0 : state.Reticle.GetInstanceID());
            hwsReticleBoostStates.RemoveAt(index);
        }
    }

    private bool RestoreHwsReticleSizeState(HwsReticleSizeState state)
    {
        try
        {
            if (state.Renderer != null && state.Renderer.sharedMaterial == state.Boosted)
                state.Renderer.sharedMaterial = state.Original;
            if (state.Boosted != null) UnityEngine.Object.Destroy(state.Boosted);
            enhancedHwsReticleSizeRenderers.Remove(state.Renderer == null ? 0 : state.Renderer.GetInstanceID());
            return true;
        }
        catch (Exception exception)
        {
            log?.LogWarning("Vektor Kill House HWS size restore warning: " + exception.Message);
            return false;
        }
    }

    private bool RestoreHwsReticleBrightnessState(HwsReticleBoostState state)
    {
        try
        {
            HWSReticleBrightness reticle = state.Reticle;
            if (reticle == null) return true;
            if (reticle.reticleSettings == null) return false;
            int count = Math.Min(reticle.reticleSettings.Length, state.Normal.Length);
            for (int index = 0; index < count; index++)
            {
                ReticleSetting setting = reticle.reticleSettings[index];
                if (setting == null) continue;
                setting.ReticleBrightness = state.Normal[index];
                setting.ReticleBrightness_NVG = state.Nvg[index];
            }
            ApplyHwsSelectedBrightness(reticle);
            return true;
        }
        catch (Exception exception)
        {
            log?.LogWarning("Vektor Kill House HWS baseline restore warning: " + exception.Message);
            return false;
        }
    }

    private static void ApplyHwsSelectedBrightness(HWSReticleBrightness reticle)
    {
        if (reticle == null || reticle.reticleSettings == null || reticle.reticleSettings.Length == 0) return;
        int selectedIndex = Mathf.Clamp(reticle.CurrentBrightnessSetting, 0, reticle.reticleSettings.Length - 1);
        ReticleSetting selected = reticle.reticleSettings[selectedIndex];
        if (selected == null) return;
        Material normal = reticle.ReticleRenderer == null ? null : reticle.ReticleRenderer.sharedMaterial;
        Material nvg = reticle.ReticleRendererNVG == null ? null : reticle.ReticleRendererNVG.sharedMaterial;
        if (normal != null && normal.HasProperty("_Reticle_Brightness"))
            normal.SetFloat("_Reticle_Brightness", selected.ReticleBrightness);
        if (nvg != null && nvg.HasProperty("_Reticle_Brightness"))
            nvg.SetFloat("_Reticle_Brightness", selected.ReticleBrightness_NVG);
    }

    private void RestoreWeaponIlluminationBoosts(bool requireComplete = false)
    {
        int hwsCount = hwsReticleBoostStates.Count;
        int hwsSizeCount = hwsReticleSizeStates.Count;
        int controllerCount = visibleIrLaserBoostStates.Count;
        int lightCount = visibleLaserLightBoostStates.Count;
        int beamCount = boostedLaserBeamStates.Count;

        // Restore original material references before writing the selected baseline
        // brightness, otherwise the write would land on a clone that is about to be destroyed.
        bool passed = true;
        foreach (HwsReticleSizeState state in hwsReticleSizeStates)
            if (!RestoreHwsReticleSizeState(state)) passed = false;
        foreach (HwsReticleBoostState state in hwsReticleBoostStates)
            if (!RestoreHwsReticleBrightnessState(state)) passed = false;

        foreach (VisibleIrLaserBoostState state in visibleIrLaserBoostStates)
        {
            try
            {
                if (state.Controller != null)
                {
                    state.Controller.minBrighness = state.MinBrightness;
                    state.Controller.maxBrighness = state.MaxBrightness;
                }
                if (state.LightData != null) state.LightData.intensity = state.LightDataIntensity;
                if (state.Light != null) state.Light.intensity = state.LightIntensity;
            }
            catch (Exception exception)
            {
                passed = false;
                log?.LogWarning("Vektor Kill House visible-laser controller restore warning: " + exception.Message);
            }
        }

        foreach (VisibleLaserLightBoostState state in visibleLaserLightBoostStates)
        {
            try
            {
                if (state.LightData != null) state.LightData.intensity = state.LightDataIntensity;
                if (state.Light != null) state.Light.intensity = state.LightIntensity;
            }
            catch (Exception exception)
            {
                passed = false;
                log?.LogWarning("Vektor Kill House visible-laser light restore warning: " + exception.Message);
            }
        }

        if (!passed)
        {
            const string message = "one or more weapon illumination baselines could not be restored";
            if (requireComplete) throw new InvalidOperationException(message);
            log?.LogWarning("Vektor Kill House " + message + "; ownership was retained for retry.");
            return;
        }

        hwsReticleSizeStates.Clear();
        hwsReticleBoostStates.Clear();
        visibleIrLaserBoostStates.Clear();
        visibleLaserLightBoostStates.Clear();
        enhancedHwsReticles.Clear();
        enhancedHwsReticleSizeRenderers.Clear();
        enhancedLaserLights.Clear();
        enhancedVisibleLaserLights.Clear();
        RestoreBoostedLaserBeamMaterials();
        if (hwsCount + hwsSizeCount + controllerCount + lightCount + beamCount > 0)
            log?.LogInfo("Vektor Kill House weapon illumination baselines restored: hws=" + hwsCount +
                         ", hwsSize=" + hwsSizeCount +
                         ", visibleLaserControllers=" + controllerCount + ", visibleLaserLights=" + lightCount +
                         ", visibleLaserBeams=" + beamCount + ".");
    }

    private void RestoreBoostedLaserBeamMaterials()
    {
        foreach (BoostedLaserBeamState state in boostedLaserBeamStates)
        {
            if (state.Renderer != null && state.Original != null) state.Renderer.sharedMaterial = state.Original;
            if (state.Boosted != null) UnityEngine.Object.Destroy(state.Boosted);
        }
        boostedLaserBeamStates.Clear();
        enhancedVisibleLaserBeams.Clear();
    }

    private static void BoostHdrColor(Material material, string property, float multiplier)
    {
        if (material == null || !material.HasProperty(property)) return;
        Color value = material.GetColor(property);
        value.r *= multiplier;
        value.g *= multiplier;
        value.b *= multiplier;
        material.SetColor(property, value);
    }

    private static string DescribeEmission(Material material)
    {
        if (material == null) return "missing";
        float intensity = material.HasProperty("_EmissiveIntensity") ? material.GetFloat("_EmissiveIntensity") : -1f;
        Color color = material.HasProperty("_EmissiveColor") ? material.GetColor("_EmissiveColor") : Color.black;
        var properties = new List<string>();
        Shader shader = material.shader;
        if (shader != null)
        {
            for (int index = 0; index < shader.GetPropertyCount() && properties.Count < 20; index++)
            {
                string property = shader.GetPropertyName(index);
                string lower = property.ToLowerInvariant();
                if (!lower.Contains("color") && !lower.Contains("emiss") && !lower.Contains("bright") &&
                    !lower.Contains("intens") && !lower.Contains("tint") && !lower.Contains("reticle") &&
                    !lower.Contains("tiling") && !lower.Contains("offset") && !lower.Contains("scale") &&
                    !lower.Contains("size")) continue;
                ShaderPropertyType type = shader.GetPropertyType(index);
                if (type == ShaderPropertyType.Color)
                {
                    Color value = material.GetColor(property);
                    properties.Add(property + "=" + value.r.ToString("F2", CultureInfo.InvariantCulture) + "," +
                                   value.g.ToString("F2", CultureInfo.InvariantCulture) + "," +
                                   value.b.ToString("F2", CultureInfo.InvariantCulture) + "," +
                                   value.a.ToString("F2", CultureInfo.InvariantCulture));
                }
                else if (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range)
                    properties.Add(property + "=" + material.GetFloat(property).ToString("F3", CultureInfo.InvariantCulture));
                else if (type == ShaderPropertyType.Vector)
                {
                    Vector4 value = material.GetVector(property);
                    properties.Add(property + "=" + value.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                                   value.y.ToString("F3", CultureInfo.InvariantCulture) + "," +
                                   value.z.ToString("F3", CultureInfo.InvariantCulture) + "," +
                                   value.w.ToString("F3", CultureInfo.InvariantCulture));
                }
            }
        }
        return material.name + "/intensity=" + intensity.ToString("F3", CultureInfo.InvariantCulture) +
               "/rgb=" + color.r.ToString("F3", CultureInfo.InvariantCulture) + "," +
               color.g.ToString("F3", CultureInfo.InvariantCulture) + "," +
               color.b.ToString("F3", CultureInfo.InvariantCulture) + "/shader=" +
               (shader == null ? "missing" : shader.name) + "/props=" + string.Join(";", properties);
    }

    private static string DescribeHwsReticleSize(Material material)
    {
        if (material == null || material.shader == null ||
            !string.Equals(material.shader.name, HwsReticleShaderName, StringComparison.Ordinal) ||
            !material.HasProperty(HwsReticleSizeProperty)) return "missing";
        return material.GetFloat(HwsReticleSizeProperty).ToString("F3", CultureInfo.InvariantCulture);
    }

    private void TryCompleteDeferredAiSightAudit()
    {
        Scene scene = FindLoadedSceneByHandle(pendingSceneHandle);
        if (!IsKillHouseScene(scene))
        {
            aiSightOcclusionPending = false;
            return;
        }
        GameObject root = FindOwnedRoot(scene);
        if (root == null) return;
        bool passed = ValidateAiSightOcclusion(root, out bool deferred);
        if (deferred) return;
        aiSightOcclusionPending = false;
        aiSightOcclusionPassed = passed;
        if (!passed)
        {
            MarkFailure(scene, root, "deferred-ai-sight-occlusion");
            return;
        }
        log.LogInfo("Vektor Kill House deferred AI sight-occlusion gate passed against the live EyesAI detection mask.");
    }

    private bool ValidateAiSightOcclusion(GameObject root, out bool deferred)
    {
        deferred = false;
        List<SolidWall> walls = CollectSolidWalls(root);
        if (walls.Count < 1)
        {
            log.LogError("Vektor Kill House AI sight gate failed: no solid native wall colliders were found.");
            return false;
        }
        int[] masks = CollectResidentAiDetectionMasks();
        if (masks.Length == 0)
        {
            deferred = true;
            log.LogWarning("Vektor Kill House AI sight gate deferred: OPERATOR has not instantiated or loaded a resident EyesAI profile yet; solidWalls=" +
                           walls.Count + ".");
            return false;
        }

        Physics.SyncTransforms();
        int probes = 0;
        var failures = new List<string>();
        foreach (int mask in masks)
        {
            foreach (SolidWall wall in walls)
            {
                Collider collider = wall.Collider;
                int layerBit = 1 << collider.gameObject.layer;
                Bounds bounds = collider.bounds;
                bool alongX = bounds.size.x <= bounds.size.z;
                Vector3 normal = alongX ? Vector3.right : Vector3.forward;
                float thickness = alongX ? bounds.size.x : bounds.size.z;
                Vector3 center = bounds.center;
                float halfProbe = Mathf.Max(.12f, thickness * .5f + .2f);
                Vector3 a = center - normal * halfProbe;
                Vector3 b = center + normal * halfProbe;
                Vector3 forwardDirection = (b - a).normalized;
                Vector3 reverseDirection = -forwardDirection;
                float probeDistance = Vector3.Distance(a, b) + .01f;
                bool layerIncluded = (mask & layerBit) != 0;
                // Test the audited collider directly. A global Linecast can hit an overlapping
                // perpendicular wall first at a sealed corner and falsely report that this wall
                // is transparent even though both colliders block the live EyesAI mask.
                bool forwardBlocked = layerIncluded && collider.Raycast(new Ray(a, forwardDirection),
                    out RaycastHit forwardHit, probeDistance);
                bool reverseBlocked = layerIncluded && collider.Raycast(new Ray(b, reverseDirection),
                    out RaycastHit reverseHit, probeDistance);
                probes += 2;
                if (!forwardBlocked || !reverseBlocked)
                {
                    failures.Add(wall.Root.name + "[layer=" + collider.gameObject.layer +
                                 ",mask=" + mask + ",forward=" + forwardBlocked +
                                 ",reverse=" + reverseBlocked + "]");
                    if (failures.Count >= 16) break;
                }
            }
            if (failures.Count >= 16) break;
        }

        bool passed = failures.Count == 0;
        if (passed)
            log.LogInfo("Vektor Kill House AI sight-occlusion gate passed: solidWalls=" + walls.Count +
                        ", detectionMasks=" + string.Join("|", masks.Select(mask => mask + ":" + DescribeLayers(mask))) +
                        ", twoSidedLinecasts=" + probes + ".");
        else
            log.LogError("Vektor Kill House AI sight-occlusion gate failed: solidWalls=" + walls.Count +
                         ", detectionMasks=" + string.Join("|", masks) + ", failures=[" +
                         string.Join(" | ", failures) + "].");
        return passed;
    }

    private static List<SolidWall> CollectSolidWalls(GameObject root)
    {
        Transform[] wallRoots = root.GetComponentsInChildren<Transform>(true).Where(item =>
            item.name.StartsWith("NATIVE_RoomWall_", StringComparison.Ordinal) ||
            item.name.StartsWith("NATIVE_ConnectorWall_", StringComparison.Ordinal) ||
            item.name.StartsWith("NATIVE_InteriorSplitWall_", StringComparison.Ordinal)).ToArray();
        var result = new List<SolidWall>();
        var colliderIds = new HashSet<int>();
        foreach (Transform wallRoot in wallRoots)
        {
            foreach (Collider collider in wallRoot.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || !collider.enabled || collider.isTrigger ||
                    !colliderIds.Add(collider.GetInstanceID())) continue;
                Bounds bounds = collider.bounds;
                if (bounds.size.y < 1.5f || Mathf.Min(bounds.size.x, bounds.size.z) <= .001f) continue;
                result.Add(new SolidWall(wallRoot, collider));
            }
        }
        return result;
    }

    private static int[] CollectResidentAiDetectionMasks()
    {
        var masks = new HashSet<int>();
        void Add(EyesAI eyes)
        {
            if (eyes == null) return;
            int value = eyes.DetectionLayerMask.value;
            if (value != 0) masks.Add(value);
        }

        GameManager manager = GameManager.instance;
        if (manager != null)
        {
            if (manager.AllAITypes != null)
                for (int index = 0; index < manager.AllAITypes.Count; index++)
                {
                    GameObject prefab = manager.AllAITypes[index];
                    if (prefab == null) continue;
                    foreach (EyesAI eyes in prefab.GetComponentsInChildren<EyesAI>(true)) Add(eyes);
                }
            if (manager.allAI != null)
                for (int index = 0; index < manager.allAI.Count; index++)
                {
                    BrainAI brain = manager.allAI[index];
                    if (brain != null) Add(brain.eyesAI);
                }
        }

        Il2CppReferenceArray<UnityEngine.Object> objects = Resources.FindObjectsOfTypeAll(Il2CppType.Of<EyesAI>());
        foreach (UnityEngine.Object value in objects)
            Add(value == null ? null : value.TryCast<EyesAI>());
        return masks.OrderBy(value => value).ToArray();
    }

    private static string DescribeLayers(int mask)
    {
        var names = new List<string>();
        for (int layer = 0; layer < 32; layer++)
        {
            if ((mask & (1 << layer)) == 0) continue;
            string name = LayerMask.LayerToName(layer);
            names.Add(string.IsNullOrEmpty(name) ? layer.ToString(CultureInfo.InvariantCulture) : name);
        }
        return string.Join(",", names);
    }

    private bool EnsureRuntimeNavigationGraph(GameObject root)
    {
        Transform[] roomFloors = root.GetComponentsInChildren<Transform>(true)
            .Where(item => string.Equals(item.name, "NATIVE_Floor", StringComparison.Ordinal)).ToArray();
        Transform[] connectorFloors = root.GetComponentsInChildren<Transform>(true)
            .Where(item => item.name.StartsWith("NATIVE_ConnectorFloor_", StringComparison.Ordinal)).ToArray();
        Transform[] floors = roomFloors.Concat(connectorFloors).ToArray();
        Transform[] warehouseAprons = root.GetComponentsInChildren<Transform>(true)
            .Where(item => string.Equals(item.name, "NATIVE_WarehouseGroundApron", StringComparison.Ordinal)).ToArray();
        int apronExclusionMarkers = root.GetComponentsInChildren<Transform>(true).Count(item =>
            string.Equals(item.name, "WAREHOUSE_APRON_NAV_EXCLUDED_ENCLOSED_PERIMETER",
                StringComparison.Ordinal));
        bool warehouseApronExcluded = warehouseAprons.Length == 1 && apronExclusionMarkers == 1 &&
            floors.All(floor => floor != warehouseAprons[0]);
        if (!warehouseApronExcluded)
        {
            log.LogError("Vektor Kill House navigation rejected the warehouse apron policy: aprons=" +
                         warehouseAprons.Length + ", exclusionMarkers=" + apronExclusionMarkers +
                         ", includedInGridSources=" + (warehouseAprons.Length == 1 &&
                             floors.Any(floor => floor == warehouseAprons[0])) + ".");
            return false;
        }
        var floorColliderList = new List<Collider>();
        var floorColliderIds = new HashSet<int>();
        int floorsWithoutCollider = 0;
        foreach (Transform floor in floors)
        {
            bool hasCollider = false;
            foreach (Collider collider in floor.GetComponentsInChildren<Collider>(true))
            {
                if (collider != null && floorColliderIds.Add(collider.GetInstanceID()))
                {
                    floorColliderList.Add(collider);
                    hasCollider = true;
                }
            }
            if (!hasCollider) floorsWithoutCollider++;
        }
        Collider[] floorColliders = floorColliderList.ToArray();
        if (roomFloors.Length < 19 || roomFloors.Length > 21 || connectorFloors.Length < 1 || connectorFloors.Length > 32 ||
            floorsWithoutCollider != 0 || floorColliders.Length < floors.Length)
        {
            log.LogError("Vektor Kill House navigation gate failed: native floor collider closure is invalid; floors=" +
                         floors.Length + ", roomFloors=" + roomFloors.Length + ", connectorFloors=" + connectorFloors.Length +
                         ", floorsWithoutCollider=" + floorsWithoutCollider + ", floorColliders=" + floorColliders.Length + ".");
            return false;
        }

        try
        {
            AstarPath.FindAstarPath();
            AstarPath astar = AstarPath.active;
            if (astar != null && (astar.gameObject == null || !astar.gameObject.scene.IsValid() ||
                                  !astar.gameObject.scene.isLoaded ||
                                  astar.gameObject.scene.handle != root.scene.handle))
            {
                log.LogError("Vektor Kill House navigation rejected an AstarPath owned by another loaded scene: " +
                             (astar.gameObject == null ? "<missing-host>" :
                                 astar.gameObject.name + "@" + astar.gameObject.scene.handle) + ".");
                return false;
            }
            if (astar == null)
            {
                runtimeAstarHost = new GameObject("MOD_VektorKillHouse_AstarPath");
                runtimeAstarHost.transform.SetParent(root.transform, false);
                astar = runtimeAstarHost.AddComponent<AstarPath>();
                runtimeOwnsAstarHost = astar != null;
            }
            else runtimeOwnsAstarHost = false;
            if (astar == null || astar.data == null) return false;

            GameObject astarHost = astar.gameObject;
            runtimeRvoSimulator = astarHost == null ? null : astarHost.GetComponent<RVOSimulator>();
            runtimeOwnsRvoSimulator = false;
            if (runtimeRvoSimulator == null && astarHost != null)
            {
                runtimeRvoSimulator = astarHost.AddComponent<RVOSimulator>();
                runtimeOwnsRvoSimulator = runtimeRvoSimulator != null;
            }
            if (runtimeRvoSimulator == null || runtimeRvoSimulator.gameObject != astarHost ||
                !runtimeRvoSimulator.isActiveAndEnabled) return false;

            if (!ReleaseRuntimeNavigationGraph(runtimeNavigationAstar != null ? runtimeNavigationAstar : astar,
                    "graph rebuild"))
            {
                log.LogError("Vektor Kill House navigation rebuild rejected: the prior runtime graph remains attached.");
                return false;
            }
            Bounds bounds = floorColliders[0].bounds;
            foreach (Collider floor in floorColliders.Skip(1)) bounds.Encapsulate(floor.bounds);
            int width = Mathf.CeilToInt((bounds.size.x + 2f) / NavigationNodeSize);
            int depth = Mathf.CeilToInt((bounds.size.z + 2f) / NavigationNodeSize);
            Pathfinding.NavGraph nativeGraph = astar.data.AddGraph(Il2CppType.Of<GridGraph>());
            GridGraph graph = nativeGraph == null ? null : nativeGraph.TryCast<GridGraph>();
            if (graph == null)
            {
                if (nativeGraph != null) astar.data.RemoveGraph(nativeGraph);
                return false;
            }

            runtimeNavigationGraph = graph;
            runtimeNavigationAstar = astar;
            runtimeNavigationOwnerSceneHandle = root.scene.handle;
            runtimeNavigationAstarHostInstanceId = astarHost == null ? 0 : astarHost.GetInstanceID();
            runtimeNavigationRvoInstanceId = runtimeRvoSimulator == null ? 0 : runtimeRvoSimulator.GetInstanceID();
            // Capture this while the newly created Unity objects are alive. By
            // SceneManager.sceneUnloaded, Unity's destroyed-object equality can
            // report the retained wrapper as null even though ownership and the
            // instance IDs were captured correctly during creation.
            runtimeNavigationAstarHostReferenceCaptured =
                !runtimeOwnsAstarHost || runtimeAstarHost != null;
            runtimeNavigationRvoReferenceCaptured =
                !runtimeOwnsRvoSimulator || runtimeRvoSimulator != null;
            graph.name = RuntimeGraphName;
            graph.center = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            graph.rotation = Vector3.zero;
            graph.aspectRatio = 1f;
            graph.isometricAngle = 0f;
            graph.SetDimensions(width, depth, NavigationNodeSize);
            graph.maxSlope = 32f;
            graph.maxStepHeight = .55f;
            graph.erodeIterations = 1;
            graph.neighbours = NumNeighbours.Eight;
            graph.cutCorners = false;
            graph.collision.use2D = false;
            graph.collision.heightCheck = true;
            graph.collision.unwalkableWhenNoGround = true;
            graph.collision.fromHeight = 5f;
            graph.collision.thickRaycast = false;
            graph.collision.collisionCheck = true;
            graph.collision.diameter = .55f;
            graph.collision.height = 1.7f;
            graph.collision.collisionOffset = .85f;

            const int floorScanLayer = 30;
            var originalLayers = new Dictionary<GameObject, int>();
            foreach (Collider floorCollider in floorColliders)
            {
                GameObject floorObject = floorCollider.gameObject;
                if (floorObject != null && !originalLayers.ContainsKey(floorObject))
                    originalLayers.Add(floorObject, floorObject.layer);
            }
            graph.collision.heightMask = 1 << floorScanLayer;
            graph.collision.mask = ~(1 << floorScanLayer);
            try
            {
                foreach (GameObject floorObject in originalLayers.Keys) floorObject.layer = floorScanLayer;
                Physics.SyncTransforms();
                astar.Scan(graph);
            }
            finally
            {
                foreach (KeyValuePair<GameObject, int> pair in originalLayers)
                    if (pair.Key != null) pair.Key.layer = pair.Value;
                Physics.SyncTransforms();
            }

            Transform[] enemies = root.GetComponentsInChildren<Transform>(true)
                .Where(item => item.name.StartsWith("PVE_EnemySpawn_", StringComparison.Ordinal)).ToArray();
            int snapped = SnapTacticalEnemyMarkersToGrid(graph, enemies);
            Transform[] players = root.GetComponentsInChildren<Transform>(true)
                .Where(item => item.name.StartsWith("PVE_PlayerSpawn_", StringComparison.Ordinal)).ToArray();
            int playerMarkersOnGrid = SnapMarkersToGrid(astar, players, true);
            int enemiesOnGrid = enemies.Count(marker => astar.IsPointOnNavmesh(marker.position));
            int expectedEnemies = MinimumAuthoredPveEnemyMarkers;
            bool passed = enemies.Length == expectedEnemies && snapped == expectedEnemies && enemiesOnGrid == expectedEnemies &&
                           players.Length == 4 && playerMarkersOnGrid == 4;
            log.LogInfo("Vektor Kill House navigation scan: passed=" + passed + ", graph=" + graph.name +
                        ", nodes=" + width + "x" + depth + ", nodeSize=" +
                        NavigationNodeSize.ToString("F2", CultureInfo.InvariantCulture) +
                        ", roomFloors=" + roomFloors.Length + ", connectorFloors=" + connectorFloors.Length +
                        ", warehouseApronNavExcluded=" + warehouseApronExcluded +
                        ", floorColliders=" + floorColliders.Length + ", enemyMarkers=" + enemiesOnGrid + "/" + enemies.Length +
                        ", playerMarkers=" + playerMarkersOnGrid + "/" + players.Length + ".");
            return passed;
        }
        catch (Exception exception)
        {
            log.LogError("Vektor Kill House navigation graph creation failed: " +
                         exception.GetType().Name + ": " + exception.Message);
            ReleaseRuntimeNavigation("failed graph creation");
            return false;
        }
    }

    private static int SnapTacticalEnemyMarkersToGrid(GridGraph graph,
        IEnumerable<Transform> markers)
    {
        int snapped = 0;
        var claimed = new List<Vector3>();
        foreach (Transform marker in markers.OrderBy(item => item.name, StringComparer.Ordinal))
        {
            if (marker == null) continue;
            Transform envelope = marker.parent;
            Transform role = envelope == null ? null : FindDirectChild(envelope, "TACTICAL_ROLE_");
            Transform cover = envelope == null ? null : FindDirectChild(envelope, "TACTICAL_COVER_POINT_");
            Transform threat = envelope == null ? null : FindDirectChild(envelope, "TACTICAL_THREAT_POINT_");
            if (role == null || cover == null || threat == null) continue;

            Vector3 authoredPosition = marker.position;
            bool selected = false;
            foreach (Vector3 candidate in WalkableGridCandidates(graph, authoredPosition))
            {
                if (claimed.Any(other => Vector2.Distance(
                        new Vector2(candidate.x, candidate.z),
                        new Vector2(other.x, other.z)) < NavigationNodeSize * .75f))
                    continue;
                if (!StandingCapsuleIsClear(candidate)) continue;
                float coverDistance = Vector2.Distance(
                    new Vector2(candidate.x, candidate.z),
                    new Vector2(cover.position.x, cover.position.z));
                float threatDistance = Vector2.Distance(
                    new Vector2(candidate.x, candidate.z),
                    new Vector2(threat.position.x, threat.position.z));
                if (coverDistance < .15f || coverDistance > 4.50f || threatDistance < 1.15f)
                    continue;

                marker.position = candidate;
                role.position = candidate;
                Vector3 facing = threat.position - candidate;
                facing.y = 0f;
                if (facing.sqrMagnitude >= .01f)
                    marker.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
                claimed.Add(candidate);
                snapped++;
                selected = true;
                break;
            }
            if (!selected)
            {
                marker.position = authoredPosition;
                role.position = authoredPosition;
            }
        }
        Physics.SyncTransforms();
        return snapped;
    }

    private static int SnapMarkersToGrid(AstarPath astar, IEnumerable<Transform> markers, bool move)
    {
        int snapped = 0;
        foreach (Transform marker in markers)
        {
            Pathfinding.NNInfo nearest = astar.GetNearest(marker.position, NNConstraint.Walkable);
            if (nearest.node == null) continue;
            Vector3 nodeCenter = (Vector3)nearest.node.position;
            float horizontal = Vector2.Distance(new Vector2(marker.position.x, marker.position.z),
                                                new Vector2(nodeCenter.x, nodeCenter.z));
            if (horizontal > NavigationMarkerClearance) continue;
            if (move) marker.position = nodeCenter + Vector3.up * .03f;
            snapped++;
        }
        Physics.SyncTransforms();
        return snapped;
    }

    private static IEnumerable<Vector3> WalkableGridCandidates(GridGraph graph, Vector3 origin)
    {
        var candidates = new List<Vector3>();
        if (graph == null) return candidates;
        Bounds region = new Bounds(origin, new Vector3(
            NavigationMarkerClearance * 2f,
            4f,
            NavigationMarkerClearance * 2f));
        var nodes = graph.GetNodesInRegion(region);
        if (nodes == null) return candidates;
        for (int index = 0; index < nodes.Count; index++)
        {
            Pathfinding.GraphNode node = nodes[index];
            if (node == null || !node.Walkable) continue;
            Vector3 candidate = (Vector3)node.position + Vector3.up * .03f;
            float horizontal = Vector2.Distance(
                new Vector2(origin.x, origin.z),
                new Vector2(candidate.x, candidate.z));
            if (horizontal <= NavigationMarkerClearance)
                candidates.Add(candidate);
        }
        return candidates
            .OrderBy(candidate => Vector2.SqrMagnitude(
                new Vector2(candidate.x - origin.x, candidate.z - origin.z)))
            .ThenBy(candidate => candidate.x)
            .ThenBy(candidate => candidate.z)
            .ToArray();
    }

    private static bool StandingCapsuleIsClear(Vector3 position)
    {
        int obstructionMask = Physics.DefaultRaycastLayers;
        foreach (string dynamicLayerName in new[] { "LocalPlayer", "Character", "Hitbox" })
        {
            int dynamicLayer = LayerMask.NameToLayer(dynamicLayerName);
            if (dynamicLayer >= 0) obstructionMask &= ~(1 << dynamicLayer);
        }
        return Physics.OverlapCapsule(
            position + Vector3.up * .42f,
            position + Vector3.up * 1.58f,
            .30f,
            obstructionMask,
            QueryTriggerInteraction.Ignore).Length == 0;
    }

    private bool HasRuntimeNavigationOwnership()
    {
        return runtimeNavigationGraph != null || runtimeNavigationAstar != null ||
               runtimeAstarHost != null || runtimeRvoSimulator != null ||
               runtimeOwnsAstarHost || runtimeOwnsRvoSimulator ||
               runtimeNavigationAstarHostReferenceCaptured ||
               runtimeNavigationRvoReferenceCaptured ||
               runtimeNavigationOwnerSceneHandle != 0 || runtimeNavigationAstarHostInstanceId != 0 ||
               runtimeNavigationRvoInstanceId != 0;
    }

    private bool HasNavigationTeardownSnapshot()
    {
        return pendingNavigationTeardownAuditFrame >= 0 || navigationTeardownSceneHandle != 0 ||
               navigationTeardownAstarHostInstanceId != 0 || navigationTeardownRvoInstanceId != 0 ||
               navigationTeardownHadRuntimeGraph || navigationTeardownRuntimeGraph != null ||
               navigationTeardownRuntimeGraphReferenceCaptured ||
               navigationTeardownAstarHostReferenceCaptured || navigationTeardownRvoReferenceCaptured ||
               navigationTeardownAuditStartedFrame >= 0 || navigationTeardownAuditDeadlineFrame >= 0 ||
               navigationTeardownAuditGeneration != 0;
    }

    private bool ArmNavigationTeardownAudit(int unloadedSceneHandle)
    {
        if (unloadedSceneHandle == 0)
        {
            navigationTeardownAuditLastDetail = "missing-owner-scene-handle";
            try
            {
                log?.LogError("Vektor Kill House refused to arm a navigation teardown audit without an owner scene handle.");
            }
            catch
            {
                // Refusal remains authoritative even if logging is unavailable.
            }
            return false;
        }

        if (HasNavigationTeardownSnapshot())
        {
            if (navigationTeardownSceneHandle != unloadedSceneHandle)
            {
                navigationTeardownAuditLastDetail = "snapshot-owner-conflict=" +
                                                    navigationTeardownSceneHandle + "/" + unloadedSceneHandle;
                try
                {
                    log?.LogError("Vektor Kill House refused to overwrite an unresolved navigation teardown " +
                                  "snapshot: " + navigationTeardownAuditLastDetail + ".");
                }
                catch
                {
                    // The exact prior snapshot remains retained.
                }
                return false;
            }

            if (navigationTeardownAuditGeneration == 0 || navigationTeardownAuditStartedFrame < 0 ||
                navigationTeardownAuditDeadlineFrame < navigationTeardownAuditStartedFrame)
            {
                navigationTeardownAuditLastDetail = "snapshot-deadline-identity-invalid";
                return false;
            }

            if (pendingNavigationTeardownAuditFrame < 0)
            {
                long currentFrame = Time.frameCount;
                pendingNavigationTeardownAuditFrame = currentFrame >= navigationTeardownAuditDeadlineFrame
                    ? currentFrame
                    : Math.Min(currentFrame + ApplyDelayFrames, navigationTeardownAuditDeadlineFrame);
            }
            return true;
        }

        navigationTeardownSceneHandle = unloadedSceneHandle;
        navigationTeardownAstarHostInstanceId = runtimeNavigationAstarHostInstanceId;
        navigationTeardownRvoInstanceId = runtimeNavigationRvoInstanceId;
        navigationTeardownOwnedAstarHost = runtimeOwnsAstarHost;
        navigationTeardownOwnedRvoSimulator = runtimeOwnsRvoSimulator;
        navigationTeardownHadRuntimeGraph = runtimeNavigationGraph != null;
        navigationTeardownRuntimeGraph = runtimeNavigationGraph;
        navigationTeardownRuntimeGraphReferenceCaptured = runtimeNavigationGraph != null;
        navigationTeardownAstarHost = runtimeOwnsAstarHost ? runtimeAstarHost : null;
        navigationTeardownRvoSimulator = runtimeOwnsRvoSimulator ? runtimeRvoSimulator : null;
        navigationTeardownAstarHostReferenceCaptured =
            !runtimeOwnsAstarHost || runtimeNavigationAstarHostReferenceCaptured;
        navigationTeardownRvoReferenceCaptured =
            !runtimeOwnsRvoSimulator || runtimeNavigationRvoReferenceCaptured;
        navigationTeardownAuditAttempts = 0;
        navigationTeardownAuditLastDetail = string.Empty;
        long auditStartFrame = Time.frameCount;
        if (navigationTeardownAuditGenerationCounter == long.MaxValue)
        {
            navigationTeardownAuditLastDetail = "audit-generation-overflow";
            return false;
        }
        navigationTeardownAuditGenerationCounter++;
        navigationTeardownAuditGeneration = navigationTeardownAuditGenerationCounter;
        navigationTeardownAuditStartedFrame = auditStartFrame;
        navigationTeardownAuditDeadlineFrame =
            auditStartFrame + NavigationTeardownAuditHardDeadlineFrames;
        // Destroy is end-of-frame. Two frames lets Unity finish scene-owned destruction while
        // still running before a replacement generation's delayed runtime contract.
        pendingNavigationTeardownAuditFrame = auditStartFrame + ApplyDelayFrames;
        return true;
    }

    private NavigationTeardownAuditResult CompleteNavigationTeardownAudit()
    {
        pendingNavigationTeardownAuditFrame = -1;
        navigationTeardownAuditAttempts++;
        int unloadedSceneHandle = navigationTeardownSceneHandle;
        int expectedAstarId = navigationTeardownAstarHostInstanceId;
        int expectedRvoId = navigationTeardownRvoInstanceId;
        bool ownedAstarHost = navigationTeardownOwnedAstarHost;
        bool ownedRvoSimulator = navigationTeardownOwnedRvoSimulator;
        bool hadRuntimeGraph = navigationTeardownHadRuntimeGraph;
        GridGraph expectedRuntimeGraph = navigationTeardownRuntimeGraph;

        try
        {
            var identityErrors = new List<string>();
            if (unloadedSceneHandle == 0) identityErrors.Add("scene-handle=0");
            if (navigationTeardownAuditGeneration == 0) identityErrors.Add("audit-generation=0");
            if (navigationTeardownAuditStartedFrame < 0) identityErrors.Add("audit-start-frame=missing");
            if (navigationTeardownAuditDeadlineFrame !=
                navigationTeardownAuditStartedFrame + NavigationTeardownAuditHardDeadlineFrames)
                identityErrors.Add("audit-deadline-mutated");
            if (hadRuntimeGraph && !navigationTeardownRuntimeGraphReferenceCaptured)
                identityErrors.Add("graph-reference-not-captured");
            if (hadRuntimeGraph && expectedAstarId == 0) identityErrors.Add("graph-host-id=0");
            if (ownedAstarHost && expectedAstarId == 0) identityErrors.Add("owned-astar-host-id=0");
            if (ownedRvoSimulator && expectedRvoId == 0) identityErrors.Add("owned-rvo-id=0");
            if (ownedAstarHost && !navigationTeardownAstarHostReferenceCaptured)
                identityErrors.Add("owned-astar-host-reference-not-captured");
            if (ownedRvoSimulator && !navigationTeardownRvoReferenceCaptured)
                identityErrors.Add("owned-rvo-reference-not-captured");
            if (runtimeNavigationOwnerSceneHandle != 0 &&
                runtimeNavigationOwnerSceneHandle != unloadedSceneHandle)
                identityErrors.Add("active-owner=" + runtimeNavigationOwnerSceneHandle);
            if (runtimeNavigationGraph != null && runtimeNavigationGraph != expectedRuntimeGraph)
                identityErrors.Add("active-graph-reference-changed");
            if (runtimeNavigationAstar != null && runtimeNavigationAstar.gameObject != null &&
                runtimeNavigationAstar.gameObject.GetInstanceID() != expectedAstarId)
                identityErrors.Add("active-astar-host-changed");
            if (runtimeAstarHost != null && runtimeAstarHost.GetInstanceID() != expectedAstarId)
                identityErrors.Add("active-owned-astar-host-changed");
            if (runtimeRvoSimulator != null && runtimeRvoSimulator.GetInstanceID() != expectedRvoId)
                identityErrors.Add("active-rvo-changed");
            if (identityErrors.Count != 0)
                return ReportAmbiguousNavigationTeardown(string.Join(",", identityErrors));

            Scene oldOwnerScene = FindLoadedSceneByHandle(unloadedSceneHandle);
            bool survivingOwnerScene = oldOwnerScene.IsValid() && oldOwnerScene.isLoaded;
            int survivingOwnedAstarHosts = ownedAstarHost && navigationTeardownAstarHost != null ? 1 : 0;
            int survivingOwnedRvoSimulators =
                ownedRvoSimulator && navigationTeardownRvoSimulator != null ? 1 : 0;
            int survivingRuntimeGraphs = 0;

            Il2CppReferenceArray<UnityEngine.Object> astarObjects =
                Resources.FindObjectsOfTypeAll(Il2CppType.Of<AstarPath>());
            foreach (UnityEngine.Object value in astarObjects)
            {
                AstarPath astar = value == null ? null : value.TryCast<AstarPath>();
                if (astar == null || astar.gameObject == null) continue;
                bool exactHost = expectedAstarId != 0 &&
                                 astar.gameObject.GetInstanceID() == expectedAstarId;
                if (!hadRuntimeGraph) continue;
                if (astar.data == null || astar.data.graphs == null)
                {
                    if (exactHost)
                        return ReportAmbiguousNavigationTeardown("exact-graph-host-uninspectable");
                    continue;
                }
                foreach (Pathfinding.NavGraph graph in astar.data.graphs)
                {
                    if (graph == null) continue;
                    bool exactGraph = graph == expectedRuntimeGraph;
                    bool reservedRuntimeGraph = string.Equals(graph.name, RuntimeGraphName,
                        StringComparison.Ordinal);
                    if (exactGraph || reservedRuntimeGraph) survivingRuntimeGraphs++;
                }
            }

            bool passed = !survivingOwnerScene && survivingOwnedAstarHosts == 0 &&
                          survivingOwnedRvoSimulators == 0 && survivingRuntimeGraphs == 0;
            navigationTeardownAuditLastDetail = "ownerScene=" + (survivingOwnerScene ? 1 : 0) +
                                                ",ownedAstarHosts=" + survivingOwnedAstarHosts +
                                                ",runtimeGraphs=" + survivingRuntimeGraphs +
                                                ",ownedRvoSimulators=" + survivingOwnedRvoSimulators;
            string message = "Vektor Kill House post-unload navigation teardown gate: passed=" + passed +
                             ", unloadedSceneHandle=" + unloadedSceneHandle +
                             ", generation=" + navigationTeardownAuditGeneration +
                             ", attempts=" + navigationTeardownAuditAttempts +
                             ", startFrame=" + navigationTeardownAuditStartedFrame +
                             ", deadlineFrame=" + navigationTeardownAuditDeadlineFrame +
                             ", " + navigationTeardownAuditLastDetail + ".";
            if (!passed)
            {
                log?.LogError(message);
                return NavigationTeardownAuditResult.Survivors;
            }

            RetireRuntimeNavigationStateAfterAbsenceProof(unloadedSceneHandle);
            try
            {
                log?.LogInfo(message);
            }
            catch
            {
                // Logging cannot revoke an already completed exact absence proof.
            }
            return NavigationTeardownAuditResult.Passed;
        }
        catch (Exception exception)
        {
            return ReportAmbiguousNavigationTeardown(exception.GetType().Name + ":" + exception.Message);
        }
    }

    private NavigationTeardownAuditResult ReportAmbiguousNavigationTeardown(string detail)
    {
        navigationTeardownAuditLastDetail = detail;
        try
        {
            log?.LogError("Vektor Kill House post-unload navigation teardown gate was ambiguous; " +
                          "owned state remains retained: unloadedSceneHandle=" + navigationTeardownSceneHandle +
                          ", generation=" + navigationTeardownAuditGeneration +
                          ", attempts=" + navigationTeardownAuditAttempts +
                          ", deadlineFrame=" + navigationTeardownAuditDeadlineFrame +
                          ", detail=" + detail + ".");
        }
        catch
        {
            // Ambiguity must retain ownership even when diagnostics cannot be emitted.
        }
        return NavigationTeardownAuditResult.Ambiguous;
    }

    private void RetireRuntimeNavigationStateAfterAbsenceProof(int unloadedSceneHandle)
    {
        if (unloadedSceneHandle == 0 || navigationTeardownSceneHandle != unloadedSceneHandle)
            throw new InvalidOperationException("navigation teardown snapshot changed before retirement");
        if (runtimeNavigationOwnerSceneHandle != 0 &&
            runtimeNavigationOwnerSceneHandle != unloadedSceneHandle)
            throw new InvalidOperationException("a newer navigation owner cannot be retired by an older audit");

        runtimeNavigationGraph = null;
        runtimeNavigationAstar = null;
        runtimeAstarHost = null;
        runtimeRvoSimulator = null;
        runtimeOwnsAstarHost = false;
        runtimeOwnsRvoSimulator = false;
        runtimeNavigationOwnerSceneHandle = 0;
        runtimeNavigationAstarHostInstanceId = 0;
        runtimeNavigationRvoInstanceId = 0;
        runtimeNavigationAstarHostReferenceCaptured = false;
        runtimeNavigationRvoReferenceCaptured = false;
        pendingNavigationTeardownAuditFrame = -1;
        navigationTeardownSceneHandle = 0;
        navigationTeardownAstarHostInstanceId = 0;
        navigationTeardownRvoInstanceId = 0;
        navigationTeardownOwnedAstarHost = false;
        navigationTeardownOwnedRvoSimulator = false;
        navigationTeardownHadRuntimeGraph = false;
        navigationTeardownRuntimeGraph = null;
        navigationTeardownRuntimeGraphReferenceCaptured = false;
        navigationTeardownAstarHost = null;
        navigationTeardownRvoSimulator = null;
        navigationTeardownAstarHostReferenceCaptured = false;
        navigationTeardownRvoReferenceCaptured = false;
        navigationTeardownAuditAttempts = 0;
        navigationTeardownAuditStartedFrame = -1;
        navigationTeardownAuditDeadlineFrame = -1;
        navigationTeardownAuditGeneration = 0;
        navigationTeardownAuditLastDetail = string.Empty;
    }

    private bool ReleaseRuntimeNavigationGraph(AstarPath astar, string reason)
    {
        if (runtimeNavigationGraph == null) return true;
        try
        {
            if (!TryIsRuntimeNavigationGraphAttached(astar, out bool attached))
            {
                log.LogWarning("Vektor Kill House navigation graph release deferred: reason=" + reason +
                               ", graph attachment could not be inspected.");
                return false;
            }
            if (!attached)
            {
                runtimeNavigationGraph = null;
                log.LogInfo("Vektor Kill House navigation graph was already detached: reason=" + reason + ".");
                return true;
            }

            bool removed = astar.data.RemoveGraph(runtimeNavigationGraph);
            if (!TryIsRuntimeNavigationGraphAttached(astar, out bool afterAttached))
            {
                log.LogWarning("Vektor Kill House navigation graph release deferred after removal attempt: reason=" +
                               reason + ", removed=" + removed + ", attachment could not be reinspected.");
                return false;
            }
            bool passed = !afterAttached;
            log.LogInfo("Vektor Kill House navigation graph released: reason=" + reason +
                        ", removed=" + removed + ", stillAttached=" + afterAttached + ".");
            if (passed) runtimeNavigationGraph = null;
            return passed;
        }
        catch (Exception exception)
        {
            log.LogWarning("Vektor Kill House navigation graph release warning: " + exception.Message);
            return false;
        }
    }

    private bool TryIsRuntimeNavigationGraphAttached(AstarPath astar, out bool attached)
    {
        attached = false;
        if (astar == null || astar.data == null || astar.data.graphs == null) return false;
        foreach (Pathfinding.NavGraph graph in astar.data.graphs)
        {
            if (graph != runtimeNavigationGraph) continue;
            attached = true;
            break;
        }
        return true;
    }

    private bool ReleaseRuntimeNavigation(string reason)
    {
        if (!ReleaseRuntimeNavigationGraph(runtimeNavigationAstar, reason)) return false;

        bool passed = true;
        bool rvoOwnedByAstarHost = runtimeOwnsAstarHost && runtimeAstarHost != null &&
                                   runtimeRvoSimulator != null &&
                                   runtimeRvoSimulator.gameObject == runtimeAstarHost;
        if (runtimeOwnsRvoSimulator && runtimeRvoSimulator != null && !rvoOwnedByAstarHost)
        {
            try
            {
                UnityEngine.Object.Destroy(runtimeRvoSimulator);
                runtimeRvoSimulator = null;
                runtimeOwnsRvoSimulator = false;
            }
            catch (Exception exception)
            {
                passed = false;
                log?.LogWarning("Vektor Kill House RVO release warning: " + exception.Message);
            }
        }
        if (runtimeOwnsAstarHost && runtimeAstarHost != null)
        {
            try
            {
                runtimeAstarHost.SetActive(false);
                UnityEngine.Object.Destroy(runtimeAstarHost);
                runtimeAstarHost = null;
                runtimeOwnsAstarHost = false;
                if (rvoOwnedByAstarHost)
                {
                    runtimeRvoSimulator = null;
                    runtimeOwnsRvoSimulator = false;
                }
            }
            catch (Exception exception)
            {
                passed = false;
                log?.LogWarning("Vektor Kill House Astar host release warning: " + exception.Message);
            }
        }
        else if (runtimeAstarHost == null) runtimeOwnsAstarHost = false;

        if (!runtimeOwnsAstarHost) runtimeAstarHost = null;
        if (!runtimeOwnsRvoSimulator) runtimeRvoSimulator = null;
        if (!passed) return false;

        runtimeNavigationAstar = null;
        runtimeNavigationOwnerSceneHandle = 0;
        runtimeNavigationAstarHostInstanceId = 0;
        runtimeNavigationRvoInstanceId = 0;
        return runtimeNavigationGraph == null && runtimeAstarHost == null && runtimeRvoSimulator == null &&
               !runtimeOwnsAstarHost && !runtimeOwnsRvoSimulator;
    }

    private void ReleaseOwnedMaterials()
    {
        RestoreBoostedLaserBeamMaterials();
        foreach (Material material in runtimeMaterialsBySourceInstance.Values)
            if (material != null) UnityEngine.Object.Destroy(material);
        runtimeMaterialsBySourceInstance.Clear();
        ownedRuntimeMaterialIds.Clear();
    }

    private void AttemptRuntimeContractFailureStep(string name, Action step, List<string> errors)
    {
        try
        {
            step();
        }
        catch (Exception exception)
        {
            try
            {
                errors.Add(name + "=" + exception.GetType().Name + ":" + exception.Message);
            }
            catch
            {
                // The failure state is already authoritative; diagnostics must never undo it.
            }
            try
            {
                log?.LogError("Vektor Kill House failure-publication step failed: step=" + name +
                              ", exception=" + exception + ".");
            }
            catch
            {
                // Logging is best effort inside the no-throw failure path.
            }
        }
    }

    private void EnsureRuntimeContractFailureMarker(Scene scene, GameObject root, string markerName,
        ref GameObject ownedMarker)
    {
        if (ownedMarker != null) return;
        if (!scene.IsValid() || !scene.isLoaded)
            throw new InvalidOperationException("failure marker scene is not loaded");
        Transform[] matching = FindRuntimeContractMarkers(scene)
            .Where(item => string.Equals(item.name, markerName, StringComparison.Ordinal)).ToArray();
        if (matching.Length > 1)
            throw new InvalidOperationException("failure marker is duplicated: " + markerName +
                                                " count=" + matching.Length);
        if (matching.Length == 1) return;

        // Retain ownership as soon as allocation succeeds. If parenting or scene movement throws,
        // unload can still retire the partially published object instead of losing the reference.
        ownedMarker = new GameObject(markerName);
        if (root != null) ownedMarker.transform.SetParent(root.transform, false);
        else SceneManager.MoveGameObjectToScene(ownedMarker, scene);
    }

    private void MarkFailure(Scene scene, GameObject root, string reason)
    {
        runtimeContractState = RuntimeContractState.Failed;
        applyNotBeforeFrame = -1;
        aiSightOcclusionPending = false;
        aiSightOcclusionPassed = false;
        opticAuditPending = false;
        nextOpticIdentityProbeFrame = -1;

        var errors = new List<string>();
        AttemptRuntimeContractFailureStep("capture-scene-owner", () =>
        {
            if (scene.IsValid() && scene.isLoaded) runtimeContractSceneHandle = scene.handle;
        }, errors);
        AttemptRuntimeContractFailureStep("retire-runtime-ready", () =>
            RetireOwnedRuntimeContractMarker(ref ownedRuntimeReadyMarker, "failure"), errors);
        AttemptRuntimeContractFailureStep("retire-framework-ready", () =>
            RetireOwnedRuntimeContractMarker(ref ownedFrameworkReadyMarker, "failure"), errors);
        AttemptRuntimeContractFailureStep("publish-runtime-failure", () =>
            EnsureRuntimeContractFailureMarker(scene, root, FailureMarkerName,
                ref ownedRuntimeFailureMarker), errors);
        AttemptRuntimeContractFailureStep("publish-framework-failure", () =>
            EnsureRuntimeContractFailureMarker(scene, root, ModdedOperationsFailureMarkerName,
                ref ownedFrameworkFailureMarker), errors);
        AttemptRuntimeContractFailureStep("deactivate-runtime-root", () =>
        {
            if (root != null) root.SetActive(false);
        }, errors);
        AttemptRuntimeContractFailureStep("restore-warehouse-lighting", RestoreWarehouseOnlyLighting, errors);
        AttemptRuntimeContractFailureStep("restore-weapon-illumination", () =>
            RestoreWeaponIlluminationBoosts(), errors);
        AttemptRuntimeContractFailureStep("restore-global-flashlight", RestoreGlobalFlashlightMultiplier, errors);

        try
        {
            runtimeContractFailurePublicationErrors = errors.Count == 0
                ? string.Empty
                : string.Join(" | ", errors);
        }
        catch
        {
            runtimeContractFailurePublicationErrors = "failure-diagnostic-formatting-failed";
        }
        try
        {
            string sceneName = scene.IsValid() ? scene.name : "<invalid>";
            log?.LogError("Vektor Kill House runtime gate failed closed: scene=" + sceneName +
                          ", reason=" + reason + ", publicationErrors=[" +
                          runtimeContractFailurePublicationErrors + "].");
        }
        catch
        {
            // The method is intentionally no-throw after committing the failure state.
        }
    }

    private static bool IsKillHouseScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return false;
        return !string.IsNullOrEmpty(scene.path) && ScenePaths.Contains(scene.path);
    }

    private static GameObject FindOwnedRoot(Scene scene)
    {
        GameObject[] matches = scene.GetRootGameObjects().Where(root => root != null &&
            root.GetComponentsInChildren<Transform>(true).Any(item => string.Equals(item.name, MapMarker, StringComparison.Ordinal))).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static Scene FindLoadedSceneByHandle(int handle)
    {
        for (int index = 0; index < SceneManager.sceneCount; index++)
        {
            Scene scene = SceneManager.GetSceneAt(index);
            if (scene.handle == handle) return scene;
        }
        return default;
    }

    private static NativeMaterialProfile P(float baseValue, float metallic, float smoothness, float normalScale,
        bool hasNormal = true, bool hasMask = true, float emissiveIntensity = 0f, bool hasEmissiveMap = false,
        bool matteArchitectural = false, string residentShaderName = ResidentShaderName,
        float baseGreen = -1f, float baseBlue = -1f)
    {
        return new NativeMaterialProfile(baseValue, metallic, smoothness, normalScale,
            hasNormal, hasMask, emissiveIntensity, hasEmissiveMap, matteArchitectural, residentShaderName,
            baseGreen, baseBlue);
    }

    private static FurnitureSurfaceProfile Fsp(float metallicRemapMax, float smoothnessRemapMax,
        float aoRemapMin, float aoRemapMax, float occlusionStrength, float receivesSsr = 1f)
    {
        return new FurnitureSurfaceProfile(metallicRemapMax, smoothnessRemapMax, aoRemapMin,
            aoRemapMax, occlusionStrength, receivesSsr);
    }

    private sealed class BoostedLaserBeamState
    {
        public readonly LineRenderer Renderer;
        public readonly Material Original;
        public readonly Material Boosted;

        public BoostedLaserBeamState(LineRenderer renderer, Material original, Material boosted)
        {
            Renderer = renderer;
            Original = original;
            Boosted = boosted;
        }
    }

    private sealed class HwsReticleSizeState
    {
        public readonly HWSReticleBrightness Owner;
        public readonly Renderer Renderer;
        public readonly Material Original;
        public readonly Material Boosted;
        public readonly float Baseline;
        public readonly float Target;

        public HwsReticleSizeState(HWSReticleBrightness owner, Renderer renderer, Material original,
            Material boosted, float baseline, float target)
        {
            Owner = owner;
            Renderer = renderer;
            Original = original;
            Boosted = boosted;
            Baseline = baseline;
            Target = target;
        }
    }

    private sealed class HwsReticleBoostState
    {
        public readonly HWSReticleBrightness Reticle;
        public readonly float[] Normal;
        public readonly float[] Nvg;
        public readonly bool HasRecognizedVanillaRange;

        public HwsReticleBoostState(HWSReticleBrightness reticle)
        {
            Reticle = reticle;
            int count = reticle == null || reticle.reticleSettings == null ? 0 : reticle.reticleSettings.Length;
            Normal = new float[count];
            Nvg = new float[count];
            for (int index = 0; index < count; index++)
            {
                ReticleSetting setting = reticle.reticleSettings[index];
                if (setting == null) continue;
                Normal[index] = setting.ReticleBrightness;
                Nvg[index] = setting.ReticleBrightness_NVG;
            }
            HasRecognizedVanillaRange = count == 11 &&
                Mathf.Abs(Normal[0] - 1f) <= .01f && Mathf.Abs(Normal[10] - 1920f) <= .01f &&
                Mathf.Abs(Nvg[0] - 25f) <= .01f && Mathf.Abs(Nvg[10] - 900f) <= .01f;
        }
    }

    private sealed class VisibleIrLaserBoostState
    {
        public readonly IRLaserLight Controller;
        public readonly float MinBrightness;
        public readonly float MaxBrightness;
        public readonly Light Light;
        public readonly float LightIntensity;
        public readonly HDAdditionalLightData LightData;
        public readonly float LightDataIntensity;

        public VisibleIrLaserBoostState(IRLaserLight controller)
        {
            Controller = controller;
            MinBrightness = controller == null ? 0f : controller.minBrighness;
            MaxBrightness = controller == null ? 0f : controller.maxBrighness;
            Light = controller == null ? null : controller._light;
            LightIntensity = Light == null ? 0f : Light.intensity;
            LightData = controller == null ? null : controller._lightData;
            LightDataIntensity = LightData == null ? 0f : LightData.intensity;
        }
    }

    private sealed class VisibleLaserLightBoostState
    {
        public readonly Light Light;
        public readonly float LightIntensity;
        public readonly HDAdditionalLightData LightData;
        public readonly float LightDataIntensity;

        public VisibleLaserLightBoostState(Light light, HDAdditionalLightData lightData)
        {
            Light = light;
            LightIntensity = light == null ? 0f : light.intensity;
            LightData = lightData;
            LightDataIntensity = lightData == null ? 0f : lightData.intensity;
        }
    }

    private sealed class SuspendedDirectionalLight
    {
        public readonly Light Light;
        public readonly bool Enabled;
        public readonly float Intensity;
        public readonly LightShadows Shadows;

        public SuspendedDirectionalLight(Light light, bool enabled, float intensity, LightShadows shadows)
        {
            Light = light;
            Enabled = enabled;
            Intensity = intensity;
            Shadows = shadows;
        }
    }

    private sealed class NativeMaterialProfile
    {
        public readonly Color BaseColor;
        public readonly float Metallic;
        public readonly float Smoothness;
        public readonly float NormalScale;
        public readonly bool HasNormal;
        public readonly bool HasMask;
        public readonly bool HasEmissiveMap;
        public readonly float EmissiveIntensity;
        public readonly bool MatteArchitectural;
        public readonly string ResidentShaderName;

        public NativeMaterialProfile(float baseValue, float metallic, float smoothness, float normalScale,
            bool hasNormal, bool hasMask, float emissiveIntensity, bool hasEmissiveMap, bool matteArchitectural,
            string residentShaderName, float baseGreen, float baseBlue)
        {
            BaseColor = new Color(baseValue, baseGreen >= 0f ? baseGreen : baseValue,
                baseBlue >= 0f ? baseBlue : baseValue, 1f);
            Metallic = metallic;
            Smoothness = smoothness;
            NormalScale = normalScale;
            HasNormal = hasNormal;
            HasMask = hasMask;
            HasEmissiveMap = hasEmissiveMap || emissiveIntensity > .001f;
            EmissiveIntensity = emissiveIntensity;
            MatteArchitectural = matteArchitectural;
            ResidentShaderName = residentShaderName;
        }
    }

    private sealed class FurnitureSurfaceProfile
    {
        public readonly float MetallicRemapMax;
        public readonly float SmoothnessRemapMax;
        public readonly float AoRemapMin;
        public readonly float AoRemapMax;
        public readonly float OcclusionStrength;
        public readonly float ReceivesSsr;

        public FurnitureSurfaceProfile(float metallicRemapMax, float smoothnessRemapMax,
            float aoRemapMin, float aoRemapMax, float occlusionStrength, float receivesSsr)
        {
            MetallicRemapMax = metallicRemapMax;
            SmoothnessRemapMax = smoothnessRemapMax;
            AoRemapMin = aoRemapMin;
            AoRemapMax = aoRemapMax;
            OcclusionStrength = occlusionStrength;
            ReceivesSsr = receivesSsr;
        }
    }

    private sealed class RuntimeBoxColliderProfile
    {
        public readonly Vector3 Center;
        public readonly Vector3 Size;

        public RuntimeBoxColliderProfile(Vector3 center, Vector3 size)
        {
            Center = center;
            Size = size;
        }
    }

    private sealed class RuntimeWallFurnitureContract
    {
        public readonly string MeshName;
        public readonly string ProvenanceMarker;
        public readonly string CollisionMeshName;
        public readonly bool CollisionConvex;
        public readonly RuntimeBoxColliderProfile[] BoxColliders;
        public readonly RuntimeChildFurnitureContract[] Children;

        public RuntimeWallFurnitureContract(string meshName, string provenanceMarker, string collisionMeshName,
            bool collisionConvex, RuntimeChildFurnitureContract[] children = null)
        {
            MeshName = meshName;
            ProvenanceMarker = provenanceMarker;
            CollisionMeshName = collisionMeshName;
            CollisionConvex = collisionConvex;
            BoxColliders = Array.Empty<RuntimeBoxColliderProfile>();
            Children = children ?? Array.Empty<RuntimeChildFurnitureContract>();
        }

        public RuntimeWallFurnitureContract(string meshName, string provenanceMarker,
            params RuntimeBoxColliderProfile[] boxColliders)
        {
            MeshName = meshName;
            ProvenanceMarker = provenanceMarker;
            CollisionMeshName = string.Empty;
            CollisionConvex = false;
            BoxColliders = boxColliders ?? Array.Empty<RuntimeBoxColliderProfile>();
            Children = Array.Empty<RuntimeChildFurnitureContract>();
        }

        public RuntimeWallFurnitureContract(string meshName, string provenanceMarker,
            RuntimeBoxColliderProfile[] boxColliders, RuntimeChildFurnitureContract[] children)
        {
            MeshName = meshName;
            ProvenanceMarker = provenanceMarker;
            CollisionMeshName = string.Empty;
            CollisionConvex = false;
            BoxColliders = boxColliders ?? Array.Empty<RuntimeBoxColliderProfile>();
            Children = children ?? Array.Empty<RuntimeChildFurnitureContract>();
        }
    }

    private sealed class RuntimeCenterFurnitureContract
    {
        public readonly string Role;
        public readonly string MeshName;
        public readonly string ProvenanceMarker;
        public readonly Vector3 LocalFacingAxis;
        public readonly bool BidirectionalFacing;
        public readonly int RootLayer;
        public readonly int VertexCount;
        public readonly int IndexCount;
        public readonly RuntimeBoxColliderProfile[] BoxColliders;

        public RuntimeCenterFurnitureContract(string role, string meshName, string provenanceMarker,
            Vector3 localFacingAxis, bool bidirectionalFacing, int rootLayer, int vertexCount,
            int indexCount, params RuntimeBoxColliderProfile[] boxColliders)
        {
            Role = role;
            MeshName = meshName;
            ProvenanceMarker = provenanceMarker;
            LocalFacingAxis = localFacingAxis;
            BidirectionalFacing = bidirectionalFacing;
            RootLayer = rootLayer;
            VertexCount = vertexCount;
            IndexCount = indexCount;
            BoxColliders = boxColliders ?? Array.Empty<RuntimeBoxColliderProfile>();
        }
    }

    private sealed class RuntimeChildFurnitureContract
    {
        public readonly string Name;
        public readonly string MeshName;
        public readonly string[] MaterialSlots;
        public readonly Vector3 LocalPosition;
        public readonly Quaternion LocalRotation;
        public readonly Vector3 LocalScale;
        public readonly bool Active;
        public readonly string CollisionMeshName;
        public readonly bool CollisionEnabled;
        public readonly bool CollisionConvex;
        public readonly RuntimeBoxColliderProfile[] BoxColliders;
        public readonly RuntimeChildFurnitureContract[] Children;
        public readonly int VertexCount;
        public readonly int IndexCount;

        public RuntimeChildFurnitureContract(string name, string meshName, string[] materialSlots,
            Vector3 localPosition, Quaternion localRotation, Vector3 localScale,
            string collisionMeshName, bool collisionEnabled, bool collisionConvex,
            RuntimeBoxColliderProfile[] boxColliders, RuntimeChildFurnitureContract[] children,
            int vertexCount, int indexCount)
        {
            Name = name;
            MeshName = meshName;
            MaterialSlots = materialSlots ?? Array.Empty<string>();
            LocalPosition = localPosition;
            LocalRotation = localRotation;
            LocalScale = localScale;
            Active = true;
            CollisionMeshName = collisionMeshName ?? string.Empty;
            CollisionEnabled = collisionEnabled;
            CollisionConvex = collisionConvex;
            BoxColliders = boxColliders ?? Array.Empty<RuntimeBoxColliderProfile>();
            Children = children ?? Array.Empty<RuntimeChildFurnitureContract>();
            VertexCount = vertexCount;
            IndexCount = indexCount;
        }
    }

    private sealed class SolidWall
    {
        public readonly Transform Root;
        public readonly Collider Collider;

        public SolidWall(Transform root, Collider collider)
        {
            Root = root;
            Collider = collider;
        }
    }

    private readonly struct FileFingerprint
    {
        public readonly long Bytes;
        public readonly string Sha256;
        public FileFingerprint(long bytes, string sha256) { Bytes = bytes; Sha256 = sha256; }
    }
}

public sealed class KillHouseUpdateDriver : MonoBehaviour
{
    public static Action Tick;
    public KillHouseUpdateDriver(IntPtr ptr) : base(ptr) { }
    public KillHouseUpdateDriver() : base(ClassInjector.DerivedConstructorPointer<KillHouseUpdateDriver>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }
    private void Update() => Tick?.Invoke();
}
