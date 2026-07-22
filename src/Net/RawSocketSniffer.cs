using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ACRLiveTiming.Net
{
    /// <summary>
    /// Inbound UDP sniffer via a Windows raw socket (SIO_RCVALL) — no Npcap/driver,
    /// only Administrator. We decode server→client traffic only, which is exactly
    /// the inbound direction a raw socket delivers. Each received datagram is a full
    /// IPv4 packet (header + payload); we parse UDP and raise (srcIp, srcPort, payload).
    /// </summary>
    public sealed class RawSocketSniffer : IDisposable
    {
        Socket? _sock;
        CancellationTokenSource? _cts;
        Task? _task;

        /// <summary>(srcIp, srcPort, payload) for each inbound UDP datagram.</summary>
        public event Action<string, int, byte[]>? Packet;
        /// <summary>Full raw IPv4 packet for every inbound datagram (debug pcap capture).</summary>
        public event Action<byte[]>? RawIp;
        public event Action<string>? Log;

        public IPAddress? LocalIp { get; private set; }

        public void Start(IPAddress localIp)
        {
            Stop();   // never leak a previous session's socket/thread/events
            LocalIp = localIp;
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            sock.Bind(new IPEndPoint(localIp, 0));
            // SIO_RCVALL: receive all packets on this interface.
            sock.IOControl(IOControlCode.ReceiveAll, new byte[] { 1, 0, 0, 0 }, new byte[4]);
            _sock = sock;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _task = Task.Run(() => Loop(sock, token));
            Log?.Invoke($"Raw socket sniffing on {localIp}");
        }

        void Loop(Socket sock, CancellationToken ct)
        {
            var buf = new byte[65535];
            int receiveErrors = 0;
            while (!ct.IsCancellationRequested)
            {
                int n;
                try { n = sock.Receive(buf); receiveErrors = 0; }
                catch
                {
                    if (ct.IsCancellationRequested) break;
                    // transient receive error — keep sniffing, but back off if it
                    // persists (NIC gone/disabled would otherwise busy-spin a core)
                    if (++receiveErrors >= 10)
                    {
                        Log?.Invoke("Sniffer: repeated receive errors (network interface gone?) — retrying every second.");
                        try { ct.WaitHandle.WaitOne(1000); } catch { break; }
                        receiveErrors = 0;
                    }
                    continue;
                }
                if (n < 20) continue;

                try
                {
                    // full IP packet for optional debug capture (any protocol)
                    if (RawIp != null)
                    {
                        var ipPacket = new byte[n];
                        Buffer.BlockCopy(buf, 0, ipPacket, 0, n);
                        RawIp(ipPacket);
                    }

                    int ihl = (buf[0] & 0x0F) * 4;
                    if (ihl < 20 || buf[9] != 17) continue;         // 17 = UDP
                    // drop IP fragments (MF flag or non-zero offset): a non-first
                    // fragment has NO UDP header — its payload bytes would be misread
                    // as ports/length — and a first fragment is truncated data
                    if ((buf[6] & 0x3F) != 0 || buf[7] != 0) continue;
                    if (n < ihl + 8) continue;

                    string src = $"{buf[12]}.{buf[13]}.{buf[14]}.{buf[15]}";
                    int srcPort = (buf[ihl] << 8) | buf[ihl + 1];
                    int udpLen = (buf[ihl + 4] << 8) | buf[ihl + 5];
                    int payloadLen = udpLen - 8;
                    if (payloadLen < 0 || ihl + 8 + payloadLen > n) payloadLen = n - ihl - 8;
                    if (payloadLen <= 0) continue;

                    var payload = new byte[payloadLen];
                    Buffer.BlockCopy(buf, ihl + 8, payload, 0, payloadLen);
                    Packet?.Invoke(src, srcPort, payload);
                }
                catch (Exception ex)
                {
                    // a handler exception (disk full during pcap write, decode bug…)
                    // must not silently kill the capture thread
                    Log?.Invoke($"Sniffer: packet handler error: {ex.Message}");
                }
            }
        }

        void Stop()
        {
            // best-effort shutdown: any of these may throw if Start() never ran
            try { _cts?.Cancel(); } catch { }
            try { _sock?.Dispose(); } catch { }      // unblocks the Receive
            try { _task?.Wait(1000); } catch { }     // observe the loop task
            try { _cts?.Dispose(); } catch { }
            _sock = null; _cts = null; _task = null;
        }

        public void Dispose() => Stop();

        /// <summary>Best-guess local IPv4: an up, non-loopback interface with a gateway.</summary>
        public static IPAddress? PickLocalIp()
        {
            IPAddress? fallback = null;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                bool hasGateway = props.GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                              && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    fallback ??= ua.Address;
                    if (hasGateway) return ua.Address;   // prefer the internet-routed NIC
                }
            }
            return fallback;
        }

        /// <summary>All candidate local IPv4 addresses, for a UI interface picker.</summary>
        public static List<IPAddress> ListLocalIps()
        {
            var addresses = new List<IPAddress>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(ua.Address))
                        addresses.Add(ua.Address);
            }
            return addresses;
        }
    }
}
