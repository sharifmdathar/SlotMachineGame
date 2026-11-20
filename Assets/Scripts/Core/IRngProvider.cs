namespace SlotMachine.Core
{
    /// <summary>
    /// Abstraction over an unbiased integer random source.
    /// Keeping the RNG behind an interface makes the payout/reel logic testable and lets a
    /// deterministic implementation be substituted for automated simulation without touching
    /// gameplay code.
    /// </summary>
    public interface IRngProvider
    {
        /// <summary>
        /// Returns a uniformly distributed integer in the half-open range [0, <paramref name="maxExclusive"/>).
        /// </summary>
        /// <param name="maxExclusive">Upper bound, exclusive. Must be greater than zero.</param>
        int NextInt(int maxExclusive);
    }
}
