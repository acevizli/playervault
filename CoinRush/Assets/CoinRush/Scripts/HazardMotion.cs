using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// Moves a hazard around the arena in its own direction, turning at random and bouncing off an
    /// invisible circular boundary.
    ///
    /// An earlier version rotated the whole ring of hazards together, which was easy to memorise.
    /// Moving each hazard on its own makes each run of a level play differently.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class HazardMotion : MonoBehaviour
    {
        /// <summary>Largest turn applied at a direction change, in degrees either way.</summary>
        const float MaxTurnDegrees = 70f;

        Rigidbody _body;
        Vector3 _heading;
        float _speed;
        float _boundRadius;
        float _turnInterval;
        float _nextTurnAt;

        void Awake()
        {
            _body = GetComponent<Rigidbody>();
        }

        /// <summary>Sets the hazard moving. A speed of zero leaves it parked where it spawned.</summary>
        public void Launch(float speed, float boundRadius, float turnInterval)
        {
            _speed = Mathf.Max(0f, speed);
            _boundRadius = Mathf.Max(1f, boundRadius);
            _turnInterval = Mathf.Max(0.25f, turnInterval);

            _heading = RandomHeading();

            // Randomise the first turn so hazards spawned on the same frame do not all turn together.
            _nextTurnAt = Time.time + Random.Range(0f, _turnInterval);
        }

        void Update()
        {
            if (_speed <= 0f)
            {
                return;
            }

            if (Time.time >= _nextTurnAt)
            {
                _heading = Quaternion.AngleAxis(
                    Random.Range(-MaxTurnDegrees, MaxTurnDegrees), Vector3.up) * _heading;

                _nextTurnAt = Time.time + _turnInterval * Random.Range(0.5f, 1.5f);
            }

            var position = transform.position;
            var next = position + _heading * (_speed * Time.deltaTime);

            // The boundary is a circle inside the square walls, so a hazard never ends up partly
            // inside a wall where the player cannot see it.
            var planar = new Vector2(next.x, next.z);
            if (planar.sqrMagnitude > _boundRadius * _boundRadius)
            {
                var inward = -planar.normalized;
                _heading = Vector3.Reflect(_heading, new Vector3(inward.x, 0f, inward.y)).normalized;

                // Move it back onto the circle in the same frame. Reflecting alone can leave a hazard
                // outside at a shallow angle, where it bounces every frame and gets stuck.
                planar = planar.normalized * _boundRadius;
                next = new Vector3(planar.x, position.y, planar.y);
            }
            else
            {
                next.y = position.y;
            }

            transform.position = next;
        }

        static Vector3 RandomHeading()
        {
            var angle = Random.Range(0f, Mathf.PI * 2f);
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        }
    }
}
