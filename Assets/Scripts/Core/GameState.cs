namespace SlotMachine.Core
{
    /// <summary>
    /// Explicit game-flow states. UI and input never infer readiness from animation timing; they
    /// read this enum so a spin cannot start while reels, payouts, or a modal are active.
    /// </summary>
    public enum GameState
    {
        /// <summary>Ready for input.</summary>
        Idle,

        /// <summary>Reels are accelerating or spinning at cruise speed.</summary>
        Spinning,

        /// <summary>At least one reel has entered its overshoot/settle phase.</summary>
        Stopping,

        /// <summary>All reels are stopped; the payout evaluator is running.</summary>
        Evaluating,

        /// <summary>A paytable or confirmation modal owns input.</summary>
        ModalOpen
    }
}
