using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SlotMachine.Core
{
    /// <summary>
    /// Immutable record of one completed spin for the session history panel. It captures the
    /// displayed symbols, the committed bet, the payout, and the true net delta for that turn.
    ///
    /// Net delta is <c>payout - bet</c> for a paid spin and <c>payout</c> for a free spin, since a
    /// free spin consumes no credits. The committed bet is preserved separately so the UI can show
    /// what a partial win was measured against.
    /// </summary>
    public sealed class SpinHistoryEntry
    {
        private readonly ReadOnlyCollection<SymbolData> _symbols;

        public SpinHistoryEntry(
            int turnNumber,
            IReadOnlyList<SymbolData> symbols,
            int bet,
            int payout,
            int netDelta,
            bool isFreeSpin,
            WinTier winTier)
        {
            if (turnNumber <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(turnNumber), "Turn number must be positive.");
            }

            if (symbols == null)
            {
                throw new ArgumentNullException(nameof(symbols));
            }

            if (symbols.Count != 3)
            {
                throw new ArgumentException("A spin history entry must contain exactly three symbols.", nameof(symbols));
            }

            if (bet <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bet), "Bet must be positive.");
            }

            if (payout < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(payout), "Payout cannot be negative.");
            }

            SymbolData[] copy = new SymbolData[symbols.Count];
            for (int i = 0; i < symbols.Count; i++)
            {
                copy[i] = symbols[i] ?? throw new ArgumentException("History symbols cannot be null.", nameof(symbols));
            }

            _symbols = Array.AsReadOnly(copy);
            TurnNumber = turnNumber;
            Bet = bet;
            Payout = payout;
            NetDelta = netDelta;
            IsFreeSpin = isFreeSpin;
            WinTier = winTier;
        }

        /// <summary>One-based spin number within the current session.</summary>
        public int TurnNumber { get; }

        /// <summary>Symbols in left-to-right reel order.</summary>
        public IReadOnlyList<SymbolData> Symbols => _symbols;

        /// <summary>Bet committed to this spin. Free spins still carry the triggering bet.</summary>
        public int Bet { get; }

        /// <summary>Credits paid by this spin before the commission is applied.</summary>
        public int Payout { get; }

        /// <summary>True turn profit/loss: payout minus the committed bet, or payout on a free spin.</summary>
        public int NetDelta { get; }

        /// <summary>True when this spin consumed a free-spin credit instead of the balance.</summary>
        public bool IsFreeSpin { get; }

        /// <summary>Payout tier awarded by the evaluator.</summary>
        public WinTier WinTier { get; }
    }
}
