using ACRLiveTiming.Content;

namespace ACRLiveTiming.Decode;

/// <summary>
/// Decoder for the replicated stage weather forecast
/// (<c>MainWeatherForecaster.StageForecastData</c>, an array of
/// <c>FWeatherEvolutionForecastData</c>). Wire layout, probed bit-exact:
/// element k occupies handles k*11+1 .. k*11+11 in one flat ascending
/// RepLayout stream <c>[IntPacked handle][value]…</c>:
///   +1  WeatherTime   f32  in-game seconds-of-day of the slot
///   +2  Timespan      f32  cumulative horizon (600, 1200, 1800, … at the
///                          realistic time speed; scaled with the speed)
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
    /// <summary>EWeatherType values in their wire order; the catalog names them
    /// through these values, listed as weather aliases.</summary>
    public static readonly string[] TypeValues =
    [
        "WT_CLEAR", "WT_LIGHT_CLOUDS", "WT_HEAVY_CLOUDS", "WT_LIGHT_FOG", "WT_HEAVY_FOG",
        "WT_LIGHT_RAIN", "WT_HEAVY_RAIN", "WT_STORM", "WT_LIGHT_SNOW", "WT_HEAVY_SNOW", "WT_BLIZZARD"
    ];

    /// <summary>Display name of a weather type number, from the content catalog.</summary>
    public static string TypeName(int type)
        => ContentCatalog.WeatherName(TypeValues[type]) ?? TypeValues[type];

    /// <summary>
    /// Scan a raw payload for a forecast table; returns its slots from the first
    /// one, as long as they follow each other whole, or null if the packet carries
    /// no table. Gates are strict (exact ascending handles, value sanity per
    /// field), so false positives are practically impossible.
    /// </summary>
    public static List<ForecastSlot>? Slots(byte[] payload)
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
            ForecastSlot? Element(int k)
            {
                int h = k * 11;
                // cheap gate first: the slot time + horizon
                if (IntPacked() != h + 1) return null;
                float t = F32();
                if (!(t >= 0f && t <= 86400f && t % 60f == 0f)) return null;
                if (IntPacked() != h + 2) return null;
                float span = F32();
                // the horizon grows with the time speed: ten minutes a slot when
                // realistic, a hundred when fast
                if (!(span >= 60f && span <= 7 * 86400f && span % 60f == 0f)) return null;
                var c = new float[7];
                for (int j = 0; j < 7; j++)
                {
                    if (IntPacked() != h + 3 + j) return null;
                    float v = F32();
                    bool ok = j < 6
                        ? v >= -0.01f && v <= 1.5f      // 0..1 condition scalars
                        : v > -40f && v < 55f;          // temperature °C
                    if (!ok) return null;
                    c[j] = v;
                }
                if (IntPacked() != h + 10) return null;
                int ty = 0;
                for (int i = 0; i < 4; i++) { int b = Bit(); if (b < 0) return null; ty |= b << i; }
                if (ty >= TypeValues.Length) return null;
                return new ForecastSlot(t, span, c[0], c[1], c[2], c[3], c[4], c[5], c[6], ty);
            }

            if (Element(0) is not ForecastSlot first) continue;
            var slots = new List<ForecastSlot> { first };
            // the slot's location (handle +11, 192 bits), then the next slot
            while (pos + 200 <= totBits && IntPacked() == slots.Count * 11)
            {
                pos += 192;
                int at = pos;
                if (Element(slots.Count) is not ForecastSlot next || next.Span <= slots[^1].Span) { pos = at; break; }
                slots.Add(next);
            }
            return slots;
        }
        return null;
    }
}
