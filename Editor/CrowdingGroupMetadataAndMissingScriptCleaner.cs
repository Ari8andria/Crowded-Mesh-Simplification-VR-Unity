#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CrowdingAwareLOD.EditorTools
{
    public static class CrowdingGroupMetadataAndMissingScriptCleaner
    {
        private const string PrefabSearchRoot = "Assets/CrowdingAwareLOD";

        [MenuItem("Crowding Pipeline/Debug/Clean Selected Hierarchy Missing Scripts")]
        public static void CleanSelectedHierarchy()
        {
            int removed = 0;

            foreach (GameObject root in Selection.gameObjects)
            {
                if (root == null)
                    continue;

                removed += CleanHierarchy(root, removeCrowdingGroupMembers: true);
                EditorUtility.SetDirty(root);
            }

            MarkLoadedScenesDirty();

            Debug.Log($"[CrowdingCleaner] Selected hierarchy cleanup completed. Removed components: {removed}");
        }

        [MenuItem("Crowding Pipeline/Debug/Clean Loaded Scenes Missing Scripts")]
        public static void CleanLoadedScenes()
        {
            int removed = 0;
            int rootsRemoved = 0;

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);

                if (!scene.IsValid() || !scene.isLoaded)
                    continue;

                GameObject[] roots = scene.GetRootGameObjects();

                foreach (GameObject root in roots)
                    removed += CleanHierarchy(root, removeCrowdingGroupMembers: true);

                foreach (GameObject root in roots)
                {
                    if (root == null)
                        continue;

                    if (root.name == "CrowdingGroupsRoot" ||
                        root.name.StartsWith("CrowdingGroupsRoot"))
                    {
                        Undo.DestroyObjectImmediate(root);
                        rootsRemoved++;
                    }
                }

                EditorSceneManager.MarkSceneDirty(scene);
            }

            Debug.Log(
                "[CrowdingCleaner] Loaded scenes cleanup completed.\n" +
                $"- Missing / group components removed: {removed}\n" +
                $"- CrowdingGroupsRoot removed: {rootsRemoved}"
            );
        }

        [MenuItem("Crowding Pipeline/Debug/Clean Current Prefab Stage Missing Scripts")]
        public static void CleanCurrentPrefabStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();

            if (stage == null || stage.prefabContentsRoot == null)
            {
                Debug.LogWarning("[CrowdingCleaner] No prefab stage currently open.");
                return;
            }

            int removed = CleanHierarchy(stage.prefabContentsRoot, removeCrowdingGroupMembers: true);

            EditorUtility.SetDirty(stage.prefabContentsRoot);
            EditorSceneManager.MarkSceneDirty(stage.scene);

            Debug.Log($"[CrowdingCleaner] Current prefab stage cleanup completed. Removed components: {removed}");
        }

        [MenuItem("Crowding Pipeline/Debug/Clean Prefabs Under CrowdingAwareLOD")]
        public static void CleanPrefabsUnderCrowdingAwareLOD()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabSearchRoot });

            int modifiedPrefabs = 0;
            int totalRemoved = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                if (string.IsNullOrEmpty(path))
                    continue;

                GameObject prefabRoot = null;

                try
                {
                    prefabRoot = PrefabUtility.LoadPrefabContents(path);

                    if (prefabRoot == null)
                        continue;

                    int removed = CleanHierarchy(prefabRoot, removeCrowdingGroupMembers: true);

                    if (removed > 0)
                    {
                        PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                        modifiedPrefabs++;
                        totalRemoved += removed;

                        Debug.Log($"[CrowdingCleaner] Cleaned prefab: {path} | removed: {removed}");
                    }
                }
                finally
                {
                    if (prefabRoot != null)
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                "[CrowdingCleaner] Prefab cleanup completed.\n" +
                $"- Modified prefabs: {modifiedPrefabs}\n" +
                $"- Removed components: {totalRemoved}"
            );
        }

        [MenuItem("Crowding Pipeline/Debug/Deep Clean Group Metadata And Missing Scripts")]
        public static void DeepClean()
        {
            CleanSelectedHierarchy();
            CleanLoadedScenes();
            CleanCurrentPrefabStage();
            CleanPrefabsUnderCrowdingAwareLOD();

            Debug.Log("[CrowdingCleaner] Deep clean completed.");
        }

        private static int CleanHierarchy(GameObject root, bool removeCrowdingGroupMembers)
        {
            if (root == null)
                return 0;

            int removed = 0;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);

            foreach (Transform t in transforms)
            {
                if (t == null)
                    continue;

                GameObject go = t.gameObject;

                removed += RemoveMissingScripts(go);

                if (removeCrowdingGroupMembers)
                    removed += RemoveCrowdingGroupMemberComponents(go);
            }

            return removed;
        }

        private static int RemoveMissingScripts(GameObject go)
        {
            if (go == null)
                return 0;

            int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);

            if (count <= 0)
                return 0;

            Undo.RegisterCompleteObjectUndo(go, "Remove Missing Scripts");
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            EditorUtility.SetDirty(go);

            return count;
        }

        private static int RemoveCrowdingGroupMemberComponents(GameObject go)
        {
            if (go == null)
                return 0;

            int removed = 0;
            Component[] components = go.GetComponents<Component>();

            foreach (Component c in components)
            {
                if (c == null)
                    continue;

                if (c.GetType().Name == "CrowdingGroupMember")
                {
                    Undo.DestroyObjectImmediate(c);
                    removed++;
                }
            }

            if (removed > 0)
                EditorUtility.SetDirty(go);

            return removed;
        }

        private static void MarkLoadedScenesDirty()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);

                if (scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.MarkSceneDirty(scene);
            }
        }
    }
}
#endif