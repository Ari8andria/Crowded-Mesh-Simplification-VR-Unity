using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    public static class TargetFlankerSelector
    {
        public static void SelectTargetsAndFlankers(
            List<CrowdingGroupData> groups,
            Camera cam,
            CrowdingTargetSettings settings)
        {
            // The camera argument is kept for backward compatibility. Distances are now
            // computed in the precomputed angular coordinates of each signature, so this
            // works both for FixedReferenceCamera and PerFixationLookAt.
            SelectTargetsAndFlankers(groups, settings);
        }

        public static void SelectTargetsAndFlankers(
            List<CrowdingGroupData> groups,
            CrowdingTargetSettings settings)
        {
            if (groups == null) return;
            if (settings == null) settings = new CrowdingTargetSettings();

            System.Random rng = settings.deterministicRandom ? new System.Random(settings.randomSeed) : new System.Random();

            for (int i = 0; i < groups.Count; i++)
            {
                CrowdingGroupData group = groups[i];
                group.targets.Clear();
                group.targetFlankerData.Clear();

                if (group.members.Count < settings.minGroupSizeForTargets) continue;

                int nTargets = Mathf.RoundToInt(group.members.Count * settings.targetRatio);
                nTargets = Mathf.Clamp(nTargets, 1, Mathf.Max(1, group.members.Count - 1));

                List<CrowdingObjectSignature> targets = SelectSpatiallyDistributedTargets(group.members, nTargets, settings, rng);
                group.targets.AddRange(targets);

                HashSet<CrowdingObjectSignature> targetSet = new HashSet<CrowdingObjectSignature>(targets);

                for (int t = 0; t < targets.Count; t++)
                {
                    CrowdingObjectSignature target = targets[t];
                    CrowdingTargetFlankerData tf = new CrowdingTargetFlankerData();
                    tf.target = target;

                    List<CrowdingObjectSignature> candidates = new List<CrowdingObjectSignature>();
                    for (int m = 0; m < group.members.Count; m++)
                    {
                        CrowdingObjectSignature member = group.members[m];
                        if (!targetSet.Contains(member)) candidates.Add(member);
                    }

                    candidates.Sort((a, b) =>
                    {
                        float da = Vector2.Distance(target.angularPositionDeg, a.angularPositionDeg);
                        float db = Vector2.Distance(target.angularPositionDeg, b.angularPositionDeg);
                        return da.CompareTo(db);
                    });

                    int nFlankers = Mathf.Min(settings.flankersPerTarget, candidates.Count);
                    for (int f = 0; f < nFlankers; f++) tf.flankers.Add(candidates[f]);

                    group.targetFlankerData.Add(tf);
                }
            }
        }

        private static List<CrowdingObjectSignature> SelectSpatiallyDistributedTargets(
            List<CrowdingObjectSignature> members,
            int nTargets,
            CrowdingTargetSettings settings,
            System.Random rng)
        {
            List<CrowdingObjectSignature> selected = new List<CrowdingObjectSignature>();
            if (members == null || members.Count == 0 || nTargets <= 0) return selected;

            if (settings.distributionMode == SpatialTargetDistributionMode.CameraPlaneKMeans)
                return SelectBySimpleKMeans(members, nTargets, settings, rng);

            List<CrowdingObjectSignature> sorted = new List<CrowdingObjectSignature>(members);
            if (settings.distributionMode == SpatialTargetDistributionMode.VerticalRows)
                sorted.Sort((a, b) => a.angularPositionDeg.y.CompareTo(b.angularPositionDeg.y));
            else
                sorted.Sort((a, b) => a.angularPositionDeg.x.CompareTo(b.angularPositionDeg.x));

            for (int k = 0; k < nTargets; k++)
            {
                int start = Mathf.FloorToInt(k * sorted.Count / (float)nTargets);
                int endExclusive = Mathf.FloorToInt((k + 1) * sorted.Count / (float)nTargets);
                endExclusive = Mathf.Clamp(endExclusive, start + 1, sorted.Count);

                CrowdingObjectSignature candidate = SelectCandidateInsideFictiveGroup(
                    sorted,
                    start,
                    endExclusive,
                    settings,
                    rng,
                    selected
                );

                if (candidate != null && !selected.Contains(candidate)) selected.Add(candidate);
            }

            FillMissingTargets(selected, sorted, nTargets, settings);
            return selected;
        }

        private static CrowdingObjectSignature SelectCandidateInsideFictiveGroup(
            List<CrowdingObjectSignature> sorted,
            int start,
            int endExclusive,
            CrowdingTargetSettings settings,
            System.Random rng,
            List<CrowdingObjectSignature> alreadySelected)
        {
            if (sorted == null || sorted.Count == 0) return null;

            start = Mathf.Clamp(start, 0, sorted.Count - 1);
            endExclusive = Mathf.Clamp(endExclusive, start + 1, sorted.Count);

            List<CrowdingObjectSignature> fictiveGroup = ExtractFictiveGroupCandidates(
                sorted,
                start,
                endExclusive,
                alreadySelected,
                excludeAlreadySelected: true
            );

            if (fictiveGroup.Count == 0)
            {
                fictiveGroup = ExtractFictiveGroupCandidates(
                    sorted,
                    start,
                    endExclusive,
                    alreadySelected,
                    excludeAlreadySelected: false
                );
            }

            if (fictiveGroup.Count == 0) return null;

            switch (settings.targetSelectionCriterion)
            {
                case CrowdingTargetSelectionCriterion.MostTrianglesInFictiveGroup:
                    return SelectMostTriangles(fictiveGroup);

                case CrowdingTargetSelectionCriterion.MostSurroundedInFictiveGroup:
                    return SelectMostSurroundedByAngularMedoid(fictiveGroup);

                case CrowdingTargetSelectionCriterion.RandomInFictiveGroup:
                default:
                    int index = rng.Next(0, fictiveGroup.Count);
                    return fictiveGroup[index];
            }
        }

        private static List<CrowdingObjectSignature> ExtractFictiveGroupCandidates(
            List<CrowdingObjectSignature> sorted,
            int start,
            int endExclusive,
            List<CrowdingObjectSignature> alreadySelected,
            bool excludeAlreadySelected)
        {
            List<CrowdingObjectSignature> candidates = new List<CrowdingObjectSignature>();
            if (sorted == null) return candidates;

            for (int i = start; i < endExclusive; i++)
            {
                CrowdingObjectSignature candidate = sorted[i];
                if (candidate == null) continue;

                if (excludeAlreadySelected && alreadySelected != null && alreadySelected.Contains(candidate))
                    continue;

                candidates.Add(candidate);
            }

            return candidates;
        }

        private static void FillMissingTargets(
            List<CrowdingObjectSignature> selected,
            List<CrowdingObjectSignature> candidates,
            int nTargets,
            CrowdingTargetSettings settings)
        {
            if (selected == null || candidates == null) return;

            if (settings.targetSelectionCriterion == CrowdingTargetSelectionCriterion.MostTrianglesInFictiveGroup)
            {
                List<CrowdingObjectSignature> byTriangles = new List<CrowdingObjectSignature>(candidates);
                byTriangles.Sort((a, b) => CompareTriangleTargets(b, a));

                for (int i = 0; selected.Count < nTargets && i < byTriangles.Count; i++)
                {
                    if (!selected.Contains(byTriangles[i])) selected.Add(byTriangles[i]);
                }
            }
            else if (settings.targetSelectionCriterion == CrowdingTargetSelectionCriterion.MostSurroundedInFictiveGroup)
            {
                List<CrowdingObjectSignature> bySurroundedness = new List<CrowdingObjectSignature>(candidates);
                bySurroundedness.Sort((a, b) => CompareSurroundedTargets(a, b, candidates));

                for (int i = 0; selected.Count < nTargets && i < bySurroundedness.Count; i++)
                {
                    if (!selected.Contains(bySurroundedness[i])) selected.Add(bySurroundedness[i]);
                }
            }
            else
            {
                for (int i = 0; selected.Count < nTargets && i < candidates.Count; i++)
                {
                    if (!selected.Contains(candidates[i])) selected.Add(candidates[i]);
                }
            }
        }

        private static List<CrowdingObjectSignature> SelectBySimpleKMeans(
            List<CrowdingObjectSignature> members,
            int k,
            CrowdingTargetSettings settings,
            System.Random rng)
        {
            List<Vector2> centers = new List<Vector2>();
            List<CrowdingObjectSignature> shuffled = new List<CrowdingObjectSignature>(members);
            shuffled.Sort((a, b) => a.angularPositionDeg.x.CompareTo(b.angularPositionDeg.x));

            for (int i = 0; i < k; i++)
            {
                int idx = Mathf.Clamp(Mathf.RoundToInt(i * (shuffled.Count - 1) / Mathf.Max(1f, k - 1f)), 0, shuffled.Count - 1);
                centers.Add(shuffled[idx].angularPositionDeg);
            }

            int[] assignments = new int[members.Count];
            for (int iter = 0; iter < 8; iter++)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    float best = float.PositiveInfinity;
                    int bestK = 0;
                    for (int c = 0; c < centers.Count; c++)
                    {
                        float d = Vector2.SqrMagnitude(members[i].angularPositionDeg - centers[c]);
                        if (d < best) { best = d; bestK = c; }
                    }
                    assignments[i] = bestK;
                }

                Vector2[] sums = new Vector2[k];
                int[] counts = new int[k];
                for (int i = 0; i < members.Count; i++)
                {
                    sums[assignments[i]] += members[i].angularPositionDeg;
                    counts[assignments[i]]++;
                }
                for (int c = 0; c < k; c++)
                {
                    if (counts[c] > 0) centers[c] = sums[c] / counts[c];
                }
            }

            List<CrowdingObjectSignature> selected = new List<CrowdingObjectSignature>();
            for (int c = 0; c < k; c++)
            {
                CrowdingObjectSignature bestObj = null;
                float bestDistToCenter = float.PositiveInfinity;

                for (int i = 0; i < members.Count; i++)
                {
                    if (assignments[i] != c) continue;

                    if (settings.targetSelectionCriterion == CrowdingTargetSelectionCriterion.MostTrianglesInFictiveGroup)
                    {
                        if (IsBetterTriangleTarget(members[i], bestObj))
                            bestObj = members[i];
                    }
                    else if (settings.targetSelectionCriterion == CrowdingTargetSelectionCriterion.MostSurroundedInFictiveGroup)
                    {
                        continue;
                    }
                    else
                    {
                        float d = Vector2.SqrMagnitude(members[i].angularPositionDeg - centers[c]);
                        if (d < bestDistToCenter)
                        {
                            bestDistToCenter = d;
                            bestObj = members[i];
                        }
                    }
                }

                if (settings.targetSelectionCriterion == CrowdingTargetSelectionCriterion.MostSurroundedInFictiveGroup)
                {
                    List<CrowdingObjectSignature> fictiveGroup = new List<CrowdingObjectSignature>();
                    for (int i = 0; i < members.Count; i++)
                    {
                        if (assignments[i] == c)
                            fictiveGroup.Add(members[i]);
                    }

                    bestObj = SelectMostSurroundedByAngularMedoid(fictiveGroup);
                }

                if (bestObj != null && !selected.Contains(bestObj)) selected.Add(bestObj);
            }

            FillMissingTargets(selected, members, k, settings);
            return selected;
        }

        private static CrowdingObjectSignature SelectMostTriangles(List<CrowdingObjectSignature> candidates)
        {
            CrowdingObjectSignature best = null;
            if (candidates == null) return best;

            for (int i = 0; i < candidates.Count; i++)
            {
                CrowdingObjectSignature candidate = candidates[i];
                if (candidate == null) continue;

                if (IsBetterTriangleTarget(candidate, best))
                    best = candidate;
            }

            return best;
        }

        private static CrowdingObjectSignature SelectMostSurroundedByAngularMedoid(List<CrowdingObjectSignature> candidates)
        {
            CrowdingObjectSignature best = null;
            float bestSum = float.PositiveInfinity;

            if (candidates == null || candidates.Count == 0) return null;

            for (int i = 0; i < candidates.Count; i++)
            {
                CrowdingObjectSignature candidate = candidates[i];
                if (candidate == null) continue;

                float sum = SumAngularDistancesToOthers(candidate, candidates);

                if (sum < bestSum)
                {
                    bestSum = sum;
                    best = candidate;
                }
                else if (Mathf.Approximately(sum, bestSum) && IsBetterTriangleTarget(candidate, best))
                {
                    best = candidate;
                }
            }

            return best;
        }

        private static float SumAngularDistancesToOthers(CrowdingObjectSignature candidate, List<CrowdingObjectSignature> candidates)
        {
            if (candidate == null || candidates == null) return float.PositiveInfinity;

            float sum = 0f;

            for (int i = 0; i < candidates.Count; i++)
            {
                CrowdingObjectSignature other = candidates[i];
                if (other == null || ReferenceEquals(candidate, other)) continue;

                sum += Vector2.Distance(candidate.angularPositionDeg, other.angularPositionDeg);
            }

            return sum;
        }

        private static int CompareSurroundedTargets(CrowdingObjectSignature a, CrowdingObjectSignature b, List<CrowdingObjectSignature> referenceSet)
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;

            float sumA = SumAngularDistancesToOthers(a, referenceSet);
            float sumB = SumAngularDistancesToOthers(b, referenceSet);

            int sumCompare = sumA.CompareTo(sumB);
            if (sumCompare != 0) return sumCompare;

            return CompareTriangleTargets(b, a);
        }

        private static bool IsBetterTriangleTarget(CrowdingObjectSignature candidate, CrowdingObjectSignature currentBest)
        {
            if (candidate == null) return false;
            if (currentBest == null) return true;
            return CompareTriangleTargets(candidate, currentBest) > 0;
        }

        private static int CompareTriangleTargets(CrowdingObjectSignature a, CrowdingObjectSignature b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            int triangleCompare = a.triangleCount.CompareTo(b.triangleCount);
            if (triangleCompare != 0) return triangleCompare;

            string idA = string.IsNullOrWhiteSpace(a.objectId) ? string.Empty : a.objectId;
            string idB = string.IsNullOrWhiteSpace(b.objectId) ? string.Empty : b.objectId;
            int idCompare = string.CompareOrdinal(idB, idA);
            if (idCompare != 0) return idCompare;

            string nameA = a.root != null ? a.root.name : string.Empty;
            string nameB = b.root != null ? b.root.name : string.Empty;
            return string.CompareOrdinal(nameB, nameA);
        }
    }
}
