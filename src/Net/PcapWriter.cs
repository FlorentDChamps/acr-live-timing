using System.IO;
using System.Net;

namespace ACRLiveTiming.Net;

/// <summary>
/// Minimal classic-pcap writer for debug captures and session journals. The raw
/// socket delivers bare IPv4 packets, so we prepend a synthetic 14-byte Ethernet
/// header (zero MACs, ethertype 0x0800) and declare LINKTYPE_ETHERNET. That keeps
/// the file readable in Wireshark and any standard pcap tooling expecting an
/// Ethernet frame. Each packet is flushed immediately, so a crash still leaves a
/// valid pcap.
/// </summary>
public sealed class PcapWriter : IDisposable
{
    private static readonly byte[] EthHeader =
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x08, 0x00]; // dst, src, IPv4

    private readonly Lock _lock = new();
    private FileStream? _fs;
    private long _count;

    public string Path { get; private set; }
    public long Count { get { lock (_lock) return _count; } }

    public PcapWriter(string path)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteGlobalHeader(_fs);
        _fs.Flush();
    }

    /// <summary>Continue a pcap this class wrote: a partial last record (left by a
    /// crash) is cut first, so the new records follow a complete one.</summary>
    public static PcapWriter Append(string path)
    {
        var (length, records) = PcapReplay.CompleteLength(path)
                                ?? throw new InvalidDataException("Not a pcap file this app can continue.");
        return new PcapWriter(path, length, records);
    }

    private PcapWriter(string path, long length, long records)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        _fs.SetLength(length);
        _fs.Position = length;
        _count = records;
    }

    private static void WriteGlobalHeader(FileStream fs)
    {
        byte[] h = new byte[24];
        // magic 0xa1b2c3d4 (little-endian on disk: D4 C3 B2 A1)
        WriteU32(h, 0, 0xa1b2c3d4);
        WriteU16(h, 4, 2);          // version major
        WriteU16(h, 6, 4);          // version minor
        WriteU32(h, 8, 0);          // thiszone
        WriteU32(h, 12, 0);         // sigfigs
        WriteU32(h, 16, 65549);     // snaplen: max IP packet + synthetic Ethernet header
        WriteU32(h, 20, 1);         // network = LINKTYPE_ETHERNET
        fs.Write(h, 0, h.Length);
    }

    /// <summary>Append one raw IPv4 packet (as received from the raw socket).</summary>
    public void WriteIpPacket(byte[] ipPacket) => Write(ipPacket, DateTime.UtcNow);

    /// <summary>Append one UDP datagram received from <paramref name="srcIp"/>:<paramref name="srcPort"/>
    /// at <paramref name="utc"/>, inside a synthetic IPv4/UDP header (destination
    /// 0.0.0.0:0, no UDP checksum).</summary>
    public void WriteUdp(string srcIp, int srcPort, byte[] payload, DateTime utc)
    {
        byte[] packet = new byte[28 + payload.Length];
        int total = packet.Length;
        packet[0] = 0x45;                                   // IPv4, 20-byte header
        packet[2] = (byte)(total >> 8); packet[3] = (byte)total;
        packet[8] = 64;                                     // TTL
        packet[9] = 17;                                     // UDP
        IPAddress.Parse(srcIp).GetAddressBytes().CopyTo(packet, 12);
        int sum = 0;
        for (int i = 0; i < 20; i += 2) sum += (packet[i] << 8) | packet[i + 1];
        while (sum > 0xFFFF) sum = (sum & 0xFFFF) + (sum >> 16);
        packet[10] = (byte)(~sum >> 8); packet[11] = (byte)~sum;
        int udpLength = 8 + payload.Length;
        packet[20] = (byte)(srcPort >> 8); packet[21] = (byte)srcPort;
        packet[24] = (byte)(udpLength >> 8); packet[25] = (byte)udpLength;
        Buffer.BlockCopy(payload, 0, packet, 28, payload.Length);
        Write(packet, utc);
    }

    private void Write(byte[] ipPacket, DateTime utc)
    {
        // pcap times are seconds since the Unix epoch, UTC
        long ticks = (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc).Ticks - DateTime.UnixEpoch.Ticks;
        uint sec = (uint)(ticks / TimeSpan.TicksPerSecond);
        uint usec = (uint)(ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMicrosecond);
        int inclLen = EthHeader.Length + ipPacket.Length;

        byte[] rec = new byte[16];
        WriteU32(rec, 0, sec);
        WriteU32(rec, 4, usec);
        WriteU32(rec, 8, (uint)inclLen);
        WriteU32(rec, 12, (uint)inclLen);

        lock (_lock)
        {
            if (_fs == null) return;
            _fs.Write(rec, 0, rec.Length);
            _fs.Write(EthHeader, 0, EthHeader.Length);
            _fs.Write(ipPacket, 0, ipPacket.Length);
            _fs.Flush();
            _count++;
        }
    }

    private static void WriteU16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
    }

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            // best-effort close: a failed flush must not crash app shutdown
            try { _fs?.Flush(); _fs?.Dispose(); } catch { }
            _fs = null;
        }
    }
}
