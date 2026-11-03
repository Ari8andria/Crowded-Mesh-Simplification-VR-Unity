using UnityEngine;
using UnityEngine.UI;

namespace CrowdingAwareLOD
{
    /// <summary>
    /// UI button used by the Uniform-vs-Weighted/Redistributed preference paradigm.
    ///
    /// Put this component on the UI buttons of the pairwise preference canvas:
    /// - Button "I prefer A" -> Choice = FirstPresentation
    /// - Button "I prefer B" -> Choice = SecondPresentation
    /// - Optional "No preference" -> Choice = NoPreference
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class CrowdingPairwisePreferenceButton : MonoBehaviour
    {
        [Header("References")]
        public CrowdingDisplayHandler displayHandler;

        [Header("Choice")]
        public CrowdingPairwiseChoice choice = CrowdingPairwiseChoice.FirstPresentation;

        private Button button;

        private void Awake()
        {
            button = GetComponent<Button>();

            if (displayHandler == null)
                displayHandler = FindFirstObjectByType<CrowdingDisplayHandler>();
        }

        private void OnEnable()
        {
            if (button == null)
                button = GetComponent<Button>();

            button.onClick.AddListener(SubmitPreference);
        }

        private void OnDisable()
        {
            if (button != null)
                button.onClick.RemoveListener(SubmitPreference);
        }

        public void SubmitPreference()
        {
            if (displayHandler == null)
                displayHandler = FindFirstObjectByType<CrowdingDisplayHandler>();

            if (displayHandler == null)
            {
                Debug.LogWarning("CrowdingPairwisePreferenceButton: no CrowdingDisplayHandler found.", this);
                return;
            }

            displayHandler.SubmitPairwisePreference(choice);
        }
    }
}
