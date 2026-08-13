#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class KillHouseGlbMeshImporter
{
    private const string NativeRoot = "Assets/VektorKillHouse/Native";
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;
    private static readonly HashSet<string> DirectResidentialFurnitureMeshes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Bed_queen", "Bookshelf", "Kitcabinet_full_fridge", "Kitcabinet_low_1x_A",
            "Kitchen_table_large", "Sidetable_A", "T_sink", "T_toilet", "Workdesk_solo"
        };

    [MenuItem("Vektor Kill House/Native/Import Targeted GLB Meshes", priority = 10)]
    public static void ImportAll()
    {
        string absoluteRoot = Path.GetFullPath(NativeRoot);
        string[] files = Directory.GetFiles(absoluteRoot, "*.glb", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            throw new FileNotFoundException("No targeted native GLB meshes exist under " + absoluteRoot + ".");

        int imported = 0;
        foreach (string file in files)
        {
            string sourcePath = ToAssetPath(file);
            string outputFolder = Path.GetDirectoryName(sourcePath).Replace('\\', '/') + "/Generated";
            EnsureFolder(outputFolder);
            string outputPath = outputFolder + "/" + Path.GetFileNameWithoutExtension(sourcePath) + ".asset";
            string normalizedSource = sourcePath.Replace('\\', '/');
            bool directResidentialMesh = normalizedSource.IndexOf(
                "/Native/Residential/Meshes/", StringComparison.OrdinalIgnoreCase) >= 0;
            string meshName = Path.GetFileNameWithoutExtension(file);
            // Couch_2seat is a custom UnityPy exact-channel export, not an AssetRipper GLB.
            // Its exporter already writes a Z reflection/reversed winding and preserves the
            // installed UV streams, so it must remain on the generic Z-inverse/no-V-flip path.
            bool exactUnityPyResidentialCompleteMesh = normalizedSource.IndexOf(
                "/Native/ResidentialComplete/Meshes/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                string.Equals(meshName, "Couch_2seat", StringComparison.Ordinal);
            if (string.Equals(meshName, "Couch_2seat", StringComparison.Ordinal) &&
                !exactUnityPyResidentialCompleteMesh)
                throw new InvalidDataException("Couch_2seat must come from its exact UnityPy ResidentialComplete export.");
            bool restoreInstalledFurnitureUvs = directResidentialMesh &&
                DirectResidentialFurnitureMeshes.Contains(meshName);
            Mesh parsed = Parse(file, meshName, directResidentialMesh, restoreInstalledFurnitureUvs);
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(outputPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(parsed, outputPath);
            }
            else
            {
                EditorUtility.CopySerialized(parsed, existing);
                UnityEngine.Object.DestroyImmediate(parsed);
                EditorUtility.SetDirty(existing);
            }
            imported++;
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("[Vektor Kill House] Imported " + imported + " targeted native GLB meshes.");
    }

    public static Mesh Parse(string absolutePath, string meshName)
    {
        return Parse(absolutePath, meshName, false, false);
    }

    private static Mesh Parse(string absolutePath, string meshName, bool reflectXInsteadOfZ,
        bool restoreInstalledFurnitureUvs)
    {
        byte[] bytes = File.ReadAllBytes(absolutePath);
        if (bytes.Length < 28 || ReadUInt(bytes, 0) != GlbMagic || ReadUInt(bytes, 4) != 2)
            throw new InvalidDataException("Unsupported GLB header: " + absolutePath);
        if (ReadUInt(bytes, 8) != bytes.Length)
            throw new InvalidDataException("GLB byte length does not match its header: " + absolutePath);

        int cursor = 12;
        JObject document = null;
        byte[] binary = null;
        while (cursor + 8 <= bytes.Length)
        {
            int chunkLength = checked((int)ReadUInt(bytes, cursor));
            uint chunkType = ReadUInt(bytes, cursor + 4);
            cursor += 8;
            if (cursor + chunkLength > bytes.Length)
                throw new InvalidDataException("GLB chunk overruns the file: " + absolutePath);
            if (chunkType == JsonChunk)
            {
                string json = System.Text.Encoding.UTF8.GetString(bytes, cursor, chunkLength).TrimEnd(' ', '\0');
                document = JObject.Parse(json);
            }
            else if (chunkType == BinChunk)
            {
                binary = new byte[chunkLength];
                Buffer.BlockCopy(bytes, cursor, binary, 0, chunkLength);
            }
            cursor += chunkLength;
        }
        if (document == null || binary == null)
            throw new InvalidDataException("GLB lacks JSON or BIN data: " + absolutePath);

        JArray gltfMeshes = (JArray)document["meshes"];
        if (gltfMeshes == null || gltfMeshes.Count == 0)
            throw new InvalidDataException("GLB contains no meshes: " + absolutePath);

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uvChannels = Enumerable.Range(0, 8).Select(_ => new List<Vector2>()).ToArray();
        var colors = new List<Color32>();
        var submeshes = new List<int[]>();
        bool allNormals = true;
        bool allTangents = true;
        bool allColors = true;
        bool[] allUvs = Enumerable.Repeat(true, 8).ToArray();

        foreach (JObject gltfMesh in gltfMeshes.Cast<JObject>())
        {
            foreach (JObject primitive in ((JArray)gltfMesh["primitives"]).Cast<JObject>())
            {
                JObject attributes = (JObject)primitive["attributes"];
                int positionAccessor = attributes.Value<int>("POSITION");
                Vector3[] sourcePositions = ReadVector3(document, binary, positionAccessor, true);
                Vector3[] sourceNormals = attributes["NORMAL"] != null
                    ? ReadVector3(document, binary, attributes.Value<int>("NORMAL"), true)
                    : null;
                Vector4[] sourceTangents = attributes["TANGENT"] != null
                    ? ReadVector4(document, binary, attributes.Value<int>("TANGENT"), true)
                    : null;
                Vector2[][] sourceUvs = new Vector2[8][];
                for (int channel = 0; channel < sourceUvs.Length; channel++)
                {
                    string semantic = "TEXCOORD_" + channel;
                    sourceUvs[channel] = attributes[semantic] != null
                        ? ReadVector2(document, binary, attributes.Value<int>(semantic))
                        : null;
                }
                Color32[] sourceColors = attributes["COLOR_0"] != null
                    ? ReadColor32(document, binary, attributes.Value<int>("COLOR_0"))
                    : null;
                int[] sourceIndices = ReadIndices(document, binary, primitive.Value<int>("indices"));
                int vertexOffset = vertices.Count;

                // AssetRipper's direct residential GLBs already contain the glTF X reflection;
                // reflecting Z again rotates asymmetric furniture 180 degrees. Restore the exact
                // installed Unity mesh by undoing the X reflection. Other targeted GLB families
                // retain their already-proven Z-handedness conversion.
                vertices.AddRange(sourcePositions.Select(value => reflectXInsteadOfZ
                    ? new Vector3(-value.x, value.y, value.z)
                    : new Vector3(value.x, value.y, -value.z)));
                if (sourceNormals != null)
                    normals.AddRange(sourceNormals.Select(value => reflectXInsteadOfZ
                        ? new Vector3(-value.x, value.y, value.z)
                        : new Vector3(value.x, value.y, -value.z)));
                else
                {
                    allNormals = false;
                    normals.AddRange(Enumerable.Repeat(Vector3.zero, sourcePositions.Length));
                }
                if (sourceTangents != null)
                    tangents.AddRange(sourceTangents.Select(value => reflectXInsteadOfZ
                        ? new Vector4(-value.x, value.y, value.z, -value.w)
                        : new Vector4(value.x, value.y, -value.z, -value.w)));
                else
                {
                    allTangents = false;
                    tangents.AddRange(Enumerable.Repeat(Vector4.zero, sourcePositions.Length));
                }
                for (int channel = 0; channel < sourceUvs.Length; channel++)
                {
                    if (sourceUvs[channel] != null)
                    {
                        // AssetRipper's direct level4 residential-root GLBs store glTF-style
                        // vertically inverted UVs while the separately exported PNGs retain the
                        // installed Unity texture orientation byte-for-byte. Restore only the nine
                        // complete retained root furniture families. ResidentialHierarchy child
                        // GLBs already match the installed UV streams and must remain unchanged.
                        uvChannels[channel].AddRange(restoreInstalledFurnitureUvs
                            ? sourceUvs[channel].Select(value => new Vector2(value.x, 1f - value.y))
                            : sourceUvs[channel]);
                    }
                    else
                    {
                        allUvs[channel] = false;
                        uvChannels[channel].AddRange(Enumerable.Repeat(Vector2.zero, sourcePositions.Length));
                    }
                }
                if (sourceColors != null)
                    colors.AddRange(sourceColors);
                else
                {
                    allColors = false;
                    colors.AddRange(Enumerable.Repeat(new Color32(255, 255, 255, 255), sourcePositions.Length));
                }

                int[] triangles = new int[sourceIndices.Length];
                for (int index = 0; index < sourceIndices.Length; index += 3)
                {
                    triangles[index] = vertexOffset + sourceIndices[index];
                    triangles[index + 1] = vertexOffset + sourceIndices[index + 2];
                    triangles[index + 2] = vertexOffset + sourceIndices[index + 1];
                }
                submeshes.Add(triangles);
            }
        }

        Mesh result = new Mesh
        {
            name = meshName,
            indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
        };
        result.SetVertices(vertices);
        if (allNormals) result.SetNormals(normals);
        if (allTangents) result.SetTangents(tangents);
        for (int channel = 0; channel < uvChannels.Length; channel++)
            if (allUvs[channel]) result.SetUVs(channel, uvChannels[channel]);
        if (allColors) result.SetColors(colors);
        result.subMeshCount = submeshes.Count;
        for (int index = 0; index < submeshes.Count; index++)
            result.SetTriangles(submeshes[index], index, false);
        if (!allNormals) result.RecalculateNormals();
        if (!allTangents && allUvs[0]) result.RecalculateTangents();
        result.RecalculateBounds();
        result.UploadMeshData(false);
        return result;
    }

    private static Vector2[] ReadVector2(JObject document, byte[] binary, int accessorIndex)
    {
        JObject accessor = GetAccessor(document, accessorIndex, "VEC2", 5126);
        Vector2[] values = new Vector2[accessor.Value<int>("count")];
        for (int i = 0; i < values.Length; i++)
        {
            int offset = GetElementOffset(document, accessor, i, 8);
            values[i] = new Vector2(ReadFloat(binary, offset), ReadFloat(binary, offset + 4));
        }
        return values;
    }

    private static Color32[] ReadColor32(JObject document, byte[] binary, int accessorIndex)
    {
        JObject accessor = GetAccessor(document, accessorIndex, "VEC4", 5121);
        if (!(accessor.Value<bool?>("normalized") ?? false))
            throw new InvalidDataException("Unsigned-byte COLOR_0 must be normalized at accessor " + accessorIndex + ".");
        Color32[] values = new Color32[accessor.Value<int>("count")];
        for (int i = 0; i < values.Length; i++)
        {
            int offset = GetElementOffset(document, accessor, i, 4);
            values[i] = new Color32(binary[offset], binary[offset + 1], binary[offset + 2], binary[offset + 3]);
        }
        return values;
    }

    private static Vector3[] ReadVector3(JObject document, byte[] binary, int accessorIndex, bool requireFloat)
    {
        JObject accessor = GetAccessor(document, accessorIndex, "VEC3", requireFloat ? 5126 : -1);
        Vector3[] values = new Vector3[accessor.Value<int>("count")];
        for (int i = 0; i < values.Length; i++)
        {
            int offset = GetElementOffset(document, accessor, i, 12);
            values[i] = new Vector3(ReadFloat(binary, offset), ReadFloat(binary, offset + 4), ReadFloat(binary, offset + 8));
        }
        return values;
    }

    private static Vector4[] ReadVector4(JObject document, byte[] binary, int accessorIndex, bool requireFloat)
    {
        JObject accessor = GetAccessor(document, accessorIndex, "VEC4", requireFloat ? 5126 : -1);
        Vector4[] values = new Vector4[accessor.Value<int>("count")];
        for (int i = 0; i < values.Length; i++)
        {
            int offset = GetElementOffset(document, accessor, i, 16);
            values[i] = new Vector4(ReadFloat(binary, offset), ReadFloat(binary, offset + 4),
                ReadFloat(binary, offset + 8), ReadFloat(binary, offset + 12));
        }
        return values;
    }

    private static int[] ReadIndices(JObject document, byte[] binary, int accessorIndex)
    {
        JObject accessor = GetAccessor(document, accessorIndex, "SCALAR", -1);
        int componentType = accessor.Value<int>("componentType");
        int componentSize = componentType == 5121 ? 1 : componentType == 5123 ? 2 : componentType == 5125 ? 4 : 0;
        if (componentSize == 0)
            throw new InvalidDataException("Unsupported GLB index component type " + componentType + ".");
        int[] values = new int[accessor.Value<int>("count")];
        for (int i = 0; i < values.Length; i++)
        {
            int offset = GetElementOffset(document, accessor, i, componentSize);
            values[i] = componentType == 5121 ? binary[offset] :
                componentType == 5123 ? BitConverter.ToUInt16(binary, offset) : checked((int)BitConverter.ToUInt32(binary, offset));
        }
        return values;
    }

    private static JObject GetAccessor(JObject document, int index, string expectedType, int expectedComponentType)
    {
        JObject accessor = (JObject)((JArray)document["accessors"])[index];
        if (!string.Equals(accessor.Value<string>("type"), expectedType, StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected GLB accessor type at index " + index + ".");
        if (expectedComponentType >= 0 && accessor.Value<int>("componentType") != expectedComponentType)
            throw new InvalidDataException("Unexpected GLB accessor component type at index " + index + ".");
        if (accessor["sparse"] != null)
            throw new InvalidDataException("Sparse GLB accessors are not supported by this targeted importer.");
        return accessor;
    }

    private static int GetElementOffset(JObject document, JObject accessor, int elementIndex, int packedSize)
    {
        JObject view = (JObject)((JArray)document["bufferViews"])[accessor.Value<int>("bufferView")];
        int start = view.Value<int?>("byteOffset") ?? 0;
        start += accessor.Value<int?>("byteOffset") ?? 0;
        int stride = view.Value<int?>("byteStride") ?? packedSize;
        return checked(start + elementIndex * stride);
    }

    private static float ReadFloat(byte[] bytes, int offset) => BitConverter.ToSingle(bytes, offset);
    private static uint ReadUInt(byte[] bytes, int offset) => BitConverter.ToUInt32(bytes, offset);

    private static string ToAssetPath(string absolutePath)
    {
        string normalized = absolutePath.Replace('\\', '/');
        string dataPath = Application.dataPath.Replace('\\', '/');
        if (!normalized.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native asset is outside this Unity project: " + absolutePath);
        return "Assets/" + normalized.Substring(dataPath.Length + 1);
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
