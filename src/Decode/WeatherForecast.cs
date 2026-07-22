namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// Decoder for the replicated stage weather forecast
    /// (<c>MainWeatherForecaster.StageForecastData</c>, an array of
    /// <c>FWeatherEvolutionForecastData</c>). Wire layout, probed bit-exact:
    /// element k occupies handles k*11+1 .. k*11+11 in one flat ascending
    /// RepLayout stream <c>[IntPacked handle][value]…</c>:
    ///   +1  WeatherTime   f32  in-game seconds-of-day of the slot
    ///   +2  Timespan      f32  cumulative horizon (600, 1200, 1800, …)
    ///   +3..+9  FLocalWeatherCondition: CloudCover Rain Snow Fog Wind
    ///           Lightning (0..1) and Temperature (°C), all f32
    ///   +10 CloserWeatherType  4-bit EWeatherType (ceil(log2(WT_MAX=11)))
    ///   +11 trailing struct payload, fixed 192 bits (skipped; content unmapped)
    /// Total stride 572 bits per element. The table is broadcast rarely — during
    /// the service-park / next-stage pre-load — and describes the UPCOMING stage
    /// (first slot ≈ conditions at its start).
    /// </summary>
    public static class WeatherForecast
    {
        /// <summary>EWeatherType display names (dmenvironment_enums.hpp order).</summary>
        public static readonly string[] TypeNames =
        {
            "Clear", "Light clouds", "Heavy clouds", "Light fog", "Heavy fog",
            "Light rain", "Heavy rain", "Storm", "Light snow", "Heavy snow", "Blizzard"
        };

        /// <summary>
        /// Scan a raw payload for a forecast table; returns the FIRST slot
        /// (earliest time) as (weather type, temperature °C), or null if the
        /// packet carries no table. Gates are strict (exact ascending handles,
        /// value sanity per field), so false positives are practically impossible.
        /// </summary>
        public static (int type, float temp)? FirstSlot(byte[] payload)
        {
            int totBits = payload.Length * 8;
            // element 0 minimum size: 10 handles + 9 f32 + 4 bits ≈ 372 bits
            for (int bit = 0; bit + 372 <= totBits; bit++)
            {
                int pos = bit;

                int Bit()
                {
                    if (pos >= totBits) return -1;
                    int v = (payload[pos >> 3] >> (pos & 7)) & 1; pos++; return v;
                }
                long IntPacked()
                {
                    long val = 0; int cnt = 0;
                    while (true)
                    {
                        long by = 0;
                        for (int i = 0; i < 8; i++) { int b = Bit(); if (b < 0) return -1; by |= (long)b << i; }
                        val |= (by >> 1) << (7 * cnt); cnt++;
                        if ((by & 1) == 0 || cnt > 5) break;
                    }
                    return val;
                }
                float F32()
                {
                    uint v = 0;
                    for (int i = 0; i < 32; i++) { int b = Bit(); if (b < 0) return float.NaN; v |= (uint)(b << i); }
                    return BitConverter.Int32BitsToSingle((int)v);
                }

                // cheap gate first: handle 1, then a plausible slot time + horizon
                if (IntPacked() != 1) continue;
                float t = F32();
                if (!(t >= 0f && t <= 86400f && t % 60f == 0f)) continue;
                if (IntPacked() != 2) continue;
                float span = F32();
                if (!(span >= 60f && span <= 14400f && span % 60f == 0f)) continue;

                bool ok = true;
                float temp = float.NaN;
                for (int j = 0; j < 7 && ok; j++)
                {
                    if (IntPacked() != 3 + j) { ok = false; break; }
                    float v = F32();
                    if (j < 6) ok = v >= -0.01f && v <= 1.5f;      // 0..1 condition scalars
                    else { ok = v > -40f && v < 55f; temp = v; }   // temperature °C
                }
                if (!ok) continue;
                if (IntPacked() != 10) continue;
                int ty = 0;
                for (int i = 0; i < 4; i++) { int b = Bit(); if (b < 0) { ty = -1; break; } ty |= b << i; }
                if (ty < 0 || ty >= TypeNames.Length) continue;

                return (ty, temp);
            }
            return null;
        }
    }
}
