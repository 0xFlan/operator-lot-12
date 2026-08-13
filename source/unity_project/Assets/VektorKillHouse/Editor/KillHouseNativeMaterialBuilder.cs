#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

public static class KillHouseNativeMaterialBuilder
{
    public const string OutputFolder = "Assets/VektorKillHouse/Native/Materials";
    // Preserve the vanilla mesh, mask, HDRP mode/unit, and exposure behavior, while raising only
    // the visible tube surface 2.2630344058 stops for the 6.8 m warehouse mounting height.
    public const float KillHouseFluorescentLitEmission = 307.2f;
    public const float KillHouseFluorescentDimEmission = 9.6f;
    public const float KillHouseFluorescentExposureWeight = 0f;
    public const float KillHouseFluorescentIntensityUnit = 1f;

    private sealed class Definition
    {
        public readonly string Name;
        public readonly string Base;
        public readonly string Normal;
        public readonly string Mask;
        public readonly string Emissive;
        public readonly string Detail;
        public readonly string BaseSourceTexEnv;

        public Definition(string name, string baseMap, string normalMap = null, string maskMap = null,
            string emissive = null, string detail = null, string baseSourceTexEnv = "_BaseColorMap")
        {
            Name = name;
            Base = baseMap;
            Normal = normalMap;
            Mask = maskMap;
            Emissive = emissive;
            Detail = detail;
            BaseSourceTexEnv = baseSourceTexEnv;
        }
    }

    private static readonly Definition[] Definitions =
    {
        new Definition("ChipBoardShader", "Chipboard_D", "Chipboard_N", "ChipBoardShader_MaskMap"),
        new Definition("PlyWoodShader", "Plywood_D", "Plywood_N", "PlyWoodShader_MaskMap"),
        new Definition("Bed", "Bed_BaseColor", "Bed_Normal", "Bed_MaskMap"),
        new Definition("PillowLarge", "PillowLarge_BaseColor", "PillowLarge_Normal", "PillowLarge_MaskMap"),
        new Definition("PillowSmall", "PillowSmall_BaseColor", "PillowSmall_Normal", "PillowSmall_MaskMap"),
        new Definition("Bedroom_Closets", "Bedroom_Closets_BaseColor", "Bedroom_Closets_Normal", "Bedroom_Closets_MaskMap"),
        new Definition("Books", "Books_BaseColor", "Books_Normal", "Books_MaskMap"),
        new Definition("Carpet_B", "Carpet_B_BaseColor", "Carpet_A_Normal", "Carpet_A_MaskMap"),
        new Definition("Couch_Fabric", "Couch_Fabric_BaseColor", "Couch_Fabric_Normal", "Couch_Fabric_MaskMap"),
        new Definition("Devices_On", "Devices_BaseColor", "Devices_Normal", "Devices_MaskMap", "Devices_Emissive"),
        new Definition("Door_Breached", "breached_low_Door_Breached_BaseMap", "breached_low_Door_Breached_Normal", "breached_low_Door_Breached_MaskMap"),
        new Definition("Door_White", "Door_White_BaseColor", "Door_White_Normal", "Door_White_MaskMap"),
        new Definition("MI_DoorsWindows", "T_Doors_Windows_BC", "T_Doors_Windows_N_ConvertedOglNormal", "T_Doors_Windows_ORM_ConvertedMask"),
        new Definition("Fireplace", "Fireplace_BaseColor", "Fireplace_Normal", "Fireplace_MaskMap"),
        new Definition("In_Floor_Basement", "Floor_Basement_BaseColor", "Floor_Basement_Normal",
            "Floor_Basement_MaskMap", detail: "Concrete1_DetailMap"),
        new Definition("In_Floor_Carpet", "In_Floorcarpet_BaseColor", "In_Floorcarpet_Normal"),
        new Definition("Kitchen_Cabinet_Marble", "Kitchen_Cabinet_Marble_BaseColor", null, "Kitchen_Cabinet_Marble_MaskMap"),
        new Definition("Kitchen_Cabinet_Wood", "Kitchen_Cabinet_Wood_BaseColor", "Kitchen_Cabinet_Wood_Normal", "Kitchen_Cabinet_Wood_MaskMap"),
        new Definition("Kitchen_TableChair", "Kitchen_TableChair_BaseColor", "Kitchen_TableChair_Normal", "Kitchen_TableChair_MaskMap"),
        new Definition("Lamps_House_Off", "Lamps_House_BaseColor", "Lamps_House_Normal", "Lamps_House_MaskMap", "Lamps_House_Emissive"),
        new Definition("Lamps_C_on _cagville", "Lamps_C_BaseColor", "Lamps_C_Normal", "Lamps_C_MaskMap", "Lamps_C_Emissive"),
        new Definition("Corrugated Metal Sheet_vb1lafx", "Albedo_4K__vb1lafx", "Normal_4K_LOD0_vb1lafx"),
        // Exact PVP Woods Warehouse donor. Its ShaderGraph combines these same vanilla maps; HDRP/Lit
        // preserves the donor's base/mask tiling without projecting a corrugated pattern onto alien UVs.
        new Definition("RM Steel smooth", "RM steel oxidized distant D", null, "RM steel oxidized G",
            detail: null, baseSourceTexEnv: "_3_DIffuse_map"),
        new Definition("Floor", "Albedo_4K__wckscdz", "Normal_4K__wckscdz", "Masks_4K__wckscdz"),
        new Definition("Sofa_House", "Sofa_House_BaseColor", "Sofa_House_Normal", "Sofa_House_MaskMap"),
        new Definition("Toilet_House", "Toilet_House_BaseColor", "Toilet_House_Normal", "Toilet_House_MaskMap"),
        new Definition("WorkDesk", "WorkDesk_BaseColor", "WorkDesk_Normal", "WorkDesk_MaskMap")
    };

    private static readonly HashSet<string> FurnitureMaterialNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Bed", "PillowLarge", "PillowSmall", "Bedroom_Closets", "Books", "Couch_Fabric", "Devices_On", "Fireplace",
        "Kitchen_Cabinet_Marble", "Kitchen_Cabinet_Wood", "Kitchen_TableChair",
        "Sofa_House", "Toilet_House", "WorkDesk"
    };
    private static readonly string[] ExactDonorShaderPasses =
    {
        "DistortionVectors", "MOTIONVECTORS", "TransparentDepthPrepass",
        "TransparentDepthPostpass", "TransparentBackface", "RayTracingPrepass"
    };
    private static readonly string[] ExactDonorOverrideTags = { "MotionVector" };

    [MenuItem("Vektor Kill House/Native/Rebuild Native Materials", priority = 11)]
    public static void BuildAll()
    {
        EnsureFolder(OutputFolder);
        ConfigureTextureImporters();
        Shader shader = Shader.Find("HDRP/Lit");
        if (shader == null)
            throw new InvalidOperationException("HDRP/Lit is unavailable. Resolve the Unity 6000.3 HDRP package before building native materials.");

        foreach (Definition definition in Definitions)
        {
            string path = MaterialPath(definition.Name);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = "MAT_NATIVE_" + Sanitize(definition.Name) };
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            ApplySourceValues(material, definition.Name);
            SetTexture(material, "_BaseColorMap", FindTexture(definition.Base));
            SetTexture(material, "_BaseMap", FindTexture(definition.Base));
            SetTexture(material, "_MainTex", FindTexture(definition.Base));
            ApplyTextureTransform(material, definition.Name, definition.BaseSourceTexEnv,
                "_BaseColorMap", "_BaseMap", "_MainTex");
            if (!string.IsNullOrEmpty(definition.Normal))
            {
                SetTexture(material, "_NormalMap", FindTexture(definition.Normal));
                SetTexture(material, "_BumpMap", FindTexture(definition.Normal));
                ApplyTextureTransform(material, definition.Name, "_NormalMap", "_NormalMap", "_BumpMap");
                material.EnableKeyword("_NORMALMAP");
                material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
            if (!string.IsNullOrEmpty(definition.Mask))
            {
                SetTexture(material, "_MaskMap", FindTexture(definition.Mask));
                material.EnableKeyword("_MASKMAP");
                ApplyTextureTransform(material, definition.Name, "_MaskMap", "_MaskMap");
            }
            if (!string.IsNullOrEmpty(definition.Detail))
            {
                SetTexture(material, "_DetailMap", FindTexture(definition.Detail));
                material.EnableKeyword("_DETAIL_MAP");
                ApplyTextureTransform(material, definition.Name, "_DetailMap", "_DetailMap");
            }
            if (!string.IsNullOrEmpty(definition.Emissive))
            {
                Texture2D emissive = FindTexture(definition.Emissive);
                SetTexture(material, "_EmissiveColorMap", emissive);
                SetTexture(material, "_EmissionMap", emissive);
                ApplyTextureTransform(material, definition.Name, "_EmissiveColorMap",
                    "_EmissiveColorMap", "_EmissionMap");
                Color emissiveColor = ReadSourceColor(definition.Name, "_EmissiveColor", Color.black);
                SetColor(material, "_EmissiveColor", emissiveColor);
                SetColor(material, "_EmissionColor", emissiveColor);
                if (emissiveColor.maxColorComponent > .001f)
                {
                    material.EnableKeyword("_EMISSIVE_COLOR_MAP");
                    material.EnableKeyword("_EMISSION");
                }
                else
                {
                    material.DisableKeyword("_EMISSIVE_COLOR_MAP");
                    material.DisableKeyword("_EMISSION");
                }
            }
            ApplyKillHouseUseProfile(material, definition.Name);
            // Instancing, GI flags, double-sided GI, shader-pass state, and override tags were
            // copied from the pinned vanilla record in ApplySourceValues. Do not normalize them.
            // Every audited donor material in this closed native family uses custom queue 2225.
            // HDMaterial.ValidateMaterial preserves/reconstructs that queue from its HDRP state.
            material.renderQueue = 2225;
            ValidateHdrpMaterial(material);
            if (FurnitureMaterialNames.Contains(definition.Name) ||
                string.Equals(definition.Name, "Floor", StringComparison.Ordinal))
                ApplyExactSerializedSavedProperties(material, definition);
            if (FurnitureMaterialNames.Contains(definition.Name) &&
                !HasExactFurnitureTransportContract(material, definition.Name, out string furnitureFailure))
                throw new InvalidDataException("Furniture material lost its exact vanilla closure: " +
                                               definition.Name + "; " + furnitureFailure + ".");
            if (string.Equals(definition.Name, "Lamps_C_on _cagville", StringComparison.Ordinal) &&
                !HasKillHouseFluorescentEmissionContract(material))
                throw new InvalidDataException("The vanilla-derived fluorescent material lost its 512-intensity warehouse tube contract.");
            if (string.Equals(definition.Name, "Floor", StringComparison.Ordinal) &&
                !HasExactWarehouseFloorTransportContract(material, out string floorFailure))
                throw new InvalidDataException("The exact PVP Woods Warehouse floor material lost its donor closure: " +
                                               floorFailure + ".");
            EditorUtility.SetDirty(material);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[Vektor Kill House] Rebuilt " + Definitions.Length + " native HDRP materials.");
    }

    public static Material Load(string sourceName)
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath(sourceName));
        if (material == null) throw new FileNotFoundException("Native material has not been built: " + sourceName);
        return material;
    }

    public static string MaterialPath(string sourceName) => OutputFolder + "/MAT_NATIVE_" + Sanitize(sourceName) + ".mat";

    private static void ConfigureTextureImporters()
    {
        string[] textureGuids = AssetDatabase.FindAssets("t:Texture2D", new[]
        {
            "Assets/VektorKillHouse/Native/TrainingWalls/Textures",
            "Assets/VektorKillHouse/Native/Residential/Textures",
            "Assets/VektorKillHouse/Native/ResidentialComplete/Textures",
            "Assets/VektorKillHouse/Native/ResidentialHierarchy/Textures",
            "Assets/VektorKillHouse/Native/RuggedDoor/Textures",
            "Assets/VektorKillHouse/Native/IndustrialLighting/Textures",
            "Assets/VektorKillHouse/Native/WarehouseShell/Textures",
            "Assets/VektorKillHouse/Native/WarehousePvp/Textures"
        });
        foreach (string guid in textureGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) continue;
            string name = Path.GetFileNameWithoutExtension(path);
            bool normal = name.EndsWith("_Normal", StringComparison.OrdinalIgnoreCase) ||
                          name.EndsWith("_N", StringComparison.OrdinalIgnoreCase) ||
                          name.IndexOf("Normal_", StringComparison.OrdinalIgnoreCase) >= 0;
            bool linear = normal || name.IndexOf("MaskMap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          name.StartsWith("Masks_", StringComparison.OrdinalIgnoreCase) ||
                          name.IndexOf("DetailMap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          string.Equals(name, "RM steel oxidized G", StringComparison.Ordinal);
            bool dirty = false;
            TextureImporterType targetType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (importer.textureType != targetType) { importer.textureType = targetType; dirty = true; }
            if (importer.sRGBTexture == linear) { importer.sRGBTexture = !linear; dirty = true; }
            // The exact warehouse-floor donor is a shipped 4K set. A 4096 cap preserves it while
            // leaving the native 1K/2K residential maps at their authored resolution.
            if (importer.maxTextureSize != 4096) { importer.maxTextureSize = 4096; dirty = true; }
            if (dirty) importer.SaveAndReimport();
        }
    }

    private static void ApplySourceValues(Material material, string sourceName)
    {
        string sourcePath = FindSourceMaterialRecord(sourceName);
        if (string.IsNullOrEmpty(sourcePath)) return;
        JObject json = JObject.Parse(File.ReadAllText(Path.GetFullPath(sourcePath)));
        var disabledPasses = new HashSet<string>((json["m_DisabledShaderPasses"] as JArray ?? new JArray())
            .Values<string>(), StringComparer.Ordinal);
        foreach (string passName in ExactDonorShaderPasses)
            material.SetShaderPassEnabled(passName, !disabledPasses.Contains(passName));
        foreach (string tagName in ExactDonorOverrideTags)
            material.SetOverrideTag(tagName, string.Empty);
        if (json["m_StringTagMap"] is JObject tags)
            foreach (JProperty tag in tags.Properties()) material.SetOverrideTag(tag.Name, tag.Value.Value<string>());
        if (json["m_LightmapFlags"] != null)
            material.globalIlluminationFlags = (MaterialGlobalIlluminationFlags)json.Value<int>("m_LightmapFlags");
        if (json["m_EnableInstancingVariants"] != null)
            material.enableInstancing = json.Value<bool>("m_EnableInstancingVariants");
        if (json["m_DoubleSidedGI"] != null)
            material.doubleSidedGI = json.Value<bool>("m_DoubleSidedGI");
        JObject floats = json.SelectToken("m_SavedProperties.m_Floats") as JObject;
        if (floats != null)
        {
            foreach (JProperty property in floats.Properties())
                if (property.Value.Type == JTokenType.Float || property.Value.Type == JTokenType.Integer)
                    SetFloat(material, property.Name, property.Value.Value<float>());
        }
        JObject colors = json.SelectToken("m_SavedProperties.m_Colors") as JObject;
        if (colors != null)
        {
            foreach (JProperty property in colors.Properties())
            {
                JToken color = property.Value;
                if (color?["m_R"] == null || color["m_G"] == null ||
                    color["m_B"] == null || color["m_A"] == null) continue;
                SetColor(material, property.Name, new Color(color.Value<float>("m_R"),
                    color.Value<float>("m_G"), color.Value<float>("m_B"), color.Value<float>("m_A")));
            }
        }

        Color baseColor = ReadSourceColor(sourceName, "_BaseColor", Color.white);
        SetColor(material, "_BaseColor", baseColor);
        SetColor(material, "_Color", baseColor);
        float smoothness = json.SelectToken("m_SavedProperties.m_Floats._Smoothness")?.Value<float>() ?? .35f;
        SetFloat(material, "_Glossiness", smoothness);
        float normalScale = json.SelectToken("m_SavedProperties.m_Floats._NormalScale")?.Value<float>() ?? 1f;
        SetFloat(material, "_BumpScale", normalScale);
    }

    // Material.SetFloat/SetColor only retain properties declared by the portable authoring shader.
    // That silently dropped 31 saved floats and one saved color from the pinned residential records.
    // Preserve the complete native saved-property table as transport cargo after HDRP normalization;
    // runtime still rehydrates the record through its audited resident shader family.
    private static void ApplyExactSerializedSavedProperties(Material material, Definition definition)
    {
        string sourcePath = FindSourceMaterialRecord(definition.Name);
        if (string.IsNullOrEmpty(sourcePath))
            throw new FileNotFoundException("Native source material record is missing: " + definition.Name);
        JObject json = JObject.Parse(File.ReadAllText(Path.GetFullPath(sourcePath)));
        var serialized = new SerializedObject(material);
        WriteFloatPairs(serialized.FindProperty("m_SavedProperties.m_Floats"),
            json.SelectToken("m_SavedProperties.m_Floats") as JObject);
        WriteIntPairs(serialized.FindProperty("m_SavedProperties.m_Ints"),
            json.SelectToken("m_SavedProperties.m_Ints") as JObject);
        WriteColorPairs(serialized.FindProperty("m_SavedProperties.m_Colors"),
            json.SelectToken("m_SavedProperties.m_Colors") as JObject);
        WriteTexturePairs(serialized.FindProperty("m_SavedProperties.m_TexEnvs"),
            json.SelectToken("m_SavedProperties.m_TexEnvs") as JObject, definition);
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void WriteFloatPairs(SerializedProperty array, JObject values)
    {
        RequirePairArray(array, "m_Floats");
        JProperty[] properties = (values ?? new JObject()).Properties().ToArray();
        array.arraySize = properties.Length;
        for (int i = 0; i < properties.Length; i++)
        {
            SerializedProperty pair = array.GetArrayElementAtIndex(i);
            RequireRelative(pair, "first").stringValue = properties[i].Name;
            RequireRelative(pair, "second").floatValue = properties[i].Value.Value<float>();
        }
    }

    private static void WriteIntPairs(SerializedProperty array, JObject values)
    {
        RequirePairArray(array, "m_Ints");
        JProperty[] properties = (values ?? new JObject()).Properties().ToArray();
        array.arraySize = properties.Length;
        for (int i = 0; i < properties.Length; i++)
        {
            SerializedProperty pair = array.GetArrayElementAtIndex(i);
            RequireRelative(pair, "first").stringValue = properties[i].Name;
            RequireRelative(pair, "second").longValue = properties[i].Value.Value<long>();
        }
    }

    private static void WriteColorPairs(SerializedProperty array, JObject values)
    {
        RequirePairArray(array, "m_Colors");
        JProperty[] properties = (values ?? new JObject()).Properties().ToArray();
        array.arraySize = properties.Length;
        for (int i = 0; i < properties.Length; i++)
        {
            SerializedProperty pair = array.GetArrayElementAtIndex(i);
            RequireRelative(pair, "first").stringValue = properties[i].Name;
            JToken value = properties[i].Value;
            RequireRelative(pair, "second").colorValue = new Color(value.Value<float>("m_R"),
                value.Value<float>("m_G"), value.Value<float>("m_B"), value.Value<float>("m_A"));
        }
    }

    private static void WriteTexturePairs(SerializedProperty array, JObject values, Definition definition)
    {
        RequirePairArray(array, "m_TexEnvs");
        JProperty[] properties = (values ?? new JObject()).Properties().ToArray();
        array.arraySize = properties.Length;
        for (int i = 0; i < properties.Length; i++)
        {
            SerializedProperty pair = array.GetArrayElementAtIndex(i);
            RequireRelative(pair, "first").stringValue = properties[i].Name;
            SerializedProperty second = RequireRelative(pair, "second");
            JToken value = properties[i].Value;
            RequireRelative(second, "m_Texture").objectReferenceValue =
                ResolveExactSourceTexture(definition, properties[i].Name, value);
            RequireRelative(second, "m_Scale").vector2Value = ReadVector2(value["m_Scale"], Vector2.one);
            RequireRelative(second, "m_Offset").vector2Value = ReadVector2(value["m_Offset"], Vector2.zero);
        }
    }

    private static Texture2D ResolveExactSourceTexture(Definition definition, string property, JToken texEnv)
    {
        long sourcePathId = texEnv?.SelectToken("m_Texture.m_PathID")?.Value<long>() ?? 0L;
        if (sourcePathId == 0L) return null;
        string exactName;
        switch (property)
        {
            case "_BaseColorMap":
            case "_BaseMap":
            case "_MainTex":
                exactName = definition.Base;
                break;
            case "_NormalMap":
            case "_BumpMap":
                exactName = definition.Normal;
                break;
            case "_MaskMap":
                exactName = definition.Mask;
                break;
            case "_DetailMap":
                exactName = definition.Detail;
                break;
            case "_EmissiveColorMap":
            case "_EmissionMap":
                exactName = definition.Emissive;
                break;
            default:
                throw new InvalidDataException("Unmapped non-null native texture PPtr " + sourcePathId +
                                               " on " + definition.Name + "." + property + ".");
        }
        if (string.IsNullOrEmpty(exactName))
            throw new InvalidDataException("Native material " + definition.Name + "." + property +
                                           " has source texture PPtr " + sourcePathId +
                                           " but no pinned extracted texture mapping.");
        return FindTexture(exactName);
    }

    private static void RequirePairArray(SerializedProperty property, string name)
    {
        if (property == null || !property.isArray)
            throw new InvalidDataException("Unity material serialization is missing pair array " + name + ".");
    }

    private static SerializedProperty RequireRelative(SerializedProperty parent, string name)
    {
        SerializedProperty property = parent?.FindPropertyRelative(name);
        if (property == null)
            throw new InvalidDataException("Unity material serialization is missing property " + name + ".");
        return property;
    }

    private static void ApplyKillHouseUseProfile(Material material, string sourceName)
    {
        bool trainingWall = string.Equals(sourceName, "ChipBoardShader", StringComparison.Ordinal) ||
                            string.Equals(sourceName, "PlyWoodShader", StringComparison.Ordinal);
        bool concreteFloor = string.Equals(sourceName, "In_Floor_Basement", StringComparison.Ordinal);
        if (trainingWall || concreteFloor)
        {
            float smoothness = trainingWall ? .18f : .14f;
            float normalScale = trainingWall ? 1.35f : .8f;
            SetFloat(material, "_Metallic", 0f);
            SetFloat(material, "_MetallicRemapMin", 0f);
            SetFloat(material, "_MetallicRemapMax", 0f);
            SetFloat(material, "_Smoothness", smoothness);
            SetFloat(material, "_Glossiness", smoothness);
            SetFloat(material, "_SmoothnessRemapMin", 0f);
            SetFloat(material, "_SmoothnessRemapMax", smoothness);
            SetFloat(material, "_NormalScale", normalScale);
            SetFloat(material, "_BumpScale", normalScale);
            SetFloat(material, "_ReceivesSSR", 0f);
            SetFloat(material, "_EnvironmentReflections", 0f);
            SetFloat(material, "_CoatMask", 0f);
            SetFloat(material, "_ClearCoatMask", 0f);
        }

        if (string.Equals(sourceName, "Corrugated Metal Sheet_vb1lafx", StringComparison.Ordinal))
        {
            SetFloat(material, "_Metallic", .08f);
            SetFloat(material, "_MetallicRemapMin", 0f);
            SetFloat(material, "_MetallicRemapMax", .08f);
            SetFloat(material, "_Smoothness", .18f);
            SetFloat(material, "_Glossiness", .18f);
            SetFloat(material, "_SmoothnessRemapMin", 0f);
            SetFloat(material, "_SmoothnessRemapMax", .18f);
            SetFloat(material, "_ReceivesSSR", 0f);
            SetFloat(material, "_EnvironmentReflections", 0f);
        }

        if (string.Equals(sourceName, "RM Steel smooth", StringComparison.Ordinal))
        {
            SetFloat(material, "_Metallic", .12f);
            SetFloat(material, "_MetallicRemapMin", 0f);
            SetFloat(material, "_MetallicRemapMax", .12f);
            SetFloat(material, "_Smoothness", .33f);
            SetFloat(material, "_Glossiness", .33f);
            SetFloat(material, "_SmoothnessRemapMin", 0f);
            SetFloat(material, "_SmoothnessRemapMax", .33f);
            SetFloat(material, "_NormalScale", 0f);
            SetFloat(material, "_BumpScale", 0f);
            SetFloat(material, "_CoatMask", 0f);
            SetFloat(material, "_ClearCoatMask", 0f);
        }

        if (string.Equals(sourceName, "Lamps_House_Off", StringComparison.Ordinal))
        {
            Color energized = new Color(2.5f, 2.3f, 2.0f, 1f);
            SetColor(material, "_EmissiveColor", energized);
            SetColor(material, "_EmissionColor", energized);
            SetFloat(material, "_EmissiveExposureWeight", .5f);
            material.EnableKeyword("_EMISSIVE_COLOR_MAP");
            material.EnableKeyword("_EMISSION");
        }

        if (string.Equals(sourceName, "Lamps_C_on _cagville", StringComparison.Ordinal))
        {
            // Preserve the exact installed Lamps_C_on mesh/mask/mode contract, but art-direct its
            // surface 2.2630344058 stops above the 64-unit donor at the 6.8 m mounting height.
            // Room illumination remains on the paired lumen-authored spotlight.
            Color energized = new Color(KillHouseFluorescentLitEmission, KillHouseFluorescentLitEmission,
                KillHouseFluorescentLitEmission, 1f);
            SetColor(material, "_EmissiveColor", energized);
            SetColor(material, "_EmissiveColorLDR", Color.white);
            SetColor(material, "_EmissionColor", Color.white);
            SetFloat(material, "_UseEmissiveIntensity", 1f);
            SetFloat(material, "_EmissiveColorMode", 1f);
            SetFloat(material, "_AlbedoAffectEmissive", 1f);
            SetFloat(material, "_EmissiveIntensity", KillHouseFluorescentLitEmission);
            SetFloat(material, "_EmissiveIntensityUnit", KillHouseFluorescentIntensityUnit);
            SetFloat(material, "_EmissiveExposureWeight", KillHouseFluorescentExposureWeight);
            material.EnableKeyword("_EMISSIVE_COLOR_MAP");
            // The installed HDRP/Lit donor does not carry Unity's legacy _EMISSION keyword.
            material.DisableKeyword("_EMISSION");
        }

        if (string.Equals(sourceName, "MI_DoorsWindows", StringComparison.Ordinal))
        {
            SetFloat(material, "_Metallic", 0f);
            SetFloat(material, "_Smoothness", .28f);
            SetFloat(material, "_Glossiness", .28f);
        }
    }

    private static Color ReadSourceColor(string sourceName, string property, Color fallback)
    {
        string sourcePath = FindSourceMaterialRecord(sourceName);
        if (string.IsNullOrEmpty(sourcePath)) return fallback;
        JObject json = JObject.Parse(File.ReadAllText(Path.GetFullPath(sourcePath)));
        JToken color = json.SelectToken("m_SavedProperties.m_Colors." + property);
        return color == null ? fallback : new Color(color.Value<float>("m_R"), color.Value<float>("m_G"),
            color.Value<float>("m_B"), color.Value<float>("m_A"));
    }

    public static bool HasKillHouseFluorescentEmissionContract(Material material)
    {
        if (material == null || material.shader == null ||
            !string.Equals(material.shader.name, "HDRP/Lit", StringComparison.Ordinal)) return false;
        Texture emissiveMap = material.HasProperty("_EmissiveColorMap")
            ? material.GetTexture("_EmissiveColorMap")
            : null;
        if (emissiveMap == null || !string.Equals(emissiveMap.name, "Lamps_C_Emissive", StringComparison.Ordinal))
            return false;
        Color emissive = material.HasProperty("_EmissiveColor")
            ? material.GetColor("_EmissiveColor")
            : Color.black;
        return material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") &&
               material.HasProperty("_UseEmissiveIntensity") &&
               Mathf.Abs(material.GetFloat("_UseEmissiveIntensity") - 1f) <= .001f &&
               material.HasProperty("_AlbedoAffectEmissive") &&
               Mathf.Abs(material.GetFloat("_AlbedoAffectEmissive") - 1f) <= .001f &&
               material.HasProperty("_EmissiveIntensity") &&
               Mathf.Abs(material.GetFloat("_EmissiveIntensity") - KillHouseFluorescentLitEmission) <= .01f &&
               material.HasProperty("_EmissiveIntensityUnit") &&
               Mathf.Abs(material.GetFloat("_EmissiveIntensityUnit") - KillHouseFluorescentIntensityUnit) <= .001f &&
               material.HasProperty("_EmissiveExposureWeight") &&
               Mathf.Abs(material.GetFloat("_EmissiveExposureWeight") - KillHouseFluorescentExposureWeight) <= .001f &&
               Mathf.Abs(emissive.r - KillHouseFluorescentLitEmission) <= .01f &&
               Mathf.Abs(emissive.g - KillHouseFluorescentLitEmission) <= .01f &&
               Mathf.Abs(emissive.b - KillHouseFluorescentLitEmission) <= .01f;
    }

    public static bool HasExactWarehouseFloorTransportContract(Material material, out string failure,
        bool requireAssetDatabaseIdentity = true)
    {
        failure = string.Empty;
        if (material == null || material.shader == null ||
            !string.Equals(material.shader.name, "HDRP/Lit", StringComparison.Ordinal) ||
            material.renderQueue != 2225)
        {
            failure = "shader-or-queue";
            return false;
        }
        if (!TextureMatches(material, "_BaseColorMap", "Albedo_4K__wckscdz") ||
            !TextureMatches(material, "_NormalMap", "Normal_4K__wckscdz") ||
            !TextureMatches(material, "_MaskMap", "Masks_4K__wckscdz"))
        {
            failure = "texture-closure";
            return false;
        }
        if (!material.IsKeywordEnabled("_MAPPING_TRIPLANAR") ||
            !material.IsKeywordEnabled("_MASKMAP") ||
            !material.IsKeywordEnabled("_NORMALMAP") ||
            !material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") ||
            !material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") ||
            (material.HasProperty("_UVBase") && Mathf.Abs(material.GetFloat("_UVBase") - 5f) > .001f) ||
            (material.HasProperty("_TexWorldScale") && Mathf.Abs(material.GetFloat("_TexWorldScale") - .25f) > .001f))
        {
            failure = "triplanar-keyword-or-scale";
            return false;
        }
        string sourcePath = FindSourceMaterialRecord("Floor");
        if (string.IsNullOrEmpty(sourcePath))
        {
            failure = "source-record";
            return false;
        }
        JObject source = JObject.Parse(File.ReadAllText(Path.GetFullPath(sourcePath)));
        Definition definition = Definitions.First(candidate =>
            string.Equals(candidate.Name, "Floor", StringComparison.Ordinal));
        if (!HasExactSerializedSavedProperties(material, source, definition, requireAssetDatabaseIdentity,
                out failure) ||
            !HasExactSerializedRenderState(material, source, out failure)) return false;
        return true;
    }

    public static bool HasExactFurnitureTransportContract(Material material, string sourceName,
        out string failure, bool requireAssetDatabaseIdentity = true)
    {
        failure = string.Empty;
        Definition definition = Definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, sourceName, StringComparison.Ordinal));
        if (definition == null || !FurnitureMaterialNames.Contains(sourceName))
        {
            failure = "unknown-furniture-material";
            return false;
        }
        if (material == null || material.shader == null ||
            !string.Equals(material.shader.name, "HDRP/Lit", StringComparison.Ordinal) ||
            material.renderQueue != 2225)
        {
            failure = "shader-or-queue";
            return false;
        }

        if (!TextureMatches(material, "_BaseColorMap", definition.Base) ||
            (!string.IsNullOrEmpty(definition.Normal) &&
             !TextureMatches(material, "_NormalMap", definition.Normal)) ||
            (!string.IsNullOrEmpty(definition.Mask) &&
             !TextureMatches(material, "_MaskMap", definition.Mask)) ||
            (!string.IsNullOrEmpty(definition.Emissive) &&
             !TextureMatches(material, "_EmissiveColorMap", definition.Emissive)))
        {
            failure = "texture-closure";
            return false;
        }
        if (string.Equals(sourceName, "Couch_Fabric", StringComparison.Ordinal) &&
            !HasExactCouchTextureDimensions(material))
        {
            failure = "couch-texture-dimensions-or-mips";
            return false;
        }

        string sourcePath = FindSourceMaterialRecord(sourceName);
        if (string.IsNullOrEmpty(sourcePath))
        {
            failure = "source-record";
            return false;
        }
        JObject json = JObject.Parse(File.ReadAllText(Path.GetFullPath(sourcePath)));
        long expectedSourceShaderPathId = UsesMilkShaderSource(sourceName)
            ? 210L
            : 354L;
        if (json.SelectToken("m_Shader.m_PathID")?.Value<long>() != expectedSourceShaderPathId)
        {
            failure = "source-shader-identity";
            return false;
        }
        if (!HasExactSerializedSavedProperties(material, json, definition, requireAssetDatabaseIdentity,
                out failure)) return false;
        string[] exactFloats =
        {
            "_Metallic", "_MetallicRemapMin", "_MetallicRemapMax", "_Smoothness",
            "_SmoothnessRemapMin", "_SmoothnessRemapMax", "_AORemapMin", "_AORemapMax",
            "_OcclusionStrength", "_NormalScale", "_MaterialID", "_ReceivesSSR",
            "_TransmissionEnable", "_TransmissionMask"
        };
        foreach (string property in exactFloats)
        {
            JToken token = json.SelectToken("m_SavedProperties.m_Floats." + property);
            if (token == null || !material.HasProperty(property)) continue;
            if (Mathf.Abs(material.GetFloat(property) - token.Value<float>()) > .001f)
            {
                failure = "float:" + property;
                return false;
            }
        }

        Color expectedBase = ReadSourceColor(sourceName, "_BaseColor", Color.white);
        if (!material.HasProperty("_BaseColor") ||
            !ColorApproximately(material.GetColor("_BaseColor"), expectedBase, .001f))
        {
            failure = "base-color";
            return false;
        }
        if (!string.IsNullOrEmpty(definition.Emissive))
        {
            Color expectedEmissive = ReadSourceColor(sourceName, "_EmissiveColor", Color.black);
            if (!material.HasProperty("_EmissiveColor") ||
                !ColorApproximately(material.GetColor("_EmissiveColor"), expectedEmissive, .001f) ||
                !material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP"))
            {
                failure = "emissive-contract";
                return false;
            }
        }
        if (string.Equals(sourceName, "Kitchen_Cabinet_Marble", StringComparison.Ordinal) &&
            (material.IsKeywordEnabled("_NORMALMAP") ||
             !material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE")))
        {
            failure = "marble-normal-keywords";
            return false;
        }
        if (!UsesMilkShaderSource(sourceName) &&
            !HasExactHdrpFurnitureKeywords(material, sourceName))
        {
            failure = "keyword-closure";
            return false;
        }
        if (!TextureTransformMatches(material, sourceName, definition.BaseSourceTexEnv, "_BaseColorMap") ||
            (!string.IsNullOrEmpty(definition.Normal) &&
             !TextureTransformMatches(material, sourceName, "_NormalMap", "_NormalMap")) ||
            (!string.IsNullOrEmpty(definition.Mask) &&
             !TextureTransformMatches(material, sourceName, "_MaskMap", "_MaskMap")))
        {
            failure = "texture-transform";
            return false;
        }
        if (!HasExactSerializedRenderState(material, json, out failure)) return false;
        return true;
    }

    private static bool HasExactCouchTextureDimensions(Material material)
    {
        Texture2D baseMap = material.GetTexture("_BaseColorMap") as Texture2D;
        Texture2D normalMap = material.GetTexture("_NormalMap") as Texture2D;
        Texture2D maskMap = material.GetTexture("_MaskMap") as Texture2D;
        return baseMap != null && baseMap.width == 2048 && baseMap.height == 2048 &&
               baseMap.mipmapCount == 12 && normalMap != null && normalMap.width == 1024 &&
               normalMap.height == 1024 && normalMap.mipmapCount == 11 && maskMap != null &&
               maskMap.width == 512 && maskMap.height == 512 && maskMap.mipmapCount == 10;
    }

    private static bool HasExactHdrpFurnitureKeywords(Material material, string sourceName)
    {
        bool expectsNormal = !string.Equals(sourceName, "Kitchen_Cabinet_Marble", StringComparison.Ordinal);
        bool expectsEmissive = string.Equals(sourceName, "Devices_On", StringComparison.Ordinal);
        return !material.IsKeywordEnabled("_DISABLE_SSR") &&
               material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT") &&
               material.IsKeywordEnabled("_MASKMAP") &&
               material.IsKeywordEnabled("_NORMALMAP_TANGENT_SPACE") &&
               material.IsKeywordEnabled("_NORMALMAP") == expectsNormal &&
               material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") == expectsEmissive &&
               !material.IsKeywordEnabled("_EMISSION");
    }

    private static bool HasExactSerializedRenderState(Material material, JObject json, out string failure)
    {
        failure = string.Empty;
        var expectedDisabled = new HashSet<string>((json["m_DisabledShaderPasses"] as JArray ?? new JArray())
            .Values<string>(), StringComparer.Ordinal);
        foreach (string passName in ExactDonorShaderPasses)
        {
            bool actualDisabled = !material.GetShaderPassEnabled(passName);
            if (actualDisabled == expectedDisabled.Contains(passName)) continue;
            failure = "shader-pass:" + passName;
            return false;
        }
        JObject expectedTags = json["m_StringTagMap"] as JObject;
        foreach (string tagName in ExactDonorOverrideTags)
        {
            string expected = expectedTags?[tagName]?.Value<string>() ?? string.Empty;
            if (string.Equals(material.GetTag(tagName, false, string.Empty), expected,
                    StringComparison.Ordinal)) continue;
            failure = "tag:" + tagName;
            return false;
        }
        if (json["m_LightmapFlags"] != null &&
            (int)material.globalIlluminationFlags != json.Value<int>("m_LightmapFlags"))
        {
            failure = "lightmap-flags";
            return false;
        }
        if (json["m_EnableInstancingVariants"] != null &&
            material.enableInstancing != json.Value<bool>("m_EnableInstancingVariants"))
        {
            failure = "instancing";
            return false;
        }
        if (json["m_DoubleSidedGI"] != null && material.doubleSidedGI != json.Value<bool>("m_DoubleSidedGI"))
        {
            failure = "double-sided-gi";
            return false;
        }
        return true;
    }

    private static bool HasExactSerializedSavedProperties(Material material, JObject json,
        Definition definition, bool requireAssetDatabaseIdentity, out string failure)
    {
        failure = string.Empty;
        var serialized = new SerializedObject(material);
        if (!SerializedFloatPairsMatch(serialized.FindProperty("m_SavedProperties.m_Floats"),
                json.SelectToken("m_SavedProperties.m_Floats") as JObject, out failure) ||
            !SerializedIntPairsMatch(serialized.FindProperty("m_SavedProperties.m_Ints"),
                json.SelectToken("m_SavedProperties.m_Ints") as JObject, out failure) ||
            !SerializedColorPairsMatch(serialized.FindProperty("m_SavedProperties.m_Colors"),
                json.SelectToken("m_SavedProperties.m_Colors") as JObject, out failure) ||
            !SerializedTexturePairsMatch(serialized.FindProperty("m_SavedProperties.m_TexEnvs"),
                json.SelectToken("m_SavedProperties.m_TexEnvs") as JObject, definition,
                requireAssetDatabaseIdentity, out failure))
            return false;
        return true;
    }

    private static bool SerializedFloatPairsMatch(SerializedProperty array, JObject expected,
        out string failure)
    {
        failure = string.Empty;
        if (!TryReadPairs(array, out Dictionary<string, SerializedProperty> actual) ||
            actual.Count != (expected ?? new JObject()).Count)
        {
            failure = "saved-float-count";
            return false;
        }
        foreach (JProperty property in (expected ?? new JObject()).Properties())
        {
            if (!actual.TryGetValue(property.Name, out SerializedProperty second) ||
                Mathf.Abs(second.floatValue - property.Value.Value<float>()) > .00001f)
            {
                failure = "saved-float:" + property.Name;
                return false;
            }
        }
        return true;
    }

    private static bool SerializedIntPairsMatch(SerializedProperty array, JObject expected,
        out string failure)
    {
        failure = string.Empty;
        if (!TryReadPairs(array, out Dictionary<string, SerializedProperty> actual) ||
            actual.Count != (expected ?? new JObject()).Count)
        {
            failure = "saved-int-count";
            return false;
        }
        foreach (JProperty property in (expected ?? new JObject()).Properties())
        {
            if (!actual.TryGetValue(property.Name, out SerializedProperty second) ||
                second.longValue != property.Value.Value<long>())
            {
                failure = "saved-int:" + property.Name;
                return false;
            }
        }
        return true;
    }

    private static bool SerializedColorPairsMatch(SerializedProperty array, JObject expected,
        out string failure)
    {
        failure = string.Empty;
        if (!TryReadPairs(array, out Dictionary<string, SerializedProperty> actual) ||
            actual.Count != (expected ?? new JObject()).Count)
        {
            failure = "saved-color-count";
            return false;
        }
        foreach (JProperty property in (expected ?? new JObject()).Properties())
        {
            JToken value = property.Value;
            Color wanted = new Color(value.Value<float>("m_R"), value.Value<float>("m_G"),
                value.Value<float>("m_B"), value.Value<float>("m_A"));
            if (!actual.TryGetValue(property.Name, out SerializedProperty second) ||
                !ColorApproximately(second.colorValue, wanted, .00001f))
            {
                failure = "saved-color:" + property.Name;
                return false;
            }
        }
        return true;
    }

    private static bool SerializedTexturePairsMatch(SerializedProperty array, JObject expected,
        Definition definition, bool requireAssetDatabaseIdentity, out string failure)
    {
        failure = string.Empty;
        JObject wanted = expected ?? new JObject();
        bool milkShaderTransportProxy = UsesMilkShaderSource(definition.Name);
        if (!TryReadPairs(array, out Dictionary<string, SerializedProperty> actual) ||
            (!milkShaderTransportProxy && actual.Count != wanted.Count) ||
            (milkShaderTransportProxy && actual.Count < wanted.Count))
        {
            failure = "saved-texenv-count";
            return false;
        }
        foreach (JProperty property in wanted.Properties())
        {
            if (!actual.TryGetValue(property.Name, out SerializedProperty second))
            {
                failure = "saved-texenv:" + property.Name;
                return false;
            }
            Texture expectedTexture = ResolveExactSourceTexture(definition, property.Name, property.Value);
            SerializedProperty textureProperty = second.FindPropertyRelative("m_Texture");
            SerializedProperty scaleProperty = second.FindPropertyRelative("m_Scale");
            SerializedProperty offsetProperty = second.FindPropertyRelative("m_Offset");
            Texture actualTexture = textureProperty == null
                ? null
                : textureProperty.objectReferenceValue as Texture;
            bool textureIdentityMatches = requireAssetDatabaseIdentity
                ? actualTexture == expectedTexture
                : actualTexture == null && expectedTexture == null ||
                  actualTexture != null && expectedTexture != null &&
                  string.Equals(actualTexture.name, expectedTexture.name, StringComparison.Ordinal);
            if (textureProperty == null || scaleProperty == null || offsetProperty == null ||
                !textureIdentityMatches ||
                Vector2.Distance(scaleProperty.vector2Value,
                    ReadVector2(property.Value["m_Scale"], Vector2.one)) > .00001f ||
                Vector2.Distance(offsetProperty.vector2Value,
                    ReadVector2(property.Value["m_Offset"], Vector2.zero)) > .00001f)
            {
                failure = "saved-texenv-state:" + property.Name;
                return false;
            }
        }
        if (milkShaderTransportProxy)
        {
            foreach (KeyValuePair<string, SerializedProperty> extra in actual.Where(pair =>
                         wanted[ pair.Key ] == null))
            {
                SerializedProperty textureProperty = extra.Value.FindPropertyRelative("m_Texture");
                SerializedProperty scaleProperty = extra.Value.FindPropertyRelative("m_Scale");
                SerializedProperty offsetProperty = extra.Value.FindPropertyRelative("m_Offset");
                Texture actualAliasTexture = textureProperty == null
                    ? null
                    : textureProperty.objectReferenceValue as Texture;
                Texture expectedAliasTexture = FindTexture(definition.Base);
                bool aliasTextureMatches = requireAssetDatabaseIdentity
                    ? actualAliasTexture == expectedAliasTexture
                    : actualAliasTexture != null &&
                      string.Equals(actualAliasTexture.name, expectedAliasTexture.name, StringComparison.Ordinal);
                bool exactHdrpMainTexAlias = string.Equals(extra.Key, "_MainTex", StringComparison.Ordinal) &&
                    textureProperty != null && scaleProperty != null && offsetProperty != null &&
                    aliasTextureMatches &&
                    Vector2.Distance(scaleProperty.vector2Value,
                        ReadVector2(wanted["_BaseColorMap"]?["m_Scale"], Vector2.one)) <= .00001f &&
                    Vector2.Distance(offsetProperty.vector2Value,
                        ReadVector2(wanted["_BaseColorMap"]?["m_Offset"], Vector2.zero)) <= .00001f;
                if (!exactHdrpMainTexAlias &&
                    (textureProperty == null || scaleProperty == null || offsetProperty == null ||
                     textureProperty.objectReferenceValue != null ||
                     Vector2.Distance(scaleProperty.vector2Value, Vector2.one) > .00001f ||
                     Vector2.Distance(offsetProperty.vector2Value, Vector2.zero) > .00001f))
                {
                    failure = "non-inert-proxy-texenv:" + extra.Key;
                    return false;
                }
            }
        }
        return true;
    }

    private static bool TryReadPairs(SerializedProperty array,
        out Dictionary<string, SerializedProperty> values)
    {
        values = new Dictionary<string, SerializedProperty>(StringComparer.Ordinal);
        if (array == null || !array.isArray) return false;
        for (int i = 0; i < array.arraySize; i++)
        {
            SerializedProperty pair = array.GetArrayElementAtIndex(i);
            SerializedProperty first = pair?.FindPropertyRelative("first");
            SerializedProperty second = pair?.FindPropertyRelative("second");
            if (first == null || second == null || string.IsNullOrEmpty(first.stringValue) ||
                values.ContainsKey(first.stringValue)) return false;
            values.Add(first.stringValue, second);
        }
        return true;
    }

    private static bool UsesMilkShaderSource(string sourceName)
    {
        return string.Equals(sourceName, "Kitchen_TableChair", StringComparison.Ordinal) ||
               string.Equals(sourceName, "Couch_Fabric", StringComparison.Ordinal);
    }

    private static bool TextureMatches(Material material, string property, string expectedName)
    {
        if (material == null || !material.HasProperty(property)) return false;
        Texture texture = material.GetTexture(property);
        return texture != null && string.Equals(texture.name, expectedName, StringComparison.Ordinal);
    }

    private static bool TextureTransformMatches(Material material, string sourceName, string sourceProperty,
        string destinationProperty)
    {
        if (!material.HasProperty(destinationProperty)) return false;
        string sourcePath = FindSourceMaterialRecord(sourceName);
        if (string.IsNullOrEmpty(sourcePath)) return false;
        JObject json = JObject.Parse(File.ReadAllText(Path.GetFullPath(sourcePath)));
        JToken texEnv = json.SelectToken("m_SavedProperties.m_TexEnvs['" + sourceProperty + "']");
        if (texEnv == null) return true;
        Vector2 expectedScale = ReadVector2(texEnv["m_Scale"], Vector2.one);
        Vector2 expectedOffset = ReadVector2(texEnv["m_Offset"], Vector2.zero);
        return Vector2.Distance(material.GetTextureScale(destinationProperty), expectedScale) <= .001f &&
               Vector2.Distance(material.GetTextureOffset(destinationProperty), expectedOffset) <= .001f;
    }

    private static bool ColorApproximately(Color actual, Color expected, float tolerance)
    {
        return Mathf.Abs(actual.r - expected.r) <= tolerance &&
               Mathf.Abs(actual.g - expected.g) <= tolerance &&
               Mathf.Abs(actual.b - expected.b) <= tolerance &&
               Mathf.Abs(actual.a - expected.a) <= tolerance;
    }

    private static string FindSourceMaterialRecord(string sourceName)
    {
        string[] sourceGuids = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(sourceName), new[]
        {
            "Assets/VektorKillHouse/Native/TrainingWalls/SourceMaterials",
            "Assets/VektorKillHouse/Native/Residential/SourceMaterials",
            "Assets/VektorKillHouse/Native/ResidentialComplete/SourceMaterials",
            "Assets/VektorKillHouse/Native/ResidentialHierarchy/SourceMaterials",
            "Assets/VektorKillHouse/Native/RuggedDoor/SourceMaterials",
            "Assets/VektorKillHouse/Native/IndustrialLighting/SourceMaterials",
            "Assets/VektorKillHouse/Native/WarehouseShell/SourceMaterials",
            "Assets/VektorKillHouse/Native/WarehousePvp/SourceMaterials"
        });
        return sourceGuids.Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), sourceName, StringComparison.Ordinal));
    }

    private static Texture2D FindTexture(string exactName)
    {
        if (string.IsNullOrEmpty(exactName)) return null;
        string[] guids = AssetDatabase.FindAssets(exactName + " t:Texture2D", new[] { "Assets/VektorKillHouse/Native" });
        string path = guids.Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(candidate => string.Equals(Path.GetFileNameWithoutExtension(candidate), exactName, StringComparison.Ordinal));
        Texture2D texture = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (texture == null) throw new FileNotFoundException("Native texture is missing: " + exactName);
        return texture;
    }

    private static void SetTexture(Material material, string property, Texture value)
    {
        if (material.HasProperty(property)) material.SetTexture(property, value);
    }

    private static void ApplyTextureTransform(Material material, string sourceName, string sourceProperty,
        params string[] destinationProperties)
    {
        string sourcePath = FindSourceMaterialRecord(sourceName);
        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(sourceProperty)) return;
        JObject json = JObject.Parse(File.ReadAllText(Path.GetFullPath(sourcePath)));
        JToken texEnv = json.SelectToken("m_SavedProperties.m_TexEnvs['" + sourceProperty + "']");
        if (texEnv == null) return;
        Vector2 scale = ReadVector2(texEnv["m_Scale"], Vector2.one);
        Vector2 offset = ReadVector2(texEnv["m_Offset"], Vector2.zero);
        foreach (string property in destinationProperties)
        {
            if (!material.HasProperty(property)) continue;
            material.SetTextureScale(property, scale);
            material.SetTextureOffset(property, offset);
        }
    }

    private static Vector2 ReadVector2(JToken token, Vector2 fallback)
    {
        if (token == null) return fallback;
        JToken x = token["m_X"] ?? token["x"];
        JToken y = token["m_Y"] ?? token["y"];
        return x == null || y == null ? fallback : new Vector2(x.Value<float>(), y.Value<float>());
    }

    private static void SetFloat(Material material, string property, float value)
    {
        if (material.HasProperty(property)) material.SetFloat(property, value);
    }

    private static void SetColor(Material material, string property, Color value)
    {
        if (material.HasProperty(property)) material.SetColor(property, value);
    }

    private static void ValidateHdrpMaterial(Material material)
    {
        Type type = Type.GetType("UnityEngine.Rendering.HighDefinition.HDMaterial, Unity.RenderPipelines.HighDefinition.Runtime");
        MethodInfo method = type?.GetMethod("ValidateMaterial", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(Material) }, null);
        method?.Invoke(null, new object[] { material });
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
