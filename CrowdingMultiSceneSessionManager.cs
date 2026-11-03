using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CrowdingAwareLOD
{
    [Serializable]
    public class CrowdingSceneConditionBlock
    {
        public int globalSessionIndex = -1;
        public string sessionId = "";
        public string sceneName = "";
        public CrowdingDisplayConditionType conditionType = CrowdingDisplayConditionType.Reference;
        public float keepRatioK = 1.0f;
        public CrowdingDecimationCondition decimationCondition = CrowdingDecimationCondition.Uniform;

        public bool IsReference => conditionType == CrowdingDisplayConditionType.Reference;

        public string BuildVariantKey(bool useGlobalSceneVariants)
        {
            if (IsReference)
                return "reference";

            if (conditionType == CrowdingDisplayConditionType.Uniform ||
                decimationCondition == CrowdingDecimationCondition.Uniform)
            {
                return CrowdingVariantKey.BuildGlobal(keepRatioK, CrowdingDecimationCondition.Uniform);
            }

            if (useGlobalSceneVariants)
                return CrowdingVariantKey.BuildGlobal(keepRatioK, decimationCondition);

            // The exact per-fixation key is built by the local DisplayHandler.
            // This label is only used for the multi-scene session log.
            return $"per_fixation_K{Mathf.RoundToInt(keepRatioK * 100f):000}_{decimationCondition}";
        }

        public string ConditionLabel
        {
            get
            {
                if (IsReference)
                    return "Reference";

                return $"{conditionType}_K{Mathf.RoundToInt(keepRatioK * 100f):000}_{decimationCondition}";
            }
        }
    }

    [DisallowMultipleComponent]
    public class CrowdingMultiSceneSessionManager : MonoBehaviour
    {
        [Header("Scenes to randomize")]
        [Tooltip("Scene names must be added in File > Build Settings > Scenes In Build.")]
        public List<string> sceneNames = new List<string>();

        [Header("Scene list auto-fill")]
        [Tooltip("If true, the manager can fill Scene Names automatically from File > Build Settings > Scenes In Build, so you do not have to type scene names by hand.")]
        public bool useEnabledBuildSettingsScenes = true;

        [Tooltip("If true, Scene Names is refreshed automatically when Start Session is called.")]
        public bool refreshSceneNamesOnStart = true;

        [Tooltip("If true, the currently active scene is excluded from the generated list. Useful when the manager is placed in a launcher scene that should not be part of the experiment.")]
        public bool excludeCurrentSceneFromAutoList = true;

        [Tooltip("If true, auto-fill only happens when Scene Names is empty. If false, the list is overwritten with the enabled Build Settings scenes.")]
        public bool onlyAutoFillWhenSceneNamesEmpty = false;

        [Header("Participant / session")]
        public string participantId = "P001";
        public string sessionId = "";
        public bool autoGenerateSessionIdIfEmpty = true;
        public bool dontDestroyOnLoad = true;

        [Header("Conditions to run in every scene")]
        public bool includeReferenceCondition = true;
        public bool includeUniformConditions = true;
        public bool includeWeightedConditions = true;
        public List<float> keepRatiosK = new List<float> { 0.75f, 0.5f, 0.25f };

        [Header("Randomization")]
        public bool randomizeSceneConditionOrder = true;
        public bool deterministicRandom = true;
        public int randomSeed = 12345;

        [Header("Local handler control")]
        [Tooltip("If true, the manager overwrites the participantId of the CrowdingDisplayHandler found in each loaded scene.")]
        public bool overrideHandlerParticipantId = true;

        [Tooltip("Recommended. Each scene should not auto-start by itself when controlled by this manager.")]
        public bool forceHandlerStartOnPlayFalse = true;

        [Tooltip("If true, the manager stops a handler that started accidentally before launching the assigned single condition.")]
        public bool stopHandlerIfAlreadyRunning = true;

        [Tooltip("Useful if the local scene needs one frame after loading to initialize objects before the sequence starts.")]
        public bool waitOneFrameAfterSceneLoad = true;

        [Header("Session log")]
        public bool logSessionOrderCsv = true;
        public string sessionLogsRootRelativePath = "CrowdingExports";
        public string sessionLogFileName = "multi_scene_session_order.csv";
        public bool appendToExistingSessionLog = false;

        [Header("Start")]
        public bool startOnPlay = true;

        [Header("Runtime state")]
        [SerializeField] private bool sessionRunning = false;
        [SerializeField] private int currentGlobalSessionIndex = -1;
        [SerializeField] private string currentSceneName = "";
        [SerializeField] private string currentConditionLabel = "";

        private readonly List<CrowdingSceneConditionBlock> sessionBlocks = new List<CrowdingSceneConditionBlock>();
        private CrowdingDisplayHandler activeHandler;
        private bool activeHandlerFinished;
        private System.Random rng;

        public bool IsRunning => sessionRunning;
        public IReadOnlyList<CrowdingSceneConditionBlock> SessionBlocks => sessionBlocks;
        public int CurrentGlobalSessionIndex => currentGlobalSessionIndex;

        public event Action<CrowdingSceneConditionBlock> BlockStarted;
        public event Action<CrowdingSceneConditionBlock> BlockFinished;
        public event Action SessionStarted;
        public event Action SessionFinished;

        private void Start()
        {
            if (startOnPlay)
                StartSession();
        }

        [ContextMenu("Multi Scene Session/Refresh Scene Names From Build Settings")]
        public void RefreshSceneNamesFromBuildSettings()
        {
            if (onlyAutoFillWhenSceneNamesEmpty && sceneNames != null && sceneNames.Count > 0)
            {
                Debug.Log(
                    "CrowdingMultiSceneSessionManager: Scene Names already contains entries; auto-fill skipped because Only Auto Fill When Scene Names Empty is enabled.",
                    this
                );
                return;
            }

            if (sceneNames == null)
                sceneNames = new List<string>();

            sceneNames.Clear();

            HashSet<string> seen = new HashSet<string>();
            string currentSceneName = SceneManager.GetActiveScene().name;

#if UNITY_EDITOR
            UnityEditor.EditorBuildSettingsScene[] buildScenes = UnityEditor.EditorBuildSettings.scenes;
            for (int i = 0; i < buildScenes.Length; i++)
            {
                UnityEditor.EditorBuildSettingsScene buildScene = buildScenes[i];
                if (buildScene == null || !buildScene.enabled || string.IsNullOrWhiteSpace(buildScene.path))
                    continue;

                string sceneName = Path.GetFileNameWithoutExtension(buildScene.path);
                AddSceneNameFromBuildSettings(sceneName, currentSceneName, seen);
            }
#else
            int sceneCount = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < sceneCount; i++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(i);
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                string sceneName = Path.GetFileNameWithoutExtension(path);
                AddSceneNameFromBuildSettings(sceneName, currentSceneName, seen);
            }
#endif

            Debug.Log($"CrowdingMultiSceneSessionManager: auto-filled {sceneNames.Count} scene name(s) from Build Settings.", this);
        }

        private void AddSceneNameFromBuildSettings(string sceneName, string currentSceneName, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
                return;

            sceneName = sceneName.Trim();

            if (excludeCurrentSceneFromAutoList && sceneName == currentSceneName)
                return;

            if (seen.Contains(sceneName))
                return;

            seen.Add(sceneName);
            sceneNames.Add(sceneName);
        }

        [ContextMenu("Multi Scene Session/Start Session")]
        public void StartSession()
        {
            if (sessionRunning)
            {
                Debug.LogWarning("CrowdingMultiSceneSessionManager: session is already running.", this);
                return;
            }

            if (useEnabledBuildSettingsScenes && refreshSceneNamesOnStart)
                RefreshSceneNamesFromBuildSettings();

            if (sceneNames == null || sceneNames.Count == 0)
            {
                Debug.LogError("CrowdingMultiSceneSessionManager: no scene names assigned. Add scenes to File > Build Settings > Scenes In Build, or disable Use Enabled Build Settings Scenes and fill Scene Names manually.", this);
                return;
            }

            if (dontDestroyOnLoad)
                DontDestroyOnLoad(gameObject);

            if (autoGenerateSessionIdIfEmpty && string.IsNullOrWhiteSpace(sessionId))
                sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            BuildSessionBlocks();

            if (sessionBlocks.Count == 0)
            {
                Debug.LogError("CrowdingMultiSceneSessionManager: session block list is empty.", this);
                return;
            }

            if (logSessionOrderCsv)
                PrepareSessionLogFile();

            sessionRunning = true;
            currentGlobalSessionIndex = -1;
            currentSceneName = "";
            currentConditionLabel = "";

            SessionStarted?.Invoke();
            StartCoroutine(SessionRoutine());
        }

        [ContextMenu("Multi Scene Session/Stop Session")]
        public void StopSession()
        {
            if (!sessionRunning)
                return;

            sessionRunning = false;

            if (activeHandler != null)
            {
                activeHandler.SequenceFinished -= OnActiveHandlerSequenceFinished;
                if (activeHandler.IsRunning)
                    activeHandler.StopSequence();
            }

            activeHandler = null;
            activeHandlerFinished = false;
            currentGlobalSessionIndex = -1;
            currentSceneName = "";
            currentConditionLabel = "";

            StopAllCoroutines();
            SessionFinished?.Invoke();
            Debug.Log("CrowdingMultiSceneSessionManager: session stopped.", this);
        }

        [ContextMenu("Multi Scene Session/Rebuild Session Blocks Only")]
        public void BuildSessionBlocks()
        {
            rng = deterministicRandom ? new System.Random(randomSeed) : new System.Random();
            sessionBlocks.Clear();

            string sid = string.IsNullOrWhiteSpace(sessionId) ? "" : sessionId.Trim();

            for (int s = 0; s < sceneNames.Count; s++)
            {
                string sceneName = sceneNames[s];
                if (string.IsNullOrWhiteSpace(sceneName))
                    continue;

                sceneName = sceneName.Trim();

                if (includeReferenceCondition)
                {
                    sessionBlocks.Add(new CrowdingSceneConditionBlock
                    {
                        sessionId = sid,
                        sceneName = sceneName,
                        conditionType = CrowdingDisplayConditionType.Reference,
                        keepRatioK = 1f,
                        decimationCondition = CrowdingDecimationCondition.Uniform
                    });
                }

                for (int k = 0; k < keepRatiosK.Count; k++)
                {
                    float kk = Mathf.Clamp01(keepRatiosK[k]);

                    if (includeUniformConditions)
                    {
                        sessionBlocks.Add(new CrowdingSceneConditionBlock
                        {
                            sessionId = sid,
                            sceneName = sceneName,
                            conditionType = CrowdingDisplayConditionType.Uniform,
                            keepRatioK = kk,
                            decimationCondition = CrowdingDecimationCondition.Uniform
                        });
                    }

                    if (includeWeightedConditions)
                    {
                        sessionBlocks.Add(new CrowdingSceneConditionBlock
                        {
                            sessionId = sid,
                            sceneName = sceneName,
                            conditionType = CrowdingDisplayConditionType.Weighted,
                            keepRatioK = kk,
                            decimationCondition = CrowdingDecimationCondition.WeightedCrowding
                        });
                    }
                }
            }

            if (randomizeSceneConditionOrder)
                Shuffle(sessionBlocks);

            for (int i = 0; i < sessionBlocks.Count; i++)
            {
                sessionBlocks[i].globalSessionIndex = i;
                sessionBlocks[i].sessionId = sid;
            }

            Debug.Log($"CrowdingMultiSceneSessionManager: built {sessionBlocks.Count} randomized scene-condition blocks.", this);
        }

        private IEnumerator SessionRoutine()
        {
            for (int i = 0; i < sessionBlocks.Count; i++)
            {
                if (!sessionRunning)
                    yield break;

                CrowdingSceneConditionBlock block = sessionBlocks[i];
                currentGlobalSessionIndex = i;
                currentSceneName = block.sceneName;
                currentConditionLabel = block.ConditionLabel;

                AppendSessionLog(block, "LoadSceneStart");

                AsyncOperation op = SceneManager.LoadSceneAsync(block.sceneName, LoadSceneMode.Single);
                if (op == null)
                {
                    Debug.LogError($"CrowdingMultiSceneSessionManager: could not load scene '{block.sceneName}'. Is it in Build Settings?", this);
                    AppendSessionLog(block, "LoadSceneFailed");
                    continue;
                }

                while (!op.isDone)
                    yield return null;

                if (waitOneFrameAfterSceneLoad)
                    yield return null;

                activeHandler = FindFirstObjectByType<CrowdingDisplayHandler>();
                if (activeHandler == null)
                {
                    Debug.LogError($"CrowdingMultiSceneSessionManager: no CrowdingDisplayHandler found in scene '{block.sceneName}'.", this);
                    AppendSessionLog(block, "NoDisplayHandlerFound");
                    continue;
                }

                if (forceHandlerStartOnPlayFalse)
                    activeHandler.startOnPlay = false;

                if (stopHandlerIfAlreadyRunning && activeHandler.IsRunning)
                    activeHandler.StopSequence();

                if (overrideHandlerParticipantId)
                    activeHandler.participantId = participantId;

                activeHandler.ConfigureExternalSingleCondition(
                    block.conditionType,
                    block.keepRatioK,
                    block.decimationCondition,
                    i,
                    block.sessionId
                );

                activeHandlerFinished = false;
                activeHandler.SequenceFinished += OnActiveHandlerSequenceFinished;

                AppendSessionLog(block, "ConditionStart");
                BlockStarted?.Invoke(block);

                activeHandler.StartSequence();

                while (sessionRunning && !activeHandlerFinished)
                    yield return null;

                if (activeHandler != null)
                    activeHandler.SequenceFinished -= OnActiveHandlerSequenceFinished;

                AppendSessionLog(block, "ConditionFinished");
                BlockFinished?.Invoke(block);

                yield return null;
            }

            sessionRunning = false;
            currentGlobalSessionIndex = -1;
            currentSceneName = "";
            currentConditionLabel = "";
            activeHandler = null;
            activeHandlerFinished = false;

            SessionFinished?.Invoke();
            Debug.Log("CrowdingMultiSceneSessionManager: session finished.", this);
        }

        private void OnActiveHandlerSequenceFinished()
        {
            activeHandlerFinished = true;
        }

        private void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(0, i + 1);
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        private string GetSessionLogFullPath()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string safeParticipant = CrowdingDisplayHandler.SanitizePathSegment(participantId);
            string safeSession = CrowdingDisplayHandler.SanitizePathSegment(string.IsNullOrWhiteSpace(sessionId) ? "NoSessionId" : sessionId);
            string folder = Path.Combine(projectRoot, sessionLogsRootRelativePath, "Participant_" + safeParticipant, "Session_" + safeSession);
            return Path.Combine(folder, string.IsNullOrWhiteSpace(sessionLogFileName) ? "multi_scene_session_order.csv" : sessionLogFileName);
        }

        private void PrepareSessionLogFile()
        {
            string path = GetSessionLogFullPath();
            string dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!appendToExistingSessionLog || !File.Exists(path))
            {
                using StreamWriter writer = new StreamWriter(path, false);
                writer.WriteLine("timestampRealtime,sessionId,participantId,eventName,globalSessionIndex,sceneName,conditionLabel,conditionType,isReference,K,decimationCondition,variantKey");
            }
        }

        private void AppendSessionLog(CrowdingSceneConditionBlock block, string eventName)
        {
            if (!logSessionOrderCsv || block == null)
                return;

            string path = GetSessionLogFullPath();
            string dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(path))
                PrepareSessionLogFile();

            string variantKey = block.BuildVariantKey(false);

            using StreamWriter writer = new StreamWriter(path, true);
            writer.WriteLine(
                $"{Time.realtimeSinceStartupAsDouble:F6},{Escape(block.sessionId)},{Escape(participantId)},{Escape(eventName)},{block.globalSessionIndex},{Escape(block.sceneName)},{Escape(block.ConditionLabel)},{block.conditionType},{block.IsReference},{block.keepRatioK:F4},{block.decimationCondition},{Escape(variantKey)}"
            );
        }

        private static string Escape(string value)
        {
            if (value == null) return "";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
