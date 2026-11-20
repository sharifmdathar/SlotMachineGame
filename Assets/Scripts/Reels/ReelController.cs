using System;
using System.Collections.Generic;
using SlotMachine.Core;
using UnityEngine;
using UnityEngine.UI;

namespace SlotMachine.Reels
{
    /// <summary>
    /// Drives one seamless reel strip inside a <see cref="RectMask2D"/> viewport.
    ///
    /// Reel math:
    /// - <c>_offset</c> is measured in symbol units. Integer values mean a symbol is exactly
    ///   centered; the fractional part is the distance currently scrolled through the next symbol.
    /// - Only a small ring of cells is instantiated. Each visible slot <c>k</c> is placed at
    ///   <c>(k - frac) * cellHeight</c> and displays <c>(floor(offset) + k) mod symbolCount</c>.
    ///   When a cell leaves the viewport it is logically recycled by the modulo, producing an
    ///   infinite strip with no allocation during play.
    /// - The result is chosen by the controller before the animation starts. The reel simply
    ///   travels to the matching integer offset, so the stop is deterministic and the animation can
    ///   never alter the outcome.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ReelController : MonoBehaviour
    {
        [Header("Prefab & Layout")]
        [SerializeField] private GameObject _cellPrefab;
        [SerializeField] private RectTransform _cellsRoot;
        [SerializeField, Min(16f)] private float _cellWidth = 160f;
        [SerializeField, Min(16f)] private float _cellHeight = 103f;
        [SerializeField, Range(0.5f, 1f)] private float _symbolScale = 0.88f;
        [SerializeField, Min(1)] private int _cellBuffer = 2;

        [Header("Symbols")]
        [SerializeField] private SymbolData[] _symbols;

        [Header("Spin Tuning")]
        [SerializeField, Min(100f)] private float _baseSpeed = 1700f;
        [SerializeField, Min(0.01f)] private float _accelerationDuration = 0.32f;
        [SerializeField, Min(0.08f)] private float _stopDuration = 0.42f;
        [SerializeField] private AnimationCurve _accelerationCurve;
        [SerializeField] private AnimationCurve _stopCurve;

        [Header("Presentation")]
        [SerializeField] private Color _winTint = new Color(1f, 0.93f, 0.62f, 1f);

        private readonly List<Image> _cellImages = new List<Image>();
        private float _offset;
        private bool _isSpinning;
        private bool _isStopping;
        private float _spinElapsed;
        private float _stopBeginTime;
        private float _stopElapsed;
        private float _stopStartOffset;
        private float _stopTargetOffset;
        private int _targetSymbolIndex;
        private int _lastTickIndex = int.MinValue;
        private bool _winHighlighted;

        /// <summary>Raised exactly once when the reel finishes its settle-back animation.</summary>
        public event Action<ReelController> OnStopped;

        /// <summary>Raised whenever the reel crosses a symbol boundary (used for tick SFX).</summary>
        public event Action<ReelController> OnSymbolTick;

        /// <summary>True while the reel is accelerating, cruising, or settling.</summary>
        public bool IsSpinning => _isSpinning;

        /// <summary>Symbol currently nearest the viewport center.</summary>
        public SymbolData CenterSymbol
        {
            get
            {
                if (_symbols == null || _symbols.Length == 0)
                {
                    return null;
                }

                return _symbols[CenterSymbolIndex];
            }
        }

        /// <summary>Index of the symbol nearest the viewport center.</summary>
        public int CenterSymbolIndex
        {
            get
            {
                if (_symbols == null || _symbols.Length == 0)
                {
                    return -1;
                }

                return PositiveMod(Mathf.RoundToInt(_offset), _symbols.Length);
            }
        }

        private void Awake()
        {
            EnsureCurves(force: false);
            if (_symbols != null && _symbols.Length > 0)
            {
                EnsureCells();
                RefreshCells();
            }
        }

        private void Update()
        {
            if (!_isSpinning)
            {
                return;
            }

            float deltaTime = Time.deltaTime;

            if (!_isStopping)
            {
                _spinElapsed += deltaTime;

                if (_spinElapsed < _stopBeginTime)
                {
                    // The curve is a speed multiplier: it ramps from a soft start to a fast loop.
                    float accelerationT = Mathf.Clamp01(_spinElapsed / _accelerationDuration);
                    float speedFactor = Mathf.Max(0.08f, EvaluateSafe(_accelerationCurve, accelerationT, accelerationT));
                    _offset += _baseSpeed * speedFactor * deltaTime / Mathf.Max(1f, _cellHeight);
                }
                else
                {
                    BeginStop();
                }
            }

            if (_isStopping)
            {
                _stopElapsed += deltaTime;
                float t = Mathf.Clamp01(_stopElapsed / _stopDuration);

                // The default stop curve rises past 1.0 and settles back. Driving the *offset*
                // with it makes the symbols overshoot the target, then bounce back onto it.
                _offset = Mathf.LerpUnclamped(_stopStartOffset, _stopTargetOffset, EvaluateSafe(_stopCurve, t, t));

                if (t >= 1f)
                {
                    CompleteStop();
                    return;
                }
            }

            RefreshCells();
            EmitTickIfNeeded();
        }

        /// <summary>
        /// Starts a spin that will arrive on <paramref name="targetSymbolIndex"/> at
        /// <paramref name="stopTime"/> seconds after this call. The controller passes staggered
        /// times (1.1 s, 1.6 s, 2.1 s) to create the classic left-to-right stop cadence.
        /// </summary>
        public void StartSpin(int targetSymbolIndex, float stopTime)
        {
            if (_symbols == null || _symbols.Length == 0)
            {
                Debug.LogError("ReelController cannot spin without symbols.", this);
                return;
            }

            EnsureCurves(force: false);
            EnsureCells();

            _targetSymbolIndex = PositiveMod(targetSymbolIndex, _symbols.Length);
            _spinElapsed = 0f;
            _stopElapsed = 0f;
            _isSpinning = true;
            _isStopping = false;
            _stopBeginTime = Mathf.Max(0.08f, stopTime - _stopDuration);
            _lastTickIndex = Mathf.FloorToInt(_offset);
            SetWinHighlight(false);
            RefreshCells();
        }

        /// <summary>Tints the strip when this reel participates in a win.</summary>
        public void SetWinHighlight(bool highlighted)
        {
            if (_winHighlighted == highlighted)
            {
                return;
            }

            _winHighlighted = highlighted;
            RefreshCells();
        }

        /// <summary>
        /// Places the reel on a specific symbol without animating. Used by the builder for an
        /// attractive scene preview and for deterministic setup at startup.
        /// </summary>
        public void PreviewAt(int centerSymbolIndex)
        {
            if (_symbols == null || _symbols.Length == 0)
            {
                return;
            }

            EnsureCurves(force: false);
            EnsureCells();
            _offset = PositiveMod(centerSymbolIndex, _symbols.Length);
            _isSpinning = false;
            _isStopping = false;
            RefreshCells();
        }

        private void BeginStop()
        {
            int symbolCount = _symbols.Length;

            // Aim at least two full laps beyond the current position so the overshoot never lands
            // visually "in front" of the last symbol the player saw.
            int baseIndex = Mathf.CeilToInt(_offset) + (symbolCount * 2);
            int targetOffset = baseIndex + PositiveMod(_targetSymbolIndex - baseIndex, symbolCount);

            _stopStartOffset = _offset;
            _stopTargetOffset = targetOffset;
            _stopElapsed = 0f;
            _isStopping = true;
        }

        private void CompleteStop()
        {
            _offset = PositiveMod(Mathf.RoundToInt(_stopTargetOffset), _symbols.Length);
            _isSpinning = false;
            _isStopping = false;
            RefreshCells();

            OnStopped?.Invoke(this);
        }

        private void EmitTickIfNeeded()
        {
            int tickIndex = Mathf.FloorToInt(_offset);
            if (tickIndex == _lastTickIndex)
            {
                return;
            }

            _lastTickIndex = tickIndex;
            OnSymbolTick?.Invoke(this);
        }

        private void EnsureCells()
        {
            if (_cellPrefab == null || _symbols == null || _symbols.Length == 0)
            {
                return;
            }

            if (_cellsRoot == null)
            {
                Transform found = transform.Find("Cells");
                _cellsRoot = found as RectTransform;
            }

            if (_cellsRoot == null)
            {
                Debug.LogError("ReelController requires a Cells RectTransform.", this);
                return;
            }

            int required = (_cellBuffer * 2) + 1;

            // Adopt cells created by the editor builder instead of rebuilding them at runtime.
            if (_cellImages.Count == 0 && _cellsRoot.childCount == required)
            {
                for (int i = 0; i < required; i++)
                {
                    Image existing = _cellsRoot.GetChild(i).GetComponent<Image>();
                    if (existing == null)
                    {
                        _cellImages.Clear();
                        break;
                    }

                    _cellImages.Add(existing);
                }
            }

            if (_cellImages.Count == required)
            {
                return;
            }

            for (int i = _cellsRoot.childCount - 1; i >= 0; i--)
            {
                GameObject child = _cellsRoot.GetChild(i).gameObject;
                if (Application.isPlaying)
                {
                    Destroy(child);
                }
                else
                {
                    DestroyImmediate(child);
                }
            }

            _cellImages.Clear();

            for (int i = 0; i < required; i++)
            {
                GameObject cell = Instantiate(_cellPrefab, _cellsRoot);
                cell.name = $"Cell_{i:00}";

                RectTransform rect = cell.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(_cellWidth, _cellHeight * _symbolScale);
                rect.anchoredPosition = Vector2.zero;

                Image image = cell.GetComponent<Image>();
                if (image != null)
                {
                    image.raycastTarget = false;
                    image.preserveAspect = true;
                }

                _cellImages.Add(image);
            }
        }

        private void RefreshCells()
        {
            if (_symbols == null || _symbols.Length == 0 || _cellImages.Count == 0)
            {
                return;
            }

            int baseIndex = Mathf.FloorToInt(_offset);
            float fraction = _offset - baseIndex;
            Color tint = _winHighlighted ? _winTint : Color.white;

            for (int i = 0; i < _cellImages.Count; i++)
            {
                Image cell = _cellImages[i];
                if (cell == null)
                {
                    continue;
                }

                int k = i - _cellBuffer;
                RectTransform rect = cell.rectTransform;
                rect.sizeDelta = new Vector2(_cellWidth, _cellHeight * _symbolScale);
                rect.anchoredPosition = new Vector2(0f, (k - fraction) * _cellHeight);

                int symbolIndex = PositiveMod(baseIndex + k, _symbols.Length);
                cell.sprite = _symbols[symbolIndex].Sprite;
                cell.color = tint;
            }
        }

        private void EnsureCurves(bool force)
        {
            if (force || _accelerationCurve == null || _accelerationCurve.length == 0)
            {
                _accelerationCurve = new AnimationCurve(
                    new Keyframe(0f, 0.15f),
                    new Keyframe(0.35f, 0.72f),
                    new Keyframe(0.75f, 1f),
                    new Keyframe(1f, 1f));
            }

            if (force || _stopCurve == null || _stopCurve.length == 0)
            {
                _stopCurve = new AnimationCurve(
                    new Keyframe(0f, 0f),
                    new Keyframe(0.62f, 1.07f),
                    new Keyframe(0.84f, 0.985f),
                    new Keyframe(1f, 1f));
            }
        }

        private static float EvaluateSafe(AnimationCurve curve, float time, float fallback)
        {
            return curve != null && curve.length > 0 ? curve.Evaluate(time) : fallback;
        }

        private static int PositiveMod(int value, int modulo)
        {
            int result = value % modulo;
            return result < 0 ? result + modulo : result;
        }
    }
}
