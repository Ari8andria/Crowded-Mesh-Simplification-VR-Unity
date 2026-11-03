using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Runs many independent experimental scenes one after another.
    ///
    /// Use case:
    /// - each Unity scene already contains one CrowdingDisplayHandler
    /// - each scene already defines its own single condition through the handler Inspector
    /// - the runner only randomizes / orders the scenes, loads them, starts the local handler,
    ///   waits until it finishes, then loads the next scene.
    ///
    /// This does NOT interleave conditions across scenes.
    /// </summary>
    [DisallowMultipleComponent]
    public class CrowdingSceneBatchRunner : MonoBehaviour
    {
        [Header("Scene list")]
        [Tooltip("If true, the scene list is rebuilt from File > Build Settings > Scenes In Build before the session starts.")]
        public bool useEnabledBuildSettingsScenes = true;

        [Tooltip("If true, refresh Scene Names automatically when Start Batch is called.")]
        public bool refreshSceneNamesOnStart = true;

        [Tooltip("Usually true when this object is placed in a launcher scene. The currently active scene will not be included in the experimental scene list.")]
        public bool excludeCurrentScene = true;

        [Tooltip("Manual fallback list. Used when Use Enabled Build Settings Scenes is false, or when the auto list is empty.")]
        public List<string> sceneNames = new List<string>();

        [Header("Randomization")]
        public bool randomizeSceneOrder = true;
        public bool deterministicRandom = true;
        public int randomSeed = 12345;

        [Header("Participant / session")]
        public string participantId = "P001";
        public string sessionId = "";
        public bool autoGenerateSessionIdIfEmpty = true;
        public bool dontDestroyOnLoad = true;

        [Header("Local CrowdingDisplayHandler control")]
        [Tooltip("If true, the runner overwrites the participantId of the handler in every loaded scene.")]
        public bool overrideHandlerParticipantId = true;

        [Tooltip("Recommended: prevents a local handler from starting before the batch runner controls it.")]
        public bool forceHandlerStartOnPlayFalse = true;

        [Tooltip("If true, stop a handler that accidentally started by itself before launching it again.")]
        public bool stopHandlerIfAlreadyRunning = true;

        [Tooltip("Wait this many frames after scene load before looking for CrowdingDisplayHandler. Useful when scripts initialize on the first frame.")]
        public int framesToWaitAfterSceneLoad = 1;

        [Header("Loading / transition")]
        [Tooltip("Optional canvas or world-space loading panel. If assigned, it is shown during scene loading and hidden while the participant performs the task.")]
        public GameObject loadingCanvas;

        [Tooltip("Minimum time to keep the loading canvas visible. Useful to avoid abrupt flashes between scenes.")]
        public float minimumLoadingScreenSeconds = 0.5f;

        [Tooltip("Optional delay after a scene finishes before the next scene starts loading.")]
        public float delayAfterSceneFinishedSeconds = 0.25f;

        [Header("Manual safety pause")]
        [Tooltip("If true, the runner pauses after every scene and waits for Continue Key before loading the next scene.")]
        public bool waitForContinueKeyBetweenScenes = false;

        public KeyCode continueKey = KeyCode.Space;

        [Header("Logging")]
        public bool logBatchCsv = true;
        public string logsRootRelativePath = "CrowdingExports";
        public string batchLogFileName = "scene_batch_log.csv";
        public bool appendToExistingBatchLog = false;

        [Header("Start")]
        public bool startOnPlay = true;

        [Header("Runtime state")]
        [SerializeField] private bool batchRunning = false;
        [SerializeField] private int currentSceneOrderIndex = -1;
        [SerializeField] private string currentSceneName = "";
        [SerializeField] private string currentEvent = "";

        private readonly List<string> runtimeSceneOrder = new List<string>();
        private CrowdingDisplayHandler activeHandler;
        private bool activeHandlerFinished;
        private System.Random rng;

        public bool IsRunning => batchRunning;
        public IReadOnlyList<string> RuntimeSceneOrder => runtimeSceneOrder;
        public int CurrentSceneOrderIndex => currentSceneOrderIndex;
        public string CurrentSceneName => currentSceneName;

        public event Action<string, int> SceneBlockStarted;
        public event Action<string, int> SceneBlockFinished;
        public event Action BatchStarted;
        public event Action BatchFinished;

        private void Start()
        {
            if (startOnPlay)
                StartBatch();
        }

        [ContextMenu("Scene Batch/Start Batch")]
        public void StartBatch()
        {
            if (batchRunning)
            {
                Debug.LogWarning("CrowdingSceneBatchRunner: batch is already running.", this);
                return;
            }

            if (dontDestroyOnLoad)
                DontDestroyOnLoad(gameObject);

            if (autoGenerateSessionIdIfEmpty && string.IsNullOrWhiteSpace(sessionId))
                sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            BuildRuntimeSceneOrder();

            if (runtimeSceneOrder.Count == 0)
            {
                Debug.LogError("CrowdingSceneBatchRunner: no scene to run. Add scenes to Build Settings or fill Scene Names manually.", this);
                return;
            }

            if (logBatchCsv)
                PrepareBatchLogFile();

            batchRunning = true;
            currentSceneOrderIndex = -1;
            currentSceneName = "";
            currentEvent = "BatchStarted";
            activeHandler = null;
            activeHandlerFinished = false;

            ShowLoadingCanvas(false);
            AppendBatchLog("BatchStarted", "");
            BatchStarted?.Invoke();

            StartCoroutine(BatchRoutine());
        }

        [ContextMenu("Scene Batch/Stop Batch")]
        public void StopBatch()
        {
            if (!batchRunning)
                return;

            batchRunning = false;

            if (activeHandler != null)
            {
                activeHandler.SequenceFinished -= OnActiveHandlerSequenceFinished;
                if (activeHandler.IsRunning)
                    activeHandler.StopSequence();
            }

            activeHandler = null;
            activeHandlerFinished = false;
            currentSceneOrderIndex = -1;
            currentSceneName = "";
            currentEvent = "BatchStopped";

            StopAllCoroutines();
            ShowLoadingCanvas(false);
            AppendBatchLog("BatchStopped", "");
            BatchFinished?.Invoke();
            Debug.Log("CrowdingSceneBatchRunner: batch stopped.", this);
        }

        [ContextMenu("Scene Batch/Refresh Scene Names From Build Settings")]
        public void RefreshSceneNamesFromBuildSettings()
        {
            sceneNames.Clear();

            string activeSceneName = SceneManager.GetActiveScene().name;

#if UNITY_EDITOR
            EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;
            for (int i = 0; i < buildScenes.Length; i++)
            {
                EditorBuildSettingsScene buildScene = buildScenes[i];
                if (buildScene == null || !buildScene.enabled)
                    continue;

                string name = Path.GetFileNameWithoutExtension(buildScene.path);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (excludeCurrentScene && name == activeSceneName)
                    continue;

                if (!sceneNames.Contains(name))
                    sceneNames.Add(name);
            }
#else
            int count = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < count; i++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(i);
                string name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (excludeCurrentScene && name == activeSceneName)
                    continue;

                if (!sceneNames.Contains(name))
                    sceneNames.Add(name);
            }
#endif

            Debug.Log($"CrowdingSceneBatchRunner: refreshed {sceneNames.Count} scene names.", this);
        }

        [ContextMenu("Scene Batch/Rebuild Runtime Scene Order Only")]
        public void BuildRuntimeSceneOrder()
        {
            if (useEnabledBuildSettingsScenes && refreshSceneNamesOnStart)
                RefreshSceneNamesFromBuildSettings();

            rng = deterministicRandom ? new System.Random(randomSeed) : new System.Random();
            runtimeSceneOrder.Clear();

            for (int i = 0; i < sceneNames.Count; i++)
            {
                string sceneName = sceneNames[i];
                if (string.IsNullOrWhiteSpace(sceneName))
                    continue;

                sceneName = sceneName.Trim();
                if (!runtimeSceneOrder.Contains(sceneName))
                    runtimeSceneOrder.Add(sceneName);
            }

            if (randomizeSceneOrder)
                Shuffle(runtimeSceneOrder);

            Debug.Log("CrowdingSceneBatchRunner: runtime scene order = " + string.Join(" -> ", runtimeSceneOrder), this);
        }

        private IEnumerator BatchRoutine()
        {
            for (int i = 0; i < runtimeSceneOrder.Count; i++)
            {
                if (!batchRunning)
                    yield break;

                currentSceneOrderIndex = i;
                currentSceneName = runtimeSceneOrder[i];

                ShowLoadingCanvas(true);
                currentEvent = "LoadSceneStart";
                AppendBatchLog("LoadSceneStart", currentSceneName);

                float loadingStart = Time.unscaledTime;
                AsyncOperation loadOperation = SceneManager.LoadSceneAsync(currentSceneName, LoadSceneMode.Single);

                if (loadOperation == null)
                {
                    Debug.LogError($"CrowdingSceneBatchRunner: could not load scene '{currentSceneName}'. Is it in Build Settings?", this);
                    AppendBatchLog("LoadSceneFailed", currentSceneName);
                    continue;
                }

                while (!loadOperation.isDone)
                    yield return null;

                float minVisible = Mathf.Max(0f, minimumLoadingScreenSeconds);
                while (Time.unscaledTime - loadingStart < minVisible)
                    yield return null;

                int waitFrames = Mathf.Max(0, framesToWaitAfterSceneLoad);
                for (int f = 0; f < waitFrames; f++)
                    yield return null;

                currentEvent = "SceneLoaded";
                AppendBatchLog("SceneLoaded", currentSceneName);

                activeHandler = FindFirstObjectByType<CrowdingDisplayHandler>();
                if (activeHandler == null)
                {
                    Debug.LogError($"CrowdingSceneBatchRunner: no CrowdingDisplayHandler found in scene '{currentSceneName}'.", this);
                    AppendBatchLog("NoDisplayHandlerFound", currentSceneName);
                    continue;
                }

                if (forceHandlerStartOnPlayFalse)
                    activeHandler.startOnPlay = false;

                if (stopHandlerIfAlreadyRunning && activeHandler.IsRunning)
                    activeHandler.StopSequence();

                if (overrideHandlerParticipantId)
                    activeHandler.participantId = participantId;

                activeHandlerFinished = false;
                activeHandler.SequenceFinished += OnActiveHandlerSequenceFinished;

                currentEvent = "SceneSequenceStart";
                AppendBatchLog("SceneSequenceStart", currentSceneName);
                SceneBlockStarted?.Invoke(currentSceneName, i);

                ShowLoadingCanvas(false);
                activeHandler.StartSequence();

                while (batchRunning && !activeHandlerFinished)
                    yield return null;

                if (activeHandler != null)
                    activeHandler.SequenceFinished -= OnActiveHandlerSequenceFinished;

                currentEvent = "SceneSequenceFinished";
                AppendBatchLog("SceneSequenceFinished", currentSceneName);
                SceneBlockFinished?.Invoke(currentSceneName, i);

                if (delayAfterSceneFinishedSeconds > 0f)
                    yield return new WaitForSecondsRealtime(delayAfterSceneFinishedSeconds);

                if (waitForContinueKeyBetweenScenes && i < runtimeSceneOrder.Count - 1)
                {
                    ShowLoadingCanvas(true);
                    currentEvent = "WaitingForContinue";
                    AppendBatchLog("WaitingForContinue", currentSceneName);

                    while (batchRunning && !IsContinueKeyPressed())
                        yield return null;
                }
            }

            batchRunning = false;
            currentSceneOrderIndex = -1;
            currentSceneName = "";
            currentEvent = "BatchFinished";
            activeHandler = null;
            activeHandlerFinished = false;

            ShowLoadingCanvas(false);
            AppendBatchLog("BatchFinished", "");
            BatchFinished?.Invoke();
            Debug.Log("CrowdingSceneBatchRunner: batch finished.", this);
        }

        private void OnActiveHandlerSequenceFinished()
        {
            activeHandlerFinished = true;
        }

        private bool IsContinueKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current == null)
                return false;

            if (continueKey == KeyCode.Space)
                return Keyboard.current.spaceKey.wasPressedThisFrame;
            if (continueKey == KeyCode.Return || continueKey == KeyCode.KeypadEnter)
                return Keyboard.current.enterKey.wasPressedThisFrame;
            if (continueKey == KeyCode.Escape)
                return Keyboard.current.escapeKey.wasPressedThisFrame;

            return Keyboard.current.spaceKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(continueKey);
#else
            return false;
#endif
        }

        private void ShowLoadingCanvas(bool visible)
        {
            if (loadingCanvas != null)
                loadingCanvas.SetActive(visible);
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

        private string GetBatchLogFullPath()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string safeParticipant = SanitizePathSegment(participantId);
            string safeSession = SanitizePathSegment(string.IsNullOrWhiteSpace(sessionId) ? "NoSessionId" : sessionId);
            string folder = Path.Combine(projectRoot, logsRootRelativePath, "Participant_" + safeParticipant, "Session_" + safeSession);
            return Path.Combine(folder, string.IsNullOrWhiteSpace(batchLogFileName) ? "scene_batch_log.csv" : batchLogFileName);
        }

        private void PrepareBatchLogFile()
        {
            string path = GetBatchLogFullPath();
            string dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!appendToExistingBatchLog || !File.Exists(path))
            {
                using StreamWriter writer = new StreamWriter(path, false);
                writer.WriteLine("timestampRealtime,sessionId,participantId,eventName,sceneOrderIndex,sceneName,activeSceneName,runtimeSceneOrder");
            }
        }

        private void AppendBatchLog(string eventName, string sceneName)
        {
            if (!logBatchCsv)
                return;

            string path = GetBatchLogFullPath();
            string dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(path))
                PrepareBatchLogFile();

            string orderText = string.Join(" -> ", runtimeSceneOrder);
            string activeSceneName = SceneManager.GetActiveScene().name;

            using StreamWriter writer = new StreamWriter(path, true);
            writer.WriteLine(
                Time.realtimeSinceStartupAsDouble.ToString("F6") + "," +
                Escape(sessionId) + "," +
                Escape(participantId) + "," +
                Escape(eventName) + "," +
                currentSceneOrderIndex + "," +
                Escape(sceneName) + "," +
                Escape(activeSceneName) + "," +
                Escape(orderText)
            );
        }

        private static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Unknown";

            string trimmed = value.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
                trimmed = trimmed.Replace(invalid[i], '_');

            trimmed = trimmed.Replace(' ', '_');
            return string.IsNullOrWhiteSpace(trimmed) ? "Unknown" : trimmed;
        }

        private static string Escape(string value)
        {
            if (value == null)
                return "";

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";

            return value;
        }
    }
}
