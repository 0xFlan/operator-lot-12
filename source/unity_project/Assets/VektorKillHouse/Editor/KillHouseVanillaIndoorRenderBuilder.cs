#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

public static class KillHouseVanillaIndoorRenderBuilder
{
    public const string RenderingFolder = "Assets/VektorKillHouse/Native/Rendering";
    public const string RawLutPath = RenderingFolder + "/AgX_PunchyPowerfulMix_RGBAHalf.bytes";
    public const string LutAssetPath = RenderingFolder + "/AgX_PunchyPowerfulMix.asset";
    public const string ProfileAssetPath = RenderingFolder + "/VektorKillHouse_IndoorOffice_Profile.asset";
    public const float GlobalVolumePriority = 100010f;

    private const int LutSize = 32;
    private const int LutPayloadBytes = LutSize * LutSize * LutSize * 8;

    [MenuItem("Vektor Kill House/Rendering/Rebuild Audited Vanilla Indoor Profile", priority = 15)]
    public static void Build()
    {
        EnsureFolder(RenderingFolder);
        Texture3D lut = BuildLut();
        VolumeProfile profile = BuildProfile(lut);
        EditorUtility.SetDirty(lut);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("[Vektor Kill House] Rebuilt the PVP Woods Warehouse post stack and native AgX LUT.");
    }

    public static VolumeProfile LoadProfile()
    {
        VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfileAssetPath);
        if (profile == null) throw new FileNotFoundException("Audited indoor VolumeProfile is missing.", ProfileAssetPath);
        return profile;
    }

    private static Texture3D BuildLut()
    {
        string absoluteRawPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", RawLutPath));
        if (!File.Exists(absoluteRawPath))
            throw new FileNotFoundException("Extracted vanilla AgX LUT payload is missing.", absoluteRawPath);
        byte[] payload = File.ReadAllBytes(absoluteRawPath);
        if (payload.Length != LutPayloadBytes)
            throw new InvalidDataException("Vanilla AgX LUT payload must contain exactly " + LutPayloadBytes + " bytes.");

        Texture3D lut = AssetDatabase.LoadAssetAtPath<Texture3D>(LutAssetPath);
        if (lut == null)
        {
            lut = new Texture3D(LutSize, LutSize, LutSize, TextureFormat.RGBAHalf, false)
            {
                name = "AgX - PunchyPowerfulMix"
            };
            AssetDatabase.CreateAsset(lut, LutAssetPath);
        }
        if (lut.width != LutSize || lut.height != LutSize || lut.depth != LutSize || lut.format != TextureFormat.RGBAHalf)
            throw new InvalidDataException("Existing AgX LUT asset no longer matches the audited 32x32x32 RGBAHalf contract.");
        lut.SetPixelData(payload, 0, 0);
        lut.name = "AgX - PunchyPowerfulMix";
        lut.wrapMode = TextureWrapMode.Repeat;
        lut.filterMode = FilterMode.Trilinear;
        lut.anisoLevel = 1;
        lut.Apply(false, false);
        return lut;
    }

    private static VolumeProfile BuildProfile(Texture3D lut)
    {
        VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfileAssetPath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "Vektor Kill House - PVP Woods Warehouse Indoor";
            AssetDatabase.CreateAsset(profile, ProfileAssetPath);
        }
        foreach (VolumeComponent old in profile.components.Where(component => component != null).ToArray())
            UnityEngine.Object.DestroyImmediate(old, true);
        profile.components.Clear();

        Exposure exposure = GetOrAdd<Exposure>(profile);
        exposure.active = true;
        exposure.mode.Override(ExposureMode.AutomaticHistogram);
        exposure.meteringMode.Override(MeteringMode.CenterWeighted);
        exposure.luminanceSource.overrideState = false;
        exposure.fixedExposure.Override(10f);
        exposure.compensation.Override(0f);
        exposure.limitMin.Override(8.5f);
        exposure.limitMax.Override(11f);
        exposure.adaptationMode.overrideState = false;
        exposure.adaptationSpeedDarkToLight.overrideState = false;
        exposure.adaptationSpeedLightToDark.overrideState = false;
        exposure.centerAroundExposureTarget.overrideState = false;

        VisualEnvironment visualEnvironment = GetOrAdd<VisualEnvironment>(profile);
        visualEnvironment.active = false;
        PhysicallyBasedSky physicallyBasedSky = GetOrAdd<PhysicallyBasedSky>(profile);
        physicallyBasedSky.active = false;
        Fog fog = GetOrAdd<Fog>(profile);
        fog.active = false;
        ProbeVolumesOptions probeVolumes = GetOrAdd<ProbeVolumesOptions>(profile);
        probeVolumes.active = true;

        Bloom bloom = GetOrAdd<Bloom>(profile);
        bloom.active = true;
        bloom.threshold.Override(.9f);
        bloom.intensity.Override(.03f);
        bloom.scatter.Override(.893f);
        bloom.tint.overrideState = false;
        bloom.dirtTexture.overrideState = false;
        bloom.dirtIntensity.Override(0f);
        bloom.anamorphic.overrideState = false;

        ScreenSpaceLensFlare lensFlare = GetOrAdd<ScreenSpaceLensFlare>(profile);
        lensFlare.active = true;
        lensFlare.intensity.Override(.5f);
        lensFlare.tintColor.overrideState = false;
        lensFlare.bloomMip.overrideState = false;
        lensFlare.firstFlareIntensity.overrideState = false;
        lensFlare.secondaryFlareIntensity.overrideState = false;
        lensFlare.warpedFlareIntensity.overrideState = false;
        lensFlare.warpedFlareScale.overrideState = false;
        lensFlare.samples.overrideState = false;
        lensFlare.sampleDimmer.overrideState = false;
        lensFlare.vignetteEffect.overrideState = false;
        lensFlare.startingPosition.overrideState = false;
        lensFlare.scale.overrideState = false;
        lensFlare.streaksIntensity.Override(1.55f);
        lensFlare.streaksLength.Override(.022f);
        lensFlare.streaksOrientation.Override(0f);
        lensFlare.streaksThreshold.overrideState = false;
        lensFlare.resolution.overrideState = false;
        lensFlare.spectralLut.overrideState = false;
        lensFlare.chromaticAbberationIntensity.Override(.6f);
        lensFlare.chromaticAbberationSampleCount.overrideState = false;

        MicroShadowing microShadowing = GetOrAdd<MicroShadowing>(profile);
        microShadowing.active = true;
        ContactShadows contactShadows = GetOrAdd<ContactShadows>(profile);
        contactShadows.active = true;
        HDShadowSettings shadowSettings = GetOrAdd<HDShadowSettings>(profile);
        shadowSettings.active = true;

        Tonemapping tonemapping = GetOrAdd<Tonemapping>(profile);
        tonemapping.active = true;
        tonemapping.mode.Override(TonemappingMode.External);
        tonemapping.lutTexture.Override(lut);
        tonemapping.lutContribution.value = 1f;
        tonemapping.lutContribution.overrideState = false;
        tonemapping.useFullACES.Override(false);

        LiftGammaGain liftGammaGain = GetOrAdd<LiftGammaGain>(profile);
        liftGammaGain.active = true;
        liftGammaGain.lift.Override(new Vector4(1f, 1f, 1f, .00827304f));
        liftGammaGain.gamma.Override(new Vector4(1f, 1f, 1f, -.091003f));
        liftGammaGain.gain.Override(new Vector4(1f, 1f, 1f, .091003f));

        WhiteBalance whiteBalance = GetOrAdd<WhiteBalance>(profile);
        whiteBalance.active = true;
        whiteBalance.temperature.Override(-3.6f);
        whiteBalance.tint.Override(-8.6f);

        ColorAdjustments colorAdjustments = GetOrAdd<ColorAdjustments>(profile);
        colorAdjustments.active = true;
        colorAdjustments.postExposure.Override(-.3f);
        colorAdjustments.contrast.Override(30f);
        colorAdjustments.colorFilter.overrideState = false;
        colorAdjustments.hueShift.Override(0f);
        colorAdjustments.saturation.Override(-15f);

        foreach (VolumeComponent component in profile.components)
            EditorUtility.SetDirty(component);
        return profile;
    }

    private static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
    {
        if (profile.TryGet(out T component)) return component;
        component = profile.Add<T>(false);
        component.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
        AssetDatabase.AddObjectToAsset(component, profile);
        return component;
    }

    private static void EnsureFolder(string path)
    {
        string[] parts = path.Split('/');
        string current = parts[0];
        for (int index = 1; index < parts.Length; index++)
        {
            string next = current + "/" + parts[index];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[index]);
            current = next;
        }
    }
}
#endif
