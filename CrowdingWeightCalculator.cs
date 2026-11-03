using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    public static class CrowdingWeightCalculator
    {
        public static CrowdingFixationPlan BuildFixationPlan(
            int fixationIndex,
            Transform fixationTransform,
            Camera cam,
            List<CrowdingGroupData> groups,
            CrowdingWeightSettings weightSettings,
            CrowdingBudgetSettings budgetSettings)
        {
            if (weightSettings == null) weightSettings = new CrowdingWeightSettings();
            if (budgetSettings == null) budgetSettings = new CrowdingBudgetSettings();

            CrowdingFixationPlan fixationPlan = new CrowdingFixationPlan();
            fixationPlan.fixationIndex = fixationIndex;
            fixationPlan.fixationTransform = fixationTransform;
            fixationPlan.fixationWorldPosition = fixationTransform != null ? fixationTransform.position : cam.transform.position + cam.transform.forward * 10f;

            if (groups == null || cam == null) return fixationPlan;

            for (int g = 0; g < groups.Count; g++)
            {
                CrowdingGroupDecimationPlan groupPlan = BuildGroupPlanWorldAngular(groups[g], fixationPlan.fixationWorldPosition, cam, weightSettings, budgetSettings);
                fixationPlan.groupPlans.Add(groupPlan);
            }

            return fixationPlan;
        }

        public static CrowdingFixationPlan BuildFixationPlanFromPrecomputedAngular(
            int fixationIndex,
            Transform fixationTransform,
            Vector3 fixationWorldPosition,
            List<CrowdingGroupData> groups,
            CrowdingWeightSettings weightSettings,
            CrowdingBudgetSettings budgetSettings)
        {
            if (weightSettings == null) weightSettings = new CrowdingWeightSettings();
            if (budgetSettings == null) budgetSettings = new CrowdingBudgetSettings();

            CrowdingFixationPlan fixationPlan = new CrowdingFixationPlan();
            fixationPlan.fixationIndex = fixationIndex;
            fixationPlan.fixationTransform = fixationTransform;
            fixationPlan.fixationWorldPosition = fixationWorldPosition;

            if (groups == null) return fixationPlan;

            for (int g = 0; g < groups.Count; g++)
            {
                CrowdingGroupDecimationPlan groupPlan = BuildGroupPlanPrecomputedAngular(groups[g], weightSettings, budgetSettings);
                fixationPlan.groupPlans.Add(groupPlan);
            }

            return fixationPlan;
        }

        private static CrowdingGroupDecimationPlan BuildGroupPlanWorldAngular(
            CrowdingGroupData group,
            Vector3 fixationWorldPosition,
            Camera cam,
            CrowdingWeightSettings weightSettings,
            CrowdingBudgetSettings budgetSettings)
        {
            CrowdingGroupDecimationPlan groupPlan = CreateBaseGroupPlan(group, budgetSettings);

            Dictionary<CrowdingObjectSignature, CrowdingObjectDecimationPlan> planByObj = new Dictionary<CrowdingObjectSignature, CrowdingObjectDecimationPlan>();
            HashSet<CrowdingObjectSignature> targets = new HashSet<CrowdingObjectSignature>(group.targets);
            HashSet<CrowdingObjectSignature> usedAsFlanker = new HashSet<CrowdingObjectSignature>();

            for (int i = 0; i < group.members.Count; i++)
            {
                CrowdingObjectSignature obj = group.members[i];
                CrowdingObjectDecimationPlan p = CreateBaseObjectPlan(obj, targets.Contains(obj), budgetSettings.groupKeepRatioK);
                p.eccentricityDeg = CrowdingGeometryUtils.AngularDistanceDeg(cam, fixationWorldPosition, obj.worldCenter);

                groupPlan.objectPlans.Add(p);
                planByObj[obj] = p;
            }

            ApplyTargetWeightsWorldAngular(group, planByObj, usedAsFlanker, cam, weightSettings);
            MarkUsedFlankers(groupPlan, usedAsFlanker);
            CrowdingBudgetPlanner.ApplyTriangleBudget(groupPlan, budgetSettings);
            return groupPlan;
        }

        private static CrowdingGroupDecimationPlan BuildGroupPlanPrecomputedAngular(
            CrowdingGroupData group,
            CrowdingWeightSettings weightSettings,
            CrowdingBudgetSettings budgetSettings)
        {
            CrowdingGroupDecimationPlan groupPlan = CreateBaseGroupPlan(group, budgetSettings);

            Dictionary<CrowdingObjectSignature, CrowdingObjectDecimationPlan> planByObj = new Dictionary<CrowdingObjectSignature, CrowdingObjectDecimationPlan>();
            HashSet<CrowdingObjectSignature> targets = new HashSet<CrowdingObjectSignature>(group.targets);
            HashSet<CrowdingObjectSignature> usedAsFlanker = new HashSet<CrowdingObjectSignature>();

            for (int i = 0; i < group.members.Count; i++)
            {
                CrowdingObjectSignature obj = group.members[i];
                CrowdingObjectDecimationPlan p = CreateBaseObjectPlan(obj, targets.Contains(obj), budgetSettings.groupKeepRatioK);

                // In PerFixationLookAt mode, the fixation point is the center of the virtual view.
                // Therefore eccentricity is simply the angular offset from (0,0).
                p.eccentricityDeg = obj != null ? obj.angularPositionDeg.magnitude : 0f;

                groupPlan.objectPlans.Add(p);
                planByObj[obj] = p;
            }

            ApplyTargetWeightsPrecomputedAngular(group, planByObj, usedAsFlanker, weightSettings);
            MarkUsedFlankers(groupPlan, usedAsFlanker);
            CrowdingBudgetPlanner.ApplyTriangleBudget(groupPlan, budgetSettings);
            return groupPlan;
        }

        private static CrowdingGroupDecimationPlan CreateBaseGroupPlan(
            CrowdingGroupData group,
            CrowdingBudgetSettings budgetSettings)
        {
            CrowdingGroupDecimationPlan groupPlan = new CrowdingGroupDecimationPlan();
            groupPlan.group = group;
            groupPlan.groupId = group.groupId;
            groupPlan.requestedGroupKeepRatio = budgetSettings.groupKeepRatioK;
            return groupPlan;
        }

        private static CrowdingObjectDecimationPlan CreateBaseObjectPlan(
            CrowdingObjectSignature obj,
            bool isTarget,
            float defaultKeepRatio)
        {
            CrowdingObjectDecimationPlan p = new CrowdingObjectDecimationPlan();
            p.obj = obj;
            p.role = isTarget ? CrowdingRole.Target : CrowdingRole.NonTarget;
            p.keepRatio = defaultKeepRatio;
            p.targetWeight = 1f;
            p.boumaMean = float.PositiveInfinity;
            p.eccentricityDeg = 0f;
            return p;
        }

        private static void ApplyTargetWeightsWorldAngular(
            CrowdingGroupData group,
            Dictionary<CrowdingObjectSignature, CrowdingObjectDecimationPlan> planByObj,
            HashSet<CrowdingObjectSignature> usedAsFlanker,
            Camera cam,
            CrowdingWeightSettings weightSettings)
        {
            for (int t = 0; t < group.targetFlankerData.Count; t++)
            {
                CrowdingTargetFlankerData tf = group.targetFlankerData[t];
                if (tf == null || tf.target == null || !planByObj.ContainsKey(tf.target)) continue;

                CrowdingObjectDecimationPlan targetPlan = planByObj[tf.target];
                float ecc = Mathf.Max(weightSettings.minEccentricityDeg, targetPlan.eccentricityDeg);

                if (targetPlan.eccentricityDeg <= weightSettings.fovealProtectionEccentricityDeg)
                {
                    targetPlan.boumaMean = float.PositiveInfinity;
                    targetPlan.targetWeight = 1f;
                }
                else if (tf.flankers.Count > 0)
                {
                    float sumB = 0f;
                    int count = 0;
                    for (int f = 0; f < tf.flankers.Count; f++)
                    {
                        CrowdingObjectSignature flanker = tf.flankers[f];
                        float d = CrowdingGeometryUtils.AngularDistanceDeg(cam, tf.target.worldCenter, flanker.worldCenter);
                        sumB += d / ecc;
                        count++;

                        targetPlan.selectedFlankers.Add(flanker);
                        usedAsFlanker.Add(flanker);
                    }

                    targetPlan.boumaMean = count > 0 ? sumB / count : float.PositiveInfinity;
                    targetPlan.targetWeight = MapBoumaMeanToWeight(targetPlan.boumaMean, weightSettings);
                }
                else
                {
                    targetPlan.boumaMean = float.PositiveInfinity;
                    targetPlan.targetWeight = 1f;
                }
            }
        }

        private static void ApplyTargetWeightsPrecomputedAngular(
            CrowdingGroupData group,
            Dictionary<CrowdingObjectSignature, CrowdingObjectDecimationPlan> planByObj,
            HashSet<CrowdingObjectSignature> usedAsFlanker,
            CrowdingWeightSettings weightSettings)
        {
            for (int t = 0; t < group.targetFlankerData.Count; t++)
            {
                CrowdingTargetFlankerData tf = group.targetFlankerData[t];
                if (tf == null || tf.target == null || !planByObj.ContainsKey(tf.target)) continue;

                CrowdingObjectDecimationPlan targetPlan = planByObj[tf.target];
                float ecc = Mathf.Max(weightSettings.minEccentricityDeg, targetPlan.eccentricityDeg);

                if (targetPlan.eccentricityDeg <= weightSettings.fovealProtectionEccentricityDeg)
                {
                    targetPlan.boumaMean = float.PositiveInfinity;
                    targetPlan.targetWeight = 1f;
                }
                else if (tf.flankers.Count > 0)
                {
                    float sumB = 0f;
                    int count = 0;
                    for (int f = 0; f < tf.flankers.Count; f++)
                    {
                        CrowdingObjectSignature flanker = tf.flankers[f];
                        float d = Vector2.Distance(tf.target.angularPositionDeg, flanker.angularPositionDeg);
                        sumB += d / ecc;
                        count++;

                        targetPlan.selectedFlankers.Add(flanker);
                        usedAsFlanker.Add(flanker);
                    }

                    targetPlan.boumaMean = count > 0 ? sumB / count : float.PositiveInfinity;
                    targetPlan.targetWeight = MapBoumaMeanToWeight(targetPlan.boumaMean, weightSettings);
                }
                else
                {
                    targetPlan.boumaMean = float.PositiveInfinity;
                    targetPlan.targetWeight = 1f;
                }
            }
        }

        private static void MarkUsedFlankers(
            CrowdingGroupDecimationPlan groupPlan,
            HashSet<CrowdingObjectSignature> usedAsFlanker)
        {
            for (int i = 0; i < groupPlan.objectPlans.Count; i++)
            {
                CrowdingObjectDecimationPlan p = groupPlan.objectPlans[i];
                if (p.role != CrowdingRole.Target && usedAsFlanker.Contains(p.obj))
                    p.role = CrowdingRole.FlankerForAtLeastOneTarget;
            }
        }

        public static float MapBoumaMeanToWeight(float bMean, CrowdingWeightSettings s)
        {
            if (float.IsNaN(bMean) || float.IsInfinity(bMean)) return s.wUncrowded;
            if (bMean < s.b0) return s.wVeryCrowded;
            if (bMean < s.b1) return s.wCrowded;
            if (bMean < s.b2) return s.wModerate;
            if (bMean < s.b3) return s.wWeak;
            return s.wUncrowded;
        }
    }
}
