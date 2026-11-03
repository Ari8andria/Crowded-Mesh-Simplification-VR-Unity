using UnityEngine;

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Uniform = every object in a group receives the same keep ratio K.
    /// WeightedCrowding = targets receive K*w_t and non-target/flanker objects receive the remaining triangle budget.
    /// </summary>
    public enum CrowdingDecimationCondition
    {
        Uniform = 0,
        WeightedCrowding = 1
    }
}
