using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// Moves the player ball by applying forces to its Rigidbody, so the physics engine handles
    /// rolling, bouncing and momentum.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class BallController : MonoBehaviour
    {
        [Header("Feel")]
        [SerializeField] float acceleration = 22f;
        [SerializeField] float maxSpeed = 9f;

        [Tooltip("Fraction of the shorter screen edge the finger travels for full tilt.")]
        [SerializeField, Range(0.05f, 0.5f)] float dragRadiusFraction = 0.18f;

        [Header("Frame of reference")]
        [Tooltip("Steering is relative to this transform. Leave empty to use the main camera.")]
        [SerializeField] Transform steeringReference;

        [Header("Safety net")]
        [Tooltip("Below this height the ball is considered fallen and is put back at its start point.")]
        [SerializeField] float fallThreshold = -5f;

        Rigidbody _body;
        SteeringInput _input;
        Vector3 _spawnPoint;
        Vector2 _steering;

        /// <summary>Raised when the ball falls out of the arena.</summary>
        public event System.Action Fell;

        void Awake()
        {
            // Cache references in Awake so they are ready before anything else calls this component.
            _body = GetComponent<Rigidbody>();
            _spawnPoint = transform.position;

            var shorterEdge = Mathf.Min(Screen.width, Screen.height);
            _input = new SteeringInput(shorterEdge * dragRadiusFraction);

            if (steeringReference == null && Camera.main != null)
            {
                steeringReference = Camera.main.transform;
            }
        }

        void Update()
        {
            // Read input in Update, which runs once per frame. FixedUpdate can run zero or several
            // times per frame, so reading input there would drop or repeat gestures.
            _steering = _input.Read();
        }

        void FixedUpdate()
        {
            // Apply forces in FixedUpdate so acceleration does not depend on frame rate.
            var direction = ToWorldDirection(_steering);
            if (direction.sqrMagnitude > 0.0001f)
            {
                _body.AddForce(direction * acceleration, ForceMode.Acceleration);
            }

            ClampHorizontalSpeed();

            if (transform.position.y < fallThreshold)
            {
                ResetToSpawn();
                Fell?.Invoke();
            }
        }

        /// <summary>Puts the ball back where it started and kills all momentum.</summary>
        public void ResetToSpawn()
        {
            _body.position = _spawnPoint;
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
        }

        Vector3 ToWorldDirection(Vector2 steering)
        {
            if (steeringReference == null)
            {
                return new Vector3(steering.x, 0f, steering.y);
            }

            // Flatten the camera axes onto the ground. Otherwise a downward-angled camera would push
            // the ball into the floor when the player steers forward.
            var forward = Vector3.ProjectOnPlane(steeringReference.forward, Vector3.up).normalized;
            var right = Vector3.ProjectOnPlane(steeringReference.right, Vector3.up).normalized;

            return right * steering.x + forward * steering.y;
        }

        void ClampHorizontalSpeed()
        {
            // Cap only horizontal speed. Capping the full velocity would also slow the fall.
            var velocity = _body.linearVelocity;
            var horizontal = new Vector3(velocity.x, 0f, velocity.z);

            if (horizontal.sqrMagnitude <= maxSpeed * maxSpeed)
            {
                return;
            }

            horizontal = horizontal.normalized * maxSpeed;
            _body.linearVelocity = new Vector3(horizontal.x, velocity.y, horizontal.z);
        }
    }
}
