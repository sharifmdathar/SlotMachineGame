using System;
using System.Globalization;
using SlotMachine.Audio;
using SlotMachine.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SlotMachine.UI
{
    /// <summary>
    /// Paytable modal. Symbol rows are authored by the project builder and populated from the same
    /// <see cref="SymbolData"/> assets used by the RNG, so displayed odds and payouts cannot drift
    /// from gameplay balance.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PaytableModal : MonoBehaviour
    {
        [SerializeField] private Button _closeButton;
        [SerializeField] private SymbolData[] _symbols;
        [SerializeField] private Image[] _symbolIcons;
        [SerializeField] private Text[] _symbolNames;
        [SerializeField] private Text[] _threeOfAKindValues;
        [SerializeField] private Text[] _twoOfAKindValues;
        [SerializeField] private Text _rulesText;

        /// <summary>Raised after the modal has been hidden.</summary>
        public event Action Closed;

        /// <summary>True while the modal root is active.</summary>
        public bool IsOpen => gameObject.activeSelf;

        private void Awake()
        {
            if (_closeButton != null)
            {
                _closeButton.onClick.AddListener(Close);
            }
        }

        private void OnDestroy()
        {
            if (_closeButton != null)
            {
                _closeButton.onClick.RemoveListener(Close);
            }
        }

        /// <summary>Shows the modal and refreshes values from the current symbol assets.</summary>
        public void Show()
        {
            gameObject.SetActive(true);
            Populate();
            SelectCloseButton();
        }

        /// <summary>Hides the modal and notifies the HUD so game flow can resume.</summary>
        public void Close()
        {
            if (!gameObject.activeSelf)
            {
                return;
            }

            ProceduralSfxController.PlayButtonClickGlobal();
            gameObject.SetActive(false);
            Closed?.Invoke();
        }

        private void Populate()
        {
            if (_symbols == null)
            {
                return;
            }

            int rowCount = _symbols.Length;
            for (int i = 0; i < rowCount; i++)
            {
                SymbolData symbol = _symbols[i];
                if (symbol == null)
                {
                    continue;
                }

                if (_symbolIcons != null && i < _symbolIcons.Length && _symbolIcons[i] != null)
                {
                    _symbolIcons[i].sprite = symbol.Sprite;
                }

                if (_symbolNames != null && i < _symbolNames.Length && _symbolNames[i] != null)
                {
                    _symbolNames[i].text = symbol.DisplayName.ToUpperInvariant();
                }

                if (_threeOfAKindValues != null && i < _threeOfAKindValues.Length && _threeOfAKindValues[i] != null)
                {
                    _threeOfAKindValues[i].text = FormatMultiplier(symbol.ThreeOfAKindMultiplier);
                }

                if (_twoOfAKindValues != null && i < _twoOfAKindValues.Length && _twoOfAKindValues[i] != null)
                {
                    _twoOfAKindValues[i].text = FormatMultiplier(symbol.TwoOfAKindMultiplier);
                }
            }

            if (_rulesText == null)
            {
                return;
            }

            SymbolData jackpotSymbol = FindJackpotSymbol();
            string jackpotName = jackpotSymbol != null ? jackpotSymbol.DisplayName : "Seven";
            string jackpotMultiplier = jackpotSymbol != null
                ? FormatMultiplier(jackpotSymbol.ThreeOfAKindMultiplier)
                : "50x";

            _rulesText.text =
                $"Triple {jackpotName} = {jackpotMultiplier} + {PayoutEvaluator.JackpotFreeSpins} Free Spins · Any 2 matching symbols on the center line pay the minor bonus.\n" +
                "Powered by CSPRNG weighted outcome selection (demo credits only).";
        }

        private SymbolData FindJackpotSymbol()
        {
            if (_symbols == null)
            {
                return null;
            }

            for (int i = 0; i < _symbols.Length; i++)
            {
                if (_symbols[i] != null && _symbols[i].IsJackpotSymbol)
                {
                    return _symbols[i];
                }
            }

            return null;
        }

        private static string FormatMultiplier(float multiplier)
        {
            return $"{multiplier.ToString("0.##", CultureInfo.InvariantCulture)}x";
        }

        private void SelectCloseButton()
        {
            if (EventSystem.current != null && _closeButton != null)
            {
                EventSystem.current.SetSelectedGameObject(_closeButton.gameObject);
            }
        }
    }
}
