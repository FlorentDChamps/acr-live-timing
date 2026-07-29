using System.Text;

namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// Fail-closed structural reader for bDNF in replicated rally-result arrays.
    /// A result is returned only when one element contains an unambiguous participant
    /// name, CarId, bDNF and in-range next handle. Final results use the global
    /// B=1+index*10 handles; partial results select one element with an odd outer
    /// handle, then restart the element's handles at CarId=4/bDNF=5. RallyTimes is
    /// deliberately not decoded: CarId is the bounded resynchronisation point.
    /// </summary>
    public static class ResultDnfScanner
    {
        public readonly record struct ResultFlag(
            string Name,
            string Car,
            bool Dnf,
            int ArrayHandle,
            int BaseHandle);

        readonly record struct NameProperty(int Handle, string Value, int Start, int End);

        ref struct BitReader
        {
            readonly byte[] _data;
            readonly int _end;
            public int Position;
            public bool Failed;

            public BitReader(byte[] data, int start, int length)
            {
                _data = data;
                Position = start;
                _end = start + length;
                Failed = start < 0 || length < 0 || _end < start || _end > data.Length * 8;
            }

            public BitReader At(int position)
            {
                var reader = this;
                reader.Position = position;
                reader.Failed |= position < 0 || position > _end;
                return reader;
            }

            public bool Bit(out int value)
            {
                value = 0;
                if (Failed || Position >= _end) { Failed = true; return false; }
                value = (_data[Position >> 3] >> (Position & 7)) & 1;
                Position++;
                return true;
            }

            public bool Bits(int count, out uint value)
            {
                value = 0;
                if (Failed || count < 0 || count > 32 || Position > _end - count)
                {
                    Failed = true;
                    return false;
                }
                for (int i = 0; i < count; i++)
                {
                    int bit = Position + i;
                    value |= (uint)((_data[bit >> 3] >> (bit & 7)) & 1) << i;
                }
                Position += count;
                return true;
            }

            public bool IntPacked(out int value)
            {
                value = 0;
                for (int count = 0; count <= 4; count++)
                {
                    if (!Bits(8, out var raw)) return false;
                    value |= (int)(raw >> 1) << (7 * count);
                    if ((raw & 1) == 0) return true;
                }
                Failed = true;
                return false;
            }

            public bool LiteralFName(out string value)
            {
                value = "";
                if (!Bit(out var indexed) || indexed != 0) return false;
                if (!Bits(32, out var rawLength)) return false;

                if ((rawLength & 0x80000000u) == 0)
                {
                    int length = (int)rawLength;
                    if (length is < 2 or > 64 || Position > _end - length * 8 - 32)
                        return false;
                    var bytes = new byte[length - 1];
                    for (int i = 0; i < length; i++)
                    {
                        if (!Bits(8, out var rawChar)) return false;
                        byte character = (byte)rawChar;
                        if (i == length - 1)
                        {
                            if (character != 0) return false;
                        }
                        else
                        {
                            if (character < 0x20 || character >= 0x7f) return false;
                            bytes[i] = character;
                        }
                    }
                    value = Encoding.Latin1.GetString(bytes);
                }
                else
                {
                    long signedLength = 0x100000000L - rawLength;
                    if (signedLength is < 2 or > 64
                        || Position > _end - signedLength * 16 - 32)
                        return false;
                    var chars = new char[signedLength - 1];
                    for (int i = 0; i < signedLength; i++)
                    {
                        if (!Bits(16, out var rawChar)) return false;
                        char character = (char)rawChar;
                        if (i == signedLength - 1)
                        {
                            if (character != '\0') return false;
                        }
                        else
                        {
                            if (char.IsControl(character) || char.IsSurrogate(character))
                                return false;
                            chars[i] = character;
                        }
                    }
                    value = new string(chars);
                }

                return Bits(32, out _);
            }
        }

        static bool TryNameProperty(
            BitReader bounds,
            int start,
            out NameProperty property)
        {
            property = default;
            var reader = bounds.At(start);
            if (!reader.IntPacked(out int handle)
                || !reader.LiteralFName(out string value)
                || string.IsNullOrEmpty(value))
                return false;
            property = new NameProperty(handle, value, start, reader.Position);
            return true;
        }

        public static IReadOnlyList<ResultFlag> Scan(
            LobbyDecoder.BlockStream.ContentBlock block)
        {
            try
            {
                if (!block.HasRepLayout || block.BitLength < 42)
                    return Array.Empty<ResultFlag>();

                var bounds = new BitReader(block.Data, block.BitOffset, block.BitLength);
                var header = bounds;
                if (!header.IntPacked(out int arrayHandle)
                    || arrayHandle is not (6 or 8)
                    || !header.Bits(16, out var countRaw))
                    return Array.Empty<ResultFlag>();
                int count = (int)countRaw;
                if (count is <= 0 or > 512)
                    return Array.Empty<ResultFlag>();

                int bodyStart = header.Position;
                int blockEnd = block.BitOffset + block.BitLength;

                // The partial-results array serializes one selected element per
                // block. Its odd outer handle is 1 + index*2; the element's own
                // property handles then restart at 1 (CarId=4, bDNF=5, bDQ=6).
                // Zero-split retirees live here and never enter the final array.
                if (arrayHandle == 6)
                {
                    var outerReader = bounds.At(bodyStart);
                    if (!outerReader.Bit(out int lead) || lead != 0)
                        return Array.Empty<ResultFlag>();
                    int outerStart = outerReader.Position;
                    if (!TryNameProperty(bounds, outerStart, out var participant)
                        || participant.Handle < 1
                        || (participant.Handle & 1) == 0
                        || (participant.Handle - 1) / 2 >= count
                        || !Names.IsPlayerName(participant.Value)
                        || LobbyDecoder.IsNation(participant.Value)
                        || LobbyDecoder.IsCarId(participant.Value))
                        return Array.Empty<ResultFlag>();

                    var matches = new List<ResultFlag>();
                    for (int start = participant.End; start <= blockEnd - 49; start++)
                    {
                        if (!TryNameProperty(bounds, start, out var car)
                            || car.Handle != 4
                            || !LobbyDecoder.IsCarId(car.Value))
                            continue;
                        var tail = bounds.At(car.End);
                        if (!tail.IntPacked(out int dnfHandle)
                            || dnfHandle != 5
                            || !tail.Bit(out int dnf)
                            || !tail.IntPacked(out int nextHandle)
                            || nextHandle is not (0 or 6))
                            continue;
                        matches.Add(new ResultFlag(
                            participant.Value,
                            car.Value,
                            dnf != 0,
                            arrayHandle,
                            participant.Handle));
                    }
                    return matches.Distinct().Count() == 1
                        ? new[] { matches[0] }
                        : Array.Empty<ResultFlag>();
                }

                var participantCandidates = new List<NameProperty>();
                for (int start = bodyStart; start <= blockEnd - 49; start++)
                {
                    if (!TryNameProperty(bounds, start, out var property)
                        || property.Handle < 1
                        || (property.Handle - 1) % 10 != 0
                        || (property.Handle - 1) / 10 >= count
                        || !Names.IsPlayerName(property.Value)
                        || LobbyDecoder.IsNation(property.Value)
                        || LobbyDecoder.IsCarId(property.Value))
                        continue;
                    participantCandidates.Add(property);
                }

                var accepted = new List<ResultFlag>();
                foreach (var participant in participantCandidates)
                {
                    int elementEnd = blockEnd;
                    foreach (var next in participantCandidates)
                        if (next.Start > participant.Start
                            && next.Handle > participant.Handle
                            && next.Start < elementEnd)
                            elementEnd = next.Start;

                    var matches = new List<ResultFlag>();
                    for (int start = participant.End; start <= elementEnd - 49; start++)
                    {
                        if (!TryNameProperty(bounds, start, out var car)
                            || car.Handle != participant.Handle + 3
                            || !LobbyDecoder.IsCarId(car.Value))
                            continue;

                        var tail = bounds.At(car.End);
                        if (!tail.IntPacked(out int dnfHandle)
                            || dnfHandle != participant.Handle + 4
                            || !tail.Bit(out int dnf)
                            || !tail.IntPacked(out int nextHandle)
                            || (nextHandle != 0
                                && (nextHandle < participant.Handle + 5
                                    || nextHandle > participant.Handle + 9)))
                            continue;
                        matches.Add(new ResultFlag(
                            participant.Value,
                            car.Value,
                            dnf != 0,
                            arrayHandle,
                            participant.Handle));
                    }

                    // More than one resynchronisation point means ordinary nested
                    // data happened to mimic property handles: reject the element.
                    if (matches.Count == 1)
                        accepted.Add(matches[0]);
                }

                // Alternative bit alignments can occasionally spell the same FString
                // under two bases. Publishing either would violate fail-closed.
                return accepted
                    .Distinct()
                    .GroupBy(result => result.Name, StringComparer.Ordinal)
                    .Where(group => group.Count() == 1)
                    .Select(group => group.Single())
                    .ToList();
            }
            catch
            {
                return Array.Empty<ResultFlag>();
            }
        }
    }
}
