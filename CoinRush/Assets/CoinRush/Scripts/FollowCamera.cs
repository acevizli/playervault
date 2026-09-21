using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// Trails the ball at a fixed offset without ever rotating.
    ///
    /// The rotation is deliberately left alone. <see cref="BallController"/> steers relative to this
    /// camera, so a camera that swung around to look at the ball would rotate the control scheme under
    /// the player's thumb mid-roll. A fixed angle keeps "up the screen" meaning one thing forever.
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
            // LateUpdate, because the ball moves during FixedUpdate/Update. Following in Update would
            // chase last frame's position and the ball would visibly jitter against the background.
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
