#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace CrowdingAwareLOD.EditorTools
{
    /// <summary>
    /// Batch builder for generating crowding-aware mesh LOD variants across several independent scenes.
    ///
    /// It opens each enabled scene from Build Settings, finds the CrowdingMeshLODBuildConfig in that scene,
    /// forces scene-specific output paths to avoid overwriting assets, then calls the existing
    /// CrowdingMeshOnlyEndToEndBuilder.RunFullBuild(config).
    ///
    /// This is intended for unattended overnight generation.
    /// </summary>
    public static class CrowdingBatchSceneLODBuilder
    {
        private const string MenuRoot = "Crowding Pipeline/Batch Scene LODs";
        private const string BatchLogFolderRelative = "CrowdingExports/BatchLODBuildLogs";

        [MenuItem(MenuRoot + "/Build LODs For Current Scene")]
        public static void BuildCurrentSceneFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            string scenePath = SceneManager.GetActiveScene().path;
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                Debug.LogError("Crowding batch LOD: current scene has no asset path. Save the scene first.");
                return;
            }

            BuildScenes(new List<string> { scenePath }, true, true);
        }

        [MenuItem(MenuRoot + "/Build LODs For Enabled Build Settings Scenes")]
        public static void BuildEnabledBuildSettingsScenesFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            List<string> scenePaths = GetEnabledBuildSettingsScenePaths();
            if (scenePaths.Count == 0)
            {
                Debug.LogError("Crowding batch LOD: no enabled scene found in Build Settings.");
                return;
            }

            bool proceed = EditorUtility.DisplayDialog(
                "Crowding batch LOD generation",
                "This will open each enabled scene from Build Settings and generate its LOD variants.\n\n" +
                "Each scene will use scene-specific output folders to avoid overwriting assets.\n\n" +
                "Unity may be busy for a long time. Continue?",
                "Start batch",
                "Cancel"
            );

            if (!proceed)
                return;

            BuildScenes(scenePaths, true, true);
        }

        /// <summary>
        /// Command-line entry point for unattended generation.
        /// Example:
        /// Unity.exe -batchmode -projectPath "C:/MyProject" -executeMethod CrowdingAwareLOD.EditorTools.CrowdingBatchSceneLODBuilder.BuildEnabledBuildSettingsScenesFromCommandLine -quit
        /// </summary>
        public static void BuildEnabledBuildSettingsScenesFromCommandLine()
        {
            List<string> scenePaths = GetEnabledBuildSettingsScenePaths();
            BuildScenes(scenePaths, true, true);
        }

        private static List<string> GetEnabledBuildSettingsScenePaths()
        {
            List<string> scenePaths = new List<string>();
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;

            for (int i = 0; i < scenes.Length; i++)
            {
                EditorBuildSettingsScene scene = scenes[i];
                if (scene == null || !scene.enabled || string.IsNullOrWhiteSpace(scene.path))
                    continue;

                if (!File.Exists(scene.path))
                {
                    Debug.LogWarning("Crowding batch LOD: scene path in Build Settings does not exist: " + scene.path);
                    continue;
                }

                scenePaths.Add(scene.path);
            }

            return scenePaths;
        }

        private static void BuildScenes(List<string> scenePaths, bool useSceneSpecificOutputPaths, bool saveSceneAfterSuccess)
        {
            if (scenePaths == null || scenePaths.Count == 0)
            {
                Debug.LogError("Crowding batch LOD: no scene path provided.");
                return;
            }

            string batchLogPath = CreateBatchLogPath();
            WriteBatchLogHeader(batchLogPath);

            int successCount = 0;
            int skippedCount = 0;
            int failedCount = 0;
            Stopwatch totalWatch = Stopwatch.StartNew();

            try
            {
                for (int i = 0; i < scenePaths.Count; i++)
                {
                    string scenePath = scenePaths[i];
                    string sceneName = Path.GetFileNameWithoutExtension(scenePath);

                    EditorUtility.DisplayProgressBar(
                        "Crowding batch LOD generation",
                        "Opening scene " + (i + 1) + "/" + scenePaths.Count + ": " + sceneName,
                        scenePaths.Count <= 0 ? 0f : (float)i / scenePaths.Count
                    );

                    Stopwatch sceneWatch = Stopwatch.StartNew();
                    string status = "FAILED";
                    string message = string.Empty;

                    try
                    {
                        if (!File.Exists(scenePath))
                        {
                            status = "SKIPPED";
                            message = "Scene file not found.";
                            skippedCount++;
                            AppendBatchLog(batchLogPath, i, scenePath, sceneName, status, message, sceneWatch.Elapsed.TotalSeconds);
                            continue;
                        }

                        EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

                        CrowdingMeshLODBuildConfig config = UnityEngine.Object.FindFirstObjectByType<CrowdingMeshLODBuildConfig>();
                        if (config == null)
                        {
                            status = "SKIPPED";
                            message = "No CrowdingMeshLODBuildConfig found in scene.";
                            skippedCount++;
                            AppendBatchLog(batchLogPath, i, scenePath, sceneName, status, message, sceneWatch.Elapsed.TotalSeconds);
                            Debug.LogWarning("Crowding batch LOD: skipped scene without CrowdingMeshLODBuildConfig: " + sceneName);
                            continue;
                        }

                        if (useSceneSpecificOutputPaths)
                            ApplySceneSpecificOutputPaths(config, sceneName);

                        EditorUtility.SetDirty(config);
                        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                        EditorUtility.DisplayProgressBar(
                            "Crowding batch LOD generation",
                            "Building LODs for " + sceneName + " (" + (i + 1) + "/" + scenePaths.Count + ")",
                            scenePaths.Count <= 0 ? 0f : ((float)i + 0.5f) / scenePaths.Count
                        );

                        bool ok = CrowdingMeshOnlyEndToEndBuilder.RunFullBuild(config);
                        if (!ok)
                        {
                            status = "FAILED";
                            message = "CrowdingMeshOnlyEndToEndBuilder.RunFullBuild returned false.";
                            failedCount++;
                            AppendBatchLog(batchLogPath, i, scenePath, sceneName, status, message, sceneWatch.Elapsed.TotalSeconds);
                            Debug.LogError("Crowding batch LOD: failed scene: " + sceneName);
                            continue;
                        }

                        AssetDatabase.SaveAssets();
                        AssetDatabase.Refresh();

                        if (saveSceneAfterSuccess)
                            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());

                        status = "SUCCESS";
                        message = "Build finished successfully.";
                        successCount++;
                        AppendBatchLog(batchLogPath, i, scenePath, sceneName, status, message, sceneWatch.Elapsed.TotalSeconds);

                        Debug.Log("Crowding batch LOD: finished scene " + sceneName + " in " + sceneWatch.Elapsed.TotalMinutes.ToString("F1") + " min.");
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        status = "FAILED";
                        message = ex.GetType().Name + ": " + ex.Message;
                        AppendBatchLog(batchLogPath, i, scenePath, sceneName, status, message, sceneWatch.Elapsed.TotalSeconds);
                        Debug.LogError("Crowding batch LOD: exception in scene " + sceneName + "\n" + ex);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                totalWatch.Stop();

                Debug.Log(
                    "Crowding batch LOD generation finished.\n" +
                    "Success: " + successCount + " | Skipped: " + skippedCount + " | Failed: " + failedCount + "\n" +
                    "Total time: " + totalWatch.Elapsed.TotalMinutes.ToString("F1") + " min\n" +
                    "Batch log: " + batchLogPath
                );
            }
        }

        private static void ApplySceneSpecificOutputPaths(CrowdingMeshLODBuildConfig config, string rawSceneName)
        {
            string sceneName = SanitizePathSegment(rawSceneName);

            // Files outside Assets, mostly intermediate / diagnostic files.
            config.sourceMeshObjRelativeFolder = "CrowdingExports/" + sceneName + "/SourceMeshes";
            config.meshManifestRelativePath = "CrowdingExports/" + sceneName + "/crowding_mesh_lod_manifest.json";

            // Generated Unity assets. These must be under Assets so the project can load them at runtime.
            config.blenderOutputRelativeFolder = "Assets/CrowdingGeneratedLODs/" + sceneName + "/Meshes";
            config.blenderImportMapRelativePath = "Assets/CrowdingGeneratedLODs/" + sceneName + "/crowding_lod_mesh_import_map.json";
            config.variantLibraryAssetPath = "Assets/CrowdingGeneratedLODs/" + sceneName + "/CrowdingMeshVariantLibrary_" + sceneName + ".asset";
        }

        private static string CreateBatchLogPath()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string folder = Path.Combine(projectRoot, BatchLogFolderRelative);
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(folder, "batch_lod_build_" + stamp + ".csv");
        }

        private static void WriteBatchLogHeader(string path)
        {
            using (StreamWriter writer = new StreamWriter(path, false))
            {
                writer.WriteLine("timeLocal,index,scenePath,sceneName,status,message,durationSeconds");
            }
        }

        private static void AppendBatchLog(string path, int index, string scenePath, string sceneName, string status, string message, double durationSeconds)
        {
            using (StreamWriter writer = new StreamWriter(path, true))
            {
                writer.WriteLine(
                    Escape(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + "," +
                    index + "," +
                    Escape(scenePath) + "," +
                    Escape(sceneName) + "," +
                    Escape(status) + "," +
                    Escape(message) + "," +
                    durationSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                );
            }
        }

        private static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Scene";

            string result = value.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
                result = result.Replace(invalid[i], '_');

            result = result.Replace(' ', '_');
            return string.IsNullOrWhiteSpace(result) ? "Scene" : result;
        }

        private static string Escape(string value)
        {
            if (value == null)
                return string.Empty;

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";

            return value;
        }
    }
}
#endif
