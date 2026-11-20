using System;

namespace SlotMachine.Core
{
    /// <summary>Win categories, ordered from no win to the jackpot bonus.</summary>
    public enum WinTier
    {
        None,
        Partial,
        Triple,
        Jackpot
    }

    /// <summary>
    /// Immutable payout breakdown for a single spin. The UI uses <see cref="Description"/> for the
    /// win banner while the controller applies <see cref="Payout"/> and <see cref="FreeSpinsAwarded"/>
    /// to the session.
    /// </summary>
    public sealed class PayoutResult
    {
        public PayoutResult(
            int bet,
            WinTier tier,
            SymbolData winningSymbol,
            int matchedCount,
            float totalMultiplier,
            int payout,
            int freeSpinsAwarded,
            string description)
        {
            Bet = bet;
            Tier = tier;
            WinningSymbol = winningSymbol;
            MatchedCount = matchedCount;
            TotalMultiplier = totalMultiplier;
            Payout = payout;
            FreeSpinsAwarded = freeSpinsAwarded;
            Description = description;
        }

        public int Bet { get; }
        public WinTier Tier { get; }
        public SymbolData WinningSymbol { get; }
        public int MatchedCount { get; }
        public float TotalMultiplier { get; }
        public int Payout { get; }
        public int FreeSpinsAwarded { get; }
        public string Description { get; }
        public bool IsWin => Tier != WinTier.None;
    }

    /// <summary>
    /// Pure payout calculation. The primary rule (three matching symbols) and the bonus rule
    /// (exactly two matching symbols, plus the Triple-7 free-spin jackpot) are deliberately kept in
    /// separate private methods so the bonus layer can be tuned or removed independently.
    /// </summary>
    public sealed class PayoutEvaluator
    {
        /// <summary>Free spins awarded by the Triple-7 jackpot.</summary>
        public const int JackpotFreeSpins = 5;

        /// <summary>
        /// Evaluates a complete outcome. Primary wins take precedence over partial wins; the
        /// jackpot bonus is applied only to a triple match of the configured jackpot symbol.
        /// </summary>
        public PayoutResult Evaluate(SpinOutcome outcome)
        {
            if (outcome == null)
            {
                throw new ArgumentNullException(nameof(outcome));
            }

            PayoutResult primary = EvaluatePrimaryWin(outcome);
            if (primary.Tier != WinTier.None)
            {
                return primary;
            }

            return EvaluateBonusWin(outcome);
        }

        /// <summary>
        /// Primary rule: all three reels match. Pays the symbol's three-of-a-kind multiplier.
        /// A triple match of the jackpot symbol upgrades the tier and adds free spins.
        /// </summary>
        private static PayoutResult EvaluatePrimaryWin(SpinOutcome outcome)
        {
            SymbolData first = outcome[0];

            if (!outcome.IsThreeOfAKind)
            {
                return CreateNoWin(outcome.Bet);
            }

            bool isJackpot = first.IsJackpotSymbol && first.ThreeOfAKindMultiplier > 0f;
            WinTier tier = isJackpot ? WinTier.Jackpot : WinTier.Triple;
            int freeSpins = isJackpot ? JackpotFreeSpins : 0;
            float multiplier = first.ThreeOfAKindMultiplier;
            int payout = CalculatePayout(outcome.Bet, multiplier);

            string name = first.DisplayName.ToUpperInvariant();
            // Keep the jackpot banner compact: the win banner is a fixed-width HUD element.
            string description = isJackpot
                ? $"JACKPOT! {multiplier:0.##}x BET + {freeSpins} FREE SPINS"
                : $"TRIPLE {name} · {multiplier:0.##}x";

            return new PayoutResult(
                outcome.Bet,
                tier,
                first,
                3,
                multiplier,
                payout,
                freeSpins,
                description);
        }

        /// <summary>
        /// Bonus rule: exactly two matching symbols pay the symbol's minor multiplier. This is a
        /// deliberately small, frequent award that keeps the game lively while the primary rule
        /// remains the main source of value.
        /// </summary>
        private static PayoutResult EvaluateBonusWin(SpinOutcome outcome)
        {
            SymbolData pair = null;
            if (outcome[0] == outcome[1])
            {
                pair = outcome[0];
            }
            else if (outcome[0] == outcome[2])
            {
                pair = outcome[0];
            }
            else if (outcome[1] == outcome[2])
            {
                pair = outcome[1];
            }

            if (pair == null || pair.TwoOfAKindMultiplier <= 0f)
            {
                return CreateNoWin(outcome.Bet);
            }

            float multiplier = pair.TwoOfAKindMultiplier;
            int payout = CalculatePayout(outcome.Bet, multiplier);
            string description = $"TWO {pair.DisplayName.ToUpperInvariant()} · {multiplier:0.##}x";

            return new PayoutResult(
                outcome.Bet,
                WinTier.Partial,
                pair,
                2,
                multiplier,
                payout,
                0,
                description);
        }

        private static PayoutResult CreateNoWin(int bet)
        {
            return new PayoutResult(bet, WinTier.None, null, 0, 0f, 0, 0, "NO WIN");
        }

        private static int CalculatePayout(int bet, float multiplier)
        {
            if (bet <= 0 || multiplier <= 0f)
            {
                return 0;
            }

            // Credits are integral, so round half away from zero for predictable, auditable payouts.
            return (int)Math.Round(bet * multiplier, MidpointRounding.AwayFromZero);
        }
    }
}
