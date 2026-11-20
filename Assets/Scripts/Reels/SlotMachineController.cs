using System;
using System.Collections;
using SlotMachine.Audio;
using SlotMachine.Core;
using UnityEngine;
using UnityEngine.UI;

namespace SlotMachine.Reels
{
    /// <summary>
    /// Owns the slot-machine session: RNG-driven outcome generation, staggered reel orchestration,
    /// payout application, PlayerPrefs persistence, free spins, auto-spin, and the lever animation.
    ///
    /// Design notes:
    /// - The result is generated once at spin start, before any reel animation begins. Reels receive
    ///   target indices, so presentation timing can never change the outcome.
    /// - Session flow is guarded by <see cref="GameState"/>; input cannot start a spin while reel,
    ///   payout, or modal work is active.
    /// - UI observes events instead of polling controller internals. This keeps HUD, audio, and
    ///   reel presentation independent of the game rules.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SlotMachineController : MonoBehaviour
    {
        private const string BalancePrefKey = "SlotMachine.Balance";
        private const string BetIndexPrefKey = "SlotMachine.BetIndex";
        private const float TickSfxCooldown = 0.055f;

        [Header("Wiring")]
        [SerializeField] private ReelController[] _reels;
        [SerializeField] private SymbolData[] _symbols;
        [SerializeField] private Image _leverOverlay;
        [SerializeField] private Sprite _leverUpSprite;
        [SerializeField] private Sprite _leverDownSprite;
        [SerializeField] private Button _leverButton;
        [SerializeField] private ProceduralSfxController _sfx;

        [Header("Rules")]
        [SerializeField] private int[] _betOptions = { 10, 25, 50, 100, 250 };
        [SerializeField, Min(1)] private int _startingBalance = 1000;
        [SerializeField] private float[] _reelStopTimes = { 1.1f, 1.6f, 2.1f };

        [Header("Timing")]
        [SerializeField, Min(0f)] private float _autoSpinDelay = 1.15f;
        [SerializeField, Min(0f)] private float _freeSpinDelay = 0.9f;
        [SerializeField, Min(0f)] private float _leverDownDuration = 0.35f;
        [SerializeField, Min(0f)] private float _resultHoldDuration = 0.35f;

        private readonly CryptoRngProvider _rng = new CryptoRngProvider();
        private readonly PayoutEvaluator _payoutEvaluator = new PayoutEvaluator();

        private GameState _state = GameState.Idle;
        private int _balance;
        private int _betIndex;
        private int _currentBet;
        private int _freeSpinsRemaining;
        private bool _autoSpinEnabled;
        private bool _currentSpinIsFree;
        private bool _isSpinning;
        private bool _allReelsStopped;
        private bool[] _stoppedReels;
        private int _stoppedCount;
        private float _lastTickTime;
        private Vector2 _leverHomePosition;
        private Coroutine _autoRoutine;
        private Coroutine _leverRoutine;

        /// <summary>Raised after the bet is committed and before the reels start moving.</summary>
        public event Action OnSpinStarted;

        /// <summary>Raised with the zero-based reel index each time a reel settles.</summary>
        public event Action<int> OnReelStopped;

        /// <summary>Raised once per spin after payout and free spins have been applied.</summary>
        public event Action<PayoutResult> OnSpinCompleted;

        /// <summary>Raised whenever the persisted balance changes.</summary>
        public event Action<int> OnBalanceChanged;

        /// <summary>Raised whenever the bet index changes.</summary>
        public event Action<int> OnBetChanged;

        /// <summary>Raised when free spins are awarded or consumed.</summary>
        public event Action<int> OnFreeSpinsChanged;

        /// <summary>Raised when auto-spin is toggled, including automatic shutoff.</summary>
        public event Action<bool> OnAutoSpinChanged;

        /// <summary>Raised when the player cannot cover the current bet.</summary>
        public event Action OnInsufficientFunds;

        /// <summary>Raised for every explicit state transition.</summary>
        public event Action<GameState> OnStateChanged;

        public GameState State => _state;
        public int Balance => _balance;
        public int Bet => _betOptions[_betIndex];
        public int StartingBalance => _startingBalance;
        public int FreeSpinsRemaining => _freeSpinsRemaining;
        public bool AutoSpinEnabled => _autoSpinEnabled;
        public bool CanDecreaseBet => _betIndex > 0;
        public bool CanIncreaseBet => _betIndex < _betOptions.Length - 1;
        public bool CanSpin => _state == GameState.Idle && (_freeSpinsRemaining > 0 || _balance >= Bet);

        private void Awake()
        {
            LoadProgress();
            EnsureRuntimeArrays();
            SubscribeToReels();

            if (_leverOverlay != null)
            {
                _leverHomePosition = _leverOverlay.rectTransform.anchoredPosition;
            }
        }

        private void Start()
        {
            if (_leverButton != null)
            {
                _leverButton.onClick.AddListener(HandleLeverClicked);
            }

            SeedReelPreview();
            OnBalanceChanged?.Invoke(_balance);
            OnBetChanged?.Invoke(_betIndex);
            OnFreeSpinsChanged?.Invoke(_freeSpinsRemaining);
            OnAutoSpinChanged?.Invoke(_autoSpinEnabled);
        }

        private void OnDestroy()
        {
            UnsubscribeFromReels();

            if (_leverButton != null)
            {
                _leverButton.onClick.RemoveListener(HandleLeverClicked);
            }
        }

        private void HandleLeverClicked()
        {
            RequestSpin();
        }

        /// <summary>
        /// Starts a spin when the machine is idle and the bet is affordable (or a free spin is
        /// available). Returns false without changing state when the request is not valid.
        /// </summary>
        public bool RequestSpin()
        {
            if (_state != GameState.Idle)
            {
                return false;
            }

            if (_freeSpinsRemaining <= 0 && _balance < Bet)
            {
                OnInsufficientFunds?.Invoke();
                return false;
            }

            CancelAutoSpin();
            StartCoroutine(RunSpin());
            return true;
        }

        /// <summary>Moves the bet selector by <paramref name="direction"/> (-1 or +1).</summary>
        public bool ChangeBet(int direction)
        {
            if (_state != GameState.Idle || direction == 0)
            {
                return false;
            }

            int nextIndex = Mathf.Clamp(_betIndex + Math.Sign(direction), 0, _betOptions.Length - 1);
            if (nextIndex == _betIndex)
            {
                return false;
            }

            _betIndex = nextIndex;
            SaveProgress();
            OnBetChanged?.Invoke(_betIndex);

            if (_autoSpinEnabled && _freeSpinsRemaining <= 0 && _balance < Bet)
            {
                SetAutoSpin(false);
                OnInsufficientFunds?.Invoke();
            }

            return true;
        }

        /// <summary>Enables or disables the automatic spin loop.</summary>
        public void SetAutoSpin(bool enabled)
        {
            if (_autoSpinEnabled == enabled)
            {
                return;
            }

            _autoSpinEnabled = enabled;
            OnAutoSpinChanged?.Invoke(_autoSpinEnabled);

            if (!enabled)
            {
                CancelAutoSpin();
                return;
            }

            if (_state == GameState.Idle && (_freeSpinsRemaining > 0 || _balance >= Bet))
            {
                ScheduleAutoSpin(0.35f);
            }
            else if (_freeSpinsRemaining <= 0 && _balance < Bet)
            {
                _autoSpinEnabled = false;
                OnAutoSpinChanged?.Invoke(false);
                OnInsufficientFunds?.Invoke();
            }
        }

        /// <summary>Convenience method used by the HUD auto-spin button.</summary>
        public void ToggleAutoSpin()
        {
            SetAutoSpin(!_autoSpinEnabled);
        }

        /// <summary>Reserves input for a modal. Returns false while reels are moving.</summary>
        public bool OpenModal()
        {
            if (_state != GameState.Idle)
            {
                return false;
            }

            CancelAutoSpin();
            SetState(GameState.ModalOpen);
            return true;
        }

        /// <summary>Releases modal input and resumes auto-spin when appropriate.</summary>
        public void CloseModal()
        {
            if (_state != GameState.ModalOpen)
            {
                return;
            }

            SetState(GameState.Idle);

            if (_autoSpinEnabled && (_freeSpinsRemaining > 0 || _balance >= Bet))
            {
                ScheduleAutoSpin(0.45f);
            }
        }

        /// <summary>Refills demo credits and clears the session's free spins.</summary>
        public void ResetBalance()
        {
            _balance = _startingBalance;
            _freeSpinsRemaining = 0;
            SaveProgress();

            OnBalanceChanged?.Invoke(_balance);
            OnFreeSpinsChanged?.Invoke(_freeSpinsRemaining);
        }

        private IEnumerator RunSpin()
        {
            _currentBet = Bet;
            _currentSpinIsFree = _freeSpinsRemaining > 0;
            _isSpinning = true;
            _allReelsStopped = false;
            _stoppedCount = 0;
            Array.Clear(_stoppedReels, 0, _stoppedReels.Length);
            SetState(GameState.Spinning);

            if (_currentSpinIsFree)
            {
                _freeSpinsRemaining--;
                OnFreeSpinsChanged?.Invoke(_freeSpinsRemaining);
            }
            else
            {
                AddBalance(-_currentBet);
            }

            for (int i = 0; i < _reels.Length; i++)
            {
                _reels[i].SetWinHighlight(false);
            }

            OnSpinStarted?.Invoke();
            _sfx?.PlayLeverPull();
            StartLeverAnimation();

            int[] resultIndices = _rng.GenerateSpinIndices(_symbols, _reels.Length);

            for (int i = 0; i < _reels.Length; i++)
            {
                float stopTime = GetStopTime(i);
                _reels[i].StartSpin(resultIndices[i], stopTime);
            }

            float safetyTimer = 0f;
            while (!_allReelsStopped && safetyTimer < 10f)
            {
                safetyTimer += Time.deltaTime;
                yield return null;
            }

            if (!_allReelsStopped)
            {
                Debug.LogWarning("Reel stop safety timeout reached; evaluating the intended outcome.", this);
                _isSpinning = false;
            }

            SetState(GameState.Evaluating);

            SymbolData[] outcomeSymbols = new SymbolData[resultIndices.Length];
            for (int i = 0; i < resultIndices.Length; i++)
            {
                outcomeSymbols[i] = _symbols[resultIndices[i]];
            }

            SpinOutcome outcome = new SpinOutcome(outcomeSymbols, _currentBet);
            PayoutResult result = _payoutEvaluator.Evaluate(outcome);

            if (result.Payout > 0)
            {
                AddBalance(result.Payout);
            }

            if (result.FreeSpinsAwarded > 0)
            {
                _freeSpinsRemaining += result.FreeSpinsAwarded;
                OnFreeSpinsChanged?.Invoke(_freeSpinsRemaining);
            }

            HighlightWinningReels(result);

            if (result.Tier == WinTier.Jackpot)
            {
                _sfx?.PlayJackpot();
            }
            else if (result.Payout > 0)
            {
                _sfx?.PlayWin();
            }

            if (_resultHoldDuration > 0f)
            {
                yield return new WaitForSeconds(_resultHoldDuration);
            }

            SetState(GameState.Idle);
            OnSpinCompleted?.Invoke(result);
            HandlePostSpin();
        }

        private void HandlePostSpin()
        {
            if (_freeSpinsRemaining > 0)
            {
                ScheduleAutoSpin(_freeSpinDelay);
                return;
            }

            if (_autoSpinEnabled)
            {
                if (_balance >= Bet)
                {
                    ScheduleAutoSpin(_autoSpinDelay);
                }
                else
                {
                    SetAutoSpin(false);
                    OnInsufficientFunds?.Invoke();
                }

                return;
            }

            if (_balance < Bet)
            {
                OnInsufficientFunds?.Invoke();
            }
        }

        private void ScheduleAutoSpin(float delay)
        {
            CancelAutoSpin();

            if (!isActiveAndEnabled)
            {
                return;
            }

            _autoRoutine = StartCoroutine(AutoSpinAfter(delay));
        }

        private IEnumerator AutoSpinAfter(float delay)
        {
            if (delay > 0f)
            {
                yield return new WaitForSecondsRealtime(delay);
            }

            _autoRoutine = null;

            if (_state != GameState.Idle)
            {
                yield break;
            }

            if (_freeSpinsRemaining > 0 || _autoSpinEnabled)
            {
                RequestSpin();
            }
        }

        private void CancelAutoSpin()
        {
            if (_autoRoutine == null)
            {
                return;
            }

            StopCoroutine(_autoRoutine);
            _autoRoutine = null;
        }

        private void StartLeverAnimation()
        {
            if (_leverOverlay == null)
            {
                return;
            }

            if (_leverRoutine != null)
            {
                StopCoroutine(_leverRoutine);
            }

            _leverRoutine = StartCoroutine(LeverPullRoutine());
        }

        private IEnumerator LeverPullRoutine()
        {
            RectTransform leverRect = _leverOverlay.rectTransform;
            _leverOverlay.sprite = _leverDownSprite;
            leverRect.anchoredPosition = _leverHomePosition + new Vector2(0f, -8f);

            yield return new WaitForSeconds(0.12f);

            leverRect.anchoredPosition = _leverHomePosition;
            yield return new WaitForSeconds(_leverDownDuration);

            _leverOverlay.sprite = _leverUpSprite;
            leverRect.anchoredPosition = _leverHomePosition;
            _leverRoutine = null;
        }

        private void HighlightWinningReels(PayoutResult result)
        {
            if (result.WinningSymbol == null)
            {
                return;
            }

            for (int i = 0; i < _reels.Length; i++)
            {
                if (_reels[i].CenterSymbol == result.WinningSymbol)
                {
                    _reels[i].SetWinHighlight(true);
                }
            }
        }

        private float GetStopTime(int reelIndex)
        {
            if (_reelStopTimes == null || _reelStopTimes.Length == 0)
            {
                return 1.1f + (0.5f * reelIndex);
            }

            return _reelStopTimes[Mathf.Min(reelIndex, _reelStopTimes.Length - 1)];
        }

        private void SetState(GameState nextState)
        {
            if (_state == nextState)
            {
                return;
            }

            _state = nextState;
            OnStateChanged?.Invoke(nextState);
        }

        private void AddBalance(int delta)
        {
            int nextBalance = Mathf.Max(0, _balance + delta);
            if (nextBalance == _balance)
            {
                return;
            }

            _balance = nextBalance;
            SaveProgress();
            OnBalanceChanged?.Invoke(_balance);
        }

        private void LoadProgress()
        {
            if (_betOptions == null || _betOptions.Length == 0)
            {
                _betOptions = new[] { 10, 25, 50, 100, 250 };
            }

            _startingBalance = Mathf.Max(1, _startingBalance);
            _balance = Mathf.Max(0, PlayerPrefs.GetInt(BalancePrefKey, _startingBalance));
            _betIndex = Mathf.Clamp(PlayerPrefs.GetInt(BetIndexPrefKey, 0), 0, _betOptions.Length - 1);
        }

        private void SaveProgress()
        {
            PlayerPrefs.SetInt(BalancePrefKey, _balance);
            PlayerPrefs.SetInt(BetIndexPrefKey, _betIndex);
            PlayerPrefs.Save();
        }

        private void EnsureRuntimeArrays()
        {
            if (_reels == null)
            {
                _reels = Array.Empty<ReelController>();
            }

            if (_symbols == null)
            {
                _symbols = Array.Empty<SymbolData>();
            }

            _stoppedReels = new bool[_reels.Length];
        }

        private void SubscribeToReels()
        {
            for (int i = 0; i < _reels.Length; i++)
            {
                if (_reels[i] == null)
                {
                    continue;
                }

                _reels[i].OnStopped += HandleReelStopped;
                _reels[i].OnSymbolTick += HandleReelTick;
            }
        }

        private void UnsubscribeFromReels()
        {
            if (_reels == null)
            {
                return;
            }

            for (int i = 0; i < _reels.Length; i++)
            {
                if (_reels[i] == null)
                {
                    continue;
                }

                _reels[i].OnStopped -= HandleReelStopped;
                _reels[i].OnSymbolTick -= HandleReelTick;
            }
        }

        private void HandleReelStopped(ReelController reel)
        {
            if (!_isSpinning)
            {
                return;
            }

            int index = Array.IndexOf(_reels, reel);
            if (index < 0 || _stoppedReels[index])
            {
                return;
            }

            _stoppedReels[index] = true;
            _stoppedCount++;

            if (_stoppedCount == 1)
            {
                SetState(GameState.Stopping);
            }

            _sfx?.PlayReelStop();
            OnReelStopped?.Invoke(index);

            if (_stoppedCount >= _reels.Length)
            {
                _isSpinning = false;
                _allReelsStopped = true;
            }
        }

        private void HandleReelTick(ReelController reel)
        {
            if (!_isSpinning || _sfx == null)
            {
                return;
            }

            if (Time.unscaledTime - _lastTickTime < TickSfxCooldown)
            {
                return;
            }

            _lastTickTime = Time.unscaledTime;
            _sfx.PlayReelTick();
        }

        private void SeedReelPreview()
        {
            if (_symbols.Length == 0 || _reels.Length == 0)
            {
                return;
            }

            int[] indices = _rng.GenerateSpinIndices(_symbols, _reels.Length);
            for (int i = 0; i < _reels.Length; i++)
            {
                _reels[i].PreviewAt(indices[i]);
            }
        }
    }
}
