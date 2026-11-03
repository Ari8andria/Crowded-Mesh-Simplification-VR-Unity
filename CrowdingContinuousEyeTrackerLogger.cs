using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Continuous eye-tracking logger adapted for the current CrowdingDisplayHandler workflow.
    ///
    /// It creates one CSV file per experimental trial, where a trial is defined as the visual period
    /// between two question canvases. In pairwise paradigms, this means one file for the full pair
    /// (presentation A + presentation B), not one file per presentation.
    ///
    /// Attach this component to the same GameObject as CrowdingDisplayHandler, or assign the handler manually.
    /// No modification of CrowdingDisplayHandler is required: this script subscribes to StepStarted / StepEnded.
    /// </summary>
    [DisallowMultipleComponent]
    public class CrowdingContinuousEyeTrackerLogger : MonoBehaviour
    {
        [Header("Experiment link")]
        [Tooltip("Display handler driving the experiment. If empty, the script tries to find one in the scene.")]
        public CrowdingDisplayHandler displayHandler;

        [Tooltip("Use the participant folder created by CrowdingDisplayHandler.")]
        public bool useDisplayHandlerParticipantFolder = true;

        [Tooltip("Optional subfolder inside the participant folder. Leave empty to write directly inside the participant folder.")]
        public string eyeTrackingSubfolderName = "EyeTrackingTrials";

        [Header("Sampling")]
        [Tooltip("Target sampling frequency in Hz. For Vive Pro Eye / OpenXR, 90 Hz is usually appropriate.")]
        public float sampleRateHz = 90f;

        [Tooltip("If true, rows are written only when an eye rotation is available.")]
        public bool logOnlyWhenTracked = false;

        [Tooltip("If true, the StreamWriter is flushed after every sample. Safer, but slower.")]
        public bool autoFlushEverySample = false;

        [Header("References")]
        [Tooltip("Usually the XR camera / HMD transform. If empty, Camera.main is used.")]
        public Transform hmdTransform;

        [Header("Eye ray conversion")]
        [Tooltip("Use HMD/camera position as gaze ray origin instead of raw eye local position.")]
        public bool useHeadPositionAsRayOrigin = true;

        [Tooltip("Use the eye rotation workaround validated in the previous project.")]
        public bool useEyeRotationWorkaround = true;

        [Tooltip("Invert local Y after applying eye rotation.")]
        public bool invertLocalY = true;

        [Tooltip("Use Vector3.back instead of Vector3.forward for local gaze direction.")]
        public bool useVectorBackForLocalDirection = true;

        [Header("Raycast")]
        public float maxRayDistance = 100f;
        public LayerMask raycastMask = ~0;
        public bool includeTriggers = false;

        [Header("Trial segmentation")]
        [Tooltip("For pairwise paradigms, keep one file for the full pair: first presentation + second presentation.")]
        public bool groupPairwisePresentationsInOneTrial = true;

        [Tooltip("If false, samples during the blank interval between pairwise presentations are skipped.")]
        public bool logDuringInterPresentationBlank = false;

        [Tooltip("If true, direct-rating steps are logged as one file per fixation step. This is recommended when rating granularity is EachFixationPoint.")]
        public bool directRatingOneFilePerStep = true;

        [Header("File naming")]
        public string filePrefix = "eye";
        public bool includeTimestampInFileName = true;
        public bool includePairOrStepIndexInFileName = true;

        [Header("Debug ray")]
        public bool drawDebugRay = false;
        public float debugRayLength = 5f;
        public float debugRayWidth = 0.01f;
        public Material debugRayMaterial;
        public Color debugRayHitColor = Color.green;
        public Color debugRayNoHitColor = Color.red;
        public bool createLineRendererAutomatically = true;

        private LineRenderer debugLineRenderer;

#if ENABLE_INPUT_SYSTEM
        private InputAction eyePositionAction;
        private InputAction eyeRotationAction;
#endif

        private float sampleInterval;
        private float nextSampleTime;

        private bool isTrialLogging = false;
        private StreamWriter writer;
        private string currentFilePath = "";
        private string currentFileName = "";
        private int trialFileIndex = -1;

        private CrowdingDisplayStepInfo trialStartStep;
        private CrowdingDisplayStepInfo activeStep;
        private double trialStartRealtime = 0.0;
        private string currentTrialTargetName = "";

        public bool IsTrialLogging => isTrialLogging;
        public string CurrentFilePath => currentFilePath;
        public string CurrentFileName => currentFileName;

        private void Awake()
        {
            sampleInterval = 1f / Mathf.Max(1f, sampleRateHz);

            if (displayHandler == null)
                displayHandler = GetComponent<CrowdingDisplayHandler>();

            if (displayHandler == null)
                displayHandler = FindObjectOfType<CrowdingDisplayHandler>();

            if (hmdTransform == null && Camera.main != null)
                hmdTransform = Camera.main.transform;

            CreateEyeActions();

            if (createLineRendererAutomatically)
                SetupLineRenderer();
        }

        private void OnValidate()
        {
            sampleRateHz = Mathf.Max(1f, sampleRateHz);
            sampleInterval = 1f / Mathf.Max(1f, sampleRateHz);
            maxRayDistance = Mathf.Max(0.01f, maxRayDistance);
            debugRayLength = Mathf.Max(0.01f, debugRayLength);
            debugRayWidth = Mathf.Max(0.0001f, debugRayWidth);
        }

        private void OnEnable()
        {
            SubscribeToHandler();
            EnableEyeActions();

            if (debugLineRenderer != null)
                debugLineRenderer.enabled = drawDebugRay;
        }

        private void OnDisable()
        {
            UnsubscribeFromHandler();
            StopTrialLogging("OnDisable");
            DisableEyeActions();

            if (debugLineRenderer != null)
                debugLineRenderer.enabled = false;
        }

        private void Update()
        {
            if (!isTrialLogging)
            {
                if (debugLineRenderer != null)
                    debugLineRenderer.enabled = false;
                return;
            }

            if (activeStep == null && !logDuringInterPresentationBlank)
            {
                if (debugLineRenderer != null)
                    debugLineRenderer.enabled = false;
                return;
            }

            if (Time.unscaledTime >= nextSampleTime)
            {
                nextSampleTime = Time.unscaledTime + sampleInterval;
                LogSample();
            }
        }

        private void SubscribeToHandler()
        {
            if (displayHandler == null)
                return;

            displayHandler.StepStarted -= OnDisplayStepStarted;
            displayHandler.StepEnded -= OnDisplayStepEnded;
            displayHandler.SequenceFinished -= OnSequenceFinished;

            displayHandler.StepStarted += OnDisplayStepStarted;
            displayHandler.StepEnded += OnDisplayStepEnded;
            displayHandler.SequenceFinished += OnSequenceFinished;
        }

        private void UnsubscribeFromHandler()
        {
            if (displayHandler == null)
                return;

            displayHandler.StepStarted -= OnDisplayStepStarted;
            displayHandler.StepEnded -= OnDisplayStepEnded;
            displayHandler.SequenceFinished -= OnSequenceFinished;
        }

        private void OnDisplayStepStarted(CrowdingDisplayStepInfo step)
        {
            if (step == null)
                return;

            if (ShouldStartNewTrial(step))
                StartTrialLogging(step);

            activeStep = step;
            nextSampleTime = Time.unscaledTime;
        }

        private void OnDisplayStepEnded(CrowdingDisplayStepInfo endedStep)
        {
            if (endedStep == null)
                return;

            activeStep = endedStep;

            if (ShouldEndTrialAfterStep(endedStep))
            {
                StopTrialLogging("StepEnded");
                return;
            }

            if (!logDuringInterPresentationBlank)
                activeStep = null;
        }

        private void OnSequenceFinished()
        {
            StopTrialLogging("SequenceFinished");
        }

        private bool IsPairwiseStep(CrowdingDisplayStepInfo step)
        {
            return step != null &&
                   step.comparisonPairIndex >= 0 &&
                   (step.experimentParadigm == CrowdingExperimentParadigm.ReferenceVsDistortionRating ||
                    step.experimentParadigm == CrowdingExperimentParadigm.UniformVsWeightedPreference);
        }

        private bool ShouldStartNewTrial(CrowdingDisplayStepInfo step)
        {
            if (step == null)
                return false;

            if (IsPairwiseStep(step) && groupPairwisePresentationsInOneTrial)
            {
                if (!isTrialLogging)
                    return true;

                return trialStartStep == null ||
                       step.comparisonPairIndex != trialStartStep.comparisonPairIndex ||
                       step.sceneName != trialStartStep.sceneName;
            }

            return directRatingOneFilePerStep || !isTrialLogging;
        }

        private bool ShouldEndTrialAfterStep(CrowdingDisplayStepInfo step)
        {
            if (step == null)
                return false;

            if (IsPairwiseStep(step) && groupPairwisePresentationsInOneTrial)
                return step.comparisonPresentationIndex >= 1;

            return directRatingOneFilePerStep;
        }

        private void StartTrialLogging(CrowdingDisplayStepInfo step)
        {
            StopTrialLogging("StartNewTrial");

            trialStartStep = step;
            activeStep = step;
            trialStartRealtime = Time.realtimeSinceStartupAsDouble;
            currentTrialTargetName = ResolveTrialTargetName(step);
            trialFileIndex++;

            string folder = GetOutputFolderPath(step);
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            currentFileName = BuildTrialFileName(step, currentTrialTargetName);
            currentFilePath = Path.Combine(folder, currentFileName);

            writer = new StreamWriter(currentFilePath, false, Encoding.UTF8);
            WriteHeader();

            isTrialLogging = true;
            nextSampleTime = Time.unscaledTime;

            Debug.Log("[CrowdingEyeTracker] Trial logging started: " + currentFilePath, this);
        }

        public void StopTrialLogging(string reason = "Manual")
        {
            if (!isTrialLogging && writer == null)
                return;

            isTrialLogging = false;
            activeStep = null;
            trialStartStep = null;
            currentTrialTargetName = "";

            if (writer != null)
            {
                writer.Flush();
                writer.Close();
                writer = null;
            }

            if (debugLineRenderer != null)
                debugLineRenderer.enabled = false;

            Debug.Log("[CrowdingEyeTracker] Trial logging stopped. Reason=" + reason, this);
        }

        private string GetOutputFolderPath(CrowdingDisplayStepInfo step)
        {
            string baseFolder = null;

            if (useDisplayHandlerParticipantFolder && displayHandler != null)
                baseFolder = displayHandler.GetParticipantLogFolderFullPath();

            if (string.IsNullOrWhiteSpace(baseFolder))
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                string participant = step != null ? step.participantId : "UnknownParticipant";
                baseFolder = Path.Combine(projectRoot, "CrowdingExports", "Participant_" + SanitizePathSegment(participant));
            }

            if (!string.IsNullOrWhiteSpace(eyeTrackingSubfolderName))
                baseFolder = Path.Combine(baseFolder, SanitizePathSegment(eyeTrackingSubfolderName));

            return baseFolder;
        }

        private string BuildTrialFileName(CrowdingDisplayStepInfo step, string targetName)
        {
            string scene = step != null && !string.IsNullOrWhiteSpace(step.sceneName)
                ? step.sceneName
                : SceneManager.GetActiveScene().name;

            StringBuilder sb = new StringBuilder();
            sb.Append(SanitizePathSegment(filePrefix));
            sb.Append("__");
            sb.Append(SanitizePathSegment(scene));
            sb.Append("__");
            sb.Append(SanitizePathSegment(targetName));

            if (includePairOrStepIndexInFileName && step != null)
            {
                if (IsPairwiseStep(step))
                {
                    sb.Append("__pair");
                    sb.Append(step.comparisonPairIndex.ToString("000", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append("__step");
                    sb.Append(step.globalStepIndex.ToString("000", CultureInfo.InvariantCulture));
                }
            }

            string trialVariantKey = "";
            if (step != null)
            {
                trialVariantKey = IsPairwiseStep(step) && !string.IsNullOrWhiteSpace(step.comparisonDistortedVariantKey)
                    ? step.comparisonDistortedVariantKey
                    : step.variantKey;
            }

            if (!string.IsNullOrWhiteSpace(trialVariantKey))
            {
                sb.Append("__");
                sb.Append(SanitizePathSegment(trialVariantKey));
            }

            if (includeTimestampInFileName)
            {
                sb.Append("__");
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture));
            }

            sb.Append(".csv");
            return sb.ToString();
        }

        private string ResolveTrialTargetName(CrowdingDisplayStepInfo step)
        {
            if (step == null)
                return "UnknownTarget";

            // In the current experiment, the displayed target/fixation object name is stored as fixationName.
            if (!string.IsNullOrWhiteSpace(step.fixationName))
                return step.fixationName;

            if (!string.IsNullOrWhiteSpace(step.variantKey))
                return step.variantKey;

            return "F" + step.fixationIndex.ToString("00", CultureInfo.InvariantCulture);
        }

        private Transform ResolveCurrentTargetTransform(CrowdingDisplayStepInfo step)
        {
            if (step == null || displayHandler == null)
                return null;

            if (displayHandler.useControllerFixationPoints &&
                displayHandler.pipelineController != null &&
                displayHandler.pipelineController.fixationPoints != null &&
                step.fixationIndex >= 0 &&
                step.fixationIndex < displayHandler.pipelineController.fixationPoints.Count)
            {
                return displayHandler.pipelineController.fixationPoints[step.fixationIndex];
            }

            if (displayHandler.fixationPoints != null &&
                step.fixationIndex >= 0 &&
                step.fixationIndex < displayHandler.fixationPoints.Count)
            {
                return displayHandler.fixationPoints[step.fixationIndex];
            }

            return null;
        }

        private void WriteHeader()
        {
            if (writer == null)
                return;

            writer.WriteLine(
                "system_time_utc," +
                "unity_time,unscaled_time,realtime_since_startup,trial_elapsed_seconds,frame," +
                "trial_file_index,trial_start_realtime," +
                "participant_id,scene_name,session_id,global_session_index," +
                "experiment_paradigm,comparison_type,comparison_pair_index,comparison_presentation_index,presentation_label," +
                "displayed_version,displayed_is_reference,displayed_is_distorted,displayed_condition_label,displayed_variant_key," +
                "comparison_first_variant_key,comparison_second_variant_key,comparison_distorted_condition_type,comparison_distorted_k,comparison_distorted_decimation_condition,comparison_distorted_variant_key," +
                "global_step_index,combination_order_index,fixation_order_index,fixation_index,fixation_name," +
                "condition_type,is_reference,k,decimation_condition,variant_key," +
                "eye_is_tracked,eye_tracking_state," +
                "raw_eye_local_pos_x,raw_eye_local_pos_y,raw_eye_local_pos_z," +
                "raw_eye_local_rot_x,raw_eye_local_rot_y,raw_eye_local_rot_z,raw_eye_local_rot_w," +
                "gaze_origin_x,gaze_origin_y,gaze_origin_z," +
                "gaze_dir_x,gaze_dir_y,gaze_dir_z," +
                "gaze_world_yaw_deg,gaze_world_pitch_deg,gaze_world_roll_deg," +
                "gaze_head_yaw_deg,gaze_head_pitch_deg,gaze_head_roll_deg," +
                "hmd_pos_x,hmd_pos_y,hmd_pos_z," +
                "hmd_rot_x,hmd_rot_y,hmd_rot_z,hmd_rot_w," +
                "hmd_yaw_deg,hmd_pitch_deg,hmd_roll_deg," +
                "target_world_x,target_world_y,target_world_z,target_distance_from_eye,target_angle_deg," +
                "hit,hit_object_name,hit_object_tag,hit_object_layer,hit_root_name,hit_distance," +
                "hit_point_x,hit_point_y,hit_point_z,hit_normal_x,hit_normal_y,hit_normal_z," +
                "hit_group_id,hit_object_id,hit_semantic_name,hit_role,hit_last_keep_ratio"
            );

            writer.Flush();
        }

        private void LogSample()
        {
            CrowdingDisplayStepInfo step = activeStep != null ? activeStep : trialStartStep;
            if (step == null || writer == null)
                return;

            Vector3 rawEyeLocalPos;
            Quaternion rawEyeLocalRot;
            bool eyeTracked;
            string trackingStateString;

            ReadEyePose(out rawEyeLocalPos, out rawEyeLocalRot, out eyeTracked, out trackingStateString);

            if (logOnlyWhenTracked && !eyeTracked)
                return;

            if (hmdTransform == null)
            {
                if (Camera.main != null)
                    hmdTransform = Camera.main.transform;

                if (hmdTransform == null)
                {
                    Debug.LogWarning("[CrowdingEyeTracker] hmdTransform is not assigned.", this);
                    return;
                }
            }

            Vector3 gazeOrigin;
            Vector3 gazeDir;
            ComputeGazeRay(rawEyeLocalPos, rawEyeLocalRot, out gazeOrigin, out gazeDir);

            Vector3 hmdPos = hmdTransform.position;
            Quaternion hmdRot = hmdTransform.rotation;
            Vector3 hmdEuler = hmdRot.eulerAngles;

            float hmdYawDeg = NormalizeAngle180(hmdEuler.y);
            float hmdPitchDeg = NormalizeAngle180(hmdEuler.x);
            float hmdRollDeg = NormalizeAngle180(hmdEuler.z);

            Quaternion gazeWorldRotation = Quaternion.LookRotation(gazeDir, Vector3.up);
            Vector3 gazeWorldEuler = gazeWorldRotation.eulerAngles;

            float gazeWorldYawDeg = NormalizeAngle180(gazeWorldEuler.y);
            float gazeWorldPitchDeg = NormalizeAngle180(gazeWorldEuler.x);
            float gazeWorldRollDeg = NormalizeAngle180(gazeWorldEuler.z);

            Vector3 gazeDirHead = Quaternion.Inverse(hmdRot) * gazeDir;
            Quaternion gazeHeadRotation = Quaternion.LookRotation(gazeDirHead, Vector3.up);
            Vector3 gazeHeadEuler = gazeHeadRotation.eulerAngles;

            float gazeHeadYawDeg = NormalizeAngle180(gazeHeadEuler.y);
            float gazeHeadPitchDeg = NormalizeAngle180(gazeHeadEuler.x);
            float gazeHeadRollDeg = NormalizeAngle180(gazeHeadEuler.z);

            Transform targetTransform = ResolveCurrentTargetTransform(step);
            Vector3 targetWorldPos = Vector3.zero;
            float targetDistanceFromEye = -1f;
            float targetAngleDeg = -1f;

            if (targetTransform != null)
            {
                Collider targetCollider = targetTransform.GetComponentInChildren<Collider>();
                targetWorldPos = targetCollider != null ? targetCollider.bounds.center : targetTransform.position;

                Vector3 toTarget = targetWorldPos - gazeOrigin;
                targetDistanceFromEye = toTarget.magnitude;
                if (targetDistanceFromEye > 1e-6f)
                    targetAngleDeg = Vector3.Angle(gazeDir, toTarget / targetDistanceFromEye);
            }

            QueryTriggerInteraction qti = includeTriggers
                ? QueryTriggerInteraction.Collide
                : QueryTriggerInteraction.Ignore;

            Ray ray = new Ray(gazeOrigin, gazeDir);
            bool hitSomething = Physics.Raycast(ray, out RaycastHit hit, maxRayDistance, raycastMask, qti);

            string hitObjectName = "";
            string hitObjectTag = "";
            string hitObjectLayer = "";
            string hitRootName = "";
            float hitDistance = -1f;
            Vector3 hitPoint = Vector3.zero;
            Vector3 hitNormal = Vector3.zero;
            int hitGroupId = -1;
            string hitObjectId = "";
            string hitSemanticName = "";
            string hitRole = "";
            float hitLastKeepRatio = -1f;

            if (hitSomething)
            {
                GameObject hitGo = hit.collider.gameObject;
                hitObjectName = hitGo.name;
                hitObjectTag = hitGo.tag;
                hitObjectLayer = LayerMask.LayerToName(hitGo.layer);
                hitRootName = hitGo.transform.root != null ? hitGo.transform.root.name : "";
                hitDistance = hit.distance;
                hitPoint = hit.point;
                hitNormal = hit.normal;

                CrowdingGroupMember member = hit.collider.GetComponentInParent<CrowdingGroupMember>();
                if (member != null)
                {
                    hitGroupId = member.groupId;
                    hitObjectId = member.objectId;
                    hitSemanticName = member.semanticName;
                    hitRole = member.baseRole.ToString();
                    hitLastKeepRatio = member.lastKeepRatio;
                }
            }

            UpdateVisibleRay(gazeOrigin, gazeDir, hitSomething, hitDistance);

            double realtime = Time.realtimeSinceStartupAsDouble;
            double trialElapsed = Math.Max(0.0, realtime - trialStartRealtime);

            string presentationLabel = "";
            if (step.comparisonPresentationIndex == 0)
                presentationLabel = step.comparisonFirstLabel;
            else if (step.comparisonPresentationIndex == 1)
                presentationLabel = step.comparisonSecondLabel;

            string displayedVersion = ResolveDisplayedVersion(step);
            string displayedConditionLabel = ResolveDisplayedConditionLabel(step);
            string displayedVariantKey = string.IsNullOrWhiteSpace(step.variantKey) ? "reference" : step.variantKey;
            bool displayedIsDistorted = IsDisplayedDistorted(step);

            string line = string.Join(",", new string[]
            {
                Escape(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
                Time.time.ToString("F6", CultureInfo.InvariantCulture),
                Time.unscaledTime.ToString("F6", CultureInfo.InvariantCulture),
                realtime.ToString("F6", CultureInfo.InvariantCulture),
                trialElapsed.ToString("F6", CultureInfo.InvariantCulture),
                Time.frameCount.ToString(CultureInfo.InvariantCulture),

                trialFileIndex.ToString(CultureInfo.InvariantCulture),
                trialStartRealtime.ToString("F6", CultureInfo.InvariantCulture),

                Escape(step.participantId),
                Escape(step.sceneName),
                Escape(step.sessionId),
                step.globalSessionIndex.ToString(CultureInfo.InvariantCulture),

                step.experimentParadigm.ToString(),
                Escape(step.comparisonType),
                step.comparisonPairIndex.ToString(CultureInfo.InvariantCulture),
                step.comparisonPresentationIndex.ToString(CultureInfo.InvariantCulture),
                Escape(presentationLabel),

                Escape(displayedVersion),
                step.isReference ? "1" : "0",
                displayedIsDistorted ? "1" : "0",
                Escape(displayedConditionLabel),
                Escape(displayedVariantKey),

                Escape(step.comparisonFirstVariantKey),
                Escape(step.comparisonSecondVariantKey),
                step.comparisonDistortedConditionType.ToString(),
                step.comparisonDistortedKeepRatioK.ToString("F4", CultureInfo.InvariantCulture),
                step.comparisonDistortedDecimationCondition.ToString(),
                Escape(step.comparisonDistortedVariantKey),

                step.globalStepIndex.ToString(CultureInfo.InvariantCulture),
                step.combinationOrderIndex.ToString(CultureInfo.InvariantCulture),
                step.fixationOrderIndex.ToString(CultureInfo.InvariantCulture),
                step.fixationIndex.ToString(CultureInfo.InvariantCulture),
                Escape(step.fixationName),

                step.conditionType.ToString(),
                step.isReference ? "1" : "0",
                step.keepRatioK.ToString("F4", CultureInfo.InvariantCulture),
                step.decimationCondition.ToString(),
                Escape(step.variantKey),

                eyeTracked ? "1" : "0",
                Escape(trackingStateString),

                rawEyeLocalPos.x.ToString("F6", CultureInfo.InvariantCulture),
                rawEyeLocalPos.y.ToString("F6", CultureInfo.InvariantCulture),
                rawEyeLocalPos.z.ToString("F6", CultureInfo.InvariantCulture),

                rawEyeLocalRot.x.ToString("F6", CultureInfo.InvariantCulture),
                rawEyeLocalRot.y.ToString("F6", CultureInfo.InvariantCulture),
                rawEyeLocalRot.z.ToString("F6", CultureInfo.InvariantCulture),
                rawEyeLocalRot.w.ToString("F6", CultureInfo.InvariantCulture),

                gazeOrigin.x.ToString("F6", CultureInfo.InvariantCulture),
                gazeOrigin.y.ToString("F6", CultureInfo.InvariantCulture),
                gazeOrigin.z.ToString("F6", CultureInfo.InvariantCulture),

                gazeDir.x.ToString("F6", CultureInfo.InvariantCulture),
                gazeDir.y.ToString("F6", CultureInfo.InvariantCulture),
                gazeDir.z.ToString("F6", CultureInfo.InvariantCulture),

                gazeWorldYawDeg.ToString("F6", CultureInfo.InvariantCulture),
                gazeWorldPitchDeg.ToString("F6", CultureInfo.InvariantCulture),
                gazeWorldRollDeg.ToString("F6", CultureInfo.InvariantCulture),

                gazeHeadYawDeg.ToString("F6", CultureInfo.InvariantCulture),
                gazeHeadPitchDeg.ToString("F6", CultureInfo.InvariantCulture),
                gazeHeadRollDeg.ToString("F6", CultureInfo.InvariantCulture),

                hmdPos.x.ToString("F6", CultureInfo.InvariantCulture),
                hmdPos.y.ToString("F6", CultureInfo.InvariantCulture),
                hmdPos.z.ToString("F6", CultureInfo.InvariantCulture),

                hmdRot.x.ToString("F6", CultureInfo.InvariantCulture),
                hmdRot.y.ToString("F6", CultureInfo.InvariantCulture),
                hmdRot.z.ToString("F6", CultureInfo.InvariantCulture),
                hmdRot.w.ToString("F6", CultureInfo.InvariantCulture),
                hmdYawDeg.ToString("F6", CultureInfo.InvariantCulture),
                hmdPitchDeg.ToString("F6", CultureInfo.InvariantCulture),
                hmdRollDeg.ToString("F6", CultureInfo.InvariantCulture),

                targetWorldPos.x.ToString("F6", CultureInfo.InvariantCulture),
                targetWorldPos.y.ToString("F6", CultureInfo.InvariantCulture),
                targetWorldPos.z.ToString("F6", CultureInfo.InvariantCulture),
                targetDistanceFromEye.ToString("F6", CultureInfo.InvariantCulture),
                targetAngleDeg.ToString("F6", CultureInfo.InvariantCulture),

                hitSomething ? "1" : "0",
                Escape(hitObjectName),
                Escape(hitObjectTag),
                Escape(hitObjectLayer),
                Escape(hitRootName),
                hitDistance.ToString("F6", CultureInfo.InvariantCulture),

                hitPoint.x.ToString("F6", CultureInfo.InvariantCulture),
                hitPoint.y.ToString("F6", CultureInfo.InvariantCulture),
                hitPoint.z.ToString("F6", CultureInfo.InvariantCulture),
                hitNormal.x.ToString("F6", CultureInfo.InvariantCulture),
                hitNormal.y.ToString("F6", CultureInfo.InvariantCulture),
                hitNormal.z.ToString("F6", CultureInfo.InvariantCulture),

                hitGroupId.ToString(CultureInfo.InvariantCulture),
                Escape(hitObjectId),
                Escape(hitSemanticName),
                Escape(hitRole),
                hitLastKeepRatio.ToString("F6", CultureInfo.InvariantCulture)
            });

            writer.WriteLine(line);

            if (autoFlushEverySample)
                writer.Flush();
        }

        private static string ResolveDisplayedVersion(CrowdingDisplayStepInfo step)
        {
            if (step == null)
                return "Unknown";

            // In the Reference-vs-Distortion paradigm, this is the explicit role of
            // the currently displayed presentation inside the pair.
            if (step.experimentParadigm == CrowdingExperimentParadigm.ReferenceVsDistortionRating)
                return step.isReference ? "Reference" : "Distorted";

            // In the preference paradigm, the displayed version is not Reference/Distorted
            // but Uniform/Weighted. Keeping the exact label avoids ambiguity.
            if (step.experimentParadigm == CrowdingExperimentParadigm.UniformVsWeightedPreference)
                return step.conditionType.ToString();

            if (step.isReference)
                return "Reference";

            return step.conditionType.ToString();
        }

        private static bool IsDisplayedDistorted(CrowdingDisplayStepInfo step)
        {
            if (step == null)
                return false;

            // Only the Reference-vs-Distortion paradigm has a strict Reference vs Distorted role.
            if (step.experimentParadigm == CrowdingExperimentParadigm.ReferenceVsDistortionRating)
                return !step.isReference;

            return false;
        }

        private static string ResolveDisplayedConditionLabel(CrowdingDisplayStepInfo step)
        {
            if (step == null)
                return "Unknown";

            if (step.isReference)
                return "Reference";

            int kPercent = Mathf.RoundToInt(step.keepRatioK * 100f);
            return step.conditionType.ToString() + "_K" + kPercent.ToString("000", CultureInfo.InvariantCulture) + "_" + step.decimationCondition.ToString();
        }

        private void CreateEyeActions()
        {
#if ENABLE_INPUT_SYSTEM
            eyePositionAction = new InputAction(
                name: "EyePosition",
                type: InputActionType.Value,
                binding: "<EyeGaze>/pose/position"
            );

            eyeRotationAction = new InputAction(
                name: "EyeRotation",
                type: InputActionType.Value,
                binding: "<EyeGaze>/pose/rotation"
            );
#endif
        }

        private void EnableEyeActions()
        {
#if ENABLE_INPUT_SYSTEM
            eyePositionAction?.Enable();
            eyeRotationAction?.Enable();
#else
            Debug.LogWarning("[CrowdingEyeTracker] ENABLE_INPUT_SYSTEM is not defined. Eye gaze actions will not be available.", this);
#endif
        }

        private void DisableEyeActions()
        {
#if ENABLE_INPUT_SYSTEM
            eyePositionAction?.Disable();
            eyeRotationAction?.Disable();
#endif
        }

        private void ReadEyePose(out Vector3 rawEyeLocalPos, out Quaternion rawEyeLocalRot, out bool eyeTracked, out string trackingStateString)
        {
#if ENABLE_INPUT_SYSTEM
            rawEyeLocalPos = eyePositionAction != null ? eyePositionAction.ReadValue<Vector3>() : Vector3.zero;
            rawEyeLocalRot = eyeRotationAction != null ? eyeRotationAction.ReadValue<Quaternion>() : Quaternion.identity;
            eyeTracked = rawEyeLocalRot != Quaternion.identity;
            trackingStateString = eyeTracked ? "RotationAvailable" : "RotationIdentity";
#else
            rawEyeLocalPos = Vector3.zero;
            rawEyeLocalRot = Quaternion.identity;
            eyeTracked = false;
            trackingStateString = "InputSystemDisabled";
#endif
        }

        private void ComputeGazeRay(Vector3 rawEyeLocalPos, Quaternion rawEyeLocalRot, out Vector3 gazeOrigin, out Vector3 gazeDir)
        {
            if (useEyeRotationWorkaround)
            {
                gazeOrigin = useHeadPositionAsRayOrigin ? hmdTransform.position : rawEyeLocalPos;

                Vector3 localDirBase = useVectorBackForLocalDirection ? Vector3.back : Vector3.forward;
                Vector3 localDir = rawEyeLocalRot * localDirBase;

                if (invertLocalY)
                    localDir = new Vector3(localDir.x, -localDir.y, localDir.z);

                gazeDir = hmdTransform.TransformDirection(localDir).normalized;
            }
            else
            {
                Transform reference = hmdTransform.parent != null ? hmdTransform.parent : hmdTransform;

                gazeOrigin = useHeadPositionAsRayOrigin
                    ? hmdTransform.position
                    : reference.TransformPoint(rawEyeLocalPos);

                gazeDir = (reference.rotation * rawEyeLocalRot * Vector3.forward).normalized;
            }
        }

        private void SetupLineRenderer()
        {
            debugLineRenderer = GetComponent<LineRenderer>();

            if (debugLineRenderer == null)
                debugLineRenderer = gameObject.AddComponent<LineRenderer>();

            debugLineRenderer.positionCount = 2;
            debugLineRenderer.startWidth = debugRayWidth;
            debugLineRenderer.endWidth = debugRayWidth;
            debugLineRenderer.useWorldSpace = true;
            debugLineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            debugLineRenderer.receiveShadows = false;
            debugLineRenderer.enabled = false;

            if (debugRayMaterial != null)
            {
                debugLineRenderer.material = debugRayMaterial;
            }
            else
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader != null)
                    debugLineRenderer.material = new Material(shader);
            }

            debugLineRenderer.startColor = debugRayNoHitColor;
            debugLineRenderer.endColor = debugRayNoHitColor;
        }

        private void UpdateVisibleRay(Vector3 origin, Vector3 direction, bool hitSomething, float hitDistance)
        {
            if (!drawDebugRay)
            {
                if (debugLineRenderer != null)
                    debugLineRenderer.enabled = false;
                return;
            }

            if (debugLineRenderer == null)
                SetupLineRenderer();

            debugLineRenderer.enabled = true;
            debugLineRenderer.startWidth = debugRayWidth;
            debugLineRenderer.endWidth = debugRayWidth;

            float rayLength = hitSomething ? hitDistance : debugRayLength;
            Vector3 endPoint = origin + direction.normalized * rayLength;

            debugLineRenderer.SetPosition(0, origin);
            debugLineRenderer.SetPosition(1, endPoint);

            Color rayColor = hitSomething ? debugRayHitColor : debugRayNoHitColor;
            debugLineRenderer.startColor = rayColor;
            debugLineRenderer.endColor = rayColor;
        }

        private static float NormalizeAngle180(float angleDeg)
        {
            angleDeg %= 360f;
            if (angleDeg > 180f)
                angleDeg -= 360f;
            return angleDeg;
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";

            return value;
        }

        private static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "NA";

            string result = value.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
                result = result.Replace(invalid[i], '_');

            result = result.Replace(' ', '_');
            return string.IsNullOrWhiteSpace(result) ? "NA" : result;
        }
    }
}
