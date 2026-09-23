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
    public sealed class FollowCamera : MonoBehaviour
    {
        [SerializeField] Transform target;

        [Tooltip("Position relative to the target, in world space.")]
        [SerializeField] Vector3 offset = new Vector3(0f, 12f, -10f);

        [Tooltip("Roughly how long the camera takes to catch up. 0 is rigid, higher is looser.")]
        [SerializeField, Range(0f, 0.5f)] float smoothTime = 0.12f;

        Vector3 _velocity;

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
                target.position + offset,
                ref _velocity,
                smoothTime);
        }
    }
}
