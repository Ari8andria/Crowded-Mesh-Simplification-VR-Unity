using UnityEngine;

namespace CrowdingAwareLOD
{
    public enum CrowdingLODPreviewVariantFamily
    {
        Original,
        PreNormalizedReference,
        PerFixation,
        GlobalScene,
        CustomVariantKey
    }

    public enum CrowdingLODPreviewCondition
    {
        Uniform,
        Weighted
    }

    /// <summary>
    /// Inspector helper for previewing crowding LOD variants without modifying the main pipeline.
    /// This component can coexist with the previous CrowdingLODPreviewControls.
    ///
    /// Supported variant keys:
    /// - reference_normalized
    /// - F00_K075_uniform / F00_K075_weighted
    /// - K075_uniform_global / K075_weighted_global
    /// - any custom variant key
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public class CrowdingLODPreviewControlsPlus : MonoBehaviour
    {
        [Header("References")]
        public CrowdingSharedMeshLODSelector selector;

        [Header("Variant to preview")]
        public CrowdingLODPreviewVariantFamily variantFamily = CrowdingLODPreviewVariantFamily.GlobalScene;
        public int fixationIndex = 0;
        public float keepBudgetK = 0.75f;
        public CrowdingLODPreviewCondition condition = CrowdingLODPreviewCondition.Uniform;

        [Header("Special variants")]
        public string preNormalizedReferenceVariantKey = "reference_normalized";
        public string customVariantKey = "K075_weighted_global";

        [Header("Debug")]
        public bool logAppliedVariant = true;

        public string CurrentComputedVariantKey
        {
            get
            {
                switch (variantFamily)
                {
                    case CrowdingLODPreviewVariantFamily.Original:
                        return "reference";

                    case CrowdingLODPreviewVariantFamily.PreNormalizedReference:
                        return preNormalizedReferenceVariantKey;

                    case CrowdingLODPreviewVariantFamily.PerFixation:
                        return BuildPerFixationVariantKey(fixationIndex, keepBudgetK, condition);

                    case CrowdingLODPreviewVariantFamily.GlobalScene:
                        return BuildGlobalVariantKey(keepBudgetK, condition);

                    case CrowdingLODPreviewVariantFamily.CustomVariantKey:
                        return customVariantKey;

                    default:
                        return string.Empty;
                }
            }
        }

        private void Reset()
        {
            AutoFindSelector();
        }

        private void OnValidate()
        {
            keepBudgetK = Mathf.Clamp01(keepBudgetK);
            fixationIndex = Mathf.Max(0, fixationIndex);
        }

        [ContextMenu("Preview/Auto Find Selector")]
        public void AutoFindSelector()
        {
            if (selector != null)
                return;

#if UNITY_2023_1_OR_NEWER
            selector = FindFirstObjectByType<CrowdingSharedMeshLODSelector>();
#else
            selector = FindObjectOfType<CrowdingSharedMeshLODSelector>();
#endif
        }

        [ContextMenu("Preview/Apply Selected Variant")]
        public void ApplySelectedVariant()
        {
            EnsureSelector();

            if (selector == null)
            {
                Debug.LogError("CrowdingLODPreviewControlsPlus has no selector assigned.", this);
                return;
            }

            if (variantFamily == CrowdingLODPreviewVariantFamily.Original)
            {
                selector.RestoreOriginalMeshes();

                if (logAppliedVariant)
                    Debug.Log("Preview applied original reference via RestoreOriginalMeshes().", this);

                return;
            }

            string key = CurrentComputedVariantKey;

            if (string.IsNullOrWhiteSpace(key))
            {
                Debug.LogError("Computed variant key is empty.", this);
                return;
            }

            selector.ApplyVariant(key);

            if (logAppliedVariant)
                Debug.Log($"Preview applied variant: {key}", this);
        }

        [ContextMenu("Preview/Restore Original Meshes")]
        public void RestoreOriginalMeshes()
        {
            EnsureSelector();

            if (selector == null)
            {
                Debug.LogError("CrowdingLODPreviewControlsPlus has no selector assigned.", this);
                return;
            }

            selector.RestoreOriginalMeshes();

            if (logAppliedVariant)
                Debug.Log("Preview restored original meshes.", this);
        }

        [ContextMenu("Preview/Apply Pre-Normalized Reference")]
        public void ApplyPreNormalizedReference()
        {
            EnsureSelector();

            if (selector == null)
            {
                Debug.LogError("CrowdingLODPreviewControlsPlus has no selector assigned.", this);
                return;
            }

            selector.ApplyVariant(preNormalizedReferenceVariantKey);

            if (logAppliedVariant)
                Debug.Log($"Preview applied pre-normalized reference: {preNormalizedReferenceVariantKey}", this);
        }

        [ContextMenu("Preview/Apply Global K075 Uniform")]
        public void ApplyGlobalK075Uniform() => ApplyGlobalPreset(0.75f, CrowdingLODPreviewCondition.Uniform);

        [ContextMenu("Preview/Apply Global K075 Weighted")]
        public void ApplyGlobalK075Weighted() => ApplyGlobalPreset(0.75f, CrowdingLODPreviewCondition.Weighted);

        [ContextMenu("Preview/Apply Global K050 Uniform")]
        public void ApplyGlobalK050Uniform() => ApplyGlobalPreset(0.50f, CrowdingLODPreviewCondition.Uniform);

        [ContextMenu("Preview/Apply Global K050 Weighted")]
        public void ApplyGlobalK050Weighted() => ApplyGlobalPreset(0.50f, CrowdingLODPreviewCondition.Weighted);

        [ContextMenu("Preview/Apply Global K025 Uniform")]
        public void ApplyGlobalK025Uniform() => ApplyGlobalPreset(0.25f, CrowdingLODPreviewCondition.Uniform);

        [ContextMenu("Preview/Apply Global K025 Weighted")]
        public void ApplyGlobalPresetK025Weighted() => ApplyGlobalPreset(0.25f, CrowdingLODPreviewCondition.Weighted);

        public void ApplyGlobalPreset(float k, CrowdingLODPreviewCondition previewCondition)
        {
            EnsureSelector();

            if (selector == null)
            {
                Debug.LogError("CrowdingLODPreviewControlsPlus has no selector assigned.", this);
                return;
            }

            keepBudgetK = k;
            condition = previewCondition;
            variantFamily = CrowdingLODPreviewVariantFamily.GlobalScene;

            string key = BuildGlobalVariantKey(k, previewCondition);
            selector.ApplyVariant(key);

            if (logAppliedVariant)
                Debug.Log($"Preview applied global variant: {key}", this);
        }

        public static string BuildPerFixationVariantKey(
            int fixationIndex,
            float keepRatioK,
            CrowdingLODPreviewCondition previewCondition)
        {
            string k = FormatK(keepRatioK);
            string conditionLabel = FormatCondition(previewCondition);
            return $"F{Mathf.Max(0, fixationIndex):00}_{k}_{conditionLabel}";
        }

        public static string BuildGlobalVariantKey(
            float keepRatioK,
            CrowdingLODPreviewCondition previewCondition)
        {
            string k = FormatK(keepRatioK);
            string conditionLabel = FormatCondition(previewCondition);
            return $"{k}_{conditionLabel}_global";
        }

        public static string FormatK(float keepRatioK)
        {
            int kInt = Mathf.RoundToInt(Mathf.Clamp01(keepRatioK) * 100f);
            return $"K{kInt:000}";
        }

        public static string FormatCondition(CrowdingLODPreviewCondition previewCondition)
        {
            return previewCondition == CrowdingLODPreviewCondition.Weighted
                ? "weighted"
                : "uniform";
        }

        private void EnsureSelector()
        {
            if (selector == null)
                AutoFindSelector();
        }
    }
}
