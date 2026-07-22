using System.IO;

namespace ACRLiveTiming.Net
{
    /// <summary>
    /// Minimal classic-pcap writer for debug captures. The raw socket delivers bare
    /// IPv4 packets, so we prepend a synthetic 14-byte Ethernet header (zero MACs,
    /// ethertype 0x0800) and declare LINKTYPE_ETHERNET. That keeps the file readable
    /// in Wireshark and any standard pcap tooling expecting an Ethernet frame. Each
    /// packet is flushed immediately, so a crash still leaves a valid pcap.
    /// </summary>
    public sealed class PcapWriter : IDisposable
    {
        static readonly byte[] EthHeader =
            { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x08, 0x00 }; // dst, src, IPv4

        readonly object _lock = new();
        FileStream? _fs;
        long _count;

        public string Path { get; private set; }
        public long Count { get { lock (_lock) return _count; } }

        public PcapWriter(string path)
        {
            Path = path;
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteGlobalHeader(_fs);
            _fs.Flush();
        }

        static void WriteGlobalHeader(FileStream fs)
        {
            var h = new byte[24];
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
        public void WriteIpPacket(byte[] ipPacket)
        {
            var now = DateTimeOffset.UtcNow;
            uint sec = (uint)now.ToUnixTimeSeconds();
            uint usec = (uint)(now.UtcTicks % TimeSpan.TicksPerSecond / 10);   // real µs
            int inclLen = EthHeader.Length + ipPacket.Length;

            var rec = new byte[16];
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

        static void WriteU16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
        }

        static void WriteU32(byte[] b, int o, uint v)
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
}
