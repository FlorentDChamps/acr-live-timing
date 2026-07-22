namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// Wire format of the replicated <c>URaceStateData</c> component (one per car) —
    /// the property handles and the RepLayout bit-stream parser, with NO detection
    /// logic. From the UE4SS dump, the replicated properties and their handles:
    ///   h4 = RaceTime (float)     stage timer, freezes at the finish line
    ///   h6 = Phase (ERacePhase)   4-bit enum: None=0 Inited=1 Pre=2 StartSequence=3
    ///                             PreSemaphore=4 Semaphore=5 Running=6 Ended=7
    ///                             EndSequence=8 Post=9 Resetting=10 Retire=11
    ///                             Disqualify=12  (serialized as ceil(log2(13)) = 4 bits)
    ///   h7 = Position (int32)     live rally standing
    ///   h9 = DistanceOnMainSpline (float, metres)
    /// The stream is <c>[lead bit][handle:IntPacked][value]…</c>; handles are absolute
    /// and ascending, so reading by handle survives the delta encoding. All values are
    /// 32-bit EXCEPT Phase (4 bits) — parsing h6 at the wrong width desyncs every
    /// handle after it in the same bunch (this bug silently corrupted rt/dist reads
    /// until the phase width was probed).
    /// </summary>
    public static class RaceStateWire
    {
        public const string ComponentPath = "RaceStateData";  // NetGUID path to match
        // sibling per-player component (same outer actor as RaceStateData):
        // URaceSectorsPlayerData.SectorsRecords = TArray<FSectorRecord{ID,Time,RelativeTime}>.
        // Wire (probed bit-exact): [lead bit][h3][ArrayNum u16][per changed element:
        // IntPacked handle k*3+1..k*3+3, then int32/f32 value]…[handle 0 terminator].
        // Element k: +1 ID (int32), +2 Time (f32, cumulative raw), +3 RelativeTime (f32).
        public const string SectorsComponentPath = "RaceSectorsPlayerData";

        public const int RaceTimeHandle = 4;
        public const int PhaseHandle = 6;
        public const int PhaseBits = 4;                       // ceil(log2(ERacePhase_MAX=13))
        public const int PositionHandle = 7;
        public const int DistanceHandle = 9;                  // DistanceOnMainSpline (metres)

        // ERacePhase values used by detection
        public const int PhasePre = 2;
        public const int PhaseRunning = 6;
        public const int PhaseEnded = 7;
        public const int PhaseEndSequence = 8;
        public const int PhasePost = 9;
        public const int PhaseRetire = 11;
        public const int PhaseDisqualify = 12;

        public const double FinishMin = 20.0, FinishMax = 3000.0;  // sanity bounds (s)

        /// <summary>
        /// Parse the property stream. Returns RaceTime (h4) and Distance (h9) as floats
        /// (NaN if absent from this delta update), Phase (h6) and Position (h7, live
        /// rally standing) as ints (-1 if absent).
        /// </summary>
        public static (float rt, float dist, int phase, int pos) ReadState(byte[] b, int bitoff)
        {
            int pos = bitoff, n = b.Length * 8;

            int Bit()
            {
                if (pos >= n) return -1;
                int v = (b[pos >> 3] >> (pos & 7)) & 1; pos++; return v;
            }
            long IntPacked()
            {
                long val = 0; int cnt = 0;
                while (true)
                {
                    long by = 0;
                    for (int i = 0; i < 8; i++) { int bt = Bit(); if (bt < 0) return -1; by |= (long)bt << i; }
                    val |= (by >> 1) << (7 * cnt); cnt++;
                    if ((by & 1) == 0 || cnt > 5) break;
                }
                return val;
            }

            float rt = float.NaN, dist = float.NaN;
            int phase = -1, standing = -1;
            if (Bit() < 0) return (rt, dist, phase, standing);     // lead bit
            for (int k = 0; k < 12; k++)
            {
                long h = IntPacked();
                if (h <= 0 || h > 60) break;                       // terminator / out of range
                if (h == PhaseHandle)
                {
                    // 4-bit enum — reading 32 bits here would swallow the next handle
                    uint p = 0; bool ok = true;
                    for (int i = 0; i < PhaseBits; i++) { int bt = Bit(); if (bt < 0) { ok = false; break; } p |= (uint)(bt << i); }
                    if (!ok) break;
                    phase = (int)p;
                    continue;
                }
                uint v = 0;
                for (int i = 0; i < 32; i++) { int bt = Bit(); if (bt < 0) return (rt, dist, phase, standing); v |= (uint)(bt << i); }
                float f = BitConverter.Int32BitsToSingle((int)v);
                if (h == RaceTimeHandle) rt = f;
                else if (h == DistanceHandle) dist = f;
                else if (h == PositionHandle && v < 256) standing = (int)v;
            }
            return (rt, dist, phase, standing);
        }

        /// <summary>
        /// Parse a RaceSectorsPlayerData property stream and append the cumulative
        /// sector times (FSectorRecord.Time, penalty-free seconds) carried by this
        /// delta to <paramref name="times"/>. Gated on the SectorsRecords handle (3)
        /// and value sanity, so a foreign delta shape is simply skipped.
        /// </summary>
        public static void ReadSectorTimes(byte[] b, int bitoff, List<float> times)
        {
            int pos = bitoff, n = b.Length * 8;

            int Bit()
            {
                if (pos >= n) return -1;
                int v = (b[pos >> 3] >> (pos & 7)) & 1; pos++; return v;
            }
            long IntPacked()
            {
                long val = 0; int cnt = 0;
                while (true)
                {
                    long by = 0;
                    for (int i = 0; i < 8; i++) { int bt = Bit(); if (bt < 0) return -1; by |= (long)bt << i; }
                    val |= (by >> 1) << (7 * cnt); cnt++;
                    if ((by & 1) == 0 || cnt > 5) break;
                }
                return val;
            }

            if (Bit() < 0) return;                      // lead bit
            if (IntPacked() != 3) return;               // SectorsRecords handle
            long num = 0;                                // ArrayNum, uint16
            for (int i = 0; i < 16; i++) { int bt = Bit(); if (bt < 0) return; num |= (long)bt << i; }
            if (num <= 0 || num > 128) return;
            for (int k = 0; k < 3 * 128; k++)
            {
                long h = IntPacked();
                if (h <= 0 || h > 3 * num) break;        // terminator / out of range
                uint v = 0;
                for (int i = 0; i < 32; i++) { int bt = Bit(); if (bt < 0) return; v |= (uint)(bt << i); }
                if ((h - 1) % 3 == 1)                    // FSectorRecord.Time
                {
                    float f = BitConverter.Int32BitsToSingle((int)v);
                    if (f > 1f && f < FinishMax) times.Add(f);
                }
            }
        }
    }
}
