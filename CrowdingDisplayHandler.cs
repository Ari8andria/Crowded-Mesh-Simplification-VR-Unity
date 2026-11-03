using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CrowdingAwareLOD
{
    public enum CrowdingDisplayAdvanceMode { Timeout, KeyPress, TimeoutOrKeyPress }
    public enum CrowdingDisplayConditionType { Reference, Uniform, Weighted }
    public enum CrowdingFixationMarkerVisibilityMode { DoNotManage, SetGameObjectActive, ToggleRenderersOnly }
    public enum CrowdingFixationMarkerEndBehavior { HideAll, RestoreInitialState, KeepCurrentVisible }
    public enum CrowdingRatingGranularity { ConditionEnd, EachFixationPoint }
    public enum CrowdingExperimentParadigm { DirectRating, ReferenceVsDistortionRating, UniformVsWeightedPreference }
    public enum CrowdingPairwiseChoice { FirstPresentation, SecondPresentation, NoPreference }

    [Serializable]
    public class CrowdingDisplayStepInfo
    {
        public int globalStepIndex;
        public int combinationOrderIndex;
        public int fixationOrderIndex;
        public int fixationIndex;
        public string fixationName;
        public CrowdingDisplayConditionType conditionType;
        public bool isReference;
        public float keepRatioK;
        public CrowdingDecimationCondition decimationCondition;
        public string variantKey;
        public string sceneName;
        public string participantId;
        public string sessionId;
        public int globalSessionIndex = -1;

        [Header("Pairwise / comparison metadata")]
        public CrowdingExperimentParadigm experimentParadigm = CrowdingExperimentParadigm.DirectRating;
        public int comparisonPairIndex = -1;
        public int comparisonPresentationIndex = -1; // 0 = first presentation, 1 = second presentation
        public string comparisonType = "";
        public bool comparisonFirstIsReference = false;
        public bool comparisonFirstIsUniform = false;
        public bool comparisonFirstIsWeighted = false;
        public string comparisonFirstLabel = "";
        public string comparisonSecondLabel = "";
        public string comparisonFirstVariantKey = "";
        public string comparisonSecondVariantKey = "";
        public CrowdingDisplayConditionType comparisonDistortedConditionType = CrowdingDisplayConditionType.Reference;
        public float comparisonDistortedKeepRatioK = 1f;
        public CrowdingDecimationCondition comparisonDistortedDecimationCondition = CrowdingDecimationCondition.Uniform;
        public string comparisonDistortedVariantKey = "";
        public double startTimeRealtime;
        public double endTimeRealtime;

        public override string ToString()
        {
            if (isReference)
                return $"Step {globalStepIndex}: REF | F{fixationIndex:00} ({fixationName})";
            return $"Step {globalStepIndex}: {variantKey} | F{fixationIndex:00} ({fixationName})";
        }
    }

    [Serializable]
    public class CrowdingDisplayCombination
    {
        public CrowdingDisplayConditionType conditionType = CrowdingDisplayConditionType.Reference;
        public float keepRatioK = 1.0f;
        public CrowdingDecimationCondition decimationCondition = CrowdingDecimationCondition.Uniform;
        public bool IsReference => conditionType == CrowdingDisplayConditionType.Reference;

        public string BuildVariantKey(int fixationIndex, bool useGlobalSceneVariants)
        {
            if (IsReference)
                return "reference";

            // IMPORTANT:
            // Uniform variants must be invariant to the active fixation point.
            // Even when useGlobalSceneVariants is false, a Uniform condition should
            // always resolve to the global key, e.g. K075_uniform_global.
            // Otherwise the DisplayHandler would request F00_K075_uniform,
            // F01_K075_uniform, etc., and recent fixation-dependent grouping /
            // budget computations could make the supposedly uniform baseline
            // change when only the fixation marker changes.
            if (conditionType == CrowdingDisplayConditionType.Uniform ||
                decimationCondition == CrowdingDecimationCondition.Uniform)
            {
                return CrowdingVariantKey.BuildGlobal(keepRatioK, CrowdingDecimationCondition.Uniform);
            }

            if (useGlobalSceneVariants)
                return CrowdingVariantKey.BuildGlobal(keepRatioK, decimationCondition);

            return CrowdingVariantKey.Build(fixationIndex, keepRatioK, decimationCondition);
        }
    }

    [DisallowMultipleComponent]
    public class CrowdingDisplayHandler : MonoBehaviour
    {
        [Header("References")]
        public CrowdingAwarePipelineController pipelineController;
        public CrowdingSharedMeshLODSelector lodSelector;

        [Tooltip("Recommended: use the fixationPoints list already defined in CrowdingAwarePipelineController.")]
        public bool useControllerFixationPoints = true;

        [Tooltip("Used only if useControllerFixationPoints is false, or as fallback when no controller is assigned.")]
        public List<Transform> fixationPoints = new List<Transform>();

        [Header("Participant / log folder")]
        public string participantId = "P001";
        public bool useParticipantFolderForLogs = true;
        public string participantLogsRootRelativePath = "CrowdingExports";
        public string participantFolderPrefix = "Participant_";
        public string displaySequenceLogFileName = "display_sequence_log.csv";

        [Header("External multi-scene session")]
        [Tooltip("Used by CrowdingMultiSceneSessionManager. When enabled, this handler plays only one condition instead of its full local condition list.")]
        [SerializeField] private bool externalSingleConditionMode = false;

        [Tooltip("Session identifier propagated by CrowdingMultiSceneSessionManager and written into the logs.")]
        [SerializeField] private string externalSessionId = "";

        [Tooltip("Global randomized block index propagated by CrowdingMultiSceneSessionManager and written into the logs.")]
        [SerializeField] private int externalGlobalSessionIndex = -1;

        private CrowdingDisplayCombination externalSingleCondition;

        [Header("Fixation cue display")]
        public GameObject fixationCue;
        public bool parentCueToFixationPoint = false;
        public Vector3 fixationCueWorldOffset = Vector3.zero;

        [Header("Fixation point marker visibility")]
        [Tooltip("If true, the current fixation point object is shown and all other fixation point objects are hidden during the sequence.")]
        public bool showOnlyCurrentFixationPointObject = true;

        [Tooltip("How the DisplayHandler controls the visibility of the fixation point objects referenced in CrowdingAwarePipelineController.fixationPoints.")]
        public CrowdingFixationMarkerVisibilityMode fixationMarkerVisibilityMode = CrowdingFixationMarkerVisibilityMode.SetGameObjectActive;

        [Tooltip("What to do with fixation point objects when the sequence is stopped or finished.")]
        public CrowdingFixationMarkerEndBehavior fixationMarkerEndBehavior = CrowdingFixationMarkerEndBehavior.HideAll;

        [Tooltip("Legacy alias kept for older scenes. If enabled, the fixation point GameObjects are treated as marker objects. New projects should use Show Only Current Fixation Point Object instead.")]
        public bool useFixationPointGameObjectsAsMarkers = true;

        [Header("LOD combinations")]
        public List<float> keepRatiosK = new List<float> { 0.75f, 0.5f, 0.25f };

        public bool includeReferenceCondition = true;

        [Tooltip("If true, uniform LOD conditions are included.")]
        public bool includeUniformCondition = true;

        [Tooltip("If true, weighted crowding-aware LOD conditions are included.")]
        public bool includeWeightedCondition = true;


        [Tooltip("If false, use the existing per-fixation variants such as F00_K075_weighted. If true, use global scene-state variants such as K075_weighted_global; meshes are applied once per condition and do not change when fixation points are sequenced.")]
        public bool useGlobalSceneVariants = false;

        [Tooltip("When true, the selector is called only when the variant key changes. With global variants, this means meshes are swapped once at the beginning of a condition, not between fixation points.")]
        public bool applyLodOnlyWhenVariantKeyChanges = true;

        [Header("LOD application timing")]
        [Tooltip("If true, the fixation marker is shown first, and the LOD change is applied only when the observer clicks the current fixation marker for the first time.")]
        public bool applyLodOnFirstFixationClick = true;

        [Tooltip("If true, debug messages are printed when the LOD is armed and applied after the first fixation click.")]
        public bool debugLodApplicationOnFirstClick = false;

        [Header("Explosion masked LOD transition")]
        [Tooltip("If true, when the observer validates a fixation point, an explosion is spawned at the current fixation point. The LOD for the next step is applied during the explosion, before the next fixation marker is shown.")]
        public bool useExplosionMaskedLodTransition = false;

        [Tooltip("Optional explosion prefab. If empty, a procedural fallback explosion is created automatically.")]
        public GameObject fixationExplosionPrefab;

        [Tooltip("Small world-space offset applied to the explosion spawn position. Useful if the effect is hidden inside the fixation object.")]
        public Vector3 explosionWorldOffset = Vector3.zero;

        [Tooltip("Scale multiplier applied to the spawned explosion prefab.")]
        public float explosionScaleMultiplier = 1.0f;

        [Tooltip("If true, the spawned explosion and all its children are moved to the configured Unity layer. This fixes effects visible in Scene view but invisible to the VR/Game camera because of camera culling masks.")]
        public bool forceExplosionLayerOnSpawn = true;

        [Tooltip("Layer used when Force Explosion Layer On Spawn is enabled. Default is usually visible to the Main Camera and XR cameras.")]
        public string explosionLayerName = "Default";

        [Tooltip("If true, all Renderer components inside the spawned explosion are enabled at runtime.")]
        public bool forceExplosionRenderersEnabled = true;

        [Tooltip("If true, all ParticleSystem components inside the spawned prefab are restarted manually at runtime.")]
        public bool playExplosionParticleSystemsOnSpawn = true;

        [Tooltip("If true, Visual Effect Graph components are also restarted when the VFX Graph package is present. This is done through reflection, so the script still compiles without VFX Graph.")]
        public bool playExplosionVisualEffectsOnSpawn = true;

        [Tooltip("Automatically destroy the spawned explosion after this many seconds. Set to 0 or below to keep it.")]
        public float explosionAutoDestroySeconds = 3.0f;

        [Tooltip("If no prefab is assigned, create a simple procedural explosion made of small fire particles.")]
        public bool useProceduralExplosionIfNoPrefab = true;

        [Tooltip("Delay after the explosion starts before applying the LOD swap. Use a short value so the swap occurs during the strongest visual transient.")]
        public float explosionLodSwapDelaySeconds = 0.10f;

        [Tooltip("Delay after the explosion starts before showing the next fixation marker. This should be slightly longer than the LOD swap delay.")]
        public float explosionNextFixationDelaySeconds = 0.30f;

        [Tooltip("If true, the current fixation marker is hidden immediately when the explosion starts.")]
        public bool hideCurrentFixationDuringExplosion = true;

        [Tooltip("If true, transition delays use unscaled time.")]
        public bool useUnscaledTimeForExplosionTransition = true;

        [Tooltip("If true, debug messages are printed during the explosion-masked transition.")]
        public bool debugExplosionMaskedTransition = false;

        [Tooltip("Number of small particles used by the built-in fallback explosion.")]
        public int proceduralExplosionParticleCount = 32;

        [Tooltip("Approximate radius/speed of the built-in fallback explosion.")]
        public float proceduralExplosionRadius = 0.55f;

        [Tooltip("Duration of the built-in fallback explosion.")]
        public float proceduralExplosionDuration = 0.45f;

        [Tooltip("Size of the particles used by the built-in fallback explosion.")]
        public float proceduralExplosionParticleSize = 0.07f;

        [Header("Additional field explosions")]
        [Tooltip("If true, additional distractor explosions are spawned in the viewer's field of view when the fixation/target explosion occurs.")]
        public bool spawnAdditionalFieldExplosions = false;

        [Tooltip("Number of additional distractor explosions to spawn. Default = 3.")]
        [Min(0)] public int additionalFieldExplosionCount = 3;

        [Tooltip("If true, additional explosions are placed at the same camera distance as the main fixation explosion. If false, Additional Field Explosion Distance From Camera is used.")]
        public bool additionalFieldExplosionsUseMainDistance = true;

        [Tooltip("Distance from the camera used for additional explosions when Additional Field Explosions Use Main Distance is false.")]
        public float additionalFieldExplosionDistanceFromCamera = 10.0f;

        [Tooltip("Viewport margin used to keep additional explosions away from the very edges of the field of view. 0 = full viewport, 0.15 = central 70% area.")]
        [Range(0f, 0.45f)] public float additionalFieldExplosionViewportMargin = 0.15f;

        [Tooltip("Minimum angular separation, in degrees, between the main explosion and distractor explosions, and between distractor explosions themselves.")]
        public float additionalFieldExplosionMinAngularSeparationDeg = 8.0f;

        [Tooltip("Optional world-space offset applied only to the additional distractor explosions.")]
        public Vector3 additionalFieldExplosionWorldOffset = Vector3.zero;

        [Header("Pre-step masked appearance")]
        [Tooltip("If true, the first presentation of a scene is preceded by a short masked interval before the fixation point appears. This avoids a visible quality pop when the randomized first presentation is distorted.")]
        public bool maskFirstStepAppearance = true;

        [Tooltip("If true, the first presentation after a rating canvas is preceded by a short masked interval before the fixation point appears.")]
        public bool maskAppearanceAfterRating = true;

        [Tooltip("If true, the pre-step mask is used only for pairwise paradigms such as Reference vs Distortion and Uniform vs Weighted.")]
        public bool preStepMaskOnlyForPairwiseParadigms = true;

        [Tooltip("If true, the pre-step mask is used only when the upcoming presentation is not the reference. Recommended for Reference vs Distortion so Reference-first trials remain clean while Distortion-first trials are masked.")]
        public bool preStepMaskOnlyWhenUpcomingStepIsDistorted = true;

        [Tooltip("If true, additional field explosions are spawned during the pre-step mask. No main fixation explosion is spawned because no target has just been destroyed.")]
        public bool spawnFieldExplosionsDuringPreStepMask = true;

        [Tooltip("Delay after the pre-step field explosions start before applying the LOD swap.")]
        public float preStepMaskLodSwapDelaySeconds = 0.10f;

        [Tooltip("Delay after the pre-step field explosions start before showing the fixation point and starting the logged presentation.")]
        public float preStepMaskShowFixationDelaySeconds = 0.30f;

        [Header("Randomization")]
        public bool randomizeCombinationOrder = false;
        public bool keepReferenceFirstWhenRandomized = true;
        public bool randomizeFixationOrder = false;
        public bool randomizeFixationOrderPerCombination = true;
        public bool deterministicRandom = true;
        public int randomSeed = 12345;

        [Header("Fixation subset")]
        [Tooltip("If true, the experiment uses only a subset of the available fixation points instead of all fixation points.")]
        public bool useFixationSubset = true;

        [Tooltip("Number of fixation points to keep. Default = 3: first, last, and median-lower fixation point.")]
        [Min(1)] public int fixationSubsetCount = 3;

        [Tooltip("If true, prints the selected fixation indices when the sequence is built.")]
        public bool debugFixationSubset = false;

        [Header("Advance")]
        public CrowdingDisplayAdvanceMode advanceMode = CrowdingDisplayAdvanceMode.KeyPress;
        public KeyCode advanceKey = KeyCode.Space;
        public float stepDurationSeconds = 2.0f;
        public bool startOnPlay = false;

        [Header("Experimental paradigm")]
        [Tooltip("DirectRating = previous behavior. ReferenceVsDistortionRating = show Reference and one distorted version in random order, then ask how disturbing the distortion is. UniformVsWeightedPreference = show Uniform and Weighted/Redistributed in random order, then ask which one is preferred.")]
        public CrowdingExperimentParadigm experimentalParadigm = CrowdingExperimentParadigm.DirectRating;

        [Tooltip("For pairwise paradigms, randomizes whether Reference/Uniform or Distorted/Weighted appears first.")]
        public bool randomizePairPresentationOrder = true;

        [Tooltip("Blank interval inserted only between the first and second presentation of the same pairwise comparison.")]
        public float pairwiseInterPresentationDelaySeconds = 0.5f;

        [Tooltip("If true, debug messages are printed for pairwise comparison sequence generation and responses.")]
        public bool debugPairwiseParadigm = false;

        [Tooltip("Label used in logs for the first pairwise presentation.")]
        public string firstPresentationLabel = "A";

        [Tooltip("Label used in logs for the second pairwise presentation.")]
        public string secondPresentationLabel = "B";

        [Header("Reference vs distortion rating labels")]
        public string disturbanceRating1Label = "Distortion invisible";
        public string disturbanceRating2Label = "Distortion barely perceptible";
        public string disturbanceRating3Label = "Distortion visible but not disturbing";
        public string disturbanceRating4Label = "Distortion visible and disturbing";
        public string disturbanceRating5Label = "Distortion visible and ugly";

        [Header("Uniform vs redistributed preference")]
        [Tooltip("Optional canvas for the Uniform vs Weighted/Redistributed preference question. If empty, Condition Rating Canvas is used as fallback.")]
        public GameObject pairwisePreferenceCanvas;

        [Tooltip("CSV file name for Uniform vs Weighted/Redistributed preference responses.")]
        public string pairwisePreferenceLogFileName = "uniform_vs_weighted_preference_log.csv";

        [Header("Condition rating canvas")]
        [Tooltip("If true, a rating canvas is shown either after each condition or after each fixation point, depending on Rating Granularity.")]
        public bool enableConditionRatingCanvas = true;

        [Tooltip("ConditionEnd = one score after all fixation points of a condition. EachFixationPoint = one score after every fixation point.")]
        public CrowdingRatingGranularity ratingGranularity = CrowdingRatingGranularity.EachFixationPoint;

        [Tooltip("Canvas containing the question and the 5 response buttons. It is hidden during fixation steps and shown only when a rating is expected.")]
        public GameObject conditionRatingCanvas;

        [Tooltip("If true, all fixation marker objects are hidden while the rating canvas is visible.")]
        public bool hideFixationMarkersDuringRating = true;

        [Tooltip("If true, the fixation cue is hidden while the rating canvas is visible.")]
        public bool hideFixationCueDuringRating = true;

        [Tooltip("If true, each rating is written into a participant-specific CSV file. With EachFixationPoint, one row is written per fixation point.")]
        public bool logConditionRatingsCsv = true;

        [Tooltip("CSV file name for ratings. The file is created in the participant log folder. You can keep condition_rating_log.csv or use fixation_rating_log.csv.")]
        public string conditionRatingLogFileName = "condition_rating_log.csv";

        [Header("Rating visibility / scene objects")]
        [Tooltip("If true, objects with the configured tag or layer are hidden while the rating canvas is visible.")]
        public bool hideTaggedOrLayeredObjectsDuringRating = true;

        [Tooltip("Objects with this tag are hidden while the rating canvas is visible. Leave empty to ignore tag filtering. Default is 'objet'.")]
        public string objectsTagToHideDuringRating = "objet";

        [Tooltip("Objects on this Unity layer are hidden while the rating canvas is visible. Leave empty to ignore layer filtering. Default is 'Objet'.")]
        public string objectsLayerToHideDuringRating = "Objet";

        [Tooltip("If true, hidden objects are restored to their previous activeSelf state after the observer submits a rating.")]
        public bool restoreTaggedOrLayeredObjectsAfterRating = true;

        [Header("Logging")]
        public bool logDisplaySequenceCsv = true;
        public string logRelativePath = "CrowdingExports/display_sequence_log.csv";
        public bool appendToExistingLog = true;

        [Header("Runtime state")]
        [SerializeField] private bool sequenceRunning = false;
        [SerializeField] private int currentStepListIndex = -1;
        [SerializeField] private float currentStepElapsed = 0f;
        [SerializeField] private CrowdingDisplayStepInfo currentStep;
        [SerializeField] private bool waitingForConditionRating = false;
        [SerializeField] private CrowdingDisplayStepInfo pendingRatingStep;

        private List<CrowdingDisplayCombination> combinations = new List<CrowdingDisplayCombination>();
        private List<CrowdingDisplayStepInfo> steps = new List<CrowdingDisplayStepInfo>();
        private string lastAppliedVariantKey = string.Empty;
        private System.Random rng;
        private double conditionRatingStartTimeRealtime = 0.0;
        [SerializeField] private bool waitingForFirstFixationClickBeforeLod = false;
        private CrowdingDisplayStepInfo pendingFirstClickLodStep;
        [SerializeField] private bool explosionMaskedTransitionRunning = false;
        private Coroutine explosionMaskedTransitionCoroutine;
        [SerializeField] private bool pairwiseInterPresentationDelayRunning = false;
        private Coroutine pairwiseInterPresentationCoroutine;
        [SerializeField] private bool preStepMaskedTransitionRunning = false;
        private Coroutine preStepMaskedTransitionCoroutine;
        private readonly Dictionary<GameObject, bool> initialFixationGameObjectActiveStates = new Dictionary<GameObject, bool>();
        private readonly Dictionary<Renderer, bool> initialFixationRendererStates = new Dictionary<Renderer, bool>();
        private readonly Dictionary<GameObject, bool> ratingHiddenObjectActiveStates = new Dictionary<GameObject, bool>();

        public bool IsRunning => sequenceRunning;
        public bool IsWaitingForConditionRating => waitingForConditionRating;
        public CrowdingDisplayStepInfo CurrentStep => currentStep;
        public IReadOnlyList<CrowdingDisplayStepInfo> Steps => steps;
        public string ParticipantId => string.IsNullOrWhiteSpace(participantId) ? "UnknownParticipant" : participantId.Trim();
        public string SessionId => string.IsNullOrWhiteSpace(externalSessionId) ? "" : externalSessionId.Trim();
        public int GlobalSessionIndex => externalGlobalSessionIndex;
        public bool ExternalSingleConditionMode => externalSingleConditionMode;

        /// <summary>
        /// Called by CrowdingMultiSceneSessionManager after a scene has been loaded.
        /// The handler will then build a sequence containing only this condition,
        /// while keeping its local fixation order, rating canvas, logging, explosion, etc.
        /// </summary>
        public void ConfigureExternalSingleCondition(
            CrowdingDisplayConditionType conditionType,
            float keepRatioK,
            CrowdingDecimationCondition decimationCondition,
            int globalSessionIndex,
            string sessionId)
        {
            externalSingleConditionMode = true;
            externalGlobalSessionIndex = globalSessionIndex;
            externalSessionId = string.IsNullOrWhiteSpace(sessionId) ? "" : sessionId.Trim();

            float kk = Mathf.Clamp01(keepRatioK);
            externalSingleCondition = new CrowdingDisplayCombination
            {
                conditionType = conditionType,
                keepRatioK = conditionType == CrowdingDisplayConditionType.Reference ? 1f : kk,
                decimationCondition = conditionType == CrowdingDisplayConditionType.Reference
                    ? CrowdingDecimationCondition.Uniform
                    : decimationCondition
            };
        }

        public void ClearExternalSingleCondition()
        {
            externalSingleConditionMode = false;
            externalSingleCondition = null;
            externalSessionId = "";
            externalGlobalSessionIndex = -1;
        }

        public event Action<CrowdingDisplayStepInfo> StepStarted;
        public event Action<CrowdingDisplayStepInfo> StepEnded;
        public event Action SequenceStarted;
        public event Action SequenceFinished;

        private void Awake()
        {
            AutoAssignReferences();
        }

        private void OnValidate()
        {
            explosionLodSwapDelaySeconds = Mathf.Max(0f, explosionLodSwapDelaySeconds);
            explosionNextFixationDelaySeconds = Mathf.Max(explosionLodSwapDelaySeconds, explosionNextFixationDelaySeconds);
            explosionScaleMultiplier = Mathf.Max(0.01f, explosionScaleMultiplier);
            explosionAutoDestroySeconds = Mathf.Max(0f, explosionAutoDestroySeconds);
            proceduralExplosionParticleCount = Mathf.Max(1, proceduralExplosionParticleCount);
            proceduralExplosionRadius = Mathf.Max(0.01f, proceduralExplosionRadius);
            proceduralExplosionDuration = Mathf.Max(0.05f, proceduralExplosionDuration);
            proceduralExplosionParticleSize = Mathf.Max(0.005f, proceduralExplosionParticleSize);
            additionalFieldExplosionCount = Mathf.Max(0, additionalFieldExplosionCount);
            additionalFieldExplosionDistanceFromCamera = Mathf.Max(0.1f, additionalFieldExplosionDistanceFromCamera);
            additionalFieldExplosionViewportMargin = Mathf.Clamp(additionalFieldExplosionViewportMargin, 0f, 0.45f);
            additionalFieldExplosionMinAngularSeparationDeg = Mathf.Max(0f, additionalFieldExplosionMinAngularSeparationDeg);
            preStepMaskLodSwapDelaySeconds = Mathf.Max(0f, preStepMaskLodSwapDelaySeconds);
            preStepMaskShowFixationDelaySeconds = Mathf.Max(preStepMaskLodSwapDelaySeconds, preStepMaskShowFixationDelaySeconds);
            AutoAssignReferences();
        }

        private void AutoAssignReferences()
        {
            if (pipelineController == null)
                pipelineController = FindFirstObjectByType<CrowdingAwarePipelineController>();

            if (lodSelector == null)
                lodSelector = FindFirstObjectByType<CrowdingSharedMeshLODSelector>();
        }

        private void Start()
        {
            if (startOnPlay)
                StartSequence();
        }

        private void OnDisable()
        {
            ClearPendingFirstClickLod();
            ClearPairwiseInterPresentationDelay(true);
            ClearPreStepMaskedTransition(true);
        }

        private void Update()
        {
            if (!sequenceRunning) return;

            // While the rating canvas is visible, the sequence is paused.
            // Only SubmitConditionRating(...) can resume it.
            if (waitingForConditionRating) return;
            if (explosionMaskedTransitionRunning) return;
            if (pairwiseInterPresentationDelayRunning) return;
            if (preStepMaskedTransitionRunning) return;

            currentStepElapsed += Time.deltaTime;

            bool timeout = advanceMode == CrowdingDisplayAdvanceMode.Timeout ||
                           advanceMode == CrowdingDisplayAdvanceMode.TimeoutOrKeyPress;

            bool key = advanceMode == CrowdingDisplayAdvanceMode.KeyPress ||
                       advanceMode == CrowdingDisplayAdvanceMode.TimeoutOrKeyPress;

            if (key && IsAdvanceKeyPressed())
                AdvanceToNextStep();
            else if (timeout && currentStepElapsed >= stepDurationSeconds)
                AdvanceToNextStep();
        }

        private bool IsAdvanceKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM
            // New Input System path. This avoids UnityEngine.Input.GetKeyDown,
            // which throws when Active Input Handling is set to "Input System Package" only.
            if (Keyboard.current == null)
                return false;

            Key mappedKey = MapKeyCodeToInputSystemKey(advanceKey);
            return Keyboard.current[mappedKey].wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(advanceKey);
#else
            return false;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private static Key MapKeyCodeToInputSystemKey(KeyCode keyCode)
        {
            switch (keyCode)
            {
                case KeyCode.Space: return Key.Space;
                case KeyCode.Return: return Key.Enter;
                case KeyCode.KeypadEnter: return Key.NumpadEnter;
                case KeyCode.Escape: return Key.Escape;
                case KeyCode.Tab: return Key.Tab;
                case KeyCode.Backspace: return Key.Backspace;
                case KeyCode.Delete: return Key.Delete;
                case KeyCode.UpArrow: return Key.UpArrow;
                case KeyCode.DownArrow: return Key.DownArrow;
                case KeyCode.LeftArrow: return Key.LeftArrow;
                case KeyCode.RightArrow: return Key.RightArrow;

                case KeyCode.Alpha0: return Key.Digit0;
                case KeyCode.Alpha1: return Key.Digit1;
                case KeyCode.Alpha2: return Key.Digit2;
                case KeyCode.Alpha3: return Key.Digit3;
                case KeyCode.Alpha4: return Key.Digit4;
                case KeyCode.Alpha5: return Key.Digit5;
                case KeyCode.Alpha6: return Key.Digit6;
                case KeyCode.Alpha7: return Key.Digit7;
                case KeyCode.Alpha8: return Key.Digit8;
                case KeyCode.Alpha9: return Key.Digit9;

                case KeyCode.A: return Key.A;
                case KeyCode.B: return Key.B;
                case KeyCode.C: return Key.C;
                case KeyCode.D: return Key.D;
                case KeyCode.E: return Key.E;
                case KeyCode.F: return Key.F;
                case KeyCode.G: return Key.G;
                case KeyCode.H: return Key.H;
                case KeyCode.I: return Key.I;
                case KeyCode.J: return Key.J;
                case KeyCode.K: return Key.K;
                case KeyCode.L: return Key.L;
                case KeyCode.M: return Key.M;
                case KeyCode.N: return Key.N;
                case KeyCode.O: return Key.O;
                case KeyCode.P: return Key.P;
                case KeyCode.Q: return Key.Q;
                case KeyCode.R: return Key.R;
                case KeyCode.S: return Key.S;
                case KeyCode.T: return Key.T;
                case KeyCode.U: return Key.U;
                case KeyCode.V: return Key.V;
                case KeyCode.W: return Key.W;
                case KeyCode.X: return Key.X;
                case KeyCode.Y: return Key.Y;
                case KeyCode.Z: return Key.Z;

                default:
                    Debug.LogWarning($"CrowdingDisplayHandler: KeyCode {keyCode} is not mapped for the new Input System. Falling back to Space.");
                    return Key.Space;
            }
        }
#endif

        [ContextMenu("Display Handler/Start Sequence")]
        public void StartSequence()
        {
            BuildSequence();

            if (steps.Count == 0)
            {
                Debug.LogWarning("CrowdingDisplayHandler: sequence is empty.", this);
                return;
            }

            sequenceRunning = true;
            currentStepListIndex = -1;
            currentStepElapsed = 0f;
            lastAppliedVariantKey = string.Empty;
            ClearPendingFirstClickLod();
            waitingForConditionRating = false;
            pendingRatingStep = null;
            conditionRatingStartTimeRealtime = 0.0;
            ClearPendingFirstClickLod();
            ClearExplosionMaskedTransition(false);
            ClearPairwiseInterPresentationDelay(true);
            ClearPreStepMaskedTransition(true);
            HideConditionRatingCanvas();

            CacheInitialFixationMarkerVisibility();

            if (logDisplaySequenceCsv)
                PrepareLogFile();

            if (enableConditionRatingCanvas && logConditionRatingsCsv)
                PrepareConditionRatingLogFile();

            if (enableConditionRatingCanvas && experimentalParadigm == CrowdingExperimentParadigm.UniformVsWeightedPreference)
                PreparePairwisePreferenceLogFile();

            SequenceStarted?.Invoke();
            AdvanceToNextStep();
        }

        [ContextMenu("Display Handler/Stop Sequence")]
        public void StopSequence()
        {
            if (!sequenceRunning) return;

            EndCurrentStep();
            sequenceRunning = false;
            currentStepListIndex = -1;
            currentStepElapsed = 0f;
            lastAppliedVariantKey = string.Empty;
            waitingForConditionRating = false;
            pendingRatingStep = null;
            conditionRatingStartTimeRealtime = 0.0;
            ClearPendingFirstClickLod();
            ClearExplosionMaskedTransition(true);
            ClearPairwiseInterPresentationDelay(true);
            ClearPreStepMaskedTransition(true);
            HideConditionRatingCanvas();
            HideFixationCue();
            SequenceFinished?.Invoke();
            Debug.Log("CrowdingDisplayHandler: sequence stopped.", this);
        }

        [ContextMenu("Display Handler/Advance One Step")]
        public void AdvanceToNextStep()
        {
            if (!sequenceRunning)
            {
                StartSequence();
                return;
            }

            if (waitingForConditionRating)
            {
                Debug.Log("CrowdingDisplayHandler: advance ignored because the condition rating canvas is waiting for a response.", this);
                return;
            }

            if (explosionMaskedTransitionRunning)
            {
                if (debugExplosionMaskedTransition)
                    Debug.Log("CrowdingDisplayHandler: advance ignored because an explosion-masked transition is already running.", this);
                return;
            }

            if (pairwiseInterPresentationDelayRunning)
            {
                if (debugPairwiseParadigm)
                    Debug.Log("CrowdingDisplayHandler: advance ignored because the pairwise inter-presentation delay is still running.", this);
                return;
            }

            if (preStepMaskedTransitionRunning)
            {
                if (debugExplosionMaskedTransition)
                    Debug.Log("CrowdingDisplayHandler: advance ignored because a pre-step masked transition is already running.", this);
                return;
            }

            // The very first call after StartSequence has no current step yet.
            // It must start normally, without an explosion.
            if (useExplosionMaskedLodTransition && currentStep != null && currentStep.startTimeRealtime > 0.0)
            {
                explosionMaskedTransitionCoroutine = StartCoroutine(ExplosionMaskedAdvanceRoutine());
                return;
            }

            AdvanceToNextStepImmediate();
        }

        private void AdvanceToNextStepImmediate()
        {
            CrowdingDisplayStepInfo endedStep = currentStep;
            EndCurrentStep();

            if (ShouldShowConditionRatingAfterStep(endedStep))
            {
                ClearPendingFirstClickLod();
                ShowConditionRatingCanvas(endedStep);
                return;
            }

            // In pairwise paradigms, keep a short blank interval only between
            // presentation 1 and presentation 2 of the same comparison pair.
            if (ShouldInsertPairwiseInterPresentationDelay(endedStep))
            {
                pairwiseInterPresentationCoroutine = StartCoroutine(PairwiseInterPresentationDelayRoutine());
                return;
            }

            StartNextStepOrFinish();
        }

        private bool ShouldInsertPairwiseInterPresentationDelay(CrowdingDisplayStepInfo endedStep)
        {
            if (!IsPairwiseParadigm())
                return false;

            if (endedStep == null || endedStep.comparisonPresentationIndex != 0)
                return false;

            int nextStepIndex = currentStepListIndex + 1;

            if (nextStepIndex < 0 || nextStepIndex >= steps.Count)
                return false;

            CrowdingDisplayStepInfo nextStep = steps[nextStepIndex];

            if (nextStep == null)
                return false;

            return nextStep.comparisonPairIndex == endedStep.comparisonPairIndex &&
                   nextStep.comparisonPresentationIndex == 1;
        }

        private IEnumerator PairwiseInterPresentationDelayRoutine()
        {
            pairwiseInterPresentationDelayRunning = true;
            ClearPendingFirstClickLod();

            // Force a real blank interval: hide both the fixation-point marker
            // and the optional cue before the second presentation appears.
            if (ShouldManageFixationPointMarkers())
                SetAllFixationPointsVisible(false);

            if (fixationCue != null)
                fixationCue.SetActive(false);

            float delay = Mathf.Max(0f, pairwiseInterPresentationDelaySeconds);

            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            pairwiseInterPresentationDelayRunning = false;
            pairwiseInterPresentationCoroutine = null;

            if (!sequenceRunning)
                yield break;

            StartNextStepOrFinish();
        }

        private void ClearPairwiseInterPresentationDelay(bool stopCoroutine)
        {
            if (stopCoroutine && pairwiseInterPresentationCoroutine != null)
                StopCoroutine(pairwiseInterPresentationCoroutine);

            pairwiseInterPresentationCoroutine = null;
            pairwiseInterPresentationDelayRunning = false;
        }

        private void ClearPreStepMaskedTransition(bool stopCoroutine)
        {
            if (stopCoroutine && preStepMaskedTransitionCoroutine != null)
                StopCoroutine(preStepMaskedTransitionCoroutine);

            preStepMaskedTransitionCoroutine = null;
            preStepMaskedTransitionRunning = false;
        }

        private void StartNextStepOrFinish(bool restoreRatingHiddenObjectsBeforeStep = true)
        {
            ClearPendingFirstClickLod();
            currentStepListIndex++;

            if (currentStepListIndex >= steps.Count)
            {
                FinishSequence();
                return;
            }

            currentStep = steps[currentStepListIndex];
            currentStepElapsed = 0f;

            bool comingFromRating = !restoreRatingHiddenObjectsBeforeStep;

            HideConditionRatingCanvas(restoreRatingHiddenObjectsBeforeStep);

            if (ShouldUsePreStepMaskedTransition(currentStep, comingFromRating))
            {
                preStepMaskedTransitionCoroutine = StartCoroutine(
                    PreStepMaskedTransitionRoutine(currentStep, restoreRatingHiddenObjectsBeforeStep)
                );
                return;
            }

            StartStepNow(currentStep, restoreRatingHiddenObjectsBeforeStep);
        }

        private void StartStepNow(CrowdingDisplayStepInfo step, bool restoreRatingHiddenObjectsBeforeStep)
        {
            if (step == null)
                return;

            step.startTimeRealtime = Time.realtimeSinceStartupAsDouble;
            currentStepElapsed = 0f;

            ApplyStep(step);

            if (!restoreRatingHiddenObjectsBeforeStep && restoreTaggedOrLayeredObjectsAfterRating)
                RestoreTaggedOrLayeredObjectsAfterRating();

            StepStarted?.Invoke(step);

            Debug.Log($"CrowdingDisplayHandler: started {step}", this);
        }

        private bool ShouldUsePreStepMaskedTransition(CrowdingDisplayStepInfo step, bool comingFromRating)
        {
            if (step == null)
                return false;

            if (preStepMaskOnlyForPairwiseParadigms && !IsPairwiseParadigm())
                return false;

            if (preStepMaskOnlyWhenUpcomingStepIsDistorted && step.isReference)
                return false;

            if (!StepNeedsLodApplication(step))
                return false;

            bool isFirstStepOfSequence = currentStepListIndex == 0;

            if (isFirstStepOfSequence && maskFirstStepAppearance)
                return true;

            if (comingFromRating && maskAppearanceAfterRating)
                return true;

            return false;
        }

        private IEnumerator PreStepMaskedTransitionRoutine(CrowdingDisplayStepInfo step, bool restoreRatingHiddenObjectsBeforeStep)
        {
            preStepMaskedTransitionRunning = true;
            ClearPendingFirstClickLod();

            if (ShouldManageFixationPointMarkers())
                SetAllFixationPointsVisible(false);

            if (fixationCue != null)
                fixationCue.SetActive(false);

            if (spawnFieldExplosionsDuringPreStepMask)
                SpawnPreStepFieldExplosions();

            float lodSwapDelay = Mathf.Max(0f, preStepMaskLodSwapDelaySeconds);
            float showFixationDelay = Mathf.Max(lodSwapDelay, preStepMaskShowFixationDelaySeconds);

            yield return WaitForExplosionSeconds(lodSwapDelay);

            ApplyLodForStepIfNeeded(step);

            if (!restoreRatingHiddenObjectsBeforeStep && restoreTaggedOrLayeredObjectsAfterRating)
                RestoreTaggedOrLayeredObjectsAfterRating();

            yield return WaitForExplosionSeconds(showFixationDelay - lodSwapDelay);

            if (!sequenceRunning || currentStep != step)
            {
                preStepMaskedTransitionRunning = false;
                preStepMaskedTransitionCoroutine = null;
                yield break;
            }

            step.startTimeRealtime = Time.realtimeSinceStartupAsDouble;
            currentStepElapsed = 0f;

            DisplayFixation(step.fixationIndex);
            StepStarted?.Invoke(step);

            if (debugExplosionMaskedTransition)
            {
                Debug.Log(
                    $"CrowdingDisplayHandler: pre-step masked transition completed. Started {step}.",
                    this
                );
            }

            preStepMaskedTransitionRunning = false;
            preStepMaskedTransitionCoroutine = null;
        }

        private void SpawnPreStepFieldExplosions()
        {
            if (additionalFieldExplosionCount <= 0)
                return;

            Camera cam = ResolveExplosionFieldCamera();
            if (cam == null)
                return;

            float distance = Mathf.Max(0.1f, additionalFieldExplosionDistanceFromCamera);
            Vector3 pseudoMainPosition = cam.transform.position + cam.transform.forward * distance;
            SpawnAdditionalExplosionsInFieldOfView(pseudoMainPosition);
        }

        private IEnumerator ExplosionMaskedAdvanceRoutine()
        {
            explosionMaskedTransitionRunning = true;

            CrowdingDisplayStepInfo endedStep = currentStep;
            Transform endedFixation = endedStep != null ? GetFixationPoint(endedStep.fixationIndex) : null;
            Vector3 explosionPosition = endedFixation != null
                ? endedFixation.position
                : (fixationCue != null ? fixationCue.transform.position : transform.position);
            Quaternion explosionRotation = endedFixation != null ? endedFixation.rotation : Quaternion.identity;

            if (debugExplosionMaskedTransition)
            {
                Debug.Log(
                    $"CrowdingDisplayHandler: explosion-masked transition started from {BuildConditionLabel(endedStep)} / fixation '{(endedStep != null ? endedStep.fixationName : "unknown")}'.",
                    this
                );
            }

            SpawnFixationExplosion(explosionPosition, explosionRotation);

            if (hideCurrentFixationDuringExplosion)
            {
                if (endedFixation != null)
                    SetFixationPointVisible(endedFixation, false);

                if (fixationCue != null)
                    fixationCue.SetActive(false);
            }

            ClearPendingFirstClickLod();
            EndCurrentStep();

            bool showRatingAfterExplosion = ShouldShowConditionRatingAfterStep(endedStep);
            bool insertPairwiseDelay = ShouldInsertPairwiseInterPresentationDelay(endedStep);

            // Even if Hide Current Fixation During Explosion is false, enforce a blank
            // interval between the two presentations of the same pairwise comparison.
            if (insertPairwiseDelay)
            {
                if (ShouldManageFixationPointMarkers())
                    SetAllFixationPointsVisible(false);

                if (fixationCue != null)
                    fixationCue.SetActive(false);
            }

            float lodSwapDelay = Mathf.Max(0f, explosionLodSwapDelaySeconds);
            float nextFixationDelay = Mathf.Max(lodSwapDelay, explosionNextFixationDelaySeconds);

            yield return WaitForExplosionSeconds(lodSwapDelay);

            if (showRatingAfterExplosion)
            {
                yield return WaitForExplosionSeconds(nextFixationDelay - lodSwapDelay);

                explosionMaskedTransitionRunning = false;
                explosionMaskedTransitionCoroutine = null;
                ShowConditionRatingCanvas(endedStep);
                yield break;
            }

            currentStepListIndex++;

            if (currentStepListIndex >= steps.Count)
            {
                yield return WaitForExplosionSeconds(nextFixationDelay - lodSwapDelay);

                explosionMaskedTransitionRunning = false;
                explosionMaskedTransitionCoroutine = null;
                FinishSequence();
                yield break;
            }

            currentStep = steps[currentStepListIndex];
            currentStepElapsed = 0f;

            HideConditionRatingCanvas();

            // The LOD swap still occurs during the explosion/blank transition,
            // before the new fixation marker is shown.
            ClearPendingFirstClickLod();
            ApplyLodForStepIfNeeded(currentStep);

            if (debugExplosionMaskedTransition)
            {
                Debug.Log(
                    $"CrowdingDisplayHandler: LOD applied during explosion. Next step = {currentStep}.",
                    this
                );
            }

            // Complete the normal explosion-to-next-fixation delay first.
            yield return WaitForExplosionSeconds(nextFixationDelay - lodSwapDelay);

            // Add the extra blank interval only between the first and second
            // presentation of the same pairwise comparison.
            if (insertPairwiseDelay)
            {
                float pairwiseDelay = Mathf.Max(0f, pairwiseInterPresentationDelaySeconds);

                if (debugPairwiseParadigm)
                {
                    Debug.Log(
                        $"CrowdingDisplayHandler: pairwise blank interval of {pairwiseDelay:F3}s between presentations 1 and 2 of pair {endedStep.comparisonPairIndex}.",
                        this
                    );
                }

                yield return WaitForExplosionSeconds(pairwiseDelay);
            }

            currentStep.startTimeRealtime = Time.realtimeSinceStartupAsDouble;
            DisplayFixation(currentStep.fixationIndex);
            StepStarted?.Invoke(currentStep);

            if (debugExplosionMaskedTransition)
                Debug.Log($"CrowdingDisplayHandler: next fixation shown after explosion: {currentStep}", this);

            explosionMaskedTransitionRunning = false;
            explosionMaskedTransitionCoroutine = null;
        }

        private IEnumerator WaitForExplosionSeconds(float seconds)
        {
            if (seconds <= 0f)
                yield break;

            if (useUnscaledTimeForExplosionTransition)
            {
                float start = Time.unscaledTime;
                while (Time.unscaledTime - start < seconds)
                    yield return null;
            }
            else
            {
                yield return new WaitForSeconds(seconds);
            }
        }

        private void SpawnFixationExplosion(Vector3 position, Quaternion rotation)
        {
            Vector3 mainSpawnPosition = position + explosionWorldOffset;
            SpawnOneExplosion(mainSpawnPosition, rotation, false);

            if (spawnAdditionalFieldExplosions && additionalFieldExplosionCount > 0)
                SpawnAdditionalExplosionsInFieldOfView(mainSpawnPosition);
        }

        private void SpawnOneExplosion(Vector3 spawnPosition, Quaternion rotation, bool isAdditionalExplosion)
        {
            if (fixationExplosionPrefab != null)
            {
                GameObject instance = Instantiate(fixationExplosionPrefab, spawnPosition, rotation);
                instance.name = fixationExplosionPrefab.name + (isAdditionalExplosion ? "_RuntimeFieldExplosion" : "_RuntimeExplosion");
                instance.transform.localScale *= Mathf.Max(0.01f, explosionScaleMultiplier);
                instance.SetActive(true);

                if (forceExplosionLayerOnSpawn)
                    ForceExplosionLayer(instance);

                if (forceExplosionRenderersEnabled)
                    EnableExplosionRenderers(instance);

                if (playExplosionParticleSystemsOnSpawn)
                    PlayExplosionParticleSystems(instance);

                if (playExplosionVisualEffectsOnSpawn)
                    PlayExplosionVisualEffects(instance);

                if (explosionAutoDestroySeconds > 0f)
                    Destroy(instance, explosionAutoDestroySeconds);

                if (debugExplosionMaskedTransition)
                {
                    string explosionKind = isAdditionalExplosion ? "additional field explosion" : "main fixation explosion";
                    Debug.Log($"CrowdingDisplayHandler: spawned {explosionKind} prefab '{fixationExplosionPrefab.name}' at {spawnPosition} on layer '{LayerMask.LayerToName(instance.layer)}'.", instance);
                }

                return;
            }

            if (useProceduralExplosionIfNoPrefab)
            {
                CrowdingProceduralExplosion.Spawn(
                    spawnPosition,
                    proceduralExplosionParticleCount,
                    proceduralExplosionRadius,
                    proceduralExplosionDuration,
                    proceduralExplosionParticleSize
                );
            }
        }

        private void SpawnAdditionalExplosionsInFieldOfView(Vector3 mainSpawnPosition)
        {
            Camera cam = ResolveExplosionFieldCamera();
            if (cam == null)
            {
                if (debugExplosionMaskedTransition)
                    Debug.LogWarning("CrowdingDisplayHandler: additional field explosions skipped because no camera could be resolved.", this);
                return;
            }

            int count = Mathf.Max(0, additionalFieldExplosionCount);
            if (count == 0)
                return;

            float margin = Mathf.Clamp(additionalFieldExplosionViewportMargin, 0f, 0.45f);
            float minAngleDeg = Mathf.Max(0f, additionalFieldExplosionMinAngularSeparationDeg);

            Vector3 camPosition = cam.transform.position;
            Vector3 mainDirection = mainSpawnPosition - camPosition;
            float mainDistance = mainDirection.magnitude;
            if (mainDistance > 1e-6f)
                mainDirection /= mainDistance;
            else
                mainDirection = cam.transform.forward;

            float spawnDistance = additionalFieldExplosionsUseMainDistance
                ? Mathf.Max(0.1f, mainDistance)
                : Mathf.Max(0.1f, additionalFieldExplosionDistanceFromCamera);

            List<Vector3> acceptedDirections = new List<Vector3>();
            int maxAttemptsPerExplosion = 40;

            for (int i = 0; i < count; i++)
            {
                bool placed = false;

                for (int attempt = 0; attempt < maxAttemptsPerExplosion; attempt++)
                {
                    float viewportX = Mathf.Lerp(margin, 1f - margin, NextExplosionRandom01());
                    float viewportY = Mathf.Lerp(margin, 1f - margin, NextExplosionRandom01());

                    Ray ray = cam.ViewportPointToRay(new Vector3(viewportX, viewportY, 0f));
                    Vector3 direction = ray.direction.normalized;

                    if (minAngleDeg > 0f && Vector3.Angle(mainDirection, direction) < minAngleDeg)
                        continue;

                    bool tooCloseToAnotherAdditionalExplosion = false;
                    for (int d = 0; d < acceptedDirections.Count; d++)
                    {
                        if (Vector3.Angle(acceptedDirections[d], direction) < minAngleDeg)
                        {
                            tooCloseToAnotherAdditionalExplosion = true;
                            break;
                        }
                    }

                    if (tooCloseToAnotherAdditionalExplosion)
                        continue;

                    Vector3 spawnPosition = camPosition + direction * spawnDistance + additionalFieldExplosionWorldOffset;
                    Quaternion spawnRotation = Quaternion.LookRotation(direction, Vector3.up);
                    SpawnOneExplosion(spawnPosition, spawnRotation, true);
                    acceptedDirections.Add(direction);
                    placed = true;
                    break;
                }

                if (!placed && debugExplosionMaskedTransition)
                {
                    Debug.LogWarning(
                        "CrowdingDisplayHandler: could not place one additional field explosion. " +
                        "Try reducing Additional Field Explosion Min Angular Separation Deg or Viewport Margin.",
                        this
                    );
                }
            }
        }

        private Camera ResolveExplosionFieldCamera()
        {
            if (pipelineController != null && pipelineController.viewCamera != null)
                return pipelineController.viewCamera;

            if (Camera.main != null)
                return Camera.main;

            return null;
        }

        private float NextExplosionRandom01()
        {
            if (deterministicRandom && rng != null)
                return (float)rng.NextDouble();

            return UnityEngine.Random.value;
        }

        private void ForceExplosionLayer(GameObject root)
        {
            if (root == null)
                return;

            int targetLayer = LayerMask.NameToLayer(string.IsNullOrWhiteSpace(explosionLayerName) ? "Default" : explosionLayerName.Trim());

            if (targetLayer < 0)
            {
                Debug.LogWarning($"CrowdingDisplayHandler: explosion layer '{explosionLayerName}' was not found. Keeping prefab original layers.", this);
                return;
            }

            SetLayerRecursively(root, targetLayer);
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null)
                return;

            root.layer = layer;

            Transform tr = root.transform;
            for (int i = 0; i < tr.childCount; i++)
                SetLayerRecursively(tr.GetChild(i).gameObject, layer);
        }

        private static void EnableExplosionRenderers(GameObject root)
        {
            if (root == null)
                return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = true;
            }
        }

        private static void PlayExplosionParticleSystems(GameObject root)
        {
            if (root == null)
                return;

            ParticleSystem[] particleSystems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particleSystems.Length; i++)
            {
                ParticleSystem ps = particleSystems[i];
                if (ps == null)
                    continue;

                ps.gameObject.SetActive(true);
                ps.Clear(true);
                ps.Play(true);
            }
        }

        private static void PlayExplosionVisualEffects(GameObject root)
        {
            if (root == null)
                return;

            Type visualEffectType = Type.GetType("UnityEngine.VFX.VisualEffect, Unity.VisualEffectGraph.Runtime");
            if (visualEffectType == null)
                return;

            Component[] visualEffects = root.GetComponentsInChildren(visualEffectType, true);
            for (int i = 0; i < visualEffects.Length; i++)
            {
                Component component = visualEffects[i];
                if (component == null)
                    continue;

                component.gameObject.SetActive(true);

                if (component is Behaviour behaviour)
                    behaviour.enabled = true;

                visualEffectType.GetMethod("Reinit")?.Invoke(component, null);
                visualEffectType.GetMethod("Play")?.Invoke(component, null);
            }
        }

        private void ClearExplosionMaskedTransition(bool stopCoroutine)
        {
            if (stopCoroutine && explosionMaskedTransitionCoroutine != null)
                StopCoroutine(explosionMaskedTransitionCoroutine);

            explosionMaskedTransitionCoroutine = null;
            explosionMaskedTransitionRunning = false;
        }

        private bool ShouldShowConditionRatingAfterStep(CrowdingDisplayStepInfo endedStep)
        {
            if (!enableConditionRatingCanvas)
                return false;

            if (endedStep == null || endedStep.startTimeRealtime <= 0)
                return false;

            GameObject targetCanvas = GetRatingCanvasForStep(endedStep);
            if (targetCanvas == null)
            {
                Debug.LogWarning("CrowdingDisplayHandler: rating is enabled, but no rating canvas is assigned. The sequence will continue without rating.", this);
                return false;
            }

            // Pairwise paradigms: ask only after both versions of the pair have been shown.
            if (IsPairwiseParadigm())
                return endedStep.comparisonPairIndex >= 0 && endedStep.comparisonPresentationIndex == 1;

            // Previous mode: ask for one score after every fixation point.
            if (ratingGranularity == CrowdingRatingGranularity.EachFixationPoint)
                return true;

            int nextStepIndex = currentStepListIndex + 1;

            // End of the full sequence: still ask for the last condition rating.
            if (nextStepIndex >= steps.Count)
                return true;

            CrowdingDisplayStepInfo nextStep = steps[nextStepIndex];

            // A condition is complete when the next step belongs to a different combination.
            // This means all fixation points for Reference / Uniform K / Weighted K have been seen.
            return nextStep == null || nextStep.combinationOrderIndex != endedStep.combinationOrderIndex;
        }

        private bool IsPairwiseParadigm()
        {
            return experimentalParadigm == CrowdingExperimentParadigm.ReferenceVsDistortionRating ||
                   experimentalParadigm == CrowdingExperimentParadigm.UniformVsWeightedPreference;
        }

        private GameObject GetRatingCanvasForStep(CrowdingDisplayStepInfo step)
        {
            if (experimentalParadigm == CrowdingExperimentParadigm.UniformVsWeightedPreference && pairwisePreferenceCanvas != null)
                return pairwisePreferenceCanvas;

            return conditionRatingCanvas;
        }

        private void ShowConditionRatingCanvas(CrowdingDisplayStepInfo endedStep)
        {
            ClearPendingFirstClickLod();
            waitingForConditionRating = true;
            pendingRatingStep = endedStep;
            conditionRatingStartTimeRealtime = Time.realtimeSinceStartupAsDouble;

            if (hideFixationCueDuringRating && fixationCue != null)
                fixationCue.SetActive(false);

            if (hideFixationMarkersDuringRating && ShouldManageFixationPointMarkers())
                SetAllFixationPointsVisible(false);

            if (hideTaggedOrLayeredObjectsDuringRating)
                HideTaggedOrLayeredObjectsForRating();

            GameObject targetCanvas = GetRatingCanvasForStep(endedStep);
            if (conditionRatingCanvas != null && conditionRatingCanvas != targetCanvas)
                conditionRatingCanvas.SetActive(false);
            if (pairwisePreferenceCanvas != null && pairwisePreferenceCanvas != targetCanvas)
                pairwisePreferenceCanvas.SetActive(false);

            if (targetCanvas != null)
                targetCanvas.SetActive(true);

            Debug.Log($"CrowdingDisplayHandler: waiting for {BuildRatingTargetLabel(endedStep)} response after {BuildConditionLabel(endedStep)}.", this);
        }

        private void HideConditionRatingCanvas(bool restoreSceneObjects = true)
        {
            if (conditionRatingCanvas != null)
                conditionRatingCanvas.SetActive(false);

            if (pairwisePreferenceCanvas != null)
                pairwisePreferenceCanvas.SetActive(false);

            if (restoreSceneObjects && restoreTaggedOrLayeredObjectsAfterRating)
                RestoreTaggedOrLayeredObjectsAfterRating();
        }

        private void HideTaggedOrLayeredObjectsForRating()
        {
            ratingHiddenObjectActiveStates.Clear();

            string targetTag = string.IsNullOrWhiteSpace(objectsTagToHideDuringRating)
                ? string.Empty
                : objectsTagToHideDuringRating.Trim();

            string targetLayerName = string.IsNullOrWhiteSpace(objectsLayerToHideDuringRating)
                ? string.Empty
                : objectsLayerToHideDuringRating.Trim();

            int targetLayer = string.IsNullOrEmpty(targetLayerName) ? -1 : LayerMask.NameToLayer(targetLayerName);
            bool useLayer = targetLayer >= 0;
            bool useTag = !string.IsNullOrEmpty(targetTag);
            bool tagLookupFailed = false;

            if (!useTag && !useLayer)
                return;

            if (!string.IsNullOrEmpty(targetLayerName) && !useLayer)
            {
                Debug.LogWarning($"CrowdingDisplayHandler: layer '{targetLayerName}' was not found. Layer-based hiding during rating will be ignored.", this);
            }

            Transform[] allTransforms = Resources.FindObjectsOfTypeAll<Transform>();

            for (int i = 0; i < allTransforms.Length; i++)
            {
                Transform tr = allTransforms[i];
                if (tr == null)
                    continue;

                GameObject go = tr.gameObject;
                if (go == null)
                    continue;

                // Ignore assets/prefabs and keep only objects that belong to a loaded scene.
                if (!go.scene.IsValid() || !go.scene.isLoaded)
                    continue;

                // Never hide the handler itself or the rating canvas/buttons, otherwise the response UI can break.
                if (go == gameObject || IsInsideAnyRatingCanvas(go))
                    continue;

                bool layerMatch = useLayer && go.layer == targetLayer;
                bool tagMatch = false;

                if (useTag && !tagLookupFailed)
                {
                    try
                    {
                        tagMatch = go.CompareTag(targetTag);
                    }
                    catch (UnityException)
                    {
                        tagLookupFailed = true;
                        Debug.LogWarning($"CrowdingDisplayHandler: tag '{targetTag}' was not found. Tag-based hiding during rating will be ignored.", this);
                    }
                }

                if (!layerMatch && !tagMatch)
                    continue;

                if (!ratingHiddenObjectActiveStates.ContainsKey(go))
                    ratingHiddenObjectActiveStates.Add(go, go.activeSelf);

                go.SetActive(false);
            }

            Debug.Log($"CrowdingDisplayHandler: hidden {ratingHiddenObjectActiveStates.Count} tagged/layered scene objects during rating.", this);
        }

        private void RestoreTaggedOrLayeredObjectsAfterRating()
        {
            if (ratingHiddenObjectActiveStates.Count == 0)
                return;

            foreach (KeyValuePair<GameObject, bool> kvp in ratingHiddenObjectActiveStates)
            {
                if (kvp.Key != null)
                    kvp.Key.SetActive(kvp.Value);
            }

            Debug.Log($"CrowdingDisplayHandler: restored {ratingHiddenObjectActiveStates.Count} tagged/layered scene objects after rating.", this);
            ratingHiddenObjectActiveStates.Clear();
        }

        private bool IsInsideAnyRatingCanvas(GameObject candidate)
        {
            if (candidate == null)
                return false;

            return IsInsideCanvas(candidate, conditionRatingCanvas) ||
                   IsInsideCanvas(candidate, pairwisePreferenceCanvas);
        }

        private static bool IsInsideCanvas(GameObject candidate, GameObject canvas)
        {
            if (candidate == null || canvas == null)
                return false;

            Transform canvasTransform = canvas.transform;
            Transform candidateTransform = candidate.transform;

            return candidateTransform == canvasTransform || candidateTransform.IsChildOf(canvasTransform);
        }

        public void SubmitConditionRating1() => SubmitConditionRating(1);
        public void SubmitConditionRating2() => SubmitConditionRating(2);
        public void SubmitConditionRating3() => SubmitConditionRating(3);
        public void SubmitConditionRating4() => SubmitConditionRating(4);
        public void SubmitConditionRating5() => SubmitConditionRating(5);

        public void SubmitConditionRating(int rating)
        {
            if (!waitingForConditionRating)
            {
                Debug.LogWarning($"CrowdingDisplayHandler: rating {rating} ignored because no rating is currently expected.", this);
                return;
            }

            if (experimentalParadigm == CrowdingExperimentParadigm.UniformVsWeightedPreference)
            {
                Debug.LogWarning("CrowdingDisplayHandler: numeric rating ignored because the current paradigm expects a pairwise preference. Use CrowdingPairwisePreferenceButton instead.", this);
                return;
            }

            int clampedRating = Mathf.Clamp(rating, 1, 5);
            double ratingEndTime = Time.realtimeSinceStartupAsDouble;

            if (logConditionRatingsCsv)
                AppendConditionRatingToLog(pendingRatingStep, clampedRating, conditionRatingStartTimeRealtime, ratingEndTime);

            Debug.Log($"CrowdingDisplayHandler: received rating {clampedRating} ({GetDisturbanceRatingLabel(clampedRating)}) for {BuildRatingTargetLabel(pendingRatingStep)} / {BuildConditionLabel(pendingRatingStep)}.", this);

            CompletePendingRatingAndContinue();
        }

        public void SubmitPairwisePreferenceFirst() => SubmitPairwisePreference(CrowdingPairwiseChoice.FirstPresentation);
        public void SubmitPairwisePreferenceSecond() => SubmitPairwisePreference(CrowdingPairwiseChoice.SecondPresentation);
        public void SubmitPairwisePreferenceNoPreference() => SubmitPairwisePreference(CrowdingPairwiseChoice.NoPreference);

        public void SubmitPairwisePreference(CrowdingPairwiseChoice choice)
        {
            if (!waitingForConditionRating)
            {
                Debug.LogWarning($"CrowdingDisplayHandler: preference {choice} ignored because no pairwise preference is currently expected.", this);
                return;
            }

            if (experimentalParadigm != CrowdingExperimentParadigm.UniformVsWeightedPreference)
            {
                Debug.LogWarning("CrowdingDisplayHandler: pairwise preference ignored because the current paradigm is not UniformVsWeightedPreference.", this);
                return;
            }

            double responseEndTime = Time.realtimeSinceStartupAsDouble;

            AppendPairwisePreferenceToLog(pendingRatingStep, choice, conditionRatingStartTimeRealtime, responseEndTime);

            if (debugPairwiseParadigm)
                Debug.Log($"CrowdingDisplayHandler: received pairwise preference {choice} for {BuildRatingTargetLabel(pendingRatingStep)}.", this);

            CompletePendingRatingAndContinue();
        }

        private void CompletePendingRatingAndContinue()
        {
            waitingForConditionRating = false;
            pendingRatingStep = null;
            conditionRatingStartTimeRealtime = 0.0;
            ClearPendingFirstClickLod();
            HideConditionRatingCanvas(false);

            // Keep the scene objects hidden until the next step has applied its LOD.
            // This avoids showing a visible LOD swap immediately after the rating panel closes.
            StartNextStepOrFinish(false);
        }

        [ContextMenu("Display Handler/Rebuild Sequence Only")]
        public void BuildSequence()
        {
            rng = deterministicRandom ? new System.Random(randomSeed) : new System.Random();

            combinations = BuildCombinations();
            ApplyCombinationRandomization(combinations);

            steps.Clear();

            List<int> baseFixationOrder = BuildFixationOrder();

            int global = 0;
            switch (experimentalParadigm)
            {
                case CrowdingExperimentParadigm.ReferenceVsDistortionRating:
                    BuildReferenceVsDistortionSequence(baseFixationOrder, ref global);
                    break;

                case CrowdingExperimentParadigm.UniformVsWeightedPreference:
                    BuildUniformVsWeightedPreferenceSequence(baseFixationOrder, ref global);
                    break;

                case CrowdingExperimentParadigm.DirectRating:
                default:
                    BuildDirectRatingSequence(baseFixationOrder, ref global);
                    break;
            }

            Debug.Log($"CrowdingDisplayHandler: sequence built. Paradigm={experimentalParadigm}, Combinations={combinations.Count}, steps={steps.Count}", this);
        }

        private void BuildDirectRatingSequence(List<int> baseFixationOrder, ref int global)
        {
            for (int c = 0; c < combinations.Count; c++)
            {
                CrowdingDisplayCombination combo = combinations[c];

                List<int> fixationOrder =
                    randomizeFixationOrder && randomizeFixationOrderPerCombination
                        ? BuildFixationOrder()
                        : new List<int>(baseFixationOrder);

                for (int fOrder = 0; fOrder < fixationOrder.Count; fOrder++)
                {
                    int fixationIndex = fixationOrder[fOrder];
                    Transform fixation = GetFixationPoint(fixationIndex);
                    if (fixation == null)
                        continue;

                    steps.Add(CreateStep(combo, global, c, fOrder, fixationIndex));
                    global++;
                }
            }
        }

        private void BuildReferenceVsDistortionSequence(List<int> baseFixationOrder, ref int global)
        {
            CrowdingDisplayCombination referenceCombo = null;
            List<CrowdingDisplayCombination> distortedCombos = new List<CrowdingDisplayCombination>();

            for (int i = 0; i < combinations.Count; i++)
            {
                if (combinations[i].IsReference)
                    referenceCombo = combinations[i];
                else
                    distortedCombos.Add(combinations[i]);
            }

            if (referenceCombo == null)
            {
                Debug.LogError("CrowdingDisplayHandler: ReferenceVsDistortionRating requires Include Reference Condition = true.", this);
                return;
            }

            if (distortedCombos.Count == 0)
            {
                Debug.LogError("CrowdingDisplayHandler: ReferenceVsDistortionRating requires at least one distorted condition, e.g. Uniform or Weighted with a K value.", this);
                return;
            }

            if (randomizeCombinationOrder)
                ShuffleList(distortedCombos);

            int pairIndex = 0;
            for (int c = 0; c < distortedCombos.Count; c++)
            {
                CrowdingDisplayCombination distortedCombo = distortedCombos[c];

                List<int> fixationOrder =
                    randomizeFixationOrder && randomizeFixationOrderPerCombination
                        ? BuildFixationOrder()
                        : new List<int>(baseFixationOrder);

                for (int fOrder = 0; fOrder < fixationOrder.Count; fOrder++)
                {
                    int fixationIndex = fixationOrder[fOrder];
                    Transform fixation = GetFixationPoint(fixationIndex);
                    if (fixation == null)
                        continue;

                    bool referenceFirst = !randomizePairPresentationOrder || rng.Next(0, 2) == 0;
                    CrowdingDisplayCombination first = referenceFirst ? referenceCombo : distortedCombo;
                    CrowdingDisplayCombination second = referenceFirst ? distortedCombo : referenceCombo;

                    CrowdingDisplayStepInfo firstStep = CreateStep(first, global, c, fOrder, fixationIndex);
                    FillReferenceVsDistortionPairMetadata(firstStep, pairIndex, 0, referenceFirst, referenceCombo, distortedCombo, fixationIndex);
                    steps.Add(firstStep);
                    global++;

                    CrowdingDisplayStepInfo secondStep = CreateStep(second, global, c, fOrder, fixationIndex);
                    FillReferenceVsDistortionPairMetadata(secondStep, pairIndex, 1, referenceFirst, referenceCombo, distortedCombo, fixationIndex);
                    steps.Add(secondStep);
                    global++;

                    pairIndex++;
                }
            }

            if (debugPairwiseParadigm)
                Debug.Log($"CrowdingDisplayHandler: built {pairIndex} Reference-vs-Distortion comparison pairs.", this);
        }

        private void BuildUniformVsWeightedPreferenceSequence(List<int> baseFixationOrder, ref int global)
        {
            List<float> kValues = new List<float>();

            for (int i = 0; i < combinations.Count; i++)
            {
                CrowdingDisplayCombination combo = combinations[i];
                if (combo == null || combo.IsReference)
                    continue;

                float kk = Mathf.Clamp01(combo.keepRatioK);
                bool found = false;
                for (int k = 0; k < kValues.Count; k++)
                {
                    if (Mathf.Approximately(kValues[k], kk))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    kValues.Add(kk);
            }

            if (kValues.Count == 0)
            {
                Debug.LogError("CrowdingDisplayHandler: UniformVsWeightedPreference requires matching Uniform and Weighted conditions with at least one K value.", this);
                return;
            }

            if (randomizeCombinationOrder)
                ShuffleList(kValues);

            int pairIndex = 0;

            for (int k = 0; k < kValues.Count; k++)
            {
                float kk = kValues[k];

                CrowdingDisplayCombination uniformCombo = FindCombo(CrowdingDisplayConditionType.Uniform, CrowdingDecimationCondition.Uniform, kk);
                CrowdingDisplayCombination weightedCombo = FindCombo(CrowdingDisplayConditionType.Weighted, CrowdingDecimationCondition.WeightedCrowding, kk);

                if (uniformCombo == null || weightedCombo == null)
                {
                    Debug.LogWarning($"CrowdingDisplayHandler: skipping K={kk:F4} because Uniform and Weighted/Redistributed variants are not both included.", this);
                    continue;
                }

                List<int> fixationOrder =
                    randomizeFixationOrder && randomizeFixationOrderPerCombination
                        ? BuildFixationOrder()
                        : new List<int>(baseFixationOrder);

                for (int fOrder = 0; fOrder < fixationOrder.Count; fOrder++)
                {
                    int fixationIndex = fixationOrder[fOrder];
                    Transform fixation = GetFixationPoint(fixationIndex);
                    if (fixation == null)
                        continue;

                    bool uniformFirst = !randomizePairPresentationOrder || rng.Next(0, 2) == 0;
                    CrowdingDisplayCombination first = uniformFirst ? uniformCombo : weightedCombo;
                    CrowdingDisplayCombination second = uniformFirst ? weightedCombo : uniformCombo;

                    CrowdingDisplayStepInfo firstStep = CreateStep(first, global, k, fOrder, fixationIndex);
                    FillUniformVsWeightedPairMetadata(firstStep, pairIndex, 0, uniformFirst, uniformCombo, weightedCombo, fixationIndex);
                    steps.Add(firstStep);
                    global++;

                    CrowdingDisplayStepInfo secondStep = CreateStep(second, global, k, fOrder, fixationIndex);
                    FillUniformVsWeightedPairMetadata(secondStep, pairIndex, 1, uniformFirst, uniformCombo, weightedCombo, fixationIndex);
                    steps.Add(secondStep);
                    global++;

                    pairIndex++;
                }
            }

            if (debugPairwiseParadigm)
                Debug.Log($"CrowdingDisplayHandler: built {pairIndex} Uniform-vs-Weighted preference pairs.", this);
        }

        private CrowdingDisplayStepInfo CreateStep(CrowdingDisplayCombination combo, int global, int combinationOrderIndex, int fixationOrderIndex, int fixationIndex)
        {
            Transform fixation = GetFixationPoint(fixationIndex);

            return new CrowdingDisplayStepInfo
            {
                globalStepIndex = global,
                combinationOrderIndex = combinationOrderIndex,
                fixationOrderIndex = fixationOrderIndex,
                fixationIndex = fixationIndex,
                fixationName = fixation != null ? fixation.name : "MissingFixation",
                conditionType = combo.conditionType,
                isReference = combo.IsReference,
                keepRatioK = combo.keepRatioK,
                decimationCondition = combo.decimationCondition,
                variantKey = combo.BuildVariantKey(fixationIndex, useGlobalSceneVariants),
                sceneName = SceneManager.GetActiveScene().name,
                participantId = ParticipantId,
                sessionId = SessionId,
                globalSessionIndex = GlobalSessionIndex,
                experimentParadigm = experimentalParadigm
            };
        }

        private void FillReferenceVsDistortionPairMetadata(
            CrowdingDisplayStepInfo step,
            int pairIndex,
            int presentationIndex,
            bool referenceFirst,
            CrowdingDisplayCombination referenceCombo,
            CrowdingDisplayCombination distortedCombo,
            int fixationIndex)
        {
            string refKey = referenceCombo.BuildVariantKey(fixationIndex, useGlobalSceneVariants);
            string distortedKey = distortedCombo.BuildVariantKey(fixationIndex, useGlobalSceneVariants);

            step.comparisonPairIndex = pairIndex;
            step.comparisonPresentationIndex = presentationIndex;
            step.comparisonType = "ReferenceVsDistortion";
            step.comparisonFirstIsReference = referenceFirst;
            step.comparisonFirstIsUniform = false;
            step.comparisonFirstIsWeighted = false;
            step.comparisonFirstLabel = firstPresentationLabel;
            step.comparisonSecondLabel = secondPresentationLabel;
            step.comparisonFirstVariantKey = referenceFirst ? refKey : distortedKey;
            step.comparisonSecondVariantKey = referenceFirst ? distortedKey : refKey;
            step.comparisonDistortedConditionType = distortedCombo.conditionType;
            step.comparisonDistortedKeepRatioK = distortedCombo.keepRatioK;
            step.comparisonDistortedDecimationCondition = distortedCombo.decimationCondition;
            step.comparisonDistortedVariantKey = distortedKey;
        }

        private void FillUniformVsWeightedPairMetadata(
            CrowdingDisplayStepInfo step,
            int pairIndex,
            int presentationIndex,
            bool uniformFirst,
            CrowdingDisplayCombination uniformCombo,
            CrowdingDisplayCombination weightedCombo,
            int fixationIndex)
        {
            string uniformKey = uniformCombo.BuildVariantKey(fixationIndex, useGlobalSceneVariants);
            string weightedKey = weightedCombo.BuildVariantKey(fixationIndex, useGlobalSceneVariants);

            step.comparisonPairIndex = pairIndex;
            step.comparisonPresentationIndex = presentationIndex;
            step.comparisonType = "UniformVsWeightedPreference";
            step.comparisonFirstIsReference = false;
            step.comparisonFirstIsUniform = uniformFirst;
            step.comparisonFirstIsWeighted = !uniformFirst;
            step.comparisonFirstLabel = firstPresentationLabel;
            step.comparisonSecondLabel = secondPresentationLabel;
            step.comparisonFirstVariantKey = uniformFirst ? uniformKey : weightedKey;
            step.comparisonSecondVariantKey = uniformFirst ? weightedKey : uniformKey;
            step.comparisonDistortedConditionType = CrowdingDisplayConditionType.Weighted;
            step.comparisonDistortedKeepRatioK = weightedCombo.keepRatioK;
            step.comparisonDistortedDecimationCondition = weightedCombo.decimationCondition;
            step.comparisonDistortedVariantKey = weightedKey;
        }

        private CrowdingDisplayCombination FindCombo(CrowdingDisplayConditionType conditionType, CrowdingDecimationCondition decimationCondition, float keepRatioK)
        {
            for (int i = 0; i < combinations.Count; i++)
            {
                CrowdingDisplayCombination combo = combinations[i];
                if (combo == null || combo.IsReference)
                    continue;

                if (combo.conditionType == conditionType &&
                    combo.decimationCondition == decimationCondition &&
                    Mathf.Approximately(combo.keepRatioK, keepRatioK))
                {
                    return combo;
                }
            }

            return null;
        }

        private void ShuffleList<T>(IList<T> list)
        {
            if (list == null || list.Count <= 1)
                return;

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(0, i + 1);
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        private List<CrowdingDisplayCombination> BuildCombinations()
        {
            List<CrowdingDisplayCombination> list = new List<CrowdingDisplayCombination>();

            if (externalSingleConditionMode && externalSingleCondition != null)
            {
                list.Add(new CrowdingDisplayCombination
                {
                    conditionType = externalSingleCondition.conditionType,
                    keepRatioK = externalSingleCondition.keepRatioK,
                    decimationCondition = externalSingleCondition.decimationCondition
                });

                return list;
            }

            if (includeReferenceCondition)
            {
                list.Add(new CrowdingDisplayCombination
                {
                    conditionType = CrowdingDisplayConditionType.Reference,
                    keepRatioK = 1f,
                    decimationCondition = CrowdingDecimationCondition.Uniform
                });
            }

            foreach (float k in keepRatiosK)
            {
                float kk = Mathf.Clamp01(k);

                if (includeUniformCondition)
                {
                    list.Add(new CrowdingDisplayCombination
                    {
                        conditionType = CrowdingDisplayConditionType.Uniform,
                        keepRatioK = kk,
                        decimationCondition = CrowdingDecimationCondition.Uniform
                    });
                }

                if (includeWeightedCondition)
                {
                    list.Add(new CrowdingDisplayCombination
                    {
                        conditionType = CrowdingDisplayConditionType.Weighted,
                        keepRatioK = kk,
                        decimationCondition = CrowdingDecimationCondition.WeightedCrowding
                    });
                }
            }

            return list;
        }

        private void ApplyCombinationRandomization(List<CrowdingDisplayCombination> list)
        {
            if (!randomizeCombinationOrder || list.Count <= 1) return;

            int startIndex = 0;
            if (keepReferenceFirstWhenRandomized && list[0].IsReference)
                startIndex = 1;

            for (int i = list.Count - 1; i > startIndex; i--)
            {
                int j = rng.Next(startIndex, i + 1);
                CrowdingDisplayCombination tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        private IReadOnlyList<Transform> GetActiveFixationPoints()
        {
            if (useControllerFixationPoints && pipelineController != null &&
                pipelineController.fixationPoints != null && pipelineController.fixationPoints.Count > 0)
            {
                return pipelineController.fixationPoints;
            }

            return fixationPoints;
        }

        private Transform GetFixationPoint(int fixationIndex)
        {
            IReadOnlyList<Transform> activeFixations = GetActiveFixationPoints();

            if (activeFixations == null)
                return null;

            if (fixationIndex < 0 || fixationIndex >= activeFixations.Count)
                return null;

            return activeFixations[fixationIndex];
        }

        private List<int> BuildFixationOrder()
        {
            IReadOnlyList<Transform> activeFixations = GetActiveFixationPoints();
            List<int> validIndices = new List<int>();

            if (activeFixations == null || activeFixations.Count == 0)
            {
                Debug.LogWarning(
                    "CrowdingDisplayHandler: no fixation points found. " +
                    "Assign them in CrowdingAwarePipelineController, or disable Use Controller Fixation Points and fill the local list.",
                    this
                );
                return validIndices;
            }

            for (int i = 0; i < activeFixations.Count; i++)
            {
                if (activeFixations[i] != null)
                    validIndices.Add(i);
            }

            List<int> order = useFixationSubset
                ? BuildFixationSubset(validIndices, fixationSubsetCount)
                : new List<int>(validIndices);

            if (randomizeFixationOrder)
                ShuffleList(order);

            if (debugFixationSubset)
            {
                Debug.Log(
                    "CrowdingDisplayHandler: fixation order = [" + string.Join(", ", order) + "]" +
                    " | subset=" + useFixationSubset +
                    " | requestedCount=" + fixationSubsetCount +
                    " | validCount=" + validIndices.Count,
                    this
                );
            }

            return order;
        }

        private static List<int> BuildFixationSubset(List<int> validIndices, int requestedCount)
        {
            List<int> subset = new List<int>();

            if (validIndices == null || validIndices.Count == 0)
                return subset;

            int n = validIndices.Count;
            int count = Mathf.Clamp(requestedCount, 1, n);

            if (count >= n)
                return new List<int>(validIndices);

            // Default case for count = 3:
            // first, last, median-lower.
            // Examples:
            // n = 5 -> indices 0, 4, 2
            // n = 6 -> indices 0, 5, 2
            AddUniqueIndex(subset, validIndices[0]);

            if (count >= 2)
                AddUniqueIndex(subset, validIndices[n - 1]);

            if (count >= 3)
            {
                int medianLowerPosition = (n - 1) / 2;
                AddUniqueIndex(subset, validIndices[medianLowerPosition]);
            }

            // For counts > 3, add additional evenly distributed interior points.
            // The default requested count is 3, so this is only a convenience fallback.
            if (count > 3)
            {
                int attempts = 0;
                int maxAttempts = Mathf.Max(1, n * 4);

                while (subset.Count < count && attempts < maxAttempts)
                {
                    attempts++;
                    int rank = subset.Count - 1;
                    float ideal = ((float)(rank + 1) / (float)(count - 1)) * (float)(n - 1);
                    int position = Mathf.FloorToInt(ideal);
                    position = Mathf.Clamp(position, 1, n - 2);
                    AddUniqueIndex(subset, validIndices[position]);

                    if (subset.Count >= count)
                        break;

                    // If the ideal position produced a duplicate, fill from the left.
                    for (int i = 1; i < n - 1 && subset.Count < count; i++)
                        AddUniqueIndex(subset, validIndices[i]);
                }
            }

            return subset;
        }

        private static void AddUniqueIndex(List<int> list, int value)
        {
            if (list == null)
                return;

            if (!list.Contains(value))
                list.Add(value);
        }

        private void ApplyStep(CrowdingDisplayStepInfo step)
        {
            // Explosion mode takes priority over the older first-click LOD mode.
            // For the first step, or after a rating panel, the LOD is applied before
            // the fixation marker is shown. Between fixation points, the coroutine
            // applies the LOD during the explosion, then shows the next marker.
            if (useExplosionMaskedLodTransition)
            {
                ClearPendingFirstClickLod();
                ApplyLodForStepIfNeeded(step);
                DisplayFixation(step.fixationIndex);
                return;
            }

            // Legacy mode: show the new fixation marker first. The LOD swap is intentionally delayed
            // until the observer clicks the current fixation marker for the first time.
            DisplayFixation(step.fixationIndex);

            if (applyLodOnFirstFixationClick && StepNeedsLodApplication(step))
            {
                waitingForFirstFixationClickBeforeLod = true;
                pendingFirstClickLodStep = step;

                if (debugLodApplicationOnFirstClick)
                {
                    Debug.Log(
                        $"CrowdingDisplayHandler: LOD application armed for first fixation click. Variant={step.variantKey}, fixation={step.fixationName}.",
                        this
                    );
                }
            }
            else
            {
                waitingForFirstFixationClickBeforeLod = false;
                pendingFirstClickLodStep = null;
                ApplyLodForStepIfNeeded(step);
            }
        }

        private bool StepNeedsLodApplication(CrowdingDisplayStepInfo step)
        {
            if (step == null)
                return false;

            string key = string.IsNullOrWhiteSpace(step.variantKey) ? "reference" : step.variantKey;

            if (applyLodOnlyWhenVariantKeyChanges && key == lastAppliedVariantKey)
                return false;

            return true;
        }

        /// <summary>
        /// Called by CrowdingFixationClickAdvanceInteractable when the current fixation marker is clicked.
        /// Returns true only when this click actually applied a pending LOD change.
        /// </summary>
        public bool ApplyPendingLodFromFixationClick(GameObject clickedFixationMarker)
        {
            if (!sequenceRunning || waitingForConditionRating)
                return false;

            if (!applyLodOnFirstFixationClick)
                return false;

            if (!waitingForFirstFixationClickBeforeLod || pendingFirstClickLodStep == null)
                return false;

            if (clickedFixationMarker != null && !clickedFixationMarker.activeInHierarchy)
            {
                if (debugLodApplicationOnFirstClick)
                {
                    Debug.Log(
                        $"CrowdingDisplayHandler: ignored first-click LOD request from inactive object {clickedFixationMarker.name}.",
                        this
                    );
                }
                return false;
            }

            if (pendingFirstClickLodStep != currentStep)
            {
                ClearPendingFirstClickLod();
                return false;
            }

            CrowdingDisplayStepInfo stepToApply = pendingFirstClickLodStep;
            ClearPendingFirstClickLod();

            if (debugLodApplicationOnFirstClick)
            {
                string sourceName = clickedFixationMarker != null ? clickedFixationMarker.name : "unknown";
                Debug.Log(
                    $"CrowdingDisplayHandler: applying LOD on first fixation click from {sourceName}. Variant={stepToApply.variantKey}, fixation={stepToApply.fixationName}.",
                    this
                );
            }

            ApplyLodForStepIfNeeded(stepToApply);
            return true;
        }

        private void ClearPendingFirstClickLod()
        {
            waitingForFirstFixationClickBeforeLod = false;
            pendingFirstClickLodStep = null;
        }

        private void ApplyLodForStepIfNeeded(CrowdingDisplayStepInfo step)
        {
            if (lodSelector == null)
            {
                Debug.LogError("CrowdingDisplayHandler: no CrowdingSharedMeshLODSelector assigned.", this);
                return;
            }

            string key = string.IsNullOrWhiteSpace(step.variantKey) ? "reference" : step.variantKey;

            if (applyLodOnlyWhenVariantKeyChanges && key == lastAppliedVariantKey)
                return;

            if (step.isReference)
            {
                lodSelector.RestoreOriginalMeshes();
            }
            else if (step.conditionType == CrowdingDisplayConditionType.Uniform ||
                     step.decimationCondition == CrowdingDecimationCondition.Uniform)
            {
                // Uniform baselines are always fixation-invariant.
                // The key was built as Kxxx_uniform_global in BuildVariantKey(...).
                lodSelector.ApplyVariant(key);
            }
            else if (useGlobalSceneVariants)
            {
                lodSelector.ApplyVariant(key);
            }
            else
            {
                lodSelector.ApplyCombination(step.fixationIndex, step.keepRatioK, step.decimationCondition);
            }

            lastAppliedVariantKey = key;
        }

        private void EndCurrentStep()
        {
            if (currentStep == null) return;
            if (currentStep.startTimeRealtime <= 0) return;

            currentStep.endTimeRealtime = Time.realtimeSinceStartupAsDouble;

            if (logDisplaySequenceCsv)
                AppendStepToLog(currentStep);

            StepEnded?.Invoke(currentStep);
        }

        private void FinishSequence()
        {
            sequenceRunning = false;
            currentStepListIndex = -1;
            currentStepElapsed = 0f;
            lastAppliedVariantKey = string.Empty;
            waitingForConditionRating = false;
            pendingRatingStep = null;
            conditionRatingStartTimeRealtime = 0.0;
            ClearPendingFirstClickLod();
            ClearExplosionMaskedTransition(true);
            ClearPairwiseInterPresentationDelay(true);
            ClearPreStepMaskedTransition(true);
            HideConditionRatingCanvas();
            HideFixationCue();
            SequenceFinished?.Invoke();
            Debug.Log("CrowdingDisplayHandler: sequence finished.", this);
        }

        private void DisplayFixation(int fixationIndex)
        {
            Transform fixation = GetFixationPoint(fixationIndex);

            if (fixation == null)
                return;

            if (ShouldManageFixationPointMarkers())
                ShowOnlyFixationPoint(fixationIndex);

            if (fixationCue != null)
            {
                fixationCue.SetActive(true);

                if (parentCueToFixationPoint)
                    fixationCue.transform.SetParent(fixation, false);
                else
                    fixationCue.transform.SetParent(null, true);

                fixationCue.transform.position = fixation.position + fixationCueWorldOffset;
                fixationCue.transform.rotation = fixation.rotation;
            }
        }

        private bool ShouldManageFixationPointMarkers()
        {
            return showOnlyCurrentFixationPointObject &&
                   fixationMarkerVisibilityMode != CrowdingFixationMarkerVisibilityMode.DoNotManage;
        }

        private void CacheInitialFixationMarkerVisibility()
        {
            initialFixationGameObjectActiveStates.Clear();
            initialFixationRendererStates.Clear();

            IReadOnlyList<Transform> activeFixations = GetActiveFixationPoints();

            if (activeFixations == null)
                return;

            for (int i = 0; i < activeFixations.Count; i++)
            {
                Transform fixation = activeFixations[i];

                if (fixation == null)
                    continue;

                GameObject go = fixation.gameObject;

                if (!initialFixationGameObjectActiveStates.ContainsKey(go))
                    initialFixationGameObjectActiveStates.Add(go, go.activeSelf);

                Renderer[] renderers = fixation.GetComponentsInChildren<Renderer>(true);

                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer renderer = renderers[r];

                    if (renderer == null)
                        continue;

                    if (!initialFixationRendererStates.ContainsKey(renderer))
                        initialFixationRendererStates.Add(renderer, renderer.enabled);
                }
            }
        }

        private void ShowOnlyFixationPoint(int fixationIndex)
        {
            IReadOnlyList<Transform> activeFixations = GetActiveFixationPoints();

            if (activeFixations == null)
                return;

            for (int i = 0; i < activeFixations.Count; i++)
            {
                Transform fixation = activeFixations[i];

                if (fixation == null)
                    continue;

                SetFixationPointVisible(fixation, i == fixationIndex);
            }
        }

        private void SetAllFixationPointsVisible(bool visible)
        {
            IReadOnlyList<Transform> activeFixations = GetActiveFixationPoints();

            if (activeFixations == null)
                return;

            for (int i = 0; i < activeFixations.Count; i++)
            {
                if (activeFixations[i] != null)
                    SetFixationPointVisible(activeFixations[i], visible);
            }
        }

        private void SetFixationPointVisible(Transform fixation, bool visible)
        {
            if (fixation == null)
                return;

            if (fixationMarkerVisibilityMode == CrowdingFixationMarkerVisibilityMode.SetGameObjectActive)
            {
                fixation.gameObject.SetActive(visible);
                return;
            }

            if (fixationMarkerVisibilityMode == CrowdingFixationMarkerVisibilityMode.ToggleRenderersOnly)
            {
                Renderer[] renderers = fixation.GetComponentsInChildren<Renderer>(true);

                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null)
                        renderers[i].enabled = visible;
                }
            }
        }

        private void RestoreInitialFixationMarkerVisibility()
        {
            foreach (KeyValuePair<GameObject, bool> kvp in initialFixationGameObjectActiveStates)
            {
                if (kvp.Key != null)
                    kvp.Key.SetActive(kvp.Value);
            }

            foreach (KeyValuePair<Renderer, bool> kvp in initialFixationRendererStates)
            {
                if (kvp.Key != null)
                    kvp.Key.enabled = kvp.Value;
            }
        }

        private void HideFixationCue()
        {
            if (fixationCue != null)
                fixationCue.SetActive(false);

            if (!ShouldManageFixationPointMarkers())
                return;

            switch (fixationMarkerEndBehavior)
            {
                case CrowdingFixationMarkerEndBehavior.HideAll:
                    SetAllFixationPointsVisible(false);
                    break;

                case CrowdingFixationMarkerEndBehavior.RestoreInitialState:
                    RestoreInitialFixationMarkerVisibility();
                    break;

                case CrowdingFixationMarkerEndBehavior.KeepCurrentVisible:
                    if (currentStep != null)
                        ShowOnlyFixationPoint(currentStep.fixationIndex);
                    break;
            }
        }

        public string GetParticipantLogFolderFullPath()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;

            if (!useParticipantFolderForLogs)
            {
                string fallback = Path.GetDirectoryName(Path.Combine(projectRoot, logRelativePath));
                return string.IsNullOrEmpty(fallback) ? projectRoot : fallback;
            }

            string safeParticipant = SanitizePathSegment(ParticipantId);
            string folderName = string.IsNullOrWhiteSpace(participantFolderPrefix)
                ? safeParticipant
                : participantFolderPrefix + safeParticipant;

            return Path.Combine(projectRoot, participantLogsRootRelativePath, folderName);
        }

        public string GetParticipantLogFullPath(string fileName)
        {
            if (useParticipantFolderForLogs)
                return Path.Combine(GetParticipantLogFolderFullPath(), string.IsNullOrWhiteSpace(fileName) ? "log.csv" : fileName);

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, logRelativePath);
        }

        private string GetLogFullPath()
        {
            return GetParticipantLogFullPath(displaySequenceLogFileName);
        }

        private string GetConditionRatingLogFullPath()
        {
            string fileName = string.IsNullOrWhiteSpace(conditionRatingLogFileName)
                ? "condition_rating_log.csv"
                : conditionRatingLogFileName;

            return GetParticipantLogFullPath(fileName);
        }

        private void PrepareLogFile()
        {
            string path = GetLogFullPath();
            string dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!appendToExistingLog || !File.Exists(path))
            {
                using StreamWriter writer = new StreamWriter(path, false);
                writer.WriteLine("sceneName,participantId,sessionId,globalSessionIndex,globalStepIndex,combinationOrderIndex,fixationOrderIndex,fixationIndex,fixationName,conditionType,isReference,K,decimationCondition,variantKey,startTimeRealtime,endTimeRealtime,durationSeconds");
            }
        }

        private void AppendStepToLog(CrowdingDisplayStepInfo step)
        {
            string path = GetLogFullPath();
            double duration = Math.Max(0.0, step.endTimeRealtime - step.startTimeRealtime);

            using StreamWriter writer = new StreamWriter(path, true);
            writer.WriteLine(
                $"{Escape(step.sceneName)},{Escape(step.participantId)},{Escape(step.sessionId)},{step.globalSessionIndex},{step.globalStepIndex},{step.combinationOrderIndex},{step.fixationOrderIndex},{step.fixationIndex},{Escape(step.fixationName)},{step.conditionType},{step.isReference},{F4(step.keepRatioK)},{step.decimationCondition},{Escape(step.variantKey)},{F6(step.startTimeRealtime)},{F6(step.endTimeRealtime)},{F6(duration)}"
            );
        }

        private void PrepareConditionRatingLogFile()
        {
            string path = GetConditionRatingLogFullPath();
            string dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!appendToExistingLog || !File.Exists(path))
            {
                using StreamWriter writer = new StreamWriter(path, false);
                writer.WriteLine(
                    "sceneName," +
                    "experimentParadigm," +
                    "ratingTarget," +
                    "presentationOrder," +
                    "distortedStrategy," +
                    "distortedK," +
                    "distortedInitialTriangles," +
                    "distortedFinalTriangles," +
                    "distortedAchievedKeepRatio," +
                    "distortedMeshEntryCount," +
                    "rating," +
                    "ratingLabel," +
                    "ratingStartTimeRealtime," +
                    "ratingEndTimeRealtime," +
                    "ratingDurationSeconds"
                );
            }
        }

        private void AppendConditionRatingToLog(CrowdingDisplayStepInfo step, int rating, double ratingStartTime, double ratingEndTime)
        {
            if (step == null)
            {
                Debug.LogWarning("CrowdingDisplayHandler: cannot log rating because the pending step is null.", this);
                return;
            }

            string path = GetConditionRatingLogFullPath();
            string dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(path))
                PrepareConditionRatingLogFile();

            double duration = Math.Max(0.0, ratingEndTime - ratingStartTime);
            string presentationOrder = BuildPresentationOrderLabel(step);
            DistortedTriangleSummary triangles = GetDistortedTriangleSummary(step);

            using StreamWriter writer = new StreamWriter(path, true);
            writer.WriteLine(string.Join(",", new string[]
            {
                Escape(step.sceneName),
                step.experimentParadigm.ToString(),
                Escape(BuildRatingTargetLabel(step)),
                Escape(presentationOrder),
                step.comparisonDistortedDecimationCondition.ToString(),
                F4(step.comparisonDistortedKeepRatioK),
                triangles.initialTriangles.ToString(CultureInfo.InvariantCulture),
                triangles.finalTriangles.ToString(CultureInfo.InvariantCulture),
                F6(triangles.achievedKeepRatio),
                triangles.meshEntryCount.ToString(CultureInfo.InvariantCulture),
                rating.ToString(CultureInfo.InvariantCulture),
                Escape(GetDisturbanceRatingLabel(rating)),
                F6(ratingStartTime),
                F6(ratingEndTime),
                F6(duration)
            }));
        }

        private string GetPairwisePreferenceLogFullPath()
        {
            string fileName = string.IsNullOrWhiteSpace(pairwisePreferenceLogFileName)
                ? "uniform_vs_weighted_preference_log.csv"
                : pairwisePreferenceLogFileName;

            return GetParticipantLogFullPath(fileName);
        }

        private void PreparePairwisePreferenceLogFile()
        {
            string path = GetPairwisePreferenceLogFullPath();
            string dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!appendToExistingLog || !File.Exists(path))
            {
                using StreamWriter writer = new StreamWriter(path, false);
                writer.WriteLine("sceneName,participantId,sessionId,globalSessionIndex,experimentParadigm,comparisonPairIndex,fixationOrderIndex,fixationIndex,fixationName,K,presentationOrder,firstVariantKey,secondVariantKey,choice,chosenPresentation,chosenConditionType,chosenDecimationCondition,chosenVariantKey,responseStartTimeRealtime,responseEndTimeRealtime,responseDurationSeconds");
            }
        }

        private void AppendPairwisePreferenceToLog(CrowdingDisplayStepInfo step, CrowdingPairwiseChoice choice, double responseStartTime, double responseEndTime)
        {
            if (step == null)
            {
                Debug.LogWarning("CrowdingDisplayHandler: cannot log pairwise preference because the pending step is null.", this);
                return;
            }

            string path = GetPairwisePreferenceLogFullPath();
            string dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(path))
                PreparePairwisePreferenceLogFile();

            string chosenPresentation = "";
            CrowdingDisplayConditionType chosenConditionType = CrowdingDisplayConditionType.Reference;
            CrowdingDecimationCondition chosenDecimationCondition = CrowdingDecimationCondition.Uniform;
            string chosenVariantKey = "";

            bool firstIsUniform = step.comparisonFirstIsUniform;
            bool firstIsWeighted = step.comparisonFirstIsWeighted;
            bool secondIsUniform = !firstIsUniform && !firstIsWeighted;
            bool secondIsWeighted = !firstIsWeighted && !firstIsUniform;

            // In UniformVsWeightedPreference pairs, the first presentation is either Uniform or Weighted.
            // comparisonFirstIsUniform / comparisonFirstIsWeighted tell us which one was first.
            switch (choice)
            {
                case CrowdingPairwiseChoice.FirstPresentation:
                    chosenPresentation = step.comparisonFirstLabel;
                    chosenVariantKey = step.comparisonFirstVariantKey;
                    chosenConditionType = step.comparisonFirstIsUniform ? CrowdingDisplayConditionType.Uniform : CrowdingDisplayConditionType.Weighted;
                    chosenDecimationCondition = step.comparisonFirstIsUniform ? CrowdingDecimationCondition.Uniform : CrowdingDecimationCondition.WeightedCrowding;
                    break;

                case CrowdingPairwiseChoice.SecondPresentation:
                    chosenPresentation = step.comparisonSecondLabel;
                    chosenVariantKey = step.comparisonSecondVariantKey;
                    chosenConditionType = step.comparisonFirstIsUniform ? CrowdingDisplayConditionType.Weighted : CrowdingDisplayConditionType.Uniform;
                    chosenDecimationCondition = step.comparisonFirstIsUniform ? CrowdingDecimationCondition.WeightedCrowding : CrowdingDecimationCondition.Uniform;
                    break;

                case CrowdingPairwiseChoice.NoPreference:
                default:
                    chosenPresentation = "NoPreference";
                    chosenVariantKey = "NoPreference";
                    chosenConditionType = CrowdingDisplayConditionType.Reference;
                    chosenDecimationCondition = CrowdingDecimationCondition.Uniform;
                    break;
            }

            double duration = Math.Max(0.0, responseEndTime - responseStartTime);
            string presentationOrder = BuildPresentationOrderLabel(step);

            using StreamWriter writer = new StreamWriter(path, true);
            writer.WriteLine(
                $"{Escape(step.sceneName)},{Escape(step.participantId)},{Escape(step.sessionId)},{step.globalSessionIndex},{step.experimentParadigm},{step.comparisonPairIndex},{step.fixationOrderIndex},{step.fixationIndex},{Escape(step.fixationName)},{F4(step.comparisonDistortedKeepRatioK)},{Escape(presentationOrder)},{Escape(step.comparisonFirstVariantKey)},{Escape(step.comparisonSecondVariantKey)},{choice},{Escape(chosenPresentation)},{chosenConditionType},{chosenDecimationCondition},{Escape(chosenVariantKey)},{F6(responseStartTime)},{F6(responseEndTime)},{F6(duration)}"
            );
        }

        private struct DistortedTriangleSummary
        {
            public long initialTriangles;
            public long finalTriangles;
            public float achievedKeepRatio;
            public int meshEntryCount;
        }

        private DistortedTriangleSummary GetDistortedTriangleSummary(CrowdingDisplayStepInfo step)
        {
            DistortedTriangleSummary summary = new DistortedTriangleSummary
            {
                initialTriangles = -1,
                finalTriangles = -1,
                achievedKeepRatio = -1f,
                meshEntryCount = 0
            };

            if (step == null)
                return summary;

            string distortedVariantKey = step.comparisonDistortedVariantKey;

            // Fallback for non-pairwise/direct cases: if the current rated step itself
            // is distorted, use its own variant key.
            if (string.IsNullOrWhiteSpace(distortedVariantKey) && !step.isReference)
                distortedVariantKey = step.variantKey;

            if (string.IsNullOrWhiteSpace(distortedVariantKey) || distortedVariantKey == "reference")
                return summary;

            if (lodSelector == null || lodSelector.variantLibrary == null)
            {
                Debug.LogWarning(
                    "CrowdingDisplayHandler: cannot log distorted triangle count because the LOD selector or variant library is missing.",
                    this
                );
                return summary;
            }

            List<CrowdingMeshVariantEntry> entries = lodSelector.variantLibrary.GetEntriesForVariant(distortedVariantKey);

            if (entries == null || entries.Count == 0)
            {
                Debug.LogWarning(
                    "CrowdingDisplayHandler: no mesh entries found for distorted variant key '" + distortedVariantKey + "'.",
                    this
                );
                return summary;
            }

            long initialTotal = 0;
            long finalTotal = 0;
            int validEntries = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                CrowdingMeshVariantEntry entry = entries[i];

                if (entry == null)
                    continue;

                initialTotal += entry.initialTriangles;
                finalTotal += entry.finalTriangles;
                validEntries++;
            }

            if (validEntries <= 0)
                return summary;

            summary.initialTriangles = initialTotal;
            summary.finalTriangles = finalTotal;
            summary.achievedKeepRatio = initialTotal > 0 ? (float)finalTotal / (float)initialTotal : -1f;
            summary.meshEntryCount = validEntries;

            return summary;
        }

        private string BuildPresentationOrderLabel(CrowdingDisplayStepInfo step)
        {
            if (step == null || string.IsNullOrWhiteSpace(step.comparisonType))
                return "";

            if (step.comparisonType == "ReferenceVsDistortion")
                return step.comparisonFirstIsReference ? "ReferenceFirst" : "DistortionFirst";

            if (step.comparisonType == "UniformVsWeightedPreference")
                return step.comparisonFirstIsUniform ? "UniformFirst" : "WeightedFirst";

            return "";
        }

        private string GetDisturbanceRatingLabel(int rating)
        {
            switch (Mathf.Clamp(rating, 1, 5))
            {
                case 1: return disturbanceRating1Label;
                case 2: return disturbanceRating2Label;
                case 3: return disturbanceRating3Label;
                case 4: return disturbanceRating4Label;
                case 5: return disturbanceRating5Label;
                default: return "";
            }
        }

        private string BuildRatingTargetLabel(CrowdingDisplayStepInfo step)
        {
            if (step == null)
                return "unknown";

            if (experimentalParadigm == CrowdingExperimentParadigm.ReferenceVsDistortionRating)
            {
                return $"Reference vs {step.comparisonDistortedConditionType} K{Mathf.RoundToInt(step.comparisonDistortedKeepRatioK * 100f):000} at F{step.fixationIndex:00} ({step.fixationName})";
            }

            if (experimentalParadigm == CrowdingExperimentParadigm.UniformVsWeightedPreference)
            {
                return $"Uniform vs Redistributed K{Mathf.RoundToInt(step.comparisonDistortedKeepRatioK * 100f):000} at F{step.fixationIndex:00} ({step.fixationName})";
            }

            if (ratingGranularity == CrowdingRatingGranularity.EachFixationPoint)
                return $"Fixation F{step.fixationIndex:00} ({step.fixationName})";

            return "Condition";
        }

        private static string BuildConditionLabel(CrowdingDisplayStepInfo step)
        {
            if (step == null)
                return "unknown condition";

            if (step.isReference)
                return "Reference";

            return $"{step.conditionType} K{Mathf.RoundToInt(step.keepRatioK * 100f):000} ({step.decimationCondition})";
        }

        public static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "UnknownParticipant";

            string trimmed = value.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
                trimmed = trimmed.Replace(invalid[i], '_');

            trimmed = trimmed.Replace(' ', '_');
            return string.IsNullOrWhiteSpace(trimmed) ? "UnknownParticipant" : trimmed;
        }

        private static string F4(float value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string F6(double value)
        {
            return value.ToString("F6", CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            if (value == null) return "";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
