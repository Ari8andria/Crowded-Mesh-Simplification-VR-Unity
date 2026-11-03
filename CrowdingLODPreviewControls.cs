using UnityEngine;

namespace CrowdingAwareLOD
{
    public class CrowdingLODPreviewControls : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        private CrowdingSharedMeshLODSelector selector;

        [Header("Variant to preview")]
        [SerializeField]
        private int fixationIndex = 0;

        [SerializeField]
        private float keepBudgetK = 0.6f;

        [SerializeField]
        private CrowdingDecimationCondition condition = CrowdingDecimationCondition.WeightedCrowding;

        private void OnValidate()
        {
            if (selector == null)
                selector = FindFirstObjectByType<CrowdingSharedMeshLODSelector>();
        }

        [ContextMenu("Apply Selected Variant")]
        public void ApplySelectedVariant()
        {
            if (selector == null)
            {
                Debug.LogError("No CrowdingSharedMeshLODSelector assigned.", this);
                return;
            }

            selector.ApplyCombination(fixationIndex, keepBudgetK, condition);

            Debug.Log(
                $"Applied LOD variant: fixation={fixationIndex}, K={keepBudgetK}, condition={condition}",
                this
            );
        }

        [ContextMenu("Apply F00 K060 Weighted")]
        public void ApplyF00K060Weighted()
        {
            ApplyVariant(0, 0.6f, CrowdingDecimationCondition.WeightedCrowding);
        }

        [ContextMenu("Apply F00 K060 Uniform")]
        public void ApplyF00K060Uniform()
        {
            ApplyVariant(0, 0.6f, CrowdingDecimationCondition.Uniform);
        }

        [ContextMenu("Apply F00 K030 Weighted")]
        public void ApplyF00K030Weighted()
        {
            ApplyVariant(0, 0.3f, CrowdingDecimationCondition.WeightedCrowding);
        }

        [ContextMenu("Apply F00 K030 Uniform")]
        public void ApplyF00K030Uniform()
        {
            ApplyVariant(0, 0.3f, CrowdingDecimationCondition.Uniform);
        }

        [ContextMenu("Apply F00 K075 Weighted")]
        public void ApplyF00K075Weighted()
        {
            ApplyVariant(0, 0.75f, CrowdingDecimationCondition.WeightedCrowding);
        }

        [ContextMenu("Apply F00 K075 Uniform")]
        public void ApplyF00K075Uniform()
        {
            ApplyVariant(0, 0.75f, CrowdingDecimationCondition.Uniform);
        }

        [ContextMenu("Apply F00 K050 Weighted")]
        public void ApplyF00K050Weighted()
        {
            ApplyVariant(0, 0.5f, CrowdingDecimationCondition.WeightedCrowding);
        }

        [ContextMenu("Apply F00 K050 Uniform")]
        public void ApplyF00K050Uniform()
        {
            ApplyVariant(0, 0.5f, CrowdingDecimationCondition.Uniform);
        }

        [ContextMenu("Apply F00 K025 Weighted")]
        public void ApplyF00K025Weighted()
        {
            ApplyVariant(0, 0.25f, CrowdingDecimationCondition.WeightedCrowding);
        }

        [ContextMenu("Apply F00 K025 Uniform")]
        public void ApplyF00K025Uniform()
        {
            ApplyVariant(0, 0.25f, CrowdingDecimationCondition.Uniform);
        }


        [ContextMenu("Restore Original Meshes")]
        public void RestoreOriginalMeshes()
        {
            if (selector == null)
            {
                Debug.LogError("No CrowdingSharedMeshLODSelector assigned.", this);
                return;
            }

            selector.RestoreOriginalMeshes();

            Debug.Log("Restored original meshes.", this);
        }

        private void ApplyVariant(
            int fixation,
            float k,
            CrowdingDecimationCondition decimationCondition)
        {
            if (selector == null)
            {
                Debug.LogError("No CrowdingSharedMeshLODSelector assigned.", this);
                return;
            }

            fixationIndex = fixation;
            keepBudgetK = k;
            condition = decimationCondition;

            selector.ApplyCombination(fixationIndex, keepBudgetK, condition);

            Debug.Log(
                $"Applied LOD variant: F{fixationIndex:00}, K={keepBudgetK}, condition={condition}",
                this
            );
        }
    }
}