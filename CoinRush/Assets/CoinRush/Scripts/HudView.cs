using System.Text;
using PlayerVault;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace CoinRush
{
    /// <summary>
    /// The HUD: balances, the current level, the reward claim status, the level select (which is
    /// also the shop) and a panel that shows everything in the vault.
    ///
    /// The canvas is built in code in <see cref="Awake"/> instead of in the scene, which avoids
    /// setting up each widget by hand. It uses the built-in legacy font because TextMeshPro shows
    /// an import dialog on a fresh clone before it can render text.
    /// </summary>
    [RequireComponent(typeof(LevelController))]
    public sealed class HudView : MonoBehaviour
    {
        const int ReferenceWidth = 1080;
        const int ReferenceHeight = 2340;
        const float NoticeSeconds = 2.5f;

        /// <summary>
        /// How long a granted reward message stays on screen. Kept short because the new balance
        /// is already on the top bar. Pending and failed claims stay until they change.
        /// </summary>
        const float ClaimSeconds = 1f;
        const float PanelRefreshSeconds = 0.5f;
        const float RowHeight = 150f;
        const float RowPitch = 174f;

        /// <summary>
        /// How far a full-bleed backdrop reaches past the safe area, in reference units.
        /// </summary>
        /// <remarks>
        /// Widgets sit inside the safe area so nothing is hidden behind a notch, but the backgrounds
        /// behind them must reach the edge of the screen. A child RectTransform can extend past its
        /// parent (uGUI only clips under a Mask), so the backgrounds overshoot by a large margin
        /// instead of measuring the inset. 1000 units is larger than any cutout.
        /// </remarks>
        const float Bleed = 1000f;

        static readonly Color Ink = new Color(0.96f, 0.97f, 1f);
        static readonly Color Dim = new Color(0.62f, 0.68f, 0.78f);
        static readonly Color Gold = new Color(1f, 0.82f, 0.28f);
        static readonly Color Warn = new Color(0.98f, 0.76f, 0.35f);
        static readonly Color Bad = new Color(1f, 0.44f, 0.4f);
        static readonly Color Accent = new Color(0.16f, 0.62f, 0.98f);
        static readonly Color Panel = new Color(0.05f, 0.07f, 0.11f, 0.72f);
        static readonly Color Scrim = new Color(0.03f, 0.04f, 0.07f, 0.94f);
        static readonly Color Owned = new Color(0.13f, 0.28f, 0.42f);
        static readonly Color Affordable = new Color(0.32f, 0.25f, 0.07f);
        static readonly Color Locked = new Color(0.11f, 0.13f, 0.18f);

        /// <summary>
        /// A button and the label on it.
        /// </summary>
        /// <remarks>
        /// The fill colour is set through <see cref="Selectable.colors"/>, not on the Image.
        /// Selectable tints its target graphic on every state change, so a colour set directly on
        /// the Image is overwritten. The default ColorBlock is white, which made the buttons white
        /// and hid their white labels.
        /// </remarks>
        sealed class ButtonView
        {
            public Button Button;
            public Image Fill;
            public Text Label;

            public void SetInteractable(bool interactable, Color on, Color off)
            {
                Button.interactable = interactable;
                Tint(interactable ? on : off);
                Label.color = interactable ? Color.white : Dim;
            }

            public void Tint(Color fill)
            {
                var colors = Button.colors;
                colors.normalColor = fill;
                colors.highlightedColor = fill;
                colors.selectedColor = fill;
                colors.pressedColor = new Color(fill.r * 0.72f, fill.g * 0.72f, fill.b * 0.72f, fill.a);
                colors.disabledColor = fill;   // the caller passes the colour for the current state
                colors.colorMultiplier = 1f;
                colors.fadeDuration = 0.06f;
                Button.colors = colors;
            }
        }

        /// <summary>One row of the level select: the button, the level name and its status.</summary>
        sealed class LevelRow
        {
            public ButtonView View;
            public Text State;
        }

        LevelController _level;

        Text _coins;
        Text _lives;
        Text _levelLabel;
        Text _pending;
        Text _banner;
        Text _claim;
        Text _notice;
        Text _vaultText;

        ButtonView _levelsButton;
        ButtonView _vaultToggle;
        ButtonView _retry;
        Button _tapCatcher;

        LevelRow[] _rows;

        GameObject _menuPanel;
        GameObject _vaultPanel;
        GameObject _blockedPanel;
        float _noticeUntil;
        float _claimUntil;
        float _panelRefreshAt;

        void Awake()
        {
            _level = GetComponent<LevelController>();
            EnsureEventSystem();
            Build();
        }

        void OnEnable()
        {
            _level.CoinsChanged += OnCoinsChanged;
            _level.LivesChanged += OnLivesChanged;
            _level.PhaseChanged += OnPhaseChanged;
            _level.ClaimChanged += OnClaimChanged;
            _level.LevelChanged += OnLevelChanged;
            _level.PendingClaimsChanged += OnPendingClaimsChanged;
            _level.RetryOfferChanged += OnRetryOfferChanged;
            _level.UnlocksChanged += RefreshMenu;
            _level.Notice += OnNotice;
            _level.Blocked += OnBlocked;
        }

        void OnDisable()
        {
            _level.CoinsChanged -= OnCoinsChanged;
            _level.LivesChanged -= OnLivesChanged;
            _level.PhaseChanged -= OnPhaseChanged;
            _level.ClaimChanged -= OnClaimChanged;
            _level.LevelChanged -= OnLevelChanged;
            _level.PendingClaimsChanged -= OnPendingClaimsChanged;
            _level.RetryOfferChanged -= OnRetryOfferChanged;
            _level.UnlocksChanged -= RefreshMenu;
            _level.Notice -= OnNotice;
            _level.Blocked -= OnBlocked;
        }

        void Update()
        {
            if (_notice.text.Length > 0 && Time.unscaledTime > _noticeUntil)
            {
                _notice.text = string.Empty;
            }

            if (_claimUntil > 0f && Time.unscaledTime > _claimUntil)
            {
                _claim.text = string.Empty;
                _claimUntil = 0f;
            }

            if (_vaultPanel.activeSelf && Time.unscaledTime >= _panelRefreshAt)
            {
                _panelRefreshAt = Time.unscaledTime + PanelRefreshSeconds;
                RefreshVaultPanel();
            }
        }

        // ------------------------------------------------------------------ model -> view

        void OnCoinsChanged(long value)
        {
            _coins.text = $"COINS  {value}";

            // The menu is also the shop, so any coin change can make a level affordable. Redraw
            // every row and let CanSpend decide.
            RefreshMenu();
        }

        /// <summary>
        /// Shows the maximum next to the balance, so the player can see when a grant will be
        /// clamped.
        /// </summary>
        void OnLivesChanged(long value)
        {
            var max = _level.LivesMax;
            _lives.text = max.HasValue ? $"LIVES  {value}/{max.Value}" : $"LIVES  {value}";
        }

        void OnLevelChanged(LevelDefinition level)
        {
            _levelLabel.text = level == null
                ? string.Empty
                : $"LEVEL {_level.CurrentIndex + 1}/{_level.LevelCount}  {level.name.ToUpperInvariant()}";
        }

        void OnPendingClaimsChanged(int count) => RefreshPendingRow();

        void OnRetryOfferChanged(bool offered) => RefreshPendingRow();

        /// <summary>
        /// The number of pending claims, always visible. The unlock button stays usable while this
        /// is above zero, because pending claims do not reserve any balance.
        /// </summary>
        /// <remarks>
        /// While a retry is offered, the line is amber with the button beside it. After a retry that
        /// changed nothing, the button is removed and the line turns grey. The game does not wait on
        /// either, because the reward is already saved.
        /// </remarks>
        void RefreshPendingRow()
        {
            var count = _level.PendingClaimCount;

            if (count == 0)
            {
                _pending.text = string.Empty;
                SetRetryVisible(false);
                return;
            }

            _pending.text = count == 1 ? "1 REWARD PENDING" : $"{count} REWARDS PENDING";
            _pending.color = _level.RetryOffered ? Warn : Dim;
            SetRetryVisible(_level.RetryOffered);
        }

        /// <summary>
        /// Shows or hides the retry button. When it is hidden, VAULT takes the full bottom row so
        /// there is no empty gap.
        /// </summary>
        void SetRetryVisible(bool visible)
        {
            _retry.Button.gameObject.SetActive(visible);

            var rect = _vaultToggle.Fill.rectTransform;
            rect.anchorMin = new Vector2(visible ? 0.5f : 0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.offsetMin = new Vector2(visible ? 12f : 72f, 140f);
            rect.offsetMax = new Vector2(-72f, 270f);
        }

        void OnPhaseChanged(LevelPhase phase)
        {
            switch (phase)
            {
                case LevelPhase.Booting:
                    _banner.text = "OPENING VAULT";
                    break;

                case LevelPhase.Completed:
                    _banner.text = "LEVEL COMPLETE\nTAP TO PLAY AGAIN";
                    break;

                case LevelPhase.GameOver:
                    _banner.text = "OUT OF LIVES\nTAP TO TRY AGAIN";
                    break;

                default:
                    _banner.text = string.Empty;
                    break;
            }

            // Clear the previous run's reward line when a new run starts.
            if (phase == LevelPhase.Menu || phase == LevelPhase.Playing)
            {
                ClearClaim();
            }

            if (phase == LevelPhase.Menu)
            {
                _levelLabel.text = string.Empty;
            }

            _menuPanel.SetActive(phase == LevelPhase.Menu);
            if (phase == LevelPhase.Menu) RefreshMenu();

            // The replay button and the menu button are only active between runs.
            var between = phase == LevelPhase.Completed || phase == LevelPhase.GameOver;
            _tapCatcher.gameObject.SetActive(between);
            _levelsButton.Button.gameObject.SetActive(between);
        }

        /// <summary>
        /// Shows the claim status, including the pending, clamped and failed cases.
        /// </summary>
        void OnClaimChanged(ClaimStatus status, ClaimRecord record)
        {
            if (record == null)
            {
                ClearClaim();
                return;
            }

            // Use the result status, not the record's. On a repeat clear the result is
            // AlreadyGranted while the record still says Granted, so switching on the record would
            // show "+100 COINS" every time.
            _claimUntil = status == ClaimStatus.Granted || status == ClaimStatus.AlreadyGranted
                ? Time.unscaledTime + ClaimSeconds
                : 0f;

            switch (status)
            {
                case ClaimStatus.Granted:
                    _claim.text = record.WasClamped
                        ? $"REWARD +{record.AmountApplied} (CAPPED FROM {record.AmountRequested})"
                        : $"REWARD +{record.AmountApplied} COINS";
                    _claim.color = Gold;
                    break;

                case ClaimStatus.AlreadyGranted:
                    _claim.text = "REWARD ALREADY CLAIMED";
                    _claim.color = Dim;
                    break;

                case ClaimStatus.Pending:
                    // Pending is not an error. The claim is saved and will be retried on the next
                    // launch.
                    _claim.text = $"REWARD PENDING - WILL RETRY ({record.Attempts} ATTEMPTS)";
                    _claim.color = Warn;
                    break;

                case ClaimStatus.Failed:
                    // Show which failure it was, so "network down" and "server refused" read
                    // differently.
                    _claim.text = DescribeFailure(record.Failure);
                    _claim.color = Bad;
                    break;
            }
        }

        void ClearClaim()
        {
            _claim.text = string.Empty;
            _claimUntil = 0f;
        }

        static string DescribeFailure(ClaimFailure failure)
        {
            switch (failure)
            {
                case ClaimFailure.Rejected: return "REWARD REFUSED BY THE SERVER";
                case ClaimFailure.Network: return "REWARD UNREACHABLE - RETRY AT NEXT LAUNCH";
                case ClaimFailure.Parse: return "REWARD RESPONSE NOT UNDERSTOOD";
                case ClaimFailure.Invalid: return "REWARD REQUEST WAS MALFORMED";
                case ClaimFailure.Cancelled: return "REWARD CANCELLED - WILL RETRY";
                default: return "REWARD FAILED";
            }
        }

        void OnNotice(string message)
        {
            _notice.text = message;
            _noticeUntil = Time.unscaledTime + NoticeSeconds;
        }

        // ------------------------------------------------------------------ level select

        /// <summary>
        /// Redraws every row of the level select. Owned levels can be played, locked ones show
        /// their price, and <see cref="Vault.CanSpend"/> decides which prices are affordable.
        /// </summary>
        void RefreshMenu()
        {
            if (_rows == null) return;

            for (var i = 0; i < _rows.Length; i++)
            {
                var row = _rows[i];
                row.View.Label.text = $"{i + 1}   {_level.LevelAt(i).name.ToUpperInvariant()}";

                if (_level.IsUnlocked(i))
                {
                    row.View.SetInteractable(true, i == _level.CurrentIndex ? Accent : Owned, Owned);
                    row.State.text = "PLAY";
                    row.State.color = Ink;
                    continue;
                }

                var cost = _level.UnlockCostOf(i);
                var affordable = _level.CanAfford(i);

                row.View.SetInteractable(affordable, Affordable, Locked);
                row.State.text = affordable ? $"BUY  {cost}" : $"{cost} COINS";
                row.State.color = affordable ? Gold : Dim;
            }
        }

        /// <summary>
        /// A tap on a locked row buys the level; a tap on an owned row plays it. After a purchase
        /// the new level starts.
        /// </summary>
        void OnLevelRowClicked(int index)
        {
            if (_level.IsUnlocked(index)) _level.SelectLevel(index);
            else _level.TryUnlock(index);
        }

        // ------------------------------------------------------------------ vault panel

        void ToggleVaultPanel()
        {
            var show = !_vaultPanel.activeSelf;
            _vaultPanel.SetActive(show);

            if (show)
            {
                _panelRefreshAt = Time.unscaledTime + PanelRefreshSeconds;
                RefreshVaultPanel();
            }
        }

        /// <summary>
        /// Shows the vault contents without a debugger: the player id, every balance with its
        /// maximum, and every pending claim with its attempt count.
        /// </summary>
        void RefreshVaultPanel()
        {
            var vault = _level.Vault;
            if (vault == null)
            {
                _vaultText.text = "VAULT NOT OPEN YET";
                return;
            }

            var builder = new StringBuilder();
            builder.Append("PLAYER   ").AppendLine(vault.PlayerId);
            builder.AppendLine();
            builder.AppendLine("BALANCES");

            foreach (var entry in vault.Balances)
            {
                var max = vault.GetMax(entry.Key);
                builder.Append("  ").Append(entry.Key.ToUpperInvariant()).Append("  ").Append(entry.Value);
                if (max.HasValue) builder.Append(" / ").Append(max.Value);
                builder.AppendLine();
            }

            var pending = vault.PendingClaims;
            builder.AppendLine();
            builder.Append("PENDING CLAIMS  ").Append(pending.Count).AppendLine();

            foreach (var record in pending)
            {
                // Describe() gives more detail than the status alone, for example
                // "pending after 3 attempt(s) (Network), HTTP 429: Too Many Requests".
                builder.Append("  ").Append(record.RewardId)
                    .Append("  +").Append(record.AmountRequested).Append(' ').Append(record.Resource)
                    .AppendLine()
                    .Append("     ").AppendLine(record.Describe().ToUpperInvariant());
            }

            builder.AppendLine();
            builder.AppendLine("LEVELS");

            for (var i = 0; i < _level.LevelCount; i++)
            {
                var record = vault.GetClaim(_level.RewardIdFor(i));
                builder.Append("  ").Append(i + 1).Append("  ")
                    .Append(_level.IsUnlocked(i) ? "OWNED " : "LOCKED")
                    .Append("  REWARD ")
                    .AppendLine(record == null ? "NEVER" : record.Status.ToString().ToUpperInvariant());
            }

            _vaultText.text = builder.ToString();
        }

        // ------------------------------------------------------------------ construction

        /// <summary>
        /// uGUI buttons need an EventSystem. The HUD creates one because it builds itself in code.
        /// The input module sets up its default actions when enabled, so no input asset is needed.
        /// </summary>
        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;

            var events = new GameObject("EventSystem", typeof(EventSystem));
            events.AddComponent<InputSystemUIInputModule>();
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

            var screen = canvasObject.transform;

            // Built first so it is behind every other widget and they get taps first. It is parented
            // to the canvas, not the safe area, so taps next to the notch still count.
            _tapCatcher = CreateTapCatcher(screen, _level.RequestReplay);

            // Text and buttons are placed inside the safe area, so a notch or home indicator does not
            // cover them. The offsets below are relative to the safe area.
            var root = CreateSafeArea(screen);

            // A dark background behind the top rows, because white text is hard to read over the
            // bright floor. It extends past the safe area up to the top of the screen.
            CreatePanel(root, "TopBar", Panel,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(-Bleed, -280f), new Vector2(Bleed, Bleed));

            _coins = CreateLabel(root, "Coins", TextAnchor.UpperLeft,
                new Vector2(0f, 1f), new Vector2(0.5f, 1f),
                new Vector2(48f, -150f), new Vector2(0f, -40f), 54, Gold);

            _lives = CreateLabel(root, "Lives", TextAnchor.UpperRight,
                new Vector2(0.5f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -150f), new Vector2(-48f, -40f), 54, Ink);

            _levelLabel = CreateLabel(root, "Level", TextAnchor.UpperLeft,
                new Vector2(0f, 1f), new Vector2(0.55f, 1f),
                new Vector2(48f, -250f), new Vector2(0f, -160f), 38, Dim);

            _pending = CreateLabel(root, "Pending", TextAnchor.UpperRight,
                new Vector2(0.45f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -250f), new Vector2(-48f, -160f), 38, Warn);

            _banner = CreateLabel(root, "Banner", TextAnchor.MiddleCenter,
                new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(48f, -190f), new Vector2(-48f, 190f), 72, Ink);

            _claim = CreateLabel(root, "Claim", TextAnchor.MiddleCenter,
                new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(48f, -310f), new Vector2(-48f, -200f), 38, Gold);

            _notice = CreateLabel(root, "Notice", TextAnchor.LowerCenter,
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(48f, 440f), new Vector2(-48f, 530f), 40, Warn);

            _levelsButton = CreateButton(root, "Levels", "LEVELS", Accent,
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(72f, 290f), new Vector2(-72f, 420f), 42);
            _levelsButton.Button.onClick.AddListener(_level.ShowMenu);

            _retry = CreateButton(root, "Retry", "RETRY PENDING", Warn,
                new Vector2(0f, 0f), new Vector2(0.5f, 0f),
                new Vector2(72f, 140f), new Vector2(-12f, 270f), 34);
            _retry.Button.onClick.AddListener(_level.RetryPendingClaims);

            _vaultToggle = CreateButton(root, "VaultToggle", "VAULT", new Color(0.18f, 0.22f, 0.3f),
                new Vector2(0.5f, 0f), new Vector2(1f, 0f),
                new Vector2(12f, 140f), new Vector2(-72f, 270f), 34);
            _vaultToggle.Button.onClick.AddListener(ToggleVaultPanel);

            BuildMenu(root);
            BuildVaultPanel(root);
            BuildBlockedPanel(root);

            // The vault opens asynchronously, so show a loading message until it is ready.
            _banner.text = "OPENING VAULT";
            _claim.text = string.Empty;
            _notice.text = string.Empty;
            _pending.text = string.Empty;
            _levelLabel.text = string.Empty;
            _levelsButton.Button.gameObject.SetActive(false);
            _tapCatcher.gameObject.SetActive(false);
            SetRetryVisible(false);
        }

        /// <summary>
        /// The level select. It leaves the top bar and bottom buttons visible, so the coin count and
        /// the purchase messages can be seen while shopping.
        /// </summary>
        void BuildMenu(Transform root)
        {
            var panel = CreatePanel(root, "MenuPanel", Scrim,
                new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(0f, 560f), new Vector2(0f, -280f));

            panel.raycastTarget = true;
            _menuPanel = panel.gameObject;

            var title = CreateLabel(panel.transform, "Title", TextAnchor.UpperCenter,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(48f, -130f), new Vector2(-48f, -30f), 52, Ink);
            title.text = "SELECT A LEVEL";

            _rows = new LevelRow[_level.LevelCount];
            for (var i = 0; i < _rows.Length; i++)
            {
                _rows[i] = CreateLevelRow(panel.transform, i, 160f + i * RowPitch);
            }

            _menuPanel.SetActive(false);
        }

        LevelRow CreateLevelRow(Transform parent, int index, float top)
        {
            var view = CreateButton(parent, $"Level{index + 1}", string.Empty, Locked,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(48f, -(top + RowHeight)), new Vector2(-48f, -top), 42);

            // Leave room on the right for the status so a long level name does not overlap the price.
            view.Label.alignment = TextAnchor.MiddleLeft;
            view.Label.rectTransform.offsetMax = new Vector2(-300f, -8f);

            var state = CreateLabel(view.Button.transform, "State", TextAnchor.MiddleRight,
                Vector2.zero, Vector2.one, new Vector2(24f, 8f), new Vector2(-32f, -8f), 36, Ink);

            // Copy the loop variable so each listener gets its own value.
            var captured = index;
            view.Button.onClick.AddListener(() => OnLevelRowClicked(captured));

            return new LevelRow { View = view, State = state };
        }

        void BuildVaultPanel(Transform root)
        {
            // Built last so it is on top. The panel is an empty frame on the safe area and only its
            // background extends past it, so the title, text and close button stay on screen.
            var panel = CreateGroup(root, "VaultPanel",
                new Vector2(0f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);

            _vaultPanel = panel.gameObject;

            // Blocks taps from reaching the replay button behind it.
            var backdrop = CreatePanel(panel, "Backdrop", Scrim,
                new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(-Bleed, -Bleed), new Vector2(Bleed, Bleed));

            backdrop.raycastTarget = true;

            CreateLabel(panel, "Title", TextAnchor.UpperCenter,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(48f, -190f), new Vector2(-48f, -90f), 56, Ink).text = "VAULT";

            _vaultText = CreateLabel(panel, "Contents", TextAnchor.UpperLeft,
                new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(72f, 320f), new Vector2(-72f, -220f), 34, Ink);

            var close = CreateButton(panel, "Close", "CLOSE", new Color(0.18f, 0.22f, 0.3f),
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(72f, 140f), new Vector2(-72f, 270f), 40);
            close.Button.onClick.AddListener(ToggleVaultPanel);

            _vaultPanel.SetActive(false);
        }

        /// <summary>
        /// Shown when the save failed its tamper check. Built last so it covers everything,
        /// including the VAULT button, and its backdrop takes every tap. It has no buttons: the
        /// player stays blocked.
        /// </summary>
        void BuildBlockedPanel(Transform root)
        {
            var panel = CreateGroup(root, "BlockedPanel",
                new Vector2(0f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);

            _blockedPanel = panel.gameObject;

            CreatePanel(panel, "Backdrop", new Color(0.03f, 0.03f, 0.05f, 1f),
                new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(-Bleed, -Bleed), new Vector2(Bleed, Bleed)).raycastTarget = true;

            CreateLabel(panel, "Title", TextAnchor.LowerCenter,
                new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(48f, 120f), new Vector2(-48f, 360f), 72, Bad).text = "SAVE MODIFIED";

            CreateLabel(panel, "Message", TextAnchor.UpperCenter,
                new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(96f, -320f), new Vector2(-96f, 80f), 42, Ink).text =
                "Your save file was changed outside the game.\n\nYou are blocked from playing.";

            _blockedPanel.SetActive(false);
        }

        void OnBlocked()
        {
            _vaultPanel.SetActive(false);
            _menuPanel.SetActive(false);
            _banner.text = string.Empty;
            _blockedPanel.SetActive(true);
        }

        /// <summary>
        /// A transparent full-screen button. A transparent Image still receives raycasts (uGUI only
        /// alpha-tests with a sprite and a hit threshold), so the button works while invisible.
        /// </summary>
        static Button CreateTapCatcher(Transform parent, UnityEngine.Events.UnityAction onClick)
        {
            var image = CreatePanel(parent, "TapCatcher", new Color(0f, 0f, 0f, 0f),
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            image.raycastTarget = true;

            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;   // no tint, so it stays invisible
            button.onClick.AddListener(onClick);

            return button;
        }

        /// <summary>
        /// An invisible frame that follows <see cref="Screen.safeArea"/>. Children are inset
        /// automatically.
        /// </summary>
        static Transform CreateSafeArea(Transform parent)
        {
            var safe = CreateGroup(parent, "SafeArea",
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            safe.gameObject.AddComponent<SafeArea>();
            return safe;
        }

        /// <summary>An empty RectTransform used as a parent. Draws nothing.</summary>
        static Transform CreateGroup(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var group = new GameObject(name, typeof(RectTransform));
            group.transform.SetParent(parent, worldPositionStays: false);

            var rect = group.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            return group.transform;
        }

        static Image CreatePanel(Transform parent, string name, Color colour,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var panel = new GameObject(name, typeof(Image));
            panel.transform.SetParent(parent, worldPositionStays: false);

            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            var image = panel.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;

            return image;
        }

        static ButtonView CreateButton(Transform parent, string name, string label, Color fill,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, int fontSize)
        {
            // White, because the ColorBlock tint is multiplied by the Image colour.
            var image = CreatePanel(parent, name, Color.white, anchorMin, anchorMax, offsetMin, offsetMax);
            image.raycastTarget = true;

            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            var text = CreateLabel(image.transform, "Label", TextAnchor.MiddleCenter,
                Vector2.zero, Vector2.one, new Vector2(24f, 8f), new Vector2(-24f, -8f),
                fontSize, Color.white);

            // LEVELS, VAULT, RETRY and CLOSE only get their captions here.
            text.text = label;

            var view = new ButtonView { Button = button, Fill = image, Label = text };
            view.Tint(fill);
            return view;
        }

        static Text CreateLabel(Transform parent, string name, TextAnchor alignment,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax,
            int fontSize, Color colour)
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
            text.color = colour;
            text.raycastTarget = false;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            var outline = label.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.75f);
            outline.effectDistance = new Vector2(3f, -3f);

            return text;
        }
    }
}
