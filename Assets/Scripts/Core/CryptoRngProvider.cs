using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace SlotMachine.Core
{
    /// <summary>
    /// Cryptographically secure, weighted RNG used for every spin result.
    ///
    /// Fairness notes:
    /// - <see cref="RandomNumberGenerator.GetInt32(int)"/> is backed by the operating-system CSPRNG
    ///   and uses rejection sampling internally, so there is no modulo bias.
    /// - Weighted selection sums the configured weights and draws one uniform integer in
    ///   [0, totalWeight). Walking the cumulative weights therefore selects each symbol with
    ///   probability weight / totalWeight, exactly as displayed in the paytable.
    /// - The RNG is stateless and is never seeded from time or from UnityEngine.Random, which keeps
    ///   results independent of frame rate and impossible to predict from the previous spin.
    /// </summary>
    public sealed class CryptoRngProvider : IRngProvider
    {
        /// <inheritdoc />
        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), "The exclusive upper bound must be positive.");
            }

            // GetInt32 uses rejection sampling over cryptographic bytes, avoiding the low-bit bias
            // that a naive "random % maxExclusive" would introduce.
            return RandomNumberGenerator.GetInt32(maxExclusive);
        }

        /// <summary>
        /// Selects a symbol index using the weights configured on <see cref="SymbolData"/>.
        /// </summary>
        public int SelectWeightedIndex(IReadOnlyList<SymbolData> symbols)
        {
            if (symbols == null)
            {
                throw new ArgumentNullException(nameof(symbols));
            }

            if (symbols.Count == 0)
            {
                throw new ArgumentException("At least one symbol is required.", nameof(symbols));
            }

            int totalWeight = 0;
            for (int i = 0; i < symbols.Count; i++)
            {
                SymbolData symbol = symbols[i];
                if (symbol == null)
                {
                    throw new ArgumentException("Symbol lists cannot contain null entries.", nameof(symbols));
                }

                if (symbol.Weight <= 0)
                {
                    throw new ArgumentException("Every symbol weight must be greater than zero.", nameof(symbols));
                }

                totalWeight += symbol.Weight;
            }

            int roll = NextInt(totalWeight);
            int cumulative = 0;

            for (int i = 0; i < symbols.Count; i++)
            {
                cumulative += symbols[i].Weight;
                if (roll < cumulative)
                {
                    return i;
                }
            }

            // The loop above is exhaustive for any roll in [0, totalWeight). This fallback only
            // protects against an invalid weight array being mutated after the sum was computed.
            return symbols.Count - 1;
        }

        /// <summary>
        /// Produces one independently weighted symbol index per reel.
        /// </summary>
        public int[] GenerateSpinIndices(IReadOnlyList<SymbolData> symbols, int reelCount)
        {
            if (reelCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(reelCount));
            }

            int[] indices = new int[reelCount];
            for (int i = 0; i < reelCount; i++)
            {
                indices[i] = SelectWeightedIndex(symbols);
            }

            return indices;
        }

    }
}
