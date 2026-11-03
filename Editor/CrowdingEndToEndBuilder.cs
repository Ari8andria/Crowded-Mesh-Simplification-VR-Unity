#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace CrowdingAwareLOD.EditorTools
{
    /// <summary>
    /// One-click-ish offline builder for the full crowding-aware LOD pipeline:
    /// Unity scene -> CID FBX + manifest JSON -> Blender decimation -> Unity FBX import -> selector population.
    ///
    /// Requirements:
    /// - Add CrowdingEndToEndBuildConfig in the scene.
    /// - Install Unity FBX Exporter package if you want automatic FBX export.
    /// - Provide a valid Blender executable path in the config.
    /// - Use blender_generate_crowding_lods_cli.py from the Blender folder.
    /// </summary>
    public static class CrowdingEndToEndBuilder
    {
        private const string VariantRootPrefix = "GeneratedCrowdingVariant_";

        [MenuItem("Crowding Pipeline/End To End/Run Full Unity-Blender-Unity Build")]
        public static void RunFullBuildFromMenu()
        {
            CrowdingEndToEndBuildConfig config = ResolveConfigFromSelectionOrScene();
            if (config == null)
            {
                Debug.LogError("No CrowdingEndToEndBuildConfig found. Add it to the same GameObject as CrowdingAwarePipelineController.");
                return;
            }

            RunFullBuild(config);
        }

        [MenuItem("Crowding Pipeline/End To End/1 Export CID FBX And Manifest Only")]
        public static void ExportFbxAndManifestOnlyFromMenu()
        {
            CrowdingEndToEndBuildConfig config = ResolveConfigFromSelectionOrScene();
            if (config == null)
            {
                Debug.LogError("No CrowdingEndToEndBuildConfig found.");
                return;
            }

            if (!PrepareSceneAndExport(config, out string fbxPath, out string manifestPath))
                return;

            Debug.Log("Export-only step finished.\nFBX: " + fbxPath + "\nManifest: " + manifestPath);
        }

        [MenuItem("Crowding Pipeline/End To End/2 Run Blender Only")]
        public static void RunBlenderOnlyFromMenu()
        {
            CrowdingEndToEndBuildConfig config = ResolveConfigFromSelectionOrScene();
            if (config == null)
            {
                Debug.LogError("No CrowdingEndToEndBuildConfig found.");
                return;
            }

            string projectRoot = GetProjectRoot();
            string fbxPath = ResolveProjectPath(config.fbxExportRelativePath);
            string manifestPath = ResolveProjectPath(config.manifestRelativePath);
            string outputPath = ResolveProjectPath(config.blenderOutputRelativePath);
            string scriptPath = ResolveProjectPath(config.blenderScriptPath);

            RunBlender(config, fbxPath, manifestPath, outputPath, scriptPath);
        }

        [MenuItem("Crowding Pipeline/End To End/3 Import Generated Variants Only")]
        public static void ImportGeneratedVariantsOnlyFromMenu()
        {
            CrowdingEndToEndBuildConfig config = ResolveConfigFromSelectionOrScene();
            if (config == null)
            {
                Debug.LogError("No CrowdingEndToEndBuildConfig found.");
                return;
            }

            ImportGeneratedVariants(config);
        }

        public static bool RunFullBuild(CrowdingEndToEndBuildConfig config)
        {
            if (!PrepareSceneAndExport(config, out string fbxPath, out string manifestPath))
                return false;

            string outputPath = ResolveProjectPath(config.blenderOutputRelativePath);
            string scriptPath = ResolveProjectPath(config.blenderScriptPath);

            if (!RunBlender(config, fbxPath, manifestPath, outputPath, scriptPath))
                return false;

            AssetDatabase.Refresh();

            if (config.importGeneratedVariantsIntoUnity)
            {
                if (!ImportGeneratedVariants(config))
                    return false;
            }

            Debug.Log("Crowding end-to-end build finished successfully.");
            return true;
        }

        private static bool PrepareSceneAndExport(CrowdingEndToEndBuildConfig config, out string fbxPath, out string manifestPath)
        {
            fbxPath = string.Empty;
            manifestPath = string.Empty;

            if (!ValidateConfig(config))
                return false;

            CrowdingAwarePipelineController controller = config.controller;
            CrowdingBlenderBatchExporter exporter = EnsureManifestExporter(config);
            if (exporter == null)
                return false;

            bool namesWereBaked = false;

            try
            {
                if (config.addMissingMarkersFromController)
                    AddMarkersFromController(controller);

                if (config.fixMissingAndDuplicateIds)
                    FixMissingAndDuplicateIds();

                if (!ValidateAllMarkersForPipeline(out List<CrowdingObjectMarker> markers))
                    return false;

                if (config.bakeCidNamesBeforeExport)
                {
                    BakeCidNames(markers);
                    namesWereBaked = true;

                    if (!ValidateCidNames(markers))
                        return false;
                }

                // Run once before exporting. This also verifies that the scene actually produces groups/plans.
                controller.RunPipeline();

                if (controller.Signatures.Count == 0)
                {
                    Debug.LogError("The crowding pipeline found no object signatures. Check tag/layer/markers.", controller);
                    return false;
                }

                if (controller.FixationPlans.Count == 0)
                {
                    Debug.LogError("The crowding pipeline produced no fixation plans. Add fixation points or allow default fixation.", controller);
                    return false;
                }

                fbxPath = ResolveProjectPath(config.fbxExportRelativePath);
                EnsureParentDirectory(fbxPath);
                if (!ExportMarkedObjectsToFbx(markers, fbxPath))
                    return false;

                ConfigureManifestExporter(config, exporter);
                exporter.ExportBlenderBatchManifest();
                manifestPath = ResolveProjectPath(config.manifestRelativePath);

                if (!File.Exists(manifestPath))
                {
                    Debug.LogError("Manifest export failed or file not found: " + manifestPath);
                    return false;
                }

                if (config.verboseLogs)
                {
                    Debug.Log("Prepared Unity exports:\nFBX: " + fbxPath + "\nManifest: " + manifestPath);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                return true;
            }
            finally
            {
                if (namesWereBaked && config.restoreNamesAfterExport)
                {
                    RestoreNamesBeforeCidBake();
                }
            }
        }

        private static bool ValidateConfig(CrowdingEndToEndBuildConfig config)
        {
            if (config == null)
                return false;

            if (config.controller == null)
                config.controller = config.GetComponent<CrowdingAwarePipelineController>();

            if (config.controller == null)
            {
                Debug.LogError("CrowdingEndToEndBuildConfig requires a CrowdingAwarePipelineController.", config);
                return false;
            }

            if (config.keepRatiosK == null || config.keepRatiosK.Count == 0)
            {
                Debug.LogError("At least one K value is required.", config);
                return false;
            }

            if (!config.generateUniformCondition && !config.generateWeightedCondition)
            {
                Debug.LogError("At least one condition must be enabled: uniform or weighted.", config);
                return false;
            }

            if (string.IsNullOrWhiteSpace(config.fbxExportRelativePath))
            {
                Debug.LogError("fbxExportRelativePath is empty.", config);
                return false;
            }

            if (string.IsNullOrWhiteSpace(config.manifestRelativePath))
            {
                Debug.LogError("manifestRelativePath is empty.", config);
                return false;
            }

            return true;
        }

        private static CrowdingBlenderBatchExporter EnsureManifestExporter(CrowdingEndToEndBuildConfig config)
        {
            if (config.manifestExporter == null)
                config.manifestExporter = config.GetComponent<CrowdingBlenderBatchExporter>();

            if (config.manifestExporter == null && config.controller != null)
                config.manifestExporter = config.controller.GetComponent<CrowdingBlenderBatchExporter>();

            if (config.manifestExporter == null && config.controller != null)
            {
                Undo.AddComponent<CrowdingBlenderBatchExporter>(config.controller.gameObject);
                config.manifestExporter = config.controller.GetComponent<CrowdingBlenderBatchExporter>();
            }

            if (config.manifestExporter == null)
            {
                Debug.LogError("Could not create/find CrowdingBlenderBatchExporter.", config);
                return null;
            }

            return config.manifestExporter;
        }

        private static void ConfigureManifestExporter(CrowdingEndToEndBuildConfig config, CrowdingBlenderBatchExporter exporter)
        {
            exporter.controller = config.controller;
            exporter.runPipelineBeforeExport = false; // already run by PrepareSceneAndExport
            exporter.forceComputeOnlyModeDuringExport = true;
            exporter.batchSettings.exportRelativePath = config.manifestRelativePath;
            exporter.batchSettings.sceneVariantPrefix = config.sceneVariantPrefix;
            exporter.batchSettings.includeUniformCondition = config.generateUniformCondition;
            exporter.batchSettings.includeWeightedCrowdingCondition = config.generateWeightedCondition;
            exporter.batchSettings.groupKeepRatiosK = new List<float>(config.keepRatiosK);
            EditorUtility.SetDirty(exporter);
        }

        private static void AddMarkersFromController(CrowdingAwarePipelineController controller)
        {
            List<GameObject> roots = CrowdingObjectScanner.FindObjectRoots(
                controller.discoveryMode,
                controller.objectTag,
                controller.objectLayerMask,
                controller.includeInactiveObjects);

            int added = 0;
            int existing = 0;

            for (int i = 0; i < roots.Count; i++)
            {
                GameObject root = roots[i];
                if (root == null) continue;

                CrowdingObjectMarker marker = root.GetComponent<CrowdingObjectMarker>();
                if (marker == null)
                {
                    marker = Undo.AddComponent<CrowdingObjectMarker>(root);
                    marker.semanticName = CrowdingObjectIdUtility.RemoveCidSuffix(root.name);
                    marker.EnsureObjectId();
                    EditorUtility.SetDirty(marker);
                    added++;
                }
                else
                {
                    marker.EnsureObjectId();
                    existing++;
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log("Markers prepared from controller. Added: " + added + ", existing: " + existing + ", roots found: " + roots.Count);
        }

        private static void FixMissingAndDuplicateIds()
        {
            CrowdingObjectMarker[] markers = Object.FindObjectsByType<CrowdingObjectMarker>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            Dictionary<string, CrowdingObjectMarker> seen = new Dictionary<string, CrowdingObjectMarker>();
            int fixedCount = 0;

            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null || !marker.includeInCrowdingPipeline) continue;

                marker.EnsureObjectId();
                string id = marker.ObjectId;

                if (!CrowdingObjectIdUtility.IsValidObjectId(id) || seen.ContainsKey(id))
                {
                    marker.RegenerateObjectId();
                    fixedCount++;
                    id = marker.ObjectId;
                }

                if (!seen.ContainsKey(id))
                    seen.Add(id, marker);
            }

            AssetDatabase.SaveAssets();
            Debug.Log("Object ID repair finished. IDs regenerated: " + fixedCount);
        }

        private static bool ValidateAllMarkersForPipeline(out List<CrowdingObjectMarker> includedMarkers)
        {
            includedMarkers = new List<CrowdingObjectMarker>();
            CrowdingObjectMarker[] markers = Object.FindObjectsByType<CrowdingObjectMarker>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            Dictionary<string, CrowdingObjectMarker> byId = new Dictionary<string, CrowdingObjectMarker>();
            int errors = 0;

            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null || !marker.includeInCrowdingPipeline) continue;

                string id = marker.ObjectId;
                if (!CrowdingObjectIdUtility.IsValidObjectId(id))
                {
                    Debug.LogError("Invalid objectId on " + CrowdingObjectIdUtility.GetHierarchyPath(marker.transform), marker);
                    errors++;
                    continue;
                }

                if (byId.ContainsKey(id))
                {
                    Debug.LogError("Duplicate objectId " + id + " on " + marker.name + " and " + byId[id].name, marker);
                    errors++;
                    continue;
                }

                byId.Add(id, marker);
                includedMarkers.Add(marker);
            }

            if (includedMarkers.Count == 0)
            {
                Debug.LogError("No included CrowdingObjectMarker found.");
                return false;
            }

            if (errors > 0)
            {
                Debug.LogError("Marker validation failed. Errors: " + errors);
                return false;
            }

            Debug.Log("Marker validation passed. Included objects: " + includedMarkers.Count);
            return true;
        }

        private static void BakeCidNames(List<CrowdingObjectMarker> markers)
        {
            int changed = 0;
            for (int i = 0; i < markers.Count; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null) continue;

                marker.EnsureObjectId();
                marker.StoreOriginalNameBeforeBake();
                string newName = marker.ExportNameWithCid;

                if (marker.gameObject.name == newName) continue;

                Undo.RecordObject(marker.gameObject, "Bake Crowding CID Suffix");
                marker.gameObject.name = newName;
                EditorUtility.SetDirty(marker.gameObject);
                changed++;
            }

            Debug.Log("CID names baked. Changed objects: " + changed);
        }

        private static bool ValidateCidNames(List<CrowdingObjectMarker> markers)
        {
            int errors = 0;
            for (int i = 0; i < markers.Count; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null) continue;

                string idInName = CrowdingObjectIdUtility.ExtractObjectIdFromName(marker.gameObject.name);
                if (idInName != marker.ObjectId)
                {
                    Debug.LogError("CID mismatch in name for " + marker.name + ". Marker=" + marker.ObjectId + ", name=" + idInName, marker);
                    errors++;
                }
            }

            if (errors == 0)
            {
                Debug.Log("CID name validation passed.");
                return true;
            }

            Debug.LogError("CID name validation failed. Errors: " + errors);
            return false;
        }

        private static void RestoreNamesBeforeCidBake()
        {
            CrowdingObjectMarker[] markers = Object.FindObjectsByType<CrowdingObjectMarker>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            int changed = 0;
            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null) continue;
                string original = marker.OriginalNameBeforeCidBake;
                if (string.IsNullOrEmpty(original)) continue;

                Undo.RecordObject(marker.gameObject, "Restore Crowding Name Before CID Bake");
                marker.gameObject.name = original;
                marker.ClearStoredOriginalName();
                EditorUtility.SetDirty(marker.gameObject);
                changed++;
            }

            if (changed > 0)
                Debug.Log("Restored names after CID export. Changed objects: " + changed);
        }

        private static bool ExportMarkedObjectsToFbx(List<CrowdingObjectMarker> markers, string outputPath)
        {
            MethodInfo exportMethod = FindFbxExportObjectMethod();
            if (exportMethod == null)
            {
                Debug.LogError(
                    "Unity FBX Exporter was not found. Install it via Package Manager: com.unity.formats.fbx. " +
                    "The builder uses reflection, so no compile-time dependency is required, but the package must be installed for automatic FBX export.");
                return false;
            }

            GameObject tempRoot = new GameObject("CrowdingCIDExportRoot_TEMP");
            tempRoot.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                for (int i = 0; i < markers.Count; i++)
                {
                    CrowdingObjectMarker marker = markers[i];
                    if (marker == null || marker.gameObject == null) continue;

                    GameObject duplicate = Object.Instantiate(marker.gameObject);
                    duplicate.name = marker.gameObject.name;
                    duplicate.transform.SetParent(tempRoot.transform, true);
                }

                object result = exportMethod.Invoke(null, new object[] { outputPath, tempRoot });
                string exportedPath = result as string;

                if (string.IsNullOrEmpty(exportedPath) && !File.Exists(outputPath))
                {
                    Debug.LogError("FBX export failed. Output path: " + outputPath);
                    return false;
                }

                Debug.Log("CID FBX exported: " + (string.IsNullOrEmpty(exportedPath) ? outputPath : exportedPath));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("FBX export failed: " + ex);
                return false;
            }
            finally
            {
                Object.DestroyImmediate(tempRoot);
            }
        }

        private static MethodInfo FindFbxExportObjectMethod()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type t = assemblies[i].GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter");
                if (t == null) continue;

                MethodInfo method = t.GetMethod(
                    "ExportObject",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(string), typeof(Object) },
                    null);

                if (method != null)
                    return method;
            }
            return null;
        }

        private static bool RunBlender(CrowdingEndToEndBuildConfig config, string fbxPath, string manifestPath, string outputPath, string scriptPath)
        {
            if (!File.Exists(fbxPath))
            {
                Debug.LogError("Cannot run Blender: FBX input does not exist: " + fbxPath);
                return false;
            }

            if (!File.Exists(manifestPath))
            {
                Debug.LogError("Cannot run Blender: manifest does not exist: " + manifestPath);
                return false;
            }

            if (!File.Exists(scriptPath))
            {
                Debug.LogError("Cannot run Blender: Blender script does not exist: " + scriptPath);
                return false;
            }

            EnsureDirectory(outputPath);

            string args =
                "--background " +
                "--python " + Quote(scriptPath) + " -- " +
                "--input-scene " + Quote(fbxPath) + " " +
                "--manifest " + Quote(manifestPath) + " " +
                "--output-dir " + Quote(outputPath) + " " +
                "--variant-prefix " + Quote(config.sceneVariantPrefix) + " " +
                (config.strictBlenderValidation ? "--strict" : "");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = string.IsNullOrWhiteSpace(config.blenderExecutablePath) ? "blender" : config.blenderExecutablePath;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;

            Debug.Log("Running Blender:\n" + psi.FileName + " " + psi.Arguments);

            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = psi;
                    process.OutputDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.Log("[Blender] " + e.Data); };
                    process.ErrorDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.LogWarning("[Blender] " + e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (config.waitForBlender)
                    {
                        process.WaitForExit();
                        if (process.ExitCode != 0)
                        {
                            Debug.LogError("Blender failed with exit code " + process.ExitCode);
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("Could not run Blender. Check blenderExecutablePath.\n" + ex);
                return false;
            }

            Debug.Log("Blender generation finished. Output: " + outputPath);
            return true;
        }

        private static bool ImportGeneratedVariants(CrowdingEndToEndBuildConfig config)
        {
            if (config.lodSelector == null)
                config.lodSelector = Object.FindFirstObjectByType<CrowdingCombinationLODSelector>();

            if (config.lodSelector == null)
            {
                Debug.LogError("Cannot import variants: no CrowdingCombinationLODSelector assigned/found.", config);
                return false;
            }

            string outputAbs = ResolveProjectPath(config.blenderOutputRelativePath);
            if (!Directory.Exists(outputAbs))
            {
                Debug.LogError("Generated variants folder not found: " + outputAbs);
                return false;
            }

            string outputAssetPath = AbsoluteToAssetPath(outputAbs);
            if (string.IsNullOrEmpty(outputAssetPath))
            {
                Debug.LogError("Generated variants folder must be inside Assets/ for automatic Unity import. Current folder: " + outputAbs);
                return false;
            }

            AssetDatabase.Refresh();

            if (config.originalSceneRoot != null)
                config.lodSelector.originalSceneRoot = config.originalSceneRoot;

            if (config.clearExistingSelectorVariantChildren)
                ClearGeneratedVariantChildren(config.lodSelector.transform);

            string[] fbxFiles = Directory.GetFiles(outputAbs, "*.fbx", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p)
                .ToArray();

            int instantiated = 0;
            for (int i = 0; i < fbxFiles.Length; i++)
            {
                string assetPath = AbsoluteToAssetPath(fbxFiles[i]);
                if (string.IsNullOrEmpty(assetPath)) continue;

                GameObject assetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (assetRoot == null)
                {
                    Debug.LogWarning("Could not load generated FBX asset: " + assetPath);
                    continue;
                }

                string key = ExtractVariantKeyFromFileName(Path.GetFileNameWithoutExtension(fbxFiles[i]));
                if (string.IsNullOrEmpty(key))
                {
                    Debug.LogWarning("Skipping FBX without variant key in name: " + assetPath);
                    continue;
                }

                GameObject instance = PrefabUtility.InstantiatePrefab(assetRoot) as GameObject;
                if (instance == null)
                {
                    Debug.LogWarning("Could not instantiate generated FBX asset: " + assetPath);
                    continue;
                }

                Undo.RegisterCreatedObjectUndo(instance, "Instantiate Crowding Variant");
                instance.name = config.sceneVariantPrefix + "_" + key;
                instance.transform.SetParent(config.lodSelector.transform, false);
                instance.SetActive(false);
                instantiated++;
            }

            if (config.autoPopulateSelector)
            {
                config.lodSelector.AutoPopulateVariantsFromChildren();
                EditorUtility.SetDirty(config.lodSelector);
            }

            if (config.validateImportedVariants)
            {
                Selection.activeGameObject = config.lodSelector.gameObject;
                bool ok = CrowdingImportedVariantValidator.ValidateSelectedSelectorVariants();
                if (!ok) return false;
            }

            Debug.Log("Generated variants imported/instantiated under selector. Count: " + instantiated);
            return instantiated > 0;
        }

        private static void ClearGeneratedVariantChildren(Transform selectorTransform)
        {
            List<GameObject> toDelete = new List<GameObject>();
            for (int i = 0; i < selectorTransform.childCount; i++)
            {
                Transform child = selectorTransform.GetChild(i);
                if (child != null && child.name.StartsWith(VariantRootPrefix, StringComparison.Ordinal))
                    toDelete.Add(child.gameObject);
                else if (child != null && child.name.Contains("F") && child.name.Contains("_K") && (child.name.Contains("_weighted") || child.name.Contains("_uniform")))
                    toDelete.Add(child.gameObject);
            }

            for (int i = 0; i < toDelete.Count; i++)
                Undo.DestroyObjectImmediate(toDelete[i]);
        }

        private static string ExtractVariantKeyFromFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            int index = name.IndexOf("F", StringComparison.Ordinal);
            while (index >= 0 && index < name.Length)
            {
                string candidate = name.Substring(index);
                if (candidate.Contains("_K") && (candidate.Contains("_weighted") || candidate.Contains("_uniform")))
                    return candidate;
                index = name.IndexOf("F", index + 1, StringComparison.Ordinal);
            }
            return string.Empty;
        }

        private static CrowdingEndToEndBuildConfig ResolveConfigFromSelectionOrScene()
        {
            if (Selection.activeGameObject != null)
            {
                CrowdingEndToEndBuildConfig selectedConfig = Selection.activeGameObject.GetComponent<CrowdingEndToEndBuildConfig>();
                if (selectedConfig != null) return selectedConfig;

                CrowdingAwarePipelineController selectedController = Selection.activeGameObject.GetComponent<CrowdingAwarePipelineController>();
                if (selectedController != null)
                {
                    selectedConfig = selectedController.GetComponent<CrowdingEndToEndBuildConfig>();
                    if (selectedConfig != null) return selectedConfig;
                }
            }

            return Object.FindFirstObjectByType<CrowdingEndToEndBuildConfig>();
        }

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static string ResolveProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(GetProjectRoot(), path));
        }

        private static string AbsoluteToAssetPath(string absolutePath)
        {
            absolutePath = Path.GetFullPath(absolutePath).Replace('\\', '/');
            string dataPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/');

            if (!absolutePath.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return "Assets" + absolutePath.Substring(dataPath.Length);
        }

        private static void EnsureParentDirectory(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static void EnsureDirectory(string path)
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        }

        private static string Quote(string path)
        {
            return "\"" + path.Replace("\"", "\\\"") + "\"";
        }
    }
}
#endif
