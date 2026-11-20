using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SlotMachine.Core
{
    /// <summary>
    /// Immutable result of one spin: the three symbols in left-to-right reel order and the bet
    /// that was active when the spin started. A spin is evaluated against its own bet so free
    /// spins and mid-session bet changes can never alter an already-paid result.
    /// </summary>
    public sealed class SpinOutcome
    {
        private readonly ReadOnlyCollection<SymbolData> _symbols;

        public SpinOutcome(IReadOnlyList<SymbolData> symbols, int bet)
        {
            if (symbols == null)
            {
                throw new ArgumentNullException(nameof(symbols));
            }

            if (symbols.Count != 3)
            {
                throw new ArgumentException("A three-reel outcome must contain exactly three symbols.", nameof(symbols));
            }

            if (bet <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bet), "Bet must be positive.");
            }

            SymbolData[] copy = new SymbolData[symbols.Count];
            for (int i = 0; i < symbols.Count; i++)
            {
                copy[i] = symbols[i] ?? throw new ArgumentException("Outcome cannot contain null symbols.", nameof(symbols));
            }

            _symbols = Array.AsReadOnly(copy);
            Bet = bet;
        }

        /// <summary>Symbols in left-to-right reel order.</summary>
        public IReadOnlyList<SymbolData> Symbols => _symbols;

        /// <summary>Bet used for this outcome.</summary>
        public int Bet { get; }

        /// <summary>Convenience indexer for left-to-right reads.</summary>
        public SymbolData this[int index] => _symbols[index];

        /// <summary>True when all three symbols are identical.</summary>
        public bool IsThreeOfAKind => _symbols[0] == _symbols[1] && _symbols[1] == _symbols[2];
    }
}
