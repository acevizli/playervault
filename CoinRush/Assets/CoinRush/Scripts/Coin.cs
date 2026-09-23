using System;
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

        bool _taken;

        /// <summary>Raised once, the first time the ball touches this coin.</summary>
        public event Action<Coin> Collected;

        void Update()
        {
            transform.Rotate(0f, spinDegreesPerSecond * Time.deltaTime, 0f, Space.World);
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
            gameObject.SetActive(false);
            Collected?.Invoke(this);
        }
    }
}
