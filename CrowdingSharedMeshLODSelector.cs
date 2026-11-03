using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CrowdingAwareLOD
{
    public enum CrowdingMeshSwapMode
    {
        Immediate,
        CrossFadeTransparentDuplicate
    }

    [DisallowMultipleComponent]
    [ExecuteAlways]
    public class CrowdingSharedMeshLODSelector : MonoBehaviour
    {
        [Header("Library")]
        public CrowdingMeshVariantLibrary variantLibrary;

        [Header("Startup")]
        public bool captureOriginalMeshesOnEnable = true;
        public bool applyOnStart = false;
        public int startFixationIndex = 0;
        public float startKeepRatioK = 0.6f;
        public CrowdingDecimationCondition startCondition = CrowdingDecimationCondition.WeightedCrowding;

        [Header("LOD swap optimization")]
        [Tooltip("If true, a mesh is not swapped when the new mesh reference is already the current one.")]
        public bool skipSwapIfSameMeshReference = true;

        [Tooltip("If true, a mesh is not swapped when the triangle count difference is below the tolerance.")]
        public bool skipSwapIfTriangleCountSimilar = true;

        [Tooltip("Relative triangle-count tolerance. 0.05 means 5%.")]
        [Range(0f, 0.25f)]
        public float triangleSimilarityTolerance = 0.05f;

        [Tooltip("If true, logs triangle counts for skipped similar swaps.")]
        public bool logSkippedSimilarMeshes = false;

        [Header("LOD swap transition")]
        public CrowdingMeshSwapMode meshSwapMode = CrowdingMeshSwapMode.Immediate;

        [Tooltip("Cross-fade duration in seconds. Used only in Play Mode.")]
        [Min(0.001f)]
        public float crossFadeDurationSeconds = 0.15f;

        [Tooltip("If true, cross-fade is used only in Play Mode. In Edit Mode the swap is immediate.")]
        public bool crossFadeOnlyInPlayMode = true;

        [Tooltip("If true, the temporary old-mesh duplicate will not cast shadows during the fade.")]
        public bool disableGhostShadows = true;

        [Tooltip("If true, the main renderer is restored to its original materials after the fade.")]
        public bool restoreOpaqueMaterialsAfterFade = true;

        [Tooltip("If true, previously running cross-fades are immediately completed before applying a new variant.")]
        public bool completePreviousFadeBeforeNewSwap = true;

        [Header("Debug")]
        public bool logSelection = true;
        public bool logCrossFade = false;

        [SerializeField, HideInInspector]
        private List<OriginalMeshRecord> serializedOriginalMeshes = new List<OriginalMeshRecord>();

        [SerializeField, HideInInspector]
        private string activeVariantKey;

        private readonly Dictionary<string, MeshSlotBinding> bindingsBySlotId =
            new Dictionary<string, MeshSlotBinding>();

        private readonly Dictionary<string, Mesh> originalMeshesBySlotId =
            new Dictionary<string, Mesh>();

        private readonly Dictionary<Mesh, int> triangleCountCache =
            new Dictionary<Mesh, int>();

        private Coroutine runningCrossFadeCoroutine;
        private readonly List<CrossFadeJob> activeCrossFadeJobs = new List<CrossFadeJob>();

        public string ActiveVariantKey => activeVariantKey;
        public bool IsCrossFadeRunning => runningCrossFadeCoroutine != null;

        private void OnEnable()
        {
            RebuildBindings();
            RebuildOriginalDictionaryFromSerializedRecords();

            if (captureOriginalMeshesOnEnable)
                CaptureMissingOriginalMeshes();
        }

        private void OnDisable()
        {
            CompleteAndCleanupActiveCrossFade();
        }

        private void Start()
        {
            if (Application.isPlaying && applyOnStart)
                ApplyCombination(startFixationIndex, startKeepRatioK, startCondition);
        }

        [ContextMenu("Crowding Mesh LOD/Rebuild Bindings")]
        public void RebuildBindings()
        {
            bindingsBySlotId.Clear();

            CrowdingObjectMarker[] markers = FindObjectsOfType<CrowdingObjectMarker>(true);

            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];

                if (marker == null)
                    continue;

                if (!marker.includeInCrowdingPipeline)
                    continue;

                MeshFilter[] filters = marker.GetComponentsInChildren<MeshFilter>(true);

                for (int f = 0; f < filters.Length; f++)
                {
                    MeshFilter mf = filters[f];

                    if (mf == null || mf.sharedMesh == null)
                        continue;

                    MeshRenderer renderer = mf.GetComponent<MeshRenderer>();

                    string childPath =
                        CrowdingMeshSlotUtility.GetRelativePath(marker.transform, mf.transform);

                    string slotId =
                        CrowdingMeshSlotUtility.BuildSlotId(
                            marker.ObjectId,
                            childPath,
                            "MeshFilter"
                        );

                    bindingsBySlotId[slotId] =
                        MeshSlotBinding.FromMeshFilter(slotId, mf, renderer);
                }

                SkinnedMeshRenderer[] skins =
                    marker.GetComponentsInChildren<SkinnedMeshRenderer>(true);

                for (int s = 0; s < skins.Length; s++)
                {
                    SkinnedMeshRenderer smr = skins[s];

                    if (smr == null || smr.sharedMesh == null)
                        continue;

                    string childPath =
                        CrowdingMeshSlotUtility.GetRelativePath(marker.transform, smr.transform);

                    string slotId =
                        CrowdingMeshSlotUtility.BuildSlotId(
                            marker.ObjectId,
                            childPath,
                            "SkinnedMeshRenderer"
                        );

                    bindingsBySlotId[slotId] =
                        MeshSlotBinding.FromSkinnedMeshRenderer(slotId, smr);
                }
            }

            if (logSelection)
            {
                Debug.Log(
                    $"CrowdingSharedMeshLODSelector bindings rebuilt. " +
                    $"Mesh slots found: {bindingsBySlotId.Count}",
                    this
                );
            }
        }

        [ContextMenu("Crowding Mesh LOD/Capture Missing Original Meshes")]
        public void CaptureMissingOriginalMeshes()
        {
            if (bindingsBySlotId.Count == 0)
                RebuildBindings();

            RebuildOriginalDictionaryFromSerializedRecords();

            int captured = 0;

            foreach (KeyValuePair<string, MeshSlotBinding> kvp in bindingsBySlotId)
            {
                string slotId = kvp.Key;

                if (originalMeshesBySlotId.ContainsKey(slotId))
                    continue;

                Mesh current = kvp.Value.GetMesh();

                if (current == null)
                    continue;

                originalMeshesBySlotId[slotId] = current;
                AddOrUpdateSerializedOriginal(slotId, current);
                captured++;
            }

            MarkSelectorDirty();

            if (logSelection)
            {
                Debug.Log(
                    $"Original shared meshes captured for missing slots: {captured}. " +
                    $"Total originals: {originalMeshesBySlotId.Count}",
                    this
                );
            }
        }

        [ContextMenu("Crowding Mesh LOD/Force Recapture Current Meshes As Originals")]
        public void ForceRecaptureCurrentMeshesAsOriginals()
        {
            CompleteAndCleanupActiveCrossFade();

            if (bindingsBySlotId.Count == 0)
                RebuildBindings();

            originalMeshesBySlotId.Clear();
            serializedOriginalMeshes.Clear();

            int captured = 0;

            foreach (KeyValuePair<string, MeshSlotBinding> kvp in bindingsBySlotId)
            {
                Mesh current = kvp.Value.GetMesh();

                if (current == null)
                    continue;

                originalMeshesBySlotId[kvp.Key] = current;

                serializedOriginalMeshes.Add(new OriginalMeshRecord
                {
                    slotId = kvp.Key,
                    originalMesh = current
                });

                captured++;
            }

            triangleCountCache.Clear();
            MarkSelectorDirty();

            if (logSelection)
            {
                Debug.Log(
                    $"FORCED recapture of current meshes as originals: {captured}",
                    this
                );
            }
        }

        public void ApplyCombination(
            int fixationIndex,
            float keepRatioK,
            CrowdingDecimationCondition condition)
        {
            string key = CrowdingVariantKey.Build(fixationIndex, keepRatioK, condition);
            ApplyVariant(key);
        }

        public void ApplyVariant(string variantKey)
        {
            if (variantLibrary == null)
            {
                Debug.LogError(
                    "CrowdingSharedMeshLODSelector has no variant library assigned.",
                    this
                );
                return;
            }

            if (completePreviousFadeBeforeNewSwap)
                CompleteAndCleanupActiveCrossFade();

            if (bindingsBySlotId.Count == 0)
                RebuildBindings();

            CaptureMissingOriginalMeshes();

            List<CrowdingMeshVariantEntry> entries =
                variantLibrary.GetEntriesForVariant(variantKey);

            if (entries == null || entries.Count == 0)
            {
                Debug.LogError($"No mesh entries found for variant: {variantKey}", this);
                return;
            }

#if UNITY_EDITOR
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName($"Apply Crowding Mesh Variant {variantKey}");
#endif

            bool canCrossFade =
                meshSwapMode == CrowdingMeshSwapMode.CrossFadeTransparentDuplicate &&
                (!crossFadeOnlyInPlayMode || Application.isPlaying) &&
                crossFadeDurationSeconds > 0.001f;

            int applied = 0;
            int crossFaded = 0;
            int immediate = 0;
            int skippedSameReference = 0;
            int skippedSimilarTriangles = 0;
            int missingBinding = 0;
            int missingMesh = 0;
            int noRendererForCrossFade = 0;

            List<CrossFadeJob> newFadeJobs = new List<CrossFadeJob>();

            for (int i = 0; i < entries.Count; i++)
            {
                CrowdingMeshVariantEntry entry = entries[i];

                if (entry == null)
                    continue;

                if (!bindingsBySlotId.TryGetValue(entry.slotId, out MeshSlotBinding binding) ||
                    binding == null ||
                    !binding.IsAlive)
                {
                    missingBinding++;
                    continue;
                }

                Mesh newMesh = entry.decimatedMesh;

                if (newMesh == null)
                {
                    missingMesh++;
                    continue;
                }

                Mesh currentMesh = binding.GetMesh();

                if (skipSwapIfSameMeshReference && currentMesh == newMesh)
                {
                    skippedSameReference++;
                    continue;
                }

                if (skipSwapIfTriangleCountSimilar &&
                    AreTriangleCountsSimilar(currentMesh, newMesh, triangleSimilarityTolerance))
                {
                    skippedSimilarTriangles++;

                    if (logSkippedSimilarMeshes)
                    {
                        int currentTriangles = GetTriangleCountCached(currentMesh);
                        int newTriangles = GetTriangleCountCached(newMesh);
                        float diff = RelativeTriangleDifference(currentTriangles, newTriangles);

                        Debug.Log(
                            $"Skipped mesh swap for slot '{entry.slotId}' because triangle counts are similar. " +
                            $"Current={currentTriangles}, New={newTriangles}, Diff={diff * 100f:F2}%, " +
                            $"Tolerance={triangleSimilarityTolerance * 100f:F2}%",
                            this
                        );
                    }

                    continue;
                }

                if (canCrossFade && binding.CanCrossFade)
                {
                    CrossFadeJob job = CreateCrossFadeJob(binding, currentMesh, newMesh);

                    if (job != null)
                    {
                        newFadeJobs.Add(job);
                        applied++;
                        crossFaded++;
                        continue;
                    }

                    noRendererForCrossFade++;
                }
                else if (canCrossFade && !binding.CanCrossFade)
                {
                    noRendererForCrossFade++;
                }

                binding.SetMesh(newMesh);
                applied++;
                immediate++;
            }

            activeVariantKey = variantKey;
            MarkSelectorDirty();

            if (newFadeJobs.Count > 0 && Application.isPlaying)
            {
                activeCrossFadeJobs.AddRange(newFadeJobs);
                runningCrossFadeCoroutine = StartCoroutine(RunCrossFade(newFadeJobs, crossFadeDurationSeconds));
            }
            else
            {
                // Edit mode or impossible coroutine: finalize transparent jobs immediately.
                for (int i = 0; i < newFadeJobs.Count; i++)
                    FinalizeCrossFadeJob(newFadeJobs[i]);
            }

            if (logSelection)
            {
                Debug.Log(
                    $"Applied crowding mesh variant {variantKey}. " +
                    $"Applied={applied}, crossFaded={crossFaded}, immediate={immediate}, " +
                    $"skippedSameReference={skippedSameReference}, " +
                    $"skippedSimilarTriangles={skippedSimilarTriangles}, " +
                    $"missingBindings={missingBinding}, missingMeshes={missingMesh}, " +
                    $"noRendererForCrossFade={noRendererForCrossFade}",
                    this
                );
            }
        }

        [ContextMenu("Crowding Mesh LOD/Restore Original Meshes")]
        public void RestoreOriginalMeshes()
        {
            CompleteAndCleanupActiveCrossFade();

            if (bindingsBySlotId.Count == 0)
                RebuildBindings();

            RebuildOriginalDictionaryFromSerializedRecords();

            if (originalMeshesBySlotId.Count == 0)
            {
                Debug.LogWarning(
                    "No original meshes were captured. Cannot restore. " +
                    "If you already applied a LOD in edit mode, use Undo or reload the scene if it was not saved.",
                    this
                );
                return;
            }

#if UNITY_EDITOR
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Restore Original Crowding Meshes");
#endif

            int restored = 0;
            int missingBinding = 0;

            foreach (KeyValuePair<string, Mesh> kvp in originalMeshesBySlotId)
            {
                if (!bindingsBySlotId.TryGetValue(kvp.Key, out MeshSlotBinding binding) ||
                    binding == null ||
                    !binding.IsAlive)
                {
                    missingBinding++;
                    continue;
                }

                if (kvp.Value == null)
                    continue;

                binding.SetMesh(kvp.Value);
                restored++;
            }

            activeVariantKey = string.Empty;
            MarkSelectorDirty();

            if (logSelection)
            {
                Debug.Log(
                    $"Original shared meshes restored: {restored}. " +
                    $"Missing bindings={missingBinding}",
                    this
                );
            }
        }

        [ContextMenu("Crowding Mesh LOD/Clear Stored Original Mesh Records")]
        public void ClearStoredOriginalMeshRecords()
        {
            CompleteAndCleanupActiveCrossFade();

            originalMeshesBySlotId.Clear();
            serializedOriginalMeshes.Clear();
            triangleCountCache.Clear();

            MarkSelectorDirty();

            if (logSelection)
                Debug.Log("Stored original mesh records cleared.", this);
        }

        [ContextMenu("Crowding Mesh LOD/Clear Triangle Count Cache")]
        public void ClearTriangleCountCache()
        {
            triangleCountCache.Clear();

            if (logSelection)
                Debug.Log("Triangle count cache cleared.", this);
        }

        private CrossFadeJob CreateCrossFadeJob(MeshSlotBinding binding, Mesh oldMesh, Mesh newMesh)
        {
            if (binding == null || !binding.CanCrossFade)
                return null;

            if (oldMesh == null || newMesh == null)
                return null;

            MeshFilter meshFilter = binding.meshFilter;
            MeshRenderer renderer = binding.meshRenderer;

            if (meshFilter == null || renderer == null)
                return null;

            Material[] originalMaterials = renderer.sharedMaterials;

            GameObject ghost = new GameObject(meshFilter.gameObject.name + "__LOD_FadeOut");
            ghost.hideFlags = HideFlags.DontSave;
            ghost.layer = meshFilter.gameObject.layer;
            ghost.transform.SetParent(meshFilter.transform.parent, false);
            ghost.transform.localPosition = meshFilter.transform.localPosition;
            ghost.transform.localRotation = meshFilter.transform.localRotation;
            ghost.transform.localScale = meshFilter.transform.localScale;

            MeshFilter ghostFilter = ghost.AddComponent<MeshFilter>();
            ghostFilter.sharedMesh = oldMesh;

            MeshRenderer ghostRenderer = ghost.AddComponent<MeshRenderer>();
            ghostRenderer.sharedMaterials = CreateFadeMaterials(originalMaterials, 1f);
            ghostRenderer.enabled = renderer.enabled;
            ghostRenderer.lightProbeUsage = renderer.lightProbeUsage;
            ghostRenderer.reflectionProbeUsage = renderer.reflectionProbeUsage;
            ghostRenderer.probeAnchor = renderer.probeAnchor;
            ghostRenderer.receiveShadows = renderer.receiveShadows;
            ghostRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            if (disableGhostShadows)
                ghostRenderer.shadowCastingMode = ShadowCastingMode.Off;
            else
                ghostRenderer.shadowCastingMode = renderer.shadowCastingMode;

            Material[] fadeInMaterials = CreateFadeMaterials(originalMaterials, 0f);

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Undo.RecordObject(meshFilter, "CrossFade Set New Mesh");
                Undo.RecordObject(renderer, "CrossFade Set Fade Materials");
            }
#endif

            meshFilter.sharedMesh = newMesh;
            renderer.sharedMaterials = fadeInMaterials;

            CrossFadeJob job = new CrossFadeJob
            {
                binding = binding,
                renderer = renderer,
                originalMaterials = originalMaterials,
                fadeInMaterials = fadeInMaterials,
                ghost = ghost,
                ghostRenderer = ghostRenderer,
                ghostMaterials = ghostRenderer.sharedMaterials
            };

            if (logCrossFade)
            {
                Debug.Log(
                    $"Created LOD cross-fade for slot '{binding.slotId}'. " +
                    $"Old triangles={GetTriangleCountCached(oldMesh)}, New triangles={GetTriangleCountCached(newMesh)}",
                    this
                );
            }

            return job;
        }

        private IEnumerator RunCrossFade(List<CrossFadeJob> jobs, float durationSeconds)
        {
            float start = Time.unscaledTime;
            float duration = Mathf.Max(0.001f, durationSeconds);

            while (true)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - start) / duration);

                for (int i = 0; i < jobs.Count; i++)
                    ApplyFadeToJob(jobs[i], t);

                if (t >= 1f)
                    break;

                yield return null;
            }

            for (int i = 0; i < jobs.Count; i++)
                FinalizeCrossFadeJob(jobs[i]);

            for (int i = 0; i < jobs.Count; i++)
                activeCrossFadeJobs.Remove(jobs[i]);

            runningCrossFadeCoroutine = null;
        }

        private void ApplyFadeToJob(CrossFadeJob job, float t)
        {
            if (job == null)
                return;

            SetMaterialsAlpha(job.fadeInMaterials, t);
            SetMaterialsAlpha(job.ghostMaterials, 1f - t);
        }

        private void FinalizeCrossFadeJob(CrossFadeJob job)
        {
            if (job == null)
                return;

            if (job.renderer != null && restoreOpaqueMaterialsAfterFade)
                job.renderer.sharedMaterials = job.originalMaterials;

            DestroyMaterialArray(job.fadeInMaterials);
            DestroyMaterialArray(job.ghostMaterials);

            if (job.ghost != null)
                DestroyObjectSafe(job.ghost);
        }

        private void CompleteAndCleanupActiveCrossFade()
        {
            if (runningCrossFadeCoroutine != null && Application.isPlaying)
            {
                StopCoroutine(runningCrossFadeCoroutine);
                runningCrossFadeCoroutine = null;
            }

            if (activeCrossFadeJobs.Count == 0)
                return;

            for (int i = 0; i < activeCrossFadeJobs.Count; i++)
                FinalizeCrossFadeJob(activeCrossFadeJobs[i]);

            activeCrossFadeJobs.Clear();
        }

        private Material[] CreateFadeMaterials(Material[] sourceMaterials, float alpha)
        {
            if (sourceMaterials == null || sourceMaterials.Length == 0)
                return new Material[0];

            Material[] result = new Material[sourceMaterials.Length];

            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                Material src = sourceMaterials[i];

                if (src == null)
                {
                    result[i] = null;
                    continue;
                }

                Material copy = new Material(src);
                copy.name = src.name + "__LOD_Fade";
                copy.hideFlags = HideFlags.DontSave;

                ConfigureMaterialForTransparency(copy);
                SetMaterialAlpha(copy, alpha);

                result[i] = copy;
            }

            return result;
        }

        private void ConfigureMaterialForTransparency(Material material)
        {
            if (material == null)
                return;

            // URP Lit / Universal shaders.
            if (material.HasProperty("_Surface"))
                material.SetFloat("_Surface", 1f);

            if (material.HasProperty("_Blend"))
                material.SetFloat("_Blend", 0f);

            if (material.HasProperty("_AlphaClip"))
                material.SetFloat("_AlphaClip", 0f);

            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);

            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);

            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 0f);

            // Built-in Standard shader compatibility.
            if (material.HasProperty("_Mode"))
                material.SetFloat("_Mode", 3f); // Transparent.

            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)RenderQueue.Transparent;
        }

        private void SetMaterialsAlpha(Material[] materials, float alpha)
        {
            if (materials == null)
                return;

            for (int i = 0; i < materials.Length; i++)
                SetMaterialAlpha(materials[i], alpha);
        }

        private void SetMaterialAlpha(Material material, float alpha)
        {
            if (material == null)
                return;

            alpha = Mathf.Clamp01(alpha);

            if (material.HasProperty("_BaseColor"))
            {
                Color c = material.GetColor("_BaseColor");
                c.a = alpha;
                material.SetColor("_BaseColor", c);
            }

            if (material.HasProperty("_Color"))
            {
                Color c = material.GetColor("_Color");
                c.a = alpha;
                material.SetColor("_Color", c);
            }
        }

        private void DestroyMaterialArray(Material[] materials)
        {
            if (materials == null)
                return;

            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null)
                    DestroyObjectSafe(materials[i]);
            }
        }

        private void DestroyObjectSafe(Object obj)
        {
            if (obj == null)
                return;

            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        private bool AreTriangleCountsSimilar(
            Mesh currentMesh,
            Mesh newMesh,
            float tolerance)
        {
            if (newMesh == null)
                return false;

            if (currentMesh == null)
                return false;

            int currentTriangles = GetTriangleCountCached(currentMesh);
            int newTriangles = GetTriangleCountCached(newMesh);

            float relativeDiff = RelativeTriangleDifference(currentTriangles, newTriangles);

            return relativeDiff <= Mathf.Max(0f, tolerance);
        }

        private float RelativeTriangleDifference(int a, int b)
        {
            int denom = Mathf.Max(a, b, 1);
            return Mathf.Abs(a - b) / (float)denom;
        }

        private int GetTriangleCountCached(Mesh mesh)
        {
            if (mesh == null)
                return 0;

            if (triangleCountCache.TryGetValue(mesh, out int cached))
                return cached;

            int triangles = CountTriangles(mesh);
            triangleCountCache[mesh] = triangles;

            return triangles;
        }

        private int CountTriangles(Mesh mesh)
        {
            if (mesh == null)
                return 0;

            int triangles = 0;

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                    continue;

                triangles += (int)mesh.GetIndexCount(s) / 3;
            }

            return triangles;
        }

        private void RebuildOriginalDictionaryFromSerializedRecords()
        {
            originalMeshesBySlotId.Clear();

            for (int i = 0; i < serializedOriginalMeshes.Count; i++)
            {
                OriginalMeshRecord record = serializedOriginalMeshes[i];

                if (record == null)
                    continue;

                if (string.IsNullOrWhiteSpace(record.slotId))
                    continue;

                if (record.originalMesh == null)
                    continue;

                originalMeshesBySlotId[record.slotId] = record.originalMesh;
            }
        }

        private void AddOrUpdateSerializedOriginal(string slotId, Mesh mesh)
        {
            for (int i = 0; i < serializedOriginalMeshes.Count; i++)
            {
                if (serializedOriginalMeshes[i].slotId == slotId)
                {
                    serializedOriginalMeshes[i].originalMesh = mesh;
                    return;
                }
            }

            serializedOriginalMeshes.Add(new OriginalMeshRecord
            {
                slotId = slotId,
                originalMesh = mesh
            });
        }

        private void MarkSelectorDirty()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                EditorUtility.SetDirty(this);
#endif
        }

        [System.Serializable]
        private class OriginalMeshRecord
        {
            public string slotId;
            public Mesh originalMesh;
        }

        private class CrossFadeJob
        {
            public MeshSlotBinding binding;
            public MeshRenderer renderer;
            public Material[] originalMaterials;
            public Material[] fadeInMaterials;
            public GameObject ghost;
            public MeshRenderer ghostRenderer;
            public Material[] ghostMaterials;
        }

        private class MeshSlotBinding
        {
            public string slotId;
            public MeshFilter meshFilter;
            public MeshRenderer meshRenderer;
            public SkinnedMeshRenderer skinnedMeshRenderer;

            public bool IsAlive => meshFilter != null || skinnedMeshRenderer != null;

            public bool CanCrossFade =>
                meshFilter != null &&
                meshRenderer != null &&
                meshFilter.sharedMesh != null;

            public static MeshSlotBinding FromMeshFilter(
                string slotId,
                MeshFilter mf,
                MeshRenderer renderer)
            {
                return new MeshSlotBinding
                {
                    slotId = slotId,
                    meshFilter = mf,
                    meshRenderer = renderer
                };
            }

            public static MeshSlotBinding FromSkinnedMeshRenderer(
                string slotId,
                SkinnedMeshRenderer smr)
            {
                return new MeshSlotBinding
                {
                    slotId = slotId,
                    skinnedMeshRenderer = smr
                };
            }

            public Mesh GetMesh()
            {
                if (meshFilter != null)
                    return meshFilter.sharedMesh;

                if (skinnedMeshRenderer != null)
                    return skinnedMeshRenderer.sharedMesh;

                return null;
            }

            public void SetMesh(Mesh mesh)
            {
                if (mesh == null)
                    return;

#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    if (meshFilter != null)
                        Undo.RecordObject(meshFilter, "Set Crowding MeshFilter Mesh");

                    if (skinnedMeshRenderer != null)
                        Undo.RecordObject(skinnedMeshRenderer, "Set Crowding SkinnedMesh Mesh");
                }
#endif

                if (meshFilter != null)
                    meshFilter.sharedMesh = mesh;
                else if (skinnedMeshRenderer != null)
                    skinnedMeshRenderer.sharedMesh = mesh;

#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    if (meshFilter != null)
                        EditorUtility.SetDirty(meshFilter);

                    if (skinnedMeshRenderer != null)
                        EditorUtility.SetDirty(skinnedMeshRenderer);
                }
#endif
            }
        }
    }
}
