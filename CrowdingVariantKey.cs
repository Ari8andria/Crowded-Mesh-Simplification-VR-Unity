using System.Text;
using UnityEngine;

namespace CrowdingAwareLOD
{
    public static class CrowdingVariantKey
    {
        public static string Build(int fixationIndex, float keepRatioK, CrowdingDecimationCondition condition)
        {
            int k100 = Mathf.RoundToInt(Mathf.Clamp01(keepRatioK) * 100f);
            string conditionName = condition == CrowdingDecimationCondition.WeightedCrowding ? "weighted" : "uniform";
            return string.Format("F{0:00}_K{1:000}_{2}", Mathf.Max(0, fixationIndex), k100, conditionName);
        }

        public static string BuildGlobal(float keepRatioK, CrowdingDecimationCondition condition)
        {
            int k100 = Mathf.RoundToInt(Mathf.Clamp01(keepRatioK) * 100f);
            string conditionName = condition == CrowdingDecimationCondition.WeightedCrowding ? "weighted" : "uniform";
            return string.Format("K{0:000}_{1}_global", k100, conditionName);
        }

        public static bool IsGlobalVariantKey(string variantKey)
        {
            return !string.IsNullOrWhiteSpace(variantKey) && variantKey.EndsWith("_global");
        }

        public static string SanitizeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "unnamed";
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                bool ok = char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.';
                sb.Append(ok ? c : '_');
            }
            return sb.ToString();
        }
    }
}
