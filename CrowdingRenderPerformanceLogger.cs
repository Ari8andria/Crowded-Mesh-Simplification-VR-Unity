using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CrowdingAwareLOD
{
    public enum CrowdingPerformanceTransitionMode
    {
        None,
        ByFrameCount,
        ByDurationSeconds,
        ByFrameCountOrDuration
    }

    public enum CrowdingPerformancePhase
    {
        Transition,
        Render,
        All
    }

    [DisallowMultipleComponent]
    public class CrowdingRenderPerformanceLogger : MonoBehaviour
    {
        [Header("References")]
        public CrowdingDisplayHandler displayHandler;

        [Header("Logging")]
        public bool logCsv = true;
        public string csvRelativePath = "CrowdingExports/render_performance_log.csv";
        [Tooltip("When true, logs are written inside the participant folder defined by CrowdingDisplayHandler.")]
        public bool useDisplayHandlerParticipantFolder = true;
        public string renderPerformanceLogFileName = "render_performance_log.csv";
        public bool appendToExistingLog = true;

        [Tooltip("If true, the logger writes a separate row for the transition phase and the stable render phase.")]
        public bool separateTransitionAndRenderFrames = true;

        [Tooltip("If true, also writes an 'All' row using all frames of the step.")]
        public bool alsoWriteAllFramesRow = false;

        [Header("Transition filtering")]
        public CrowdingPerformanceTransitionMode transitionMode = CrowdingPerformanceTransitionMode.ByFrameCountOrDuration;

        [Tooltip("Number of first frames after a new LOD/fixation step that are treated as transition frames.")]
        public int transitionFrameCount = 10;

        [Tooltip("First seconds after a new LOD/fixation step that are treated as transition frames.")]
        public float transitionDurationSeconds = 0.25f;

        [Tooltip("If true, the first Update sample after StepStarted is ignored completely. Useful if you do not want the display-handler keypress/apply frame in the statistics.")]
        public bool ignoreFirstSampleAfterStepStarted = false;

        [Header("Sampling")]
        public bool captureFrameTimings = true;
        public int frameTimingBufferSize = 120;

        private readonly List<FrameSample> transitionSamples = new List<FrameSample>();
        private readonly List<FrameSample> renderSamples = new List<FrameSample>();
        private readonly List<FrameSample> allSamples = new List<FrameSample>();

        private FrameTiming[] frameTimings;
        private CrowdingDisplayStepInfo activeStep;
        private bool recording = false;
        private int sampleIndexInStep = 0;
        private double stepStartRealtime = 0.0;

        private void Awake()
        {
            if (displayHandler == null)
                displayHandler = FindFirstObjectByType<CrowdingDisplayHandler>();

            frameTimings = new FrameTiming[Mathf.Max(1, frameTimingBufferSize)];
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Update()
        {
            if (!recording || activeStep == null)
                return;

            sampleIndexInStep++;

            if (ignoreFirstSampleAfterStepStarted && sampleIndexInStep == 1)
                return;

            double now = Time.realtimeSinceStartupAsDouble;
            double elapsed = Math.Max(0.0, now - stepStartRealtime);

            double cpuMs = -1.0;
            double gpuMs = -1.0;

            if (captureFrameTimings)
            {
                FrameTimingManager.CaptureFrameTimings();
                uint count = FrameTimingManager.GetLatestTimings((uint)frameTimings.Length, frameTimings);

                if (count > 0)
                {
                    FrameTiming latest = frameTimings[count - 1];

                    if (latest.cpuFrameTime > 0)
                        cpuMs = latest.cpuFrameTime;

                    if (latest.gpuFrameTime > 0)
                        gpuMs = latest.gpuFrameTime;
                }
            }

            FrameSample sample = new FrameSample
            {
                frameIndexInStep = sampleIndexInStep,
                timeSinceStepStartSeconds = elapsed,
                deltaMs = Time.unscaledDeltaTime * 1000.0,
                cpuMs = cpuMs,
                gpuMs = gpuMs
            };

            allSamples.Add(sample);

            if (!separateTransitionAndRenderFrames || transitionMode == CrowdingPerformanceTransitionMode.None)
            {
                renderSamples.Add(sample);
                return;
            }

            if (IsTransitionSample(sample))
                transitionSamples.Add(sample);
            else
                renderSamples.Add(sample);
        }

        [ContextMenu("Performance Logger/Subscribe To Display Handler")]
        public void Subscribe()
        {
            if (displayHandler == null)
                displayHandler = FindFirstObjectByType<CrowdingDisplayHandler>();

            if (displayHandler == null)
            {
                Debug.LogWarning("CrowdingRenderPerformanceLogger: no CrowdingDisplayHandler found.", this);
                return;
            }

            displayHandler.StepStarted -= OnStepStarted;
            displayHandler.StepEnded -= OnStepEnded;
            displayHandler.SequenceStarted -= OnSequenceStarted;
            displayHandler.SequenceFinished -= OnSequenceFinished;

            displayHandler.StepStarted += OnStepStarted;
            displayHandler.StepEnded += OnStepEnded;
            displayHandler.SequenceStarted += OnSequenceStarted;
            displayHandler.SequenceFinished += OnSequenceFinished;
        }

        private void Unsubscribe()
        {
            if (displayHandler == null) return;

            displayHandler.StepStarted -= OnStepStarted;
            displayHandler.StepEnded -= OnStepEnded;
            displayHandler.SequenceStarted -= OnSequenceStarted;
            displayHandler.SequenceFinished -= OnSequenceFinished;
        }

        private void OnSequenceStarted()
        {
            if (logCsv)
                PrepareLogFile();
        }

        private void OnSequenceFinished()
        {
            recording = false;
            activeStep = null;
            ClearSamples();
        }

        private void OnStepStarted(CrowdingDisplayStepInfo step)
        {
            activeStep = step;
            recording = true;
            sampleIndexInStep = 0;
            stepStartRealtime = Time.realtimeSinceStartupAsDouble;
            ClearSamples();
        }

        private void OnStepEnded(CrowdingDisplayStepInfo step)
        {
            if (!recording || activeStep == null) return;

            recording = false;

            if (logCsv)
            {
                if (separateTransitionAndRenderFrames && transitionMode != CrowdingPerformanceTransitionMode.None)
                {
                    AppendPerformanceRow(step, CrowdingPerformancePhase.Transition, transitionSamples);
                    AppendPerformanceRow(step, CrowdingPerformancePhase.Render, renderSamples);

                    if (alsoWriteAllFramesRow)
                        AppendPerformanceRow(step, CrowdingPerformancePhase.All, allSamples);
                }
                else
                {
                    AppendPerformanceRow(step, CrowdingPerformancePhase.Render, allSamples);
                }
            }

            activeStep = null;
            ClearSamples();
        }

        private bool IsTransitionSample(FrameSample sample)
        {
            bool byFrames = transitionFrameCount > 0 && sample.frameIndexInStep <= transitionFrameCount;
            bool byTime = transitionDurationSeconds > 0f && sample.timeSinceStepStartSeconds <= transitionDurationSeconds;

            switch (transitionMode)
            {
                case CrowdingPerformanceTransitionMode.ByFrameCount:
                    return byFrames;

                case CrowdingPerformanceTransitionMode.ByDurationSeconds:
                    return byTime;

                case CrowdingPerformanceTransitionMode.ByFrameCountOrDuration:
                    return byFrames || byTime;

                case CrowdingPerformanceTransitionMode.None:
                default:
                    return false;
            }
        }

        private void ClearSamples()
        {
            transitionSamples.Clear();
            renderSamples.Clear();
            allSamples.Clear();
        }

        private void AppendPerformanceRow(
            CrowdingDisplayStepInfo step,
            CrowdingPerformancePhase phase,
            List<FrameSample> samples)
        {
            string path = GetLogFullPath();
            SampleStats deltaStats = ComputeDeltaStats(samples);
            SampleStats cpuStats = ComputeCpuStats(samples);
            SampleStats gpuStats = ComputeGpuStats(samples);

            double phaseDuration = ComputePhaseDuration(samples);
            double stepDuration = Math.Max(0.0, step.endTimeRealtime - step.startTimeRealtime);

            double meanFps = deltaStats.mean > 0 ? 1000.0 / deltaStats.mean : 0.0;
            double minFps = deltaStats.max > 0 ? 1000.0 / deltaStats.max : 0.0;
            double maxFps = deltaStats.min > 0 ? 1000.0 / deltaStats.min : 0.0;

            using StreamWriter writer = new StreamWriter(path, true);
            writer.WriteLine(
                $"{Escape(step.sceneName)}," +
                $"{Escape(step.participantId)}," +
                $"{step.globalStepIndex}," +
                $"{step.fixationIndex}," +
                $"{Escape(step.fixationName)}," +
                $"{step.conditionType}," +
                $"{step.isReference}," +
                $"{step.keepRatioK:F4}," +
                $"{step.decimationCondition}," +
                $"{Escape(step.variantKey)}," +
                $"{phase}," +
                $"{stepDuration:F6}," +
                $"{phaseDuration:F6}," +
                $"{samples.Count}," +
                $"{transitionFrameCount}," +
                $"{transitionDurationSeconds:F6}," +
                $"{meanFps:F4}," +
                $"{minFps:F4}," +
                $"{maxFps:F4}," +
                $"{deltaStats.mean:F4}," +
                $"{deltaStats.min:F4}," +
                $"{deltaStats.max:F4}," +
                $"{deltaStats.std:F4}," +
                $"{cpuStats.mean:F4}," +
                $"{cpuStats.min:F4}," +
                $"{cpuStats.max:F4}," +
                $"{cpuStats.std:F4}," +
                $"{gpuStats.mean:F4}," +
                $"{gpuStats.min:F4}," +
                $"{gpuStats.max:F4}," +
                $"{gpuStats.std:F4}," +
                $"{QualitySettings.vSyncCount}," +
                $"{Application.targetFrameRate}," +
                $"{Screen.width}," +
                $"{Screen.height}"
            );
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
                writer.WriteLine(
                    "sceneName," +
                    "participantId," +
                    "globalStepIndex," +
                    "fixationIndex," +
                    "fixationName," +
                    "conditionType," +
                    "isReference," +
                    "K," +
                    "decimationCondition," +
                    "variantKey," +
                    "phase," +
                    "stepDurationSeconds," +
                    "phaseDurationSeconds," +
                    "frameCount," +
                    "transitionFrameCountConfig," +
                    "transitionDurationSecondsConfig," +
                    "meanFps," +
                    "minFps," +
                    "maxFps," +
                    "meanFrameMs," +
                    "minFrameMs," +
                    "maxFrameMs," +
                    "stdFrameMs," +
                    "meanCpuFrameMs," +
                    "minCpuFrameMs," +
                    "maxCpuFrameMs," +
                    "stdCpuFrameMs," +
                    "meanGpuFrameMs," +
                    "minGpuFrameMs," +
                    "maxGpuFrameMs," +
                    "stdGpuFrameMs," +
                    "vSyncCount," +
                    "targetFrameRate," +
                    "screenWidth," +
                    "screenHeight"
                );
            }
        }

        private string GetLogFullPath()
        {
            if (useDisplayHandlerParticipantFolder && displayHandler != null)
                return displayHandler.GetParticipantLogFullPath(renderPerformanceLogFileName);

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, csvRelativePath);
        }

        private static double ComputePhaseDuration(List<FrameSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return 0.0;

            double sum = 0.0;
            for (int i = 0; i < samples.Count; i++)
                sum += Math.Max(0.0, samples[i].deltaMs / 1000.0);

            return sum;
        }

        private static SampleStats ComputeDeltaStats(List<FrameSample> samples)
        {
            List<double> values = new List<double>(samples != null ? samples.Count : 0);
            if (samples != null)
            {
                for (int i = 0; i < samples.Count; i++)
                    values.Add(samples[i].deltaMs);
            }
            return ComputeStats(values);
        }

        private static SampleStats ComputeCpuStats(List<FrameSample> samples)
        {
            List<double> values = new List<double>(samples != null ? samples.Count : 0);
            if (samples != null)
            {
                for (int i = 0; i < samples.Count; i++)
                {
                    if (samples[i].cpuMs > 0)
                        values.Add(samples[i].cpuMs);
                }
            }
            return ComputeStats(values);
        }

        private static SampleStats ComputeGpuStats(List<FrameSample> samples)
        {
            List<double> values = new List<double>(samples != null ? samples.Count : 0);
            if (samples != null)
            {
                for (int i = 0; i < samples.Count; i++)
                {
                    if (samples[i].gpuMs > 0)
                        values.Add(samples[i].gpuMs);
                }
            }
            return ComputeStats(values);
        }

        private static SampleStats ComputeStats(List<double> values)
        {
            if (values == null || values.Count == 0)
                return new SampleStats { count = 0, mean = -1, min = -1, max = -1, std = -1 };

            double sum = 0;
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;

            for (int i = 0; i < values.Count; i++)
            {
                double v = values[i];
                sum += v;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }

            double mean = sum / values.Count;
            double variance = 0;

            for (int i = 0; i < values.Count; i++)
            {
                double d = values[i] - mean;
                variance += d * d;
            }

            variance /= values.Count;

            return new SampleStats
            {
                count = values.Count,
                mean = mean,
                min = min,
                max = max,
                std = Math.Sqrt(variance)
            };
        }

        private static string Escape(string value)
        {
            if (value == null) return "";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private struct FrameSample
        {
            public int frameIndexInStep;
            public double timeSinceStepStartSeconds;
            public double deltaMs;
            public double cpuMs;
            public double gpuMs;
        }

        private struct SampleStats
        {
            public int count;
            public double mean;
            public double min;
            public double max;
            public double std;
        }
    }
}
