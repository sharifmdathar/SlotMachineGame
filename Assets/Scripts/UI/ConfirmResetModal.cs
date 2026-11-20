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

        /// <summary>Shows the dialog with the refill amount included in the body copy.</summary>
        public void Show(int startingBalance)
        {
            if (_titleText != null)
            {
                _titleText.text = "OUT OF CREDITS";
            }

            if (_bodyText != null)
            {
                _bodyText.text = $"REFILL YOUR DEMO BALANCE TO {startingBalance.ToString("N0", CultureInfo.InvariantCulture)} CREDITS?";
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
