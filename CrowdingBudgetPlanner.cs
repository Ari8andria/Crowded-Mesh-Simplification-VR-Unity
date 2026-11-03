using UnityEngine;

namespace CrowdingAwareLOD
{
    public static class CrowdingBudgetPlanner
    {
        public static void ApplyTriangleBudget(CrowdingGroupDecimationPlan groupPlan, CrowdingBudgetSettings settings)
        {
            if (groupPlan == null || groupPlan.objectPlans.Count == 0)
                return;

            if (settings == null)
                settings = new CrowdingBudgetSettings();

            switch (settings.budgetStrategy)
            {
                case CrowdingBudgetStrategy.ExtraSimplifyTargetsOnly:
                    ApplyExtraSimplifyTargetsOnly(groupPlan, settings);
                    break;

                case CrowdingBudgetStrategy.RedistributeWithinGroup:
                default:
                    ApplyRedistributeWithinGroup(groupPlan, settings);
                    break;
            }
        }

        /// <summary>
        /// Old behavior.
        /// Targets are simplified according to K * targetWeight.
        /// Non-targets/flankers are adjusted so that the whole group keeps approximately K of its initial triangles.
        /// </summary>
        private static void ApplyRedistributeWithinGroup(CrowdingGroupDecimationPlan groupPlan, CrowdingBudgetSettings settings)
        {
            float k = Mathf.Clamp01(settings.groupKeepRatioK);
            float totalTriangles = 0f;
            float targetFinalTriangles = 0f;
            float nonTargetTriangles = 0f;

            groupPlan.requestedGroupKeepRatio = k;
            groupPlan.budgetRedistributionClamped = false;

            for (int i = 0; i < groupPlan.objectPlans.Count; i++)
            {
                CrowdingObjectDecimationPlan p = groupPlan.objectPlans[i];
                float t = Mathf.Max(0, p.InitialTriangles);
                totalTriangles += t;

                if (p.role == CrowdingRole.Target)
                {
                    float ratio = Mathf.Clamp(k * Mathf.Clamp01(p.targetWeight), settings.minTargetKeepRatio, 1f);
                    p.keepRatio = ratio;
                    targetFinalTriangles += ratio * t;
                }
                else
                {
                    nonTargetTriangles += t;
                }
            }

            if (totalTriangles <= 0f)
            {
                SetAllRatios(groupPlan, 1f);
                groupPlan.effectiveGroupKeepRatio = 1f;
                return;
            }

            float requestedFinalTriangles = k * totalTriangles;
            float nonTargetRatio = 1f;

            if (nonTargetTriangles > 0f)
            {
                nonTargetRatio = (requestedFinalTriangles - targetFinalTriangles) / nonTargetTriangles;

                if (nonTargetRatio > 1f)
                {
                    nonTargetRatio = 1f;
                    groupPlan.budgetRedistributionClamped = true;
                }

                nonTargetRatio = Mathf.Clamp(nonTargetRatio, settings.minNonTargetKeepRatio, 1f);
            }
            else
            {
                groupPlan.budgetRedistributionClamped = true;
            }

            for (int i = 0; i < groupPlan.objectPlans.Count; i++)
            {
                CrowdingObjectDecimationPlan p = groupPlan.objectPlans[i];
                if (p.role != CrowdingRole.Target)
                    p.keepRatio = nonTargetRatio;
            }

            groupPlan.effectiveGroupKeepRatio = groupPlan.EstimatedFinalTriangles / Mathf.Max(1f, groupPlan.InitialTriangles);

            if (groupPlan.budgetRedistributionClamped && settings.warnIfBudgetCannotBeRedistributed)
            {
                Debug.LogWarning(string.Format(
                    "Crowding group {0}: requested K={1:F2}, effective K={2:F2}. Non-target ratio had to be clamped, so the exact triangle budget could not be redistributed.",
                    groupPlan.groupId, k, groupPlan.effectiveGroupKeepRatio));
            }
        }

        /// <summary>
        /// New behavior.
        /// All objects receive the baseline K. Targets receive additional simplification according to their
        /// crowding weight. No compensation is applied to flankers/non-targets.
        ///
        /// Uniform condition is unchanged: keepRatio = K for every object.
        /// Weighted condition becomes: non-targets/flankers = K, targets = K * Lerp(1, targetWeight, strength).
        /// Therefore the weighted condition can have a lower total triangle count than the uniform condition.
        /// </summary>
        private static void ApplyExtraSimplifyTargetsOnly(CrowdingGroupDecimationPlan groupPlan, CrowdingBudgetSettings settings)
        {
            float k = Mathf.Clamp01(settings.groupKeepRatioK);
            float strength = Mathf.Clamp01(settings.targetExtraSimplificationStrength);

            groupPlan.requestedGroupKeepRatio = k;
            groupPlan.budgetRedistributionClamped = false;

            if (groupPlan.InitialTriangles <= 0)
            {
                SetAllRatios(groupPlan, 1f);
                groupPlan.effectiveGroupKeepRatio = 1f;
                return;
            }

            for (int i = 0; i < groupPlan.objectPlans.Count; i++)
            {
                CrowdingObjectDecimationPlan p = groupPlan.objectPlans[i];

                if (p.role == CrowdingRole.Target)
                {
                    float w = Mathf.Clamp01(p.targetWeight);
                    float extraFactor = Mathf.Lerp(1f, w, strength);
                    p.keepRatio = Mathf.Clamp(k * extraFactor, settings.minTargetKeepRatio, 1f);
                }
                else
                {
                    p.keepRatio = Mathf.Clamp(k, settings.minNonTargetKeepRatio, 1f);
                }
            }

            groupPlan.effectiveGroupKeepRatio = groupPlan.EstimatedFinalTriangles / Mathf.Max(1f, groupPlan.InitialTriangles);
        }

        private static void SetAllRatios(CrowdingGroupDecimationPlan groupPlan, float ratio)
        {
            ratio = Mathf.Clamp01(ratio);
            for (int i = 0; i < groupPlan.objectPlans.Count; i++)
                groupPlan.objectPlans[i].keepRatio = ratio;
        }
    }
}
