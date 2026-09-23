using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// Fits a RectTransform to the device's safe area: the part of the screen not covered by a
    /// notch, rounded corners, camera cutout or home indicator.
    /// </summary>
    /// <remarks>
    /// A ScreenSpaceOverlay canvas covers the whole display, so on a recent iPhone a HUD anchored
    /// to its edges ends up behind the notch and the home indicator. Unity reports the usable area
    /// in pixels as <see cref="Screen.safeArea"/>. This converts it to anchors, so the
    /// CanvasScaler still works and child widgets keep their offsets.
    ///
    /// Checked every frame instead of once at startup. The value changes on rotation, in iPad
    /// split view and when the Android gesture bar appears. On iOS it can also be the full screen
    /// for the first few frames after launch.
    /// </remarks>
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    public sealed class SafeArea : MonoBehaviour
    {
        RectTransform _rect;
        Rect _applied;
        int _width;
        int _height;

        void Awake()
        {
            _rect = GetComponent<RectTransform>();
            Apply();
        }

        void Update()
        {
            Apply();
        }

        void Apply()
        {
            var area = Screen.safeArea;
            var width = Screen.width;
            var height = Screen.height;

            if (width <= 0 || height <= 0) return;
            if (area == _applied && width == _width && height == _height) return;

            // Some Android devices report a safe area outside the screen during rotation or a
            // resolution change. Applying it would shrink the HUD to nothing or push it off
            // screen, so keep the last valid rect instead.
            if (area.width <= 0f || area.height <= 0f ||
                area.xMin < 0f || area.yMin < 0f ||
                area.xMax > width || area.yMax > height)
            {
                return;
            }

            _applied = area;
            _width = width;
            _height = height;

            _rect.anchorMin = new Vector2(area.xMin / width, area.yMin / height);
            _rect.anchorMax = new Vector2(area.xMax / width, area.yMax / height);
            _rect.offsetMin = Vector2.zero;
            _rect.offsetMax = Vector2.zero;
        }
    }
}
