using UnityEngine;
using UnityEngine.UI;

namespace CrowdingAwareLOD
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Button))]
    public class CrowdingRatingButton : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private CrowdingDisplayHandler displayHandler;

        [Header("Rating")]
        [Range(1, 5)]
        [SerializeField] private int rating = 1;

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

            button.onClick.AddListener(SubmitRating);
        }

        private void OnDisable()
        {
            if (button != null)
                button.onClick.RemoveListener(SubmitRating);
        }

        public void SubmitRating()
        {
            if (displayHandler == null)
            {
                Debug.LogWarning($"CrowdingRatingButton: no CrowdingDisplayHandler assigned on {name}.", this);
                return;
            }

            displayHandler.SubmitConditionRating(rating);
        }
    }
}
