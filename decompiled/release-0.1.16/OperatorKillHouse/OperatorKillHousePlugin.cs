using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BrainFailProductions.PolyFew;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem;
using Mirror;
using Pathfinding;
using Pathfinding.RVO;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace OperatorKillHouse;

[BepInPlugin("operator.vektor-killhouse", "LOT 12: FALSE WALL", "0.1.16")]
[BepInDependency("operator.modded-operations", "0.3.29")]
public sealed class OperatorKillHousePlugin : BasePlugin
{
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

		public HwsReticleSizeState(HWSReticleBrightness owner, Renderer renderer, Material original, Material boosted, float baseline, float target)
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
			int num = ((!((Object)(object)reticle == (Object)null) && reticle.reticleSettings != null) ? ((Il2CppArrayBase<ReticleSetting>)(object)reticle.reticleSettings).Length : 0);
			Normal = new float[num];
			Nvg = new float[num];
			for (int i = 0; i < num; i++)
			{
				ReticleSetting val = ((Il2CppArrayBase<ReticleSetting>)(object)reticle.reticleSettings)[i];
				if (val != null)
				{
					Normal[i] = val.ReticleBrightness;
					Nvg[i] = val.ReticleBrightness_NVG;
				}
			}
			HasRecognizedVanillaRange = num == 11 && Mathf.Abs(Normal[0] - 1f) <= 0.01f && Mathf.Abs(Normal[10] - 1920f) <= 0.01f && Mathf.Abs(Nvg[0] - 25f) <= 0.01f && Mathf.Abs(Nvg[10] - 900f) <= 0.01f;
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
			MinBrightness = (((Object)(object)controller == (Object)null) ? 0f : controller.minBrighness);
			MaxBrightness = (((Object)(object)controller == (Object)null) ? 0f : controller.maxBrighness);
			Light = (((Object)(object)controller == (Object)null) ? null : controller._light);
			LightIntensity = (((Object)(object)Light == (Object)null) ? 0f : Light.intensity);
			LightData = (((Object)(object)controller == (Object)null) ? null : controller._lightData);
			LightDataIntensity = (((Object)(object)LightData == (Object)null) ? 0f : LightData.intensity);
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
			LightIntensity = (((Object)(object)light == (Object)null) ? 0f : light.intensity);
			LightData = lightData;
			LightDataIntensity = (((Object)(object)lightData == (Object)null) ? 0f : lightData.intensity);
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
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Unknown result type (might be due to invalid IL or missing references)
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

		public NativeMaterialProfile(float baseValue, float metallic, float smoothness, float normalScale, bool hasNormal, bool hasMask, float emissiveIntensity, bool hasEmissiveMap, bool matteArchitectural, string residentShaderName, float baseGreen, float baseBlue)
		{
			//IL_0029: Unknown result type (might be due to invalid IL or missing references)
			//IL_002e: Unknown result type (might be due to invalid IL or missing references)
			BaseColor = new Color(baseValue, (baseGreen >= 0f) ? baseGreen : baseValue, (baseBlue >= 0f) ? baseBlue : baseValue, 1f);
			Metallic = metallic;
			Smoothness = smoothness;
			NormalScale = normalScale;
			HasNormal = hasNormal;
			HasMask = hasMask;
			HasEmissiveMap = hasEmissiveMap || emissiveIntensity > 0.001f;
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

		public FurnitureSurfaceProfile(float metallicRemapMax, float smoothnessRemapMax, float aoRemapMin, float aoRemapMax, float occlusionStrength, float receivesSsr)
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
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_000e: Unknown result type (might be due to invalid IL or missing references)
			//IL_000f: Unknown result type (might be due to invalid IL or missing references)
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

		public RuntimeWallFurnitureContract(string meshName, string provenanceMarker, string collisionMeshName, bool collisionConvex, RuntimeChildFurnitureContract[] children = null)
		{
			MeshName = meshName;
			ProvenanceMarker = provenanceMarker;
			CollisionMeshName = collisionMeshName;
			CollisionConvex = collisionConvex;
			BoxColliders = Array.Empty<RuntimeBoxColliderProfile>();
			Children = children ?? Array.Empty<RuntimeChildFurnitureContract>();
		}

		public RuntimeWallFurnitureContract(string meshName, string provenanceMarker, params RuntimeBoxColliderProfile[] boxColliders)
		{
			MeshName = meshName;
			ProvenanceMarker = provenanceMarker;
			CollisionMeshName = string.Empty;
			CollisionConvex = false;
			BoxColliders = boxColliders ?? Array.Empty<RuntimeBoxColliderProfile>();
			Children = Array.Empty<RuntimeChildFurnitureContract>();
		}

		public RuntimeWallFurnitureContract(string meshName, string provenanceMarker, RuntimeBoxColliderProfile[] boxColliders, RuntimeChildFurnitureContract[] children)
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

		public RuntimeCenterFurnitureContract(string role, string meshName, string provenanceMarker, Vector3 localFacingAxis, bool bidirectionalFacing, int rootLayer, int vertexCount, int indexCount, params RuntimeBoxColliderProfile[] boxColliders)
		{
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Unknown result type (might be due to invalid IL or missing references)
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

		public RuntimeChildFurnitureContract(string name, string meshName, string[] materialSlots, Vector3 localPosition, Quaternion localRotation, Vector3 localScale, string collisionMeshName, bool collisionEnabled, bool collisionConvex, RuntimeBoxColliderProfile[] boxColliders, RuntimeChildFurnitureContract[] children, int vertexCount, int indexCount)
		{
			//IL_0025: Unknown result type (might be due to invalid IL or missing references)
			//IL_0027: Unknown result type (might be due to invalid IL or missing references)
			//IL_002d: Unknown result type (might be due to invalid IL or missing references)
			//IL_002f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0035: Unknown result type (might be due to invalid IL or missing references)
			//IL_0037: Unknown result type (might be due to invalid IL or missing references)
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

	private readonly struct FileFingerprint(long bytes, string sha256)
	{
		public readonly long Bytes = bytes;

		public readonly string Sha256 = sha256;
	}

	public const string PluginGuid = "operator.vektor-killhouse";

	public const string PluginName = "LOT 12: FALSE WALL";

	public const string PluginVersion = "0.1.16";

	private const string ExactUnityVersion = "6000.3.8f1";

	private const float IndoorFlashlightMultiplier = 6f;

	private const float IndoorVisibleLaserMultiplier = 6f;

	private const float IndoorVisibleLaserBeamEmissionMultiplier = 4f;

	private const float IndoorReticleNormalBrightnessMultiplier = 2f;

	private const float IndoorReticleNvgBrightnessMultiplier = 1f;

	private const float IndoorReticleNormalBrightnessCap = 3840f;

	private const float IndoorReticleNvgBrightnessCap = 900f;

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

	private const string DoorShellName = "NATIVE_DOORV2_SHELL";

	private const string DoorAudioBankName = "NATIVE_DOORV2_AUDIO_BANK";

	private const int NativeOpaqueRenderQueue = 2225;

	private const float DoorwayOpeningTangentOffset = -0.0391f;

	private const float DoorHingeToLeafCenter = 0.50283f;

	private const float DoorCenterTolerance = 0.035f;

	private const float WarehouseRoofHeight = 11.35f;

	private const float WarehouseFixtureHeight = 6.8f;

	private const float WarehouseGroundElevation = -0.015f;

	private const float WarehouseGroundSourceWidth = 5.425254f;

	private const float WarehouseGroundSourceDepth = 4.129904f;

	private const float WarehouseGroundMinimumApron = 3.98f;

	private const float NavigationNodeSize = 0.4f;

	private const float NavigationMarkerClearance = 1.35f;

	private const float CenterRoomFurnitureWallInset = 0.82f;

	private const float CenterRoomTacticalCapsuleRadius = 0.3f;

	private const int ApplyDelayFrames = 2;

	private const int OpticIdentityProbeFrames = 120;

	private const uint OfficialDoorV2AssetId = 3964291274u;

	private static readonly string[] ExactDonorShaderPasses = new string[6] { "DistortionVectors", "MOTIONVECTORS", "TransparentDepthPrepass", "TransparentDepthPostpass", "TransparentBackface", "RayTracingPrepass" };

	private static readonly string[] ExactDonorOverrideTags = new string[1] { "MotionVector" };

	private static readonly HashSet<string> ScenePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assets/VektorKillHouse/Scenes/KH01_CircuitHouse.unity", "Assets/VektorKillHouse/Scenes/KH02_OffsetFigureEight.unity", "Assets/VektorKillHouse/Scenes/KH03_SerpentineApartment.unity", "Assets/VektorKillHouse/Scenes/KH04_CourtyardRing.unity", "Assets/VektorKillHouse/Scenes/KH05_SplitSpine.unity", "Assets/VektorKillHouse/Scenes/KH06_CompressedGrid.unity", "Assets/VektorKillHouse/Scenes/KH07_BrokenDiamond.unity", "Assets/VektorKillHouse/Scenes/KH08_DoubleBack.unity", "Assets/VektorKillHouse/Scenes/KH09_Pinwheel.unity", "Assets/VektorKillHouse/Scenes/KH10_WideLabyrinth.unity" };

	private static readonly IReadOnlyDictionary<string, NativeMaterialProfile> NativeMaterialProfiles = new Dictionary<string, NativeMaterialProfile>(StringComparer.Ordinal)
	{
		["Bed"] = P(115f / 136f, 1f, 0.5f, 1f),
		["PillowLarge"] = P(0.88235295f, 1f, 0.224f, 1f),
		["PillowSmall"] = P(31f / 34f, 1f, 0.234f, 1f),
		["Bedroom_Closets"] = P(1f, 1f, 0.5f, 1f),
		["Carpet_B"] = P(1f, 0f, 0.124f, 1f),
		["ChipBoardShader"] = P(0.8f, 0f, 0.18f, 1.35f, hasNormal: true, hasMask: true, 0f, hasEmissiveMap: false, matteArchitectural: true),
		["Couch_Fabric"] = P(121f / 136f, 1f, 0.5f, 1f, hasNormal: true, hasMask: true, 0f, hasEmissiveMap: false, matteArchitectural: false, "MilkShaders/Lit-Template"),
		["Door_Breached"] = P(0.79073066f, 0f, 0.14142138f, 1f),
		["MI_DoorsWindows"] = P(1f, 0f, 0.28f, 1f),
		["Fireplace"] = P(1f, 1f, 0.5f, 1f),
		["Floor"] = P(1f, 0f, 0.5f, 1f),
		["In_Floor_Basement"] = P(1f, 0f, 0.14f, 0.8f, hasNormal: true, hasMask: true, 0f, hasEmissiveMap: false, matteArchitectural: true, "MilkShaders/Lit-Template"),
		["In_Floor_Carpet"] = P(131f / 136f, 0f, 0f, 1f, hasNormal: true, hasMask: false),
		["Kitchen_Cabinet_Marble"] = P(1f, 1f, 0.5f, 1f, hasNormal: false),
		["Kitchen_Cabinet_Wood"] = P(1f, 1f, 0.5f, 1f),
		["Kitchen_TableChair"] = P(0.83823526f, 1f, 0.5f, 1f, hasNormal: true, hasMask: true, 0f, hasEmissiveMap: false, matteArchitectural: false, "MilkShaders/Lit-Template"),
		["Lamps_C_on__cagville"] = P(1f, 0.08f, 0.22f, 1f, hasNormal: true, hasMask: true, 307.2f, hasEmissiveMap: true),
		["RM_Steel_smooth"] = P(0.58431375f, 0.12f, 0.33f, 0f, hasNormal: false, hasMask: true, 0f, hasEmissiveMap: false, matteArchitectural: false, "HDRP/Lit", 0.6156863f, 0.6431373f),
		["PlyWoodShader"] = P(0.8f, 0f, 0.18f, 1.35f, hasNormal: true, hasMask: true, 0f, hasEmissiveMap: false, matteArchitectural: true),
		["Toilet_House"] = P(1f, 1f, 0.5f, 1f),
		["WorkDesk"] = P(1f, 1f, 0.5f, 1f)
	};

	private static readonly IReadOnlyDictionary<string, string> ExpectedBaseTextureNames = new Dictionary<string, string>(StringComparer.Ordinal)
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

	private static readonly IReadOnlyDictionary<string, string> ExpectedNormalTextureNames = new Dictionary<string, string>(StringComparer.Ordinal)
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

	private static readonly IReadOnlyDictionary<string, string> ExpectedMaskTextureNames = new Dictionary<string, string>(StringComparer.Ordinal)
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

	private static readonly IReadOnlyDictionary<string, string> ExpectedEmissiveTextureNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["Lamps_C_on__cagville"] = "Lamps_C_Emissive" };

	private static readonly IReadOnlyDictionary<string, string> ExpectedDetailTextureNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["In_Floor_Basement"] = "Concrete1_DetailMap" };

	private static readonly IReadOnlyDictionary<string, string[]> FurnitureMaterialSlotsByMesh = new Dictionary<string, string[]>(StringComparer.Ordinal)
	{
		["Bed_queen"] = new string[1] { "Bed" },
		["Pillow_small"] = new string[1] { "PillowSmall" },
		["Pillow_large"] = new string[1] { "PillowLarge" },
		["Bookshelf"] = new string[1] { "Fireplace" },
		["Couch_2seat"] = new string[1] { "Couch_Fabric" },
		["Kitcabinet_full_fridge"] = new string[1] { "Kitchen_Cabinet_Wood" },
		["Kitcabinet_low_1x_A"] = new string[2] { "Kitchen_Cabinet_Wood", "Kitchen_Cabinet_Marble" },
		["Kitchen_table_large"] = new string[1] { "Kitchen_TableChair" },
		["Sidetable_A"] = new string[1] { "Bedroom_Closets" },
		["Sidetable_A_drawer"] = new string[1] { "Bedroom_Closets" },
		["T_sink_door_L"] = new string[1] { "Bedroom_Closets" },
		["T_sink_door_R"] = new string[1] { "Bedroom_Closets" },
		["T_sink"] = new string[2] { "Bedroom_Closets", "Toilet_House" },
		["T_toilet"] = new string[1] { "Toilet_House" },
		["T_toilet_lid"] = new string[1] { "Toilet_House" },
		["T_toilet_seat"] = new string[1] { "Toilet_House" },
		["Workdesk_door_L"] = new string[1] { "WorkDesk" },
		["Workdesk_door_R"] = new string[1] { "WorkDesk" },
		["Workdesk_solo"] = new string[1] { "WorkDesk" }
	};

	private static readonly HashSet<string> FurnitureMaterialProfileNames = new HashSet<string>(StringComparer.Ordinal)
	{
		"Bed", "PillowLarge", "PillowSmall", "Bedroom_Closets", "Couch_Fabric", "Fireplace", "Kitchen_Cabinet_Marble", "Kitchen_Cabinet_Wood", "Kitchen_TableChair", "Toilet_House",
		"WorkDesk"
	};

	private static readonly IReadOnlyDictionary<string, FurnitureSurfaceProfile> FurnitureSurfaceProfiles = new Dictionary<string, FurnitureSurfaceProfile>(StringComparer.Ordinal)
	{
		["Bed"] = Fsp(1f, 1f, 0f, 1f, 1f),
		["PillowLarge"] = Fsp(1f, 1f, 0f, 1f, 1f),
		["PillowSmall"] = Fsp(1f, 1f, 0f, 1f, 1f),
		["Bedroom_Closets"] = Fsp(0f, 0.22741756f, 0f, 1f, 0.919f),
		["Couch_Fabric"] = Fsp(0.33685863f, 0.588395f, 0f, 1f, 0.3f),
		["Fireplace"] = Fsp(1f, 0.541779f, 0.5f, 1f, 0.5f),
		["Kitchen_Cabinet_Marble"] = Fsp(0f, 0.62526333f, 0f, 1f, 1f),
		["Kitchen_Cabinet_Wood"] = Fsp(0f, 0.29595914f, 0f, 1f, 1f),
		["Kitchen_TableChair"] = Fsp(1f, 0.5f, 0f, 1f, 0.5f, 0f),
		["Toilet_House"] = Fsp(1f, 0.83155084f, 0f, 1f, 0.814f),
		["WorkDesk"] = Fsp(0f, 0.69251335f, 0f, 1f, 1f)
	};

	private static readonly IReadOnlyDictionary<string, FileFingerprint> ExactFiles = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase)
	{
		["OPERATOR.exe"] = new FileFingerprint(667648L, "F8158D7939937CB26C2DBEF0A127E82F969E5CC72BE58E62BB78F39B179FF53D"),
		["GameAssembly.dll"] = new FileFingerprint(115185152L, "D4347448524D79A7E367F2B22D66BB3A21E1F3733D32ABA37FC7E3E270A620DE"),
		["UnityPlayer.dll"] = new FileFingerprint(35734960L, "D935627D3AC843293F1C51EE9D85538191F0CB98DEF4271C1A29302B52A021D4")
	};

	private readonly Dictionary<int, Material> runtimeMaterialsBySourceInstance = new Dictionary<int, Material>();

	private readonly HashSet<int> ownedRuntimeMaterialIds = new HashSet<int>();

	private readonly Dictionary<int, SuspendedDirectionalLight> suspendedDirectionalLights = new Dictionary<int, SuspendedDirectionalLight>();

	private ManualLogSource log;

	private GameObject driverObject;

	private UnityAction<Scene, LoadSceneMode> sceneLoadedCallback;

	private UnityAction<Scene> sceneUnloadedCallback;

	private int pendingSceneHandle;

	private int applyNotBeforeFrame = -1;

	private GridGraph runtimeNavigationGraph;

	private AstarPath runtimeNavigationAstar;

	private GameObject runtimeAstarHost;

	private RVOSimulator runtimeRvoSimulator;

	private bool runtimeOwnsAstarHost;

	private int runtimeNavigationOwnerSceneHandle;

	private int runtimeNavigationAstarHostInstanceId;

	private int runtimeNavigationRvoInstanceId;

	private bool runtimeOwnsRvoSimulator;

	private int pendingNavigationTeardownAuditFrame = -1;

	private int navigationTeardownSceneHandle;

	private int navigationTeardownAstarHostInstanceId;

	private int navigationTeardownRvoInstanceId;

	private bool navigationTeardownOwnedAstarHost;

	private bool navigationTeardownOwnedRvoSimulator;

	private bool navigationTeardownHadRuntimeGraph;

	private bool exactEnvironmentAccepted;

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

	private static readonly HashSet<string> ForbiddenStandaloneFurnitureMeshes = new HashSet<string>(StringComparer.Ordinal) { "Book_set", "Book_set_bookshelf_A", "Sofa_A", "Sofa_B", "T_bathtub", "D_TV_standing" };

	private static readonly HashSet<string> FurnitureMeshesWithoutVertexColor = new HashSet<string>(StringComparer.Ordinal) { "Pillow_small", "Pillow_large", "Sidetable_A_drawer", "T_sink_door_L", "T_sink_door_R", "T_toilet_lid", "T_toilet_seat", "Workdesk_door_L", "Workdesk_door_R" };

	private static readonly IReadOnlyDictionary<string, RuntimeWallFurnitureContract> RuntimeWallFurnitureContracts = new Dictionary<string, RuntimeWallFurnitureContract>(StringComparer.Ordinal)
	{
		["Bed_queen"] = new RuntimeWallFurnitureContract("Bed_queen", "WALL_BACKED_PROP_PROVENANCE_level4_GO360_MF20235_MR15441_sharedassets4_Mesh1141_MR_SHA256_ae0fb43c7902b028d279f8b4fd91d9cb0ab9603055f1093188ac6cf702dce51a", "Bed_COL", collisionConvex: false, new RuntimeChildFurnitureContract[4]
		{
			RuntimeChild("Pillow_small", "Pillow_small", new string[1] { "PillowSmall" }, new Vector3(0.307f, 0.707f, -0.645f), new Quaternion(0.25153852f, -0.058406256f, -0.012960499f, 0.9659965f), "PillowSmall_COL", collisionEnabled: true, collisionConvex: false, null, null, 233, 768),
			RuntimeChild("Pillow_small", "Pillow_small", new string[1] { "PillowSmall" }, new Vector3(-0.18f, 0.707f, -0.65f), new Quaternion(0.31748056f, 0.09755653f, -0.053890042f, 0.9416925f), "PillowSmall_COL", collisionEnabled: true, collisionConvex: false, null, null, 233, 768),
			RuntimeChild("Pillow_large", "Pillow_large", new string[1] { "PillowLarge" }, new Vector3(-0.30999994f, 0.60899997f, -0.89799976f), new Quaternion(0.063810684f, -0.005018107f, -0.022382082f, 0.99769837f), "PillowLarge_COL", collisionEnabled: true, collisionConvex: false, null, null, 334, 1332),
			RuntimeChild("Pillow_large", "Pillow_large", new string[1] { "PillowLarge" }, new Vector3(0.404f, 0.62100005f, -0.858f), new Quaternion(0.063628756f, 0.006954471f, -0.012878145f, 0.99786633f), "PillowLarge_COL", collisionEnabled: true, collisionConvex: false, null, null, 334, 1332)
		}),
		["Bookshelf"] = new RuntimeWallFurnitureContract("Bookshelf", "WALL_BACKED_PROP_PROVENANCE_level4_GO2136_MF21517_MR16725_sharedassets4_Mesh721_MR_SHA256_6bb929cf8371caaed03e9a1662c45cd0f65ca416b045418fee9acf1aa07e226c", "BookShelf_COL", collisionConvex: false),
		["Kitcabinet_full_fridge"] = new RuntimeWallFurnitureContract("Kitcabinet_full_fridge", "WALL_BACKED_PROP_PROVENANCE_level4_GO2480_MF21764_MR16972_sharedassets4_Mesh1094_MR_SHA256_efadbb90c344d9c9e6caa3fbebbb0fe84c2de26780eed97ade67bde4aabdbf46", RuntimeBox(new Vector3(0.4f, 1.1859446f, 0.3384462f), new Vector3(0.8f, 2.382934f, 0.6791087f)), RuntimeBox(new Vector3(1.48f, 1.1859446f, 0.3384462f), new Vector3(0.1f, 2.382934f, 0.6791087f)), RuntimeBox(new Vector3(0.7495269f, 2.14f, 0.3384462f), new Vector3(1.6002039f, 0.47f, 0.6791087f))),
		["Kitcabinet_low_1x_A"] = new RuntimeWallFurnitureContract("Kitcabinet_low_1x_A", "WALL_BACKED_PROP_PROVENANCE_level4_GO1365_MF20950_MR16158_sharedassets4_Mesh1129_MR_SHA256_b0fa7469554a42d5da1ddcae9de8bb08183ae28e9ea4fb5445a8b2bbde7b8e2a", RuntimeBox(new Vector3(0.50000006f, 0.45941916f, 0.33795837f), new Vector3(1.0000004f, 0.92988235f, 0.67591673f))),
		["Sidetable_A"] = new RuntimeWallFurnitureContract("Sidetable_A", "WALL_BACKED_PROP_PROVENANCE_level4_GO680_MF20466_MR15674_sharedassets4_Mesh1179_MR_SHA256_91c01d42fba3b158a452794ca9251f19c8bca785cc17daf3120d587471561b55", "SideTable_A_COL", collisionConvex: true, new RuntimeChildFurnitureContract[1] { RuntimeChild("Sidetable_A_drawer", "Sidetable_A_drawer", new string[1] { "Bedroom_Closets" }, new Vector3(-6.561279E-06f, 0.5494517f, 0.02767208f), Quaternion.identity, "Drawer_SideTableA_COL", collisionEnabled: false, collisionConvex: true, null, null, 439, 1068) }),
		["T_sink"] = new RuntimeWallFurnitureContract("T_sink", "WALL_BACKED_PROP_PROVENANCE_level4_GO785_MF20544_MR15751_sharedassets4_Mesh890_MR_SHA256_8175fa8e22445eb202c17c40051ed7c91404653e4732804644cdbea973fe8fe7", "T_Sink_COL", collisionConvex: false, new RuntimeChildFurnitureContract[2]
		{
			RuntimeChild("T_sink_door_L (1)", "T_sink_door_L", new string[1] { "Bedroom_Closets" }, new Vector3(0.46655378f, 0.46642563f, 0.5314964f), new Quaternion(0f, 2.38419E-07f, 0f, 1f), null, collisionEnabled: true, collisionConvex: false, new RuntimeBoxColliderProfile[1] { RuntimeBox(new Vector3(-0.23099838f, 0f, 0.014692759f), new Vector3(0.4704434f, 0.6842715f, 0.0506542f)) }, null, 290, 690),
			RuntimeChild("T_sink_door_R (1)", "T_sink_door_R", new string[1] { "Bedroom_Closets" }, new Vector3(-0.46917388f, 0.46642563f, 0.5314964f), new Quaternion(0f, 2.38419E-07f, 8.7423E-08f, 1f), null, collisionEnabled: true, collisionConvex: false, new RuntimeBoxColliderProfile[1] { RuntimeBox(new Vector3(0.23210377f, 4.61936E-07f, 0.014692919f), new Vector3(0.47044343f, 0.6842716f, 0.05065419f)) }, null, 282, 690)
		}),
		["T_toilet"] = new RuntimeWallFurnitureContract("T_toilet", "WALL_BACKED_PROP_PROVENANCE_level4_GO1702_MF21188_MR16396_sharedassets4_Mesh1052_MR_SHA256_d04bc81fc4fd15a7b8222df3eefbcf3f5709e55142026fb6f6031d6584f72609", new RuntimeBoxColliderProfile[2]
		{
			RuntimeBox(new Vector3(-3.7252903E-08f, 0.22f, -0.006511614f), new Vector3(0.39312702f, 0.44f, 0.61581385f)),
			RuntimeBox(new Vector3(-3.7252903E-08f, 0.3703465f, -0.25f), new Vector3(0.39312702f, 0.742007f, 0.13f))
		}, new RuntimeChildFurnitureContract[1] { RuntimeChild("T_toilet_lid (1)", "T_toilet_lid", new string[1] { "Toilet_House" }, new Vector3(-0.000156097f, 0.40989417f, -0.10519142f), new Quaternion(-0.725541f, 0f, 2.9802E-08f, 0.688179f), null, collisionEnabled: true, collisionConvex: false, new RuntimeBoxColliderProfile[1] { RuntimeBox(new Vector3(0.001000941f, 0.017943554f, 0.18414833f), new Vector3(0.35535637f, 0.022520866f, 0.42543015f)) }, new RuntimeChildFurnitureContract[1] { RuntimeChild("T_toilet_seat (1)", "T_toilet_seat", new string[1] { "Toilet_House" }, Vector3.zero, new Quaternion(0.005043178f, -1.95E-08f, -6.3864E-08f, 0.9999873f), null, collisionEnabled: true, collisionConvex: false, new RuntimeBoxColliderProfile[1] { RuntimeBox(new Vector3(0f, -0.015444206f, 0.18332578f), new Vector3(0.36064148f, 0.05244905f, 0.4300352f)) }, null, 248, 1116) }, 210, 1008) }),
		["Workdesk_solo"] = new RuntimeWallFurnitureContract("Workdesk_solo", "WALL_BACKED_PROP_PROVENANCE_level4_GO2401_MF21703_MR16911_sharedassets4_Mesh673_MR_SHA256_193630ef5ef5663d7dd6f6f7fd60e1aeb90782b7301c0b07076187620fb2b048", "WorkDesk_Solo_COL", collisionConvex: false, new RuntimeChildFurnitureContract[2]
		{
			RuntimeChild("Workdesk_door_L", "Workdesk_door_L", new string[1] { "WorkDesk" }, new Vector3(-0.8347601f, 0.40908816f, 0.45750347f), Quaternion.identity, null, collisionEnabled: true, collisionConvex: false, new RuntimeBoxColliderProfile[1] { RuntimeBox(new Vector3(0.20593888f, 0f, 4.657E-09f), new Vector3(0.41081268f, 0.55097f, 0.04496894f)) }, null, 158, 300),
			RuntimeChild("Workdesk_door_R", "Workdesk_door_R", new string[1] { "WorkDesk" }, new Vector3(0.83183086f, 0.40908816f, 0.45750347f), Quaternion.identity, null, collisionEnabled: true, collisionConvex: false, new RuntimeBoxColliderProfile[1] { RuntimeBox(new Vector3(-0.20426422f, 0f, 4.657E-09f), new Vector3(0.41081268f, 0.55097f, 0.04496894f)) }, null, 158, 300)
		})
	};

	private static readonly IReadOnlyDictionary<string, RuntimeCenterFurnitureContract> RuntimeCenterFurnitureContracts = new Dictionary<string, RuntimeCenterFurnitureContract>(StringComparer.Ordinal)
	{
		["TABLE"] = new RuntimeCenterFurnitureContract("TABLE", "Kitchen_table_large", "CENTER_ROOM_PROP_PROVENANCE_LEVEL4_GO1878_KITCHEN_TABLE_LARGE_SCALE_1_EXACT_PREFAB_UV0UV1_425779D06FD5E61A6C9C4C83359DBF35D81337C963F68B7BA46B704D8A069538", Vector3.forward, true, 0, 995, 0, RuntimeBox(new Vector3(0f, 0.36249366f, -2.9802322E-08f), new Vector3(0.2f, 0.7262458f, 0.2f)), RuntimeBox(new Vector3(0f, 0.68f, -2.9802322E-08f), new Vector3(1f, 0.07f, 1.75f))),
		["SOFA"] = new RuntimeCenterFurnitureContract("SOFA", "Couch_2seat", "CENTER_ROOM_PROP_PROVENANCE_LEVEL4_GO578_COUCH_2SEAT_SCALE_1_MESH_C58D40C40D6B9F18BE6A883EA69581002C005630DF1194206A638896EF483586_UV_CF3DB25EA907E15EF421DE6FC1D68C7031D0DA923DBF529383087B2DA55B6171_PROBE_ANCHOR_GO2175_T9763_MR15599", Vector3.forward, false, 24, 844, 2286, RuntimeBox(new Vector3(0.0015151651f, 0.23126331f, 0.056184977f), new Vector3(1.5813075f, 0.4606368f, 0.80798507f)), RuntimeBox(new Vector3(0.0015150877f, 0.47571158f, -0.2326804f), new Vector3(1.5813075f, 0.94953334f, 0.23025444f)), RuntimeBox(new Vector3(0.7339685f, 0.3626925f, 0.05618478f), new Vector3(0.116401285f, 0.7234952f, 0.80798507f)), RuntimeBox(new Vector3(-0.7227199f, 0.35605446f, 0.05618517f), new Vector3(0.13283774f, 0.7102191f, 0.80798507f)))
	};

	private static RuntimeBoxColliderProfile RuntimeBox(Vector3 center, Vector3 size)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		return new RuntimeBoxColliderProfile(center, size);
	}

	private static RuntimeChildFurnitureContract RuntimeChild(string name, string meshName, string[] materialSlots, Vector3 localPosition, Quaternion localRotation, string collisionMeshName = null, bool collisionEnabled = true, bool collisionConvex = false, RuntimeBoxColliderProfile[] boxColliders = null, RuntimeChildFurnitureContract[] children = null, int vertexCount = 0, int indexCount = 0)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0004: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		return new RuntimeChildFurnitureContract(name, meshName, materialSlots, localPosition, localRotation, Vector3.one, collisionMeshName, collisionEnabled, collisionConvex, boxColliders, children, vertexCount, indexCount);
	}

	public override void Load()
	{
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Expected O, but got Unknown
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		log = ((BasePlugin)this).Log;
		exactEnvironmentAccepted = VerifyExactEnvironment();
		if (!exactEnvironmentAccepted)
		{
			log.LogError((object)"Vektor Kill House runtime disabled: the installed OPERATOR build does not match the audited donor build.");
			return;
		}
		ClassInjector.RegisterTypeInIl2Cpp<KillHouseUpdateDriver>();
		KillHouseUpdateDriver.Tick = OnDriverTick;
		driverObject = new GameObject("MOD_VektorKillHouse_ExactSceneRuntime");
		Object.DontDestroyOnLoad((Object)(object)driverObject);
		driverObject.AddComponent<KillHouseUpdateDriver>();
		sceneLoadedCallback = DelegateSupport.ConvertDelegate<UnityAction<Scene, LoadSceneMode>>((Delegate)new Action<Scene, LoadSceneMode>(OnSceneLoaded));
		sceneUnloadedCallback = DelegateSupport.ConvertDelegate<UnityAction<Scene>>((Delegate)new Action<Scene>(OnSceneUnloaded));
		SceneManager.sceneLoaded += sceneLoadedCallback;
		SceneManager.sceneUnloaded += sceneUnloadedCallback;
		for (int i = 0; i < SceneManager.sceneCount; i++)
		{
			Scene sceneAt = SceneManager.GetSceneAt(i);
			if (IsKillHouseScene(sceneAt))
			{
				OnSceneLoaded(sceneAt, (LoadSceneMode)1);
			}
		}
		AuditResidentDoorTemplates("plugin-load", force: false);
		log.LogInfo((object)"LOT 12: FALSE WALL 0.1.16 loaded; framework-selected exact-scene material and indoor GridGraph reconstruction is armed.");
	}

	public override bool Unload()
	{
		try
		{
			if ((Delegate)(object)sceneLoadedCallback != (Delegate)null)
			{
				SceneManager.sceneLoaded -= sceneLoadedCallback;
			}
			if ((Delegate)(object)sceneUnloadedCallback != (Delegate)null)
			{
				SceneManager.sceneUnloaded -= sceneUnloadedCallback;
			}
			sceneLoadedCallback = null;
			sceneUnloadedCallback = null;
			KillHouseUpdateDriver.Tick = null;
			pendingSceneHandle = 0;
			applyNotBeforeFrame = -1;
			aiSightOcclusionPending = false;
			aiSightOcclusionPassed = false;
			opticAuditPending = false;
			nextOpticIdentityProbeFrame = -1;
			equippedWeaponIdentityFingerprint = 0uL;
			equippedWeaponIdentityInitialized = false;
			pendingNavigationTeardownAuditFrame = -1;
			navigationTeardownSceneHandle = 0;
			navigationTeardownAstarHostInstanceId = 0;
			navigationTeardownRvoInstanceId = 0;
			navigationTeardownOwnedAstarHost = false;
			navigationTeardownOwnedRvoSimulator = false;
			navigationTeardownHadRuntimeGraph = false;
			RestoreWarehouseOnlyLighting();
			RestoreWeaponIlluminationBoosts();
			RestoreGlobalFlashlightMultiplier();
			ReleaseRuntimeNavigation("plugin unload");
			ReleaseOwnedMaterials();
			if ((Object)(object)driverObject != (Object)null)
			{
				Object.Destroy((Object)(object)driverObject);
			}
			driverObject = null;
			exactEnvironmentAccepted = false;
			return true;
		}
		catch (Exception ex)
		{
			ManualLogSource obj = log;
			if (obj != null)
			{
				obj.LogError((object)("Vektor Kill House unload failed: " + ex));
			}
			return false;
		}
	}

	private bool VerifyExactEnvironment()
	{
		if (!string.Equals(Application.unityVersion, "6000.3.8f1", StringComparison.Ordinal))
		{
			log.LogError((object)("Vektor Kill House exact-build rejection: unityVersion=" + Application.unityVersion + ", expected=6000.3.8f1."));
			return false;
		}
		foreach (KeyValuePair<string, FileFingerprint> exactFile in ExactFiles)
		{
			string text = Path.Combine(Paths.GameRootPath, exactFile.Key);
			if (!File.Exists(text) || new FileInfo(text).Length != exactFile.Value.Bytes)
			{
				log.LogError((object)("Vektor Kill House exact-build rejection: missing or wrong-sized " + exactFile.Key + "."));
				return false;
			}
			using SHA256 sHA = SHA256.Create();
			using FileStream inputStream = File.OpenRead(text);
			if (!string.Equals(Convert.ToHexString(sHA.ComputeHash(inputStream)), exactFile.Value.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				log.LogError((object)("Vektor Kill House exact-build rejection: SHA-256 mismatch for " + exactFile.Key + "."));
				return false;
			}
		}
		log.LogInfo((object)"Vektor Kill House exact-build fingerprint passed: Unity plus executable, GameAssembly, and UnityPlayer.");
		return true;
	}

	private unsafe void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		if (IsKillHouseScene(scene))
		{
			RestoreWeaponIlluminationBoosts();
			pendingSceneHandle = SceneHandle.op_Implicit(((Scene)(ref scene)).handle);
			applyNotBeforeFrame = Time.frameCount + 2;
			aiSightOcclusionPending = false;
			aiSightOcclusionPassed = false;
			nextAiSightAuditFrame = Time.frameCount + 30;
			opticAuditPending = true;
			nextOpticAuditFrame = Time.frameCount + 120;
			nextOpticIdentityProbeFrame = Time.frameCount + 120;
			equippedWeaponIdentityFingerprint = 0uL;
			equippedWeaponIdentityInitialized = false;
			lastOpticAuditSignature = string.Empty;
			log.LogInfo((object)("Vektor Kill House variant observed: path=" + ((Scene)(ref scene)).path + ", mode=" + ((object)(*(LoadSceneMode*)(&mode))/*cast due to constrained. prefix*/).ToString() + ", applyFrame=" + applyNotBeforeFrame + "."));
		}
	}

	private void OnSceneUnloaded(Scene scene)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		if (((Scene)(ref scene)).handle != SceneHandle.op_Implicit(pendingSceneHandle))
		{
			if (((Scene)(ref scene)).handle == SceneHandle.op_Implicit(runtimeNavigationOwnerSceneHandle))
			{
				ArmNavigationTeardownAudit(SceneHandle.op_Implicit(((Scene)(ref scene)).handle));
				ReleaseRuntimeNavigation("superseded kill-house scene unload");
				ReleaseOwnedMaterials();
				log.LogInfo((object)("Vektor Kill House superseded generation released without disturbing replacement handle=" + pendingSceneHandle + "."));
			}
			return;
		}
		ArmNavigationTeardownAudit(SceneHandle.op_Implicit(((Scene)(ref scene)).handle));
		pendingSceneHandle = 0;
		applyNotBeforeFrame = -1;
		aiSightOcclusionPending = false;
		aiSightOcclusionPassed = false;
		opticAuditPending = false;
		nextOpticIdentityProbeFrame = -1;
		equippedWeaponIdentityFingerprint = 0uL;
		equippedWeaponIdentityInitialized = false;
		lastOpticAuditSignature = string.Empty;
		RestoreWarehouseOnlyLighting();
		RestoreWeaponIlluminationBoosts();
		RestoreGlobalFlashlightMultiplier();
		ReleaseRuntimeNavigation("kill-house scene unload");
		ReleaseOwnedMaterials();
		log.LogInfo((object)"Vektor Kill House variant unloaded; scene-owned runtime state released.");
	}

	private void OnDriverTick()
	{
		if (!exactEnvironmentAccepted)
		{
			return;
		}
		if (pendingNavigationTeardownAuditFrame >= 0)
		{
			if (Time.frameCount < pendingNavigationTeardownAuditFrame)
			{
				return;
			}
			CompleteNavigationTeardownAudit();
		}
		if (pendingSceneHandle == 0 || (applyNotBeforeFrame < 0 && !aiSightOcclusionPending && !opticAuditPending && nextOpticIdentityProbeFrame < 0) || (runtimeNavigationOwnerSceneHandle != 0 && runtimeNavigationOwnerSceneHandle != pendingSceneHandle))
		{
			return;
		}
		if (applyNotBeforeFrame >= 0 && Time.frameCount >= applyNotBeforeFrame)
		{
			applyNotBeforeFrame = -1;
			ApplyRuntimeContract();
		}
		if (aiSightOcclusionPending && Time.frameCount >= nextAiSightAuditFrame)
		{
			nextAiSightAuditFrame = Time.frameCount + 120;
			TryCompleteDeferredAiSightAudit();
		}
		if (nextOpticIdentityProbeFrame >= 0 && Time.frameCount >= nextOpticIdentityProbeFrame)
		{
			nextOpticIdentityProbeFrame = Time.frameCount + 120;
			ProbeEquippedWeaponIdentity();
		}
		if (opticAuditPending && Time.frameCount >= nextOpticAuditFrame)
		{
			nextOpticAuditFrame = Time.frameCount + 120;
			if (AuditLiveWeaponIllumination())
			{
				opticAuditPending = false;
			}
		}
	}

	private static Type FindManagedType(string fullName)
	{
		Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
		for (int i = 0; i < assemblies.Length; i++)
		{
			Type type = assemblies[i].GetType(fullName, throwOnError: false);
			if (type != null)
			{
				return type;
			}
		}
		return null;
	}

	private void ApplyRuntimeContract()
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_0188: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bb: Unknown result type (might be due to invalid IL or missing references)
		Scene scene = FindLoadedSceneByHandle(pendingSceneHandle);
		if (!IsKillHouseScene(scene))
		{
			return;
		}
		GameObject val = FindOwnedRoot(scene);
		AuditResidentDoorTemplates("kill-house-scene", force: true);
		if ((Object)(object)val == (Object)null || !ValidateSceneContract(val))
		{
			MarkFailure(scene, val, "ownership-or-scene-contract");
			return;
		}
		bool flag = ApplyWarehouseOnlyLighting(val);
		bool flag2 = EnsureIndoorRenderContract(val);
		bool flag3 = flag && flag2 && ValidateIndoorLighting(val);
		bool flag4 = flag3 && EnsureNativeDoorV2Runtime(val);
		bool flag5 = flag4 && RebindSceneMaterials(val);
		bool flag6 = false;
		bool deferred = false;
		if (flag5)
		{
			flag6 = ValidateAiSightOcclusion(val, out deferred);
		}
		bool flag7 = flag5 && (flag6 || deferred) && EnsureRuntimeNavigationGraph(val);
		int markerCount = 0;
		bool flag8 = flag7 && ValidateTacticalEnemyPlacement(val, out markerCount);
		if (!flag || !flag2 || !flag3 || !flag4 || !flag5 || (!flag6 && !deferred) || !flag7 || !flag8)
		{
			MarkFailure(scene, val, (!flag) ? "warehouse-light-isolation" : ((!flag2) ? "indoor-render-contract" : ((!flag3) ? "indoor-lighting" : ((!flag4) ? "doorv2-reconstruction" : ((!flag5) ? "material-rebind" : ((!flag6 && !deferred) ? "ai-sight-occlusion" : ((!flag7) ? "navigation" : "tactical-enemy-placement")))))));
			return;
		}
		aiSightOcclusionPassed = flag6;
		aiSightOcclusionPending = deferred;
		if (deferred)
		{
			nextAiSightAuditFrame = Time.frameCount + 30;
		}
		if ((Object)(object)val.transform.Find("RUNTIME_VEKTOR_KILLHOUSE_READY") == (Object)null)
		{
			new GameObject("RUNTIME_VEKTOR_KILLHOUSE_READY").transform.SetParent(val.transform, false);
		}
		if ((Object)(object)val.transform.Find("MODDED_OPERATIONS_RUNTIME_CONTRACT_READY") == (Object)null)
		{
			new GameObject("MODDED_OPERATIONS_RUNTIME_CONTRACT_READY").transform.SetParent(val.transform, false);
		}
		log.LogInfo((object)("Vektor Kill House runtime gate passed: scene=" + ((Scene)(ref scene)).name + ", nativeOnly=true, fullDoorV2=true, stateAwareFixtureLighting=true, vanillaIndoorRender=true, fixedSafeRoom=true, tacticalEnemyPositions=" + markerCount + "/" + markerCount + ", vanillaIdleBehavior=Wander12m, frameworkInitialResponseDelay=3-6s, aiSightOcclusion=" + (flag6 ? "passed" : "deferred-until-resident-ai") + "."));
	}

	private bool EnsureIndoorRenderContract(GameObject root)
	{
		Volume[] array = Il2CppArrayBase<Volume>.op_Implicit(root.GetComponentsInChildren<Volume>(true));
		Volume volume = array.SingleOrDefault((Volume candidate) => string.Equals(((Object)candidate).name, "VANILLA_OFFICE_GLOBAL_VOLUME", StringComparison.Ordinal));
		string detail = "volume-count=" + array.Length;
		bool flag = array.Length == 1 && VolumeHasPvpWarehouseContract(volume, out detail);
		bool flag2 = false;
		try
		{
			if ((Object)(object)globalFlashlightMultiplier == (Object)null)
			{
				globalFlashlightMultiplier = driverObject.AddComponent<GlobalFlashLightMultiplier>();
			}
			globalFlashlightMultiplier.MultiplierValue = 6f;
			globalFlashlightMultiplier.UpdateFlashLightMultiplier();
			flag2 = Mathf.Abs(globalFlashlightMultiplier.MultiplierValue - 6f) <= 0.001f;
		}
		catch (Exception ex)
		{
			log.LogError((object)("Vektor Kill House global flashlight multiplier failed: " + ex.GetType().Name + ": " + ex.Message + "."));
		}
		bool result = flag && flag2;
		log.LogInfo((object)("Vektor Kill House indoor render gate: passed=" + result + ", volumes=" + array.Length + ", officeVolume=" + flag + ", globalFlashlightMultiplier=" + (((Object)(object)globalFlashlightMultiplier == (Object)null) ? "missing" : globalFlashlightMultiplier.MultiplierValue.ToString("F2", CultureInfo.InvariantCulture)) + ", reticleNormalBrightnessMultiplier=" + 2f.ToString("F2", CultureInfo.InvariantCulture) + ", reticleNvgBrightnessMultiplier=" + 1f.ToString("F2", CultureInfo.InvariantCulture) + ", reticleNormalSizeMultiplier=" + 1.5f.ToString("F2", CultureInfo.InvariantCulture) + ", visibleLaserMultiplier=" + 6f.ToString("F2", CultureInfo.InvariantCulture) + ", donor=PVP-Woods-Warehouse, exposureCompensation=0.00, exposureRangeEV=8.50-11.00, bloom=0.03, lensFlare=0.50, externalLut=AgX-PunchyPowerfulMix" + (flag ? string.Empty : (", volumeDetail=" + detail)) + "."));
		return result;
	}

	private static bool VolumeHasPvpWarehouseContract(Volume volume, out string detail)
	{
		//IL_06b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_06b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_072d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0732: Unknown result type (might be due to invalid IL or missing references)
		//IL_0225: Unknown result type (might be due to invalid IL or missing references)
		//IL_022b: Invalid comparison between Unknown and I4
		//IL_04d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_04d8: Invalid comparison between Unknown and I4
		detail = "volume=<null>";
		if ((Object)(object)volume == (Object)null)
		{
			return false;
		}
		VolumeProfile sharedProfile = volume.sharedProfile;
		if (!volume.isGlobal || ((Component)volume).gameObject.layer != 0 || Mathf.Abs(volume.priority - 100010f) > 0.01f || Mathf.Abs(volume.weight - 1f) > 0.001f || (Object)(object)sharedProfile == (Object)null)
		{
			detail = "global=" + volume.isGlobal + ",layer=" + ((Component)volume).gameObject.layer + ",priority=" + volume.priority.ToString("F2", CultureInfo.InvariantCulture) + ",weight=" + volume.weight.ToString("F2", CultureInfo.InvariantCulture) + ",profile=" + (((Object)(object)sharedProfile == (Object)null) ? "missing" : ((Object)sharedProfile).name);
			return false;
		}
		Exposure val = default(Exposure);
		VisualEnvironment val2 = default(VisualEnvironment);
		PhysicallyBasedSky val3 = default(PhysicallyBasedSky);
		Fog val4 = default(Fog);
		ProbeVolumesOptions val5 = default(ProbeVolumesOptions);
		Bloom val6 = default(Bloom);
		ScreenSpaceLensFlare val7 = default(ScreenSpaceLensFlare);
		MicroShadowing val8 = default(MicroShadowing);
		ContactShadows val9 = default(ContactShadows);
		HDShadowSettings val10 = default(HDShadowSettings);
		Tonemapping val11 = default(Tonemapping);
		LiftGammaGain val12 = default(LiftGammaGain);
		WhiteBalance val13 = default(WhiteBalance);
		ColorAdjustments val14 = default(ColorAdjustments);
		if (!sharedProfile.TryGet<Exposure>(ref val) || !sharedProfile.TryGet<VisualEnvironment>(ref val2) || !sharedProfile.TryGet<PhysicallyBasedSky>(ref val3) || !sharedProfile.TryGet<Fog>(ref val4) || !sharedProfile.TryGet<ProbeVolumesOptions>(ref val5) || !sharedProfile.TryGet<Bloom>(ref val6) || !sharedProfile.TryGet<ScreenSpaceLensFlare>(ref val7) || !sharedProfile.TryGet<MicroShadowing>(ref val8) || !sharedProfile.TryGet<ContactShadows>(ref val9) || !sharedProfile.TryGet<HDShadowSettings>(ref val10) || !sharedProfile.TryGet<Tonemapping>(ref val11) || !sharedProfile.TryGet<LiftGammaGain>(ref val12) || !sharedProfile.TryGet<WhiteBalance>(ref val13) || !sharedProfile.TryGet<ColorAdjustments>(ref val14))
		{
			detail = "profile-components=" + sharedProfile.components.Count;
			return false;
		}
		Texture value = ((VolumeParameter<Texture>)(object)val11.lutTexture).value;
		Texture3D val15 = (((Object)(object)value == (Object)null) ? null : ((Il2CppObjectBase)value).TryCast<Texture3D>());
		bool result = sharedProfile.components.Count == 14 && ((VolumeComponent)val).active && ((VolumeParameter)val.mode).overrideState && (int)((VolumeParameter<ExposureMode>)(object)val.mode).value == 4 && ((VolumeParameter)val.compensation).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val.compensation).value) <= 0.001f && ((VolumeParameter)val.limitMin).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val.limitMin).value - 8.5f) <= 0.001f && ((VolumeParameter)val.limitMax).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val.limitMax).value - 11f) <= 0.001f && !((VolumeComponent)val2).active && !((VolumeComponent)val3).active && !((VolumeComponent)val4).active && ((VolumeComponent)val5).active && ((VolumeComponent)val6).active && ((VolumeParameter)val6.intensity).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val6.intensity).value - 0.03f) <= 0.001f && ((VolumeParameter)val6.threshold).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val6.threshold).value - 0.9f) <= 0.001f && ((VolumeParameter)val6.scatter).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val6.scatter).value - 0.893f) <= 0.001f && ((VolumeComponent)val7).active && ((VolumeParameter)val7.intensity).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val7.intensity).value - 0.5f) <= 0.001f && ((VolumeParameter)val7.streaksIntensity).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val7.streaksIntensity).value - 1.55f) <= 0.001f && ((VolumeParameter)val7.streaksLength).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val7.streaksLength).value - 0.022f) <= 0.001f && ((VolumeParameter)val7.streaksOrientation).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val7.streaksOrientation).value) <= 0.001f && ((VolumeParameter)val7.chromaticAbberationIntensity).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val7.chromaticAbberationIntensity).value - 0.6f) <= 0.001f && ((VolumeComponent)val8).active && ((VolumeComponent)val9).active && ((VolumeComponent)val10).active && ((VolumeComponent)val11).active && ((VolumeParameter)val11.mode).overrideState && (int)((VolumeParameter<TonemappingMode>)(object)val11.mode).value == 4 && ((VolumeParameter)val11.lutTexture).overrideState && (Object)(object)val15 != (Object)null && ((Texture)val15).width == 32 && ((Texture)val15).height == 32 && val15.depth == 32 && string.Equals(((Object)val15).name, "AgX - PunchyPowerfulMix", StringComparison.Ordinal) && !((VolumeParameter)val11.lutContribution).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val11.lutContribution).value - 1f) <= 0.001f && ((VolumeComponent)val12).active && ((VolumeParameter)val12.lift).overrideState && ((VolumeParameter)val12.gamma).overrideState && ((VolumeParameter)val12.gain).overrideState && ((VolumeComponent)val13).active && ((VolumeComponent)val14).active && ((VolumeParameter)val14.postExposure).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val14.postExposure).value + 0.3f) <= 0.001f && ((VolumeParameter)val14.contrast).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val14.contrast).value - 30f) <= 0.001f && ((VolumeParameter)val14.hueShift).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val14.hueShift).value) <= 0.001f && ((VolumeParameter)val14.saturation).overrideState && Mathf.Abs(((VolumeParameter<float>)(object)val14.saturation).value + 15f) <= 0.001f;
		detail = "components=" + sharedProfile.components.Count + ",exposure=" + ((object)((VolumeParameter<ExposureMode>)(object)val.mode).value/*cast due to constrained. prefix*/).ToString() + "/" + ((VolumeParameter<float>)(object)val.limitMin).value.ToString("F2", CultureInfo.InvariantCulture) + "-" + ((VolumeParameter<float>)(object)val.limitMax).value.ToString("F2", CultureInfo.InvariantCulture) + ",tonemap=" + ((object)((VolumeParameter<TonemappingMode>)(object)val11.mode).value/*cast due to constrained. prefix*/).ToString() + ",lut=" + (((Object)(object)val15 == (Object)null) ? "missing" : ((Object)val15).name) + ",rawLut=" + (((Object)(object)value == (Object)null) ? "missing" : (((Object)value).name + "/" + ((object)value).GetType().Name)) + ",bloom=" + ((VolumeParameter<float>)(object)val6.intensity).value.ToString("F2", CultureInfo.InvariantCulture) + ",bloomScatter=" + ((VolumeParameter<float>)(object)val6.scatter).value.ToString("F3", CultureInfo.InvariantCulture) + ",lensFlare=" + ((VolumeParameter<float>)(object)val7.intensity).value.ToString("F2", CultureInfo.InvariantCulture) + ",flareStreaks=" + ((VolumeParameter<float>)(object)val7.streaksIntensity).value.ToString("F2", CultureInfo.InvariantCulture) + "/" + ((VolumeParameter<float>)(object)val7.streaksLength).value.ToString("F3", CultureInfo.InvariantCulture) + ",chromatic=" + ((VolumeParameter<float>)(object)val7.chromaticAbberationIntensity).value.ToString("F2", CultureInfo.InvariantCulture) + ",whiteBalanceActive=" + ((VolumeComponent)val13).active;
		return result;
	}

	private void RestoreGlobalFlashlightMultiplier()
	{
		if ((Object)(object)globalFlashlightMultiplier == (Object)null)
		{
			return;
		}
		try
		{
			globalFlashlightMultiplier.MultiplierValue = 1f;
			globalFlashlightMultiplier.UpdateFlashLightMultiplier();
		}
		catch (Exception ex)
		{
			ManualLogSource obj = log;
			if (obj != null)
			{
				obj.LogWarning((object)("Vektor Kill House could not restore the global flashlight multiplier: " + ex.GetType().Name + ": " + ex.Message + "."));
			}
		}
	}

	private bool ApplyWarehouseOnlyLighting(GameObject root)
	{
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0106: Unknown result type (might be due to invalid IL or missing references)
		//IL_010c: Invalid comparison between Unknown and I4
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
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
			RenderSettings.ambientMode = (AmbientMode)3;
			RenderSettings.ambientLight = Color.black;
			RenderSettings.ambientIntensity = 0f;
			RenderSettings.reflectionIntensity = 0f;
			Light[] array = FindLoadedDirectionalLights();
			foreach (Light val in array)
			{
				if (!((Object)(object)val == (Object)null) && !((Component)val).transform.IsChildOf(root.transform) && !suspendedDirectionalLights.ContainsKey(((Object)val).GetInstanceID()))
				{
					suspendedDirectionalLights[((Object)val).GetInstanceID()] = new SuspendedDirectionalLight(val, ((Behaviour)val).enabled, val.intensity, val.shadows);
					((Behaviour)val).enabled = false;
					val.intensity = 0f;
					val.shadows = (LightShadows)0;
				}
			}
			int num;
			if ((Object)(object)RenderSettings.skybox == (Object)null && (int)RenderSettings.ambientMode == 3)
			{
				Color ambientLight = RenderSettings.ambientLight;
				if (((Color)(ref ambientLight)).maxColorComponent <= 0.001f && RenderSettings.ambientIntensity <= 0.001f && RenderSettings.reflectionIntensity <= 0.001f)
				{
					num = (FindLoadedDirectionalLights().All((Light light) => !((Behaviour)light).enabled && light.intensity <= 0.001f) ? 1 : 0);
					goto IL_0167;
				}
			}
			num = 0;
			goto IL_0167;
			IL_0167:
			bool result = (byte)num != 0;
			log.LogInfo((object)("Vektor Kill House warehouse-only global lighting: passed=" + result + ", suspendedExternalDirectionals=" + suspendedDirectionalLights.Count + ", skybox=none, ambient=black/0, reflectionIntensity=0, weapon-local-lights-preserved=true."));
			return result;
		}
		catch (Exception ex)
		{
			log.LogError((object)("Vektor Kill House warehouse-only global lighting failed: " + ex));
			return false;
		}
	}

	private void RestoreWarehouseOnlyLighting()
	{
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		foreach (SuspendedDirectionalLight value in suspendedDirectionalLights.Values)
		{
			if (!((Object)(object)value.Light == (Object)null))
			{
				((Behaviour)value.Light).enabled = value.Enabled;
				value.Light.intensity = value.Intensity;
				value.Light.shadows = value.Shadows;
			}
		}
		suspendedDirectionalLights.Clear();
		if (warehouseEnvironmentOverrideApplied)
		{
			RenderSettings.skybox = savedSkybox;
			RenderSettings.ambientMode = savedAmbientMode;
			RenderSettings.ambientLight = savedAmbientLight;
			RenderSettings.ambientIntensity = savedAmbientIntensity;
			RenderSettings.reflectionIntensity = savedReflectionIntensity;
			savedSkybox = null;
			warehouseEnvironmentOverrideApplied = false;
		}
	}

	private static Light[] FindLoadedDirectionalLights()
	{
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Invalid comparison between Unknown and I4
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		List<Light> list = new List<Light>();
		foreach (Object item in (Il2CppArrayBase<Object>)(object)Resources.FindObjectsOfTypeAll(Il2CppType.Of<Light>()))
		{
			Light val = ((item == (Object)null) ? null : ((Il2CppObjectBase)item).TryCast<Light>());
			if (!((Object)(object)val == (Object)null) && (int)val.type == 1 && !((Object)(object)((Component)val).gameObject == (Object)null))
			{
				Scene scene = ((Component)val).gameObject.scene;
				if (((Scene)(ref scene)).IsValid() && ((Scene)(ref scene)).isLoaded)
				{
					list.Add(val);
				}
			}
		}
		return list.ToArray();
	}

	private bool ValidateIndoorLighting(GameObject root)
	{
		//IL_02e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_04fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0502: Unknown result type (might be due to invalid IL or missing references)
		//IL_099b: Unknown result type (might be due to invalid IL or missing references)
		//IL_09a1: Invalid comparison between Unknown and I4
		//IL_09a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_09a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_066d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0679: Unknown result type (might be due to invalid IL or missing references)
		//IL_067f: Invalid comparison between Unknown and I4
		//IL_0a64: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a69: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ab1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ab6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0aba: Unknown result type (might be due to invalid IL or missing references)
		//IL_0abf: Unknown result type (might be due to invalid IL or missing references)
		//IL_077a: Unknown result type (might be due to invalid IL or missing references)
		//IL_077f: Unknown result type (might be due to invalid IL or missing references)
		//IL_079b: Unknown result type (might be due to invalid IL or missing references)
		//IL_07a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0852: Unknown result type (might be due to invalid IL or missing references)
		//IL_0857: Unknown result type (might be due to invalid IL or missing references)
		//IL_06f1: Unknown result type (might be due to invalid IL or missing references)
		Light[] source = Il2CppArrayBase<Light>.op_Implicit(root.GetComponentsInChildren<Light>(true));
		Light[] array = source.Where((Light light) => (int)light.type == 1).ToArray();
		Light[] array2 = source.Where((Light light) => ((Object)light).name.StartsWith("ROOM_LOCAL_FIXTURE_LIGHT_", StringComparison.Ordinal)).ToArray();
		Transform val = root.transform.Find("10_ROOMS");
		Transform val2 = root.transform.Find("70_LIGHTING");
		int num = ((!((Object)(object)val == (Object)null)) ? val.childCount : 0);
		Transform[] array3 = (((Object)(object)val2 == (Object)null) ? Array.Empty<Transform>() : (from item in Enumerable.Range(0, val2.childCount).Select((Func<int, Transform>)val2.GetChild)
			where ((Object)item).name.StartsWith("ROOM_LIGHT_", StringComparison.Ordinal) && ((Object)item).name.IndexOf("_STATE_", StringComparison.Ordinal) >= 0
			select item).ToArray());
		int num2 = Enumerable.Count(array3, (Transform item) => ((Object)item).name.EndsWith("_STATE_LIT", StringComparison.Ordinal));
		int num3 = Enumerable.Count(array3, (Transform item) => ((Object)item).name.EndsWith("_STATE_DIM", StringComparison.Ordinal));
		int num4 = Enumerable.Count(array3, (Transform item) => ((Object)item).name.EndsWith("_STATE_DARK", StringComparison.Ordinal));
		int num5 = Enumerable.Count(array3, (Transform item) => ((Object)item).name.StartsWith("ROOM_LIGHT_00_SAFE_", StringComparison.Ordinal) && ((Object)item).name.EndsWith("_STATE_LIT", StringComparison.Ordinal));
		foreach (Light item in array.Where((Light light) => string.Equals(((Object)light).name, "PACKAGE_FALLBACK_DIRECTIONAL_LIGHT", StringComparison.Ordinal)))
		{
			((Behaviour)item).enabled = false;
			item.intensity = 0f;
			item.shadows = (LightShadows)0;
		}
		int num6 = 0;
		Light[] array4 = array2;
		foreach (Light val3 in array4)
		{
			Transform parent = ((Component)val3).transform.parent;
			int num8 = ((!((Object)(object)parent == (Object)null)) ? ((IEnumerable<Light>)((Component)parent).GetComponentsInChildren<Light>(true)).Count((Light candidate) => ((Object)candidate).name.StartsWith("ROOM_LOCAL_FIXTURE_LIGHT_", StringComparison.Ordinal)) : 0);
			string obj = (((Object)(object)parent == (Object)null) ? string.Empty : ((Object)parent).name);
			bool flag = obj.EndsWith("_STATE_LIT", StringComparison.Ordinal);
			bool flag2 = obj.EndsWith("_STATE_DIM", StringComparison.Ordinal);
			bool flag3 = obj.EndsWith("_STATE_DARK", StringComparison.Ordinal);
			float intensity = ((!flag) ? ((!flag2) ? 0f : ((num8 == 1) ? 160f : 120f)) : ((num8 == 1) ? 1400f : 1100f));
			val3.type = (LightType)0;
			val3.color = Color.white;
			val3.range = 11.5f;
			val3.spotAngle = 58f;
			val3.innerSpotAngle = 38f;
			val3.shadows = (LightShadows)2;
			val3.useColorTemperature = true;
			val3.colorTemperature = 4300f;
			val3.intensity = intensity;
			((Behaviour)val3).enabled = !flag3 && (flag || flag2);
			HDAdditionalLightData[] array5 = Il2CppArrayBase<HDAdditionalLightData>.op_Implicit(((Component)val3).GetComponents<HDAdditionalLightData>());
			HDAdditionalLightData val4 = ((array5.Length == 0) ? null : array5[0]);
			if ((Object)(object)val4 == (Object)null)
			{
				val4 = ((Component)val3).gameObject.AddComponent<HDAdditionalLightData>();
				num6++;
			}
			val4.lightUnit = (LightUnit)0;
			val4.intensity = intensity;
			val4.range = 11.5f;
		}
		HashSet<int> localFixtureLightIds = new HashSet<int>(array2.Select((Light light) => ((Object)light).GetInstanceID()));
		List<HDAdditionalLightData> list = new List<HDAdditionalLightData>();
		HashSet<int> hashSet = new HashSet<int>();
		Transform[] array6 = array3;
		for (int num7 = 0; num7 < array6.Length; num7++)
		{
			foreach (HDAdditionalLightData componentsInChild in ((Component)array6[num7]).GetComponentsInChildren<HDAdditionalLightData>(true))
			{
				if ((Object)(object)componentsInChild != (Object)null && hashSet.Add(((Object)componentsInChild).GetInstanceID()))
				{
					list.Add(componentsInChild);
				}
			}
		}
		HDAdditionalLightData[] array7 = list.ToArray();
		int num9 = Enumerable.Count(array7, (HDAdditionalLightData data) => (Object)(object)data.legacyLight == (Object)null || !localFixtureLightIds.Contains(((Object)data.legacyLight).GetInstanceID()) || (Object)(object)((Component)data).gameObject != (Object)(object)((Component)data.legacyLight).gameObject);
		int num10 = 0;
		int num11 = 0;
		int num12 = 0;
		List<string> list2 = new List<string>();
		array4 = array2;
		foreach (Light val5 in array4)
		{
			Light[] array8 = Il2CppArrayBase<Light>.op_Implicit(((Component)val5).gameObject.GetComponents<Light>());
			HDAdditionalLightData[] array9 = Il2CppArrayBase<HDAdditionalLightData>.op_Implicit(((Component)val5).gameObject.GetComponents<HDAdditionalLightData>());
			num11 += array9.Length;
			if (array9.Length != 1)
			{
				num12 += Math.Abs(array9.Length - 1);
			}
			HDAdditionalLightData val6 = ((array9.Length == 1) ? array9[0] : null);
			float y = root.transform.InverseTransformPoint(((Component)val5).transform.position).y;
			string text = (((Object)(object)((Component)val5).transform.parent == (Object)null) ? string.Empty : ((Object)((Component)val5).transform.parent).name);
			bool flag4 = ((!text.EndsWith("_STATE_LIT", StringComparison.Ordinal)) ? ((!text.EndsWith("_STATE_DIM", StringComparison.Ordinal)) ? (text.EndsWith("_STATE_DARK", StringComparison.Ordinal) && !((Behaviour)val5).enabled && (Object)(object)val6 != (Object)null && val6.intensity <= 0.001f) : (((Behaviour)val5).enabled && (Object)(object)val6 != (Object)null && val6.intensity >= 110f && val6.intensity <= 170f)) : (((Behaviour)val5).enabled && (Object)(object)val6 != (Object)null && val6.intensity >= 1050f && val6.intensity <= 1450f));
			if (!(array8.Length == 1 && (Object)(object)array8[0] == (Object)(object)val5 && array9.Length == 1 && (Object)(object)val6 != (Object)null && ((Behaviour)val6).enabled && (Object)(object)((Component)val6).gameObject == (Object)(object)((Component)val5).gameObject && (Object)(object)val6.legacyLight != (Object)null && (Object)(object)val6.legacyLight == (Object)(object)val5 && flag4) || (int)val5.type != 0 || (int)val5.shadows != 2 || !(y >= 6.55f) || !(y <= 6.7000003f) || !(val5.range >= 11f) || !(val5.spotAngle >= 56f) || !(val5.spotAngle <= 60f) || !val5.useColorTemperature || !(val5.colorTemperature >= 4200f) || !(val5.colorTemperature <= 4400f) || !((Object)(object)val6 != (Object)null) || (int)val6.lightUnit != 0 || !(val6.range >= 11f))
			{
				num10++;
				if (list2.Count < 4)
				{
					list2.Add(((Object)val5).name + "{mapY=" + y.ToString("F2", CultureInfo.InvariantCulture) + ",enabled=" + ((Behaviour)val5).enabled + ",type=" + ((object)val5.type/*cast due to constrained. prefix*/).ToString() + ",shadows=" + ((object)val5.shadows/*cast due to constrained. prefix*/).ToString() + ",range=" + val5.range.ToString("F2", CultureInfo.InvariantCulture) + ",temperature=" + val5.colorTemperature.ToString("F0", CultureInfo.InvariantCulture) + ",lightComponents=" + array8.Length + ",hdComponents=" + array9.Length + ",hd=" + (((Object)(object)val6 == (Object)null) ? "missing-or-duplicate" : (((object)val6.lightUnit/*cast due to constrained. prefix*/).ToString() + "/" + val6.intensity.ToString("F1", CultureInfo.InvariantCulture) + "/" + val6.range.ToString("F2", CultureInfo.InvariantCulture))) + "}");
				}
			}
		}
		int num13 = Enumerable.Count(source, (Light light) => (int)light.type == 2);
		int num14 = Enumerable.Count(source, (Light light) => (int)light.type == 0);
		int num15 = Enumerable.Count(source, (Light light) => (int)light.type == 0 && !((Object)light).name.StartsWith("ROOM_LOCAL_FIXTURE_LIGHT_", StringComparison.Ordinal));
		Light[] array10 = FindLoadedDirectionalLights();
		int num16 = Enumerable.Count(array10, (Light light) => ((Behaviour)light).enabled || light.intensity > 0.001f);
		int num17;
		if (warehouseEnvironmentOverrideApplied && (Object)(object)RenderSettings.skybox == (Object)null && (int)RenderSettings.ambientMode == 3)
		{
			Color ambientLight = RenderSettings.ambientLight;
			if (((Color)(ref ambientLight)).maxColorComponent <= 0.001f && RenderSettings.ambientIntensity <= 0.001f)
			{
				num17 = ((RenderSettings.reflectionIntensity <= 0.001f) ? 1 : 0);
				goto IL_09d6;
			}
		}
		num17 = 0;
		goto IL_09d6;
		IL_09d6:
		bool flag5 = (byte)num17 != 0;
		bool flag6 = array2.Length >= num && num10 == 0 && num11 == array2.Length && num12 == 0 && array7.Length == array2.Length && num9 == 0 && array3.Length == num && num5 == 1 && num2 >= 5 && num3 >= 4 && num4 >= 7;
		bool result = array.Length == 1 && !((Behaviour)array[0]).enabled && array[0].intensity <= 0.001f && num16 == 0 && flag5 && num13 == 0 && num14 == array2.Length && flag6;
		Encoding uTF = Encoding.UTF8;
		Scene scene = root.scene;
		string text2 = Convert.ToBase64String(uTF.GetBytes(((Scene)(ref scene)).path ?? string.Empty));
		ManualLogSource obj2 = log;
		string[] array11 = new string[48];
		array11[0] = "Vektor Kill House indoor lighting gate: passed=";
		array11[1] = result.ToString();
		array11[2] = ", sceneHandle=";
		scene = root.scene;
		array11[3] = ((object)((Scene)(ref scene)).handle/*cast due to constrained. prefix*/).ToString();
		array11[4] = ", scenePathBase64=";
		array11[5] = text2;
		array11[6] = ", roomSpaces=";
		array11[7] = num.ToString();
		array11[8] = ", fixtureLights=";
		array11[9] = array2.Length.ToString();
		array11[10] = ", roomLightStates=lit:";
		array11[11] = num2.ToString();
		array11[12] = "/dim:";
		array11[13] = num3.ToString();
		array11[14] = "/dark:";
		array11[15] = num4.ToString();
		array11[16] = ", litSafeRooms=";
		array11[17] = num5.ToString();
		array11[18] = ", invalidFixtures=";
		array11[19] = num10.ToString();
		array11[20] = ", hdrpDataAdded=";
		array11[21] = num6.ToString();
		array11[22] = ", pairedFixtureHdrpData=";
		array11[23] = num11.ToString();
		array11[24] = ", duplicateFixtureHdrpData=";
		array11[25] = num12.ToString();
		array11[26] = ", fixtureTreeHdrpData=";
		array11[27] = array7.Length.ToString();
		array11[28] = ", orphanFixtureHdrpData=";
		array11[29] = num9.ToString();
		array11[30] = ", pointLights=";
		array11[31] = num13.ToString();
		array11[32] = ", spotLights=";
		array11[33] = num14.ToString();
		array11[34] = ", nonFixtureSpotLights=";
		array11[35] = num15.ToString();
		array11[36] = ", enabledDirectional=";
		array11[37] = Enumerable.Count(array, (Light light) => ((Behaviour)light).enabled).ToString();
		array11[38] = ", loadedDirectionals=";
		array11[39] = array10.Length.ToString();
		array11[40] = ", enabledLoadedDirectionals=";
		array11[41] = num16.ToString();
		array11[42] = ", warehouseEnvironment=";
		array11[43] = flag5.ToString();
		array11[44] = ", disabledSentinelDirectional=";
		array11[45] = Enumerable.Count(array, (Light light) => !((Behaviour)light).enabled).ToString();
		array11[46] = ((list2.Count == 0) ? string.Empty : (", invalidSamples=[" + string.Join(" | ", list2) + "]"));
		array11[47] = ".";
		obj2.LogInfo((object)string.Concat(array11));
		return result;
	}

	private bool ValidateTacticalEnemyPlacement(GameObject root, out int markerCount)
	{
		//IL_011a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0121: Unknown result type (might be due to invalid IL or missing references)
		//IL_0126: Unknown result type (might be due to invalid IL or missing references)
		//IL_012b: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_0150: Unknown result type (might be due to invalid IL or missing references)
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0166: Unknown result type (might be due to invalid IL or missing references)
		//IL_016d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0179: Unknown result type (might be due to invalid IL or missing references)
		//IL_0183: Unknown result type (might be due to invalid IL or missing references)
		//IL_0191: Unknown result type (might be due to invalid IL or missing references)
		//IL_019d: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0200: Unknown result type (might be due to invalid IL or missing references)
		//IL_0205: Unknown result type (might be due to invalid IL or missing references)
		//IL_020f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0214: Unknown result type (might be due to invalid IL or missing references)
		//IL_0219: Unknown result type (might be due to invalid IL or missing references)
		//IL_021d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0222: Unknown result type (might be due to invalid IL or missing references)
		//IL_022c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0231: Unknown result type (might be due to invalid IL or missing references)
		//IL_0236: Unknown result type (might be due to invalid IL or missing references)
		//IL_028d: Unknown result type (might be due to invalid IL or missing references)
		//IL_028f: Unknown result type (might be due to invalid IL or missing references)
		//IL_02af: Unknown result type (might be due to invalid IL or missing references)
		Transform[] array = ((IEnumerable<Transform>)root.GetComponentsInChildren<Transform>(true)).Where((Transform item) => ((Object)item).name.StartsWith("PVE_EnemySpawn_", StringComparison.Ordinal)).ToArray();
		markerCount = array.Length;
		int num = 0;
		int num2 = 0;
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		List<string> list = new List<string>();
		Transform[] array2 = array;
		foreach (Transform val in array2)
		{
			Transform parent = val.parent;
			if ((Object)(object)parent == (Object)null || !((Object)parent).name.StartsWith("TACTICAL_POSITION_", StringComparison.Ordinal))
			{
				continue;
			}
			Transform val2 = FindDirectChild(parent, "TACTICAL_ROLE_");
			Transform val3 = FindDirectChild(parent, "TACTICAL_COVER_POINT_");
			Transform val4 = FindDirectChild(parent, "TACTICAL_THREAT_POINT_");
			Transform val5 = FindDirectChild(parent, "TACTICAL_NATIVE_BRAINAI_WANDER_RADIUS_12M");
			if ((Object)(object)val2 == (Object)null || (Object)(object)val3 == (Object)null || (Object)(object)val4 == (Object)null || (Object)(object)val5 == (Object)null)
			{
				continue;
			}
			hashSet.Add(((Object)val2).name);
			if (TryRepairRuntimeTacticalStandingPosition(val, val2, val3, val4))
			{
				num2++;
			}
			Vector3 val6 = val4.position - val.position;
			val6.y = 0f;
			Vector3 forward = val.forward;
			forward.y = 0f;
			float num4 = Vector2.Distance(new Vector2(val2.position.x, val2.position.z), new Vector2(val3.position.x, val3.position.z));
			float num5 = Vector2.Distance(new Vector2(val.position.x, val.position.z), new Vector2(val3.position.x, val3.position.z));
			float magnitude = ((Vector3)(ref val6)).magnitude;
			float num6 = ((magnitude < 0.01f) ? (-1f) : Vector3.Dot(((Vector3)(ref forward)).normalized, ((Vector3)(ref val6)).normalized));
			Vector3 val7 = val.position + Vector3.up * 0.42f;
			Vector3 val8 = val.position + Vector3.up * 1.58f;
			int num7 = -5;
			string[] array3 = new string[3] { "LocalPlayer", "Character", "Hitbox" };
			for (int num8 = 0; num8 < array3.Length; num8++)
			{
				int num9 = LayerMask.NameToLayer(array3[num8]);
				if (num9 >= 0)
				{
					num7 &= ~(1 << num9);
				}
			}
			Collider[] array4 = Il2CppArrayBase<Collider>.op_Implicit((Il2CppArrayBase<Collider>)(object)Physics.OverlapCapsule(val7, val8, 0.3f, num7, (QueryTriggerInteraction)1));
			bool flag = array4.Length == 0;
			bool flag2 = Il2CppArrayBase<Collider>.op_Implicit((Il2CppArrayBase<Collider>)(object)Physics.OverlapSphere(val3.position, 0.72f, -5, (QueryTriggerInteraction)1)).Any((Collider collider) => (Object)(object)collider != (Object)null && HasNativeCoverAncestor(((Component)collider).transform));
			if (num4 >= 0.35f && num4 <= 4.25f && num5 >= 0.15f && num5 <= 4.5f && magnitude >= 1.15f && num6 >= 0.78f && flag && flag2)
			{
				num++;
			}
			else if (list.Count < 8)
			{
				list.Add(((Object)val).name + "{authoredCover=" + num4.ToString("F2", CultureInfo.InvariantCulture) + ",liveCover=" + num5.ToString("F2", CultureInfo.InvariantCulture) + ",threat=" + magnitude.ToString("F2", CultureInfo.InvariantCulture) + ",facing=" + num6.ToString("F2", CultureInfo.InvariantCulture) + ",standingClear=" + flag + ",standingMask=" + DescribeLayers(num7) + ",standingHits=" + string.Join(";", from collider in array4.Take(6)
					select ((Object)collider).name + "@" + LayerMask.LayerToName(((Component)collider).gameObject.layer) + "/" + (((Object)(object)((Component)collider).transform.parent == (Object)null) ? "<root>" : ((Object)((Component)collider).transform.parent).name)) + ",coverBacked=" + flag2 + "}");
			}
		}
		int num10 = ((IEnumerable<Transform>)root.GetComponentsInChildren<Transform>(true)).Count((Transform item) => ((Object)item).name.StartsWith("TACTICAL_POSITION_", StringComparison.Ordinal));
		bool result = markerCount >= 36 && num10 == markerCount && num == markerCount && hashSet.Count >= 3;
		log.LogInfo((object)("Vektor Kill House tactical enemy placement: passed=" + result + ", markers=" + markerCount + ", validCoverFacingAndClearance=" + num + ", runtimeColliderRepairs=" + num2 + ", distinctRoles=" + hashSet.Count + ", nativeIdleBehavior=Wander, wanderRadiusMeters=12, comms=true, counterSuppression=true" + ((list.Count == 0) ? string.Empty : (", invalidSamples=[" + string.Join(" | ", list) + "]")) + "."));
		return result;
	}

	private static bool TryRepairRuntimeTacticalStandingPosition(Transform marker, Transform role, Transform cover, Transform threat)
	{
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_0104: Unknown result type (might be due to invalid IL or missing references)
		//IL_0110: Unknown result type (might be due to invalid IL or missing references)
		//IL_0123: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_0147: Unknown result type (might be due to invalid IL or missing references)
		//IL_014d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		//IL_0159: Unknown result type (might be due to invalid IL or missing references)
		//IL_015f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0164: Unknown result type (might be due to invalid IL or missing references)
		//IL_016b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_0176: Unknown result type (might be due to invalid IL or missing references)
		//IL_017c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0181: Unknown result type (might be due to invalid IL or missing references)
		//IL_0186: Unknown result type (might be due to invalid IL or missing references)
		//IL_018d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0193: Unknown result type (might be due to invalid IL or missing references)
		//IL_0198: Unknown result type (might be due to invalid IL or missing references)
		//IL_019e: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01af: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0204: Unknown result type (might be due to invalid IL or missing references)
		//IL_0207: Unknown result type (might be due to invalid IL or missing references)
		//IL_020c: Unknown result type (might be due to invalid IL or missing references)
		//IL_020e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0213: Unknown result type (might be due to invalid IL or missing references)
		//IL_0215: Unknown result type (might be due to invalid IL or missing references)
		//IL_0217: Unknown result type (might be due to invalid IL or missing references)
		//IL_0221: Unknown result type (might be due to invalid IL or missing references)
		//IL_0226: Unknown result type (might be due to invalid IL or missing references)
		//IL_022b: Unknown result type (might be due to invalid IL or missing references)
		//IL_022d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0237: Unknown result type (might be due to invalid IL or missing references)
		//IL_023c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0241: Unknown result type (might be due to invalid IL or missing references)
		//IL_0243: Unknown result type (might be due to invalid IL or missing references)
		//IL_025b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0262: Unknown result type (might be due to invalid IL or missing references)
		//IL_0269: Unknown result type (might be due to invalid IL or missing references)
		//IL_026f: Unknown result type (might be due to invalid IL or missing references)
		//IL_027a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0284: Unknown result type (might be due to invalid IL or missing references)
		//IL_0290: Unknown result type (might be due to invalid IL or missing references)
		//IL_0297: Unknown result type (might be due to invalid IL or missing references)
		//IL_029e: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_02af: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_02fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_031c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0321: Unknown result type (might be due to invalid IL or missing references)
		//IL_0326: Unknown result type (might be due to invalid IL or missing references)
		int num = -5;
		string[] array = new string[3] { "LocalPlayer", "Character", "Hitbox" };
		for (int i = 0; i < array.Length; i++)
		{
			int num2 = LayerMask.NameToLayer(array[i]);
			if (num2 >= 0)
			{
				num &= ~(1 << num2);
			}
		}
		Vector3 val = marker.position + Vector3.up * 0.42f;
		Vector3 val2 = marker.position + Vector3.up * 1.58f;
		if (((Il2CppArrayBase<Collider>)(object)Physics.OverlapCapsule(val, val2, 0.3f, num, (QueryTriggerInteraction)1)).Length == 0)
		{
			return false;
		}
		Vector3 val3 = marker.position - cover.position;
		val3.y = 0f;
		if (((Vector3)(ref val3)).sqrMagnitude < 0.01f)
		{
			val3 = marker.position - threat.position;
		}
		val3.y = 0f;
		if (((Vector3)(ref val3)).sqrMagnitude < 0.01f)
		{
			val3 = Vector3.forward;
		}
		((Vector3)(ref val3)).Normalize();
		Vector3 val4 = default(Vector3);
		((Vector3)(ref val4))._002Ector(0f - val3.z, 0f, val3.x);
		Vector3[] array2 = (Vector3[])(object)new Vector3[8]
		{
			val3 * 0.45f,
			val3 * 0.75f,
			val3 * 1.05f,
			val3 * 1.35f,
			val3 * 0.75f + val4 * 0.45f,
			val3 * 0.75f - val4 * 0.45f,
			val3 * 1.05f + val4 * 0.65f,
			val3 * 1.05f - val4 * 0.65f
		};
		foreach (Vector3 val5 in array2)
		{
			Vector3 val6 = marker.position + val5;
			Vector3 val7 = val6 + Vector3.up * 0.42f;
			Vector3 val8 = val6 + Vector3.up * 1.58f;
			if (((Il2CppArrayBase<Collider>)(object)Physics.OverlapCapsule(val7, val8, 0.3f, num, (QueryTriggerInteraction)1)).Length != 0)
			{
				continue;
			}
			float num3 = Vector2.Distance(new Vector2(val6.x, val6.z), new Vector2(cover.position.x, cover.position.z));
			float num4 = Vector2.Distance(new Vector2(val6.x, val6.z), new Vector2(threat.position.x, threat.position.z));
			if (!(num3 < 0.45f) && !(num3 > 4.25f) && !(num4 < 1.25f))
			{
				marker.position = val6;
				role.position = val6;
				Vector3 val9 = threat.position - val6;
				val9.y = 0f;
				if (((Vector3)(ref val9)).sqrMagnitude >= 0.01f)
				{
					marker.rotation = Quaternion.LookRotation(((Vector3)(ref val9)).normalized, Vector3.up);
				}
				Physics.SyncTransforms();
				return true;
			}
		}
		return false;
	}

	private static Transform FindDirectChild(Transform parent, string prefix)
	{
		if ((Object)(object)parent == (Object)null)
		{
			return null;
		}
		for (int i = 0; i < parent.childCount; i++)
		{
			Transform child = parent.GetChild(i);
			if ((Object)(object)child != (Object)null && ((Object)child).name.StartsWith(prefix, StringComparison.Ordinal))
			{
				return child;
			}
		}
		return null;
	}

	private static bool HasNativeCoverAncestor(Transform transform)
	{
		while ((Object)(object)transform != (Object)null)
		{
			if (((Object)transform).name.StartsWith("NATIVE_", StringComparison.Ordinal) && !string.Equals(((Object)transform).name, "NATIVE_Floor", StringComparison.Ordinal) && !string.Equals(((Object)transform).name, "NATIVE_Ceiling", StringComparison.Ordinal))
			{
				return true;
			}
			transform = transform.parent;
		}
		return false;
	}

	private bool EnsureNativeDoorV2Runtime(GameObject root)
	{
		Transform[] source = Il2CppArrayBase<Transform>.op_Implicit(root.GetComponentsInChildren<Transform>(true));
		Transform[] array = source.Where((Transform item) => ((Object)item).name.StartsWith("DOORV2_SOCKET_", StringComparison.Ordinal)).ToArray();
		Transform[] array2 = source.Where((Transform item) => string.Equals(((Object)item).name, "NATIVE_DOORV2_SHELL", StringComparison.Ordinal)).ToArray();
		Transform[] array3 = source.Where((Transform item) => string.Equals(((Object)item).name, "NATIVE_DOORV2_AUDIO_BANK", StringComparison.Ordinal)).ToArray();
		if (array.Length < 1 || array2.Length != array.Length || array3.Length != 1)
		{
			log.LogError((object)("Vektor Kill House DoorV2 gate failed: sockets=" + array.Length + ", shells=" + array2.Length + ", audioBanks=" + array3.Length + "."));
			return false;
		}
		Dictionary<string, AudioClip> clips = BuildDoorAudioLibrary(array3[0]);
		if (!ValidateDoorAudioLibrary(clips))
		{
			return false;
		}
		GameObject val = null;
		bool flag = NetworkClient.GetPrefab(3964291274u, ref val) && (Object)(object)val != (Object)null;
		bool flag2 = IsCompleteDoorV2(flag ? val.GetComponent<DoorV2>() : null);
		if (NetworkClient.active && !NetworkServer.active)
		{
			log.LogInfo((object)("Vektor Kill House DoorV2 client provisioning: registered=" + flag + ", complete=" + flag2 + ", assetId=" + 3964291274u + ", localShellsKeptInactive=" + array2.Length + "."));
			if (flag2)
			{
				return array2.All((Transform shell) => !((Component)shell).gameObject.activeSelf);
			}
			return false;
		}
		int num = 0;
		int num2 = 0;
		Transform[] array4 = array2;
		foreach (Transform val2 in array4)
		{
			try
			{
				DoorV2 val3 = ((Component)val2).GetComponent<DoorV2>();
				NetworkIdentity identity;
				if ((Object)(object)val3 == (Object)null)
				{
					val3 = ConfigureNativeDoorV2Shell(val2, clips, out identity);
					num++;
				}
				else
				{
					identity = ((Component)val2).GetComponent<NetworkIdentity>();
				}
				if (!ValidateReconstructedDoorV2Shell(val2, val3) || !DoorMatchesNativeOpening(val2) || (Object)(object)identity == (Object)null)
				{
					throw new InvalidOperationException("DoorV2 graph or closed-leaf doorway alignment did not close after reconstruction.");
				}
				if (!((Component)val2).gameObject.activeSelf)
				{
					((Component)val2).gameObject.SetActive(true);
				}
				if (NetworkServer.active && identity.netId == 0)
				{
					NetworkServer.Spawn(((Component)val2).gameObject, 3964291274u, (NetworkConnection)null);
					if (identity.netId == 0)
					{
						throw new InvalidOperationException("Mirror did not assign a netId to the reconstructed DoorV2.");
					}
					num2++;
				}
			}
			catch (Exception ex)
			{
				if ((Object)(object)val2 != (Object)null)
				{
					((Component)val2).gameObject.SetActive(false);
				}
				log.LogError((object)("Vektor Kill House DoorV2 shell failed: socket=" + (((Object)(object)val2 == (Object)null || (Object)(object)val2.parent == (Object)null) ? "<unknown>" : ((Object)val2.parent).name) + ", " + ex.GetType().Name + ": " + ex.Message));
				return false;
			}
		}
		Physics.SyncTransforms();
		foreach (Transform item in array2.Where((Transform item) => !ValidateReconstructedDoorV2Shell(item, ((Component)item).GetComponent<DoorV2>()) || !DoorMatchesNativeOpening(item)))
		{
			log.LogError((object)("Vektor Kill House DoorV2 final validation detail: " + DescribeDoorValidation(item)));
		}
		int num4 = Enumerable.Count(array2, (Transform shell) => ValidateReconstructedDoorV2Shell(shell, ((Component)shell).GetComponent<DoorV2>()) && DoorMatchesNativeOpening(shell));
		int num5 = Enumerable.Count(array2, (Transform shell) => ((Component)shell).gameObject.activeSelf);
		int num6 = array2.Sum((Transform shell) => ((IEnumerable<MeshFilter>)((Component)shell).GetComponentsInChildren<MeshFilter>(true)).Count((MeshFilter filter) => (Object)(object)filter.sharedMesh != (Object)null && IsBuiltinPrimitiveName(((Object)filter.sharedMesh).name)));
		bool result = num4 == array2.Length && num5 == array2.Length && num6 == 0 && (!NetworkServer.active || array2.All((Transform shell) => ((Component)shell).GetComponent<NetworkIdentity>().netId != 0));
		log.LogInfo((object)("Vektor Kill House DoorV2 reconstruction: passed=" + result + ", shells=" + array2.Length + ", configured=" + num + ", complete=" + num4 + ", active=" + num5 + ", mirrorSpawned=" + num2 + ", registeredVanilla=" + flag2 + ", primitiveMeshes=" + num6 + "."));
		return result;
	}

	private static Dictionary<string, AudioClip> BuildDoorAudioLibrary(Transform bank)
	{
		Dictionary<string, AudioClip> dictionary = new Dictionary<string, AudioClip>(StringComparer.Ordinal);
		foreach (AudioSource componentsInChild in ((Component)bank).GetComponentsInChildren<AudioSource>(true))
		{
			if (!((Object)(object)componentsInChild == (Object)null) && !((Object)(object)componentsInChild.clip == (Object)null))
			{
				dictionary[((Object)componentsInChild.clip).name] = componentsInChild.clip;
			}
		}
		return dictionary;
	}

	private bool ValidateDoorAudioLibrary(IReadOnlyDictionary<string, AudioClip> clips)
	{
		string[] array = new string[5] { "wooden door opening ", "wooden door locked ", "wooden door closing ", "wooden door thud ", "wooden door breach " };
		int[] array2 = new int[5] { 10, 10, 10, 15, 2 };
		bool flag = clips.Count == 47;
		for (int i = 0; i < array.Length; i++)
		{
			for (int j = 1; j <= array2[i]; j++)
			{
				flag &= clips.ContainsKey(array[i] + j.ToString(CultureInfo.InvariantCulture));
			}
		}
		if (!flag)
		{
			log.LogError((object)("Vektor Kill House DoorV2 audio closure failed: uniqueClips=" + clips.Count + "."));
		}
		return flag;
	}

	private DoorV2 ConfigureNativeDoorV2Shell(Transform shell, IReadOnlyDictionary<string, AudioClip> clips, out NetworkIdentity identity)
	{
		//IL_03f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0406: Unknown result type (might be due to invalid IL or missing references)
		//IL_049b: Unknown result type (might be due to invalid IL or missing references)
		//IL_04a6: Unknown result type (might be due to invalid IL or missing references)
		((Component)shell).gameObject.SetActive(false);
		TrySetTag(((Component)shell).gameObject, "Door");
		identity = ((Component)shell).gameObject.AddComponent<NetworkIdentity>();
		identity.assetId = 3964291274u;
		DoorV2 val = ((Component)shell).gameObject.AddComponent<DoorV2>();
		((Component)shell).gameObject.AddComponent<ExcludeFromMirrorSpawnable>();
		MilkRigidbodySync val2 = ((Component)shell).gameObject.AddComponent<MilkRigidbodySync>();
		Transform val3 = RequirePath(shell, "Door Pivot and rigidbody");
		Transform myPushObject = RequirePath(val3, "Center");
		Transform val4 = RequirePath(val3, "Door Model");
		Transform val5 = RequirePath(val4, "cubey model/PLACEHOLDER DOOR MODEL");
		Transform obj = RequirePath(val4, "Door_interior");
		Transform val6 = RequirePath(val3, "Handle01");
		Transform obj2 = RequirePath(val3, "Handle02");
		Transform val7 = RequirePath(val3, "Hinge Top");
		Transform val8 = RequirePath(val3, "Hinge Bottom");
		Transform val9 = RequirePath(val3, "Lock");
		Transform val10 = RequirePath(val3, "Suburb Door Exploded");
		Transform source = RequirePath(shell, "Openable NavMesh Link Source");
		Transform source2 = RequirePath(shell, "Walkable NavMeshLink Source");
		Transform source3 = RequirePath(val3, "NavmeshCut");
		InteractionObject handle = ConfigureInteractionObject(RequirePath(val3, "InteractionL (4)"));
		InteractionObject handle2 = ConfigureInteractionObject(RequirePath(val3, "InteractionL (3)"));
		InteractionObject center = ConfigureInteractionObject(RequirePath(val3, "InteractionL Centre"));
		InteractionObject center2 = ConfigureInteractionObject(RequirePath(val3, "InteractionL Centre 2"));
		DoorHandleV2 val11 = ((Component)val6).gameObject.AddComponent<DoorHandleV2>();
		DoorHandleV2 val12 = ((Component)obj2).gameObject.AddComponent<DoorHandleV2>();
		val11.doorV2 = val;
		val12.doorV2 = val;
		val11.RivalDoorHandle = val12;
		val12.RivalDoorHandle = val11;
		val11.IsFrontHandle = true;
		val12.IsFrontHandle = false;
		val11.myPushObject = myPushObject;
		val12.myPushObject = myPushObject;
		val11.Handle = handle;
		val11.Center = center;
		val12.Handle = handle2;
		val12.Center = center2;
		ShootableDoorPart obj3 = ((Component)val9).gameObject.AddComponent<ShootableDoorPart>();
		ShootableDoorPart val13 = ((Component)val7).gameObject.AddComponent<ShootableDoorPart>();
		ShootableDoorPart val14 = ((Component)val8).gameObject.AddComponent<ShootableDoorPart>();
		obj3.Door = val;
		obj3.PartID = 1;
		val13.Door = val;
		val13.PartID = 2;
		val14.Door = val;
		val14.PartID = 3;
		DoorHitBox val15 = ((Component)val5).gameObject.AddComponent<DoorHitBox>();
		DoorHitBox obj4 = ((Component)obj).gameObject.AddComponent<DoorHitBox>();
		val15.Door = val;
		obj4.Door = val;
		foreach (MeshFilter componentsInChild in ((Component)shell).GetComponentsInChildren<MeshFilter>(true))
		{
			if ((Object)(object)componentsInChild != (Object)null && (Object)(object)componentsInChild.sharedMesh != (Object)null && !IsBuiltinPrimitiveName(((Object)componentsInChild.sharedMesh).name) && (Object)(object)((Component)componentsInChild).GetComponent<PolyFewHost>() == (Object)null)
			{
				((Component)componentsInChild).gameObject.AddComponent<PolyFewHost>();
			}
		}
		NodeLink2 doorOpenableNavLink = ConfigureNodeLink(source, 2u);
		NodeLink2 doorWalkableNavLink = ConfigureNodeLink(source2, 1u);
		NavmeshCut navMeshCut = ConfigureNavmeshCut(source3);
		Rigidbody val16 = RequireComponent<Rigidbody>(val3);
		BoxCollider val17 = RequireComponent<BoxCollider>(val5);
		BoxCollider latchCollider = RequireComponent<BoxCollider>(val9);
		BoxCollider hingeTopCollider = RequireComponent<BoxCollider>(val7);
		BoxCollider hingeBottomCollider = RequireComponent<BoxCollider>(val8);
		PhysicsMaterial material = ((Collider)val17).material;
		Rigidbody[] array = Il2CppArrayBase<Rigidbody>.op_Implicit(((Component)val10).GetComponentsInChildren<Rigidbody>(true));
		if (array.Length != 30)
		{
			throw new InvalidOperationException("Vanilla breach-body closure is " + array.Length + ", expected 30.");
		}
		((NetworkBehaviour)val2).syncDirection = (SyncDirection)0;
		((NetworkBehaviour)val2).syncMode = (SyncMode)0;
		((NetworkBehaviour)val2).syncInterval = 0f;
		((MilkTransformSync)val2).Active = false;
		((MilkTransformSync)val2).UpdatesPerSecond = 30f;
		((MilkTransformSync)val2).TransformToSync = val3;
		((MilkTransformSync)val2).SyncPosition = true;
		((MilkTransformSync)val2).SyncRotation = true;
		((MilkTransformSync)val2).UseLocalSpace = false;
		val2.RB = val16;
		val2.releaseOwnershipDelay = 2f;
		((NetworkBehaviour)val).syncDirection = (SyncDirection)0;
		((NetworkBehaviour)val).syncMode = (SyncMode)0;
		((NetworkBehaviour)val).syncInterval = 0.1f;
		val.PivotTransform = val3;
		val.HandleFront = val11;
		val.HandleBack = val12;
		val.DoorModelParent = ((Component)val4).gameObject;
		val.rb = val16;
		val.DoorMask = LayerMask.op_Implicit(4545);
		val.PlayerMovementLayerMask = LayerMask.op_Implicit(33554436);
		val.DoorPhysicsMaterial = material;
		val.DoorPhysicsSync = val2;
		val.DoorHitBox = val17;
		val.maxRotationY = 110f;
		val.Invert = false;
		val.Damping = 0.5f;
		val.StartLocked = false;
		val.StartLockedChance = 0f;
		val.lockedMesh = new Il2CppReferenceArray<GameObject>(0L);
		val.unlockedMesh = new Il2CppReferenceArray<GameObject>(0L);
		val.AiCantOpen = false;
		val.AiCantOpenChance = 0f;
		val.DoorOpenableNavLink = doorOpenableNavLink;
		val.DoorWalkableNavLink = doorWalkableNavLink;
		val.NavMeshCut = navMeshCut;
		val.navCutOpenSize = Vector3.zero;
		val.navCutCloseSize = Vector3.zero;
		val.LatchHealth = 400f;
		val.hinge01_Health = 400f;
		val.hinge02_Health = 400f;
		val.latchCollider = latchCollider;
		val.HingeTopCollider = hingeTopCollider;
		val.HingeBottomCollider = hingeBottomCollider;
		val.audioSource = RequireComponent<AudioSource>(RequirePath(val3, "AudioSource"));
		val.doorUnlock = AudioSeries(clips, "wooden door opening ", 10);
		val.doorLocked = AudioSeries(clips, "wooden door locked ", 10);
		val.doorClose = AudioSeries(clips, "wooden door closing ", 10);
		val.doorThud = AudioSeries(clips, "wooden door thud ", 15);
		val.doorBreach = AudioSeries(clips, "wooden door breach ", 2);
		val.deadDoorSpringStrength = 400f;
		val.deadDoorDamping = 8f;
		val.deadDoorAngularDamping = 5f;
		val.deadDoorScrollForce = 200f;
		val.deadDoorWalkForce = 200f;
		val.canBlowup = false;
		val.DestroyedDoor = ((Component)val10).gameObject;
		val.DestroyedDoorRB = ToReferenceArray(array);
		val.canLatch = true;
		val.IsLatched = true;
		return val;
	}

	private static InteractionObject ConfigureInteractionObject(Transform interactionTransform)
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Expected O, but got Unknown
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Expected O, but got Unknown
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Expected O, but got Unknown
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Expected O, but got Unknown
		//IL_00e3: Expected O, but got Unknown
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		InteractionObject obj = ((Component)interactionTransform).gameObject.AddComponent<InteractionObject>();
		WeightCurve val = new WeightCurve
		{
			type = (Type)0,
			curve = CreateDoorInteractionCurve()
		};
		obj.weightCurves = new Il2CppReferenceArray<WeightCurve>(1L);
		((Il2CppArrayBase<WeightCurve>)(object)obj.weightCurves)[0] = val;
		obj.multipliers = new Il2CppReferenceArray<Multiplier>(2L);
		((Il2CppArrayBase<Multiplier>)(object)obj.multipliers)[0] = new Multiplier
		{
			curve = (Type)0,
			multiplier = 1f,
			result = (Type)7
		};
		((Il2CppArrayBase<Multiplier>)(object)obj.multipliers)[1] = new Multiplier
		{
			curve = (Type)0,
			multiplier = 1f,
			result = (Type)10
		};
		InteractionEvent val2 = new InteractionEvent
		{
			time = 0.5f,
			pause = true,
			pickUp = false,
			animations = new Il2CppReferenceArray<AnimatorEvent>(0L),
			messages = new Il2CppReferenceArray<Message>(0L),
			unityEvent = new UnityEvent()
		};
		obj.events = new Il2CppReferenceArray<InteractionEvent>(1L);
		((Il2CppArrayBase<InteractionEvent>)(object)obj.events)[0] = val2;
		InteractionTarget obj2 = ((Component)RequirePath(interactionTransform, "hand_l")).gameObject.AddComponent<InteractionTarget>();
		obj2.effectorType = (FullBodyBipedEffector)5;
		obj2.multipliers = new Il2CppReferenceArray<Multiplier>(0L);
		obj2.interactionSpeedMlp = 1f;
		obj2.pivot = null;
		obj2.rotationMode = (RotationMode)0;
		obj2.twistAxis = Vector3.up;
		obj2.twistWeight = 1f;
		obj2.swingWeight = 0f;
		obj2.threeDOFWeight = 1f;
		obj2.rotateOnce = true;
		return obj;
	}

	private static AnimationCurve CreateDoorInteractionCurve()
	{
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d4: Expected O, but got Unknown
		Keyframe val = default(Keyframe);
		((Keyframe)(ref val))._002Ector(0f, 0.026881754f, 1.9333475f, 1.9333475f, 0f, 1f / 3f);
		Keyframe val2 = default(Keyframe);
		((Keyframe)(ref val2))._002Ector(0.50333333f, 1f, -0.012975514f, -0.012975514f, 1f / 3f, 1f / 3f);
		Keyframe val3 = default(Keyframe);
		((Keyframe)(ref val3))._002Ector(1f, 0.026881754f, -1.9592985f, -1.9592985f, 1f / 3f, 0f);
		((Keyframe)(ref val)).tangentMode = 34;
		((Keyframe)(ref val2)).tangentMode = 34;
		((Keyframe)(ref val3)).tangentMode = 34;
		((Keyframe)(ref val)).weightedMode = (WeightedMode)0;
		((Keyframe)(ref val2)).weightedMode = (WeightedMode)0;
		((Keyframe)(ref val3)).weightedMode = (WeightedMode)0;
		return new AnimationCurve((Keyframe[])(object)new Keyframe[3] { val, val2, val3 })
		{
			preWrapMode = (WrapMode)2,
			postWrapMode = (WrapMode)2
		};
	}

	private static NodeLink2 ConfigureNodeLink(Transform source, uint tag)
	{
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		NodeLink2 obj = ((Component)source).gameObject.AddComponent<NodeLink2>();
		((VersionedMonoBehaviour)obj).version = 1073741824;
		obj.end = RequirePath(source, "NavMesh Link Dest");
		obj.costFactor = 1f;
		obj.oneWay = false;
		obj.pathfindingTag = new PathfindingTag(tag);
		obj.graphMask = new GraphMask
		{
			value = -1
		};
		return obj;
	}

	private static NavmeshCut ConfigureNavmeshCut(Transform source)
	{
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		NavmeshCut obj = ((Component)source).gameObject.AddComponent<NavmeshCut>();
		((VersionedMonoBehaviour)obj).version = 1073741824;
		((NavmeshClipper)obj).graphMask = new GraphMask
		{
			value = 1
		};
		obj.type = (MeshType)3;
		obj.mesh = null;
		obj.rectangleSize = new Vector2(1.2f, 0.145f);
		obj.circleRadius = 1f;
		obj.circleResolution = 6;
		obj.height = 2.19f;
		obj.meshScale = 1f;
		obj.center = new Vector3(0.63f, 0.94f, 0f);
		obj.updateDistance = 0.4f;
		obj.isDual = false;
		obj.radiusExpansionMode = (RadiusExpansionMode)1;
		obj.cutsAddedGeom = true;
		obj.updateRotationDistance = 10f;
		obj.useRotationAndScale = true;
		return obj;
	}

	private static Il2CppReferenceArray<AudioClip> AudioSeries(IReadOnlyDictionary<string, AudioClip> clips, string prefix, int count)
	{
		Il2CppReferenceArray<AudioClip> val = new Il2CppReferenceArray<AudioClip>((long)count);
		for (int i = 1; i <= count; i++)
		{
			((Il2CppArrayBase<AudioClip>)(object)val)[i - 1] = clips[prefix + i.ToString(CultureInfo.InvariantCulture)];
		}
		return val;
	}

	private static Il2CppReferenceArray<Rigidbody> ToReferenceArray(IReadOnlyList<Rigidbody> values)
	{
		Il2CppReferenceArray<Rigidbody> val = new Il2CppReferenceArray<Rigidbody>((long)values.Count);
		for (int i = 0; i < values.Count; i++)
		{
			((Il2CppArrayBase<Rigidbody>)(object)val)[i] = values[i];
		}
		return val;
	}

	private static bool IsCompleteDoorV2(DoorV2 door)
	{
		if ((Object)(object)door != (Object)null && (Object)(object)door.PivotTransform != (Object)null && (Object)(object)door.HandleFront != (Object)null && (Object)(object)door.HandleBack != (Object)null && (Object)(object)door.DoorModelParent != (Object)null && (Object)(object)door.rb != (Object)null && (Object)(object)door.DoorPhysicsMaterial != (Object)null && (Object)(object)door.DoorPhysicsSync != (Object)null && (Object)(object)door.DoorHitBox != (Object)null && (Object)(object)door.latchCollider != (Object)null && (Object)(object)door.HingeTopCollider != (Object)null && (Object)(object)door.HingeBottomCollider != (Object)null && (Object)(object)door.DoorOpenableNavLink != (Object)null && (Object)(object)door.DoorWalkableNavLink != (Object)null && (Object)(object)door.NavMeshCut != (Object)null && (Object)(object)door.audioSource != (Object)null && (Object)(object)door.DestroyedDoor != (Object)null && door.DestroyedDoorRB != null && ((Il2CppArrayBase<Rigidbody>)(object)door.DestroyedDoorRB).Length == 30 && door.doorUnlock != null && ((Il2CppArrayBase<AudioClip>)(object)door.doorUnlock).Length == 10 && door.doorLocked != null && ((Il2CppArrayBase<AudioClip>)(object)door.doorLocked).Length == 10 && door.doorClose != null && ((Il2CppArrayBase<AudioClip>)(object)door.doorClose).Length == 10 && door.doorThud != null && ((Il2CppArrayBase<AudioClip>)(object)door.doorThud).Length == 15 && door.doorBreach != null)
		{
			return ((Il2CppArrayBase<AudioClip>)(object)door.doorBreach).Length == 2;
		}
		return false;
	}

	private static bool ValidateReconstructedDoorV2Shell(Transform shell, DoorV2 door)
	{
		if (!IsCompleteDoorV2(door))
		{
			return false;
		}
		Component[] array = Il2CppArrayBase<Component>.op_Implicit(((Component)shell).GetComponents<Component>());
		bool num = array.Length == 5 && SameNativeComponent(array[0], (Component)(object)shell) && SameNativeComponent(array[1], (Component)(object)((Component)shell).GetComponent<NetworkIdentity>()) && SameNativeComponent(array[2], (Component)(object)door) && SameNativeComponent(array[3], (Component)(object)((Component)shell).GetComponent<ExcludeFromMirrorSpawnable>()) && SameNativeComponent(array[4], (Component)(object)((Component)shell).GetComponent<MilkRigidbodySync>());
		int num2 = ((IEnumerable<MeshFilter>)((Component)shell).GetComponentsInChildren<MeshFilter>(true)).Count((MeshFilter filter) => (Object)(object)filter.sharedMesh != (Object)null && IsBuiltinPrimitiveName(((Object)filter.sharedMesh).name));
		Transform val = shell.Find("Door Pivot and rigidbody");
		Transform val2 = shell.Find("Door Pivot and rigidbody/Door Model/Door_interior");
		MeshFilter val3 = (((Object)(object)val2 == (Object)null) ? null : ((Component)val2).GetComponent<MeshFilter>());
		Renderer val4 = (((Object)(object)val2 == (Object)null) ? null : ((Component)val2).GetComponent<Renderer>());
		int num3 = ((IEnumerable<Transform>)((Component)shell).GetComponentsInChildren<Transform>(true)).Count((Transform value) => ((Object)value).name.StartsWith("SHADOW BLOCKER Door_interior", StringComparison.Ordinal));
		bool flag = (Object)(object)val != (Object)null && (Object)(object)val2 != (Object)null && val2.IsChildOf(val) && (Object)(object)val3 != (Object)null && (Object)(object)val3.sharedMesh != (Object)null && ((Object)val3.sharedMesh).name.StartsWith("SM_Door_2_LOD0", StringComparison.Ordinal) && (Object)(object)val4 != (Object)null && val4.enabled && num3 == 0;
		if (num && flag && ((Component)shell).GetComponentsInChildren<Transform>(true).Length == 118 && ((Component)shell).GetComponentsInChildren<MeshFilter>(true).Length == 31 && ((Component)shell).GetComponentsInChildren<Renderer>(true).Length == 31 && ((Component)shell).GetComponentsInChildren<BoxCollider>(true).Length == 35 && ((Component)shell).GetComponentsInChildren<Rigidbody>(true).Length == 31 && ((Component)shell).GetComponentsInChildren<AudioSource>(true).Length == 1 && ((Component)shell).GetComponentsInChildren<DoorHandleV2>(true).Length == 2 && ((Component)shell).GetComponentsInChildren<InteractionObject>(true).Length == 4 && ((Component)shell).GetComponentsInChildren<InteractionTarget>(true).Length == 4 && ((Component)shell).GetComponentsInChildren<ShootableDoorPart>(true).Length == 3 && ((Component)shell).GetComponentsInChildren<DoorHitBox>(true).Length == 2 && ((Component)shell).GetComponentsInChildren<NodeLink2>(true).Length == 2 && ((Component)shell).GetComponentsInChildren<NavmeshCut>(true).Length == 1 && ((Component)shell).GetComponentsInChildren<PolyFewHost>(true).Length == 31)
		{
			return num2 == 0;
		}
		return false;
	}

	private static bool DoorMatchesNativeOpening(Transform shell)
	{
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0091: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_010a: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01be: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0204: Unknown result type (might be due to invalid IL or missing references)
		//IL_020a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0214: Unknown result type (might be due to invalid IL or missing references)
		//IL_0219: Unknown result type (might be due to invalid IL or missing references)
		//IL_021e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0220: Unknown result type (might be due to invalid IL or missing references)
		//IL_0222: Unknown result type (might be due to invalid IL or missing references)
		//IL_022c: Unknown result type (might be due to invalid IL or missing references)
		//IL_022e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0238: Unknown result type (might be due to invalid IL or missing references)
		//IL_023f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0246: Unknown result type (might be due to invalid IL or missing references)
		//IL_024b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0252: Unknown result type (might be due to invalid IL or missing references)
		//IL_0259: Unknown result type (might be due to invalid IL or missing references)
		//IL_0266: Unknown result type (might be due to invalid IL or missing references)
		//IL_0283: Unknown result type (might be due to invalid IL or missing references)
		//IL_029a: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d4: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)shell == (Object)null || (Object)(object)shell.parent == (Object)null || (Object)(object)shell.parent.parent == (Object)null || !((Object)shell.parent).name.StartsWith("DOORV2_SOCKET_", StringComparison.Ordinal))
		{
			return false;
		}
		string text = ((Object)shell.parent).name.Substring("DOORV2_SOCKET_".Length);
		Transform val = shell.parent.parent.Find("NATIVE_DoorWall_" + text);
		if ((Object)(object)val == (Object)null)
		{
			return false;
		}
		Vector3 val2 = shell.parent.forward;
		Vector3 normalized = ((Vector3)(ref val2)).normalized;
		val2 = val.right;
		float num = Mathf.Abs(Vector3.Dot(normalized, ((Vector3)(ref val2)).normalized));
		val2 = shell.forward;
		Vector3 normalized2 = ((Vector3)(ref val2)).normalized;
		val2 = val.right;
		float num2 = Mathf.Abs(Vector3.Dot(normalized2, ((Vector3)(ref val2)).normalized));
		DoorV2 component = ((Component)shell).GetComponent<DoorV2>();
		bool flag = (Object)(object)component != (Object)null && (Object)(object)component.PivotTransform != (Object)null && Quaternion.Angle(component.PivotTransform.localRotation, Quaternion.identity) <= 0.5f;
		Transform val3 = shell.Find("Door Pivot and rigidbody/Door Model/cubey model/PLACEHOLDER DOOR MODEL");
		Transform val4 = shell.Find("Door Pivot and rigidbody/Door Model/Door_interior");
		BoxCollider val5 = (((Object)(object)val3 == (Object)null) ? null : ((Component)val3).GetComponent<BoxCollider>());
		MeshFilter val6 = (((Object)(object)val4 == (Object)null) ? null : ((Component)val4).GetComponent<MeshFilter>());
		MeshFilter componentInChildren = ((Component)val).GetComponentInChildren<MeshFilter>(true);
		if ((Object)(object)val5 == (Object)null || (Object)(object)val6 == (Object)null || (Object)(object)val6.sharedMesh == (Object)null || (Object)(object)componentInChildren == (Object)null || (Object)(object)componentInChildren.sharedMesh == (Object)null)
		{
			return false;
		}
		Vector3 val7 = ((Component)val5).transform.TransformPoint(val5.center);
		Transform transform = ((Component)val6).transform;
		Bounds bounds = val6.sharedMesh.bounds;
		Vector3 val8 = transform.TransformPoint(((Bounds)(ref bounds)).center);
		Transform transform2 = ((Component)componentInChildren).transform;
		bounds = componentInChildren.sharedMesh.bounds;
		Vector3 expected = transform2.TransformPoint(((Bounds)(ref bounds)).center) + val.forward * -0.0391f;
		float num3 = HorizontalDistanceInWallPlane(val7, expected, val);
		float num4 = HorizontalDistanceInWallPlane(val8, expected, val);
		float num5 = Vector2.Distance(new Vector2(val8.x, val8.z), new Vector2(val7.x, val7.z));
		bool flag2 = Mathf.Abs(shell.localPosition.x + 0.50283f) <= 0.005f && Mathf.Abs(shell.localPosition.y) <= 0.005f && Mathf.Abs(shell.localPosition.z) <= 0.005f;
		if (num >= 0.999f && num2 >= 0.999f && Quaternion.Angle(shell.parent.rotation, shell.rotation) <= 0.1f && flag && flag2 && num3 <= 0.035f && num4 <= 0.035f)
		{
			return num5 <= 0.035f;
		}
		return false;
	}

	private static float HorizontalDistanceInWallPlane(Vector3 value, Vector3 expected, Transform wall)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		Vector3 val = value - expected;
		Vector3 val2 = wall.right;
		float num = Vector3.Dot(val, ((Vector3)(ref val2)).normalized);
		val2 = wall.forward;
		float num2 = Vector3.Dot(val, ((Vector3)(ref val2)).normalized);
		return Mathf.Sqrt(num * num + num2 * num2);
	}

	private static bool SameNativeComponent(Component left, Component right)
	{
		if ((Object)(object)left != (Object)null && (Object)(object)right != (Object)null)
		{
			return ((Object)left).GetInstanceID() == ((Object)right).GetInstanceID();
		}
		return false;
	}

	private static string DescribeDoorValidation(Transform shell)
	{
		if ((Object)(object)shell == (Object)null)
		{
			return "shell=<null>";
		}
		DoorV2 component = ((Component)shell).GetComponent<DoorV2>();
		Component[] source = Il2CppArrayBase<Component>.op_Implicit(((Component)shell).GetComponents<Component>());
		return "socket=" + (((Object)(object)shell.parent == (Object)null) ? "<none>" : ((Object)shell.parent).name) + ", core=" + IsCompleteDoorV2(component) + ", rootComponents=" + string.Join("|", source.Select((Component item) => (!((Object)(object)item == (Object)null)) ? ((object)item).GetType().Name : "<null>")) + ", transforms=" + ((Component)shell).GetComponentsInChildren<Transform>(true).Length + ", meshFilters=" + ((Component)shell).GetComponentsInChildren<MeshFilter>(true).Length + ", renderers=" + ((Component)shell).GetComponentsInChildren<Renderer>(true).Length + ", boxes=" + ((Component)shell).GetComponentsInChildren<BoxCollider>(true).Length + ", bodies=" + ((Component)shell).GetComponentsInChildren<Rigidbody>(true).Length + ", audio=" + ((Component)shell).GetComponentsInChildren<AudioSource>(true).Length + ", handles=" + ((Component)shell).GetComponentsInChildren<DoorHandleV2>(true).Length + ", interactions=" + ((Component)shell).GetComponentsInChildren<InteractionObject>(true).Length + ", targets=" + ((Component)shell).GetComponentsInChildren<InteractionTarget>(true).Length + ", shootable=" + ((Component)shell).GetComponentsInChildren<ShootableDoorPart>(true).Length + ", hitBoxes=" + ((Component)shell).GetComponentsInChildren<DoorHitBox>(true).Length + ", links=" + ((Component)shell).GetComponentsInChildren<NodeLink2>(true).Length + ", cuts=" + ((Component)shell).GetComponentsInChildren<NavmeshCut>(true).Length + ", polyFew=" + ((Component)shell).GetComponentsInChildren<PolyFewHost>(true).Length + ", alignedWithOpening=" + DoorMatchesNativeOpening(shell) + ", primitives=" + ((IEnumerable<MeshFilter>)((Component)shell).GetComponentsInChildren<MeshFilter>(true)).Count((MeshFilter filter) => (Object)(object)filter.sharedMesh != (Object)null && IsBuiltinPrimitiveName(((Object)filter.sharedMesh).name)) + ", physicsMaterial=" + ((Object)(object)component != (Object)null && (Object)(object)component.DoorPhysicsMaterial != (Object)null) + ", unlock=" + (((Object)(object)component == (Object)null || component.doorUnlock == null) ? (-1) : ((Il2CppArrayBase<AudioClip>)(object)component.doorUnlock).Length) + ", locked=" + (((Object)(object)component == (Object)null || component.doorLocked == null) ? (-1) : ((Il2CppArrayBase<AudioClip>)(object)component.doorLocked).Length) + ", close=" + (((Object)(object)component == (Object)null || component.doorClose == null) ? (-1) : ((Il2CppArrayBase<AudioClip>)(object)component.doorClose).Length) + ", thud=" + (((Object)(object)component == (Object)null || component.doorThud == null) ? (-1) : ((Il2CppArrayBase<AudioClip>)(object)component.doorThud).Length) + ", breach=" + (((Object)(object)component == (Object)null || component.doorBreach == null) ? (-1) : ((Il2CppArrayBase<AudioClip>)(object)component.doorBreach).Length) + ".";
	}

	private static Transform RequirePath(Transform root, string path)
	{
		Transform obj = root.Find(path);
		if ((Object)(object)obj == (Object)null)
		{
			throw new InvalidOperationException("DoorV2 transform is missing: " + path + ".");
		}
		return obj;
	}

	private static T RequireComponent<T>(Transform transform) where T : Component
	{
		T component = ((Component)transform).GetComponent<T>();
		if ((Object)(object)component == (Object)null)
		{
			throw new InvalidOperationException("DoorV2 component is missing: " + typeof(T).Name + " on " + ((Object)transform).name + ".");
		}
		return component;
	}

	private static bool IsBuiltinPrimitiveName(string name)
	{
		if (!string.Equals(name, "Cube", StringComparison.Ordinal) && !string.Equals(name, "Sphere", StringComparison.Ordinal) && !string.Equals(name, "Capsule", StringComparison.Ordinal) && !string.Equals(name, "Cylinder", StringComparison.Ordinal) && !string.Equals(name, "Plane", StringComparison.Ordinal))
		{
			return string.Equals(name, "Quad", StringComparison.Ordinal);
		}
		return true;
	}

	private static void TrySetTag(GameObject gameObject, string tag)
	{
		try
		{
			gameObject.tag = tag;
		}
		catch (Exception)
		{
			gameObject.tag = "Untagged";
		}
	}

	private void AuditResidentDoorTemplates(string phase, bool force)
	{
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		if (residentDoorAuditLogged && !force)
		{
			return;
		}
		try
		{
			Il2CppReferenceArray<Object> obj = Resources.FindObjectsOfTypeAll(Il2CppType.Of<DoorV2>());
			int num = 0;
			List<string> list = new List<string>();
			foreach (Object item in (Il2CppArrayBase<Object>)(object)obj)
			{
				DoorV2 val = ((item == (Object)null) ? null : ((Il2CppObjectBase)item).TryCast<DoorV2>());
				if (!((Object)(object)val == (Object)null) && !((Object)(object)((Component)val).gameObject == (Object)null))
				{
					bool flag = (Object)(object)val.PivotTransform != (Object)null && (Object)(object)val.HandleFront != (Object)null && (Object)(object)val.HandleBack != (Object)null && (Object)(object)val.rb != (Object)null && (Object)(object)val.DoorPhysicsSync != (Object)null && (Object)(object)val.DoorHitBox != (Object)null && (Object)(object)val.latchCollider != (Object)null && (Object)(object)val.HingeTopCollider != (Object)null && (Object)(object)val.HingeBottomCollider != (Object)null && (Object)(object)val.DoorOpenableNavLink != (Object)null && (Object)(object)val.DoorWalkableNavLink != (Object)null && (Object)(object)val.NavMeshCut != (Object)null && (Object)(object)val.audioSource != (Object)null && (Object)(object)val.DestroyedDoor != (Object)null;
					if (flag)
					{
						num++;
					}
					Scene scene = ((Component)val).gameObject.scene;
					list.Add("name=" + ((Object)((Component)val).gameObject).name + ", scene=" + (((Scene)(ref scene)).IsValid() ? ((Scene)(ref scene)).path : "<asset-or-persistent>") + ", active=" + ((Component)val).gameObject.activeInHierarchy + ", complete=" + flag);
				}
			}
			GameObject val2 = null;
			bool flag2 = NetworkClient.GetPrefab(3964291274u, ref val2) && (Object)(object)val2 != (Object)null;
			DoorV2 val3 = (flag2 ? val2.GetComponent<DoorV2>() : null);
			bool flag3 = (Object)(object)val3 != (Object)null && (Object)(object)val3.PivotTransform != (Object)null && (Object)(object)val3.HandleFront != (Object)null && (Object)(object)val3.HandleBack != (Object)null && (Object)(object)val3.rb != (Object)null && (Object)(object)val3.DoorPhysicsSync != (Object)null && (Object)(object)val3.DoorOpenableNavLink != (Object)null && (Object)(object)val3.DoorWalkableNavLink != (Object)null && (Object)(object)val3.NavMeshCut != (Object)null && (Object)(object)val3.DestroyedDoor != (Object)null;
			residentDoorAuditLogged = true;
			log.LogInfo((object)("Vektor Kill House resident DoorV2 audit: phase=" + phase + ", total=" + list.Count + ", complete=" + num + ", officialAssetId=" + 3964291274u + ", registered=" + flag2 + ", registeredComplete=" + flag3 + ", registeredName=" + (((Object)(object)val2 == (Object)null) ? "<null>" : ((Object)val2).name) + ", records=[" + string.Join(" | ", list.Take(24)) + "]."));
		}
		catch (Exception ex)
		{
			log.LogWarning((object)("Vektor Kill House resident DoorV2 audit failed: phase=" + phase + ", " + ex.GetType().Name + ": " + ex.Message));
		}
	}

	private bool ValidateSceneContract(GameObject root)
	{
		//IL_052b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0531: Unknown result type (might be due to invalid IL or missing references)
		//IL_0522: Unknown result type (might be due to invalid IL or missing references)
		//IL_0533: Unknown result type (might be due to invalid IL or missing references)
		//IL_0553: Unknown result type (might be due to invalid IL or missing references)
		//IL_0589: Unknown result type (might be due to invalid IL or missing references)
		//IL_058f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0580: Unknown result type (might be due to invalid IL or missing references)
		//IL_0591: Unknown result type (might be due to invalid IL or missing references)
		//IL_05a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_05b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_05c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_05d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_063d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0642: Unknown result type (might be due to invalid IL or missing references)
		//IL_05e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_05f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0605: Unknown result type (might be due to invalid IL or missing references)
		//IL_0611: Unknown result type (might be due to invalid IL or missing references)
		//IL_06e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_06eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_06ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_06fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0717: Unknown result type (might be due to invalid IL or missing references)
		//IL_071c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0720: Unknown result type (might be due to invalid IL or missing references)
		//IL_072c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0748: Unknown result type (might be due to invalid IL or missing references)
		//IL_074d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0751: Unknown result type (might be due to invalid IL or missing references)
		//IL_075d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0776: Unknown result type (might be due to invalid IL or missing references)
		//IL_077b: Unknown result type (might be due to invalid IL or missing references)
		//IL_077f: Unknown result type (might be due to invalid IL or missing references)
		//IL_078b: Unknown result type (might be due to invalid IL or missing references)
		//IL_07a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_07a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_07ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_07b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0945: Unknown result type (might be due to invalid IL or missing references)
		//IL_094a: Unknown result type (might be due to invalid IL or missing references)
		//IL_094e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0953: Unknown result type (might be due to invalid IL or missing references)
		Transform[] source = Il2CppArrayBase<Transform>.op_Implicit(root.GetComponentsInChildren<Transform>(true));
		MeshFilter[] source2 = Il2CppArrayBase<MeshFilter>.op_Implicit(root.GetComponentsInChildren<MeshFilter>(true));
		string[] primitiveNames = new string[6] { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };
		int num = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "MAP_ID_community.vektor-modular-killhouse.modular-killhouse", StringComparison.Ordinal));
		int num2 = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "SPAWN_SET_killhouse-pve", StringComparison.Ordinal));
		int num3 = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "SPAWN_SET_killhouse-pvp", StringComparison.Ordinal));
		int num4 = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "FIXED_SAFE_ROOM_KH_SAFE_ROOM_V1", StringComparison.Ordinal));
		int num5 = Enumerable.Count(source, (Transform item) => ((Object)item).name.StartsWith("PVE_PlayerSpawn_", StringComparison.Ordinal));
		int num6 = Enumerable.Count(source, (Transform item) => ((Object)item).name.StartsWith("PVP_Team1Spawn_", StringComparison.Ordinal));
		int num7 = Enumerable.Count(source, (Transform item) => ((Object)item).name.StartsWith("PVP_Team2Spawn_", StringComparison.Ordinal));
		int num8 = Enumerable.Count(source, (Transform item) => ((Object)item).name.StartsWith("PVE_EnemySpawn_", StringComparison.Ordinal));
		Transform[] array = source.Where((Transform item) => ((Object)item).name.StartsWith("PVE_ExfilZone_", StringComparison.Ordinal)).ToArray();
		Transform[] array2 = source.Where((Transform item) => string.Equals(((Object)item).name, "NATIVE_Floor", StringComparison.Ordinal)).ToArray();
		int num9 = array2.Length;
		int num10 = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "NATIVE_Ceiling", StringComparison.Ordinal));
		Transform[] array3 = source.Where((Transform item) => ((Object)item).name.StartsWith("NATIVE_ConnectorFloor_", StringComparison.Ordinal)).ToArray();
		int num11 = array3.Length;
		int num12 = Enumerable.Count(source, (Transform item) => ((Object)item).name.StartsWith("NATIVE_ConnectorCeiling_", StringComparison.Ordinal));
		int num13 = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "NATIVE_WarehousePvpCompleteShell", StringComparison.Ordinal));
		string[] warehousePartNames = new string[4] { "NATIVE_WarehouseBase", "NATIVE_WarehouseOverHeadSupport", "NATIVE_WarehouseRoof", "NATIVE_WarehouseSupport2" };
		Transform[] array4 = source.Where((Transform item) => warehousePartNames.Contains(((Object)item).name)).ToArray();
		int num14 = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "WAREHOUSE_PREFAB_PVP_WOODS_EXACT_FOUR_PART", StringComparison.Ordinal));
		int num15 = Enumerable.Count(array4, (Transform part) => !AllRendererSlotsUseNativeProfile(part, "RM_Steel_smooth"));
		int num16 = CountRendererSlotsUsingNativeProfile(source.FirstOrDefault((Transform item) => ((Object)item).name == "NATIVE_WarehousePvpCompleteShell"), "RM_Steel_smooth");
		int num17 = array4.Sum((Transform part) => ((Component)part).GetComponentsInChildren<MeshCollider>(true).Length);
		Transform[] array5 = source.Where((Transform item) => string.Equals(((Object)item).name, "NATIVE_WarehouseGroundApron", StringComparison.Ordinal)).ToArray();
		Transform val = array5.FirstOrDefault();
		MeshFilter[] array6 = (((Object)(object)val == (Object)null) ? Array.Empty<MeshFilter>() : Il2CppArrayBase<MeshFilter>.op_Implicit(((Component)val).GetComponentsInChildren<MeshFilter>(true)));
		MeshRenderer[] array7 = (((Object)(object)val == (Object)null) ? Array.Empty<MeshRenderer>() : Il2CppArrayBase<MeshRenderer>.op_Implicit(((Component)val).GetComponentsInChildren<MeshRenderer>(true)));
		MeshFilter val2 = array6.FirstOrDefault();
		MeshRenderer val3 = array7.FirstOrDefault();
		MeshCollider val4 = (((Object)(object)val == (Object)null) ? null : ((Component)val).GetComponent<MeshCollider>());
		int num18 = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "WAREHOUSE_GROUND_LEVEL11_FLOOR_MESH152_MATERIAL26", StringComparison.Ordinal));
		int num19 = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "WAREHOUSE_GROUND_PROVENANCE_APPEARANCE_GO104_GEOMETRY_GO9601", StringComparison.Ordinal));
		int num20 = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "WAREHOUSE_APRON_NAV_EXCLUDED_ENCLOSED_PERIMETER", StringComparison.Ordinal));
		string failure;
		bool flag = WarehouseGroundMeshContractValid(((Object)(object)val2 == (Object)null) ? null : val2.sharedMesh, out failure);
		Renderer[] array8 = (from item in array2.Concat(array3)
			select ((Component)item).GetComponentInChildren<Renderer>(true) into item
			where (Object)(object)item != (Object)null
			select item).ToArray();
		bool flag2 = array8.Length == num9 + num11;
		Bounds val5;
		Bounds val6;
		if (array8.Length != 0)
		{
			val5 = array8[0].bounds;
		}
		else
		{
			val6 = default(Bounds);
			val5 = val6;
		}
		Bounds val7 = val5;
		foreach (Renderer item in array8.Skip(1))
		{
			((Bounds)(ref val7)).Encapsulate(item.bounds);
		}
		Bounds val8;
		if (!((Object)(object)val3 == (Object)null))
		{
			val8 = ((Renderer)val3).bounds;
		}
		else
		{
			val6 = default(Bounds);
			val8 = val6;
		}
		Bounds val9 = val8;
		bool flag3 = (Object)(object)val3 != (Object)null && flag2 && ((Bounds)(ref val9)).min.x <= ((Bounds)(ref val7)).min.x - 3.98f && ((Bounds)(ref val9)).max.x >= ((Bounds)(ref val7)).max.x + 3.98f && ((Bounds)(ref val9)).min.z <= ((Bounds)(ref val7)).min.z - 3.98f && ((Bounds)(ref val9)).max.z >= ((Bounds)(ref val7)).max.z + 3.98f;
		float num21 = (((Object)(object)val3 == (Object)null) ? float.NaN : root.transform.InverseTransformPoint(((Bounds)(ref val9)).center).y);
		bool flag4 = Mathf.Abs(num21 - -0.015f) <= 0.002f;
		int num22;
		if ((Object)(object)val != (Object)null && (Object)(object)val4 != (Object)null && (Object)(object)val2 != (Object)null && ((Component)val).GetComponentsInChildren<Collider>(true).Length == 1 && (Object)(object)val4.sharedMesh == (Object)(object)val2.sharedMesh && ((Collider)val4).enabled && !((Collider)val4).isTrigger && !val4.convex)
		{
			val6 = ((Collider)val4).bounds;
			if (Mathf.Abs(((Bounds)(ref val6)).center.x - ((Bounds)(ref val9)).center.x) <= 0.002f)
			{
				val6 = ((Collider)val4).bounds;
				if (Mathf.Abs(((Bounds)(ref val6)).center.y - ((Bounds)(ref val9)).center.y) <= 0.002f)
				{
					val6 = ((Collider)val4).bounds;
					if (Mathf.Abs(((Bounds)(ref val6)).center.z - ((Bounds)(ref val9)).center.z) <= 0.002f)
					{
						val6 = ((Collider)val4).bounds;
						if (Mathf.Abs(((Bounds)(ref val6)).size.x - ((Bounds)(ref val9)).size.x) <= 0.002f)
						{
							val6 = ((Collider)val4).bounds;
							num22 = ((Mathf.Abs(((Bounds)(ref val6)).size.z - ((Bounds)(ref val9)).size.z) <= 0.002f) ? 1 : 0);
							goto IL_07d6;
						}
					}
				}
			}
		}
		num22 = 0;
		goto IL_07d6;
		IL_07d6:
		bool flag5 = (byte)num22 != 0;
		Transform val10 = root.transform.Find("05_HIGH_WAREHOUSE_SHELL");
		bool flag6 = array5.Length == 1 && (Object)(object)val != (Object)null && (Object)(object)val.parent == (Object)(object)val10 && array6.Length == 1 && array7.Length == 1 && ((Il2CppArrayBase<Material>)(object)((Renderer)val3).sharedMaterials).Length == 1 && AllRendererSlotsUseNativeProfile(val, "Floor") && flag && flag3 && flag4 && flag5 && num18 == 1 && num19 == 1 && num20 == 1;
		int num23 = Enumerable.Count(source, (Transform item) => ((Object)item).name == "NATIVE_WarehouseRoof_9M" || ((Object)item).name.StartsWith("NATIVE_WarehouseRoofPanel_", StringComparison.Ordinal) || ((Object)item).name.StartsWith("NATIVE_WarehousePerimeterWall_", StringComparison.Ordinal));
		int num24 = CountRendererSlotsUsingNativeProfile(root.transform, "Corrugated_Metal_Sheet_vb1lafx");
		int num25 = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "OPEN_TOP_KILLHOUSE_INSIDE_HIGH_WAREHOUSE", StringComparison.Ordinal));
		int num26 = Enumerable.Count(source, (Transform item) => string.Equals(((Object)item).name, "HALLWAY_SIDE_DOOR", StringComparison.Ordinal));
		int num27 = Enumerable.Count(source2, (MeshFilter filter) => (Object)(object)filter.sharedMesh != (Object)null && primitiveNames.Contains(((Object)filter.sharedMesh).name));
		Transform val11 = array4.FirstOrDefault((Transform item) => ((Object)item).name == "NATIVE_WarehouseRoof");
		Renderer val12 = (((Object)(object)val11 != (Object)null) ? ((Component)val11).GetComponentInChildren<Renderer>(true) : null);
		float num28;
		if (!((Object)(object)val12 == (Object)null))
		{
			Transform transform = root.transform;
			val6 = val12.bounds;
			num28 = transform.InverseTransformPoint(((Bounds)(ref val6)).max).y;
		}
		else
		{
			num28 = float.NaN;
		}
		float num29 = num28;
		BoxCollider val13 = ((array.Length == 1) ? ((Component)array[0]).GetComponent<BoxCollider>() : null);
		int num30 = (num9 - 1) * 2;
		int wallBackedCount;
		int provenanceMarkerCount;
		int forbiddenCount;
		int invalidPlacementCount;
		int missingFamilyCount;
		int overlappingCount;
		int blockedPortalCount;
		int centerRoomCount;
		int centerTableCount;
		int centerSofaCount;
		int invalidCenterCount;
		int centerTacticalConflictCount;
		string firstFailure;
		bool flag7 = ValidateRuntimeFurniturePlacement(root, out wallBackedCount, out provenanceMarkerCount, out forbiddenCount, out invalidPlacementCount, out missingFamilyCount, out overlappingCount, out blockedPortalCount, out centerRoomCount, out centerTableCount, out centerSofaCount, out invalidCenterCount, out centerTacticalConflictCount, out firstFailure);
		bool flag8 = num == 1 && num2 == 1 && num3 == 1 && num4 == 1 && num5 == 4 && num6 == 6 && num7 == 6 && num9 >= 19 && num9 <= 21 && num8 == num30 && array.Length == 1 && (Object)(object)val13 != (Object)null && ((Collider)val13).isTrigger && num10 == 0 && num11 >= 1 && num11 <= 32 && num12 == 0 && num13 == 1 && array4.Length == 4 && num17 == 4 && array5.Length == 1 && flag6 && (Object)(object)val12 != (Object)null && Mathf.Abs(num29 - 11.35f) <= 0.15f && num14 == 1 && num15 == 0 && num16 == 4 && num23 == 0 && num24 == 0 && num25 == 1 && num26 >= 1 && num27 == 0 && flag7;
		if (!flag8)
		{
			log.LogError((object)("Vektor Kill House scene contract failed: mapMarkers=" + num + ", pveSpawnSetMarkers=" + num2 + ", pvpSpawnSetMarkers=" + num3 + ", safeRooms=" + num4 + ", pvePlayers=" + num5 + ", pvpTeam1Players=" + num6 + ", pvpTeam2Players=" + num7 + ", enemies=" + num8 + ", exfils=" + array.Length + ", expectedEnemies=" + num30 + ", exfilTrigger=" + ((Object)(object)val13 != (Object)null && ((Collider)val13).isTrigger) + ", roomFloors=" + num9 + ", roomCeilings=" + num10 + ", connectorFloors=" + num11 + ", connectorCeilings=" + num12 + ", warehouseShellGroups=" + num13 + ", warehouseParts=" + array4.Length + ", warehouseRoofElevation=" + num29.ToString("F2", CultureInfo.InvariantCulture) + ", warehouseFinishMarkers=" + num14 + ", invalidWarehousePartFinish=" + num15 + ", warehouseSteelSlots=" + num16 + ", warehouseMeshColliders=" + num17 + ", warehouseGrounds=" + array5.Length + ", warehouseGroundValid=" + flag6 + ", warehouseGroundMeshValid=" + flag + ", warehouseGroundMeshFailure=" + failure + ", warehouseGroundBoundsValid=" + flag3 + ", warehouseGroundElevation=" + num21.ToString("F3", CultureInfo.InvariantCulture) + ", warehouseGroundElevationValid=" + flag4 + ", warehouseGroundColliderValid=" + flag5 + ", warehouseGroundMarkers=" + num18 + "/" + num19 + "/" + num20 + ", obsoleteWarehouseModules=" + num23 + ", obsoleteCorrugatedSlots=" + num24 + ", openTopMarkers=" + num25 + ", hallwaySideDoors=" + num26 + ", primitiveMeshes=" + num27 + ", wallBackedFurniture=" + wallBackedCount + ", furnitureProvenanceMarkers=" + provenanceMarkerCount + ", forbiddenFurniture=" + forbiddenCount + ", invalidFurniturePlacement=" + invalidPlacementCount + ", missingWallFurnitureFamilies=" + missingFamilyCount + ", centerRoomFurniture=" + centerRoomCount + ", centerRoomTables=" + centerTableCount + ", centerRoomSofas=" + centerSofaCount + ", invalidCenterRoomFurniture=" + invalidCenterCount + ", centerRoomTacticalConflicts=" + centerTacticalConflictCount + ", overlappingFurniture=" + overlappingCount + ", blockedFurniturePortals=" + blockedPortalCount + ", furniturePlacementFailure=" + firstFailure + "."));
		}
		return flag8;
	}

	private static bool ValidateRuntimeFurniturePlacement(GameObject root, out int wallBackedCount, out int provenanceMarkerCount, out int forbiddenCount, out int invalidPlacementCount, out int missingFamilyCount, out int overlappingCount, out int blockedPortalCount, out int centerRoomCount, out int centerTableCount, out int centerSofaCount, out int invalidCenterCount, out int centerTacticalConflictCount, out string firstFailure)
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
		if ((Object)(object)root == (Object)null)
		{
			firstFailure = "root-null";
			return false;
		}
		Transform[] array = Il2CppArrayBase<Transform>.op_Implicit(root.GetComponentsInChildren<Transform>(true));
		MeshFilter[] source = Il2CppArrayBase<MeshFilter>.op_Implicit(root.GetComponentsInChildren<MeshFilter>(true));
		provenanceMarkerCount = Enumerable.Count(array, (Transform item) => ((Object)item).name.StartsWith("WALL_BACKED_PROP_PROVENANCE_", StringComparison.Ordinal));
		forbiddenCount = Enumerable.Count(source, (MeshFilter filter) => (Object)(object)filter != (Object)null && (Object)(object)filter.sharedMesh != (Object)null && ForbiddenStandaloneFurnitureMeshes.Contains(((Object)filter.sharedMesh).name));
		Transform[] array2 = array.Where((Transform item) => RuntimeHasDirectChildWithPrefix(item, "WALL_BACKED_PROP_OUTWARD_")).ToArray();
		wallBackedCount = array2.Length;
		HashSet<string> families = new HashSet<string>(StringComparer.Ordinal);
		Transform[] array3 = array2;
		foreach (Transform val in array3)
		{
			string family;
			string text = RuntimeWallFurnitureFailure(root, val, out family);
			if (!string.IsNullOrEmpty(family))
			{
				families.Add(family);
			}
			if (!string.IsNullOrEmpty(text))
			{
				invalidPlacementCount++;
				if (string.IsNullOrEmpty(firstFailure))
				{
					firstFailure = HierarchyPath(val) + ":" + text;
				}
			}
		}
		string[] array4 = RuntimeWallFurnitureContracts.Keys.Where((string key) => !families.Contains(key)).ToArray();
		missingFamilyCount = array4.Length;
		if (array4.Length != 0 && string.IsNullOrEmpty(firstFailure))
		{
			firstFailure = "missing-families=" + string.Join("|", array4);
		}
		if (forbiddenCount > 0 && string.IsNullOrEmpty(firstFailure))
		{
			firstFailure = "forbidden-standalone-meshes=" + forbiddenCount;
		}
		Transform[] array5 = array.Where((Transform item) => RuntimeDirectChildrenWithPrefix(item, "CENTER_ROOM_PROP_ROLE_").Length == 1).ToArray();
		centerRoomCount = array5.Length;
		array3 = array5;
		foreach (Transform val2 in array3)
		{
			string role;
			string text2 = RuntimeCenterFurnitureFailure(root, val2, out role);
			if (string.Equals(role, "TABLE", StringComparison.Ordinal))
			{
				centerTableCount++;
			}
			else if (string.Equals(role, "SOFA", StringComparison.Ordinal))
			{
				centerSofaCount++;
			}
			if (!string.IsNullOrEmpty(text2))
			{
				invalidCenterCount++;
				if (string.IsNullOrEmpty(firstFailure))
				{
					firstFailure = HierarchyPath(val2) + ":" + text2;
				}
			}
		}
		string[] array6 = RuntimeCenterFurnitureTacticalBlockerDetails(array5, array);
		centerTacticalConflictCount = array6.Length;
		if (array6.Length != 0 && string.IsNullOrEmpty(firstFailure))
		{
			firstFailure = array6[0];
		}
		Transform[] furniture = (from filter in source
			where (Object)(object)filter != (Object)null && (Object)(object)filter.sharedMesh != (Object)null && (RuntimeWallFurnitureContracts.ContainsKey(((Object)filter.sharedMesh).name) || string.Equals(((Object)filter.sharedMesh).name, "Kitchen_table_large", StringComparison.Ordinal) || string.Equals(((Object)filter.sharedMesh).name, "Couch_2seat", StringComparison.Ordinal))
			select ((Component)filter).transform).Distinct().ToArray();
		string[] array7 = RuntimeFurnitureOverlapDetails(furniture);
		overlappingCount = array7.Length;
		if (array7.Length != 0 && string.IsNullOrEmpty(firstFailure))
		{
			firstFailure = array7[0];
		}
		string[] array8 = RuntimeFurniturePortalBlockerDetails(furniture, array);
		blockedPortalCount = array8.Length;
		if (array8.Length != 0 && string.IsNullOrEmpty(firstFailure))
		{
			firstFailure = array8[0];
		}
		bool flag = wallBackedCount >= 12 && provenanceMarkerCount == wallBackedCount && forbiddenCount == 0 && invalidPlacementCount == 0 && missingFamilyCount == 0 && overlappingCount == 0 && blockedPortalCount == 0 && centerRoomCount >= 2 && centerTableCount >= 1 && centerSofaCount >= 1 && invalidCenterCount == 0 && centerTacticalConflictCount == 0;
		if (!flag && string.IsNullOrEmpty(firstFailure))
		{
			firstFailure = "counts=wall:" + wallBackedCount + "/provenance:" + provenanceMarkerCount + "/forbidden:" + forbiddenCount + "/invalid:" + invalidPlacementCount + "/missing:" + missingFamilyCount + "/overlap:" + overlappingCount + "/portal:" + blockedPortalCount + "/center:" + centerRoomCount + "/table:" + centerTableCount + "/sofa:" + centerSofaCount + "/centerInvalid:" + invalidCenterCount + "/centerTactical:" + centerTacticalConflictCount;
		}
		return flag;
	}

	private static string RuntimeWallFurnitureFailure(GameObject root, Transform prop, out string family)
	{
		//IL_0174: Unknown result type (might be due to invalid IL or missing references)
		//IL_0179: Unknown result type (might be due to invalid IL or missing references)
		//IL_017b: Unknown result type (might be due to invalid IL or missing references)
		//IL_017d: Unknown result type (might be due to invalid IL or missing references)
		//IL_016d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0197: Unknown result type (might be due to invalid IL or missing references)
		//IL_019c: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01be: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01da: Unknown result type (might be due to invalid IL or missing references)
		//IL_01df: Unknown result type (might be due to invalid IL or missing references)
		//IL_0166: Unknown result type (might be due to invalid IL or missing references)
		//IL_015f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0158: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_030b: Unknown result type (might be due to invalid IL or missing references)
		//IL_032e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0320: Unknown result type (might be due to invalid IL or missing references)
		//IL_033c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0343: Unknown result type (might be due to invalid IL or missing references)
		//IL_0348: Unknown result type (might be due to invalid IL or missing references)
		//IL_034d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0354: Unknown result type (might be due to invalid IL or missing references)
		//IL_0362: Unknown result type (might be due to invalid IL or missing references)
		//IL_036e: Unknown result type (might be due to invalid IL or missing references)
		//IL_037c: Unknown result type (might be due to invalid IL or missing references)
		family = string.Empty;
		if ((Object)(object)prop == (Object)null)
		{
			return "prop-null";
		}
		MeshFilter component = ((Component)prop).GetComponent<MeshFilter>();
		Mesh val = (((Object)(object)component == (Object)null) ? null : component.sharedMesh);
		if ((Object)(object)val == (Object)null)
		{
			return "root-mesh-missing";
		}
		family = ((Object)val).name;
		if (ForbiddenStandaloneFurnitureMeshes.Contains(family))
		{
			return "forbidden-family=" + family;
		}
		if (!RuntimeWallFurnitureContracts.TryGetValue(family, out var value))
		{
			return "unproven-wall-family=" + family;
		}
		Transform[] array = RuntimeDirectChildrenWithPrefix(prop, "WALL_BACKED_PROP_OUTWARD_");
		if (array.Length != 1)
		{
			return "outward-marker-count=" + array.Length;
		}
		Transform[] array2 = RuntimeDirectChildrenWithPrefix(prop, "WALL_BACKED_PROP_PROVENANCE_");
		if (array2.Length != 1 || !string.Equals(((Object)array2[0]).name, value.ProvenanceMarker, StringComparison.Ordinal))
		{
			return "provenance-marker=" + ((array2.Length == 1) ? ((Object)array2[0]).name : ("count-" + array2.Length));
		}
		string text = ((Object)array[0]).name.Substring("WALL_BACKED_PROP_OUTWARD_".Length);
		Vector3 val2 = (Vector3)(text switch
		{
			"S" => Vector3.back, 
			"N" => Vector3.forward, 
			"W" => Vector3.left, 
			"E" => Vector3.right, 
			_ => Vector3.zero, 
		});
		if (val2 == Vector3.zero)
		{
			return "outward-marker-direction=" + text;
		}
		Vector3 val3 = prop.forward;
		float num = Vector3.Dot(((Vector3)(ref val3)).normalized, -val2);
		val3 = -prop.forward;
		float num2 = Vector3.Dot(((Vector3)(ref val3)).normalized, val2);
		val3 = prop.up;
		float num3 = Vector3.Dot(((Vector3)(ref val3)).normalized, Vector3.up);
		if (num < 0.999f || num2 < 0.999f || num3 < 0.999f)
		{
			return "axis-alignment=" + num.ToString("F4", CultureInfo.InvariantCulture) + "/" + num2.ToString("F4", CultureInfo.InvariantCulture) + "/" + num3.ToString("F4", CultureInfo.InvariantCulture);
		}
		if (!RuntimeColliderContractValid(prop, value, out var failure))
		{
			return "collider=" + failure;
		}
		if (!RuntimeFurnitureHierarchyContractValid(prop, value.Children, allowPlacementMarkers: true, out var failure2))
		{
			return "hierarchy=" + failure2;
		}
		if (((Component)prop).GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
		{
			return "hierarchy=retained-node-monobehaviour-present";
		}
		if (!TryRuntimeRoomFloorBounds(root, prop, out var bounds))
		{
			return "room-owner-or-floor-bounds";
		}
		if (!TryRuntimeRendererBounds(((Component)prop).gameObject, out var bounds2) || !RuntimeBoundsInside(bounds2, bounds, 0.02f))
		{
			return "renderer-outside-room";
		}
		if (!TryRuntimePhysicalBounds(((Component)prop).gameObject, out var bounds3) || !RuntimeBoundsInside(bounds3, bounds, 0.02f))
		{
			return "collider-outside-room";
		}
		float num4 = ((Mathf.Abs(val2.x) > 0.5f) ? ((Bounds)(ref bounds)).extents.x : ((Bounds)(ref bounds)).extents.z);
		float num5 = Vector3.Dot(((Bounds)(ref bounds2)).center - ((Bounds)(ref bounds)).center, val2) + Mathf.Abs(val2.x) * ((Bounds)(ref bounds2)).extents.x + Mathf.Abs(val2.z) * ((Bounds)(ref bounds2)).extents.z;
		if (Mathf.Abs(num4 - 0.12f - num5) > 0.04f)
		{
			return "wall-standoff=" + (num4 - num5).ToString("F3", CultureInfo.InvariantCulture);
		}
		return string.Empty;
	}

	private static string RuntimeCenterFurnitureFailure(GameObject root, Transform prop, out string role)
	{
		//IL_01a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_0160: Unknown result type (might be due to invalid IL or missing references)
		//IL_0165: Unknown result type (might be due to invalid IL or missing references)
		//IL_0417: Unknown result type (might be due to invalid IL or missing references)
		//IL_041c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0429: Unknown result type (might be due to invalid IL or missing references)
		//IL_042e: Unknown result type (might be due to invalid IL or missing references)
		//IL_06a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_06ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_06b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_06b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_06ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_06f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0713: Unknown result type (might be due to invalid IL or missing references)
		//IL_0715: Unknown result type (might be due to invalid IL or missing references)
		role = string.Empty;
		if ((Object)(object)root == (Object)null || (Object)(object)prop == (Object)null)
		{
			return "center-prop-null";
		}
		Transform[] array = RuntimeDirectChildrenWithPrefix(prop, "CENTER_ROOM_PROP_ROLE_");
		if (array.Length != 1)
		{
			return "center-role-marker-count=" + array.Length;
		}
		role = ((Object)array[0]).name.Substring("CENTER_ROOM_PROP_ROLE_".Length);
		if (!RuntimeCenterFurnitureContracts.TryGetValue(role, out var value))
		{
			return "center-role-unproven=" + role;
		}
		MeshFilter[] array2 = Il2CppArrayBase<MeshFilter>.op_Implicit(((Component)prop).GetComponents<MeshFilter>());
		MeshRenderer[] array3 = Il2CppArrayBase<MeshRenderer>.op_Implicit(((Component)prop).GetComponents<MeshRenderer>());
		Mesh val = ((array2.Length == 1) ? array2[0].sharedMesh : null);
		if (array2.Length != 1 || array3.Length != 1 || (Object)(object)val == (Object)null || !((Renderer)array3[0]).enabled || !string.Equals(((Object)val).name, value.MeshName, StringComparison.Ordinal))
		{
			return "center-root-visual=" + array2.Length + "/" + array3.Length + "/" + (((Object)(object)val == (Object)null) ? "null" : ((Object)val).name) + "/expected=" + value.MeshName;
		}
		Vector3 val2;
		if (((Component)prop).gameObject.layer != value.RootLayer || !RuntimeVectorApproximately(prop.localScale, Vector3.one, 1E-05f))
		{
			string[] obj = new string[6]
			{
				"center-root-layer-scale=",
				((Component)prop).gameObject.layer.ToString(),
				"/",
				null,
				null,
				null
			};
			val2 = prop.localScale;
			obj[3] = ((Vector3)(ref val2)).ToString("F5");
			obj[4] = "/expected-layer=";
			obj[5] = value.RootLayer.ToString();
			return string.Concat(obj);
		}
		if (val.subMeshCount != 1 || val.GetIndexCount(0) == 0 || (value.VertexCount > 0 && val.vertexCount != value.VertexCount) || (value.IndexCount > 0 && val.GetIndexCount(0) != value.IndexCount) || !val.HasVertexAttribute((VertexAttribute)4) || !val.HasVertexAttribute((VertexAttribute)5) || !val.HasVertexAttribute((VertexAttribute)3))
		{
			return "center-mesh-closure=" + val.vertexCount + "/" + val.subMeshCount + "/" + ((val.subMeshCount != 0) ? val.GetIndexCount(0) : 0u);
		}
		if (!FurnitureMaterialSlotsByMesh.TryGetValue(value.MeshName, out var value2))
		{
			return "center-material-contract-missing=" + value.MeshName;
		}
		Material[] array4 = Il2CppArrayBase<Material>.op_Implicit((Il2CppArrayBase<Material>)(object)((Renderer)array3[0]).sharedMaterials);
		if (array4.Length != value2.Length || array4.Length != val.subMeshCount)
		{
			return "center-material-count=" + array4.Length + "/expected=" + value2.Length;
		}
		for (int i = 0; i < array4.Length; i++)
		{
			string text = (((Object)(object)array4[i] == (Object)null) ? string.Empty : NormalizeNativeMaterialName(((Object)array4[i]).name));
			if (!string.Equals(text, value2[i], StringComparison.Ordinal))
			{
				return "center-material-slot-" + i + "=" + text + "/expected=" + value2[i];
			}
		}
		if (!RuntimeBoxColliderContractValid(prop, value.BoxColliders, out var failure))
		{
			return "center-collider=" + failure;
		}
		if (string.Equals(value.Role, "SOFA", StringComparison.Ordinal))
		{
			if (!RuntimeExactCouchRendererState(array3[0], prop, out var failure2))
			{
				return "center-sofa-renderer=" + failure2;
			}
			BoxCollider[] array5 = Il2CppArrayBase<BoxCollider>.op_Implicit(((Component)prop).GetComponents<BoxCollider>());
			int num = 0;
			while (num < array5.Length)
			{
				BoxCollider val3 = array5[num];
				if ((Object)(object)val3 != (Object)null && !((Collider)val3).providesContacts)
				{
					LayerMask val4 = ((Collider)val3).includeLayers;
					if (((LayerMask)(ref val4)).value == 0)
					{
						val4 = ((Collider)val3).excludeLayers;
						if (((LayerMask)(ref val4)).value == 0 && ((Collider)val3).layerOverridePriority == 0)
						{
							num++;
							continue;
						}
					}
				}
				return "center-sofa-box-auxiliary=" + num;
			}
		}
		if (((Component)prop).GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
		{
			return "center-retained-monobehaviour";
		}
		if (!TryRuntimeDressingRoomIndex(prop.parent, out var roomIndex) || roomIndex <= 0)
		{
			return "center-room-owner";
		}
		Transform[] array6 = RuntimeDirectChildrenWithPrefix(prop, "CENTER_ROOM_PROP_ROOM_");
		string text2 = "CENTER_ROOM_PROP_ROOM_" + roomIndex.ToString("00", CultureInfo.InvariantCulture);
		if (array6.Length != 1 || !string.Equals(((Object)array6[0]).name, text2, StringComparison.Ordinal))
		{
			return "center-room-marker=" + ((array6.Length == 1) ? ((Object)array6[0]).name : ("count-" + array6.Length)) + "/expected=" + text2;
		}
		Transform[] array7 = RuntimeDirectChildrenWithPrefix(prop, "CENTER_ROOM_PROP_PROVENANCE_");
		if (array7.Length != 1 || !string.Equals(((Object)array7[0]).name, value.ProvenanceMarker, StringComparison.Ordinal))
		{
			return "center-provenance=" + ((array7.Length == 1) ? ((Object)array7[0]).name : ("count-" + array7.Length));
		}
		Transform[] array8 = RuntimeDirectChildrenWithPrefix(prop, "CENTER_ROOM_PROP_FACING_");
		string text3 = RuntimeCenterFacingMarker(prop, value);
		if (array8.Length != 1 || string.IsNullOrEmpty(text3) || !string.Equals(((Object)array8[0]).name, text3, StringComparison.Ordinal))
		{
			return "center-facing=" + ((array8.Length == 1) ? ((Object)array8[0]).name : ("count-" + array8.Length)) + "/expected=" + text3;
		}
		Transform[] array9 = RuntimeDirectChildrenWithPrefix(prop, "CENTER_ROOM_PROP_DETERMINISTIC_CANDIDATE_");
		if (array9.Length != 1 || !int.TryParse(((Object)array9[0]).name.Substring("CENTER_ROOM_PROP_DETERMINISTIC_CANDIDATE_".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result <= 0)
		{
			return "center-candidate-marker";
		}
		Transform[] array10 = RuntimeDirectChildrenNamed(prop, "CENTER_ROOM_PROP_CLEARANCE_VALID");
		Transform[] array11 = RuntimeDirectChildrenNamed(prop, "CENTER_ROOM_PROP_CIRCULATION_VALID");
		if (array10.Length != 1 || array11.Length != 1)
		{
			return "center-clearance-circulation-markers";
		}
		Transform[] placementMarkers = (Transform[])(object)new Transform[7]
		{
			array[0],
			array6[0],
			array7[0],
			array8[0],
			array9[0],
			array10[0],
			array11[0]
		};
		if (!RuntimeCenterFurnitureMarkersValid(prop, value, placementMarkers, out var failure3))
		{
			return "center-markers=" + failure3;
		}
		val2 = prop.up;
		if (Vector3.Dot(((Vector3)(ref val2)).normalized, Vector3.up) < 0.999f)
		{
			return "center-up-axis";
		}
		if (!TryRuntimeRoomFloorBounds(root, prop, out var bounds))
		{
			return "center-room-floor-bounds";
		}
		if (!TryRuntimeRendererBounds(((Component)prop).gameObject, out var bounds2) || !RuntimeBoundsInsideInset(bounds2, bounds, 0.82f))
		{
			return "center-renderer-perimeter-clearance";
		}
		if (!TryRuntimePhysicalBounds(((Component)prop).gameObject, out var bounds3) || !RuntimeBoundsInsideInset(bounds3, bounds, 0.82f))
		{
			return "center-collider-perimeter-clearance";
		}
		string text4 = RuntimeCenterSiblingOverlapFailure(prop);
		if (!string.IsNullOrEmpty(text4))
		{
			return "center-sibling-overlap=" + text4;
		}
		return string.Empty;
	}

	private static bool RuntimeBoxColliderContractValid(Transform root, RuntimeBoxColliderProfile[] expected, out string failure)
	{
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
		failure = string.Empty;
		Collider[] array = (((Object)(object)root == (Object)null) ? Array.Empty<Collider>() : Il2CppArrayBase<Collider>.op_Implicit(((Component)root).GetComponents<Collider>()));
		BoxCollider[] array2 = (((Object)(object)root == (Object)null) ? Array.Empty<BoxCollider>() : Il2CppArrayBase<BoxCollider>.op_Implicit(((Component)root).GetComponents<BoxCollider>()));
		if (array2.Length != expected.Length || array.Length != array2.Length)
		{
			failure = "count=" + array2.Length + "/all=" + array.Length + "/expected=" + expected.Length;
			return false;
		}
		for (int i = 0; i < array2.Length; i++)
		{
			BoxCollider val = array2[i];
			RuntimeBoxColliderProfile runtimeBoxColliderProfile = expected[i];
			if (!((Object)(object)val != (Object)null) || !((Collider)val).enabled || ((Collider)val).isTrigger || !((Object)(object)((Collider)val).sharedMaterial == (Object)null) || !RuntimeVectorApproximately(val.center, runtimeBoxColliderProfile.Center, 1E-05f) || !RuntimeVectorApproximately(val.size, runtimeBoxColliderProfile.Size, 1E-05f))
			{
				failure = "box-" + i + "-state";
				return false;
			}
		}
		return true;
	}

	private static bool RuntimeCenterFurnitureMarkersValid(Transform prop, RuntimeCenterFurnitureContract contract, Transform[] placementMarkers, out string failure)
	{
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		failure = string.Empty;
		if (placementMarkers.Length != 7 || placementMarkers.Any((Transform marker) => !RuntimeMetadataMarkerValid(marker, Vector3.zero, 0)))
		{
			failure = "placement-marker-transform";
			return false;
		}
		HashSet<string> hashSet = new HashSet<string>(placementMarkers.Select((Transform marker) => ((Object)marker).name), StringComparer.Ordinal);
		if (string.Equals(contract.Role, "SOFA", StringComparison.Ordinal))
		{
			Transform[] array = RuntimeDirectChildrenNamed(prop, "NATIVE_FURNITURE_FRONT_LOCAL_POSITIVE_Z");
			Transform[] array2 = RuntimeDirectChildrenNamed(prop, "NATIVE_FURNITURE_PROVENANCE_level4_GO578_Couch_2seat_Mesh962_Mat174");
			Transform[] array3 = RuntimeDirectChildrenNamed(prop, "GameObject");
			if (array.Length != 1 || array2.Length != 1 || !RuntimeMetadataMarkerValid(array[0], Vector3.forward, 24) || !RuntimeMetadataMarkerValid(array2[0], Vector3.zero, 24) || array3.Length != 1 || !((Component)array3[0]).gameObject.activeSelf || !RuntimeMetadataMarkerValid(array3[0], new Vector3(0f, 1.322f, -0.349f), 24))
			{
				failure = "sofa-native-marker-or-probe-transform";
				return false;
			}
			hashSet.Add("NATIVE_FURNITURE_FRONT_LOCAL_POSITIVE_Z");
			hashSet.Add("NATIVE_FURNITURE_PROVENANCE_level4_GO578_Couch_2seat_Mesh962_Mat174");
			hashSet.Add("GameObject");
		}
		if (prop.childCount != hashSet.Count)
		{
			failure = "child-count=" + prop.childCount + "/expected=" + hashSet.Count;
			return false;
		}
		for (int num = 0; num < prop.childCount; num++)
		{
			Transform child = prop.GetChild(num);
			if (!hashSet.Contains(((Object)child).name))
			{
				failure = "unexpected-child=" + ((Object)child).name;
				return false;
			}
		}
		return true;
	}

	private static bool RuntimeExactCouchRendererState(MeshRenderer renderer, Transform root, out string failure)
	{
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Invalid comparison between Unknown and I4
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Expected I4, but got Unknown
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Invalid comparison between Unknown and I4
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_011b: Invalid comparison between Unknown and I4
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Expected I4, but got Unknown
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_0146: Invalid comparison between Unknown and I4
		//IL_0124: Unknown result type (might be due to invalid IL or missing references)
		//IL_012b: Expected I4, but got Unknown
		//IL_014f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0156: Expected I4, but got Unknown
		failure = string.Empty;
		Transform[] array = RuntimeDirectChildrenNamed(root, "GameObject");
		if ((Object)(object)renderer == (Object)null || array.Length != 1 || (Object)(object)((Renderer)renderer).probeAnchor != (Object)(object)array[0])
		{
			failure = "probe-anchor=" + array.Length + "/" + (((Object)(object)renderer == (Object)null || (Object)(object)((Renderer)renderer).probeAnchor == (Object)null) ? "null" : ((Object)((Renderer)renderer).probeAnchor).name);
			return false;
		}
		if (!((Renderer)renderer).enabled)
		{
			failure = "enabled=false/expected=true";
			return false;
		}
		if ((int)((Renderer)renderer).shadowCastingMode != 1)
		{
			failure = "shadow-casting=" + (int)((Renderer)renderer).shadowCastingMode + "/expected=1";
			return false;
		}
		if (!((Renderer)renderer).receiveShadows)
		{
			failure = "receive-shadows=false/expected=true";
			return false;
		}
		if (!((Renderer)renderer).allowOcclusionWhenDynamic)
		{
			failure = "dynamic-occlusion=false/expected=true";
			return false;
		}
		if (((Renderer)renderer).staticShadowCaster)
		{
			failure = "static-shadow-caster=true/expected=false";
			return false;
		}
		if ((int)((Renderer)renderer).motionVectorGenerationMode != 1)
		{
			failure = "motion-vectors=" + (int)((Renderer)renderer).motionVectorGenerationMode + "/expected=1";
			return false;
		}
		if ((int)((Renderer)renderer).lightProbeUsage != 1)
		{
			failure = "light-probe-usage=" + (int)((Renderer)renderer).lightProbeUsage + "/expected=1";
			return false;
		}
		if ((int)((Renderer)renderer).reflectionProbeUsage != 1)
		{
			failure = "reflection-probe-usage=" + (int)((Renderer)renderer).reflectionProbeUsage + "/expected=1";
			return false;
		}
		if (((Renderer)renderer).renderingLayerMask != 257)
		{
			failure = "rendering-layer-mask=" + ((Renderer)renderer).renderingLayerMask + "/expected=257";
			return false;
		}
		if (((Renderer)renderer).rendererPriority != 0)
		{
			failure = "renderer-priority=" + ((Renderer)renderer).rendererPriority + "/expected=0";
			return false;
		}
		if (((Renderer)renderer).sortingLayerID != 0)
		{
			failure = "sorting-layer-id=" + ((Renderer)renderer).sortingLayerID + "/expected=0";
			return false;
		}
		if (((Renderer)renderer).sortingOrder != 0)
		{
			failure = "sorting-order=" + ((Renderer)renderer).sortingOrder + "/expected=0";
			return false;
		}
		if ((Object)(object)renderer.additionalVertexStreams != (Object)null)
		{
			failure = "additional-vertex-streams=" + ((Object)renderer.additionalVertexStreams).name + "/expected=null";
			return false;
		}
		if ((Object)(object)((Renderer)renderer).lightProbeProxyVolumeOverride != (Object)null)
		{
			failure = "light-probe-proxy-volume=" + ((Object)((Renderer)renderer).lightProbeProxyVolumeOverride).name + "/expected=null";
			return false;
		}
		if ((Object)(object)((Renderer)renderer).staticBatchRootTransform != (Object)null)
		{
			failure = "static-batch-root=" + ((Object)((Renderer)renderer).staticBatchRootTransform).name + "/expected=null";
			return false;
		}
		int lightmapIndex = ((Renderer)renderer).lightmapIndex;
		int realtimeLightmapIndex = ((Renderer)renderer).realtimeLightmapIndex;
		if ((lightmapIndex >= 0 && lightmapIndex != 65535) || (realtimeLightmapIndex >= 0 && realtimeLightmapIndex != 65535))
		{
			failure = "scene-baked-lightmap=" + lightmapIndex + "/" + realtimeLightmapIndex;
			return false;
		}
		Component[] array2 = Il2CppArrayBase<Component>.op_Implicit(((Component)root).GetComponents<Component>());
		if (array2.Length != 7 || !(array2[0] is Transform) || !(array2[1] is MeshFilter) || !(array2[2] is MeshRenderer) || array2.Skip(3).Any((Component component) => !(component is BoxCollider)))
		{
			failure = "root-component-order-count=" + array2.Length;
			return false;
		}
		return true;
	}

	private static bool RuntimeMetadataMarkerValid(Transform marker, Vector3 localPosition, int layer)
	{
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)marker != (Object)null && ((Component)marker).gameObject.activeSelf && marker.childCount == 0 && ((Component)marker).gameObject.layer == layer && ((Component)marker).GetComponents<Component>().Length == 1 && RuntimeVectorApproximately(marker.localPosition, localPosition, 1E-05f) && RuntimeQuaternionApproximately(marker.localRotation, Quaternion.identity, 1E-05f))
		{
			return RuntimeVectorApproximately(marker.localScale, Vector3.one, 1E-05f);
		}
		return false;
	}

	private static Transform[] RuntimeDirectChildrenNamed(Transform parent, string name)
	{
		if ((Object)(object)parent == (Object)null)
		{
			return Array.Empty<Transform>();
		}
		return (from child in Enumerable.Range(0, parent.childCount).Select((Func<int, Transform>)parent.GetChild)
			where string.Equals(((Object)child).name, name, StringComparison.Ordinal)
			select child).ToArray();
	}

	private static string RuntimeCenterFacingMarker(Transform prop, RuntimeCenterFurnitureContract contract)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_0111: Unknown result type (might be due to invalid IL or missing references)
		//IL_0116: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_0118: Unknown result type (might be due to invalid IL or missing references)
		//IL_010a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0103: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		Vector3 val = prop.TransformDirection(contract.LocalFacingAxis);
		val.y = 0f;
		if (((Vector3)(ref val)).sqrMagnitude < 0.999f)
		{
			return string.Empty;
		}
		((Vector3)(ref val)).Normalize();
		if (contract.BidirectionalFacing)
		{
			return "CENTER_ROOM_PROP_FACING_LONG_AXIS_" + ((Mathf.Abs(Vector3.Dot(val, Vector3.right)) >= 0.999f) ? "X" : ((Mathf.Abs(Vector3.Dot(val, Vector3.forward)) >= 0.999f) ? "Z" : string.Empty));
		}
		string text = ((!(Mathf.Abs(val.x) >= Mathf.Abs(val.z))) ? ((val.z >= 0f) ? "N" : "S") : ((val.x >= 0f) ? "E" : "W"));
		Vector3 val2 = (Vector3)(text switch
		{
			"N" => Vector3.forward, 
			"W" => Vector3.left, 
			"E" => Vector3.right, 
			_ => Vector3.back, 
		});
		if (!(Vector3.Dot(val, val2) >= 0.999f))
		{
			return string.Empty;
		}
		return "CENTER_ROOM_PROP_FACING_FRONT_" + text;
	}

	private static bool TryRuntimeDressingRoomIndex(Transform dressing, out int roomIndex)
	{
		roomIndex = -1;
		if ((Object)(object)dressing != (Object)null && ((Object)dressing).name.StartsWith("DRESSING_", StringComparison.Ordinal) && ((Object)dressing).name.Length >= 11)
		{
			return int.TryParse(((Object)dressing).name.Substring(9, 2), NumberStyles.None, CultureInfo.InvariantCulture, out roomIndex);
		}
		return false;
	}

	private static bool RuntimeBoundsInsideInset(Bounds value, Bounds room, float inset)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		if (((Bounds)(ref room)).extents.x > inset && ((Bounds)(ref room)).extents.z > inset && ((Bounds)(ref value)).min.x >= ((Bounds)(ref room)).min.x + inset && ((Bounds)(ref value)).max.x <= ((Bounds)(ref room)).max.x - inset && ((Bounds)(ref value)).min.z >= ((Bounds)(ref room)).min.z + inset)
		{
			return ((Bounds)(ref value)).max.z <= ((Bounds)(ref room)).max.z - inset;
		}
		return false;
	}

	private static string RuntimeCenterSiblingOverlapFailure(Transform prop)
	{
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_010c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0118: Unknown result type (might be due to invalid IL or missing references)
		Transform val = (((Object)(object)prop == (Object)null) ? null : prop.parent);
		if ((Object)(object)val == (Object)null || !TryRuntimePhysicalBounds(((Component)prop).gameObject, out var bounds))
		{
			return "owner-or-physical-bounds";
		}
		for (int i = 0; i < val.childCount; i++)
		{
			Transform child = val.GetChild(i);
			if (!((Object)(object)child == (Object)(object)prop) && ((Object)child).name.IndexOf("Carpet", StringComparison.OrdinalIgnoreCase) < 0 && TryRuntimePhysicalBounds(((Component)child).gameObject, out var bounds2))
			{
				float num = Mathf.Min(((Bounds)(ref bounds)).max.x, ((Bounds)(ref bounds2)).max.x) - Mathf.Max(((Bounds)(ref bounds)).min.x, ((Bounds)(ref bounds2)).min.x);
				float num2 = Mathf.Min(((Bounds)(ref bounds)).max.y, ((Bounds)(ref bounds2)).max.y) - Mathf.Max(((Bounds)(ref bounds)).min.y, ((Bounds)(ref bounds2)).min.y);
				float num3 = Mathf.Min(((Bounds)(ref bounds)).max.z, ((Bounds)(ref bounds2)).max.z) - Mathf.Max(((Bounds)(ref bounds)).min.z, ((Bounds)(ref bounds2)).min.z);
				if (num > 0.02f && num2 > 0.02f && num3 > 0.02f)
				{
					return ((Object)child).name;
				}
			}
		}
		return string.Empty;
	}

	private static string[] RuntimeCenterFurnitureTacticalBlockerDetails(Transform[] centerFurniture, Transform[] transforms)
	{
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		if (centerFurniture.Length == 0)
		{
			return Array.Empty<string>();
		}
		Transform[] array = transforms.Where((Transform item) => ((Object)item).name.StartsWith("PVE_EnemySpawn_", StringComparison.Ordinal)).ToArray();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		Physics.SyncTransforms();
		Transform[] array2 = array;
		foreach (Transform val in array2)
		{
			Collider[] source = Il2CppArrayBase<Collider>.op_Implicit((Il2CppArrayBase<Collider>)(object)Physics.OverlapCapsule(val.position + Vector3.up * 0.42f, val.position + Vector3.up * 1.58f, 0.3f, -1, (QueryTriggerInteraction)1));
			foreach (Transform prop in centerFurniture)
			{
				if (source.Any((Collider hit) => (Object)(object)hit != (Object)null && RuntimeIsDescendantOf(((Component)hit).transform, prop)))
				{
					hashSet.Add("center-tactical-blocked=" + HierarchyPath(val) + "<-" + HierarchyPath(prop));
				}
			}
		}
		return hashSet.ToArray();
	}

	private static bool RuntimeColliderContractValid(Transform prop, RuntimeWallFurnitureContract contract, out string failure)
	{
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_026f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f7: Invalid comparison between Unknown and I4
		failure = string.Empty;
		Collider[] array = Il2CppArrayBase<Collider>.op_Implicit(((Component)prop).GetComponents<Collider>());
		if (contract.BoxColliders.Length != 0)
		{
			BoxCollider[] array2 = Il2CppArrayBase<BoxCollider>.op_Implicit(((Component)prop).GetComponents<BoxCollider>());
			if (array2.Length != contract.BoxColliders.Length || array.Length != array2.Length)
			{
				failure = "box-count=" + array2.Length + "/expected=" + contract.BoxColliders.Length + "/all=" + array.Length;
				return false;
			}
			for (int i = 0; i < array2.Length; i++)
			{
				RuntimeBoxColliderProfile runtimeBoxColliderProfile = contract.BoxColliders[i];
				BoxCollider val = array2[i];
				if ((Object)(object)val == (Object)null || !((Collider)val).enabled || ((Collider)val).isTrigger || (Object)(object)((Collider)val).sharedMaterial != (Object)null || !RuntimeVectorApproximately(val.center, runtimeBoxColliderProfile.Center, 1E-05f) || !RuntimeVectorApproximately(val.size, runtimeBoxColliderProfile.Size, 1E-05f))
				{
					failure = "box-" + i + "-state";
					return false;
				}
			}
			return true;
		}
		MeshCollider[] array3 = Il2CppArrayBase<MeshCollider>.op_Implicit(((Component)prop).GetComponents<MeshCollider>());
		if (array3.Length != 1 || array.Length != 1)
		{
			failure = "mesh-count=" + array3.Length + "/all=" + array.Length;
			return false;
		}
		MeshCollider val2 = array3[0];
		Mesh val3 = (((Object)(object)val2 == (Object)null) ? null : val2.sharedMesh);
		if ((Object)(object)val2 == (Object)null || !((Collider)val2).enabled || ((Collider)val2).isTrigger || (Object)(object)((Collider)val2).sharedMaterial != (Object)null || (Object)(object)val3 == (Object)null || !string.Equals(((Object)val3).name, contract.CollisionMeshName, StringComparison.Ordinal) || val2.convex != contract.CollisionConvex || (int)val2.cookingOptions != 30)
		{
			failure = "mesh-state=" + (((Object)(object)val3 == (Object)null) ? "null" : ((Object)val3).name) + "/expected=" + contract.CollisionMeshName + "/convex=" + ((Object)(object)val2 != (Object)null && val2.convex) + "/cooking=" + (((Object)(object)val2 == (Object)null) ? (-1) : ((int)val2.cookingOptions));
			return false;
		}
		return true;
	}

	private static bool RuntimeFurnitureHierarchyContractValid(Transform parent, RuntimeChildFurnitureContract[] expected, bool allowPlacementMarkers, out string failure)
	{
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_010c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0112: Unknown result type (might be due to invalid IL or missing references)
		failure = string.Empty;
		if ((Object)(object)parent == (Object)null)
		{
			failure = "parent-null";
			return false;
		}
		if (parent.childCount < expected.Length)
		{
			failure = "child-count=" + parent.childCount + "/expected-at-least-" + expected.Length;
			return false;
		}
		for (int i = 0; i < expected.Length; i++)
		{
			RuntimeChildFurnitureContract runtimeChildFurnitureContract = expected[i];
			Transform child = parent.GetChild(i);
			if (!string.Equals(((Object)child).name, runtimeChildFurnitureContract.Name, StringComparison.Ordinal) || ((Component)child).gameObject.activeSelf != runtimeChildFurnitureContract.Active)
			{
				failure = "child-" + i + "-identity=" + ((Object)child).name + "/" + ((Component)child).gameObject.activeSelf;
				return false;
			}
			if (!RuntimeVectorApproximately(child.localPosition, runtimeChildFurnitureContract.LocalPosition, 1E-05f) || !RuntimeVectorApproximately(child.localScale, runtimeChildFurnitureContract.LocalScale, 1E-05f) || !RuntimeQuaternionApproximately(child.localRotation, runtimeChildFurnitureContract.LocalRotation, 1E-05f))
			{
				failure = "child-" + i + "-transform";
				return false;
			}
			MeshFilter[] array = Il2CppArrayBase<MeshFilter>.op_Implicit(((Component)child).GetComponents<MeshFilter>());
			MeshRenderer[] array2 = Il2CppArrayBase<MeshRenderer>.op_Implicit(((Component)child).GetComponents<MeshRenderer>());
			Mesh val = ((array.Length == 1) ? array[0].sharedMesh : null);
			if (array.Length != 1 || array2.Length != 1 || (Object)(object)val == (Object)null || !((Renderer)array2[0]).enabled || !string.Equals(((Object)val).name, runtimeChildFurnitureContract.MeshName, StringComparison.Ordinal))
			{
				failure = "child-" + i + "-visual=" + array.Length + "/" + array2.Length + "/" + (((Object)(object)val == (Object)null) ? "null" : ((Object)val).name);
				return false;
			}
			if (val.vertexCount != runtimeChildFurnitureContract.VertexCount || val.subMeshCount != 1 || val.GetIndexCount(0) != runtimeChildFurnitureContract.IndexCount || !val.HasVertexAttribute((VertexAttribute)4) || !val.HasVertexAttribute((VertexAttribute)5) || val.HasVertexAttribute((VertexAttribute)3))
			{
				failure = "child-" + i + "-mesh-closure=" + val.vertexCount + "/" + val.subMeshCount + "/" + ((val.subMeshCount != 0) ? val.GetIndexCount(0) : 0u);
				return false;
			}
			Material[] array3 = Il2CppArrayBase<Material>.op_Implicit((Il2CppArrayBase<Material>)(object)((Renderer)array2[0]).sharedMaterials);
			if (array3.Length != runtimeChildFurnitureContract.MaterialSlots.Length || array3.Length != val.subMeshCount)
			{
				failure = "child-" + i + "-material-count=" + array3.Length + "/expected-" + runtimeChildFurnitureContract.MaterialSlots.Length;
				return false;
			}
			for (int j = 0; j < array3.Length; j++)
			{
				string text = (((Object)(object)array3[j] == (Object)null) ? string.Empty : NormalizeNativeMaterialName(((Object)array3[j]).name));
				if (!string.Equals(text, runtimeChildFurnitureContract.MaterialSlots[j], StringComparison.Ordinal))
				{
					failure = "child-" + i + "-slot-" + j + "=" + text + "/expected-" + runtimeChildFurnitureContract.MaterialSlots[j];
					return false;
				}
			}
			if (!RuntimeChildColliderContractValid(child, runtimeChildFurnitureContract, out var failure2))
			{
				failure = "child-" + i + "-collider=" + failure2;
				return false;
			}
			if (!RuntimeFurnitureHierarchyContractValid(child, runtimeChildFurnitureContract.Children, allowPlacementMarkers: false, out var failure3))
			{
				failure = "child-" + i + "/" + failure3;
				return false;
			}
		}
		for (int k = expected.Length; k < parent.childCount; k++)
		{
			Transform child2 = parent.GetChild(k);
			if (!allowPlacementMarkers || (!((Object)child2).name.StartsWith("WALL_BACKED_PROP_OUTWARD_", StringComparison.Ordinal) && !((Object)child2).name.StartsWith("WALL_BACKED_PROP_PROVENANCE_", StringComparison.Ordinal)) || child2.childCount != 0 || ((Component)child2).GetComponents<Component>().Length != 1)
			{
				failure = "unmanifested-child-" + k + "=" + ((Object)child2).name;
				return false;
			}
		}
		return true;
	}

	private static bool RuntimeChildColliderContractValid(Transform node, RuntimeChildFurnitureContract contract, out string failure)
	{
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_0295: Unknown result type (might be due to invalid IL or missing references)
		//IL_0201: Unknown result type (might be due to invalid IL or missing references)
		//IL_0208: Invalid comparison between Unknown and I4
		failure = string.Empty;
		Collider[] array = Il2CppArrayBase<Collider>.op_Implicit(((Component)node).GetComponents<Collider>());
		if (contract.BoxColliders.Length != 0)
		{
			BoxCollider[] array2 = Il2CppArrayBase<BoxCollider>.op_Implicit(((Component)node).GetComponents<BoxCollider>());
			if (array2.Length != contract.BoxColliders.Length || array.Length != array2.Length)
			{
				failure = "box-count=" + array2.Length + "/all=" + array.Length + "/expected=" + contract.BoxColliders.Length;
				return false;
			}
			for (int i = 0; i < array2.Length; i++)
			{
				BoxCollider val = array2[i];
				RuntimeBoxColliderProfile runtimeBoxColliderProfile = contract.BoxColliders[i];
				if ((Object)(object)val == (Object)null || !((Collider)val).enabled || ((Collider)val).isTrigger || (Object)(object)((Collider)val).sharedMaterial != (Object)null || !RuntimeVectorApproximately(val.center, runtimeBoxColliderProfile.Center, 1E-05f) || !RuntimeVectorApproximately(val.size, runtimeBoxColliderProfile.Size, 1E-05f))
				{
					failure = "box-" + i + "-state";
					return false;
				}
			}
			return true;
		}
		if (string.IsNullOrEmpty(contract.CollisionMeshName))
		{
			if (array.Length == 0)
			{
				return true;
			}
			failure = "unexpected-collider-count=" + array.Length;
			return false;
		}
		MeshCollider[] array3 = Il2CppArrayBase<MeshCollider>.op_Implicit(((Component)node).GetComponents<MeshCollider>());
		MeshCollider val2 = ((array3.Length == 1) ? array3[0] : null);
		Mesh val3 = (((Object)(object)val2 == (Object)null) ? null : val2.sharedMesh);
		if (array3.Length != 1 || array.Length != 1 || (Object)(object)val2 == (Object)null || ((Collider)val2).enabled != contract.CollisionEnabled || ((Collider)val2).isTrigger || (Object)(object)((Collider)val2).sharedMaterial != (Object)null || (Object)(object)val3 == (Object)null || !string.Equals(((Object)val3).name, contract.CollisionMeshName, StringComparison.Ordinal) || val2.convex != contract.CollisionConvex || (int)val2.cookingOptions != 30)
		{
			failure = "mesh-state=" + (((Object)(object)val3 == (Object)null) ? "null" : ((Object)val3).name) + "/enabled=" + ((Object)(object)val2 != (Object)null && ((Collider)val2).enabled) + "/convex=" + ((Object)(object)val2 != (Object)null && val2.convex) + "/cooking=" + (((Object)(object)val2 == (Object)null) ? (-1) : ((int)val2.cookingOptions));
			return false;
		}
		return true;
	}

	private static bool RuntimeQuaternionApproximately(Quaternion first, Quaternion second, float epsilon)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_006f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		bool num = Mathf.Abs(first.x - second.x) <= epsilon && Mathf.Abs(first.y - second.y) <= epsilon && Mathf.Abs(first.z - second.z) <= epsilon && Mathf.Abs(first.w - second.w) <= epsilon;
		bool flag = Mathf.Abs(first.x + second.x) <= epsilon && Mathf.Abs(first.y + second.y) <= epsilon && Mathf.Abs(first.z + second.z) <= epsilon && Mathf.Abs(first.w + second.w) <= epsilon;
		return num || flag;
	}

	private static bool TryRuntimeRoomFloorBounds(GameObject root, Transform prop, out Bounds bounds)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		bounds = default(Bounds);
		int result = -1;
		Transform val = (((Object)(object)prop == (Object)null) ? null : prop.parent);
		while ((Object)(object)val != (Object)null && (!((Object)val).name.StartsWith("DRESSING_", StringComparison.Ordinal) || ((Object)val).name.Length < 11 || !int.TryParse(((Object)val).name.Substring(9, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)))
		{
			if (string.Equals(((Object)val).name, "FIXED_SAFE_ROOM_KH_SAFE_ROOM_V1", StringComparison.Ordinal))
			{
				result = 0;
				break;
			}
			val = val.parent;
		}
		if (result < 0)
		{
			return false;
		}
		Transform val2 = root.transform.Find("10_ROOMS");
		if ((Object)(object)val2 == (Object)null)
		{
			return false;
		}
		string roomPrefix = "ROOM_" + result.ToString("00", CultureInfo.InvariantCulture) + "_";
		Transform val3 = Enumerable.Range(0, val2.childCount).Select((Func<int, Transform>)val2.GetChild).FirstOrDefault((Transform item) => ((Object)item).name.StartsWith(roomPrefix, StringComparison.Ordinal));
		if ((Object)(object)val3 == (Object)null)
		{
			return false;
		}
		Transform val4 = ((IEnumerable<Transform>)((Component)val3).GetComponentsInChildren<Transform>(true)).FirstOrDefault((Transform item) => string.Equals(((Object)item).name, "NATIVE_Floor", StringComparison.Ordinal));
		if ((Object)(object)val4 != (Object)null)
		{
			return TryRuntimeRendererBounds(((Component)val4).gameObject, out bounds);
		}
		return false;
	}

	private static bool TryRuntimeRendererBounds(GameObject root, out Bounds bounds)
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		Renderer[] array = (((Object)(object)root == (Object)null) ? Array.Empty<Renderer>() : Il2CppArrayBase<Renderer>.op_Implicit(root.GetComponentsInChildren<Renderer>(true)));
		if (array.Length == 0)
		{
			bounds = default(Bounds);
			return false;
		}
		bounds = array[0].bounds;
		foreach (Renderer item in array.Skip(1))
		{
			((Bounds)(ref bounds)).Encapsulate(item.bounds);
		}
		return true;
	}

	private static bool TryRuntimePhysicalBounds(GameObject root, out Bounds bounds)
	{
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		Collider[] array = (((Object)(object)root == (Object)null) ? Array.Empty<Collider>() : ((IEnumerable<Collider>)root.GetComponentsInChildren<Collider>(true)).Where((Collider collider) => (Object)(object)collider != (Object)null && collider.enabled && !collider.isTrigger).ToArray());
		if (array.Length == 0)
		{
			bounds = default(Bounds);
			return false;
		}
		bounds = array[0].bounds;
		foreach (Collider item in array.Skip(1))
		{
			((Bounds)(ref bounds)).Encapsulate(item.bounds);
		}
		return true;
	}

	private static bool RuntimeBoundsInside(Bounds value, Bounds room, float tolerance)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		if (((Bounds)(ref value)).min.x >= ((Bounds)(ref room)).min.x - tolerance && ((Bounds)(ref value)).max.x <= ((Bounds)(ref room)).max.x + tolerance && ((Bounds)(ref value)).min.z >= ((Bounds)(ref room)).min.z - tolerance)
		{
			return ((Bounds)(ref value)).max.z <= ((Bounds)(ref room)).max.z + tolerance;
		}
		return false;
	}

	private static string[] RuntimeFurnitureOverlapDetails(IEnumerable<Transform> furniture)
	{
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_0106: Unknown result type (might be due to invalid IL or missing references)
		//IL_011a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0126: Unknown result type (might be due to invalid IL or missing references)
		//IL_0137: Unknown result type (might be due to invalid IL or missing references)
		//IL_0143: Unknown result type (might be due to invalid IL or missing references)
		Transform[] array = furniture.Where((Transform item) => (Object)(object)item != (Object)null).ToArray();
		List<string> list = new List<string>();
		for (int num = 0; num < array.Length; num++)
		{
			if (!TryRuntimePhysicalBounds(((Component)array[num]).gameObject, out var bounds))
			{
				list.Add("physical-bounds-missing=" + HierarchyPath(array[num]));
				continue;
			}
			for (int num2 = num + 1; num2 < array.Length; num2++)
			{
				if (!TryRuntimePhysicalBounds(((Component)array[num2]).gameObject, out var bounds2))
				{
					list.Add("physical-bounds-missing=" + HierarchyPath(array[num2]));
					continue;
				}
				float num3 = Mathf.Min(((Bounds)(ref bounds)).max.x, ((Bounds)(ref bounds2)).max.x) - Mathf.Max(((Bounds)(ref bounds)).min.x, ((Bounds)(ref bounds2)).min.x);
				float num4 = Mathf.Min(((Bounds)(ref bounds)).max.y, ((Bounds)(ref bounds2)).max.y) - Mathf.Max(((Bounds)(ref bounds)).min.y, ((Bounds)(ref bounds2)).min.y);
				float num5 = Mathf.Min(((Bounds)(ref bounds)).max.z, ((Bounds)(ref bounds2)).max.z) - Mathf.Max(((Bounds)(ref bounds)).min.z, ((Bounds)(ref bounds2)).min.z);
				if (!(num3 <= 0.02f) && !(num4 <= 0.02f) && !(num5 <= 0.02f))
				{
					list.Add("furniture-overlap=" + HierarchyPath(array[num]) + "<->" + HierarchyPath(array[num2]));
				}
			}
		}
		return list.Distinct<string>(StringComparer.Ordinal).ToArray();
	}

	private static string[] RuntimeFurniturePortalBlockerDetails(Transform[] furniture, Transform[] transforms)
	{
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0108: Unknown result type (might be due to invalid IL or missing references)
		//IL_010d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0112: Unknown result type (might be due to invalid IL or missing references)
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_011e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0123: Unknown result type (might be due to invalid IL or missing references)
		Transform[] array = transforms.Where((Transform val3) => ((Object)val3).name.StartsWith("DOORV2_SOCKET_", StringComparison.Ordinal) || ((Object)val3).name.StartsWith("OPEN_CONNECTION_", StringComparison.Ordinal)).ToArray();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		Physics.SyncTransforms();
		Transform[] array2 = array;
		foreach (Transform val in array2)
		{
			Vector3 forward = val.forward;
			forward.y = 0f;
			if (((Vector3)(ref forward)).sqrMagnitude < 0.99f)
			{
				hashSet.Add("portal-axis-invalid=" + HierarchyPath(val));
				continue;
			}
			((Vector3)(ref forward)).Normalize();
			float[] array3 = new float[2] { -1f, 1f };
			foreach (float num3 in array3)
			{
				for (int num4 = 0; num4 < 5; num4++)
				{
					Vector3 val2 = val.position + forward * num3 * (0.35f + (float)num4 * 0.38f);
					Collider[] source = Il2CppArrayBase<Collider>.op_Implicit((Il2CppArrayBase<Collider>)(object)Physics.OverlapCapsule(val2 + Vector3.up * 0.35f, val2 + Vector3.up * 1.75f, 0.42f, -1, (QueryTriggerInteraction)1));
					Collider hit = source.FirstOrDefault((Collider candidate) => (Object)(object)candidate != (Object)null && furniture.Any((Transform owner) => RuntimeIsDescendantOf(((Component)candidate).transform, owner)));
					if (!((Object)(object)hit == (Object)null))
					{
						Transform item = furniture.First((Transform ancestor) => RuntimeIsDescendantOf(((Component)hit).transform, ancestor));
						hashSet.Add("portal-blocked=" + HierarchyPath(val) + "<-" + HierarchyPath(item));
					}
				}
			}
		}
		return hashSet.ToArray();
	}

	private static bool RuntimeHasDirectChildWithPrefix(Transform parent, string prefix)
	{
		return RuntimeDirectChildrenWithPrefix(parent, prefix).Length != 0;
	}

	private static Transform[] RuntimeDirectChildrenWithPrefix(Transform parent, string prefix)
	{
		if ((Object)(object)parent == (Object)null)
		{
			return Array.Empty<Transform>();
		}
		return (from child in Enumerable.Range(0, parent.childCount).Select((Func<int, Transform>)parent.GetChild)
			where ((Object)child).name.StartsWith(prefix, StringComparison.Ordinal)
			select child).ToArray();
	}

	private static bool RuntimeIsDescendantOf(Transform value, Transform ancestor)
	{
		Transform val = value;
		while ((Object)(object)val != (Object)null)
		{
			if ((Object)(object)val == (Object)(object)ancestor)
			{
				return true;
			}
			val = val.parent;
		}
		return false;
	}

	private static bool RuntimeVectorApproximately(Vector3 first, Vector3 second, float epsilon)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		if (Mathf.Abs(first.x - second.x) <= epsilon && Mathf.Abs(first.y - second.y) <= epsilon)
		{
			return Mathf.Abs(first.z - second.z) <= epsilon;
		}
		return false;
	}

	private static bool WarehouseGroundMeshContractValid(Mesh mesh, out string failure)
	{
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0143: Unknown result type (might be due to invalid IL or missing references)
		//IL_0148: Unknown result type (might be due to invalid IL or missing references)
		//IL_014b: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_0169: Unknown result type (might be due to invalid IL or missing references)
		//IL_0187: Unknown result type (might be due to invalid IL or missing references)
		//IL_0298: Unknown result type (might be due to invalid IL or missing references)
		//IL_029d: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a6: Unknown result type (might be due to invalid IL or missing references)
		failure = string.Empty;
		if ((Object)(object)mesh == (Object)null)
		{
			failure = "mesh-null";
			return false;
		}
		string[] array = new string[6] { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };
		if (!string.Equals(((Object)mesh).name, "Floor", StringComparison.Ordinal) || array.Contains(((Object)mesh).name))
		{
			failure = "mesh-identity:" + ((Object)mesh).name;
			return false;
		}
		if (mesh.vertexCount != 4 || mesh.subMeshCount != 1 || mesh.GetIndexCount(0) != 6 || (int)mesh.GetTopology(0) != 0)
		{
			failure = "mesh-topology:" + mesh.vertexCount + "/" + mesh.subMeshCount + "/" + ((mesh.subMeshCount != 0) ? mesh.GetIndexCount(0) : 0u);
			return false;
		}
		if (!mesh.HasVertexAttribute((VertexAttribute)1) || !mesh.HasVertexAttribute((VertexAttribute)2) || !mesh.HasVertexAttribute((VertexAttribute)4) || !mesh.HasVertexAttribute((VertexAttribute)5))
		{
			failure = "vertex-channel-closure";
			return false;
		}
		Bounds bounds = mesh.bounds;
		Vector3 val;
		if (Mathf.Abs(((Bounds)(ref bounds)).size.x - 5.425254f) > 0.001f || Mathf.Abs(((Bounds)(ref bounds)).size.z - 4.129904f) > 0.001f || ((Bounds)(ref bounds)).size.y > 0.001f)
		{
			val = ((Bounds)(ref bounds)).size;
			failure = "source-bounds:" + ((Vector3)(ref val)).ToString("F6");
			return false;
		}
		if (!mesh.isReadable)
		{
			failure = "mesh-not-readable";
			return false;
		}
		try
		{
			Il2CppStructArray<Vector3> normals = mesh.normals;
			Il2CppStructArray<Vector4> tangents = mesh.tangents;
			Il2CppStructArray<Vector2> uv = mesh.uv;
			Il2CppStructArray<Vector2> uv2 = mesh.uv2;
			if (((Il2CppArrayBase<Vector3>)(object)normals).Length != 4 || ((Il2CppArrayBase<Vector4>)(object)tangents).Length != 4 || ((Il2CppArrayBase<Vector2>)(object)uv).Length != 4 || ((Il2CppArrayBase<Vector2>)(object)uv2).Length != 4)
			{
				failure = "vertex-channel-lengths:" + ((Il2CppArrayBase<Vector3>)(object)normals).Length + "/" + ((Il2CppArrayBase<Vector4>)(object)tangents).Length + "/" + ((Il2CppArrayBase<Vector2>)(object)uv).Length + "/" + ((Il2CppArrayBase<Vector2>)(object)uv2).Length;
				return false;
			}
			for (int i = 0; i < ((Il2CppArrayBase<Vector3>)(object)normals).Length; i++)
			{
				val = ((Il2CppArrayBase<Vector3>)(object)normals)[i];
				if (!(Vector3.Dot(((Vector3)(ref val)).normalized, Vector3.up) >= 0.9999f))
				{
					failure = "non-upward-normal:" + i;
					return false;
				}
			}
		}
		catch (Exception ex)
		{
			failure = "vertex-channel-read:" + ex.GetType().Name;
			return false;
		}
		return true;
	}

	private static bool AllRendererSlotsUseNativeProfile(Transform root, string profileName)
	{
		if ((Object)(object)root == (Object)null)
		{
			return false;
		}
		Renderer[] array = Il2CppArrayBase<Renderer>.op_Implicit(((Component)root).GetComponentsInChildren<Renderer>(true));
		if (array.Length != 0)
		{
			return array.All((Renderer renderer) => ((Il2CppArrayBase<Material>)(object)renderer.sharedMaterials).Length > 0 && ((IEnumerable<Material>)renderer.sharedMaterials).All((Material material) => (Object)(object)material != (Object)null && string.Equals(NormalizeNativeMaterialName(((Object)material).name), profileName, StringComparison.Ordinal)));
		}
		return false;
	}

	private static int CountRendererSlotsUsingNativeProfile(Transform root, string profileName)
	{
		if ((Object)(object)root == (Object)null)
		{
			return 0;
		}
		return ((IEnumerable<Renderer>)((Component)root).GetComponentsInChildren<Renderer>(true)).Sum((Renderer renderer) => ((IEnumerable<Material>)renderer.sharedMaterials).Count((Material material) => (Object)(object)material != (Object)null && string.Equals(NormalizeNativeMaterialName(((Object)material).name), profileName, StringComparison.Ordinal)));
	}

	private bool RebindSceneMaterials(GameObject root)
	{
		Shader val = Shader.Find("HDRP/Lit");
		Shader val2 = Shader.Find("MilkShaders/Lit-Template");
		if ((Object)(object)val == (Object)null || (Object)(object)val2 == (Object)null)
		{
			log.LogError((object)("Vektor Kill House material gate failed: required resident shaders are unavailable; hdrp=" + ((Object)(object)val != (Object)null) + ", milkLitTemplate=" + ((Object)(object)val2 != (Object)null) + "."));
			return false;
		}
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (Renderer componentsInChild in root.GetComponentsInChildren<Renderer>(true))
		{
			Material[] array = Il2CppArrayBase<Material>.op_Implicit((Il2CppArrayBase<Material>)(object)componentsInChild.sharedMaterials);
			bool flag = false;
			for (int i = 0; i < array.Length; i++)
			{
				Material val3 = array[i];
				if ((Object)(object)val3 == (Object)null)
				{
					return false;
				}
				if (ownedRuntimeMaterialIds.Contains(((Object)val3).GetInstanceID()))
				{
					continue;
				}
				if (!runtimeMaterialsBySourceInstance.TryGetValue(((Object)val3).GetInstanceID(), out var value) || (Object)(object)value == (Object)null)
				{
					string text = NormalizeNativeMaterialName(((Object)val3).name);
					if (!NativeMaterialProfiles.TryGetValue(text, out var value2))
					{
						log.LogError((object)("Vektor Kill House material gate failed: no audited native profile for " + ((Object)val3).name + "."));
						return false;
					}
					Shader resident = (string.Equals(value2.ResidentShaderName, "MilkShaders/Lit-Template", StringComparison.Ordinal) ? val2 : val);
					value = CreateResidentNativeMaterial(val3, resident, text, value2);
					if ((Object)(object)value == (Object)null)
					{
						return false;
					}
					runtimeMaterialsBySourceInstance[((Object)val3).GetInstanceID()] = value;
					ownedRuntimeMaterialIds.Add(((Object)value).GetInstanceID());
					hashSet.Add(text);
				}
				array[i] = value;
				flag = true;
				num++;
			}
			if (flag)
			{
				componentsInChild.sharedMaterials = Il2CppReferenceArray<Material>.op_Implicit(array);
			}
			int num6 = ApplyFixtureVisualState(componentsInChild);
			switch (num6)
			{
			case 1:
				num2++;
				break;
			case 2:
				num3++;
				break;
			case 3:
				num4++;
				break;
			}
			if (num6 > 0 && !FixtureVisualStateValid(componentsInChild, num6))
			{
				num5++;
			}
		}
		int rendererCount;
		int familyCount;
		int invalidCount;
		string firstFailure;
		bool flag2 = ValidateFurnitureRendererClosure(root, out rendererCount, out familyCount, out invalidCount, out firstFailure);
		string[] array2 = runtimeMaterialsBySourceInstance.Values.Where((Material material) => !MaterialHasResidentProfileContract(material)).Select(DescribeResidentProfileValidation).ToArray();
		bool result = num > 0 && runtimeMaterialsBySourceInstance.Count == NativeMaterialProfiles.Count && hashSet.SetEquals(NativeMaterialProfiles.Keys) && array2.Length == 0 && flag2 && num2 > 0 && num3 > 0 && num4 > 0 && num5 == 0;
		log.LogInfo((object)("Vektor Kill House material rebind: passed=" + result + ", slots=" + num + ", uniqueNativeMaterials=" + runtimeMaterialsBySourceInstance.Count + ", nativeProfiles=" + hashSet.Count + ", matteArchitecturalProfiles=3" + ((array2.Length == 0) ? string.Empty : (", invalidResidentProfiles=[" + string.Join(" | ", array2) + "]")) + ", furnitureRenderers=" + rendererCount + ", furnitureFamilies=" + familyCount + ", invalidFurnitureRenderers=" + invalidCount + (string.IsNullOrEmpty(firstFailure) ? string.Empty : (", furnitureFailure=" + firstFailure)) + ", fixtureVisualStates=lit:" + num2 + "/dim:" + num3 + "/dark:" + num4 + ", invalidFixtureVisuals=" + num5 + ", fluorescentEmission=307.2/dim9.6/exposureWeight0, copyPropertiesUsed=false."));
		return result;
	}

	private static string DescribeResidentProfileValidation(Material material)
	{
		string text = (((Object)(object)material != (Object)null && ((Object)material).name != null && ((Object)material).name.StartsWith("RUNTIME_NATIVE_", StringComparison.Ordinal)) ? ((Object)material).name.Substring("RUNTIME_NATIVE_".Length) : "<unknown>");
		if (!NativeMaterialProfiles.TryGetValue(text, out var value))
		{
			return text + "{profile=false}";
		}
		bool flag = FurnitureSurfaceContractValid(material, text);
		return text + "{resident=" + MaterialHasResidentContract(material, value) + ",surface=" + flag + ",texture=" + MaterialHasExactTextureClosure(material, text) + (flag ? string.Empty : (",surfaceValues=" + DescribeFurnitureSurfaceValues(material))) + "," + DescribeMaterialContract(material) + "}";
	}

	private static string DescribeFurnitureSurfaceValues(Material material)
	{
		string[] source = new string[11]
		{
			"_MetallicRemapMin", "_MetallicRemapMax", "_SmoothnessRemapMin", "_SmoothnessRemapMax", "_AORemapMin", "_AORemapMax", "_OcclusionStrength", "_ReceivesSSR", "_MaterialID", "_TransmissionEnable",
			"_TransmissionMask"
		};
		return string.Join("/", source.Select((string name) => name + "=" + (((Object)(object)material != (Object)null && material.HasProperty(name)) ? material.GetFloat(name).ToString("0.###", CultureInfo.InvariantCulture) : "<missing>")));
	}

	private static bool ValidateFurnitureRendererClosure(GameObject root, out int rendererCount, out int familyCount, out int invalidCount, out string firstFailure)
	{
		rendererCount = 0;
		invalidCount = 0;
		firstFailure = string.Empty;
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		if ((Object)(object)root == (Object)null)
		{
			familyCount = 0;
			firstFailure = "root-null";
			return false;
		}
		foreach (MeshFilter componentsInChild in root.GetComponentsInChildren<MeshFilter>(true))
		{
			Mesh val = (((Object)(object)componentsInChild == (Object)null) ? null : componentsInChild.sharedMesh);
			if ((Object)(object)val == (Object)null || !FurnitureMaterialSlotsByMesh.TryGetValue(((Object)val).name, out var value))
			{
				continue;
			}
			rendererCount++;
			hashSet.Add(((Object)val).name);
			MeshRenderer component = ((Component)componentsInChild).GetComponent<MeshRenderer>();
			Material[] array = (((Object)(object)component == (Object)null) ? Array.Empty<Material>() : Il2CppArrayBase<Material>.op_Implicit((Il2CppArrayBase<Material>)(object)((Renderer)component).sharedMaterials));
			string text = string.Empty;
			if ((Object)(object)component == (Object)null)
			{
				text = "renderer-missing";
			}
			else if (val.subMeshCount <= 0 || array.Length != val.subMeshCount)
			{
				text = "submesh-slot-count:" + val.subMeshCount + "/" + array.Length;
			}
			else if (!val.HasVertexAttribute((VertexAttribute)4))
			{
				text = "uv0-missing";
			}
			else if (!val.HasVertexAttribute((VertexAttribute)5))
			{
				text = "uv1-missing";
			}
			else if (string.Equals(((Object)val).name, "T_bathtub", StringComparison.Ordinal) && !val.HasVertexAttribute((VertexAttribute)6))
			{
				text = "bathtub-uv2-missing";
			}
			else if (FurnitureMeshesWithoutVertexColor.Contains(((Object)val).name) && val.HasVertexAttribute((VertexAttribute)3))
			{
				text = "unexpected-color0";
			}
			else if (!FurnitureMeshesWithoutVertexColor.Contains(((Object)val).name) && !val.HasVertexAttribute((VertexAttribute)3))
			{
				text = "color0-missing";
			}
			else
			{
				for (int i = 0; i < array.Length; i++)
				{
					string text2 = value[Math.Min(i, value.Length - 1)];
					Material val2 = array[i];
					if ((Object)(object)val2 == (Object)null || !string.Equals(NormalizeNativeMaterialName(((Object)val2).name), text2, StringComparison.Ordinal))
					{
						text = "slot-" + i + ":expected-" + text2 + "/actual-" + (((Object)(object)val2 == (Object)null) ? "null" : NormalizeNativeMaterialName(((Object)val2).name));
						break;
					}
					if (!MaterialHasExactTextureClosure(val2, text2))
					{
						text = "slot-" + i + ":texture-closure-" + text2;
						break;
					}
				}
			}
			if (!string.IsNullOrEmpty(text))
			{
				invalidCount++;
				if (string.IsNullOrEmpty(firstFailure))
				{
					firstFailure = HierarchyPath(((Component)componentsInChild).transform) + ":" + text;
				}
			}
		}
		familyCount = hashSet.Count;
		if (rendererCount > 0)
		{
			return invalidCount == 0;
		}
		return false;
	}

	private static int ApplyFixtureVisualState(Renderer renderer)
	{
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ec: Expected O, but got Unknown
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0109: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		Transform val = (((Object)(object)renderer == (Object)null) ? null : ((Component)renderer).transform);
		string text = string.Empty;
		while ((Object)(object)val != (Object)null)
		{
			if (((Object)val).name.StartsWith("ROOM_LIGHT_", StringComparison.Ordinal) && ((Object)val).name.IndexOf("_STATE_", StringComparison.Ordinal) >= 0)
			{
				text = ((Object)val).name;
				break;
			}
			val = val.parent;
		}
		int num = (text.EndsWith("_STATE_LIT", StringComparison.Ordinal) ? 1 : (text.EndsWith("_STATE_DIM", StringComparison.Ordinal) ? 2 : (text.EndsWith("_STATE_DARK", StringComparison.Ordinal) ? 3 : 0)));
		float num2;
		switch (num)
		{
		case 0:
			return 0;
		default:
			num2 = 0f;
			break;
		case 2:
			num2 = 9.6f;
			break;
		case 1:
			num2 = 307.2f;
			break;
		}
		float num3 = num2;
		Color val2 = (Color)((num3 <= 0.001f) ? Color.black : new Color(num3, num3, num3, 1f));
		Color val3 = ((num3 <= 0.001f) ? Color.black : Color.white);
		MaterialPropertyBlock val4 = new MaterialPropertyBlock();
		renderer.GetPropertyBlock(val4);
		val4.SetColor("_EmissiveColor", val2);
		val4.SetColor("_EmissiveColorLDR", val3);
		val4.SetColor("_EmissionColor", val3);
		val4.SetFloat("_EmissiveIntensity", num3);
		val4.SetFloat("_UseEmissiveIntensity", 1f);
		val4.SetFloat("_EmissiveColorMode", 1f);
		val4.SetFloat("_AlbedoAffectEmissive", 1f);
		val4.SetFloat("_EmissiveIntensityUnit", 1f);
		val4.SetFloat("_EmissiveExposureWeight", 0f);
		renderer.SetPropertyBlock(val4);
		return num;
	}

	private static bool FixtureVisualStateValid(Renderer renderer, int state)
	{
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c0: Expected O, but got Unknown
		//IL_01cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d2: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)renderer == (Object)null || state < 1 || state > 3 || ((Il2CppArrayBase<Material>)(object)renderer.sharedMaterials).Length == 0)
		{
			return false;
		}
		Transform val = ((Component)renderer).transform;
		while ((Object)(object)val != (Object)null && !((Object)val).name.StartsWith("NATIVE_Lamp_fluorescent_B_", StringComparison.Ordinal))
		{
			val = val.parent;
		}
		if (!((Object)(object)val == (Object)null))
		{
			Vector3 val2 = val.TransformDirection(Vector3.back);
			if (!(Vector3.Dot(((Vector3)(ref val2)).normalized, Vector3.down) < 0.98f))
			{
				foreach (Material item in (Il2CppArrayBase<Material>)(object)renderer.sharedMaterials)
				{
					if ((Object)(object)item == (Object)null || !string.Equals(NormalizeNativeMaterialName(((Object)item).name), "Lamps_C_on__cagville", StringComparison.Ordinal) || !item.HasProperty("_EmissiveColor") || !item.HasProperty("_EmissiveColorMap") || !item.HasProperty("_EmissiveIntensity") || !item.HasProperty("_UseEmissiveIntensity") || !item.HasProperty("_EmissiveIntensityUnit") || !item.HasProperty("_AlbedoAffectEmissive") || !item.HasProperty("_EmissiveExposureWeight"))
					{
						return false;
					}
					Texture texture = item.GetTexture("_EmissiveColorMap");
					if ((Object)(object)texture == (Object)null || !string.Equals(((Object)texture).name, "Lamps_C_Emissive", StringComparison.Ordinal) || !item.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") || Mathf.Abs(item.GetFloat("_AlbedoAffectEmissive") - 1f) > 0.001f)
					{
						return false;
					}
				}
				float num = state switch
				{
					2 => 9.6f, 
					1 => 307.2f, 
					_ => 0f, 
				};
				MaterialPropertyBlock val3 = new MaterialPropertyBlock();
				renderer.GetPropertyBlock(val3);
				Color color = val3.GetColor("_EmissiveColor");
				if (Mathf.Abs(val3.GetFloat("_EmissiveIntensity") - num) <= 0.01f && Mathf.Abs(val3.GetFloat("_UseEmissiveIntensity") - 1f) <= 0.001f && Mathf.Abs(val3.GetFloat("_AlbedoAffectEmissive") - 1f) <= 0.001f && Mathf.Abs(val3.GetFloat("_EmissiveIntensityUnit") - 1f) <= 0.001f && Mathf.Abs(val3.GetFloat("_EmissiveExposureWeight") - 0f) <= 0.001f)
				{
					return Mathf.Abs(((Color)(ref color)).maxColorComponent - num) <= 0.01f;
				}
				return false;
			}
		}
		return false;
	}

	private Material CreateResidentNativeMaterial(Material source, Shader resident, string profileName, NativeMaterialProfile profile)
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e9: Expected O, but got Unknown
		//IL_0204: Unknown result type (might be due to invalid IL or missing references)
		//IL_0211: Unknown result type (might be due to invalid IL or missing references)
		//IL_0555: Unknown result type (might be due to invalid IL or missing references)
		//IL_0534: Unknown result type (might be due to invalid IL or missing references)
		//IL_055a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0563: Unknown result type (might be due to invalid IL or missing references)
		//IL_0571: Unknown result type (might be due to invalid IL or missing references)
		//IL_0622: Unknown result type (might be due to invalid IL or missing references)
		//IL_0630: Unknown result type (might be due to invalid IL or missing references)
		//IL_0641: Unknown result type (might be due to invalid IL or missing references)
		//IL_07fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_080b: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			Color color = GetColor(source, "_BaseColor", profile.BaseColor);
			Texture texture = GetTexture(source, "_BaseColorMap");
			Texture val = (profile.HasNormal ? GetTexture(source, "_NormalMap") : null);
			Texture val2 = (profile.HasMask ? GetTexture(source, "_MaskMap") : null);
			Texture val3 = (profile.HasEmissiveMap ? GetTexture(source, "_EmissiveColorMap") : null);
			string value;
			bool flag = ExpectedDetailTextureNames.TryGetValue(profileName, out value);
			Texture val4 = (flag ? GetTexture(source, "_DetailMap") : null);
			string value2;
			string text = (ExpectedBaseTextureNames.TryGetValue(profileName, out value2) ? value2 : string.Empty);
			if ((Object)(object)texture == (Object)null || (profile.HasNormal && (Object)(object)val == (Object)null) || (object)texture == Texture2D.whiteTexture || (!string.IsNullOrEmpty(text) && !string.Equals(((Object)texture).name, text, StringComparison.Ordinal)) || (profile.HasMask && (Object)(object)val2 == (Object)null) || (profile.HasEmissiveMap && (Object)(object)val3 == (Object)null) || !TextureNameMatches(val, ExpectedNormalTextureNames, profileName, profile.HasNormal) || !TextureNameMatches(val2, ExpectedMaskTextureNames, profileName, profile.HasMask) || !TextureNameMatches(val3, ExpectedEmissiveTextureNames, profileName, profile.HasEmissiveMap) || (flag && !TextureNameEquals(val4, value)))
			{
				log.LogError((object)("Vektor Kill House material gate failed: transported vanilla texture closure is incomplete for " + profileName + "; expectedBase=" + text + ", actualBase=" + (((Object)(object)texture == (Object)null) ? "<null>" : ((Object)texture).name) + "."));
				return null;
			}
			Material val5 = new Material(resident)
			{
				name = "RUNTIME_NATIVE_" + profileName,
				enableInstancing = true,
				renderQueue = 2225
			};
			val5.shader = resident;
			val5.renderQueue = 2225;
			SetColor(val5, "_BaseColor", color);
			SetColor(val5, "_Color", color);
			SetTexture(val5, "_BaseColorMap", texture);
			SetTexture(val5, "_BaseMap", texture);
			SetTexture(val5, "_MainTex", texture);
			SetFloat(val5, "_Metallic", profile.Metallic);
			SetFloat(val5, "_Smoothness", profile.Smoothness);
			SetFloat(val5, "_Glossiness", profile.Smoothness);
			SetFloat(val5, "_NormalScale", profile.NormalScale);
			SetFloat(val5, "_BumpScale", profile.NormalScale);
			SetFloat(val5, "_SurfaceType", 0f);
			SetFloat(val5, "_Surface", 0f);
			SetFloat(val5, "_AlphaCutoffEnable", 0f);
			SetFloat(val5, "_DoubleSidedEnable", 0f);
			SetFloat(val5, "_CullMode", 2f);
			SetFloat(val5, "_CullModeForward", 2f);
			SetFloat(val5, "_Cull", 2f);
			SetFloat(val5, "_ZWrite", 1f);
			if (FurnitureSurfaceProfiles.TryGetValue(profileName, out var value3))
			{
				SetFloat(val5, "_MetallicRemapMin", 0f);
				SetFloat(val5, "_MetallicRemapMax", value3.MetallicRemapMax);
				SetFloat(val5, "_SmoothnessRemapMin", 0f);
				SetFloat(val5, "_SmoothnessRemapMax", value3.SmoothnessRemapMax);
				SetFloat(val5, "_AORemapMin", value3.AoRemapMin);
				SetFloat(val5, "_AORemapMax", value3.AoRemapMax);
				SetFloat(val5, "_OcclusionStrength", value3.OcclusionStrength);
				SetFloat(val5, "_ReceivesSSR", value3.ReceivesSsr);
				SetFloat(val5, "_MaterialID", 1f);
				SetFloat(val5, "_TransmissionEnable", 1f);
				SetFloat(val5, "_TransmissionMask", 1f);
			}
			if (profile.MatteArchitectural)
			{
				SetFloat(val5, "_MetallicRemapMin", 0f);
				SetFloat(val5, "_MetallicRemapMax", 0f);
				SetFloat(val5, "_SmoothnessRemapMin", 0f);
				SetFloat(val5, "_SmoothnessRemapMax", profile.Smoothness);
				SetFloat(val5, "_ReceivesSSR", 0f);
				SetFloat(val5, "_EnvironmentReflections", 0f);
				SetFloat(val5, "_CoatMask", 0f);
				SetFloat(val5, "_ClearCoatMask", 0f);
			}
			if (profile.HasNormal)
			{
				SetTexture(val5, "_NormalMap", val);
				SetTexture(val5, "_BumpMap", val);
				val5.EnableKeyword("_NORMALMAP");
				val5.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
			}
			else
			{
				val5.DisableKeyword("_NORMALMAP");
				val5.DisableKeyword("_NORMALMAP_TANGENT_SPACE");
			}
			if (profile.HasMask)
			{
				SetTexture(val5, "_MaskMap", val2);
				val5.EnableKeyword("_MASKMAP");
			}
			else
			{
				SetTexture(val5, "_MaskMap", null);
				val5.DisableKeyword("_MASKMAP");
			}
			Color value4 = (Color)((profile.EmissiveIntensity > 0.001f) ? new Color(profile.EmissiveIntensity, profile.EmissiveIntensity, profile.EmissiveIntensity, 1f) : Color.black);
			SetColor(val5, "_EmissiveColor", value4);
			SetColor(val5, "_EmissionColor", value4);
			SetTexture(val5, "_EmissiveColorMap", val3);
			SetTexture(val5, "_EmissionMap", val3);
			if (flag)
			{
				SetTexture(val5, "_DetailMap", val4);
			}
			if (profile.HasEmissiveMap && profile.EmissiveIntensity > 0.001f)
			{
				val5.EnableKeyword("_EMISSIVE_COLOR_MAP");
				val5.EnableKeyword("_EMISSION");
			}
			else
			{
				val5.DisableKeyword("_EMISSIVE_COLOR_MAP");
				val5.DisableKeyword("_EMISSION");
			}
			if (string.Equals(profileName, "Lamps_C_on__cagville", StringComparison.Ordinal))
			{
				Color value5 = default(Color);
				((Color)(ref value5))._002Ector(307.2f, 307.2f, 307.2f, 1f);
				SetColor(val5, "_EmissiveColor", value5);
				SetColor(val5, "_EmissiveColorLDR", Color.white);
				SetColor(val5, "_EmissionColor", Color.white);
				SetFloat(val5, "_UseEmissiveIntensity", 1f);
				SetFloat(val5, "_EmissiveColorMode", 1f);
				SetFloat(val5, "_AlbedoAffectEmissive", 1f);
				SetFloat(val5, "_EmissiveIntensity", 307.2f);
				SetFloat(val5, "_EmissiveIntensityUnit", 1f);
				SetFloat(val5, "_EmissiveExposureWeight", 0f);
				val5.EnableKeyword("_EMISSIVE_COLOR_MAP");
				val5.DisableKeyword("_EMISSION");
			}
			else if (string.Equals(profileName, "Kitchen_TableChair", StringComparison.Ordinal))
			{
				SetFloat(val5, "_BASE_LAYER_TRIPLANAR", 0f);
				SetFloat(val5, "_DETAIL_TRIPLANAR_UV", 0f);
				SetFloat(val5, "_ConservativeDepthOffsetEnable", 0f);
				SetFloat(val5, "_ExcludeFromTUAndAA", 0f);
				SetFloat(val5, "_MaterialTypeMask", 2f);
				SetFloat(val5, "_RenderQueueType", 1f);
				SetFloat(val5, "_RequireSplitLighting", 0f);
				SetFloat(val5, "_USE_TRANSPARENT_SHADOWS", 0f);
				SetFloat(val5, "_saturation", 1f);
				SetFloat(val5, "_ReceivesSSR", 0f);
				val5.EnableKeyword("_DISABLE_SSR");
				val5.EnableKeyword("_DISABLE_SSR_TRANSPARENT");
				val5.DisableKeyword("_MASKMAP");
				val5.DisableKeyword("_NORMALMAP");
				val5.DisableKeyword("_NORMALMAP_TANGENT_SPACE");
			}
			else if (string.Equals(profileName, "In_Floor_Basement", StringComparison.Ordinal))
			{
				if (val5.HasProperty("_DetailMap"))
				{
					val5.SetTextureScale("_DetailMap", new Vector2(5f, 5f));
					val5.SetTextureOffset("_DetailMap", Vector2.zero);
				}
				SetFloat(val5, "_BASE_LAYER_TRIPLANAR", 0f);
				SetFloat(val5, "_DETAIL_TRIPLANAR_UV", 1f);
				SetFloat(val5, "_ConservativeDepthOffsetEnable", 0f);
				SetFloat(val5, "_ExcludeFromTUAndAA", 0f);
				SetFloat(val5, "_MaterialTypeMask", 2f);
				SetFloat(val5, "_RenderQueueType", 1f);
				SetFloat(val5, "_RequireSplitLighting", 0f);
				SetFloat(val5, "_USE_TRANSPARENT_SHADOWS", 0f);
				SetFloat(val5, "_saturation", 1f);
				SetFloat(val5, "_ReceivesSSR", 0f);
				val5.EnableKeyword("_DETAIL_MAP");
				val5.EnableKeyword("_DETAIL_TRIPLANAR_UV");
				val5.EnableKeyword("_DISABLE_SSR_TRANSPARENT");
				val5.DisableKeyword("_DISABLE_SSR");
				val5.DisableKeyword("_MASKMAP");
				val5.DisableKeyword("_NORMALMAP");
				val5.DisableKeyword("_NORMALMAP_TANGENT_SPACE");
			}
			else if (string.Equals(profileName, "Kitchen_Cabinet_Marble", StringComparison.Ordinal))
			{
				val5.DisableKeyword("_NORMALMAP");
				val5.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
			}
			else if (string.Equals(profileName, "Floor", StringComparison.Ordinal))
			{
				SetFloat(val5, "_UVBase", 5f);
				SetFloat(val5, "_TexWorldScale", 0.25f);
				SetFloat(val5, "_ObjectSpaceUVMapping", 0f);
				val5.EnableKeyword("_MAPPING_TRIPLANAR");
				val5.EnableKeyword("_DISABLE_SSR_TRANSPARENT");
				val5.EnableKeyword("_MASKMAP");
				val5.EnableKeyword("_NORMALMAP");
				val5.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
			}
			if (string.Equals(((Object)resident).name, "HDRP/Lit", StringComparison.Ordinal))
			{
				ValidateResidentHdrpMaterial(val5);
			}
			if ((FurnitureMaterialProfileNames.Contains(profileName) || string.Equals(profileName, "Floor", StringComparison.Ordinal)) && !ApplyEmbeddedExactDonorState(val5, profileName, out var failure))
			{
				Object.Destroy((Object)(object)val5);
				log.LogError((object)("Vektor Kill House material gate failed: exact embedded donor state could not be applied for " + profileName + "; " + failure + "."));
				return null;
			}
			if (!MaterialHasResidentContract(val5, profile))
			{
				string text2 = DescribeMaterialContract(val5);
				Object.Destroy((Object)(object)val5);
				log.LogError((object)("Vektor Kill House material gate failed: explicit resident state did not validate for " + profileName + "; " + text2 + "."));
				return null;
			}
			return val5;
		}
		catch (Exception ex)
		{
			log.LogError((object)("Vektor Kill House material gate failed for " + profileName + ": " + ex.GetType().Name + ": " + ex.Message));
			return null;
		}
	}

	private static string NormalizeNativeMaterialName(string name)
	{
		string text = name ?? string.Empty;
		if (text.StartsWith("MAT_NATIVE_", StringComparison.Ordinal))
		{
			text = text.Substring("MAT_NATIVE_".Length);
		}
		if (text.StartsWith("RUNTIME_NATIVE_", StringComparison.Ordinal))
		{
			text = text.Substring("RUNTIME_NATIVE_".Length);
		}
		if (text.EndsWith(" (Instance)", StringComparison.Ordinal))
		{
			text = text.Substring(0, text.Length - " (Instance)".Length);
		}
		return text;
	}

	private static string HierarchyPath(Transform item)
	{
		if ((Object)(object)item == (Object)null)
		{
			return "<null>";
		}
		List<string> list = new List<string>();
		Transform val = item;
		while ((Object)(object)val != (Object)null)
		{
			list.Add(((Object)val).name);
			val = val.parent;
		}
		list.Reverse();
		return string.Join("/", list);
	}

	private static Texture GetTexture(Material material, string property)
	{
		if (!((Object)(object)material != (Object)null) || !material.HasProperty(property))
		{
			return null;
		}
		return material.GetTexture(property);
	}

	private static Color GetColor(Material material, string property, Color fallback)
	{
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		if (!((Object)(object)material != (Object)null) || !material.HasProperty(property))
		{
			return fallback;
		}
		return material.GetColor(property);
	}

	private static void SetTexture(Material material, string property, Texture value)
	{
		if (material.HasProperty(property))
		{
			material.SetTexture(property, value);
		}
	}

	private static void SetFloat(Material material, string property, float value)
	{
		if (material.HasProperty(property))
		{
			material.SetFloat(property, value);
		}
	}

	private static void SetColor(Material material, string property, Color value)
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		if (material.HasProperty(property))
		{
			material.SetColor(property, value);
		}
	}

	private static bool ApplyEmbeddedExactDonorState(Material material, string profileName, out string failure)
	{
		//IL_0277: Unknown result type (might be due to invalid IL or missing references)
		//IL_0346: Unknown result type (might be due to invalid IL or missing references)
		//IL_0382: Unknown result type (might be due to invalid IL or missing references)
		failure = string.Empty;
		if ((Object)(object)material == (Object)null)
		{
			failure = "material-null";
			return false;
		}
		string text = typeof(OperatorKillHousePlugin).Assembly.GetManifestResourceNames().SingleOrDefault((string name) => name.EndsWith("." + profileName + ".json", StringComparison.OrdinalIgnoreCase));
		if (string.IsNullOrEmpty(text))
		{
			failure = "embedded-record-missing";
			return false;
		}
		try
		{
			using Stream stream = typeof(OperatorKillHousePlugin).Assembly.GetManifestResourceStream(text);
			if (stream == null)
			{
				failure = "embedded-stream-missing";
				return false;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(stream);
			JsonElement rootElement = jsonDocument.RootElement;
			long @int = rootElement.GetProperty("m_Shader").GetProperty("m_PathID").GetInt64();
			string text2 = @int switch
			{
				354L => "HDRP/Lit", 
				210L => "MilkShaders/Lit-Template", 
				_ => string.Empty, 
			};
			if (string.IsNullOrEmpty(text2) || (Object)(object)material.shader == (Object)null || !string.Equals(((Object)material.shader).name, text2, StringComparison.Ordinal))
			{
				string text3 = @int.ToString();
				Shader shader = material.shader;
				failure = "resident-shader:" + text3 + "/" + ((shader != null) ? ((Object)shader).name : null);
				return false;
			}
			JsonElement property = rootElement.GetProperty("m_SavedProperties");
			int num = 0;
			foreach (JsonProperty item in property.GetProperty("m_Floats").EnumerateObject())
			{
				if (material.HasProperty(item.Name))
				{
					material.SetFloat(item.Name, item.Value.GetSingle());
					num++;
				}
			}
			int num2 = 0;
			foreach (JsonProperty item2 in property.GetProperty("m_Colors").EnumerateObject())
			{
				if (material.HasProperty(item2.Name))
				{
					JsonElement value = item2.Value;
					material.SetColor(item2.Name, new Color(value.GetProperty("m_R").GetSingle(), value.GetProperty("m_G").GetSingle(), value.GetProperty("m_B").GetSingle(), value.GetProperty("m_A").GetSingle()));
					num2++;
				}
			}
			int num3 = 0;
			foreach (JsonProperty item3 in property.GetProperty("m_TexEnvs").EnumerateObject())
			{
				if (material.HasProperty(item3.Name))
				{
					JsonElement property2 = item3.Value.GetProperty("m_Scale");
					JsonElement property3 = item3.Value.GetProperty("m_Offset");
					material.SetTextureScale(item3.Name, new Vector2(property2.GetProperty("m_X").GetSingle(), property2.GetProperty("m_Y").GetSingle()));
					material.SetTextureOffset(item3.Name, new Vector2(property3.GetProperty("m_X").GetSingle(), property3.GetProperty("m_Y").GetSingle()));
					num3++;
				}
			}
			string[] array = ((IEnumerable<string>)material.shaderKeywords).ToArray();
			foreach (string text4 in array)
			{
				material.DisableKeyword(text4);
			}
			foreach (JsonElement item4 in rootElement.GetProperty("m_ValidKeywords").EnumerateArray())
			{
				material.EnableKeyword(item4.GetString());
			}
			HashSet<string> hashSet = new HashSet<string>(from jsonElement in rootElement.GetProperty("m_DisabledShaderPasses").EnumerateArray()
				select jsonElement.GetString(), StringComparer.Ordinal);
			array = ExactDonorShaderPasses;
			foreach (string text5 in array)
			{
				material.SetShaderPassEnabled(text5, !hashSet.Contains(text5));
			}
			array = ExactDonorOverrideTags;
			foreach (string text6 in array)
			{
				material.SetOverrideTag(text6, string.Empty);
			}
			foreach (JsonProperty item5 in rootElement.GetProperty("m_StringTagMap").EnumerateObject())
			{
				material.SetOverrideTag(item5.Name, item5.Value.GetString());
			}
			material.globalIlluminationFlags = (MaterialGlobalIlluminationFlags)rootElement.GetProperty("m_LightmapFlags").GetInt32();
			material.enableInstancing = rootElement.GetProperty("m_EnableInstancingVariants").GetBoolean();
			material.doubleSidedGI = rootElement.GetProperty("m_DoubleSidedGI").GetBoolean();
			material.renderQueue = rootElement.GetProperty("m_CustomRenderQueue").GetInt32();
			if (num == 0 || num2 == 0 || num3 == 0)
			{
				failure = "empty-applicable-state:" + num + "/" + num2 + "/" + num3;
				return false;
			}
			return EmbeddedExactDonorStateValid(material, profileName, out failure);
		}
		catch (Exception ex)
		{
			failure = ex.GetType().Name + ":" + ex.Message;
			return false;
		}
	}

	private static bool EmbeddedExactDonorStateValid(Material material, string profileName, out string failure)
	{
		//IL_0368: Unknown result type (might be due to invalid IL or missing references)
		//IL_0382: Invalid comparison between Unknown and I4
		//IL_0531: Unknown result type (might be due to invalid IL or missing references)
		//IL_0536: Unknown result type (might be due to invalid IL or missing references)
		//IL_065c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0661: Unknown result type (might be due to invalid IL or missing references)
		//IL_0677: Unknown result type (might be due to invalid IL or missing references)
		//IL_067c: Unknown result type (might be due to invalid IL or missing references)
		failure = string.Empty;
		string text = typeof(OperatorKillHousePlugin).Assembly.GetManifestResourceNames().SingleOrDefault((string name) => name.EndsWith("." + profileName + ".json", StringComparison.OrdinalIgnoreCase));
		if ((Object)(object)material == (Object)null || string.IsNullOrEmpty(text))
		{
			failure = "material-or-record-missing";
			return false;
		}
		try
		{
			using Stream utf8Json = typeof(OperatorKillHousePlugin).Assembly.GetManifestResourceStream(text);
			using JsonDocument jsonDocument = JsonDocument.Parse(utf8Json);
			JsonElement rootElement = jsonDocument.RootElement;
			if (material.renderQueue != rootElement.GetProperty("m_CustomRenderQueue").GetInt32())
			{
				failure = "render-queue";
				return false;
			}
			HashSet<string> hashSet = new HashSet<string>(from jsonElement in rootElement.GetProperty("m_ValidKeywords").EnumerateArray()
				select jsonElement.GetString(), StringComparer.Ordinal);
			if (!hashSet.SetEquals((IEnumerable<string>)material.shaderKeywords))
			{
				failure = "keywords:expected=" + string.Join("/", hashSet.OrderBy((string result) => result)) + "/actual=" + string.Join("/", ((IEnumerable<string>)material.shaderKeywords).OrderBy((string result) => result));
				return false;
			}
			HashSet<string> hashSet2 = new HashSet<string>(from jsonElement in rootElement.GetProperty("m_DisabledShaderPasses").EnumerateArray()
				select jsonElement.GetString(), StringComparer.Ordinal);
			string[] exactDonorShaderPasses = ExactDonorShaderPasses;
			foreach (string text2 in exactDonorShaderPasses)
			{
				bool flag = !material.GetShaderPassEnabled(text2);
				if (flag != hashSet2.Contains(text2))
				{
					failure = "shader-pass:" + text2 + "/expectedDisabled=" + hashSet2.Contains(text2) + "/actualDisabled=" + flag;
					return false;
				}
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (JsonProperty item in rootElement.GetProperty("m_StringTagMap").EnumerateObject())
			{
				dictionary[item.Name] = item.Value.GetString() ?? string.Empty;
			}
			exactDonorShaderPasses = ExactDonorOverrideTags;
			foreach (string text3 in exactDonorShaderPasses)
			{
				string value;
				string text4 = (dictionary.TryGetValue(text3, out value) ? value : string.Empty);
				string tag = material.GetTag(text3, false, string.Empty);
				if (!string.Equals(tag, text4, StringComparison.Ordinal))
				{
					failure = "tag:" + text3 + "/expected=" + text4 + "/actual=" + tag;
					return false;
				}
			}
			if ((int)material.globalIlluminationFlags != rootElement.GetProperty("m_LightmapFlags").GetInt32() || material.enableInstancing != rootElement.GetProperty("m_EnableInstancingVariants").GetBoolean() || material.doubleSidedGI != rootElement.GetProperty("m_DoubleSidedGI").GetBoolean())
			{
				failure = "material-render-flags";
				return false;
			}
			JsonElement property = rootElement.GetProperty("m_SavedProperties");
			int num2 = 0;
			foreach (JsonProperty item2 in property.GetProperty("m_Floats").EnumerateObject())
			{
				if (material.HasProperty(item2.Name))
				{
					num2++;
					if (!(Mathf.Abs(material.GetFloat(item2.Name) - item2.Value.GetSingle()) <= 0.001f))
					{
						failure = "float:" + item2.Name;
						return false;
					}
				}
			}
			int num3 = 0;
			Color expected = default(Color);
			foreach (JsonProperty item3 in property.GetProperty("m_Colors").EnumerateObject())
			{
				if (material.HasProperty(item3.Name))
				{
					num3++;
					JsonElement value2 = item3.Value;
					((Color)(ref expected))._002Ector(value2.GetProperty("m_R").GetSingle(), value2.GetProperty("m_G").GetSingle(), value2.GetProperty("m_B").GetSingle(), value2.GetProperty("m_A").GetSingle());
					if (!ColorApproximately(material.GetColor(item3.Name), expected, 0.001f))
					{
						failure = "color:" + item3.Name;
						return false;
					}
				}
			}
			int num4 = 0;
			Vector2 val = default(Vector2);
			Vector2 val2 = default(Vector2);
			foreach (JsonProperty item4 in property.GetProperty("m_TexEnvs").EnumerateObject())
			{
				if (material.HasProperty(item4.Name))
				{
					num4++;
					JsonElement property2 = item4.Value.GetProperty("m_Scale");
					JsonElement property3 = item4.Value.GetProperty("m_Offset");
					((Vector2)(ref val))._002Ector(property2.GetProperty("m_X").GetSingle(), property2.GetProperty("m_Y").GetSingle());
					((Vector2)(ref val2))._002Ector(property3.GetProperty("m_X").GetSingle(), property3.GetProperty("m_Y").GetSingle());
					if (!(Vector2.Distance(material.GetTextureScale(item4.Name), val) <= 0.001f) || !(Vector2.Distance(material.GetTextureOffset(item4.Name), val2) <= 0.001f))
					{
						failure = "texture-transform:" + item4.Name;
						return false;
					}
				}
			}
			if (num2 == 0 || num3 == 0 || num4 == 0)
			{
				failure = "empty-validated-state:" + num2 + "/" + num3 + "/" + num4;
				return false;
			}
			return true;
		}
		catch (Exception ex)
		{
			failure = ex.GetType().Name + ":" + ex.Message;
			return false;
		}
	}

	private static void ValidateResidentHdrpMaterial(Material material)
	{
		(FindManagedType("UnityEngine.Rendering.HighDefinition.HDMaterial")?.GetMethod("ValidateMaterial", BindingFlags.Static | BindingFlags.Public, null, new Type[1] { typeof(Material) }, null))?.Invoke(null, new object[1] { material });
	}

	private static bool MaterialHasResidentContract(Material material, string residentShaderName)
	{
		if ((Object)(object)material != (Object)null && (Object)(object)material.shader != (Object)null && string.Equals(((Object)material.shader).name, residentShaderName, StringComparison.Ordinal) && material.renderQueue == 2225 && (Object)(object)GetTexture(material, "_BaseColorMap") != (Object)null && (!material.HasProperty("_SurfaceType") || Mathf.Approximately(material.GetFloat("_SurfaceType"), 0f)))
		{
			if (material.HasProperty("_ZWrite"))
			{
				return material.GetFloat("_ZWrite") >= 0.99f;
			}
			return true;
		}
		return false;
	}

	private static bool MaterialHasResidentContract(Material material, NativeMaterialProfile profile)
	{
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		if (profile == null || !MaterialHasResidentContract(material, profile.ResidentShaderName))
		{
			return false;
		}
		Color color = GetColor(material, "_BaseColor", Color.clear);
		if (!(Mathf.Abs(color.r - profile.BaseColor.r) <= 0.001f) || !(Mathf.Abs(color.g - profile.BaseColor.g) <= 0.001f) || !(Mathf.Abs(color.b - profile.BaseColor.b) <= 0.001f) || (material.HasProperty("_Metallic") && !(Mathf.Abs(material.GetFloat("_Metallic") - profile.Metallic) <= 0.001f)) || (material.HasProperty("_Smoothness") && !(Mathf.Abs(material.GetFloat("_Smoothness") - profile.Smoothness) <= 0.001f)) || (material.HasProperty("_NormalScale") && !(Mathf.Abs(material.GetFloat("_NormalScale") - profile.NormalScale) <= 0.001f)))
		{
			return false;
		}
		if (!profile.MatteArchitectural)
		{
			return true;
		}
		if ((!material.HasProperty("_Metallic") || material.GetFloat("_Metallic") <= profile.Metallic + 0.001f) && (!material.HasProperty("_MetallicRemapMax") || material.GetFloat("_MetallicRemapMax") <= profile.Metallic + 0.001f) && (!material.HasProperty("_SmoothnessRemapMax") || material.GetFloat("_SmoothnessRemapMax") <= profile.Smoothness + 0.001f) && (!material.HasProperty("_ReceivesSSR") || material.GetFloat("_ReceivesSSR") <= 0.001f))
		{
			if (material.HasProperty("_EnvironmentReflections"))
			{
				return material.GetFloat("_EnvironmentReflections") <= 0.001f;
			}
			return true;
		}
		return false;
	}

	private static bool MaterialHasResidentProfileContract(Material material)
	{
		if ((Object)(object)material == (Object)null)
		{
			return false;
		}
		string text = ((((Object)material).name != null && ((Object)material).name.StartsWith("RUNTIME_NATIVE_", StringComparison.Ordinal)) ? ((Object)material).name.Substring("RUNTIME_NATIVE_".Length) : string.Empty);
		if (NativeMaterialProfiles.TryGetValue(text, out var value) && MaterialHasResidentContract(material, value) && FurnitureSurfaceContractValid(material, text))
		{
			return MaterialHasExactTextureClosure(material, text);
		}
		return false;
	}

	private static bool FurnitureSurfaceContractValid(Material material, string profileName)
	{
		if (!FurnitureSurfaceProfiles.TryGetValue(profileName, out var value))
		{
			return true;
		}
		if (FloatPropertyMatches(material, "_MetallicRemapMin", 0f) && FloatPropertyMatches(material, "_MetallicRemapMax", value.MetallicRemapMax) && FloatPropertyMatches(material, "_SmoothnessRemapMin", 0f) && FloatPropertyMatches(material, "_SmoothnessRemapMax", value.SmoothnessRemapMax) && FloatPropertyMatches(material, "_AORemapMin", value.AoRemapMin) && FloatPropertyMatches(material, "_AORemapMax", value.AoRemapMax) && OptionalFloatPropertyMatches(material, "_OcclusionStrength", value.OcclusionStrength) && FloatPropertyMatches(material, "_ReceivesSSR", value.ReceivesSsr) && FloatPropertyMatches(material, "_MaterialID", 1f) && FloatPropertyMatches(material, "_TransmissionEnable", 1f))
		{
			return OptionalFloatPropertyMatches(material, "_TransmissionMask", 1f);
		}
		return false;
	}

	private static bool FloatPropertyMatches(Material material, string property, float expected)
	{
		if ((Object)(object)material != (Object)null && material.HasProperty(property))
		{
			return Mathf.Abs(material.GetFloat(property) - expected) <= 0.001f;
		}
		return false;
	}

	private static bool OptionalFloatPropertyMatches(Material material, string property, float expected)
	{
		if ((Object)(object)material != (Object)null)
		{
			if (material.HasProperty(property))
			{
				return Mathf.Abs(material.GetFloat(property) - expected) <= 0.001f;
			}
			return true;
		}
		return false;
	}

	private static bool MaterialHasExactTextureClosure(Material material, string profileName)
	{
		//IL_017c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0181: Unknown result type (might be due to invalid IL or missing references)
		//IL_0186: Unknown result type (might be due to invalid IL or missing references)
		//IL_0195: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_03f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_03fb: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)material == (Object)null || !ExpectedBaseTextureNames.TryGetValue(profileName, out var value) || !TextureNameEquals(GetTexture(material, "_BaseColorMap"), value))
		{
			return false;
		}
		if (ExpectedNormalTextureNames.TryGetValue(profileName, out var value2) && !TextureNameEquals(GetTexture(material, "_NormalMap"), value2))
		{
			return false;
		}
		if (ExpectedMaskTextureNames.TryGetValue(profileName, out var value3) && !TextureNameEquals(GetTexture(material, "_MaskMap"), value3))
		{
			return false;
		}
		if (ExpectedEmissiveTextureNames.TryGetValue(profileName, out var value4) && !TextureNameEquals(GetTexture(material, "_EmissiveColorMap"), value4))
		{
			return false;
		}
		if (ExpectedDetailTextureNames.TryGetValue(profileName, out var value5) && !TextureNameEquals(GetTexture(material, "_DetailMap"), value5))
		{
			return false;
		}
		if ((FurnitureMaterialProfileNames.Contains(profileName) || string.Equals(profileName, "Floor", StringComparison.Ordinal)) && !EmbeddedExactDonorStateValid(material, profileName, out var _))
		{
			return false;
		}
		if (FurnitureMaterialProfileNames.Contains(profileName) && (!TextureTransformIsDefault(material, "_BaseColorMap") || (ExpectedNormalTextureNames.ContainsKey(profileName) && !TextureTransformIsDefault(material, "_NormalMap")) || (ExpectedMaskTextureNames.ContainsKey(profileName) && !TextureTransformIsDefault(material, "_MaskMap")) || (ExpectedEmissiveTextureNames.ContainsKey(profileName) && !TextureTransformIsDefault(material, "_EmissiveColorMap"))))
		{
			return false;
		}
		if (FurnitureMaterialProfileNames.Contains(profileName) && !FurnitureKeywordContractValid(material, profileName))
		{
			return false;
		}
		if (string.Equals(profileName, "Devices_On", StringComparison.Ordinal))
		{
			Color color = GetColor(material, "_EmissiveColor", Color.black);
			if (!material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") || Mathf.Abs(color.r - 1.720795f) > 0.001f || Mathf.Abs(color.g - 1.720795f) > 0.001f || Mathf.Abs(color.b - 1.720795f) > 0.001f)
			{
				return false;
			}
		}
		if (string.Equals(profileName, "Kitchen_Cabinet_Marble", StringComparison.Ordinal) && (material.IsKeywordEnabled("_NORMALMAP") || !material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE")))
		{
			return false;
		}
		if (string.Equals(profileName, "Kitchen_TableChair", StringComparison.Ordinal))
		{
			Shader shader = material.shader;
			if (!string.Equals((shader != null) ? ((Object)shader).name : null, "MilkShaders/Lit-Template", StringComparison.Ordinal) || !material.IsKeywordEnabled("_DISABLE_SSR") || !material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") || material.IsKeywordEnabled("_MASKMAP") || material.IsKeywordEnabled("_NORMALMAP") || material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") || (material.HasProperty("_MaterialTypeMask") && Mathf.Abs(material.GetFloat("_MaterialTypeMask") - 2f) > 0.001f) || (material.HasProperty("_saturation") && Mathf.Abs(material.GetFloat("_saturation") - 1f) > 0.001f))
			{
				return false;
			}
		}
		if (string.Equals(profileName, "Couch_Fabric", StringComparison.Ordinal))
		{
			Shader shader2 = material.shader;
			if (!string.Equals((shader2 != null) ? ((Object)shader2).name : null, "MilkShaders/Lit-Template", StringComparison.Ordinal) || !material.IsKeywordEnabled("_DETAIL_TRIPLANAR_UV") || !material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") || material.IsKeywordEnabled("_DISABLE_SSR") || material.IsKeywordEnabled("_MASKMAP") || material.IsKeywordEnabled("_NORMALMAP") || material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") || (material.HasProperty("_MaterialTypeMask") && Mathf.Abs(material.GetFloat("_MaterialTypeMask") - 2f) > 0.001f) || (material.HasProperty("_saturation") && Mathf.Abs(material.GetFloat("_saturation") - 1f) > 0.001f))
			{
				return false;
			}
		}
		if (string.Equals(profileName, "In_Floor_Basement", StringComparison.Ordinal))
		{
			Shader shader3 = material.shader;
			if (!string.Equals((shader3 != null) ? ((Object)shader3).name : null, "MilkShaders/Lit-Template", StringComparison.Ordinal) || !TextureTransformMatches(material, "_DetailMap", new Vector2(5f, 5f), Vector2.zero) || !material.IsKeywordEnabled("_DETAIL_MAP") || !material.IsKeywordEnabled("_DETAIL_TRIPLANAR_UV") || !material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") || material.IsKeywordEnabled("_DISABLE_SSR") || material.IsKeywordEnabled("_MASKMAP") || material.IsKeywordEnabled("_NORMALMAP") || material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE"))
			{
				return false;
			}
		}
		if (string.Equals(profileName, "Floor", StringComparison.Ordinal))
		{
			Shader shader4 = material.shader;
			if (!string.Equals((shader4 != null) ? ((Object)shader4).name : null, "HDRP/Lit", StringComparison.Ordinal) || !material.IsKeywordEnabled("_MAPPING_TRIPLANAR") || !material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") || !material.IsKeywordEnabled("_MASKMAP") || !material.IsKeywordEnabled("_NORMALMAP") || !material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") || !FloatPropertyMatches(material, "_UVBase", 5f) || !FloatPropertyMatches(material, "_TexWorldScale", 0.25f))
			{
				return false;
			}
		}
		return true;
	}

	private static bool FurnitureKeywordContractValid(Material material, string profileName)
	{
		if ((Object)(object)material == (Object)null)
		{
			return false;
		}
		if (string.Equals(profileName, "Kitchen_TableChair", StringComparison.Ordinal))
		{
			if (material.IsKeywordEnabled("_DISABLE_SSR") && material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") && !material.IsKeywordEnabled("_MASKMAP") && !material.IsKeywordEnabled("_NORMALMAP") && !material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") && !material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP"))
			{
				return !material.IsKeywordEnabled("_EMISSION");
			}
			return false;
		}
		if (string.Equals(profileName, "Couch_Fabric", StringComparison.Ordinal))
		{
			if (!material.IsKeywordEnabled("_DISABLE_SSR") && material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") && material.IsKeywordEnabled("_DETAIL_TRIPLANAR_UV") && !material.IsKeywordEnabled("_MASKMAP") && !material.IsKeywordEnabled("_NORMALMAP") && !material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") && !material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP"))
			{
				return !material.IsKeywordEnabled("_EMISSION");
			}
			return false;
		}
		bool flag = ExpectedNormalTextureNames.ContainsKey(profileName);
		bool flag2 = string.Equals(profileName, "Devices_On", StringComparison.Ordinal);
		if (!material.IsKeywordEnabled("_DISABLE_SSR") && material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") && material.IsKeywordEnabled("_MASKMAP") && material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") && material.IsKeywordEnabled("_NORMALMAP") == flag && material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") == flag2)
		{
			return !material.IsKeywordEnabled("_EMISSION");
		}
		return false;
	}

	private static bool TextureNameMatches(Texture texture, IReadOnlyDictionary<string, string> expectedNames, string profileName, bool required)
	{
		if (!required)
		{
			if (!((Object)(object)texture == (Object)null))
			{
				return !expectedNames.ContainsKey(profileName);
			}
			return true;
		}
		if (expectedNames.TryGetValue(profileName, out var value))
		{
			return TextureNameEquals(texture, value);
		}
		return false;
	}

	private static bool TextureNameEquals(Texture texture, string expected)
	{
		if ((Object)(object)texture != (Object)null)
		{
			return string.Equals(((Object)texture).name, expected, StringComparison.Ordinal);
		}
		return false;
	}

	private static bool ColorApproximately(Color actual, Color expected, float tolerance)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		if (Mathf.Abs(actual.r - expected.r) <= tolerance && Mathf.Abs(actual.g - expected.g) <= tolerance && Mathf.Abs(actual.b - expected.b) <= tolerance)
		{
			return Mathf.Abs(actual.a - expected.a) <= tolerance;
		}
		return false;
	}

	private static bool TextureTransformIsDefault(Material material, string property)
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)material != (Object)null && material.HasProperty(property) && Vector2.Distance(material.GetTextureScale(property), Vector2.one) <= 0.001f)
		{
			return Vector2.Distance(material.GetTextureOffset(property), Vector2.zero) <= 0.001f;
		}
		return false;
	}

	private static bool TextureTransformMatches(Material material, string property, Vector2 scale, Vector2 offset)
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)material != (Object)null && material.HasProperty(property) && Vector2.Distance(material.GetTextureScale(property), scale) <= 0.001f)
		{
			return Vector2.Distance(material.GetTextureOffset(property), offset) <= 0.001f;
		}
		return false;
	}

	private static string DescribeMaterialContract(Material material)
	{
		if ((Object)(object)material == (Object)null)
		{
			return "material=<null>";
		}
		return "shader=" + (((Object)(object)material.shader == (Object)null) ? "<null>" : ((Object)material.shader).name) + ", queue=" + material.renderQueue + ", expectedQueue=" + 2225 + ", baseMap=" + ((Object)(object)GetTexture(material, "_BaseColorMap") != (Object)null) + ", surfaceType=" + (material.HasProperty("_SurfaceType") ? material.GetFloat("_SurfaceType").ToString("F3", CultureInfo.InvariantCulture) : "<absent>") + ", zWrite=" + (material.HasProperty("_ZWrite") ? material.GetFloat("_ZWrite").ToString("F3", CultureInfo.InvariantCulture) : "<absent>");
	}

	private void ProbeEquippedWeaponIdentity()
	{
		try
		{
			ulong fingerprint;
			bool flag = TryBuildEquippedWeaponIdentityFingerprint(out fingerprint);
			if (!equippedWeaponIdentityInitialized || fingerprint != equippedWeaponIdentityFingerprint)
			{
				if (equippedWeaponIdentityInitialized && equippedWeaponIdentityFingerprint != 0)
				{
					RestoreWeaponIlluminationBoosts();
				}
				equippedWeaponIdentityFingerprint = fingerprint;
				equippedWeaponIdentityInitialized = true;
				lastOpticAuditSignature = string.Empty;
				if (!flag)
				{
					opticAuditPending = false;
					nextOpticAuditFrame = -1;
				}
				else
				{
					opticAuditPending = true;
					nextOpticAuditFrame = Time.frameCount + 10;
					log.LogInfo((object)("Vektor Kill House equipped-weapon identity changed; one-shot optic audit rearmed: " + fingerprint.ToString("X16", CultureInfo.InvariantCulture) + "."));
				}
			}
		}
		catch (Exception ex)
		{
			string text = ex.GetType().Name + ":" + ex.Message;
			if (!string.Equals(text, lastOpticAuditSignature, StringComparison.Ordinal))
			{
				lastOpticAuditSignature = text;
				log.LogWarning((object)("Vektor Kill House equipped-weapon identity probe deferred: " + text + "."));
			}
		}
	}

	private static bool TryBuildEquippedWeaponIdentityFingerprint(out ulong fingerprint)
	{
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		fingerprint = 0uL;
		if (!TryGetActiveEquippedWeapon(out var player, out var activeRoot, out var weapon))
		{
			return false;
		}
		ulong fingerprint2 = 1469598103934665603uL;
		fingerprint2 = MixWeaponIdentity(fingerprint2, ((Object)player).GetInstanceID());
		fingerprint2 = MixWeaponIdentity(fingerprint2, ((Object)activeRoot).GetInstanceID());
		fingerprint2 = MixWeaponIdentity(fingerprint2, ((Object)weapon).GetInstanceID());
		fingerprint2 = MixWeaponIdentity(fingerprint2, ((object)player.CurrentWeaponSlot/*cast due to constrained. prefix*/).GetHashCode());
		fingerprint2 = MixWeaponIdentity(fingerprint2, weapon.laserIndex);
		fingerprint2 = MixWeaponIdentity(fingerprint2, weapon.flashlightIndex);
		Transform transform = activeRoot.transform;
		fingerprint2 = MixWeaponIdentity(fingerprint2, (!((Object)(object)transform == (Object)null)) ? transform.childCount : 0);
		if ((Object)(object)transform != (Object)null)
		{
			for (int i = 0; i < transform.childCount; i++)
			{
				Transform child = transform.GetChild(i);
				fingerprint2 = MixWeaponIdentity(fingerprint2, (!((Object)(object)child == (Object)null)) ? ((Object)child).GetInstanceID() : 0);
				fingerprint2 = MixWeaponIdentity(fingerprint2, (!((Object)(object)child == (Object)null)) ? child.childCount : 0);
				fingerprint2 = MixWeaponIdentity(fingerprint2, ((Object)(object)child != (Object)null && ((Component)child).gameObject.activeSelf) ? 1 : 0);
			}
		}
		fingerprint2 = MixWeaponIdentity(fingerprint2, (weapon.Lasers != null) ? weapon.Lasers.Count : 0);
		if (weapon.Lasers != null)
		{
			for (int j = 0; j < weapon.Lasers.Count; j++)
			{
				fingerprint2 = MixWeaponIdentity(fingerprint2, (!((Object)(object)weapon.Lasers[j] == (Object)null)) ? ((Object)weapon.Lasers[j]).GetInstanceID() : 0);
			}
		}
		fingerprint2 = MixWeaponIdentity(fingerprint2, (weapon.Flashlights != null) ? weapon.Flashlights.Count : 0);
		if (weapon.Flashlights != null)
		{
			for (int k = 0; k < weapon.Flashlights.Count; k++)
			{
				fingerprint2 = MixWeaponIdentity(fingerprint2, (!((Object)(object)weapon.Flashlights[k] == (Object)null)) ? ((Object)weapon.Flashlights[k]).GetInstanceID() : 0);
			}
		}
		fingerprint = ((fingerprint2 == 0L) ? 1 : fingerprint2);
		return true;
	}

	private static ulong MixWeaponIdentity(ulong fingerprint, int value)
	{
		fingerprint ^= (uint)value;
		return fingerprint * 1099511628211L;
	}

	private static bool TryGetActiveEquippedWeapon(out PlayerNetworking player, out GameObject activeRoot, out WeaponV3 weapon)
	{
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0079: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		player = (((Object)(object)GameManager.instance == (Object)null) ? null : GameManager.myPlayerNetworking);
		activeRoot = (((Object)(object)player == (Object)null) ? null : player.activeWeapon);
		weapon = (((Object)(object)activeRoot == (Object)null) ? null : activeRoot.GetComponent<WeaponV3>());
		if ((Object)(object)player != (Object)null && (Object)(object)activeRoot != (Object)null)
		{
			Scene scene = activeRoot.scene;
			if (((Scene)(ref scene)).isLoaded && (Object)(object)weapon != (Object)null)
			{
				scene = ((Component)weapon).gameObject.scene;
				if (((Scene)(ref scene)).isLoaded)
				{
					return weapon.isEquiped;
				}
			}
		}
		return false;
	}

	private bool AuditLiveWeaponIllumination()
	{
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_0273: Unknown result type (might be due to invalid IL or missing references)
		//IL_0278: Unknown result type (might be due to invalid IL or missing references)
		//IL_03d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_03da: Unknown result type (might be due to invalid IL or missing references)
		//IL_0531: Unknown result type (might be due to invalid IL or missing references)
		//IL_0536: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (!TryGetActiveEquippedWeapon(out var _, out var activeRoot, out var weapon))
			{
				return false;
			}
			EnhanceLiveWeaponIllumination(activeRoot, weapon);
			List<string> list = new List<string>();
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			HWSReticleBrightness[] array = Il2CppArrayBase<HWSReticleBrightness>.op_Implicit(activeRoot.GetComponentsInChildren<HWSReticleBrightness>(true));
			Scene scene;
			foreach (HWSReticleBrightness val in array)
			{
				if ((Object)(object)val == (Object)null)
				{
					continue;
				}
				scene = ((Component)val).gameObject.scene;
				if (((Scene)(ref scene)).isLoaded)
				{
					num++;
					if (list.Count < 3)
					{
						Material material = (((Object)(object)val.ReticleRenderer == (Object)null) ? null : ((Renderer)val.ReticleRenderer).sharedMaterial);
						int num5 = ((val.reticleSettings == null || ((Il2CppArrayBase<ReticleSetting>)(object)val.reticleSettings).Length == 0) ? (-1) : Mathf.Clamp(val.CurrentBrightnessSetting, 0, ((Il2CppArrayBase<ReticleSetting>)(object)val.reticleSettings).Length - 1));
						ReticleSetting val2 = ((num5 < 0) ? null : ((Il2CppArrayBase<ReticleSetting>)(object)val.reticleSettings)[num5]);
						Material material2 = (((Object)(object)val.ReticleRendererNVG == (Object)null) ? null : ((Renderer)val.ReticleRendererNVG).sharedMaterial);
						list.Add("HWS{" + ((Object)val).name + ",current=" + val.CurrentBrightnessSetting + ",default=" + val.DefaultBrightnessSetting + ",settings=" + ((val.reticleSettings != null) ? ((Il2CppArrayBase<ReticleSetting>)(object)val.reticleSettings).Length : 0) + ",selected=" + ((val2 == null) ? "missing" : (val2.ReticleBrightness.ToString("F3", CultureInfo.InvariantCulture) + "/" + val2.ReticleBrightness_NVG.ToString("F3", CultureInfo.InvariantCulture))) + ",sizeNormalNvg=" + DescribeHwsReticleSize(material) + "/" + DescribeHwsReticleSize(material2) + ",material=" + DescribeEmission(material) + "}");
					}
				}
			}
			ReflexSightV2[] array2 = Il2CppArrayBase<ReflexSightV2>.op_Implicit(activeRoot.GetComponentsInChildren<ReflexSightV2>(true));
			foreach (ReflexSightV2 val3 in array2)
			{
				if ((Object)(object)val3 == (Object)null)
				{
					continue;
				}
				scene = ((Component)val3).gameObject.scene;
				if (((Scene)(ref scene)).isLoaded)
				{
					num2++;
					if (list.Count < 4)
					{
						ReticleIllumnation reticleIllumnation = val3.reticleIllumnation;
						list.Add("REFLEX{" + ((Object)val3).name + ",illumination=" + ((reticleIllumnation == null) ? "missing" : (reticleIllumnation.currentIllumnation.ToString("F3", CultureInfo.InvariantCulture) + "/" + reticleIllumnation.MinIllumnation.ToString("F3", CultureInfo.InvariantCulture) + "-" + reticleIllumnation.MaxIllumnation.ToString("F3", CultureInfo.InvariantCulture) + ",steps=" + reticleIllumnation.illumnationSteps.ToString("F3", CultureInfo.InvariantCulture))) + ",material=" + DescribeEmission(val3.ReticleMaterial) + "}");
					}
				}
			}
			IRLaserLight[] array3 = Il2CppArrayBase<IRLaserLight>.op_Implicit(activeRoot.GetComponentsInChildren<IRLaserLight>(true));
			foreach (IRLaserLight val4 in array3)
			{
				if ((Object)(object)val4 == (Object)null)
				{
					continue;
				}
				scene = ((Component)val4).gameObject.scene;
				if (((Scene)(ref scene)).isLoaded)
				{
					num3++;
					if (list.Count < 5)
					{
						list.Add("IR{" + ((Object)val4).name + ",light=" + ((Object)(object)val4._light != (Object)null && ((Behaviour)val4._light).enabled) + ",lumens=" + (((Object)(object)val4._lightData == (Object)null) ? "missing" : val4._lightData.intensity.ToString("F2", CultureInfo.InvariantCulture)) + ",line=" + ((Object)(object)val4.lineRenderer != (Object)null && ((Renderer)val4.lineRenderer).enabled) + ",range=" + val4.range.ToString("F2", CultureInfo.InvariantCulture) + "}");
					}
				}
			}
			WeaponV3[] array4 = (WeaponV3[])(object)new WeaponV3[1] { weapon };
			foreach (WeaponV3 val5 in array4)
			{
				if ((Object)(object)val5 == (Object)null)
				{
					continue;
				}
				scene = ((Component)val5).gameObject.scene;
				if (!((Scene)(ref scene)).isLoaded || !val5.isEquiped)
				{
					continue;
				}
				num4++;
				if (list.Count >= 6)
				{
					continue;
				}
				List<string> list2 = new List<string>();
				if (val5.Flashlights != null)
				{
					for (int j = 0; j < val5.Flashlights.Count; j++)
					{
						GameObject val6 = val5.Flashlights[j];
						if (!((Object)(object)val6 == (Object)null))
						{
							HDAdditionalLightData[] source = Il2CppArrayBase<HDAdditionalLightData>.op_Implicit(val6.GetComponentsInChildren<HDAdditionalLightData>(true));
							list2.Add(j + ":" + val6.activeSelf + "/" + val6.activeInHierarchy + "/" + string.Join(",", source.Select((HDAdditionalLightData data) => data.intensity.ToString("F1", CultureInfo.InvariantCulture))));
						}
					}
				}
				List<string> list3 = new List<string>();
				if (val5.Lasers != null)
				{
					for (int num6 = 0; num6 < val5.Lasers.Count; num6++)
					{
						GameObject val7 = val5.Lasers[num6];
						if (!((Object)(object)val7 == (Object)null))
						{
							HDAdditionalLightData[] source2 = Il2CppArrayBase<HDAdditionalLightData>.op_Implicit(val7.GetComponentsInChildren<HDAdditionalLightData>(true));
							Light[] source3 = Il2CppArrayBase<Light>.op_Implicit(val7.GetComponentsInChildren<Light>(true));
							LineRenderer[] source4 = Il2CppArrayBase<LineRenderer>.op_Implicit(val7.GetComponentsInChildren<LineRenderer>(true));
							string[] value = (from component in (IEnumerable<Component>)val7.GetComponentsInChildren<Component>(true)
								select (!((Object)(object)component == (Object)null)) ? ((object)component).GetType().Name : string.Empty into type
								where type.IndexOf("Laser", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("Beam", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0
								select type).Distinct().Take(12).ToArray();
							list3.Add(num6 + ":" + val7.activeSelf + "/" + val7.activeInHierarchy + "/lights=" + string.Join(",", source2.Select((HDAdditionalLightData data) => data.intensity.ToString("F1", CultureInfo.InvariantCulture))) + "/unityLights=" + string.Join(",", source3.Select(delegate(Light light)
							{
								//IL_009f: Unknown result type (might be due to invalid IL or missing references)
								//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
								string[] obj = new string[13]
								{
									((Object)light).name,
									":",
									((Behaviour)light).enabled.ToString(),
									"/",
									((Component)light).gameObject.activeInHierarchy.ToString(),
									"/i=",
									light.intensity.ToString("F1", CultureInfo.InvariantCulture),
									"/r=",
									light.range.ToString("F1", CultureInfo.InvariantCulture),
									"/p=",
									null,
									null,
									null
								};
								Vector3 position = ((Component)light).transform.position;
								obj[10] = ((Vector3)(ref position)).ToString("F2");
								obj[11] = "/layer=";
								obj[12] = ((Component)light).gameObject.layer.ToString();
								return string.Concat(obj);
							})) + "/lines=" + string.Join(",", source4.Select((LineRenderer line) => ((Renderer)line).enabled + ":" + DescribeEmission(((Renderer)line).sharedMaterial))) + "/controllers=" + string.Join(",", value));
						}
					}
				}
				list.Add("WEAPON{" + val5.displayName + ",flashlights=" + ((val5.Flashlights != null) ? val5.Flashlights.Count : 0) + ",flashlightIndex=" + val5.flashlightIndex + ",lasers=" + ((val5.Lasers != null) ? val5.Lasers.Count : 0) + ",laserIndex=" + val5.laserIndex + ",flashlightStates=" + string.Join(";", list2) + ",laserStates=" + string.Join(";", list3) + "}");
			}
			string text = "hws=" + num + ",reflex=" + num2 + ",ir=" + num3 + ",weapons=" + num4 + ",globalMultiplier=" + (((Object)(object)globalFlashlightMultiplier == (Object)null) ? "missing" : globalFlashlightMultiplier.MultiplierValue.ToString("F2", CultureInfo.InvariantCulture)) + ",boosts=reticleNormal:" + 2f.ToString("F1", CultureInfo.InvariantCulture) + "/reticleNvg:" + 1f.ToString("F1", CultureInfo.InvariantCulture) + "/reticleNormalSize:" + 1.5f.ToString("F1", CultureInfo.InvariantCulture) + "/visibleLaser:" + 6f.ToString("F1", CultureInfo.InvariantCulture) + "/beamEmission:" + 4f.ToString("F1", CultureInfo.InvariantCulture) + ",enhanced=hws:" + enhancedHwsReticles.Count + "/hwsSize:" + enhancedHwsReticleSizeRenderers.Count + "/reflex:stock/laserControllers:" + enhancedLaserLights.Count + "/laserLights:" + enhancedVisibleLaserLights.Count + "/laserBeams:" + boostedLaserBeamStates.Count + ",baselines=hws:" + hwsReticleBoostStates.Count + "/hwsSize:" + hwsReticleSizeStates.Count + "/laserControllers:" + visibleIrLaserBoostStates.Count + "/laserLights:" + visibleLaserLightBoostStates.Count + ",details=[" + string.Join(" | ", list) + "]";
			if (string.Equals(text, lastOpticAuditSignature, StringComparison.Ordinal))
			{
				return true;
			}
			lastOpticAuditSignature = text;
			log.LogInfo((object)("Vektor Kill House live optic/illuminator audit: " + text + "."));
			return true;
		}
		catch (Exception ex)
		{
			string text2 = ex.GetType().Name + ":" + ex.Message;
			if (string.Equals(text2, lastOpticAuditSignature, StringComparison.Ordinal))
			{
				return false;
			}
			lastOpticAuditSignature = text2;
			log.LogWarning((object)("Vektor Kill House live optic/illuminator audit deferred: " + text2 + "."));
			return false;
		}
	}

	private void EnhanceLiveWeaponIllumination(GameObject activeRoot, WeaponV3 activeWeapon)
	{
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0169: Unknown result type (might be due to invalid IL or missing references)
		//IL_016e: Unknown result type (might be due to invalid IL or missing references)
		//IL_050e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0513: Unknown result type (might be due to invalid IL or missing references)
		//IL_052a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0534: Expected O, but got Unknown
		RestoreStaleHwsReticleBoosts();
		HWSReticleBrightness[] array = Il2CppArrayBase<HWSReticleBrightness>.op_Implicit(activeRoot.GetComponentsInChildren<HWSReticleBrightness>(true));
		Scene scene;
		foreach (HWSReticleBrightness val in array)
		{
			if ((Object)(object)val == (Object)null)
			{
				continue;
			}
			scene = ((Component)val).gameObject.scene;
			if (!((Scene)(ref scene)).isLoaded || val.reticleSettings == null || ((Il2CppArrayBase<ReticleSetting>)(object)val.reticleSettings).Length == 0 || !ReticleBelongsToEquippedWeapon(val) || enhancedHwsReticles.Contains(((Object)val).GetInstanceID()))
			{
				continue;
			}
			HwsReticleBoostState hwsReticleBoostState = new HwsReticleBoostState(val);
			if (!hwsReticleBoostState.HasRecognizedVanillaRange)
			{
				continue;
			}
			enhancedHwsReticles.Add(((Object)val).GetInstanceID());
			hwsReticleBoostStates.Add(hwsReticleBoostState);
			for (int j = 0; j < ((Il2CppArrayBase<ReticleSetting>)(object)val.reticleSettings).Length; j++)
			{
				ReticleSetting val2 = ((Il2CppArrayBase<ReticleSetting>)(object)val.reticleSettings)[j];
				if (val2 != null)
				{
					val2.ReticleBrightness = Mathf.Min(hwsReticleBoostState.Normal[j] * 2f, 3840f);
					val2.ReticleBrightness_NVG = Mathf.Min(hwsReticleBoostState.Nvg[j] * 1f, 900f);
				}
			}
			TryEnhanceHwsReticleNormalSize(val, (Renderer)(object)val.ReticleRenderer);
			ApplyHwsSelectedBrightness(val);
		}
		WeaponV3[] array2 = (WeaponV3[])(object)new WeaponV3[1] { activeWeapon };
		foreach (WeaponV3 val3 in array2)
		{
			if ((Object)(object)val3 == (Object)null)
			{
				continue;
			}
			scene = ((Component)val3).gameObject.scene;
			if (!((Scene)(ref scene)).isLoaded || !val3.isEquiped || val3.Lasers == null)
			{
				continue;
			}
			for (int k = 0; k < val3.Lasers.Count; k++)
			{
				GameObject val4 = val3.Lasers[k];
				if ((Object)(object)val4 == (Object)null)
				{
					continue;
				}
				IRLaserLight[] array3 = Il2CppArrayBase<IRLaserLight>.op_Implicit(val4.GetComponentsInChildren<IRLaserLight>(true));
				HashSet<int> hashSet = new HashSet<int>(from controller in array3
					where (Object)(object)controller != (Object)null && (Object)(object)controller._light != (Object)null
					select ((Object)controller._light).GetInstanceID());
				bool flag = array3.Any((IRLaserLight controller) => (Object)(object)controller != (Object)null && !controller.IRonly && ((Component)controller).gameObject.layer != 16 && (((Object)(object)controller._light != (Object)null && ((Component)controller._light).gameObject.layer == 0) || ((Object)(object)controller._lightData != (Object)null && ((Component)controller._lightData).gameObject.layer == 0)));
				bool flag2 = array3.Length != 0 && !flag;
				IRLaserLight[] array4 = array3;
				foreach (IRLaserLight val5 in array4)
				{
					if ((Object)(object)val5 == (Object)null || val5.IRonly || ((Component)val5).gameObject.layer == 16)
					{
						continue;
					}
					GameObject val6 = (((Object)(object)val5._light != (Object)null) ? ((Component)val5._light).gameObject : (((Object)(object)val5._lightData == (Object)null) ? null : ((Component)val5._lightData).gameObject));
					if (!((Object)(object)val6 == (Object)null) && val6.layer == 0 && enhancedLaserLights.Add(((Object)val5).GetInstanceID()))
					{
						VisibleIrLaserBoostState visibleIrLaserBoostState = new VisibleIrLaserBoostState(val5);
						visibleIrLaserBoostStates.Add(visibleIrLaserBoostState);
						val5.minBrighness = visibleIrLaserBoostState.MinBrightness * 6f;
						val5.maxBrighness = visibleIrLaserBoostState.MaxBrightness * 6f;
						if ((Object)(object)visibleIrLaserBoostState.LightData != (Object)null)
						{
							visibleIrLaserBoostState.LightData.intensity = Mathf.Max(visibleIrLaserBoostState.LightDataIntensity * 6f, val5.minBrighness);
						}
						if ((Object)(object)visibleIrLaserBoostState.Light != (Object)null)
						{
							visibleIrLaserBoostState.Light.intensity = Mathf.Max(visibleIrLaserBoostState.LightIntensity * 6f, val5.minBrighness);
						}
					}
				}
				Light[] array5 = Il2CppArrayBase<Light>.op_Implicit(val4.GetComponentsInChildren<Light>(true));
				foreach (Light val7 in array5)
				{
					if (!((Object)(object)val7 == (Object)null) && ((Component)val7).gameObject.layer == 0 && !hashSet.Contains(((Object)val7).GetInstanceID()) && !((Object)(object)((Component)val7).GetComponentInParent<IRLaserLight>() != (Object)null) && enhancedVisibleLaserLights.Add(((Object)val7).GetInstanceID()))
					{
						HDAdditionalLightData component = ((Component)val7).GetComponent<HDAdditionalLightData>();
						VisibleLaserLightBoostState visibleLaserLightBoostState = new VisibleLaserLightBoostState(val7, component);
						visibleLaserLightBoostStates.Add(visibleLaserLightBoostState);
						if ((Object)(object)component != (Object)null)
						{
							component.intensity = visibleLaserLightBoostState.LightDataIntensity * 6f;
						}
						val7.intensity = visibleLaserLightBoostState.LightIntensity * 6f;
					}
				}
				LineRenderer[] array6 = Il2CppArrayBase<LineRenderer>.op_Implicit(val4.GetComponentsInChildren<LineRenderer>(true));
				foreach (LineRenderer val8 in array6)
				{
					if ((Object)(object)val8 == (Object)null || flag2 || ((Component)val8).gameObject.layer == 16)
					{
						continue;
					}
					IRLaserLight componentInParent = ((Component)val8).GetComponentInParent<IRLaserLight>();
					if (((Object)(object)componentInParent != (Object)null && componentInParent.IRonly) || !enhancedVisibleLaserBeams.Add(((Object)val8).GetInstanceID()))
					{
						continue;
					}
					Material sharedMaterial = ((Renderer)val8).sharedMaterial;
					if (!((Object)(object)sharedMaterial == (Object)null))
					{
						Material val9 = new Material(sharedMaterial)
						{
							name = ((Object)sharedMaterial).name + "_KH_BRIGHT_BEAM",
							hideFlags = (HideFlags)52
						};
						BoostHdrColor(val9, "_EmissiveColor", 4f);
						BoostHdrColor(val9, "_EmissionColor", 4f);
						BoostHdrColor(val9, "_UnlitColor", 4f);
						BoostHdrColor(val9, "_Color", 4f);
						if (val9.HasProperty("_UseEmissiveIntensity") && val9.GetFloat("_UseEmissiveIntensity") >= 0.5f && val9.HasProperty("_EmissiveIntensity"))
						{
							val9.SetFloat("_EmissiveIntensity", val9.GetFloat("_EmissiveIntensity") * 4f);
						}
						((Renderer)val8).sharedMaterial = val9;
						boostedLaserBeamStates.Add(new BoostedLaserBeamState(val8, sharedMaterial, val9));
					}
				}
			}
		}
	}

	private static bool ReticleBelongsToEquippedWeapon(HWSReticleBrightness reticle)
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		if (!((Object)(object)reticle == (Object)null))
		{
			Scene scene = ((Component)reticle).gameObject.scene;
			if (((Scene)(ref scene)).isLoaded)
			{
				WeaponV3 componentInParent = ((Component)reticle).GetComponentInParent<WeaponV3>();
				if ((Object)(object)componentInParent != (Object)null)
				{
					scene = ((Component)componentInParent).gameObject.scene;
					if (((Scene)(ref scene)).isLoaded)
					{
						return componentInParent.isEquiped;
					}
				}
				return false;
			}
		}
		return false;
	}

	private void TryEnhanceHwsReticleNormalSize(HWSReticleBrightness owner, Renderer renderer)
	{
		//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Expected O, but got Unknown
		if ((Object)(object)owner == (Object)null || (Object)(object)renderer == (Object)null || enhancedHwsReticleSizeRenderers.Contains(((Object)renderer).GetInstanceID()))
		{
			return;
		}
		Material sharedMaterial = renderer.sharedMaterial;
		if ((Object)(object)sharedMaterial == (Object)null || (Object)(object)sharedMaterial.shader == (Object)null || !string.Equals(((Object)sharedMaterial.shader).name, "Ultimate Scope Shaders/HolographicSight", StringComparison.Ordinal) || !sharedMaterial.HasProperty("_Retical_Size"))
		{
			return;
		}
		float num = sharedMaterial.GetFloat("_Retical_Size");
		if (!(num < 5f) && !(num > 25f))
		{
			float num2 = Mathf.Min(num * 1.5f, 22.56f);
			if (!(num2 <= num + 0.001f))
			{
				Material val = new Material(sharedMaterial)
				{
					name = ((Object)sharedMaterial).name + "_KH_NORMAL_RETICLE_SIZE",
					hideFlags = (HideFlags)52
				};
				val.SetFloat("_Retical_Size", num2);
				renderer.sharedMaterial = val;
				enhancedHwsReticleSizeRenderers.Add(((Object)renderer).GetInstanceID());
				hwsReticleSizeStates.Add(new HwsReticleSizeState(owner, renderer, sharedMaterial, val, num, num2));
			}
		}
	}

	private void RestoreStaleHwsReticleBoosts()
	{
		for (int num = hwsReticleSizeStates.Count - 1; num >= 0; num--)
		{
			HwsReticleSizeState hwsReticleSizeState = hwsReticleSizeStates[num];
			if (!ReticleBelongsToEquippedWeapon(hwsReticleSizeState.Owner))
			{
				RestoreHwsReticleSizeState(hwsReticleSizeState);
				hwsReticleSizeStates.RemoveAt(num);
			}
		}
		for (int num2 = hwsReticleBoostStates.Count - 1; num2 >= 0; num2--)
		{
			HwsReticleBoostState hwsReticleBoostState = hwsReticleBoostStates[num2];
			if (!ReticleBelongsToEquippedWeapon(hwsReticleBoostState.Reticle))
			{
				RestoreHwsReticleBrightnessState(hwsReticleBoostState);
				enhancedHwsReticles.Remove((!((Object)(object)hwsReticleBoostState.Reticle == (Object)null)) ? ((Object)hwsReticleBoostState.Reticle).GetInstanceID() : 0);
				hwsReticleBoostStates.RemoveAt(num2);
			}
		}
	}

	private void RestoreHwsReticleSizeState(HwsReticleSizeState state)
	{
		try
		{
			if ((Object)(object)state.Renderer != (Object)null && (Object)(object)state.Renderer.sharedMaterial == (Object)(object)state.Boosted)
			{
				state.Renderer.sharedMaterial = state.Original;
			}
			if ((Object)(object)state.Boosted != (Object)null)
			{
				Object.Destroy((Object)(object)state.Boosted);
			}
		}
		catch (Exception ex)
		{
			ManualLogSource obj = log;
			if (obj != null)
			{
				obj.LogWarning((object)("Vektor Kill House HWS size restore warning: " + ex.Message));
			}
		}
		enhancedHwsReticleSizeRenderers.Remove((!((Object)(object)state.Renderer == (Object)null)) ? ((Object)state.Renderer).GetInstanceID() : 0);
	}

	private void RestoreHwsReticleBrightnessState(HwsReticleBoostState state)
	{
		try
		{
			HWSReticleBrightness reticle = state.Reticle;
			if ((Object)(object)reticle == (Object)null || reticle.reticleSettings == null)
			{
				return;
			}
			int num = Math.Min(((Il2CppArrayBase<ReticleSetting>)(object)reticle.reticleSettings).Length, state.Normal.Length);
			for (int i = 0; i < num; i++)
			{
				ReticleSetting val = ((Il2CppArrayBase<ReticleSetting>)(object)reticle.reticleSettings)[i];
				if (val != null)
				{
					val.ReticleBrightness = state.Normal[i];
					val.ReticleBrightness_NVG = state.Nvg[i];
				}
			}
			ApplyHwsSelectedBrightness(reticle);
		}
		catch (Exception ex)
		{
			ManualLogSource obj = log;
			if (obj != null)
			{
				obj.LogWarning((object)("Vektor Kill House HWS baseline restore warning: " + ex.Message));
			}
		}
	}

	private static void ApplyHwsSelectedBrightness(HWSReticleBrightness reticle)
	{
		if ((Object)(object)reticle == (Object)null || reticle.reticleSettings == null || ((Il2CppArrayBase<ReticleSetting>)(object)reticle.reticleSettings).Length == 0)
		{
			return;
		}
		int num = Mathf.Clamp(reticle.CurrentBrightnessSetting, 0, ((Il2CppArrayBase<ReticleSetting>)(object)reticle.reticleSettings).Length - 1);
		ReticleSetting val = ((Il2CppArrayBase<ReticleSetting>)(object)reticle.reticleSettings)[num];
		if (val != null)
		{
			Material val2 = (((Object)(object)reticle.ReticleRenderer == (Object)null) ? null : ((Renderer)reticle.ReticleRenderer).sharedMaterial);
			Material val3 = (((Object)(object)reticle.ReticleRendererNVG == (Object)null) ? null : ((Renderer)reticle.ReticleRendererNVG).sharedMaterial);
			if ((Object)(object)val2 != (Object)null && val2.HasProperty("_Reticle_Brightness"))
			{
				val2.SetFloat("_Reticle_Brightness", val.ReticleBrightness);
			}
			if ((Object)(object)val3 != (Object)null && val3.HasProperty("_Reticle_Brightness"))
			{
				val3.SetFloat("_Reticle_Brightness", val.ReticleBrightness_NVG);
			}
		}
	}

	private void RestoreWeaponIlluminationBoosts()
	{
		int count = hwsReticleBoostStates.Count;
		int count2 = hwsReticleSizeStates.Count;
		int count3 = visibleIrLaserBoostStates.Count;
		int count4 = visibleLaserLightBoostStates.Count;
		int count5 = boostedLaserBeamStates.Count;
		foreach (HwsReticleSizeState hwsReticleSizeState in hwsReticleSizeStates)
		{
			RestoreHwsReticleSizeState(hwsReticleSizeState);
		}
		foreach (HwsReticleBoostState hwsReticleBoostState in hwsReticleBoostStates)
		{
			RestoreHwsReticleBrightnessState(hwsReticleBoostState);
		}
		foreach (VisibleIrLaserBoostState visibleIrLaserBoostState in visibleIrLaserBoostStates)
		{
			try
			{
				if ((Object)(object)visibleIrLaserBoostState.Controller != (Object)null)
				{
					visibleIrLaserBoostState.Controller.minBrighness = visibleIrLaserBoostState.MinBrightness;
					visibleIrLaserBoostState.Controller.maxBrighness = visibleIrLaserBoostState.MaxBrightness;
				}
				if ((Object)(object)visibleIrLaserBoostState.LightData != (Object)null)
				{
					visibleIrLaserBoostState.LightData.intensity = visibleIrLaserBoostState.LightDataIntensity;
				}
				if ((Object)(object)visibleIrLaserBoostState.Light != (Object)null)
				{
					visibleIrLaserBoostState.Light.intensity = visibleIrLaserBoostState.LightIntensity;
				}
			}
			catch (Exception ex)
			{
				ManualLogSource obj = log;
				if (obj != null)
				{
					obj.LogWarning((object)("Vektor Kill House visible-laser controller restore warning: " + ex.Message));
				}
			}
		}
		foreach (VisibleLaserLightBoostState visibleLaserLightBoostState in visibleLaserLightBoostStates)
		{
			try
			{
				if ((Object)(object)visibleLaserLightBoostState.LightData != (Object)null)
				{
					visibleLaserLightBoostState.LightData.intensity = visibleLaserLightBoostState.LightDataIntensity;
				}
				if ((Object)(object)visibleLaserLightBoostState.Light != (Object)null)
				{
					visibleLaserLightBoostState.Light.intensity = visibleLaserLightBoostState.LightIntensity;
				}
			}
			catch (Exception ex2)
			{
				ManualLogSource obj2 = log;
				if (obj2 != null)
				{
					obj2.LogWarning((object)("Vektor Kill House visible-laser light restore warning: " + ex2.Message));
				}
			}
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
		if (count + count2 + count3 + count4 + count5 > 0)
		{
			ManualLogSource obj3 = log;
			if (obj3 != null)
			{
				obj3.LogInfo((object)("Vektor Kill House weapon illumination baselines restored: hws=" + count + ", hwsSize=" + count2 + ", visibleLaserControllers=" + count3 + ", visibleLaserLights=" + count4 + ", visibleLaserBeams=" + count5 + "."));
			}
		}
	}

	private void RestoreBoostedLaserBeamMaterials()
	{
		foreach (BoostedLaserBeamState boostedLaserBeamState in boostedLaserBeamStates)
		{
			if ((Object)(object)boostedLaserBeamState.Renderer != (Object)null && (Object)(object)boostedLaserBeamState.Original != (Object)null)
			{
				((Renderer)boostedLaserBeamState.Renderer).sharedMaterial = boostedLaserBeamState.Original;
			}
			if ((Object)(object)boostedLaserBeamState.Boosted != (Object)null)
			{
				Object.Destroy((Object)(object)boostedLaserBeamState.Boosted);
			}
		}
		boostedLaserBeamStates.Clear();
		enhancedVisibleLaserBeams.Clear();
	}

	private static void BoostHdrColor(Material material, string property, float multiplier)
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		if (!((Object)(object)material == (Object)null) && material.HasProperty(property))
		{
			Color color = material.GetColor(property);
			color.r *= multiplier;
			color.g *= multiplier;
			color.b *= multiplier;
			material.SetColor(property, color);
		}
	}

	private static string DescribeEmission(Material material)
	{
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0118: Unknown result type (might be due to invalid IL or missing references)
		//IL_011d: Unknown result type (might be due to invalid IL or missing references)
		//IL_011f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d3: Invalid comparison between Unknown and I4
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d8: Invalid comparison between Unknown and I4
		//IL_020c: Unknown result type (might be due to invalid IL or missing references)
		//IL_020f: Invalid comparison between Unknown and I4
		//IL_0217: Unknown result type (might be due to invalid IL or missing references)
		//IL_021c: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)material == (Object)null)
		{
			return "missing";
		}
		float num = (material.HasProperty("_EmissiveIntensity") ? material.GetFloat("_EmissiveIntensity") : (-1f));
		Color val = (material.HasProperty("_EmissiveColor") ? material.GetColor("_EmissiveColor") : Color.black);
		List<string> list = new List<string>();
		Shader shader = material.shader;
		if ((Object)(object)shader != (Object)null)
		{
			for (int i = 0; i < shader.GetPropertyCount(); i++)
			{
				if (list.Count >= 20)
				{
					break;
				}
				string propertyName = shader.GetPropertyName(i);
				string text = propertyName.ToLowerInvariant();
				if (text.Contains("color") || text.Contains("emiss") || text.Contains("bright") || text.Contains("intens") || text.Contains("tint") || text.Contains("reticle") || text.Contains("tiling") || text.Contains("offset") || text.Contains("scale") || text.Contains("size"))
				{
					ShaderPropertyType propertyType = shader.GetPropertyType(i);
					if ((int)propertyType == 0)
					{
						Color color = material.GetColor(propertyName);
						list.Add(propertyName + "=" + color.r.ToString("F2", CultureInfo.InvariantCulture) + "," + color.g.ToString("F2", CultureInfo.InvariantCulture) + "," + color.b.ToString("F2", CultureInfo.InvariantCulture) + "," + color.a.ToString("F2", CultureInfo.InvariantCulture));
					}
					else if ((int)propertyType == 2 || (int)propertyType == 3)
					{
						list.Add(propertyName + "=" + material.GetFloat(propertyName).ToString("F3", CultureInfo.InvariantCulture));
					}
					else if ((int)propertyType == 1)
					{
						Vector4 vector = material.GetVector(propertyName);
						list.Add(propertyName + "=" + vector.x.ToString("F3", CultureInfo.InvariantCulture) + "," + vector.y.ToString("F3", CultureInfo.InvariantCulture) + "," + vector.z.ToString("F3", CultureInfo.InvariantCulture) + "," + vector.w.ToString("F3", CultureInfo.InvariantCulture));
					}
				}
			}
		}
		return ((Object)material).name + "/intensity=" + num.ToString("F3", CultureInfo.InvariantCulture) + "/rgb=" + val.r.ToString("F3", CultureInfo.InvariantCulture) + "," + val.g.ToString("F3", CultureInfo.InvariantCulture) + "," + val.b.ToString("F3", CultureInfo.InvariantCulture) + "/shader=" + (((Object)(object)shader == (Object)null) ? "missing" : ((Object)shader).name) + "/props=" + string.Join(";", list);
	}

	private static string DescribeHwsReticleSize(Material material)
	{
		if ((Object)(object)material == (Object)null || (Object)(object)material.shader == (Object)null || !string.Equals(((Object)material.shader).name, "Ultimate Scope Shaders/HolographicSight", StringComparison.Ordinal) || !material.HasProperty("_Retical_Size"))
		{
			return "missing";
		}
		return material.GetFloat("_Retical_Size").ToString("F3", CultureInfo.InvariantCulture);
	}

	private void TryCompleteDeferredAiSightAudit()
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		Scene scene = FindLoadedSceneByHandle(pendingSceneHandle);
		if (!IsKillHouseScene(scene))
		{
			aiSightOcclusionPending = false;
			return;
		}
		GameObject val = FindOwnedRoot(scene);
		if ((Object)(object)val == (Object)null)
		{
			return;
		}
		bool deferred;
		bool flag = ValidateAiSightOcclusion(val, out deferred);
		if (!deferred)
		{
			aiSightOcclusionPending = false;
			aiSightOcclusionPassed = flag;
			if (!flag)
			{
				MarkFailure(scene, val, "deferred-ai-sight-occlusion");
			}
			else
			{
				log.LogInfo((object)"Vektor Kill House deferred AI sight-occlusion gate passed against the live EyesAI detection mask.");
			}
		}
	}

	private bool ValidateAiSightOcclusion(GameObject root, out bool deferred)
	{
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_0124: Unknown result type (might be due to invalid IL or missing references)
		//IL_0125: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0133: Unknown result type (might be due to invalid IL or missing references)
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		//IL_0139: Unknown result type (might be due to invalid IL or missing references)
		//IL_013e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0143: Unknown result type (might be due to invalid IL or missing references)
		//IL_0145: Unknown result type (might be due to invalid IL or missing references)
		//IL_0147: Unknown result type (might be due to invalid IL or missing references)
		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
		//IL_014e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		//IL_0157: Unknown result type (might be due to invalid IL or missing references)
		//IL_0159: Unknown result type (might be due to invalid IL or missing references)
		//IL_015b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0160: Unknown result type (might be due to invalid IL or missing references)
		//IL_0162: Unknown result type (might be due to invalid IL or missing references)
		//IL_0164: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_0184: Unknown result type (might be due to invalid IL or missing references)
		//IL_019b: Unknown result type (might be due to invalid IL or missing references)
		//IL_019d: Unknown result type (might be due to invalid IL or missing references)
		//IL_019f: Unknown result type (might be due to invalid IL or missing references)
		deferred = false;
		List<SolidWall> list = CollectSolidWalls(root);
		if (list.Count < 1)
		{
			log.LogError((object)"Vektor Kill House AI sight gate failed: no solid native wall colliders were found.");
			return false;
		}
		int[] array = CollectResidentAiDetectionMasks();
		if (array.Length == 0)
		{
			deferred = true;
			log.LogWarning((object)("Vektor Kill House AI sight gate deferred: OPERATOR has not instantiated or loaded a resident EyesAI profile yet; solidWalls=" + list.Count + "."));
			return false;
		}
		Physics.SyncTransforms();
		int num = 0;
		List<string> list2 = new List<string>();
		int[] array2 = array;
		RaycastHit val6 = default(RaycastHit);
		RaycastHit val7 = default(RaycastHit);
		for (int i = 0; i < array2.Length; i++)
		{
			int num2 = array2[i];
			foreach (SolidWall item in list)
			{
				Collider collider = item.Collider;
				int num3 = 1 << ((Component)collider).gameObject.layer;
				Bounds bounds = collider.bounds;
				bool num4 = ((Bounds)(ref bounds)).size.x <= ((Bounds)(ref bounds)).size.z;
				Vector3 val = (num4 ? Vector3.right : Vector3.forward);
				float num5 = (num4 ? ((Bounds)(ref bounds)).size.x : ((Bounds)(ref bounds)).size.z);
				Vector3 center = ((Bounds)(ref bounds)).center;
				float num6 = Mathf.Max(0.12f, num5 * 0.5f + 0.2f);
				Vector3 val2 = center - val * num6;
				Vector3 val3 = center + val * num6;
				Vector3 val4 = val3 - val2;
				Vector3 normalized = ((Vector3)(ref val4)).normalized;
				Vector3 val5 = -normalized;
				float num7 = Vector3.Distance(val2, val3) + 0.01f;
				bool num8 = (num2 & num3) != 0;
				bool flag = num8 && collider.Raycast(new Ray(val2, normalized), ref val6, num7);
				bool flag2 = num8 && collider.Raycast(new Ray(val3, val5), ref val7, num7);
				num += 2;
				if (!flag || !flag2)
				{
					list2.Add(((Object)item.Root).name + "[layer=" + ((Component)collider).gameObject.layer + ",mask=" + num2 + ",forward=" + flag + ",reverse=" + flag2 + "]");
					if (list2.Count >= 16)
					{
						break;
					}
				}
			}
			if (list2.Count >= 16)
			{
				break;
			}
		}
		bool flag3 = list2.Count == 0;
		if (flag3)
		{
			log.LogInfo((object)("Vektor Kill House AI sight-occlusion gate passed: solidWalls=" + list.Count + ", detectionMasks=" + string.Join("|", array.Select((int mask) => mask + ":" + DescribeLayers(mask))) + ", twoSidedLinecasts=" + num + "."));
		}
		else
		{
			log.LogError((object)("Vektor Kill House AI sight-occlusion gate failed: solidWalls=" + list.Count + ", detectionMasks=" + string.Join("|", array) + ", failures=[" + string.Join(" | ", list2) + "]."));
		}
		return flag3;
	}

	private static List<SolidWall> CollectSolidWalls(GameObject root)
	{
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		Transform[] array = ((IEnumerable<Transform>)root.GetComponentsInChildren<Transform>(true)).Where((Transform item) => ((Object)item).name.StartsWith("NATIVE_RoomWall_", StringComparison.Ordinal) || ((Object)item).name.StartsWith("NATIVE_ConnectorWall_", StringComparison.Ordinal) || ((Object)item).name.StartsWith("NATIVE_InteriorSplitWall_", StringComparison.Ordinal)).ToArray();
		List<SolidWall> list = new List<SolidWall>();
		HashSet<int> hashSet = new HashSet<int>();
		Transform[] array2 = array;
		foreach (Transform val in array2)
		{
			foreach (Collider componentsInChild in ((Component)val).GetComponentsInChildren<Collider>(true))
			{
				if (!((Object)(object)componentsInChild == (Object)null) && componentsInChild.enabled && !componentsInChild.isTrigger && hashSet.Add(((Object)componentsInChild).GetInstanceID()))
				{
					Bounds bounds = componentsInChild.bounds;
					if (!(((Bounds)(ref bounds)).size.y < 1.5f) && !(Mathf.Min(((Bounds)(ref bounds)).size.x, ((Bounds)(ref bounds)).size.z) <= 0.001f))
					{
						list.Add(new SolidWall(val, componentsInChild));
					}
				}
			}
		}
		return list;
	}

	private static int[] CollectResidentAiDetectionMasks()
	{
		HashSet<int> masks = new HashSet<int>();
		GameManager instance = GameManager.instance;
		if ((Object)(object)instance != (Object)null)
		{
			if (instance.AllAITypes != null)
			{
				for (int i = 0; i < instance.AllAITypes.Count; i++)
				{
					GameObject val = instance.AllAITypes[i];
					if ((Object)(object)val == (Object)null)
					{
						continue;
					}
					foreach (EyesAI componentsInChild in val.GetComponentsInChildren<EyesAI>(true))
					{
						Add(componentsInChild);
					}
				}
			}
			if (instance.allAI != null)
			{
				for (int j = 0; j < instance.allAI.Count; j++)
				{
					BrainAI val2 = instance.allAI[j];
					if ((Object)(object)val2 != (Object)null)
					{
						Add(val2.eyesAI);
					}
				}
			}
		}
		foreach (Object item in (Il2CppArrayBase<Object>)(object)Resources.FindObjectsOfTypeAll(Il2CppType.Of<EyesAI>()))
		{
			Add((item == (Object)null) ? null : ((Il2CppObjectBase)item).TryCast<EyesAI>());
		}
		return masks.OrderBy((int value) => value).ToArray();
		void Add(EyesAI eyes)
		{
			//IL_000b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0010: Unknown result type (might be due to invalid IL or missing references)
			if (!((Object)(object)eyes == (Object)null))
			{
				LayerMask detectionLayerMask = eyes.DetectionLayerMask;
				int value = ((LayerMask)(ref detectionLayerMask)).value;
				if (value != 0)
				{
					masks.Add(value);
				}
			}
		}
	}

	private static string DescribeLayers(int mask)
	{
		List<string> list = new List<string>();
		for (int i = 0; i < 32; i++)
		{
			if ((mask & (1 << i)) != 0)
			{
				string text = LayerMask.LayerToName(i);
				list.Add(string.IsNullOrEmpty(text) ? i.ToString(CultureInfo.InvariantCulture) : text);
			}
		}
		return string.Join(",", list);
	}

	private bool EnsureRuntimeNavigationGraph(GameObject root)
	{
		//IL_041c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0426: Expected O, but got Unknown
		//IL_0327: Unknown result type (might be due to invalid IL or missing references)
		//IL_032c: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_03cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_03d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_03d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_0343: Unknown result type (might be due to invalid IL or missing references)
		//IL_0348: Unknown result type (might be due to invalid IL or missing references)
		//IL_035f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0364: Unknown result type (might be due to invalid IL or missing references)
		//IL_0368: Unknown result type (might be due to invalid IL or missing references)
		//IL_036e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0373: Unknown result type (might be due to invalid IL or missing references)
		//IL_0377: Unknown result type (might be due to invalid IL or missing references)
		//IL_0559: Unknown result type (might be due to invalid IL or missing references)
		//IL_055e: Unknown result type (might be due to invalid IL or missing references)
		//IL_057e: Unknown result type (might be due to invalid IL or missing references)
		//IL_05a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_05c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0640: Unknown result type (might be due to invalid IL or missing references)
		//IL_0645: Unknown result type (might be due to invalid IL or missing references)
		//IL_0649: Unknown result type (might be due to invalid IL or missing references)
		//IL_06a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_06b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_06bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_06c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_06d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0814: Unknown result type (might be due to invalid IL or missing references)
		//IL_082a: Unknown result type (might be due to invalid IL or missing references)
		Transform[] array = ((IEnumerable<Transform>)root.GetComponentsInChildren<Transform>(true)).Where((Transform item) => string.Equals(((Object)item).name, "NATIVE_Floor", StringComparison.Ordinal)).ToArray();
		Transform[] array2 = ((IEnumerable<Transform>)root.GetComponentsInChildren<Transform>(true)).Where((Transform item) => ((Object)item).name.StartsWith("NATIVE_ConnectorFloor_", StringComparison.Ordinal)).ToArray();
		Transform[] array3 = array.Concat(array2).ToArray();
		Transform[] warehouseAprons = ((IEnumerable<Transform>)root.GetComponentsInChildren<Transform>(true)).Where((Transform item) => string.Equals(((Object)item).name, "NATIVE_WarehouseGroundApron", StringComparison.Ordinal)).ToArray();
		int num = ((IEnumerable<Transform>)root.GetComponentsInChildren<Transform>(true)).Count((Transform item) => string.Equals(((Object)item).name, "WAREHOUSE_APRON_NAV_EXCLUDED_ENCLOSED_PERIMETER", StringComparison.Ordinal));
		bool flag = warehouseAprons.Length == 1 && num == 1 && array3.All((Transform floor) => (Object)(object)floor != (Object)(object)warehouseAprons[0]);
		if (!flag)
		{
			log.LogError((object)("Vektor Kill House navigation rejected the warehouse apron policy: aprons=" + warehouseAprons.Length + ", exclusionMarkers=" + num + ", includedInGridSources=" + (warehouseAprons.Length == 1 && array3.Any((Transform floor) => (Object)(object)floor == (Object)(object)warehouseAprons[0])) + "."));
			return false;
		}
		List<Collider> list = new List<Collider>();
		HashSet<int> hashSet = new HashSet<int>();
		int num2 = 0;
		Transform[] array4 = array3;
		foreach (Transform obj in array4)
		{
			bool flag2 = false;
			foreach (Collider componentsInChild in ((Component)obj).GetComponentsInChildren<Collider>(true))
			{
				if ((Object)(object)componentsInChild != (Object)null && hashSet.Add(((Object)componentsInChild).GetInstanceID()))
				{
					list.Add(componentsInChild);
					flag2 = true;
				}
			}
			if (!flag2)
			{
				num2++;
			}
		}
		Collider[] array5 = list.ToArray();
		if (array.Length < 19 || array.Length > 21 || array2.Length < 1 || array2.Length > 32 || num2 != 0 || array5.Length < array3.Length)
		{
			log.LogError((object)("Vektor Kill House navigation gate failed: native floor collider closure is invalid; floors=" + array3.Length + ", roomFloors=" + array.Length + ", connectorFloors=" + array2.Length + ", floorsWithoutCollider=" + num2 + ", floorColliders=" + array5.Length + "."));
			return false;
		}
		try
		{
			AstarPath.FindAstarPath();
			AstarPath astar = AstarPath.active;
			Scene scene;
			if ((Object)(object)astar != (Object)null)
			{
				if (!((Object)(object)((Component)astar).gameObject == (Object)null))
				{
					scene = ((Component)astar).gameObject.scene;
					if (((Scene)(ref scene)).IsValid())
					{
						scene = ((Component)astar).gameObject.scene;
						if (((Scene)(ref scene)).isLoaded)
						{
							scene = ((Component)astar).gameObject.scene;
							SceneHandle handle = ((Scene)(ref scene)).handle;
							scene = root.scene;
							if (!(handle != ((Scene)(ref scene)).handle))
							{
								goto IL_0407;
							}
						}
					}
				}
				ManualLogSource obj2 = log;
				object obj3;
				if (!((Object)(object)((Component)astar).gameObject == (Object)null))
				{
					string name = ((Object)((Component)astar).gameObject).name;
					scene = ((Component)astar).gameObject.scene;
					obj3 = name + "@" + ((object)((Scene)(ref scene)).handle/*cast due to constrained. prefix*/).ToString();
				}
				else
				{
					obj3 = "<missing-host>";
				}
				obj2.LogError((object)("Vektor Kill House navigation rejected an AstarPath owned by another loaded scene: " + (string?)obj3 + "."));
				return false;
			}
			goto IL_0407;
			IL_0407:
			if ((Object)(object)astar == (Object)null)
			{
				runtimeAstarHost = new GameObject("MOD_VektorKillHouse_AstarPath");
				runtimeAstarHost.transform.SetParent(root.transform, false);
				astar = runtimeAstarHost.AddComponent<AstarPath>();
				runtimeOwnsAstarHost = (Object)(object)astar != (Object)null;
			}
			else
			{
				runtimeOwnsAstarHost = false;
			}
			if ((Object)(object)astar == (Object)null || astar.data == null)
			{
				return false;
			}
			GameObject gameObject = ((Component)astar).gameObject;
			runtimeRvoSimulator = (((Object)(object)gameObject == (Object)null) ? null : gameObject.GetComponent<RVOSimulator>());
			runtimeOwnsRvoSimulator = false;
			if ((Object)(object)runtimeRvoSimulator == (Object)null && (Object)(object)gameObject != (Object)null)
			{
				runtimeRvoSimulator = gameObject.AddComponent<RVOSimulator>();
				runtimeOwnsRvoSimulator = (Object)(object)runtimeRvoSimulator != (Object)null;
			}
			if ((Object)(object)runtimeRvoSimulator == (Object)null || (Object)(object)((Component)runtimeRvoSimulator).gameObject != (Object)(object)gameObject || !((Behaviour)runtimeRvoSimulator).isActiveAndEnabled)
			{
				return false;
			}
			ReleaseRuntimeNavigationGraph(((Object)(object)runtimeNavigationAstar != (Object)null) ? runtimeNavigationAstar : astar, "graph rebuild");
			Bounds bounds = array5[0].bounds;
			foreach (Collider item in array5.Skip(1))
			{
				((Bounds)(ref bounds)).Encapsulate(item.bounds);
			}
			int num4 = Mathf.CeilToInt((((Bounds)(ref bounds)).size.x + 2f) / 0.4f);
			int num5 = Mathf.CeilToInt((((Bounds)(ref bounds)).size.z + 2f) / 0.4f);
			NavGraph val = astar.data.AddGraph(Il2CppType.Of<GridGraph>());
			GridGraph val2 = ((val == null) ? null : ((Il2CppObjectBase)val).TryCast<GridGraph>());
			if (val2 == null)
			{
				if (val != null)
				{
					astar.data.RemoveGraph(val);
				}
				return false;
			}
			runtimeNavigationGraph = val2;
			runtimeNavigationAstar = astar;
			scene = root.scene;
			runtimeNavigationOwnerSceneHandle = SceneHandle.op_Implicit(((Scene)(ref scene)).handle);
			runtimeNavigationAstarHostInstanceId = ((!((Object)(object)gameObject == (Object)null)) ? ((Object)gameObject).GetInstanceID() : 0);
			runtimeNavigationRvoInstanceId = ((!((Object)(object)runtimeRvoSimulator == (Object)null)) ? ((Object)runtimeRvoSimulator).GetInstanceID() : 0);
			((NavGraph)val2).name = "MOD_VektorKillHouse_RuntimeNavigation";
			val2.center = new Vector3(((Bounds)(ref bounds)).center.x, ((Bounds)(ref bounds)).min.y, ((Bounds)(ref bounds)).center.z);
			val2.rotation = Vector3.zero;
			val2.aspectRatio = 1f;
			val2.isometricAngle = 0f;
			val2.SetDimensions(num4, num5, 0.4f);
			val2.maxSlope = 32f;
			val2.maxStepHeight = 0.55f;
			val2.erodeIterations = 1;
			val2.neighbours = (NumNeighbours)1;
			val2.cutCorners = false;
			val2.collision.use2D = false;
			val2.collision.heightCheck = true;
			val2.collision.unwalkableWhenNoGround = true;
			val2.collision.fromHeight = 5f;
			val2.collision.thickRaycast = false;
			val2.collision.collisionCheck = true;
			val2.collision.diameter = 0.55f;
			val2.collision.height = 1.7f;
			val2.collision.collisionOffset = 0.85f;
			Dictionary<GameObject, int> dictionary = new Dictionary<GameObject, int>();
			Collider[] array6 = array5;
			for (int num3 = 0; num3 < array6.Length; num3++)
			{
				GameObject gameObject2 = ((Component)array6[num3]).gameObject;
				if ((Object)(object)gameObject2 != (Object)null && !dictionary.ContainsKey(gameObject2))
				{
					dictionary.Add(gameObject2, gameObject2.layer);
				}
			}
			val2.collision.heightMask = LayerMask.op_Implicit(1073741824);
			val2.collision.mask = LayerMask.op_Implicit(-1073741825);
			try
			{
				foreach (GameObject key in dictionary.Keys)
				{
					key.layer = 30;
				}
				Physics.SyncTransforms();
				astar.Scan((NavGraph)(object)val2);
			}
			finally
			{
				foreach (KeyValuePair<GameObject, int> item2 in dictionary)
				{
					if ((Object)(object)item2.Key != (Object)null)
					{
						item2.Key.layer = item2.Value;
					}
				}
				Physics.SyncTransforms();
			}
			Transform[] array7 = ((IEnumerable<Transform>)root.GetComponentsInChildren<Transform>(true)).Where((Transform item) => ((Object)item).name.StartsWith("PVE_EnemySpawn_", StringComparison.Ordinal)).ToArray();
			int num6 = SnapMarkersToGrid(astar, array7, move: true);
			Transform[] array8 = ((IEnumerable<Transform>)root.GetComponentsInChildren<Transform>(true)).Where((Transform item) => ((Object)item).name.StartsWith("PVE_PlayerSpawn_", StringComparison.Ordinal)).ToArray();
			int num7 = SnapMarkersToGrid(astar, array8, move: true);
			int num8 = Enumerable.Count(array7, (Transform marker) => astar.IsPointOnNavmesh(marker.position));
			int num9 = (array.Length - 1) * 2;
			bool result = array7.Length == num9 && num6 == num9 && num8 == num9 && array8.Length == 4 && num7 == 4;
			log.LogInfo((object)("Vektor Kill House navigation scan: passed=" + result + ", graph=" + ((NavGraph)val2).name + ", nodes=" + num4 + "x" + num5 + ", nodeSize=" + 0.4f.ToString("F2", CultureInfo.InvariantCulture) + ", roomFloors=" + array.Length + ", connectorFloors=" + array2.Length + ", warehouseApronNavExcluded=" + flag + ", floorColliders=" + array5.Length + ", enemyMarkers=" + num8 + "/" + array7.Length + ", playerMarkers=" + num7 + "/" + array8.Length + "."));
			return result;
		}
		catch (Exception ex)
		{
			log.LogError((object)("Vektor Kill House navigation graph creation failed: " + ex.GetType().Name + ": " + ex.Message));
			ReleaseRuntimeNavigation("failed graph creation");
			return false;
		}
	}

	private static int SnapMarkersToGrid(AstarPath astar, IEnumerable<Transform> markers, bool move)
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		int num = 0;
		foreach (Transform marker in markers)
		{
			NNInfo nearest = astar.GetNearest(marker.position, NNConstraint.Walkable);
			if (nearest.node == null)
			{
				continue;
			}
			Vector3 val = (Vector3)nearest.node.position;
			if (!(Vector2.Distance(new Vector2(marker.position.x, marker.position.z), new Vector2(val.x, val.z)) > 1.35f))
			{
				if (move)
				{
					marker.position = val + Vector3.up * 0.03f;
				}
				num++;
			}
		}
		Physics.SyncTransforms();
		return num;
	}

	private void ArmNavigationTeardownAudit(int unloadedSceneHandle)
	{
		navigationTeardownSceneHandle = unloadedSceneHandle;
		navigationTeardownAstarHostInstanceId = runtimeNavigationAstarHostInstanceId;
		navigationTeardownRvoInstanceId = runtimeNavigationRvoInstanceId;
		navigationTeardownOwnedAstarHost = runtimeOwnsAstarHost;
		navigationTeardownOwnedRvoSimulator = runtimeOwnsRvoSimulator;
		navigationTeardownHadRuntimeGraph = runtimeNavigationGraph != null;
		pendingNavigationTeardownAuditFrame = Time.frameCount + 2;
	}

	private void CompleteNavigationTeardownAudit()
	{
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_0202: Unknown result type (might be due to invalid IL or missing references)
		//IL_0207: Unknown result type (might be due to invalid IL or missing references)
		//IL_0226: Unknown result type (might be due to invalid IL or missing references)
		//IL_022c: Unknown result type (might be due to invalid IL or missing references)
		int num = navigationTeardownSceneHandle;
		int num2 = navigationTeardownAstarHostInstanceId;
		int num3 = navigationTeardownRvoInstanceId;
		bool flag = navigationTeardownOwnedAstarHost;
		bool flag2 = navigationTeardownOwnedRvoSimulator;
		bool flag3 = navigationTeardownHadRuntimeGraph;
		pendingNavigationTeardownAuditFrame = -1;
		navigationTeardownSceneHandle = 0;
		navigationTeardownAstarHostInstanceId = 0;
		navigationTeardownRvoInstanceId = 0;
		navigationTeardownOwnedAstarHost = false;
		navigationTeardownOwnedRvoSimulator = false;
		navigationTeardownHadRuntimeGraph = false;
		int num4 = 0;
		int num5 = 0;
		int num6 = 0;
		try
		{
			foreach (Object item in (Il2CppArrayBase<Object>)(object)Resources.FindObjectsOfTypeAll(Il2CppType.Of<AstarPath>()))
			{
				AstarPath val = ((item == (Object)null) ? null : ((Il2CppObjectBase)item).TryCast<AstarPath>());
				if ((Object)(object)val == (Object)null || (Object)(object)((Component)val).gameObject == (Object)null)
				{
					continue;
				}
				Scene scene = ((Component)val).gameObject.scene;
				bool num7 = num2 != 0 && ((Object)((Component)val).gameObject).GetInstanceID() == num2;
				bool flag4 = ((Scene)(ref scene)).IsValid() && ((Scene)(ref scene)).handle == SceneHandle.op_Implicit(num) && string.Equals(((Object)((Component)val).gameObject).name, "MOD_VektorKillHouse_AstarPath", StringComparison.Ordinal);
				if (!num7 && !flag4)
				{
					continue;
				}
				if (flag)
				{
					num4++;
				}
				if (!flag3 || val.data == null || val.data.graphs == null)
				{
					continue;
				}
				foreach (NavGraph item2 in (Il2CppArrayBase<NavGraph>)(object)val.data.graphs)
				{
					if (item2 != null && string.Equals(item2.name, "MOD_VektorKillHouse_RuntimeNavigation", StringComparison.Ordinal))
					{
						num6++;
					}
				}
			}
			foreach (Object item3 in (Il2CppArrayBase<Object>)(object)Resources.FindObjectsOfTypeAll(Il2CppType.Of<RVOSimulator>()))
			{
				RVOSimulator val2 = ((item3 == (Object)null) ? null : ((Il2CppObjectBase)item3).TryCast<RVOSimulator>());
				if (!((Object)(object)val2 == (Object)null) && !((Object)(object)((Component)val2).gameObject == (Object)null))
				{
					Scene scene2 = ((Component)val2).gameObject.scene;
					bool flag5 = num3 != 0 && ((Object)val2).GetInstanceID() == num3;
					bool flag6 = ((Scene)(ref scene2)).IsValid() && ((Scene)(ref scene2)).handle == SceneHandle.op_Implicit(num) && string.Equals(((Object)((Component)val2).gameObject).name, "MOD_VektorKillHouse_AstarPath", StringComparison.Ordinal);
					if (flag2 && (flag5 || flag6))
					{
						num5++;
					}
				}
			}
			bool flag7 = num4 == 0 && num5 == 0 && num6 == 0;
			string text = "Vektor Kill House post-unload navigation teardown gate: passed=" + flag7 + ", unloadedSceneHandle=" + num + ", ownedAstarHosts=" + num4 + ", runtimeGraphs=" + num6 + ", ownedRvoSimulators=" + num5 + ".";
			if (flag7)
			{
				log.LogInfo((object)text);
			}
			else
			{
				log.LogError((object)text);
			}
		}
		catch (Exception ex)
		{
			log.LogError((object)("Vektor Kill House post-unload navigation teardown gate failed: " + ex.GetType().Name + ": " + ex.Message + "."));
		}
	}

	private void ReleaseRuntimeNavigationGraph(AstarPath astar, string reason)
	{
		if (runtimeNavigationGraph != null)
		{
			try
			{
				bool flag = (Object)(object)astar != (Object)null && astar.data != null && astar.data.RemoveGraph((NavGraph)(object)runtimeNavigationGraph);
				log.LogInfo((object)("Vektor Kill House navigation graph released: reason=" + reason + ", removed=" + flag + "."));
			}
			catch (Exception ex)
			{
				log.LogWarning((object)("Vektor Kill House navigation graph release warning: " + ex.Message));
			}
			runtimeNavigationGraph = null;
		}
	}

	private void ReleaseRuntimeNavigation(string reason)
	{
		try
		{
			ReleaseRuntimeNavigationGraph(runtimeNavigationAstar, reason);
		}
		catch (Exception ex)
		{
			ManualLogSource obj = log;
			if (obj != null)
			{
				obj.LogWarning((object)("Vektor Kill House navigation release warning: " + ex.Message));
			}
			runtimeNavigationGraph = null;
		}
		if (runtimeOwnsRvoSimulator && (Object)(object)runtimeRvoSimulator != (Object)null && ((Object)(object)runtimeAstarHost == (Object)null || (Object)(object)((Component)runtimeRvoSimulator).gameObject != (Object)(object)runtimeAstarHost))
		{
			Object.Destroy((Object)(object)runtimeRvoSimulator);
		}
		if (runtimeOwnsAstarHost && (Object)(object)runtimeAstarHost != (Object)null)
		{
			Object.Destroy((Object)(object)runtimeAstarHost);
		}
		runtimeAstarHost = null;
		runtimeRvoSimulator = null;
		runtimeNavigationAstar = null;
		runtimeOwnsAstarHost = false;
		runtimeOwnsRvoSimulator = false;
		runtimeNavigationOwnerSceneHandle = 0;
		runtimeNavigationAstarHostInstanceId = 0;
		runtimeNavigationRvoInstanceId = 0;
	}

	private void ReleaseOwnedMaterials()
	{
		RestoreBoostedLaserBeamMaterials();
		foreach (Material value in runtimeMaterialsBySourceInstance.Values)
		{
			if ((Object)(object)value != (Object)null)
			{
				Object.Destroy((Object)(object)value);
			}
		}
		runtimeMaterialsBySourceInstance.Clear();
		ownedRuntimeMaterialIds.Clear();
	}

	private void MarkFailure(Scene scene, GameObject root, string reason)
	{
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)root != (Object)null && (Object)(object)root.transform.Find("RUNTIME_VEKTOR_KILLHOUSE_GATE_FAILED") == (Object)null)
		{
			new GameObject("RUNTIME_VEKTOR_KILLHOUSE_GATE_FAILED").transform.SetParent(root.transform, false);
		}
		if ((Object)(object)root != (Object)null && (Object)(object)root.transform.Find("MODDED_OPERATIONS_RUNTIME_CONTRACT_FAILED") == (Object)null)
		{
			new GameObject("MODDED_OPERATIONS_RUNTIME_CONTRACT_FAILED").transform.SetParent(root.transform, false);
		}
		log.LogError((object)("Vektor Kill House runtime gate failed closed: scene=" + ((Scene)(ref scene)).name + ", reason=" + reason + "."));
		if ((Object)(object)root != (Object)null)
		{
			root.SetActive(false);
		}
		RestoreWarehouseOnlyLighting();
		RestoreWeaponIlluminationBoosts();
		RestoreGlobalFlashlightMultiplier();
	}

	private static bool IsKillHouseScene(Scene scene)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		if (!((Scene)(ref scene)).IsValid() || !((Scene)(ref scene)).isLoaded)
		{
			return false;
		}
		if (!string.IsNullOrEmpty(((Scene)(ref scene)).path) && ScenePaths.Contains(((Scene)(ref scene)).path))
		{
			return true;
		}
		return ScenePaths.Any((string path) => string.Equals(Path.GetFileNameWithoutExtension(path), ((Scene)(ref scene)).name, StringComparison.OrdinalIgnoreCase));
	}

	private static GameObject FindOwnedRoot(Scene scene)
	{
		GameObject[] array = ((IEnumerable<GameObject>)((Scene)(ref scene)).GetRootGameObjects()).Where((GameObject root) => (Object)(object)root != (Object)null && ((IEnumerable<Transform>)root.GetComponentsInChildren<Transform>(true)).Any((Transform item) => string.Equals(((Object)item).name, "MAP_ID_community.vektor-modular-killhouse.modular-killhouse", StringComparison.Ordinal))).ToArray();
		if (array.Length != 1)
		{
			return null;
		}
		return array[0];
	}

	private static Scene FindLoadedSceneByHandle(int handle)
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		for (int i = 0; i < SceneManager.sceneCount; i++)
		{
			Scene sceneAt = SceneManager.GetSceneAt(i);
			if (((Scene)(ref sceneAt)).handle == SceneHandle.op_Implicit(handle))
			{
				return sceneAt;
			}
		}
		return default(Scene);
	}

	private static NativeMaterialProfile P(float baseValue, float metallic, float smoothness, float normalScale, bool hasNormal = true, bool hasMask = true, float emissiveIntensity = 0f, bool hasEmissiveMap = false, bool matteArchitectural = false, string residentShaderName = "HDRP/Lit", float baseGreen = -1f, float baseBlue = -1f)
	{
		return new NativeMaterialProfile(baseValue, metallic, smoothness, normalScale, hasNormal, hasMask, emissiveIntensity, hasEmissiveMap, matteArchitectural, residentShaderName, baseGreen, baseBlue);
	}

	private static FurnitureSurfaceProfile Fsp(float metallicRemapMax, float smoothnessRemapMax, float aoRemapMin, float aoRemapMax, float occlusionStrength, float receivesSsr = 1f)
	{
		return new FurnitureSurfaceProfile(metallicRemapMax, smoothnessRemapMax, aoRemapMin, aoRemapMax, occlusionStrength, receivesSsr);
	}
}
