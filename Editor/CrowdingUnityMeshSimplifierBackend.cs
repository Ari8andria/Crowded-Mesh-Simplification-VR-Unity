#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace CrowdingAwareLOD.EditorTools
{
    /// <summary>
    /// Optional Unity-side decimation backend using Whinarn UnityMeshSimplifier.
    /// Implemented through reflection so the project compiles even if the package is not installed.
    ///
    /// Triangle-only version:
    /// - No CrowdingBudgetMetric.
    /// - The backend receives the same keepRatio as Blender.
    /// - Reports achieved triangle ratio only.
    /// </summary>
    public static class CrowdingUnityMeshSimplifierBackend
    {
        public static bool Build(CrowdingMeshLODBuildConfig config, string manifestPath)
        {
            if (config == null)
            {
                Debug.LogError("UnityMeshSimplifier backend: config is null.");
                return false;
            }

            if (!File.Exists(manifestPath))
            {
                Debug.LogError("Mesh manifest not found: " + manifestPath);
                return false;
            }

            UnityMeshSimplifierReflection simplifierApi = UnityMeshSimplifierReflection.TryCreate(config);
            if (simplifierApi == null)
                return false;

            string json = File.ReadAllText(manifestPath);
            CrowdingMeshDecimationManifest manifest = JsonUtility.FromJson<CrowdingMeshDecimationManifest>(json);

            if (manifest == null || manifest.meshSlots == null || manifest.meshSlots.Count == 0 || manifest.variants == null || manifest.variants.Count == 0)
            {
                Debug.LogError("Invalid or empty mesh manifest: " + manifestPath);
                return false;
            }

            List<string> manifestVariantKeys = manifest.variants
                .Where(v => v != null && !string.IsNullOrWhiteSpace(v.variantKey))
                .Select(v => v.variantKey)
                .Distinct()
                .OrderBy(k => k)
                .ToList();

            bool manifestContainsGlobalVariants = manifestVariantKeys.Any(CrowdingVariantKey.IsGlobalVariantKey);
            if (config.verboseLogs)
            {
                Debug.Log(
                    "UnityMeshSimplifier backend: manifest variants=" + manifest.variants.Count +
                    ", distinctKeys=" + manifestVariantKeys.Count +
                    ", containsGlobalVariants=" + manifestContainsGlobalVariants +
                    "\n" + string.Join("\n", manifestVariantKeys.Take(30)),
                    config);
            }

            Dictionary<string, CrowdingMeshSlotManifestRecord> slotsById = manifest.meshSlots
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.slotId))
                .GroupBy(s => s.slotId)
                .ToDictionary(g => g.Key, g => g.First());

            EnsureAssetFolder(Path.GetDirectoryName(config.variantLibraryAssetPath));

            string generatedMeshAssetRoot = GetGeneratedMeshAssetRoot(config);
            EnsureAssetFolder(generatedMeshAssetRoot);

            CrowdingMeshVariantLibrary library = AssetDatabase.LoadAssetAtPath<CrowdingMeshVariantLibrary>(config.variantLibraryAssetPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<CrowdingMeshVariantLibrary>();
                AssetDatabase.CreateAsset(library, config.variantLibraryAssetPath);
            }

            library.generatedFromManifest = config.meshManifestRelativePath;
            library.generatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            library.entries.Clear();

            string reportFolder = ResolveProjectPath(config.blenderOutputRelativeFolder);
            if (!Directory.Exists(reportFolder))
                Directory.CreateDirectory(reportFolder);

            List<ReportRow> reportRows = new List<ReportRow>();
            int generatedMeshes = 0;
            int skipped = 0;

            for (int v = 0; v < manifest.variants.Count; v++)
            {
                CrowdingMeshVariantManifestRecord variant = manifest.variants[v];
                if (variant == null || variant.meshRecords == null)
                    continue;

                for (int r = 0; r < variant.meshRecords.Count; r++)
                {
                    CrowdingMeshVariantSlotRecord rec = variant.meshRecords[r];
                    if (rec == null || string.IsNullOrWhiteSpace(rec.slotId))
                        continue;

                    if (!slotsById.TryGetValue(rec.slotId, out CrowdingMeshSlotManifestRecord slot) || slot == null || !slot.supported)
                    {
                        skipped++;
                        continue;
                    }

                    Mesh sourceMesh = ResolveSourceMesh(slot);
                    if (sourceMesh == null)
                    {
                        Debug.LogWarning("Could not resolve source mesh for slot: " + rec.slotId + " path=" + slot.unityMeshAssetPath + " mesh=" + slot.unityMeshName);
                        skipped++;
                        continue;
                    }

                    float keepRatio = Mathf.Clamp01(rec.keepRatio);
                    Mesh simplifiedMesh = simplifierApi.Simplify(sourceMesh, keepRatio, config);
                    if (simplifiedMesh == null)
                    {
                        Debug.LogWarning("UnityMeshSimplifier failed for slot: " + rec.slotId + " variant=" + variant.variantKey);
                        skipped++;
                        continue;
                    }

                    simplifiedMesh.name = CrowdingMeshSlotUtility.Sanitize(variant.variantKey + "__" + rec.slotId);

                    if (simplifiedMesh.vertexCount > 65535)
                        simplifiedMesh.indexFormat = IndexFormat.UInt32;

                    if (config.umsRecalculateNormals)
                        simplifiedMesh.RecalculateNormals();

                    if (config.umsRecalculateTangents && simplifiedMesh.uv != null && simplifiedMesh.uv.Length == simplifiedMesh.vertexCount)
                    {
                        try { simplifiedMesh.RecalculateTangents(); }
                        catch { /* Some meshes cannot generate tangents. */ }
                    }

                    if (config.umsRecalculateBounds)
                        simplifiedMesh.RecalculateBounds();

                    string assetPath = BuildMeshAssetPath(generatedMeshAssetRoot, variant.variantKey, rec.slotId);
                    Mesh meshAsset = SaveOrUpdateMeshAsset(simplifiedMesh, assetPath);

                    int finalTriangles = CountTriangles(meshAsset);
                    float achieved = rec.initialTriangles > 0 ? (float)finalTriangles / (float)rec.initialTriangles : 0f;

                    CrowdingMeshVariantEntry entry = new CrowdingMeshVariantEntry();
                    entry.variantKey = variant.variantKey;
                    entry.slotId = rec.slotId;
                    entry.objectId = rec.objectId;
                    entry.rootName = rec.rootName;
                    entry.childPath = rec.childPath;
                    entry.rendererType = rec.rendererType;
                    entry.requestedKeepRatio = keepRatio;
                    entry.initialTriangles = rec.initialTriangles;
                    entry.finalTriangles = finalTriangles;
                    entry.achievedKeepRatio = achieved;
                    entry.decimatedMesh = meshAsset;
                    library.AddOrReplaceEntry(entry);

                    reportRows.Add(new ReportRow
                    {
                        variantKey = variant.variantKey,
                        slotId = rec.slotId,
                        objectId = rec.objectId,
                        childPath = rec.childPath,
                        requestedKeepRatio = keepRatio,
                        initialTriangles = rec.initialTriangles,
                        finalTriangles = finalTriangles,
                        achievedKeepRatio = achieved,
                        sourceMeshPath = slot.unityMeshAssetPath,
                        sourceMeshName = slot.unityMeshName
                    });

                    generatedMeshes++;
                }
            }

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (config.assignLibraryToSelector)
            {
                if (config.meshSelector == null)
                    config.meshSelector = config.GetComponent<CrowdingSharedMeshLODSelector>();
                if (config.meshSelector == null)
                    config.meshSelector = Undo.AddComponent<CrowdingSharedMeshLODSelector>(config.gameObject);

                config.meshSelector.variantLibrary = library;
                EditorUtility.SetDirty(config.meshSelector);
            }

            WriteReport(Path.Combine(reportFolder, "crowding_unitymeshsimplifier_triangle_report.csv"), reportRows);

            List<string> libraryVariantKeys = library.GetDistinctVariantKeys();

            Debug.Log(
                "UnityMeshSimplifier QEM backend finished.\n" +
                "Library: " + config.variantLibraryAssetPath + "\n" +
                "Entries: " + library.entries.Count + "\n" +
                "Distinct variant keys: " + libraryVariantKeys.Count + "\n" +
                "Contains global variants: " + libraryVariantKeys.Any(CrowdingVariantKey.IsGlobalVariantKey) + "\n" +
                "Generated meshes: " + generatedMeshes + "\n" +
                "Skipped: " + skipped + "\n" +
                "Smart Link: " + config.umsEnableSmartLink + " | VertexLinkDistance=" + config.umsVertexLinkDistance,
                config);

            return library.entries.Count > 0;
        }

        private static Mesh ResolveSourceMesh(CrowdingMeshSlotManifestRecord slot)
        {
            if (slot == null || string.IsNullOrWhiteSpace(slot.unityMeshAssetPath))
                return null;

            Mesh exact = AssetDatabase.LoadAllAssetsAtPath(slot.unityMeshAssetPath)
                .OfType<Mesh>()
                .FirstOrDefault(m => m != null && m.name == slot.unityMeshName);
            if (exact != null)
                return exact;

            return AssetDatabase.LoadAssetAtPath<Mesh>(slot.unityMeshAssetPath);
        }

        private static string BuildMeshAssetPath(string generatedMeshAssetRoot, string variantKey, string slotId)
        {
            string folder = (generatedMeshAssetRoot.TrimEnd('/', '\\') + "/" + CrowdingMeshSlotUtility.Sanitize(variantKey)).Replace("\\", "/");
            EnsureAssetFolder(folder);
            return (folder + "/" + CrowdingMeshSlotUtility.Sanitize(slotId) + ".asset").Replace("\\", "/");
        }

        private static string GetGeneratedMeshAssetRoot(CrowdingMeshLODBuildConfig config)
        {
            // UnityMeshSimplifier writes Mesh .asset files directly. Therefore the output folder
            // must be an AssetDatabase path, not an absolute filesystem path.
            string folder = (config != null ? config.blenderOutputRelativeFolder : string.Empty) ?? string.Empty;
            folder = folder.Replace("\\", "/").Trim().TrimEnd('/');

            if (string.IsNullOrWhiteSpace(folder) || !folder.StartsWith("Assets/"))
            {
                string libraryFolder = config != null ? Path.GetDirectoryName(config.variantLibraryAssetPath) : null;
                if (string.IsNullOrWhiteSpace(libraryFolder))
                    libraryFolder = "Assets/CrowdingGeneratedLODs";

                folder = (libraryFolder.Replace("\\", "/").TrimEnd('/') + "/Meshes").Replace("\\", "/");
                Debug.LogWarning(
                    "UnityMeshSimplifier backend requires an Assets/... output folder for generated Mesh assets. " +
                    "Using: " + folder);
            }

            return folder;
        }

        private static Mesh SaveOrUpdateMeshAsset(Mesh mesh, string assetPath)
        {
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mesh, existing);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            AssetDatabase.CreateAsset(mesh, assetPath);
            return mesh;
        }

        private static int CountTriangles(Mesh mesh)
        {
            if (mesh == null)
                return 0;

            int count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                count += mesh.GetTriangles(i).Length / 3;
            return count;
        }

        private static void WriteReport(string path, List<ReportRow> rows)
        {
            using (StreamWriter sw = new StreamWriter(path))
            {
                sw.WriteLine("variantKey,slotId,objectId,childPath,requestedKeepRatio,initialTriangles,finalTriangles,achievedKeepRatio,sourceMeshPath,sourceMeshName");
                for (int i = 0; i < rows.Count; i++)
                {
                    ReportRow r = rows[i];
                    sw.WriteLine(string.Join(",", new string[]
                    {
                        Csv(r.variantKey),
                        Csv(r.slotId),
                        Csv(r.objectId),
                        Csv(r.childPath),
                        r.requestedKeepRatio.ToString(CultureInfo.InvariantCulture),
                        r.initialTriangles.ToString(CultureInfo.InvariantCulture),
                        r.finalTriangles.ToString(CultureInfo.InvariantCulture),
                        r.achievedKeepRatio.ToString(CultureInfo.InvariantCulture),
                        Csv(r.sourceMeshPath),
                        Csv(r.sourceMeshName)
                    }));
                }
            }
        }

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static string ResolveProjectPath(string relativeOrAbsolute)
        {
            if (Path.IsPathRooted(relativeOrAbsolute))
                return Path.GetFullPath(relativeOrAbsolute);
            return Path.GetFullPath(Path.Combine(GetProjectRoot(), relativeOrAbsolute));
        }

        private static void EnsureAssetFolder(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder))
                return;

            assetFolder = assetFolder.Replace("\\", "/");
            if (AssetDatabase.IsValidFolder(assetFolder))
                return;

            string[] parts = assetFolder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private class ReportRow
        {
            public string variantKey;
            public string slotId;
            public string objectId;
            public string childPath;
            public float requestedKeepRatio;
            public int initialTriangles;
            public int finalTriangles;
            public float achievedKeepRatio;
            public string sourceMeshPath;
            public string sourceMeshName;
        }

        private class UnityMeshSimplifierReflection
        {
            private readonly Type simplifierType;
            private readonly Type optionsType;
            private readonly object optionsInstance;

            private UnityMeshSimplifierReflection(Type simplifierType, Type optionsType, object optionsInstance)
            {
                this.simplifierType = simplifierType;
                this.optionsType = optionsType;
                this.optionsInstance = optionsInstance;
            }

            public static UnityMeshSimplifierReflection TryCreate(CrowdingMeshLODBuildConfig config)
            {
                Type simplifierType = FindType("UnityMeshSimplifier.MeshSimplifier");
                Type optionsType = FindType("UnityMeshSimplifier.SimplificationOptions");

                if (simplifierType == null || optionsType == null)
                {
                    Debug.LogError(
                        "UnityMeshSimplifier package not found. Install Whinarn UnityMeshSimplifier, " +
                        "or set Decimation Backend back to Blender Decimate.");
                    return null;
                }

                object options = Activator.CreateInstance(optionsType);
                SetMember(options, optionsType, "EnableSmartLink", config.umsEnableSmartLink);
                SetMember(options, optionsType, "VertexLinkDistance", config.umsVertexLinkDistance);
                SetMember(options, optionsType, "PreserveBorderEdges", config.umsPreserveBorderEdges);
                SetMember(options, optionsType, "PreserveUVSeamEdges", config.umsPreserveUVSeamEdges);
                SetMember(options, optionsType, "PreserveUVFoldoverEdges", config.umsPreserveUVFoldoverEdges);
                SetMember(options, optionsType, "PreserveSurfaceCurvature", config.umsPreserveSurfaceCurvature);
                SetMember(options, optionsType, "MaxIterationCount", config.umsMaxIterationCount);
                SetMember(options, optionsType, "Agressiveness", config.umsAgressiveness);

                return new UnityMeshSimplifierReflection(simplifierType, optionsType, options);
            }

            public Mesh Simplify(Mesh source, float quality, CrowdingMeshLODBuildConfig config)
            {
                if (source == null)
                    return null;

                quality = Mathf.Clamp01(quality);

                object simplifier = Activator.CreateInstance(simplifierType);

                SetMember(simplifier, simplifierType, "SimplificationOptions", optionsInstance);
                SetMember(simplifier, simplifierType, "Options", optionsInstance);

                MethodInfo initialize = FindMethod(simplifierType, "Initialize", typeof(Mesh));
                if (initialize == null)
                    throw new MissingMethodException("UnityMeshSimplifier.MeshSimplifier.Initialize(Mesh) was not found.");
                initialize.Invoke(simplifier, new object[] { source });

                // Some versions require options after Initialize.
                SetMember(simplifier, simplifierType, "SimplificationOptions", optionsInstance);
                SetMember(simplifier, simplifierType, "Options", optionsInstance);

                MethodInfo simplify = FindMethodByName(simplifierType, "SimplifyMesh");
                if (simplify == null)
                    throw new MissingMethodException("UnityMeshSimplifier.MeshSimplifier.SimplifyMesh(...) was not found.");

                ParameterInfo[] pars = simplify.GetParameters();
                object q = pars.Length > 0 && pars[0].ParameterType == typeof(double) ? (object)(double)quality : (object)quality;
                simplify.Invoke(simplifier, new object[] { q });

                MethodInfo toMesh = FindMethodByName(simplifierType, "ToMesh");
                if (toMesh == null)
                    throw new MissingMethodException("UnityMeshSimplifier.MeshSimplifier.ToMesh() was not found.");

                return toMesh.Invoke(simplifier, null) as Mesh;
            }

            private static Type FindType(string fullName)
            {
                Type t = Type.GetType(fullName);
                if (t != null)
                    return t;

                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    t = assemblies[i].GetType(fullName);
                    if (t != null)
                        return t;
                }
                return null;
            }

            private static MethodInfo FindMethod(Type type, string name, params Type[] parameterTypes)
            {
                return type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, parameterTypes, null);
            }

            private static MethodInfo FindMethodByName(Type type, string name)
            {
                return type.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == name);
            }

            private static void SetMember(object instance, Type type, string name, object value)
            {
                if (instance == null || type == null)
                    return;

                PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    object converted = ConvertValue(value, prop.PropertyType);
                    prop.SetValue(instance, converted, null);
                    return;
                }

                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    object converted = ConvertValue(value, field.FieldType);
                    field.SetValue(instance, converted);
                }
            }

            private static object ConvertValue(object value, Type targetType)
            {
                if (value == null)
                    return null;

                Type t = Nullable.GetUnderlyingType(targetType) ?? targetType;
                if (t.IsEnum)
                    return Enum.ToObject(t, value);

                if (t == typeof(float))
                    return Convert.ToSingle(value, CultureInfo.InvariantCulture);
                if (t == typeof(double))
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (t == typeof(int))
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                if (t == typeof(bool))
                    return Convert.ToBoolean(value, CultureInfo.InvariantCulture);

                return value;
            }
        }
    }
}
#endif
