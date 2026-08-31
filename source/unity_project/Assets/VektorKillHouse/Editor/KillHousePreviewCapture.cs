#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class KillHousePreviewCapture
{
    private const int Width = 1600;
    private const int Height = 900;
    private const string BatchCaptureEnvironmentVariable = "VEKTOR_KILLHOUSE_BATCH_CAPTURE";
    private static bool environmentBatchQueued;
    private static string environmentBatchRequest;

    [InitializeOnLoadMethod]
    private static void QueueRequestedBatchCapture()
    {
        string request = Environment.GetEnvironmentVariable(BatchCaptureEnvironmentVariable);
        if (environmentBatchQueued ||
            (!string.Equals(request, "KH01", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(request, "REBUILD_AND_CAPTURE_KH01", StringComparison.OrdinalIgnoreCase)))
            return;
        environmentBatchQueued = true;
        environmentBatchRequest = request;
        EditorApplication.update += RunRequestedBatchCaptureWhenReady;
    }

    private static void RunRequestedBatchCaptureWhenReady()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        EditorApplication.update -= RunRequestedBatchCaptureWhenReady;
        Environment.SetEnvironmentVariable(BatchCaptureEnvironmentVariable, null);
        if (string.Equals(environmentBatchRequest, "REBUILD_AND_CAPTURE_KH01",
                StringComparison.OrdinalIgnoreCase))
            RebuildScenesBundlesAndCaptureKh01Batch();
        else
            CaptureKh01FurnitureBatch();
    }

    public static void RebuildScenesBundlesAndCaptureKh01Batch()
    {
        try
        {
            KillHouseVariantBuilder.BuildAll();
            KillHouseBundleBuilder.Build();
            CaptureScene("Assets/VektorKillHouse/Scenes/KH01_CircuitHouse.unity");
            Debug.Log("[Vektor Kill House] Rebuilt ten scenes and proof bundles, then captured KH01 visual evidence.");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private sealed class ReviewMaterialScope : IDisposable
    {
        private readonly Dictionary<Renderer, Material[]> originals = new Dictionary<Renderer, Material[]>();
        private readonly List<Material> temporary = new List<Material>();

        public ReviewMaterialScope(IEnumerable<Renderer> renderers)
        {
            Shader standard = Shader.Find("Standard");
            if (standard == null) throw new InvalidDataException("Built-in Standard shader is unavailable for review capture.");
            foreach (Renderer renderer in renderers)
            {
                originals[renderer] = renderer.sharedMaterials;
                Material[] replacements = renderer.sharedMaterials.Select(source => Build(source, standard)).ToArray();
                renderer.sharedMaterials = replacements;
            }
        }

        private Material Build(Material source, Shader standard)
        {
            var material = new Material(standard) { hideFlags = HideFlags.HideAndDontSave };
            temporary.Add(material);
            material.name = "REVIEW_ONLY_" + (source == null ? "Fallback" : source.name);
            material.color = ReadColor(source);
            string textureProperty = FindTextureProperty(source);
            Texture texture = string.IsNullOrEmpty(textureProperty) ? null : source.GetTexture(textureProperty);
            if (texture != null)
            {
                material.mainTexture = texture;
                material.mainTextureScale = source.GetTextureScale(textureProperty);
                material.mainTextureOffset = source.GetTextureOffset(textureProperty);
            }
            material.SetFloat("_Glossiness", source != null && source.HasProperty("_Smoothness") ? source.GetFloat("_Smoothness") : .18f);
            material.SetFloat("_Metallic", source != null && source.HasProperty("_Metallic") ? source.GetFloat("_Metallic") : 0f);
            return material;
        }

        private static Color ReadColor(Material source)
        {
            if (source == null) return new Color(.58f, .58f, .58f, 1f);
            if (source.HasProperty("_BaseColor")) return source.GetColor("_BaseColor");
            if (source.HasProperty("_Color")) return source.GetColor("_Color");
            return Color.white;
        }

        private static string FindTextureProperty(Material source)
        {
            if (source == null) return null;
            string[] names = { "_BaseColorMap", "_BaseMap", "_MainTex", "_UnlitColorMap" };
            foreach (string name in names)
                if (source.HasProperty(name) && source.GetTexture(name) != null) return name;
            return null;
        }

        public void Dispose()
        {
            foreach (KeyValuePair<Renderer, Material[]> pair in originals)
                if (pair.Key != null) pair.Key.sharedMaterials = pair.Value;
            foreach (Material material in temporary)
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
        }
    }

    [MenuItem("Vektor Kill House/Review/Capture Representative Scenes", priority = 80)]
    public static void CaptureRepresentativeScenes()
    {
        string[] scenes =
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
        foreach (string scene in scenes) CaptureScene(scene);
        Debug.Log("[Vektor Kill House] Representative native-asset review captures completed.");
    }

    public static void CaptureRepresentativeScenesBatch()
    {
        try
        {
            CaptureRepresentativeScenes();
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    public static void CaptureKh01FurnitureBatch()
    {
        try
        {
            CaptureScene("Assets/VektorKillHouse/Scenes/KH01_CircuitHouse.unity");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private static void CaptureScene(string scenePath)
    {
        EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        string sceneName = Path.GetFileNameWithoutExtension(scenePath);
        Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        Light[] lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        var lightIntensities = lights.ToDictionary(light => light, light => light.intensity);
        foreach (Light light in lights)
        {
            if (light.type == LightType.Spot && light.name.StartsWith("ROOM_LOCAL_FIXTURE_LIGHT_", StringComparison.Ordinal))
                light.intensity = light.enabled ? 1.15f : 0f;
            else if (light.type == LightType.Directional)
                light.intensity = 0f;
        }

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        RenderSettings.ambientIntensity = 0f;
        RenderSettings.reflectionIntensity = 0f;
        RenderSettings.skybox = null;
        RenderSettings.fog = false;

        using (new ReviewMaterialScope(renderers))
        {
            Transform[] roofs = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(item => item.name == "NATIVE_WarehouseRoof").ToArray();
            foreach (Transform roof in roofs) roof.gameObject.SetActive(false);
            CaptureTopDown(sceneName, renderers.Where(renderer => renderer.gameObject.activeInHierarchy));
            foreach (Transform roof in roofs) roof.gameObject.SetActive(true);
            CaptureFirstPerson(sceneName);
            CaptureMotifView(sceneName);
            CaptureWarehouseView(sceneName);
            CaptureCeilingFixtureView(sceneName);
            CaptureBedView(sceneName);
            CaptureFurnitureView(sceneName);
        }

        foreach (KeyValuePair<Light, float> pair in lightIntensities)
            if (pair.Key != null) pair.Key.intensity = pair.Value;
    }

    private static void CaptureTopDown(string sceneName, IEnumerable<Renderer> renderers)
    {
        Bounds bounds = default;
        bool initialized = false;
        foreach (Renderer renderer in renderers)
        {
            if (!initialized) { bounds = renderer.bounds; initialized = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        if (!initialized) throw new InvalidDataException(sceneName + " has no visible renderers.");

        GameObject cameraObject = new GameObject("REVIEW_ONLY_TOPDOWN_CAMERA") { hideFlags = HideFlags.HideAndDontSave };
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = Math.Max(bounds.size.z * .58f, bounds.size.x * .33f);
        camera.transform.position = new Vector3(bounds.center.x, bounds.max.y + 30f, bounds.center.z);
        camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.055f, .06f, .07f);
        camera.nearClipPlane = .1f;
        camera.farClipPlane = 100f;
        Render(camera, sceneName + "_topdown.png");
        UnityEngine.Object.DestroyImmediate(cameraObject);
    }

    private static void CaptureFirstPerson(string sceneName)
    {
        GameObject cameraObject = new GameObject("REVIEW_ONLY_FIRSTPERSON_CAMERA") { hideFlags = HideFlags.HideAndDontSave };
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 74f;
        camera.transform.position = new Vector3(3.35f, 1.58f, -.55f);
        camera.transform.rotation = Quaternion.Euler(-6f, 88f, 0f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.035f, .04f, .045f);
        camera.nearClipPlane = .08f;
        camera.farClipPlane = 80f;
        Render(camera, sceneName + "_firstperson.png");
        UnityEngine.Object.DestroyImmediate(cameraObject);
    }

    private static void CaptureWarehouseView(string sceneName)
    {
        Transform roof = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude,
            FindObjectsSortMode.None).Single(item => item.name == "NATIVE_WarehouseRoof");
        Renderer[] roofRenderers = roof.GetComponentsInChildren<Renderer>(true);
        Bounds roofBounds = roofRenderers[0].bounds;
        foreach (Renderer renderer in roofRenderers.Skip(1)) roofBounds.Encapsulate(renderer.bounds);

        GameObject cameraObject = new GameObject("REVIEW_ONLY_WAREHOUSE_CAMERA") { hideFlags = HideFlags.HideAndDontSave };
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 82f;
        Vector3 target = new Vector3(roofBounds.center.x, 6.5f, roofBounds.center.z);
        camera.transform.position = new Vector3(roofBounds.center.x - 5f, 1.65f, roofBounds.center.z - 5f);
        camera.transform.rotation = Quaternion.LookRotation((target - camera.transform.position).normalized, Vector3.up);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = .08f;
        camera.farClipPlane = 100f;
        Render(camera, sceneName + "_warehouse.png");
        UnityEngine.Object.DestroyImmediate(cameraObject);
    }

    private static void CaptureCeilingFixtureView(string sceneName)
    {
        Light fixtureLight = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude,
                FindObjectsSortMode.None)
            .FirstOrDefault(light => light.name.StartsWith("ROOM_LOCAL_FIXTURE_LIGHT_",
                    StringComparison.Ordinal) &&
                light.transform.parent != null &&
                light.transform.parent.name.EndsWith("_STATE_LIT", StringComparison.Ordinal));
        if (fixtureLight == null)
            throw new InvalidDataException(sceneName + " has no active lit ceiling fixture for review.");

        string suffix = fixtureLight.name.Substring("ROOM_LOCAL_FIXTURE_LIGHT_".Length);
        Transform fixture = fixtureLight.transform.parent.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(item => string.Equals(item.name,
                "NATIVE_Lamp_fluorescent_B_" + suffix, StringComparison.Ordinal));
        if (fixture == null)
            throw new InvalidDataException(sceneName + " has no visual paired with its lit ceiling fixture.");

        Renderer[] renderers = fixture.GetComponentsInChildren<Renderer>(true);
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);

        // The general review pass intentionally removes HDRP emission so the room geometry is
        // readable.  That makes a correctly mounted, unlit fluorescent housing almost black
        // against the corrugated roof and can hide the very occlusion this close-up is meant to
        // prove.  Give only the selected fixture a temporary high-contrast emissive review
        // material; this never touches the authored scene or shipped bundle.
        Material[][] reviewMaterials = renderers.Select(renderer => renderer.sharedMaterials).ToArray();
        Shader reviewShader = Shader.Find("Standard");
        if (reviewShader == null)
            throw new InvalidDataException("Built-in Standard shader is unavailable for ceiling-fixture review.");
        Material fixtureReviewMaterial = new Material(reviewShader)
        {
            name = "REVIEW_ONLY_VISIBLE_CEILING_FIXTURE",
            color = Color.white,
            hideFlags = HideFlags.HideAndDontSave
        };
        fixtureReviewMaterial.EnableKeyword("_EMISSION");
        fixtureReviewMaterial.SetColor("_EmissionColor", Color.white * 8f);
        fixtureReviewMaterial.SetFloat("_Glossiness", .15f);
        foreach (Renderer renderer in renderers)
            renderer.sharedMaterials = Enumerable.Repeat(fixtureReviewMaterial,
                Mathf.Max(1, renderer.sharedMaterials.Length)).ToArray();

        GameObject cameraObject = new GameObject("REVIEW_ONLY_CEILING_FIXTURE_CAMERA")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 52f;
        Vector3 target = bounds.center;
        camera.transform.position = target + Vector3.down * 3.25f + Vector3.back * .8f;
        camera.transform.rotation = Quaternion.LookRotation(
            (target - camera.transform.position).normalized,
            Vector3.up);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = .05f;
        camera.farClipPlane = 25f;
        try
        {
            Debug.Log("[Vektor Kill House] Ceiling-fixture review target=" + fixture.name +
                      ", bounds=" + bounds + ", camera=" + camera.transform.position + ".");
            Render(camera, sceneName + "_ceiling-fixture.png");
        }
        finally
        {
            for (int index = 0; index < renderers.Length; index++)
                if (renderers[index] != null) renderers[index].sharedMaterials = reviewMaterials[index];
            UnityEngine.Object.DestroyImmediate(fixtureReviewMaterial);
            UnityEngine.Object.DestroyImmediate(cameraObject);
        }
    }

    private static void CaptureBedView(string sceneName)
    {
        Transform bed = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude,
            FindObjectsSortMode.None).FirstOrDefault(item => item.name == "NATIVE_Bed");
        if (bed == null) return;
        Renderer[] bedRenderers = bed.GetComponentsInChildren<Renderer>(true);
        Bounds bounds = bedRenderers[0].bounds;
        foreach (Renderer renderer in bedRenderers.Skip(1)) bounds.Encapsulate(renderer.bounds);

        GameObject cameraObject = new GameObject("REVIEW_ONLY_BED_CAMERA") { hideFlags = HideFlags.HideAndDontSave };
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 72f;
        Vector3 target = bounds.center + Vector3.up * .2f;
        // Direct installed level4 hierarchy proof places Bed_queen's pillows/headboard at local
        // -Z, so the retained root's local +Z points into the room. Review from that proven room
        // side; using -forward would place the inspection camera behind the owned partition.
        camera.transform.position = target + bed.forward * 1.8f + Vector3.up * .85f;
        camera.transform.rotation = Quaternion.LookRotation((target - camera.transform.position).normalized, Vector3.up);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = .08f;
        camera.farClipPlane = 35f;
        Render(camera, sceneName + "_bed.png");
        UnityEngine.Object.DestroyImmediate(cameraObject);
    }

    private static void CaptureMotifView(string sceneName)
    {
        Transform feature = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .FirstOrDefault(item => item.name.StartsWith("NATIVE_InteriorSplitWall_", StringComparison.Ordinal) ||
                                    item.name.StartsWith("NATIVE_LowDivider_", StringComparison.Ordinal) ||
                                    item.name.StartsWith("NATIVE_OfficePartition_", StringComparison.Ordinal));
        if (feature == null) throw new InvalidDataException(sceneName + " has no spatial feature for motif review.");

        Transform featureGroup = feature.parent;
        Renderer[] renderers = featureGroup.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) throw new InvalidDataException(sceneName + " motif group has no renderer.");
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);

        GameObject cameraObject = new GameObject("REVIEW_ONLY_MOTIF_CAMERA") { hideFlags = HideFlags.HideAndDontSave };
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 78f;
        Vector3 target = new Vector3(bounds.center.x, Mathf.Min(1.35f, bounds.max.y), bounds.center.z);
        camera.transform.position = target + new Vector3(3f, .25f, -3f);
        camera.transform.rotation = Quaternion.LookRotation((target - camera.transform.position).normalized, Vector3.up);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.035f, .04f, .045f);
        camera.nearClipPlane = .08f;
        camera.farClipPlane = 40f;
        Render(camera, sceneName + "_motif.png");
        UnityEngine.Object.DestroyImmediate(cameraObject);
    }

    private static void CaptureFurnitureView(string sceneName)
    {
        Transform bookcase = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude,
            FindObjectsSortMode.None).FirstOrDefault(item => item.name == "NATIVE_Bookcase");
        if (bookcase == null) return;
        Renderer[] renderers = bookcase.parent.GetComponentsInChildren<Renderer>(true)
            .Where(renderer => renderer.transform.IsChildOf(bookcase) ||
                               renderer.name == "NATIVE_Books").ToArray();
        if (renderers.Length == 0) return;
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);

        GameObject cameraObject = new GameObject("REVIEW_ONLY_FURNITURE_CAMERA") { hideFlags = HideFlags.HideAndDontSave };
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 68f;
        Vector3 target = bounds.center;
        camera.transform.position = target + bookcase.forward * 2.2f + Vector3.up * .25f;
        camera.transform.rotation = Quaternion.LookRotation((target - camera.transform.position).normalized, Vector3.up);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = .08f;
        camera.farClipPlane = 30f;
        Render(camera, sceneName + "_furniture.png");
        UnityEngine.Object.DestroyImmediate(cameraObject);
    }

    private static void Render(Camera camera, string fileName)
    {
        GameObject reviewLightObject = new GameObject("REVIEW_ONLY_CAMERA_INSPECTION_LIGHT")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        reviewLightObject.transform.SetParent(camera.transform, false);
        Light reviewLight = reviewLightObject.AddComponent<Light>();
        reviewLight.type = LightType.Spot;
        reviewLight.range = 55f;
        reviewLight.spotAngle = 125f;
        reviewLight.intensity = .55f;
        reviewLight.shadows = LightShadows.None;
        RenderTexture target = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32);
        target.antiAliasing = 4;
        RenderTexture previous = RenderTexture.active;
        camera.targetTexture = target;
        camera.Render();
        RenderTexture.active = target;
        var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        texture.Apply(false, false);
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "evidence", "visual"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, fileName), texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
        camera.targetTexture = null;
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(target);
        UnityEngine.Object.DestroyImmediate(reviewLightObject);
    }
}
#endif
