using UnityEngine;

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Small built-in fallback explosion used by CrowdingDisplayHandler when no explosion prefab is assigned.
    /// It creates short-lived glowing spheres with trail renderers, then destroys them automatically.
    /// </summary>
    public static class CrowdingProceduralExplosion
    {
        public static void Spawn(Vector3 position, int particleCount, float radius, float duration, float particleSize)
        {
            particleCount = Mathf.Max(1, particleCount);
            radius = Mathf.Max(0.01f, radius);
            duration = Mathf.Max(0.05f, duration);
            particleSize = Mathf.Max(0.005f, particleSize);

            GameObject root = new GameObject("Crowding_Procedural_Explosion");
            root.transform.position = position;
            Object.Destroy(root, duration + 0.35f);

            Material particleMaterial = CreateMaterial(new Color(1.0f, 0.35f, 0.04f, 1.0f));
            Material trailMaterial = CreateMaterial(new Color(1.0f, 0.65f, 0.08f, 0.8f));

            for (int i = 0; i < particleCount; i++)
            {
                Vector3 direction = Random.onUnitSphere;
                direction.y = Mathf.Abs(direction.y) * 0.45f + 0.10f;
                direction.Normalize();

                float speed = Random.Range(radius * 1.6f, radius * 3.2f);
                float size = Random.Range(particleSize * 0.65f, particleSize * 1.35f);

                GameObject particle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                particle.name = "Crowding_Explosion_Fireball";
                particle.transform.SetParent(root.transform, true);
                particle.transform.position = position;
                particle.transform.localScale = Vector3.one * size;

                Collider collider = particle.GetComponent<Collider>();
                if (collider != null)
                    Object.Destroy(collider);

                Renderer renderer = particle.GetComponent<Renderer>();
                if (renderer != null)
                    renderer.material = particleMaterial;

                TrailRenderer trail = particle.AddComponent<TrailRenderer>();
                trail.time = duration * 0.45f;
                trail.minVertexDistance = 0.005f;
                trail.startWidth = size * 0.8f;
                trail.endWidth = 0f;
                trail.material = trailMaterial;
                trail.autodestruct = false;

                CrowdingProceduralExplosionParticle mover = particle.AddComponent<CrowdingProceduralExplosionParticle>();
                mover.Initialize(direction * speed, duration, size);
            }

            Light flash = root.AddComponent<Light>();
            flash.type = LightType.Point;
            flash.range = Mathf.Max(1.0f, radius * 3.0f);
            flash.intensity = 2.2f;
            flash.color = new Color(1.0f, 0.45f, 0.08f, 1.0f);

            CrowdingProceduralExplosionLight lightFade = root.AddComponent<CrowdingProceduralExplosionLight>();
            lightFade.Initialize(flash, duration * 0.65f);
        }

        private static Material CreateMaterial(Color color)
        {
            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Standard");

            Material material = new Material(shader);
            material.color = color;
            return material;
        }
    }

    public class CrowdingProceduralExplosionParticle : MonoBehaviour
    {
        private Vector3 velocity;
        private float duration;
        private float startSize;
        private float elapsed;
        private Renderer cachedRenderer;
        private Color startColor;

        public void Initialize(Vector3 initialVelocity, float lifetime, float size)
        {
            velocity = initialVelocity;
            duration = Mathf.Max(0.05f, lifetime);
            startSize = Mathf.Max(0.005f, size);
        }

        private void Awake()
        {
            cachedRenderer = GetComponent<Renderer>();
            if (cachedRenderer != null && cachedRenderer.material != null)
                startColor = cachedRenderer.material.color;
            else
                startColor = Color.white;
        }

        private void Update()
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            velocity += Physics.gravity * 0.15f * Time.deltaTime;
            transform.position += velocity * Time.deltaTime;

            float scale = Mathf.Lerp(startSize, 0.001f, t);
            transform.localScale = Vector3.one * scale;

            if (cachedRenderer != null && cachedRenderer.material != null)
            {
                Color c = startColor;
                c.a = Mathf.Lerp(startColor.a, 0f, t);
                cachedRenderer.material.color = c;
            }

            if (elapsed >= duration)
                Destroy(gameObject);
        }
    }

    public class CrowdingProceduralExplosionLight : MonoBehaviour
    {
        private Light targetLight;
        private float duration;
        private float elapsed;
        private float startIntensity;

        public void Initialize(Light lightToFade, float fadeDuration)
        {
            targetLight = lightToFade;
            duration = Mathf.Max(0.05f, fadeDuration);
            startIntensity = targetLight != null ? targetLight.intensity : 0f;
        }

        private void Update()
        {
            if (targetLight == null)
                return;

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            targetLight.intensity = Mathf.Lerp(startIntensity, 0f, t);

            if (elapsed >= duration)
                Destroy(this);
        }
    }
}
