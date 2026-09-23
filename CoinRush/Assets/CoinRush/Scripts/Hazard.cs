using System;
using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// Something that costs the player a life. Like <see cref="Coin"/>, it holds no game state; it
    /// reports contact and the level decides what happens. Its look is <see cref="HazardMonster"/>.
    /// </summary>
    public sealed class Hazard : MonoBehaviour
    {
        [Tooltip("Seconds before this hazard can hurt the player again.")]
        [SerializeField] float cooldownSeconds = 1f;

        float _readyAt;

        /// <summary>Raised when the ball touches this hazard, at most once per cooldown window.</summary>
        public event Action<Hazard> Touched;

        void OnTriggerEnter(Collider other)
        {
            // The ball is moved back to the start on contact, so it should not still overlap. The
            // cooldown covers cases like a respawn point inside a hazard, which would otherwise
            // take every life in a few frames.
            if (Time.time < _readyAt || other.GetComponentInParent<BallController>() == null)
            {
                return;
            }

            _readyAt = Time.time + cooldownSeconds;
            Touched?.Invoke(this);
        }
    }
}
