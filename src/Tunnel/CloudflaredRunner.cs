using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ACRLiveTiming.Tunnel
{
    /// <summary>
    /// Runs Cloudflare Tunnel in quick mode: launches cloudflared as a child process
    /// (`tunnel --url http://localhost:PORT`) and parses its output for the public
    /// https://xxx.trycloudflare.com URL. The binary is downloaded once on first use
    /// from Cloudflare's official GitHub release into %LOCALAPPDATA%\ACRLiveTiming.
    /// The release is PINNED and the download is verified against Cloudflare's
    /// published SHA-256 before being executed — never a floating "latest" binary.
    /// </summary>
    public sealed class CloudflaredRunner : IDisposable
    {
        // Pinned cloudflared release. To upgrade: bump Version AND Sha256 together,
        // taking the cloudflared-windows-amd64.exe checksum from the release notes:
        // https://github.com/cloudflare/cloudflared/releases/tag/<Version>
        const string Version = "2026.6.1";
        const string Sha256 = "5253e66f1f493c4e13539749f1aa86fd0c61e3072900fec29a44ba046a6d97e2";
        const string DownloadUrl =
            "https://github.com/cloudflare/cloudflared/releases/download/" + Version +
            "/cloudflared-windows-amd64.exe";

        static readonly Regex UrlRe =
            new(@"https://[a-z0-9\-]+\.trycloudflare\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // cloudflared prints the URL immediately, but the tunnel only becomes
        // reachable once an edge connection registers a few seconds later.
        static readonly Regex ReadyRe =
            new(@"[Rr]egistered tunnel connection|Connection .* registered", RegexOptions.Compiled);

        Process? _proc;
        bool _announcedReady;

        public event Action<string>? Log;
        public event Action<string>? UrlFound;
        public event Action? Ready;

        // Version in the file name: bumping the pin naturally re-downloads, and a
        // stale unverified "cloudflared.exe" from older app builds is never reused.
        public static string ExePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ACRLiveTiming", $"cloudflared-{Version}.exe");

        public static bool IsInstalled => File.Exists(ExePath);

        /// <summary>
        /// Download the pinned cloudflared.exe if missing, streaming with progress
        /// logs, then verify its SHA-256 against Cloudflare's published checksum.
        /// Throws (and deletes the download) on mismatch.
        /// </summary>
        public static async Task EnsureExeAsync(Action<string>? log)
        {
            if (IsInstalled) { log?.Invoke($"cloudflared {Version} already installed."); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(ExePath)!);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ACRLiveTiming");
            // ResponseHeadersRead: HttpClient.Timeout does NOT cover the body stream,
            // so a stalled connection would otherwise hang the download forever.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var ct = timeout.Token;
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            log?.Invoke(total.HasValue
                ? $"Downloading cloudflared… ({total.Value / 1024 / 1024} MB)"
                : "Downloading cloudflared…");

            var tmp = ExePath + ".tmp";
            using (var src = await resp.Content.ReadAsStreamAsync(ct))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                long read = 0;
                int lastDecile = 0;      // log every 10%
                long lastMbLogged = 0;   // fallback when length is unknown
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n, ct);
                    read += n;
                    if (total.HasValue && total.Value > 0)
                    {
                        int decile = (int)(read * 10 / total.Value);
                        if (decile > lastDecile)
                        {
                            lastDecile = decile;
                            log?.Invoke($"cloudflared download… {decile * 10}% ({read / 1024 / 1024}/{total.Value / 1024 / 1024} MB)");
                        }
                    }
                    else if (read - lastMbLogged >= 4L * 1024 * 1024)
                    {
                        lastMbLogged = read;
                        log?.Invoke($"cloudflared download… {read / 1024 / 1024} MB");
                    }
                }
            }

            // Integrity check BEFORE the file ever gets an executable name/path.
            string actual = await ComputeSha256Async(tmp);
            if (!actual.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(tmp); } catch { }
                throw new InvalidOperationException(
                    $"cloudflared {Version} checksum mismatch — download discarded. " +
                    $"Expected {Sha256}, got {actual}.");
            }

            if (File.Exists(ExePath)) File.Delete(ExePath);
            File.Move(tmp, ExePath);
            CleanUpOldVersions(log);
            log?.Invoke($"cloudflared {Version} download complete, SHA-256 verified ({new FileInfo(ExePath).Length / 1024 / 1024} MB) — publishing is ready.");
        }

        static async Task<string> ComputeSha256Async(string path)
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = await sha.ComputeHashAsync(stream);
            return Convert.ToHexString(hash);
        }

        /// <summary>Remove superseded cloudflared binaries (older pins) from our folder.</summary>
        static void CleanUpOldVersions(Action<string>? log)
        {
            var dir = Path.GetDirectoryName(ExePath)!;
            var current = Path.GetFileName(ExePath);
            foreach (var file in Directory.GetFiles(dir, "cloudflared*.exe"))
            {
                if (Path.GetFileName(file) == current) continue;
                try
                {
                    File.Delete(file);
                    log?.Invoke($"Removed old {Path.GetFileName(file)}.");
                }
                catch { /* in use or locked — harmless, retried next launch */ }
            }
        }

        public void Start(int port)
        {
            if (Running) Stop();   // never orphan a previous child
            _announcedReady = false;

            // Re-verify the binary against the pinned checksum at EVERY launch, not
            // only at download time: %LOCALAPPDATA% is writable by non-elevated
            // processes of the same user, and this child runs elevated — a swapped
            // file must never be executed. The verifying handle stays open (deny-write)
            // across Process.Start so the file can't be replaced between the hash and
            // the launch.
            FileStream? gate = null;
            try
            {
                gate = new FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                string actual = Convert.ToHexString(SHA256.HashData(gate));
                if (!actual.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    gate.Dispose(); gate = null;
                    try { File.Delete(ExePath); } catch { }
                    throw new InvalidOperationException(
                        "cloudflared failed its pre-launch integrity check and was removed " +
                        $"(expected SHA-256 {Sha256}, got {actual}). Restart the app to re-download it.");
                }

                _proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ExePath,
                        Arguments = $"tunnel --no-autoupdate --url http://localhost:{port}",
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };
                _proc.OutputDataReceived += OnData;
                _proc.ErrorDataReceived += OnData;   // cloudflared logs the URL on stderr
                _proc.Start();
            }
            finally { gate?.Dispose(); }

            // Tie the child to a kill-on-close job object: if this app dies without a
            // clean shutdown (crash, Task Manager), the OS closes the job handle and
            // kills cloudflared with it — the public tunnel never outlives the app.
            KillOnAppExit.Attach(_proc);
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            Log?.Invoke("cloudflared started, waiting for URL…");
        }

        void OnData(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            Log?.Invoke("[cloudflared] " + e.Data);
            var match = UrlRe.Match(e.Data);
            if (match.Success) UrlFound?.Invoke(match.Value);
            if (!_announcedReady && ReadyRe.IsMatch(e.Data))
            {
                _announcedReady = true;
                Ready?.Invoke();
            }
        }

        public bool Running => _proc != null && !_proc.HasExited;

        public void Stop()
        {
            // best-effort teardown: the process may already have exited on its own
            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    _proc.Kill(true);
                    _proc.WaitForExit(3000);   // let output events drain before Dispose
                }
            }
            catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
            _announcedReady = false;   // reusable: a later Start() gets a clean slate
        }

        public void Dispose() => Stop();
    }

    /// <summary>
    /// Windows job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: children attached
    /// to it die when this process does, however this process ends. The job handle is
    /// held for the app's lifetime and closed by the OS at process teardown.
    /// </summary>
    static class KillOnAppExit
    {
        const int JobObjectExtendedLimitInformation = 9;
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                         ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll")]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        static readonly IntPtr _job = Create();

        static IntPtr Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len);
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }

        /// <summary>Best-effort attach — a failure just means no auto-kill safety net.</summary>
        public static void Attach(Process p)
        {
            try { if (_job != IntPtr.Zero) AssignProcessToJobObject(_job, p.Handle); }
            catch { }
        }
    }
}
