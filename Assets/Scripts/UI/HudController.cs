using System;
using System.Collections;
using System.Globalization;
using SlotMachine.Audio;
using SlotMachine.Core;
using SlotMachine.Reels;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace SlotMachine.UI
{
    /// <summary>
    /// Presentation layer for the machine: balance count-up, bet selector, spin/auto/paytable
    /// buttons, free-spin badge, win banner, and modal orchestration. The HUD never mutates game
    /// rules directly; it calls the controller API and reacts to controller events.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HudController : MonoBehaviour
    {
        [Header("Machine")]
        [SerializeField] private SlotMachineController _machine;

        [Header("Balance & Bet")]
        [SerializeField] private Text _balanceText;
        [SerializeField] private Text _betText;

        [Header("Buttons")]
        [SerializeField] private Button _spinButton;
        [SerializeField] private Text _spinButtonLabel;
        [SerializeField] private Button _betDecreaseButton;
        [SerializeField] private Button _betIncreaseButton;
        [SerializeField] private Button _autoSpinButton;
        [SerializeField] private Text _autoSpinLabel;
        [SerializeField] private Button _paytableButton;

        [Header("Spin History")]
        [SerializeField] private SpinHistoryCard _historyCard;

        [Header("Banners")]
        [SerializeField] private CanvasGroup _winBannerGroup;
        [SerializeField] private Image _winBannerBackground;
        [SerializeField] private Text _winBannerText;
        [SerializeField] private GameObject _freeSpinBadge;
        [SerializeField] private Text _freeSpinText;

        [Header("Modals")]
        [SerializeField] private PaytableModal _paytableModal;
        [SerializeField] private ConfirmResetModal _confirmResetModal;

        [Header("Tuning")]
        [SerializeField, Min(0.01f)] private float _balanceCountDuration = 0.55f;
        [SerializeField, Min(0.1f)] private float _winBannerHold = 2.2f;
        [SerializeField] private Color _winTextColor = new Color(1f, 0.97f, 0.84f, 1f);
        [SerializeField] private Color _jackpotTextColor = new Color(1f, 0.78f, 0.22f, 1f);
        [SerializeField] private Color _lossTextColor = new Color(0.72f, 0.76f, 0.86f, 1f);

        private int _displayedBalance;
        private Coroutine _balanceRoutine;
        private Coroutine _bannerRoutine;

        private void Start()
        {
            if (_machine == null || _balanceText == null || _spinButton == null)
            {
                Debug.LogError("HUD is missing required references.", this);
                enabled = false;
                return;
            }

            _displayedBalance = _machine.Balance;
            _balanceText.text = FormatCredits(_displayedBalance);
            UpdateBetLabel();
            UpdateFreeSpins(_machine.FreeSpinsRemaining);
            UpdateAutoSpinLabel(_machine.AutoSpinEnabled);
            HideWinBannerImmediate();
            RefreshHistory();

            _spinButton.onClick.AddListener(OnSpinClicked);
            _betDecreaseButton.onClick.AddListener(OnDecreaseBet);
            _betIncreaseButton.onClick.AddListener(OnIncreaseBet);
            _autoSpinButton.onClick.AddListener(OnAutoSpinClicked);
            _paytableButton.onClick.AddListener(OnPaytableClicked);

            _machine.OnBalanceChanged += HandleBalanceChanged;
            _machine.OnBetChanged += HandleBetChanged;
            _machine.OnSpinStarted += HandleSpinStarted;
            _machine.OnSpinCompleted += HandleSpinCompleted;
            _machine.OnFreeSpinsChanged += HandleFreeSpinsChanged;
            _machine.OnAutoSpinChanged += HandleAutoSpinChanged;
            _machine.OnInsufficientFunds += HandleInsufficientFunds;
            _machine.OnHistoryChanged += RefreshHistory;

            if (_paytableModal != null)
            {
                _paytableModal.Closed += HandlePaytableClosed;
            }

            if (_confirmResetModal != null)
            {
                _confirmResetModal.Confirmed += HandleResetConfirmed;
                _confirmResetModal.Cancelled += HandleResetCancelled;
            }

            StartCoroutine(LowCreditCheckRoutine());
        }

        private void OnDestroy()
        {
            if (_machine != null)
            {
                _machine.OnBalanceChanged -= HandleBalanceChanged;
                _machine.OnBetChanged -= HandleBetChanged;
                _machine.OnSpinStarted -= HandleSpinStarted;
                _machine.OnSpinCompleted -= HandleSpinCompleted;
                _machine.OnFreeSpinsChanged -= HandleFreeSpinsChanged;
                _machine.OnAutoSpinChanged -= HandleAutoSpinChanged;
                _machine.OnInsufficientFunds -= HandleInsufficientFunds;
                _machine.OnHistoryChanged -= RefreshHistory;
            }

            if (_paytableModal != null)
            {
                _paytableModal.Closed -= HandlePaytableClosed;
            }

            if (_confirmResetModal != null)
            {
                _confirmResetModal.Confirmed -= HandleResetConfirmed;
                _confirmResetModal.Cancelled -= HandleResetCancelled;
            }

            if (_spinButton != null)
            {
                _spinButton.onClick.RemoveListener(OnSpinClicked);
                _betDecreaseButton.onClick.RemoveListener(OnDecreaseBet);
                _betIncreaseButton.onClick.RemoveListener(OnIncreaseBet);
                _autoSpinButton.onClick.RemoveListener(OnAutoSpinClicked);
                _paytableButton.onClick.RemoveListener(OnPaytableClicked);
            }
        }

        private void Update()
        {
            if (_machine == null)
            {
                return;
            }

            if (_machine.State == GameState.Idle && SpacebarPressed())
            {
                OnSpinClicked();
            }

            RefreshInteractables();
        }

        private void RefreshInteractables()
        {
            bool idle = _machine.State == GameState.Idle;
            _spinButton.interactable = _machine.CanSpin;
            _betDecreaseButton.interactable = idle && _machine.CanDecreaseBet;
            _betIncreaseButton.interactable = idle && _machine.CanIncreaseBet;
            _autoSpinButton.interactable = _machine.State != GameState.ModalOpen;
            _paytableButton.interactable = idle;

            if (_spinButtonLabel != null)
            {
                bool freeSpin = _machine.FreeSpinsRemaining > 0;
                _spinButtonLabel.text = freeSpin ? "FREE SPIN" : "SPIN";
                _spinButtonLabel.fontSize = freeSpin ? 26 : 38;
            }
        }

        private void OnSpinClicked()
        {
            if (_machine.State != GameState.Idle)
            {
                return;
            }

            ProceduralSfxController.PlayButtonClickGlobal();
            _machine.RequestSpin();
        }

        private void OnDecreaseBet()
        {
            ProceduralSfxController.PlayButtonClickGlobal();
            _machine.ChangeBet(-1);
        }

        private void OnIncreaseBet()
        {
            ProceduralSfxController.PlayButtonClickGlobal();
            _machine.ChangeBet(1);
        }

        private void OnAutoSpinClicked()
        {
            ProceduralSfxController.PlayButtonClickGlobal();
            _machine.ToggleAutoSpin();
        }

        private void OnPaytableClicked()
        {
            if (_machine.State != GameState.Idle || _paytableModal == null)
            {
                return;
            }

            if (_machine.OpenModal())
            {
                ProceduralSfxController.PlayButtonClickGlobal();
                _paytableModal.Show();
            }
        }

        private void HandlePaytableClosed()
        {
            _machine.CloseModal();
        }

        private void HandleResetConfirmed()
        {
            _machine.ResetBalance();
            _confirmResetModal.Hide();
            _machine.CloseModal();
        }

        private void HandleResetCancelled()
        {
            _confirmResetModal.Hide();
            _machine.CloseModal();
        }

        private void HandleBalanceChanged(int balance)
        {
            if (_balanceRoutine != null)
            {
                StopCoroutine(_balanceRoutine);
            }

            _balanceRoutine = StartCoroutine(CountBalanceRoutine(_displayedBalance, balance));
        }

        private IEnumerator CountBalanceRoutine(int from, int to)
        {
            float elapsed = 0f;
            while (elapsed < _balanceCountDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / _balanceCountDuration);
                t = t * t * (3f - (2f * t));
                _displayedBalance = Mathf.RoundToInt(Mathf.Lerp(from, to, t));
                _balanceText.text = FormatCredits(_displayedBalance);
                yield return null;
            }

            _displayedBalance = to;
            _balanceText.text = FormatCredits(_displayedBalance);
            _balanceRoutine = null;
        }

        private void HandleBetChanged(int betIndex)
        {
            UpdateBetLabel();
        }

        private void UpdateBetLabel()
        {
            if (_betText != null && _machine != null)
            {
                _betText.text = FormatCredits(_machine.Bet);
            }
        }

        private void HandleSpinStarted()
        {
            HideWinBannerImmediate();
            RefreshInteractables();
        }

        private void RefreshHistory()
        {
            if (_historyCard != null && _machine != null)
            {
                _historyCard.Refresh(_machine.RecentHistory, _machine.SessionNet);
            }
        }

        private void HandleSpinCompleted(PayoutResult result)
        {
            RefreshHistory();
            if (_bannerRoutine != null)
            {
                StopCoroutine(_bannerRoutine);
            }

            _bannerRoutine = StartCoroutine(ShowWinBannerRoutine(result));
        }

        private IEnumerator ShowWinBannerRoutine(PayoutResult result)
        {
            _winBannerGroup.gameObject.SetActive(true);
            _winBannerGroup.alpha = 0f;
            _winBannerGroup.transform.localScale = Vector3.one * 0.86f;

            bool jackpot = result.Tier == WinTier.Jackpot;
            bool hasWin = result.IsWin;
            _winBannerText.fontSize = jackpot ? 18 : 23;
            _winBannerText.color = jackpot ? _jackpotTextColor : hasWin ? _winTextColor : _lossTextColor;
            _winBannerText.text = hasWin
                ? $"{result.Description}\n+{FormatCredits(result.Payout)} CREDITS"
                : "NO WIN · TRY AGAIN";

            _winBannerBackground.color = jackpot ? new Color(1f, 0.82f, 0.44f, 1f) : Color.white;

            float fadeIn = 0.18f;
            float elapsed = 0f;
            while (elapsed < fadeIn)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / fadeIn);
                _winBannerGroup.alpha = t;
                _winBannerGroup.transform.localScale = Vector3.one * Mathf.Lerp(0.86f, 1f, t);
                yield return null;
            }

            _winBannerGroup.alpha = 1f;
            _winBannerGroup.transform.localScale = Vector3.one;

            yield return new WaitForSecondsRealtime(hasWin ? _winBannerHold : 1.2f);

            float fadeOut = 0.3f;
            elapsed = 0f;
            while (elapsed < fadeOut)
            {
                elapsed += Time.unscaledDeltaTime;
                _winBannerGroup.alpha = 1f - Mathf.Clamp01(elapsed / fadeOut);
                yield return null;
            }

            _winBannerGroup.gameObject.SetActive(false);
            _bannerRoutine = null;
        }

        private void HideWinBannerImmediate()
        {
            if (_bannerRoutine != null)
            {
                StopCoroutine(_bannerRoutine);
                _bannerRoutine = null;
            }

            if (_winBannerGroup != null)
            {
                _winBannerGroup.alpha = 0f;
                _winBannerGroup.gameObject.SetActive(false);
            }
        }

        private void HandleFreeSpinsChanged(int remaining)
        {
            UpdateFreeSpins(remaining);
        }

        private void UpdateFreeSpins(int remaining)
        {
            bool visible = remaining > 0;
            if (_freeSpinBadge != null)
            {
                _freeSpinBadge.SetActive(visible);
            }

            if (_freeSpinText != null)
            {
                _freeSpinText.text = $"FREE SPINS  {remaining}";
            }
        }

        private void HandleAutoSpinChanged(bool enabled)
        {
            UpdateAutoSpinLabel(enabled);
        }

        private void UpdateAutoSpinLabel(bool enabled)
        {
            if (_autoSpinLabel == null)
            {
                return;
            }

            _autoSpinLabel.text = enabled ? "AUTO  ON" : "AUTO  OFF";
            _autoSpinLabel.color = enabled ? _jackpotTextColor : Color.white;
        }

        private void HandleInsufficientFunds()
        {
            if (_machine.State != GameState.Idle || _confirmResetModal == null || _paytableModal == null)
            {
                return;
            }

            if (_paytableModal.IsOpen || _confirmResetModal.IsOpen)
            {
                return;
            }

            if (_machine.OpenModal())
            {
                _confirmResetModal.Show(_machine.StartingBalance, _machine.Balance, _machine.Bet, _machine.MinBet);
            }
        }

        private IEnumerator LowCreditCheckRoutine()
        {
            yield return new WaitForSecondsRealtime(0.75f);

            if (_machine.State == GameState.Idle
                && _machine.FreeSpinsRemaining <= 0
                && _machine.Balance < _machine.Bet)
            {
                HandleInsufficientFunds();
            }
        }

        private static string FormatCredits(int amount)
        {
            return amount.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static bool SpacebarPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Space);
#else
            return false;
#endif
        }
    }
}
