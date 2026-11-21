using System.Collections.Generic;
using System.Globalization;
using SlotMachine.Core;
using UnityEngine;
using UnityEngine.UI;

namespace SlotMachine.UI
{
    /// <summary>
    /// Presentation-only renderer for the top-right RECENT SPINS card. The controller owns the
    /// immutable history entries and cumulative session net; this component only formats rows and
    /// applies profit/loss colors. No game rules or balance state live here.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpinHistoryCard : MonoBehaviour
    {
        [Header("Rows")]
        [SerializeField] private Text _netText;
        [SerializeField] private GameObject _emptyState;
        [SerializeField] private GameObject[] _rowRoots;
        [SerializeField] private Image[] _rowSymbolIcons;
        [SerializeField] private Text[] _rowBetTexts;
        [SerializeField] private Text[] _rowNetTexts;

        [Header("Colors")]
        [SerializeField] private Color _profitColor = new Color(0.54f, 0.95f, 0.60f, 1f);
        [SerializeField] private Color _lossColor = new Color(1f, 0.48f, 0.44f, 1f);
        [SerializeField] private Color _neutralColor = new Color(0.72f, 0.76f, 0.88f, 1f);
        [SerializeField] private Color _freeColor = new Color(1f, 0.79f, 0.28f, 1f);
        [SerializeField] private Color _betColor = new Color(0.84f, 0.87f, 0.96f, 0.9f);

        private const float ScreenRightInset = 48f;
        private const float ScreenTopInset = 70f;

        private int _lastScreenWidth;
        private int _lastScreenHeight;
        private float _lastScaleFactor;
        private Canvas _canvas;

        private void OnEnable()
        {
            ApplyScreenInset();
        }

        private void Awake()
        {
            _canvas = GetComponentInParent<Canvas>();
            HideRows();
            if (_emptyState != null)
            {
                _emptyState.SetActive(true);
            }
        }

        private void Update()
        {
            float scale = _canvas != null ? _canvas.scaleFactor : 0f;
            if (Screen.width != _lastScreenWidth
                || Screen.height != _lastScreenHeight
                || !Mathf.Approximately(scale, _lastScaleFactor))
            {
                ApplyScreenInset();
            }
        }

        private void ApplyScreenInset()
        {
            if (_canvas == null || _canvas.scaleFactor <= 0f)
            {
                return;
            }

            // The HTML fullscreen button is measured in CSS pixels, while this card is scaled by
            // CanvasScaler. Convert the 70 px screen-space clearance back into canvas units so
            // the card stays below the button at every viewport size.
            RectTransform rect = (RectTransform)transform;
            rect.anchoredPosition = new Vector2(-ScreenRightInset, -ScreenTopInset / _canvas.scaleFactor);
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;
            _lastScaleFactor = _canvas.scaleFactor;
        }

        /// <summary>
        /// Rebuilds the visible rows from newest to oldest. Entries are owned by the machine
        /// controller, so this method never mutates the history or session totals.
        /// </summary>
        public void Refresh(IReadOnlyList<SpinHistoryEntry> entries, int sessionNet)
        {
            int entryCount = entries?.Count ?? 0;
            int rowCount = _rowRoots?.Length ?? 0;

            for (int i = 0; i < rowCount; i++)
            {
                bool visible = i < entryCount;
                _rowRoots[i].SetActive(visible);
                if (!visible)
                {
                    continue;
                }

                SpinHistoryEntry entry = entries[i];
                SetRow(entry, i);
            }

            if (_emptyState != null)
            {
                _emptyState.SetActive(entryCount == 0);
            }

            if (_netText != null)
            {
                _netText.text = $"NET: {FormatSigned(sessionNet, showPlusOnZero: true)}";
                _netText.color = sessionNet > 0
                    ? _profitColor
                    : sessionNet < 0
                        ? _lossColor
                        : Color.white;
            }
        }

        private void SetRow(SpinHistoryEntry entry, int rowIndex)
        {
            if (_rowBetTexts != null && rowIndex < _rowBetTexts.Length && _rowBetTexts[rowIndex] != null)
            {
                _rowBetTexts[rowIndex].text = entry.IsFreeSpin ? "FREE" : $"B:{entry.Bet}";
                _rowBetTexts[rowIndex].color = entry.IsFreeSpin ? _freeColor : _betColor;
            }

            if (_rowNetTexts != null && rowIndex < _rowNetTexts.Length && _rowNetTexts[rowIndex] != null)
            {
                _rowNetTexts[rowIndex].text = FormatSigned(entry.NetDelta, showPlusOnZero: false);
                _rowNetTexts[rowIndex].color = entry.NetDelta > 0
                    ? _profitColor
                    : entry.NetDelta < 0
                        ? _lossColor
                        : _neutralColor;
            }

            if (_rowSymbolIcons == null)
            {
                return;
            }

            int iconBase = rowIndex * 3;
            for (int i = 0; i < 3; i++)
            {
                int iconIndex = iconBase + i;
                if (iconIndex >= _rowSymbolIcons.Length || _rowSymbolIcons[iconIndex] == null)
                {
                    continue;
                }

                Sprite sprite = entry.Symbols[i]?.Sprite;
                _rowSymbolIcons[iconIndex].sprite = sprite;
                _rowSymbolIcons[iconIndex].enabled = sprite != null;
            }
        }

        private void HideRows()
        {
            if (_rowRoots == null)
            {
                return;
            }

            for (int i = 0; i < _rowRoots.Length; i++)
            {
                if (_rowRoots[i] != null)
                {
                    _rowRoots[i].SetActive(false);
                }
            }
        }

        private static string FormatSigned(int value, bool showPlusOnZero)
        {
            if (value > 0)
            {
                return $"+{value.ToString("N0", CultureInfo.InvariantCulture)}";
            }

            if (value < 0)
            {
                return $"-{Mathf.Abs(value).ToString("N0", CultureInfo.InvariantCulture)}";
            }

            return showPlusOnZero ? "+0" : "0";
        }
    }
}
