using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// Spins whatever it is attached to about the world Y axis.
    ///
    /// Put on the hazard container rather than on each hazard: rotating the parent orbits the whole
    /// ring rigidly, where rotating each child independently would only make each cube pirouette on
    /// the spot.
    /// </summary>
    public sealed class Rotator : MonoBehaviour
    {
        public float degreesPerSecond;

        void Update()
        {
            if (degreesPerSecond == 0f) return;
            transform.Rotate(0f, degreesPerSecond * Time.deltaTime, 0f, Space.World);
        }
    }
}
