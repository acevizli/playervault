using System;
using System.Collections;
using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// A single pickup. It does not know about scores or the SDK. It raises an event when touched
    /// and hides itself; the level decides what the pickup is worth.
    /// </summary>
    public sealed class Coin : MonoBehaviour
    {
        [SerializeField] float spinDegreesPerSecond = 140f;

        [Header("Pickup")]
        [SerializeField] float popSeconds = 0.3f;
        [SerializeField] float popRise = 0.8f;
        [SerializeField] int sparkCount = 7;
        [SerializeField] float sparkSpeed = 5f;

        bool _taken;
        float _spinMultiplier = 1f;

        /// <summary>Raised once, the first time the ball touches this coin.</summary>
        public event Action<Coin> Collected;

        void Update()
        {
            transform.Rotate(0f, spinDegreesPerSecond * _spinMultiplier * Time.deltaTime, 0f, Space.World);
        }

        void OnTriggerEnter(Collider other)
        {
            // Physics can report the same overlap on several frames while the object is being
            // removed, so the flag makes sure Collected fires only once.
            if (_taken || other.GetComponentInParent<BallController>() == null)
            {
                return;
            }

            _taken = true;

            // The coin stays visible for a moment so the pickup reads as a pickup. A coin that simply
            // vanishes looks like a rendering bug. Only the trigger goes off straight away.
            GetComponent<Collider>().enabled = false;
            Collected?.Invoke(this);
            StartCoroutine(Pop());
        }

        IEnumerator Pop()
        {
            // Scaled time, so a paused game freezes the pop with everything else. If the level is
            // rebuilt first, the coin is destroyed and the pop simply stops.
            var visual = transform.childCount > 0 ? transform.GetChild(0) : null;
            var startScale = visual != null ? visual.localScale : Vector3.one;
            var start = transform.position;

            SpawnSparks(visual);
            _spinMultiplier = 6f;

            for (var elapsed = 0f; elapsed < popSeconds; elapsed += Time.deltaTime)
            {
                var t = elapsed / popSeconds;

                // Swell for the first third, then shrink to nothing while rising.
                var scale = t < 0.33f
                    ? Mathf.Lerp(1f, 1.5f, t / 0.33f)
                    : Mathf.Lerp(1.5f, 0f, (t - 0.33f) / 0.67f);

                if (visual != null) visual.localScale = startScale * scale;
                transform.position = start + Vector3.up * (popRise * (1f - (1f - t) * (1f - t)));
                yield return null;
            }

            gameObject.SetActive(false);
        }

        void SpawnSparks(Transform visual)
        {
            var source = visual != null ? visual.GetComponent<Renderer>() : null;
            if (source == null || sparkCount <= 0)
            {
                return;
            }

            var material = source.sharedMaterial;

            for (var i = 0; i < sparkCount; i++)
            {
                // Spread evenly around the coin with a little jitter, angled upwards.
                var angle = (i + UnityEngine.Random.Range(-0.3f, 0.3f)) / sparkCount * Mathf.PI * 2f;
                var direction = new Vector3(Mathf.Cos(angle), UnityEngine.Random.Range(0.6f, 1.2f), Mathf.Sin(angle));

                var spark = GameObject.CreatePrimitive(PrimitiveType.Cube);
                spark.name = "Spark";
                Destroy(spark.GetComponent<Collider>());

                // Parented to the coin's parent, not the coin, so the sparks outlive the coin being
                // switched off but still go when the level is cleared.
                spark.transform.SetParent(transform.parent, worldPositionStays: false);
                spark.transform.position = transform.position;
                spark.transform.rotation = UnityEngine.Random.rotation;
                spark.transform.localScale = Vector3.one * 0.14f;

                var renderer = spark.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                spark.AddComponent<Spark>().Launch(direction.normalized * sparkSpeed);
            }
        }

        /// <summary>A shard thrown out by a pickup. Falls, shrinks and removes itself.</summary>
        sealed class Spark : MonoBehaviour
        {
            const float Lifetime = 0.45f;
            const float Gravity = 14f;

            Vector3 _velocity;
            Vector3 _startScale;
            float _age;

            public void Launch(Vector3 velocity)
            {
                _velocity = velocity;
                _startScale = transform.localScale;
            }

            void Update()
            {
                _age += Time.deltaTime;
                if (_age >= Lifetime)
                {
                    Destroy(gameObject);
                    return;
                }

                _velocity += Vector3.down * (Gravity * Time.deltaTime);
                transform.position += _velocity * Time.deltaTime;
                transform.localScale = _startScale * (1f - _age / Lifetime);
            }
        }
    }
}
