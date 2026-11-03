using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace CrowdingAwareLOD
{
    public enum CrowdingXRFireballEventSource
    {
        SelectEntered,
        Activated
    }

    /// <summary>
    /// Visual feedback for XR clicks on fixation markers.
    /// Add this script on the same GameObject as XR Simple Interactable.
    /// It does not modify the DisplayHandler and does not advance the sequence.
    /// </summary>
    [DisallowMultipleComponent]
    public class CrowdingXRClickFireballFeedback : MonoBehaviour
    {
        [Header("XR event")]
        [Tooltip("Use the same event source as CrowdingFixationClickAdvanceInteractable. SelectEntered is often the grip. Activated is often the trigger depending on your XRI input actions.")]
        public CrowdingXRFireballEventSource eventSource = CrowdingXRFireballEventSource.SelectEntered;

        [Header("Target")]
        [Tooltip("Optional target point for the fireballs. If empty, the script uses this object's Collider center, then this Transform position as fallback.")]
        public Transform targetOverride;
        public Vector3 targetWorldOffset = Vector3.zero;

        [Header("Fireballs")]
        [Tooltip("Optional prefab. If empty, a small procedural glowing sphere with a trail is created at runtime.")]
        public GameObject fireballPrefab;
        [Range(1, 20)] public int fireballCount = 4;
        [Range(0.03f, 0.5f)] public float fireballSize = 0.08f;
        [Range(0.05f, 2.0f)] public float travelDuration = 0.35f;
        [Range(0.0f, 1.0f)] public float randomStartSpread = 0.05f;
        [Range(0.0f, 1.5f)] public float arcHeight = 0.20f;
        [Range(0.0f, 1.0f)] public float randomArcSpread = 0.15f;
        [Range(0.0f, 0.25f)] public float spawnIntervalSeconds = 0.025f;

        [Header("Procedural appearance")]
        public Color coreColor = new Color(1.0f, 0.45f, 0.05f, 1.0f);
        public Color trailColor = new Color(1.0f, 0.12f, 0.0f, 0.85f);
        [Range(0.02f, 1.0f)] public float trailTime = 0.22f;
        [Range(0.005f, 0.1f)] public float trailWidth = 0.035f;
        public bool addPointLight = false;
        [Range(0.0f, 5.0f)] public float pointLightIntensity = 1.5f;
        [Range(0.0f, 2.0f)] public float pointLightRange = 0.6f;

        [Header("Debug")]
        public bool debugLogs = false;

        private XRBaseInteractable interactable;
        private Collider cachedCollider;

        private void Awake()
        {
            interactable = GetComponent<XRBaseInteractable>();
            cachedCollider = GetComponent<Collider>();

            if (interactable == null)
            {
                Debug.LogWarning(
                    $"[CrowdingXRClickFireballFeedback] No XR Simple Interactable / XRBaseInteractable found on {name}. " +
                    "Add XR Simple Interactable manually on the same GameObject.",
                    this
                );
            }
        }

        private void OnEnable()
        {
            if (interactable == null)
                interactable = GetComponent<XRBaseInteractable>();

            if (interactable == null)
                return;

            interactable.selectEntered.AddListener(OnSelectEntered);
            interactable.activated.AddListener(OnActivated);
        }

        private void OnDisable()
        {
            if (interactable == null)
                return;

            interactable.selectEntered.RemoveListener(OnSelectEntered);
            interactable.activated.RemoveListener(OnActivated);
        }

        private void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (eventSource != CrowdingXRFireballEventSource.SelectEntered)
                return;

            Transform source = args.interactorObject != null ? args.interactorObject.transform : null;
            Play(source);
        }

        private void OnActivated(ActivateEventArgs args)
        {
            if (eventSource != CrowdingXRFireballEventSource.Activated)
                return;

            Transform source = args.interactorObject != null ? args.interactorObject.transform : null;
            Play(source);
        }

        public void Play(Transform controllerOrInteractorTransform)
        {
            Vector3 start = GetStartPosition(controllerOrInteractorTransform);
            Vector3 end = GetTargetPosition();

            if (debugLogs)
                Debug.Log($"[CrowdingXRClickFireballFeedback] Fireballs: {start} -> {end} on {name}", this);

            CrowdingFireballRuntime.Instance.PlayFireballs(
                start,
                end,
                fireballPrefab,
                fireballCount,
                fireballSize,
                travelDuration,
                randomStartSpread,
                arcHeight,
                randomArcSpread,
                spawnIntervalSeconds,
                coreColor,
                trailColor,
                trailTime,
                trailWidth,
                addPointLight,
                pointLightIntensity,
                pointLightRange
            );
        }

        private Vector3 GetStartPosition(Transform controllerOrInteractorTransform)
        {
            if (controllerOrInteractorTransform != null)
                return controllerOrInteractorTransform.position;

            if (Camera.main != null)
                return Camera.main.transform.position;

            return transform.position;
        }

        private Vector3 GetTargetPosition()
        {
            if (targetOverride != null)
                return targetOverride.position + targetWorldOffset;

            if (cachedCollider == null)
                cachedCollider = GetComponent<Collider>();

            if (cachedCollider != null)
                return cachedCollider.bounds.center + targetWorldOffset;

            return transform.position + targetWorldOffset;
        }
    }

    /// <summary>
    /// Runtime helper kept on a separate active GameObject so fireball coroutines can continue
    /// even if the clicked fixation marker is hidden immediately after advancing.
    /// </summary>
    internal class CrowdingFireballRuntime : MonoBehaviour
    {
        private static CrowdingFireballRuntime instance;
        public static CrowdingFireballRuntime Instance
        {
            get
            {
                if (instance != null)
                    return instance;

                GameObject go = new GameObject("CrowdingFireballRuntime");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<CrowdingFireballRuntime>();
                return instance;
            }
        }

        private Material proceduralCoreMaterial;
        private Material proceduralTrailMaterial;

        public void PlayFireballs(
            Vector3 start,
            Vector3 end,
            GameObject prefab,
            int count,
            float size,
            float duration,
            float startSpread,
            float arcHeight,
            float arcSpread,
            float spawnInterval,
            Color coreColor,
            Color trailColor,
            float trailTime,
            float trailWidth,
            bool addLight,
            float lightIntensity,
            float lightRange)
        {
            StartCoroutine(PlayFireballsRoutine(
                start,
                end,
                prefab,
                Mathf.Max(1, count),
                Mathf.Max(0.01f, size),
                Mathf.Max(0.01f, duration),
                Mathf.Max(0f, startSpread),
                Mathf.Max(0f, arcHeight),
                Mathf.Max(0f, arcSpread),
                Mathf.Max(0f, spawnInterval),
                coreColor,
                trailColor,
                Mathf.Max(0.01f, trailTime),
                Mathf.Max(0.001f, trailWidth),
                addLight,
                lightIntensity,
                lightRange
            ));
        }

        private IEnumerator PlayFireballsRoutine(
            Vector3 start,
            Vector3 end,
            GameObject prefab,
            int count,
            float size,
            float duration,
            float startSpread,
            float arcHeight,
            float arcSpread,
            float spawnInterval,
            Color coreColor,
            Color trailColor,
            float trailTime,
            float trailWidth,
            bool addLight,
            float lightIntensity,
            float lightRange)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 jitter = Random.insideUnitSphere * startSpread;
                jitter.y *= 0.35f;

                GameObject fireball = CreateFireball(prefab, start + jitter, size, coreColor, trailColor, trailTime, trailWidth, addLight, lightIntensity, lightRange);
                StartCoroutine(AnimateFireball(fireball, start + jitter, end, duration, arcHeight, arcSpread));

                if (spawnInterval > 0f)
                    yield return new WaitForSeconds(spawnInterval);
            }
        }

        private GameObject CreateFireball(
            GameObject prefab,
            Vector3 position,
            float size,
            Color coreColor,
            Color trailColor,
            float trailTime,
            float trailWidth,
            bool addLight,
            float lightIntensity,
            float lightRange)
        {
            GameObject fireball;

            if (prefab != null)
            {
                fireball = Instantiate(prefab, position, Quaternion.identity);
                fireball.transform.localScale = Vector3.one * size;
                return fireball;
            }

            fireball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            fireball.name = "Crowding_Click_Fireball";
            fireball.transform.position = position;
            fireball.transform.localScale = Vector3.one * size;

            Collider col = fireball.GetComponent<Collider>();
            if (col != null)
                Destroy(col);

            Renderer renderer = fireball.GetComponent<Renderer>();
            if (renderer != null)
            {
                if (proceduralCoreMaterial == null)
                    proceduralCoreMaterial = CreateMaterial(coreColor);
                else
                    proceduralCoreMaterial.color = coreColor;

                renderer.material = proceduralCoreMaterial;
            }

            TrailRenderer trail = fireball.AddComponent<TrailRenderer>();
            trail.time = trailTime;
            trail.widthMultiplier = trailWidth;
            trail.numCornerVertices = 4;
            trail.numCapVertices = 4;
            trail.autodestruct = false;
            trail.startColor = trailColor;
            trail.endColor = new Color(trailColor.r, trailColor.g, trailColor.b, 0f);

            if (proceduralTrailMaterial == null)
                proceduralTrailMaterial = CreateMaterial(trailColor);
            else
                proceduralTrailMaterial.color = trailColor;

            trail.material = proceduralTrailMaterial;

            if (addLight)
            {
                Light light = fireball.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = coreColor;
                light.intensity = lightIntensity;
                light.range = lightRange;
            }

            return fireball;
        }

        private Material CreateMaterial(Color color)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Standard");

            Material mat = new Material(shader);
            mat.color = color;
            return mat;
        }

        private IEnumerator AnimateFireball(GameObject fireball, Vector3 start, Vector3 end, float duration, float arcHeight, float arcSpread)
        {
            if (fireball == null)
                yield break;

            Vector3 middle = (start + end) * 0.5f;
            Vector3 randomSide = Random.insideUnitSphere * arcSpread;
            randomSide.y *= 0.35f;
            Vector3 control = middle + Vector3.up * arcHeight + randomSide;

            float elapsed = 0f;

            while (elapsed < duration)
            {
                if (fireball == null)
                    yield break;

                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = 1f - Mathf.Pow(1f - t, 2f);

                Vector3 p0 = Vector3.Lerp(start, control, eased);
                Vector3 p1 = Vector3.Lerp(control, end, eased);
                fireball.transform.position = Vector3.Lerp(p0, p1, eased);

                float scale = Mathf.Lerp(1f, 0.35f, Mathf.SmoothStep(0f, 1f, t));
                fireball.transform.localScale *= scale > 0f ? 1f : 1f;

                yield return null;
            }

            fireball.transform.position = end;
            yield return new WaitForSeconds(0.08f);

            if (fireball != null)
                Destroy(fireball);
        }
    }
}
