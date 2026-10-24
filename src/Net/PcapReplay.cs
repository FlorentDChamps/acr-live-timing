using System.IO;

namespace ACRLiveTiming.Net;

/// <summary>
/// Classic-pcap reader for offline replay. Yields (srcIp, srcPort, udpPayload,
/// capture time in UTC) per UDP datagram, which can be pumped into the same Engine
/// path as the live sniffer. Supports LINKTYPE_ETHERNET (1, what our PcapWriter produces) and
/// LINKTYPE_RAW (101, bare IPv4).
/// </summary>
public static class PcapReplay
{
    public static IEnumerable<(string ip, int port, byte[] payload, DateTime utc)> ReadUdp(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 24)
            throw new InvalidDataException("Not a pcap file (too short).");

        // classic pcap, little-endian, µs (0xa1b2c3d4) or ns (0xa1b23c4d)
        // timestamps. Anything else (big-endian, pcapng…) would silently replay
        // zero packets; reject it with a reason.
        uint magic = ReadU32(data, 0);
        if (magic != 0xa1b2c3d4u && magic != 0xa1b23c4du)
        {
            throw new InvalidDataException(magic == 0x0a0d0d0au
                ? "pcapng files are not supported — save/export as classic .pcap."
                : "Not a classic little-endian pcap file.");
        }

        // global header: linktype at offset 20 (little-endian)
        uint linktype = ReadU32(data, 20);
        int l2 = linktype == 1 ? 14 : 0;   // strip Ethernet header if present
        long ticksPerFraction = magic == 0xa1b23c4du ? 0 : TimeSpan.TicksPerMicrosecond;

        int off = 24;
        while (off + 16 <= data.Length)
        {
            uint sec = ReadU32(data, off);
            uint fraction = ReadU32(data, off + 4);
            uint incl = ReadU32(data, off + 8);
            off += 16;
            if (off + incl > data.Length) break;
            int frame = off;
            int frameLen = (int)incl;
            off += (int)incl;

            var parsed = Parse(data, frame, frameLen, l2, linktype);
            if (parsed == null) continue;
            long ticks = ticksPerFraction == 0 ? fraction / 100 : fraction * ticksPerFraction;
            var utc = DateTime.UnixEpoch.AddTicks(sec * TimeSpan.TicksPerSecond + ticks);   // Kind Utc
            yield return (parsed.Value.Item1, parsed.Value.Item2, parsed.Value.Item3, utc);
        }
    }

    /// <summary>Why <paramref name="path"/> cannot be read as a capture, or null
    /// when its header is one <see cref="ReadUdp"/> reads.</summary>
    public static string? CheckHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] header = new byte[24];
            if (fs.Read(header, 0, 24) != 24) return "Not a pcap file (too short).";
            uint magic = ReadU32(header, 0);
            if (magic == 0xa1b2c3d4u || magic == 0xa1b23c4du) return null;
            return magic == 0x0a0d0d0au
                ? "pcapng files are not supported — save/export as classic .pcap."
                : "Not a classic little-endian pcap file.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Length of the complete records of a classic little-endian µs pcap
    /// (what <see cref="PcapWriter"/> writes) and how many there are: a crash can
    /// leave a partial last record, which an append must cut first. Null when the
    /// file is not such a pcap.</summary>
    public static (long length, long records)? CompleteLength(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] header = new byte[24];
        if (fs.Read(header, 0, 24) != 24 || ReadU32(header, 0) != 0xa1b2c3d4u) return null;
        long off = 24, records = 0;
        byte[] rec = new byte[16];
        while (true)
        {
            fs.Position = off;
            if (fs.Read(rec, 0, 16) != 16) break;
            long next = off + 16 + ReadU32(rec, 8);
            if (next > fs.Length) break;
            off = next;
            records++;
        }
        return (off, records);
    }

    private static (string, int, byte[])? Parse(byte[] data, int frame, int len, int l2, uint linktype)
    {
        if (len < l2 + 20) return null;
        int l3 = frame + l2;

        if (linktype == 1)   // Ethernet: require IPv4 ethertype
        {
            int ethertype = (data[frame + 12] << 8) | data[frame + 13];
            if (ethertype != 0x0800) return null;
        }

        if ((data[l3] >> 4) != 4) return null;            // IPv4 only
        if (data[l3 + 9] != 17) return null;              // 17 = UDP
        // drop IP fragments — a non-first fragment has no UDP header (see sniffer)
        if ((data[l3 + 6] & 0x3F) != 0 || data[l3 + 7] != 0) return null;
        int ihl = (data[l3] & 0x0F) * 4;
        if (ihl < 20 || l2 + ihl + 8 > len) return null;

        string src = $"{data[l3 + 12]}.{data[l3 + 13]}.{data[l3 + 14]}.{data[l3 + 15]}";
        int l4 = l3 + ihl;
        int srcPort = (data[l4] << 8) | data[l4 + 1];
        int udpLen = (data[l4 + 4] << 8) | data[l4 + 5];
        int start = l4 + 8;
        int end = l4 + udpLen;                 // UDP length covers header+data
        if (end > frame + len || end <= start) return null;

        byte[] payload = new byte[end - start];
        Buffer.BlockCopy(data, start, payload, 0, payload.Length);
        return (src, srcPort, payload);
    }

    private static uint ReadU32(byte[] b, int o)
        => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
}
