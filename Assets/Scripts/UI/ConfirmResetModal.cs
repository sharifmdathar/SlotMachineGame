using System;
using System.Globalization;
using SlotMachine.Audio;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SlotMachine.UI
{
    /// <summary>
    /// Low-credit confirmation dialog. Uses the sliced YES/NO sprite sheet states and reports a
    /// decision through events so the HUD/controller remain responsible for actually changing the
    /// balance and game state.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ConfirmResetModal : MonoBehaviour
    {
        [SerializeField] private Button _yesButton;
        [SerializeField] private Button _noButton;
        [SerializeField] private Text _titleText;
        [SerializeField] private Text _bodyText;

        /// <summary>Raised when the player chooses YES.</summary>
        public event Action Confirmed;

        /// <summary>Raised when the player chooses NO.</summary>
        public event Action Cancelled;

        /// <summary>True while the modal root is active.</summary>
        public bool IsOpen => gameObject.activeSelf;

        private void Awake()
        {
            if (_yesButton != null)
            {
                _yesButton.onClick.AddListener(HandleYes);
            }

            if (_noButton != null)
            {
                _noButton.onClick.AddListener(HandleNo);
            }
        }

        private void OnDestroy()
        {
            if (_yesButton != null)
            {
                _yesButton.onClick.RemoveListener(HandleYes);
            }

            if (_noButton != null)
            {
                _noButton.onClick.RemoveListener(HandleNo);
            }
        }

        /// <summary>
        /// Shows the dialog with copy appropriate to the shortfall. A balance below the minimum bet
        /// is truly out of credits; a balance that covers the minimum but not the selected bet is a
        /// bet-size problem and should offer lowering the bet instead of implying the wallet is empty.
        /// </summary>
        public void Show(int startingBalance, int currentBalance, int currentBet, int minimumBet)
        {
            bool trulyOutOfCredits = currentBalance < minimumBet;
            bool betExceedsBalance = !trulyOutOfCredits && currentBalance < currentBet;
            if (!trulyOutOfCredits && !betExceedsBalance)
            {
                // Defensive fallback for an unexpected caller: treat it as a refill prompt.
                trulyOutOfCredits = true;
            }

            if (_titleText != null)
            {
                _titleText.text = trulyOutOfCredits ? "OUT OF CREDITS" : "BET EXCEEDS BALANCE";
            }

            if (_bodyText != null)
            {
                string refill = startingBalance.ToString("N0", CultureInfo.InvariantCulture);
                _bodyText.text = trulyOutOfCredits
                    ? $"REFILL YOUR DEMO BALANCE TO {refill} CREDITS?"
                    : $"YOU HAVE {currentBalance.ToString("N0", CultureInfo.InvariantCulture)} CREDITS. LOWER YOUR BET TO PLAY, OR REFILL TO {refill}?";
            }

            gameObject.SetActive(true);

            if (EventSystem.current != null && _yesButton != null)
            {
                EventSystem.current.SetSelectedGameObject(_yesButton.gameObject);
            }
        }

        /// <summary>Hides the dialog without raising a decision event.</summary>
        public void Hide()
        {
            gameObject.SetActive(false);
        }

        private void HandleYes()
        {
            ProceduralSfxController.PlayButtonClickGlobal();
            Confirmed?.Invoke();
        }

        private void HandleNo()
        {
            ProceduralSfxController.PlayButtonClickGlobal();
            Cancelled?.Invoke();
        }
    }
}
