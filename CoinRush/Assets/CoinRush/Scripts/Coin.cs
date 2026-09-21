using System;
using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// A single pickup. Knows nothing about scores or the SDK — it only announces that it was touched
    /// and takes itself off the board. Whoever owns the level's state decides what that is worth.
    /// </summary>
    public sealed class Coin : MonoBehaviour
    {
        [SerializeField] float spinDegreesPerSecond = 140f;

        bool _taken;

        /// <summary>Raised once, the first time the ball touches this coin.</summary>
        public event Action<Coin> Collected;

        void Update()
        {
            transform.Rotate(0f, spinDegreesPerSecond * Time.deltaTime, 0f, Space.World);
        }

        void OnTriggerEnter(Collider other)
        {
            // Physics can report the same overlap on consecutive frames while the object is being torn
            // down, so the flag — not the deactivation — is what guarantees "collected" fires once.
            if (_taken || other.GetComponentInParent<BallController>() == null)
            {
                return;
            }

            _taken = true;
            gameObject.SetActive(false);
            Collected?.Invoke(this);
        }
    }
}
