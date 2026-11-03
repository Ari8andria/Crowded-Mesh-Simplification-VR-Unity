# Crowded-Mesh-Simplification-VR-Unity
A simplification strategy for dense VR scenes based on the Visual Crowding effect

## Script description
Main scripts:
- CrowdingAwarePipelineController: coordinates the crowding-aware simplification pipeline.
- CrowdingObjectScanner and CrowdingAngularFootprintUtility: collect scene object information and compute angular footprints.
- CrowdingSegmentation and TargetFlankerSelector: group objects and identify targets and flankers.
- CrowdingWeightCalculator and CrowdingBudgetPlanner: compute crowding weights and plan triangle-budget redistribution.
- CrowdingSharedMeshLODSelector: selects mesh LOD variants.
- CrowdingDisplayHandler and CrowdingMultiSceneSessionManager: manage stimulus presentation and experimental sessions across scenes.
- CrowdingContinuousEyeTrackerLogger: records eye-tracking data.
- CrowdingRatingButton: handles participant ratings.
- CrowdingTriangleBudgetCounter and CrowdingRenderPerformanceLogger: monitor triangle counts and rendering performance.

The Editor folder contains tools for building, importing, and validating mesh LODs. The Blender folder contains Python scripts for generating decimated mesh variants.
It is also possible to generate LODs with Unity Mesh Simplifier (https://github.com/Whinarn/UnityMeshSimplifier.git) instead of Blender.
Blender may yield a better visual result than the version with Unity Mesh Simplifier but it is much more heavier and takes much more time to generate the LODs with Blender.
