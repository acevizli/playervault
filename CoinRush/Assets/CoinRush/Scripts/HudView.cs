using System.Text;
using PlayerVault;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace CoinRush
{
    /// <summary>
    /// The heads-up display: balances, the level being played, the state of the reward claim, the
    /// level select — which doubles as the shop — and an inspector panel showing the vault's whole
    /// contents.
    ///
    /// The canvas is built in <see cref="Awake"/> rather than authored in the scene. A dozen widgets
    /// do not justify hand-placing a Canvas, a scaler and a rect transform each, and every one of
    /// those is a place to mis-set a value that only misbehaves on an aspect ratio nobody tested. It
    /// also uses the built-in legacy font: TextMeshPro refuses to render a character until its
    /// essential resources are imported, which puts a dialog box between a fresh clone and a running
    /// game.
    /// </summary>
    [RequireComponent(typeof(LevelController))]
    public sealed class HudView : MonoBehaviour
    {
        const int ReferenceWidth = 1080;
        const int ReferenceHeight = 2340;
        const float NoticeSeconds = 2.5f;

        /// <summary>
        /// How long a settled reward stays on screen. Short on purpose: it is an event, not a
        /// status, and the balance it moved is already on the top bar. Pending and failed claims
        /// are not put on this timer — those are the ones worth reading.
        /// </summary>
        const float ClaimSeconds = 1f;
        const float PanelRefreshSeconds = 0.5f;
        const float RowHeight = 150f;
        const float RowPitch = 174f;

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
        /// The fill colour is set through <see cref="Selectable.colors"/> rather than on the Image.
        /// Selectable owns its target graphic: every state change cross-fades the canvas renderer
        /// back to whichever ColorBlock entry matches the current state, so a colour written
        /// straight to the Image is overwritten a frame later. The default block is white for every
        /// state, which is why these buttons flashed white and swallowed their own white labels.
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
                colors.disabledColor = fill;   // The caller already passed the colour for this state.
                colors.colorMultiplier = 1f;
                colors.fadeDuration = 0.06f;
                Button.colors = colors;
            }
        }

        /// <summary>One line of the level select: the button, its name, and its right-hand state.</summary>
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
            _level.UnlocksChanged += RefreshMenu;
            _level.Notice += OnNotice;
        }

        void OnDisable()
        {
            _level.CoinsChanged -= OnCoinsChanged;
            _level.LivesChanged -= OnLivesChanged;
            _level.PhaseChanged -= OnPhaseChanged;
            _level.ClaimChanged -= OnClaimChanged;
            _level.LevelChanged -= OnLevelChanged;
            _level.PendingClaimsChanged -= OnPendingClaimsChanged;
            _level.UnlocksChanged -= RefreshMenu;
            _level.Notice -= OnNotice;
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

            // The menu is also the shop, so every coin picked up can flip a row from unaffordable
            // to buyable. Asking the vault again is cheaper than tracking which rows might have
            // changed, and it keeps CanSpend the single authority on what the player can afford.
            RefreshMenu();
        }

        /// <summary>
        /// Renders the ceiling alongside the balance. The maximum is a configured fact the vault
        /// already knows, and a HUD that prints a bare "3" makes the cap invisible until the moment
        /// it silently swallows a grant.
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

        /// <summary>
        /// The count of claims the vault has not settled yet, shown permanently rather than only on
        /// the screen that produced them. It is the visible half of the guarantee the case asks for:
        /// the unlock button next to it stays live while this is non-zero, because a claim in flight
        /// reserves nothing.
        /// </summary>
        void OnPendingClaimsChanged(int count)
        {
            if (count == 0)
            {
                _pending.text = string.Empty;
                _retry.Button.gameObject.SetActive(false);
                return;
            }

            _pending.text = count == 1 ? "1 REWARD PENDING" : $"{count} REWARDS PENDING";
            _pending.color = Warn;
            _retry.Button.gameObject.SetActive(true);
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

            // A run starting, by either route, wipes the previous run's reward line. Nothing about
            // the last clear is worth saying while the ball is rolling again.
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

            // The replay catcher and the way back to the menu are only live between runs. While the
            // ball is rolling they would sit in front of nothing that matters, but leaving them
            // enabled is one more surface to reason about.
            var between = phase == LevelPhase.Completed || phase == LevelPhase.GameOver;
            _tapCatcher.gameObject.SetActive(between);
            _levelsButton.Button.gameObject.SetActive(between);
        }

        /// <summary>
        /// Renders the claim's own account of itself. Every branch here is a state the case asks the
        /// SDK to expose, and showing them verbatim is how the integration stays honest — a HUD that
        /// only knew "granted" would quietly hide the pending and clamped cases.
        /// </summary>
        void OnClaimChanged(ClaimStatus status, ClaimRecord record)
        {
            if (record == null)
            {
                ClearClaim();
                return;
            }

            // The status comes from the claim's outcome, not from the record. They differ on a
            // replay — the outcome is AlreadyGranted while the record it wraps still says Granted,
            // because that is the truth about the first time. Switching on the record would print
            // "+100 COINS" every single clear, which is the SDK's guarantee being contradicted by
            // the game that depends on it.
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
                    // Pending is not an error. The claim is durable, and the vault replays it on the
                    // next launch, so the player is told to expect it rather than to retry.
                    _claim.text = $"REWARD PENDING - WILL RETRY ({record.Attempts} ATTEMPTS)";
                    _claim.color = Warn;
                    break;

                case ClaimStatus.Failed:
                    // The failures are told apart rather than printed as one word. "The network was
                    // down" and "the server said no" are the same colour of red to a HUD that only
                    // prints record.Failure, and they are completely different news to a player.
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
        /// Redraws every row of the level select. Each row asks the vault what it is: owned levels
        /// play, locked ones show their price, and <see cref="Vault.CanSpend"/> — not a comparison
        /// written here — decides which of those prices is live. Rewriting all five is cheaper than
        /// working out which one changed, and it means there is one code path producing the state.
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
        /// One tap, two meanings — but never both. A locked row buys; an owned row plays. Buying
        /// does start the level it just bought, which is the only place the two meet, and that is a
        /// deliberate convenience rather than a tap doing something the label did not promise.
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
        /// Dumps everything the vault holds. This is the screen that makes the SDK's state
        /// inspectable without a debugger: every declared balance with its ceiling, the player id the
        /// save file is keyed by, and every claim that has not settled, with its attempt count.
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
                builder.Append("  ").Append(record.RewardId)
                    .Append("  +").Append(record.AmountRequested).Append(' ').Append(record.Resource)
                    .Append("  ").Append(record.Attempts).AppendLine(" ATTEMPTS");
            }

            builder.AppendLine();
            builder.AppendLine("LEVELS");

            for (var i = 0; i < _level.LevelCount; i++)
            {
                var record = vault.GetClaim(LevelController.RewardIdFor(i));
                builder.Append("  ").Append(i + 1).Append("  ")
                    .Append(_level.IsUnlocked(i) ? "OWNED " : "LOCKED")
                    .Append("  REWARD ")
                    .AppendLine(record == null ? "NEVER" : record.Status.ToString().ToUpperInvariant());
            }

            _vaultText.text = builder.ToString();
        }

        // ------------------------------------------------------------------ construction

        /// <summary>
        /// uGUI buttons do nothing without an EventSystem, and this scene has no reason to carry one
        /// in its hierarchy when the HUD that needs it builds itself. The input module assigns its
        /// own default actions on enable, so nothing here has to author an input asset.
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

            var root = canvasObject.transform;

            // Built first, so it sits at the back of the sibling order and every other widget wins
            // the raycast against it. This is the whole reason the replay is a uGUI button rather
            // than a pointer read: sorting, not frame ordering, decides who owns the tap.
            _tapCatcher = CreateTapCatcher(root, _level.RequestReplay);

            // A slab behind the top rows. Flat white text over a bright arena floor is unreadable at
            // exactly the moment the player is looking at the arena instead of the numbers.
            CreatePanel(root, "TopBar", Panel,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -280f), new Vector2(0f, 0f));

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
            _retry.Button.gameObject.SetActive(false);

            _vaultToggle = CreateButton(root, "VaultToggle", "VAULT", new Color(0.18f, 0.22f, 0.3f),
                new Vector2(0.5f, 0f), new Vector2(1f, 0f),
                new Vector2(12f, 140f), new Vector2(-72f, 270f), 34);
            _vaultToggle.Button.onClick.AddListener(ToggleVaultPanel);

            BuildMenu(root);
            BuildVaultPanel(root);

            // The vault opens asynchronously, so the first thing on screen is a statement that
            // something is happening rather than an empty arena the player cannot steer.
            _banner.text = "OPENING VAULT";
            _claim.text = string.Empty;
            _notice.text = string.Empty;
            _pending.text = string.Empty;
            _levelLabel.text = string.Empty;
            _levelsButton.Button.gameObject.SetActive(false);
            _tapCatcher.gameObject.SetActive(false);
        }

        /// <summary>
        /// The level select. It deliberately stops short of the top bar and the bottom button row
        /// rather than covering the screen: coins stay readable while shopping, and the notice line
        /// that says why a purchase was refused has to be visible at the moment it is refused.
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

            // The shared label is pushed left and given room on the right for the state, so a long
            // level name cannot run underneath its own price.
            view.Label.alignment = TextAnchor.MiddleLeft;
            view.Label.rectTransform.offsetMax = new Vector2(-300f, -8f);

            var state = CreateLabel(view.Button.transform, "State", TextAnchor.MiddleRight,
                Vector2.zero, Vector2.one, new Vector2(24f, 8f), new Vector2(-32f, -8f), 36, Ink);

            // Captured once: the loop variable would otherwise be shared by every listener.
            var captured = index;
            view.Button.onClick.AddListener(() => OnLevelRowClicked(captured));

            return new LevelRow { View = view, State = state };
        }

        void BuildVaultPanel(Transform root)
        {
            // Built last so it sits on top of everything else in sibling order, and opaque enough to
            // swallow taps that would otherwise reach the replay handler behind it.
            var panel = CreatePanel(root, "VaultPanel", Scrim,
                new Vector2(0f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);

            panel.raycastTarget = true;
            _vaultPanel = panel.gameObject;

            CreateLabel(panel.transform, "Title", TextAnchor.UpperCenter,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(48f, -190f), new Vector2(-48f, -90f), 56, Ink).text = "VAULT";

            _vaultText = CreateLabel(panel.transform, "Contents", TextAnchor.UpperLeft,
                new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(72f, 320f), new Vector2(-72f, -220f), 34, Ink);

            var close = CreateButton(panel.transform, "Close", "CLOSE", new Color(0.18f, 0.22f, 0.3f),
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(72f, 140f), new Vector2(-72f, 270f), 40);
            close.Button.onClick.AddListener(ToggleVaultPanel);

            _vaultPanel.SetActive(false);
        }

        /// <summary>
        /// A transparent, full-screen button. A fully transparent Image still takes raycasts — uGUI
        /// only alpha-tests when a sprite and a hit threshold are set — so nothing has to be visible
        /// for the tap to land.
        /// </summary>
        static Button CreateTapCatcher(Transform parent, UnityEngine.Events.UnityAction onClick)
        {
            var image = CreatePanel(parent, "TapCatcher", new Color(0f, 0f, 0f, 0f),
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            image.raycastTarget = true;

            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;   // No tint: the catcher must stay invisible.
            button.onClick.AddListener(onClick);

            return button;
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
            // White, deliberately: the ColorBlock tint multiplies the Image's own colour, so the
            // Image has to be the identity or every state comes out doubled.
            var image = CreatePanel(parent, name, Color.white, anchorMin, anchorMax, offsetMin, offsetMax);
            image.raycastTarget = true;

            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            var text = CreateLabel(image.transform, "Label", TextAnchor.MiddleCenter,
                Vector2.zero, Vector2.one, new Vector2(24f, 8f), new Vector2(-24f, -8f),
                fontSize, Color.white);

            // Every button that is not refreshed from state — LEVELS, VAULT, RETRY, CLOSE — got its
            // caption from here and nowhere else, so dropping this line left them blank.
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
