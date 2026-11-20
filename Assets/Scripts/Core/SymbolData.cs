using UnityEngine;

namespace SlotMachine.Core
{
    /// <summary>
    /// Authoring data for one reel symbol. All payout tuning lives in data assets so designers can
    /// balance the machine without touching code. The RNG reads <see cref="Weight"/>; the payout
    /// evaluator reads the two multiplier fields and <see cref="IsJackpotSymbol"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "SymbolData", menuName = "Slot Game/Symbol Data")]
    public sealed class SymbolData : ScriptableObject
    {
        [SerializeField] private string _id = "symbol";
        [SerializeField] private string _displayName = "Symbol";
        [SerializeField] private Sprite _sprite;
        [SerializeField, Min(1)] private int _weight = 1;
        [SerializeField, Min(0f)] private float _threeOfAKindMultiplier = 1f;
        [SerializeField, Min(0f)] private float _twoOfAKindMultiplier = 0.5f;
        [SerializeField] private bool _isJackpotSymbol;

        /// <summary>Stable machine-readable identifier (for save data, analytics, or tests).</summary>
        public string Id => _id;

        /// <summary>Human-readable name shown in the paytable and win banner.</summary>
        public string DisplayName => _displayName;

        /// <summary>Artwork used on the reel strip and in the paytable.</summary>
        public Sprite Sprite => _sprite;

        /// <summary>Relative selection weight. Higher values appear more often.</summary>
        public int Weight => _weight;

        /// <summary>Bet multiplier paid when all three reels show this symbol.</summary>
        public float ThreeOfAKindMultiplier => _threeOfAKindMultiplier;

        /// <summary>Bet multiplier paid when exactly two reels show this symbol.</summary>
        public float TwoOfAKindMultiplier => _twoOfAKindMultiplier;

        /// <summary>Triple matches of this symbol award the jackpot free-spin bonus.</summary>
        public bool IsJackpotSymbol => _isJackpotSymbol;
    }
}
