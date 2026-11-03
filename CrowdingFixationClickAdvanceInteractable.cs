using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

namespace CrowdingAwareLOD
{
    public enum CrowdingFixationClickEventSource
    {
        SelectEntered,
        Activated
    }

    /// <summary>
    /// Minimal XRI click counter for fixation markers.
    ///
    /// Important:
    /// This script does NOT use [RequireComponent(typeof(XRBaseInteractable))]
    /// because XRBaseInteractable is abstract and Unity cannot add it automatically.
    /// Add an XR Simple Interactable manually on the same GameObject.
    /// </summary>
    [DisallowMultipleComponent]
    public class CrowdingFixationClickAdvanceInteractable : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private CrowdingDisplayHandler displayHandler;
        [SerializeField] private UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable interactable;

        [Header("Click counting")]
        [SerializeField] private CrowdingFixationClickEventSource clickEventSource = CrowdingFixationClickEventSource.SelectEntered;
        [SerializeField] private bool randomizeRequiredClicksOnEnable = true;
        [SerializeField] private bool resetClickCountOnEnable = true;
        [SerializeField] private bool requireSequenceRunning = true;
        private int minRequiredClicks = 4;
        private int maxRequiredClicks = 6;
        private int requiredClicks = 4;

        [Header("LOD application on first click")]
        [Tooltip("If true, the first click on a newly displayed fixation marker asks CrowdingDisplayHandler to apply the pending LOD change before normal click counting continues.")]
        [SerializeField] private bool applyPendingLodOnClick = true;

        [Tooltip("If false, the first click that applies the LOD is only used to trigger the LOD change and is not counted toward the number of clicks required to advance to the next fixation point. Recommended: false.")]
        [SerializeField] private bool countLodTriggerClickAsAdvanceClick = false;

        [Header("Debug")]
        [SerializeField] private bool debugLogs = true;

        private int clickCount = 0;
        private bool subscribed = false;

        public int RequiredClicks => requiredClicks;
        public int ClickCount => clickCount;

        private void Awake()
        {
            AutoAssignReferences();
            ClampClickSettings();
        }

        private void OnValidate()
        {
            ClampClickSettings();
        }

        private void OnEnable()
        {
            AutoAssignReferences();
            ClampClickSettings();

            if (resetClickCountOnEnable)
                clickCount = 0;

            if (randomizeRequiredClicksOnEnable)
                RandomizeRequiredClicks();

            Subscribe();

            if (debugLogs)
            {
                Debug.Log(
                    $"[FixationClickAdvance] {name} enabled. Required clicks = {requiredClicks}. Source = {clickEventSource}.",
                    this
                );
            }
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void AutoAssignReferences()
        {
            if (displayHandler == null)
                displayHandler = FindFirstObjectByType<CrowdingDisplayHandler>();

            if (interactable == null)
                interactable = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>();
        }

        private void ClampClickSettings()
        {
            minRequiredClicks = Mathf.Max(1, minRequiredClicks);
            maxRequiredClicks = Mathf.Max(minRequiredClicks, maxRequiredClicks);
            requiredClicks = Mathf.Clamp(requiredClicks, minRequiredClicks, maxRequiredClicks);
        }

        private void RandomizeRequiredClicks()
        {
            // Random.Range(int, int) uses an exclusive upper bound.
            requiredClicks = Random.Range(minRequiredClicks, maxRequiredClicks + 1);
        }

        private void Subscribe()
        {
            if (subscribed)
                return;

            if (interactable == null)
            {
                Debug.LogError(
                    $"[FixationClickAdvance] No XR interactable found on {name}. " +
                    "Add 'XR Simple Interactable' manually on the same GameObject. " +
                    "Do not add 'XRBaseInteractable' because it is abstract.",
                    this
                );
                return;
            }

            interactable.selectEntered.AddListener(OnSelectEntered);
            interactable.activated.AddListener(OnActivated);
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed || interactable == null)
                return;

            interactable.selectEntered.RemoveListener(OnSelectEntered);
            interactable.activated.RemoveListener(OnActivated);
            subscribed = false;
        }

        private void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (clickEventSource != CrowdingFixationClickEventSource.SelectEntered)
                return;

            RegisterClick("SelectEntered");
        }

        private void OnActivated(ActivateEventArgs args)
        {
            if (clickEventSource != CrowdingFixationClickEventSource.Activated)
                return;

            RegisterClick("Activated");
        }

        private void RegisterClick(string source)
        {
            if (displayHandler == null)
            {
                Debug.LogError($"[FixationClickAdvance] No DisplayHandler assigned on {name}.", this);
                return;
            }

            if (requireSequenceRunning && !displayHandler.IsRunning)
            {
                if (debugLogs)
                    Debug.Log($"[FixationClickAdvance] Ignored click on {name}: sequence is not running.", this);
                return;
            }

            bool appliedPendingLod = false;

            if (applyPendingLodOnClick)
                appliedPendingLod = displayHandler.ApplyPendingLodFromFixationClick(gameObject);

            if (appliedPendingLod && !countLodTriggerClickAsAdvanceClick)
            {
                clickCount = 0;

                if (debugLogs)
                {
                    Debug.Log(
                        $"[FixationClickAdvance] {name} first click applied pending LOD. This click is not counted for advance.",
                        this
                    );
                }

                return;
            }

            clickCount++;

            if (debugLogs)
            {
                Debug.Log(
                    $"[FixationClickAdvance] {name} click {clickCount}/{requiredClicks} ({source}).",
                    this
                );
            }

            if (clickCount < requiredClicks)
                return;

            clickCount = 0;

            if (debugLogs)
                Debug.Log($"[FixationClickAdvance] {name} reached required clicks. Advancing sequence.", this);

            displayHandler.AdvanceToNextStep();
        }

        [ContextMenu("Randomize Required Clicks Now")]
        public void RandomizeRequiredClicksNow()
        {
            ClampClickSettings();
            RandomizeRequiredClicks();
            clickCount = 0;

            if (debugLogs)
                Debug.Log($"[FixationClickAdvance] {name} new required clicks = {requiredClicks}.", this);
        }

        [ContextMenu("Reset Click Counter")]
        public void ResetClickCounter()
        {
            clickCount = 0;

            if (debugLogs)
                Debug.Log($"[FixationClickAdvance] {name} click counter reset.", this);
        }
    }
}
