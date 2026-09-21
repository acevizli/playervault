using System;
using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// A spinning block that costs the player a life. Like <see cref="Coin"/> it holds no game state;
    /// it reports contact and lets the level decide the consequence.
    /// </summary>
    public sealed class Hazard : MonoBehaviour
    {
        [SerializeField] float spinDegreesPerSecond = 60f;

        [Tooltip("Seconds before this hazard can hurt the player again.")]
        [SerializeField] float cooldownSeconds = 1f;

        float _readyAt;

        /// <summary>Raised when the ball touches this hazard, at most once per cooldown window.</summary>
        public event Action<Hazard> Touched;

        void Update()
        {
            transform.Rotate(0f, spinDegreesPerSecond * Time.deltaTime, 0f, Space.Self);
        }

        void OnTriggerEnter(Collider other)
        {
            // The ball is teleported home on contact, so it should not still be overlapping — but a
            // respawn point inside a hazard, or two hazards in a corner, would otherwise drain every
            // life in a handful of frames. The cooldown makes that impossible rather than unlikely.
            if (Time.time < _readyAt || other.GetComponentInParent<BallController>() == null)
            {
                return;
            }

            _readyAt = Time.time + cooldownSeconds;
            Touched?.Invoke(this);
        }
    }
}
