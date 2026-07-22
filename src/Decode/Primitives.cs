using System.Text;

namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// Byte-level primitives ported 1:1 from the original protocol-RE prototype:
    /// little-endian float32
    /// reads, bit-shifting for the multi-shift result scan, and FString extraction
    /// (UE serializes FStrings byte-aligned: &lt;u32 len incl null LE&gt;&lt;utf8&gt;&lt;00&gt;).
    /// </summary>
    public static class Primitives
    {
        public readonly struct FStr
        {
            public readonly int Start;
            public readonly int End;
            public readonly string Text;
            public FStr(int start, int end, string text) { Start = start; End = end; Text = text; }
        }

        /// <summary>float32 LE at offset o, or null if out of range.</summary>
        public static float? F32(byte[] b, int o)
            => (o >= 0 && o <= b.Length - 4) ? BitConverter.ToSingle(b, o) : (float?)null;

        /// <summary>Right-shift the whole buffer by b bits (recovers bit-shifted net bunches).</summary>
        public static byte[] Shr(byte[] data, int b)
        {
            if (b == 0) return data;
            var shifted = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                int cur = data[i] >> b;
                if (i + 1 < data.Length) cur |= (data[i + 1] << (8 - b)) & 0xFF;
                shifted[i] = (byte)cur;
            }
            return shifted;
        }

        /// <summary>
        /// The 8 bit-shifted views of a payload with their FStrings, computed ONCE and
        /// shared by every per-packet consumer (result scan, stage-name scan, run-start
        /// detection) — net bunches start mid-byte, so all consumers need all shifts.
        /// </summary>
        public static (byte[][] data, List<FStr>[] fstrs) ShiftScan(byte[] payload)
        {
            var data = new byte[8][];
            var fstrs = new List<FStr>[8];
            for (int s = 0; s < 8; s++)
            {
                data[s] = s == 0 ? payload : Shr(payload, s);
                fstrs[s] = FStringsOff(data[s]);
            }
            return (data, fstrs);
        }

        /// <summary>All extractable FString texts in the payload.</summary>
        public static List<string> FStrings(byte[] payload)
        {
            var strings = new List<string>();
            foreach (var f in FStringsOff(payload)) strings.Add(f.Text);
            return strings;
        }

        /// <summary>
        /// FStrings with (start, end, text) byte offsets. UE serializes an FString as
        /// a signed int32 length followed by the characters (incl. a trailing null):
        /// a POSITIVE length is 1 byte/char ANSI, a NEGATIVE length is UCS-2 / UTF-16LE
        /// (used for any non-ASCII display name — CJK, Cyrillic, accents…). We decode
        /// both so exotic pseudos are recognised as player names, not dropped.
        /// </summary>
        public static List<FStr> FStringsOff(byte[] payload)
        {
            var strings = new List<FStr>();
            int i = 0, n = payload.Length;
            while (i + 4 <= n)
            {
                uint raw = BitConverter.ToUInt32(payload, i);

                // ANSI FString: positive length (bytes incl. trailing null), 1 byte/char.
                if (raw >= 1 && raw <= 48 && i + 4 + (int)raw <= n)
                {
                    int byteLen = (int)raw;
                    int bodyLen = (payload[i + 4 + byteLen - 1] == 0) ? byteLen - 1 : byteLen;
                    bool ok = bodyLen > 0;
                    for (int k = 0; k < bodyLen && ok; k++)
                    {
                        byte c = payload[i + 4 + k];
                        if (c < 32 || c >= 127) ok = false;
                    }
                    if (ok)
                    {
                        strings.Add(new FStr(i, i + 4 + byteLen, Encoding.Latin1.GetString(payload, i + 4, bodyLen)));
                        i += 4 + byteLen;
                        continue;
                    }
                }
                // UTF-16 FString: NEGATIVE length. len = -charCount (incl. null),
                // 2 bytes/char LE. charCount 2..25 -> raw in [0xFFFFFFE7, 0xFFFFFFFE].
                else if (raw >= 0xFFFFFFE7u && raw <= 0xFFFFFFFEu)
                {
                    int charCount = (int)(0x100000000L - raw);   // 2..25 incl. null
                    int byteLen = charCount * 2;
                    int bodyChars = charCount - 1;               // drop trailing null
                    if (i + 4 + byteLen <= n
                        && payload[i + 4 + byteLen - 2] == 0
                        && payload[i + 4 + byteLen - 1] == 0)    // null terminator
                    {
                        bool ok = bodyChars > 0;
                        for (int k = 0; k < bodyChars && ok; k++)
                        {
                            int ch = payload[i + 4 + k * 2] | (payload[i + 4 + k * 2 + 1] << 8);
                            // reject C0/C1 controls and UNPAIRED surrogates only — a
                            // valid high+low pair (emoji & other astral chars in player
                            // names) must pass
                            if (ch < 0x20 || (ch >= 0x7f && ch <= 0x9f)) ok = false;
                            else if (ch >= 0xD800 && ch <= 0xDBFF)
                            {
                                int nxt = k + 1 < bodyChars
                                    ? payload[i + 4 + (k + 1) * 2] | (payload[i + 4 + (k + 1) * 2 + 1] << 8)
                                    : -1;
                                if (nxt >= 0xDC00 && nxt <= 0xDFFF) k++;   // consume the pair
                                else ok = false;                           // lone high surrogate
                            }
                            else if (ch >= 0xDC00 && ch <= 0xDFFF) ok = false;   // lone low surrogate
                        }
                        if (ok)
                        {
                            strings.Add(new FStr(i, i + 4 + byteLen,
                                Encoding.Unicode.GetString(payload, i + 4, byteLen - 2)));
                            i += 4 + byteLen;
                            continue;
                        }
                    }
                }
                i += 1;
            }
            return strings;
        }
    }
}
