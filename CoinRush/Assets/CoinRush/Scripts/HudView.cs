using UnityEngine;
using UnityEngine.UI;

namespace CoinRush
{
    /// <summary>
    /// The on-screen readout: coins, lives, and a banner when the run ends.
    ///
    /// The whole canvas is built in <c>Awake</c> rather than authored in the scene. Three labels do not
    /// justify the click-through of a Canvas, a scaler, three rect transforms and their anchors — and
    /// every one of those is a place to mis-set a value that only shows up on a different aspect ratio.
    /// Built in code it is the same on every device and it is reviewable in the diff.
    ///
    /// It uses the built-in legacy font on purpose: TextMeshPro needs its essential resources imported
    /// into the project before it will render a single character, and that is a dialog box standing
    /// between a fresh clone and a running game.
    /// </summary>
    [RequireComponent(typeof(LevelController))]
    public sealed class HudView : MonoBehaviour
    {
        const int ReferenceWidth = 1080;
        const int ReferenceHeight = 2340;

        LevelController _level;
        Text _coins;
        Text _lives;
        Text _banner;

        void Awake()
        {
            _level = GetComponent<LevelController>();
            Build();
        }

        void OnEnable()
        {
            _level.CoinsChanged += OnCoinsChanged;
            _level.LivesChanged += OnLivesChanged;
            _level.PhaseChanged += OnPhaseChanged;
        }

        void OnDisable()
        {
            _level.CoinsChanged -= OnCoinsChanged;
            _level.LivesChanged -= OnLivesChanged;
            _level.PhaseChanged -= OnPhaseChanged;
        }

        void OnCoinsChanged(int value) => _coins.text = $"COINS  {value}";

        void OnLivesChanged(int value) => _lives.text = $"LIVES  {value}";

        void OnPhaseChanged(LevelPhase phase)
        {
            switch (phase)
            {
                case LevelPhase.Completed:
                    _banner.text = "LEVEL COMPLETE\n<tap to play again>";
                    break;
                case LevelPhase.GameOver:
                    _banner.text = "OUT OF LIVES\n<tap to try again>";
                    break;
                default:
                    _banner.text = string.Empty;
                    break;
            }
        }

        void Build()
        {
            var canvasObject = new GameObject("HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, worldPositionStays: false);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Scale with the screen rather than sizing in raw pixels, or the text is thumbnail-sized on
            // a high-density phone. Match 0.5 splits the difference between width and height so the HUD
            // survives both portrait and a landscape device held sideways.
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
            scaler.matchWidthOrHeight = 0.5f;

            _coins = CreateLabel(canvasObject.transform, "Coins", TextAnchor.UpperLeft,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(0.5f, 1f),
                offsetMin: new Vector2(48f, -160f), offsetMax: new Vector2(0f, -48f), fontSize: 54);

            _lives = CreateLabel(canvasObject.transform, "Lives", TextAnchor.UpperRight,
                anchorMin: new Vector2(0.5f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(0f, -160f), offsetMax: new Vector2(-48f, -48f), fontSize: 54);

            _banner = CreateLabel(canvasObject.transform, "Banner", TextAnchor.MiddleCenter,
                anchorMin: new Vector2(0f, 0.5f), anchorMax: new Vector2(1f, 0.5f),
                offsetMin: new Vector2(48f, -200f), offsetMax: new Vector2(-48f, 200f), fontSize: 72);

            _banner.text = string.Empty;
        }

        static Text CreateLabel(Transform parent, string name, TextAnchor alignment,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, int fontSize)
        {
            var label = new GameObject(name, typeof(Text), typeof(Outline));
            label.transform.SetParent(parent, worldPositionStays: false);

            var rect = label.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            var text = label.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            // An outline, because white text over a bright amber ball on a dark floor is legible right
            // up until the ball rolls underneath it.
            var outline = label.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.75f);
            outline.effectDistance = new Vector2(3f, -3f);

            return text;
        }
    }
}
