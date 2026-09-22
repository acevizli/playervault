using PlayerVault;
using UnityEngine;
using UnityEngine.UI;

namespace CoinRush
{
    /// <summary>
    /// The heads-up display: coins, lives, a phase banner, and the state of the reward claim.
    ///
    /// The whole canvas is built in <see cref="Awake"/> rather than authored in the scene. Four labels
    /// do not justify hand-placing a Canvas, a scaler and four rect transforms, and each of those is a
    /// place to mis-set a value that only misbehaves on an aspect ratio nobody tested. It also uses the
    /// built-in legacy font: TextMeshPro refuses to render a character until its essential resources
    /// are imported, which puts a dialog box between a fresh clone and a running game.
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
        Text _claim;

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
            _level.ClaimChanged += OnClaimChanged;
        }

        void OnDisable()
        {
            _level.CoinsChanged -= OnCoinsChanged;
            _level.LivesChanged -= OnLivesChanged;
            _level.PhaseChanged -= OnPhaseChanged;
            _level.ClaimChanged -= OnClaimChanged;
        }

        void OnCoinsChanged(long value) => _coins.text = $"COINS  {value}";

        void OnLivesChanged(long value) => _lives.text = $"LIVES  {value}";

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
                    _claim.text = string.Empty;
                    break;
            }
        }

        /// <summary>
        /// Renders the claim's own account of itself. Every branch here is a state the case asks the
        /// SDK to expose, and showing them verbatim is how the integration stays honest — a HUD that
        /// only knew "granted" would quietly hide the pending and clamped cases.
        /// </summary>
        void OnClaimChanged(ClaimRecord record)
        {
            if (record == null)
            {
                _claim.text = string.Empty;
                return;
            }

            switch (record.Status)
            {
                case ClaimStatus.Granted:
                    _claim.text = record.WasClamped
                        ? $"REWARD +{record.AmountApplied} (capped from {record.AmountRequested})"
                        : $"REWARD +{record.AmountApplied} COINS";
                    _claim.color = new Color(1f, 0.85f, 0.25f);
                    break;

                case ClaimStatus.AlreadyGranted:
                    _claim.text = "REWARD ALREADY CLAIMED";
                    _claim.color = new Color(0.7f, 0.75f, 0.8f);
                    break;

                case ClaimStatus.Pending:
                    // Pending is not an error. The claim is durable, and the vault replays it on the
                    // next launch, so the player is told to expect it rather than to retry.
                    _claim.text = $"REWARD PENDING — WILL RETRY LATER ({record.Attempts} attempts)";
                    _claim.color = new Color(0.95f, 0.8f, 0.4f);
                    break;

                case ClaimStatus.Failed:
                    _claim.text = $"REWARD REFUSED ({record.Failure})";
                    _claim.color = new Color(1f, 0.45f, 0.4f);
                    break;
            }
        }

        void Build()
        {
            var canvasObject = new GameObject("HUD",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, worldPositionStays: false);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

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

            _claim = CreateLabel(canvasObject.transform, "Claim", TextAnchor.MiddleCenter,
                anchorMin: new Vector2(0f, 0.5f), anchorMax: new Vector2(1f, 0.5f),
                offsetMin: new Vector2(48f, -320f), offsetMax: new Vector2(-48f, -210f), fontSize: 40);

            _banner.text = string.Empty;
            _claim.text = string.Empty;
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

            var outline = label.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.75f);
            outline.effectDistance = new Vector2(3f, -3f);

            return text;
        }
    }
}
