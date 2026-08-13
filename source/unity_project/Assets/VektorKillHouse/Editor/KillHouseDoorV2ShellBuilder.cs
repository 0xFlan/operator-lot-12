#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class KillHouseDoorV2ShellBuilder
{
    public const string ShellPrefabPath = "Assets/VektorKillHouse/Native/Prefabs/PF_NATIVE_DoorV2_SHELL.prefab";
    public const string AudioBankPrefabPath = "Assets/VektorKillHouse/Native/Prefabs/PF_NATIVE_DoorV2_AUDIO_BANK.prefab";
    public const string DoorPhysicsMaterialPath = "Assets/VektorKillHouse/Native/Materials/MAT_NATIVE__doorMat.physicMaterial";

    private const string SourceGraphRelative = "../../evidence/level4_interior_door_graph.json";
    private const string AudioFolder = "Assets/VektorKillHouse/Native/Residential/DoorV2/Audio";

    private static readonly IReadOnlyDictionary<long, string> BreachedMeshNames = new Dictionary<long, string>
    {
        [4034] = "Door_exterior_paneled_cell.005_low",
        [3710] = "Door_exterior_paneled_cell.018_low",
        [4024] = "Door_exterior_paneled_cell.019_low",
        [3825] = "Door_exterior_paneled_cell.023_low",
        [3938] = "Door_exterior_paneled_cell.024_low",
        [3751] = "Door_exterior_paneled_cell.032_low",
        [3683] = "Door_exterior_paneled_cell.035_low",
        [3707] = "Door_exterior_paneled_cell.047_low",
        [4017] = "Door_exterior_paneled_cell.062_low",
        [3932] = "Door_exterior_paneled_cell.069_low",
        [3724] = "Door_exterior_paneled_cell.087_low",
        [3939] = "Door_exterior_paneled_cell.093_low"
    };

    [MenuItem("Vektor Kill House/Native/Rebuild DoorV2 Runtime Shell", priority = 13)]
    public static void BuildAll()
    {
        PhysicsMaterial doorMaterial = BuildPhysicsMaterial();
        BuildShell(doorMaterial);
        BuildAudioBank();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("[Vektor Kill House] Rebuilt exact-vanilla DoorV2 component shell and 47-clip wooden-door audio bank.");
    }

    public static GameObject LoadShell()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ShellPrefabPath);
        if (prefab == null) throw new FileNotFoundException("DoorV2 runtime shell has not been built.", ShellPrefabPath);
        return prefab;
    }

    public static GameObject LoadAudioBank()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AudioBankPrefabPath);
        if (prefab == null) throw new FileNotFoundException("DoorV2 audio bank has not been built.", AudioBankPrefabPath);
        return prefab;
    }

    private static PhysicsMaterial BuildPhysicsMaterial()
    {
        PhysicsMaterial material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(DoorPhysicsMaterialPath);
        if (material == null)
        {
            material = new PhysicsMaterial("MAT_NATIVE__doorMat");
            AssetDatabase.CreateAsset(material, DoorPhysicsMaterialPath);
        }
        material.dynamicFriction = .6f;
        material.staticFriction = .6f;
        material.bounciness = 0f;
        material.frictionCombine = PhysicsMaterialCombine.Average;
        material.bounceCombine = PhysicsMaterialCombine.Minimum;
        EditorUtility.SetDirty(material);
        return material;
    }

    private static void BuildShell(PhysicsMaterial doorMaterial)
    {
        string graphPath = Path.GetFullPath(Path.Combine(Application.dataPath, SourceGraphRelative));
        if (!File.Exists(graphPath)) throw new FileNotFoundException("Vanilla level4 DoorV2 graph audit is missing.", graphPath);
        JObject graph = JObject.Parse(File.ReadAllText(graphPath));
        JArray records = (JArray)graph["gameObjects"];
        if (records == null || records.Count != 119)
            throw new InvalidDataException("DoorV2 shell requires the audited 119-object vanilla interior graph.");

        Material intactMaterial = KillHouseNativeMaterialBuilder.Load("Door_White");
        Material breachedMaterial = KillHouseNativeMaterialBuilder.Load("Door_Breached");
        var objects = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        GameObject shellRoot = null;
        try
        {
            foreach (JObject record in records.Cast<JObject>().OrderBy(value => Depth(value.Value<string>("path"))))
            {
                string sourcePath = record.Value<string>("path");
                string sourceName = sourcePath.Split('/').Last();
                string parentPath = ParentPath(sourcePath);
                GameObject instance = new GameObject(parentPath.Length == 0 ? "PF_NATIVE_DoorV2_SHELL" : sourceName);
                if (parentPath.Length == 0)
                {
                    shellRoot = instance;
                    instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    instance.transform.localScale = Vector3.one;
                }
                else
                {
                    if (!objects.TryGetValue(parentPath, out GameObject parent))
                        throw new InvalidDataException("DoorV2 graph parent was not created: " + parentPath);
                    instance.transform.SetParent(parent.transform, false);
                    instance.transform.localPosition = ReadVector3(record["localPosition"]);
                    instance.transform.localRotation = ReadQuaternion(record["localRotation"]);
                    instance.transform.localScale = ReadVector3(record["localScale"], Vector3.one);
                }
                instance.layer = record.Value<int>("layer");
                objects[sourcePath] = instance;

                foreach (JObject component in ((JArray)record["components"]).Cast<JObject>().OrderBy(value => value.Value<int>("order")))
                    AddPortableComponent(instance, sourceName, component, intactMaterial, breachedMaterial, doorMaterial);
            }

            InstallRuggedAnimatedDoorVisual(shellRoot);

            foreach (JObject record in records.Cast<JObject>().OrderByDescending(value => Depth(value.Value<string>("path"))))
                objects[record.Value<string>("path")].SetActive(record.Value<bool>("activeSelf"));
            RemoveStaticShadowBlocker(shellRoot);
            shellRoot.SetActive(false);

            int primitiveMeshes = shellRoot.GetComponentsInChildren<MeshFilter>(true).Count(filter =>
                filter.sharedMesh != null && new[] { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" }.Contains(filter.sharedMesh.name));
            Transform destroyed = FindDescendant(shellRoot.transform, "Suburb Door Exploded");
            int breachedBodies = destroyed == null ? 0 : destroyed.GetComponentsInChildren<Rigidbody>(true).Length;
            Transform animatedPivot = FindDescendant(shellRoot.transform, "Door Pivot and rigidbody");
            Transform intactLeaf = FindDescendant(shellRoot.transform, "Door_interior");
            int staticDoorShadows = shellRoot.GetComponentsInChildren<Transform>(true)
                .Count(value => value.name.StartsWith("SHADOW BLOCKER Door_interior", StringComparison.Ordinal));
            if (primitiveMeshes != 0 || breachedBodies != 30 || staticDoorShadows != 0 ||
                animatedPivot == null || intactLeaf == null || !intactLeaf.IsChildOf(animatedPivot) ||
                shellRoot.GetComponentsInChildren<BoxCollider>(true).Length != 35 ||
                shellRoot.GetComponentsInChildren<AudioSource>(true).Length != 1)
                throw new InvalidDataException("Portable DoorV2 shell closure is incomplete: primitives=" + primitiveMeshes +
                    ", breachedBodies=" + breachedBodies + ", colliders=" +
                    shellRoot.GetComponentsInChildren<BoxCollider>(true).Length + ", staticDoorShadows=" +
                    staticDoorShadows + ".");

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(shellRoot, ShellPrefabPath);
            if (saved == null) throw new IOException("Could not save DoorV2 runtime shell prefab.");
            AssetDatabase.SetLabels(saved, new[]
            {
                "vektor-killhouse", "operator-native-reconstruction", "doorv2-runtime-shell",
                "official-graph-required-at-runtime", "no-visible-built-in-primitives",
                "vanilla-afghan-metal-door-visual", "animated-pivot-only-intact-leaf"
            });
        }
        finally
        {
            if (shellRoot != null) UnityEngine.Object.DestroyImmediate(shellRoot);
        }
    }

    private static void AddPortableComponent(GameObject instance, string sourceName, JObject component,
        Material intactMaterial, Material breachedMaterial, PhysicsMaterial doorMaterial)
    {
        string type = component.Value<string>("type");
        if (type == "Transform" || type == "MonoBehaviour") return;
        if (type == "MeshFilter")
        {
            Mesh mesh = ResolvePortableMesh(sourceName, component.Value<long>("meshPathId"));
            if (mesh != null) instance.AddComponent<MeshFilter>().sharedMesh = mesh;
            return;
        }
        if (type == "MeshRenderer")
        {
            MeshFilter filter = instance.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return;
            MeshRenderer renderer = instance.AddComponent<MeshRenderer>();
            renderer.enabled = component.Value<bool?>("enabled") ?? true;
            renderer.sharedMaterial = sourceName.StartsWith("Door_exterior_paneled_cell.", StringComparison.Ordinal)
                ? breachedMaterial : intactMaterial;
            renderer.shadowCastingMode = sourceName.StartsWith("SHADOW BLOCKER", StringComparison.Ordinal)
                ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            renderer.receiveShadows = !sourceName.StartsWith("SHADOW BLOCKER", StringComparison.Ordinal);
            return;
        }
        if (type == "BoxCollider")
        {
            BoxCollider collider = instance.AddComponent<BoxCollider>();
            collider.enabled = component.Value<bool?>("enabled") ?? true;
            collider.isTrigger = component.Value<bool?>("isTrigger") ?? false;
            collider.center = ReadVector3(component["center"]);
            collider.size = ReadVector3(component["size"], Vector3.one);
            collider.material = doorMaterial;
            return;
        }
        if (type == "Rigidbody")
        {
            Rigidbody body = instance.AddComponent<Rigidbody>();
            body.mass = component.Value<float?>("mass") ?? 1f;
            body.linearDamping = component.Value<float?>("linearDamping") ?? 0f;
            body.angularDamping = component.Value<float?>("angularDamping") ?? .05f;
            body.useGravity = component.Value<bool?>("useGravity") ?? true;
            body.isKinematic = component.Value<bool?>("isKinematic") ?? false;
            body.constraints = (RigidbodyConstraints)(component.Value<int?>("constraints") ?? 0);
            body.collisionDetectionMode = (CollisionDetectionMode)(component.Value<int?>("collisionDetection") ?? 0);
            return;
        }
        if (type == "AudioSource")
        {
            AudioSource source = instance.AddComponent<AudioSource>();
            source.playOnAwake = component.Value<bool?>("playOnAwake") ?? true;
            source.volume = component.Value<float?>("volume") ?? 1f;
            source.spatialBlend = 1f;
            source.dopplerLevel = 1f;
            source.minDistance = component.Value<float?>("minDistance") ?? 1f;
            source.maxDistance = component.Value<float?>("maxDistance") ?? 10f;
            source.rolloffMode = AudioRolloffMode.Custom;
        }
    }

    private static Mesh ResolvePortableMesh(string sourceName, long meshPathId)
    {
        string exact = null;
        if (string.Equals(sourceName, "Door_interior", StringComparison.Ordinal) ||
            sourceName.StartsWith("SHADOW BLOCKER Door_interior", StringComparison.Ordinal))
            exact = "Door_interior";
        else if (sourceName.StartsWith("Door_exterior_paneled_cell.", StringComparison.Ordinal))
            BreachedMeshNames.TryGetValue(meshPathId, out exact);
        if (string.IsNullOrEmpty(exact)) return null;

        string[] guids = AssetDatabase.FindAssets(exact + " t:Mesh", new[] { "Assets/VektorKillHouse/Native" });
        string path = guids.Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault(candidate =>
            candidate.IndexOf("/Generated/", StringComparison.OrdinalIgnoreCase) >= 0 &&
            string.Equals(Path.GetFileNameWithoutExtension(candidate), exact, StringComparison.Ordinal));
        Mesh mesh = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (mesh == null) throw new FileNotFoundException("Portable DoorV2 mesh is missing: " + exact);
        return mesh;
    }

    private static void BuildAudioBank()
    {
        string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { AudioFolder });
        AudioClip[] clips = guids.Select(AssetDatabase.GUIDToAssetPath).Select(AssetDatabase.LoadAssetAtPath<AudioClip>)
            .Where(clip => clip != null).OrderBy(clip => clip.name, StringComparer.Ordinal).ToArray();
        if (clips.Length != 47) throw new InvalidDataException("DoorV2 audio bank requires exactly 47 installed wooden-door clips; found " + clips.Length + ".");
        string[] requiredPrefixes = { "wooden door opening ", "wooden door locked ", "wooden door closing ", "wooden door thud ", "wooden door breach " };
        if (clips.Any(clip => !requiredPrefixes.Any(prefix => clip.name.StartsWith(prefix, StringComparison.Ordinal))))
            throw new InvalidDataException("DoorV2 audio bank contains a non-wooden-door clip.");

        GameObject root = new GameObject("PF_NATIVE_DoorV2_AUDIO_BANK");
        try
        {
            foreach (AudioClip clip in clips)
            {
                GameObject child = new GameObject("VANILLA_AUDIO_" + clip.name);
                child.transform.SetParent(root.transform, false);
                AudioSource source = child.AddComponent<AudioSource>();
                source.clip = clip;
                source.playOnAwake = false;
                source.enabled = false;
            }
            root.SetActive(false);
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, AudioBankPrefabPath);
            if (saved == null) throw new IOException("Could not save DoorV2 audio bank prefab.");
            AssetDatabase.SetLabels(saved, new[]
            {
                "vektor-killhouse", "operator-native-audio", "doorv2-audio-bank", "installed-game-assets-only"
            });
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static void InstallRuggedAnimatedDoorVisual(GameObject shellRoot)
    {
        Transform placeholder = FindDescendant(shellRoot.transform, "PLACEHOLDER DOOR MODEL");
        Transform interior = FindDescendant(shellRoot.transform, "Door_interior");
        BoxCollider reference = placeholder == null ? null : placeholder.GetComponent<BoxCollider>();
        MeshFilter interiorFilter = interior == null ? null : interior.GetComponent<MeshFilter>();
        MeshRenderer interiorRenderer = interior == null ? null : interior.GetComponent<MeshRenderer>();
        BoxCollider interiorCollider = interior == null ? null : interior.GetComponent<BoxCollider>();
        if (reference == null || interiorFilter == null || interiorRenderer == null || interiorCollider == null)
            throw new InvalidDataException("DoorV2 rugged visual installation lacks its reference collider or animated renderer.");

        Mesh ruggedMesh = FindGeneratedMesh("SM_Door_2_LOD0");
        interiorFilter.sharedMesh = ruggedMesh;
        interiorRenderer.sharedMaterial = KillHouseNativeMaterialBuilder.Load("MI_DoorsWindows");
        interior.localPosition = Vector3.zero;
        // The installed Afghan donor uses DoorPhysics Y=180 and mesh Y=90, for a net -90 degree
        // orientation relative to the hinge. Fit its real 1.30 m leaf to the official 0.976 m
        // physical aperture without changing the authoritative DoorV2 rigidbody/collider graph.
        interior.localRotation = Quaternion.Euler(0f, -90f, 0f);
        Bounds ruggedBounds = ruggedMesh.bounds;
        Vector3 target = reference.bounds.size;
        interior.localScale = new Vector3(
            target.z / Mathf.Max(.001f, ruggedBounds.size.x),
            target.y / Mathf.Max(.001f, ruggedBounds.size.y),
            target.x / Mathf.Max(.001f, ruggedBounds.size.z));
        interiorCollider.center = ruggedBounds.center;
        interiorCollider.size = ruggedBounds.size;

        Physics.SyncTransforms();
        Vector3 referenceCenter = reference.bounds.center;
        interior.position += referenceCenter - interiorRenderer.bounds.center;
        Physics.SyncTransforms();
        Vector3 residual = interiorRenderer.bounds.center - reference.bounds.center;
        Vector3 sizeResidual = interiorRenderer.bounds.size - reference.bounds.size;
        if (residual.magnitude > .006f || Mathf.Abs(sizeResidual.x) > .012f ||
            Mathf.Abs(sizeResidual.y) > .012f || Mathf.Abs(sizeResidual.z) > .012f)
            throw new InvalidDataException("DoorV2 rugged intact mesh did not align to the vanilla physical leaf.");
    }

    private static void RemoveStaticShadowBlocker(GameObject shellRoot)
    {
        Transform shadow = FindDescendant(shellRoot.transform, "SHADOW BLOCKER Door_interior_1");
        if (shadow == null)
            throw new InvalidDataException("DoorV2 source graph no longer contains the audited static shadow blocker.");
        UnityEngine.Object.DestroyImmediate(shadow.gameObject);
    }

    private static Mesh FindGeneratedMesh(string exactName)
    {
        string[] guids = AssetDatabase.FindAssets(exactName + " t:Mesh", new[] { "Assets/VektorKillHouse/Native" });
        string path = guids.Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault(candidate =>
            candidate.IndexOf("/Generated/", StringComparison.OrdinalIgnoreCase) >= 0 &&
            string.Equals(Path.GetFileNameWithoutExtension(candidate), exactName, StringComparison.Ordinal));
        Mesh mesh = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (mesh == null) throw new FileNotFoundException("Portable DoorV2 rugged mesh is missing: " + exactName);
        return mesh;
    }

    private static Transform FindDescendant(Transform root, string name)
    {
        return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(value => value.name == name);
    }

    private static int Depth(string path) => path.Count(character => character == '/');
    private static string ParentPath(string path)
    {
        int index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path.Substring(0, index);
    }

    private static Vector3 ReadVector3(JToken token, Vector3 fallback = default)
    {
        if (token == null) return fallback;
        return new Vector3(token.Value<float?>("x") ?? fallback.x,
            token.Value<float?>("y") ?? fallback.y, token.Value<float?>("z") ?? fallback.z);
    }

    private static Quaternion ReadQuaternion(JToken token)
    {
        if (token == null) return Quaternion.identity;
        return new Quaternion(token.Value<float?>("x") ?? 0f, token.Value<float?>("y") ?? 0f,
            token.Value<float?>("z") ?? 0f, token.Value<float?>("w") ?? 1f);
    }
}
#endif
