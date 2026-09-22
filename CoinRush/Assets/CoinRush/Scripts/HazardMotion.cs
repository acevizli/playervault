using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// Walks a hazard around the arena on its own heading, turning at random and bouncing off an
    /// invisible circular boundary.
    ///
    /// The earlier version orbited the whole hazard ring rigidly, which made the danger a pattern to
    /// memorise rather than a thing to watch. Independent wandering costs one vector per hazard and
    /// makes every run of the same level play differently.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class HazardMotion : MonoBehaviour
    {
        /// <summary>Largest course correction applied at a turn, in degrees either way.</summary>
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

            // Staggered rather than synchronised: hazards launched on the same frame would otherwise
            // all turn on the same frame forever, which reads as choreography, not chaos.
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

            // The boundary is a circle rather than the square the walls actually make. A hazard that
            // clipped a corner would sit half inside a wall, and the ball would lose a life to
            // something it cannot see.
            var planar = new Vector2(next.x, next.z);
            if (planar.sqrMagnitude > _boundRadius * _boundRadius)
            {
                var inward = -planar.normalized;
                _heading = Vector3.Reflect(_heading, new Vector3(inward.x, 0f, inward.y)).normalized;

                // Pinned back onto the circle in the same frame it crossed. Reflecting alone can
                // leave a hazard outside on a shallow angle, where it bounces every frame and stalls.
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
