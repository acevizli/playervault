using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// Follows the ball at a fixed offset without rotating.
    ///
    /// <see cref="BallController"/> steers relative to this camera, so rotating the camera would
    /// also rotate the controls while the player is steering. With a fixed angle, up on the screen
    /// always means the same direction.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class FollowCamera : MonoBehaviour
    {
        [SerializeField] Transform target;

        [Tooltip("Position relative to the target, in world space.")]
        [SerializeField] Vector3 offset = new Vector3(0f, 12f, -10f);

        [Tooltip("Roughly how long the camera takes to catch up. 0 is rigid, higher is looser.")]
        [SerializeField, Range(0f, 0.5f)] float smoothTime = 0.12f;

        [Tooltip("Narrowest slice of the floor, in world units, that stays visible either side of the " +
                 "ball. Unity's field of view is vertical, so a tall phone would otherwise see much " +
                 "less of the arena sideways than a wide Game view does. 0 turns this off.")]
        [SerializeField] float minVisibleWidth = 12f;

        Camera _camera;
        Vector3 _velocity;

        void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        void LateUpdate()
        {
            // LateUpdate runs after the ball has moved. Following in Update would use last frame's
            // position and make the ball jitter.
            if (target == null)
            {
                return;
            }

            transform.position = Vector3.SmoothDamp(
                transform.position,
                target.position + FramedOffset(),
                ref _velocity,
                smoothTime);
        }

        /// <summary>
        /// The offset, pulled back along its own direction far enough that
        /// <see cref="minVisibleWidth"/> fits across the screen. Moving back keeps the viewing angle,
        /// where widening the field of view would stretch the edges of a tall screen.
        /// </summary>
        Vector3 FramedOffset()
        {
            if (minVisibleWidth <= 0f || _camera.aspect <= 0f)
            {
                return offset;
            }

            var halfWidthPerUnit = Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * _camera.aspect;
            var distanceNeeded = minVisibleWidth * 0.5f / halfWidthPerUnit;
            var distance = offset.magnitude;

            return distanceNeeded > distance ? offset * (distanceNeeded / distance) : offset;
        }
    }
}
