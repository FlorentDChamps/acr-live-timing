namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// Wire constants of the per-car URaceStateData component (ACR 0.6, UE 5.6.1):
    /// the ERacePhase values the finish and retirement logic keys on, and the sanity
    /// bounds of a stage time. The component itself is decoded from its replicated
    /// property layout by <see cref="ReplicationDecoder"/>.
    /// </summary>
    public static class RaceStateWire
    {
        // ERacePhase: None=0 Inited=1 Pre=2 StartSequence=3 PreSemaphore=4 Semaphore=5
        // Running=6 Ended=7 EndSequence=8 Post=9 Resetting=10 Retire=11 Disqualify=12
        public const int PhasePre = 2;
        public const int PhaseRunning = 6;
        public const int PhaseEnded = 7;
        public const int PhaseEndSequence = 8;
        public const int PhasePost = 9;
        public const int PhaseRetire = 11;
        public const int PhaseDisqualify = 12;

        public const double FinishMin = 20.0, FinishMax = 3000.0;  // sanity bounds (s)

        /// <summary>Float32 tolerance (seconds) for "same finish time". Times are
        /// decoded as float32 from two sources — the replicated RaceStateData timer
        /// peak and the result entry — which need not agree bit-exactly, so equality is
        /// a near-match. Deliberately loose: too tight and one finish gets counted
        /// twice. Distinct from name binding (see SessionMatrix.NameMatchTolerance),
        /// which needs the opposite trade-off.</summary>
        public const double FinishMatchTolerance = 0.15;
    }
}
