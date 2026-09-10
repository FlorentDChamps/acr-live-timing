using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace ACRLiveTiming.Tunnel
{
    /// <summary>
    /// Runs Cloudflare Tunnel in quick mode: launches cloudflared as a child process
    /// (`tunnel --url http://localhost:PORT`) and parses its output for the public
    /// https://xxx.trycloudflare.com URL. The binary is downloaded on first use from
    /// Cloudflare's official GitHub release into %LOCALAPPDATA%\ACRLiveTiming and
    /// refreshed at most once every 30 days (or right after an unexpected exit).
    /// Every downloaded file, and the installed file again before EVERY launch, must
    /// carry a valid Authenticode signature issued to "Cloudflare, Inc." — the same
    /// supply-chain guarantee as a pinned checksum, without tying an ACR Live Timing
    /// release to a cloudflared release.
    /// </summary>
    public sealed class CloudflaredRunner : IDisposable
    {
        const string LatestUrl =
            "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
        const string DownloadUrlFormat =
            "https://github.com/cloudflare/cloudflared/releases/download/{0}/cloudflared-windows-amd64.exe";
        // Downgrade floor: a Cloudflare-signed but older release (served by a broken
        // mirror, or dropped into the folder by another process) is never installed
        // or executed below this version.
        static readonly Version MinVersion = new(2026, 8, 3);
        static readonly TimeSpan RecheckInterval = TimeSpan.FromDays(30);

        static readonly Regex TagRe =
            new(@"/releases/download/(\d{4}\.\d{1,2}\.\d{1,3})/", RegexOptions.Compiled);
        static readonly Regex InstalledRe =
            new(@"^cloudflared-(\d{4}\.\d{1,2}\.\d{1,3})\.exe$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex UrlRe =
            new(@"https://[a-z0-9\-]+\.trycloudflare\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // cloudflared prints the URL immediately, but the tunnel only becomes
        // reachable once an edge connection registers a few seconds later.
        static readonly Regex ReadyRe =
            new(@"[Rr]egistered tunnel connection|Connection .* registered", RegexOptions.Compiled);

        static int _ensureBusy;   // 1 while EnsureExeAsync runs (startup + post-crash may overlap)

        Process? _proc;
        bool _announcedReady;

        public event Action<string>? Log;
        public event Action<string>? UrlFound;
        public event Action? Ready;
        /// <summary>Raised only when cloudflared exits without this runner stopping it.</summary>
        public event Action<int>? ExitedUnexpectedly;

        static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ACRLiveTiming");

        // Version in the file name: a newer download never overwrites a running binary,
        // and a stale "cloudflared.exe" from older app builds is never picked up.
        static string PathFor(Version v) => Path.Combine(Dir, $"cloudflared-{v}.exe");
        static string LastCheckPath => Path.Combine(Dir, "cloudflared.lastcheck");

        /// <summary>Highest-versioned installed binary at or above the floor, if any.</summary>
        static (string Path, Version Version)? FindInstalled()
        {
            if (!Directory.Exists(Dir)) return null;
            (string, Version)? best = null;
            foreach (var file in Directory.GetFiles(Dir, "cloudflared-*.exe"))
            {
                var m = InstalledRe.Match(Path.GetFileName(file));
                if (!m.Success || !Version.TryParse(m.Groups[1].Value, out var v)) continue;
                if (v < MinVersion) continue;
                if (best == null || v > best.Value.Item2) best = (file, v);
            }
            return best;
        }

        public static bool IsInstalled => FindInstalled() != null;

        /// <summary>
        /// Make a verified cloudflared available: download the latest release when
        /// none is installed, otherwise re-check GitHub for a newer one at most every
        /// 30 days (always when <paramref name="force"/>). A download is Authenticode-
        /// verified before it takes an executable name; the previous version is only
        /// removed after the new one passed. Throws on network or verification failure,
        /// leaving any installed binary untouched.
        /// </summary>
        public static async Task EnsureExeAsync(Action<string>? log, bool force = false)
        {
            if (Interlocked.Exchange(ref _ensureBusy, 1) == 1) return;   // already running
            try { await EnsureExeCoreAsync(log, force); }
            finally { Interlocked.Exchange(ref _ensureBusy, 0); }
        }

        static async Task EnsureExeCoreAsync(Action<string>? log, bool force)
        {
            var installed = FindInstalled();
            if (installed != null && !force && !RecheckDue())
            {
                log?.Invoke($"cloudflared {installed.Value.Version} installed.");
                CleanUpOldVersions(installed.Value.Path, log);   // a previous cleanup may have hit a running binary
                return;
            }
            Directory.CreateDirectory(Dir);

            // ResponseHeadersRead: HttpClient.Timeout does NOT cover the body stream,
            // so a stalled connection would otherwise hang the download forever.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var ct = timeout.Token;
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ACRLiveTiming");

            // GitHub answers /releases/latest/download/<asset> with a redirect to the
            // versioned asset URL: the tag in that URL is the latest release. No API
            // call, no rate limit.
            Version latest;
            using (var probe = await http.GetAsync(LatestUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                var location = probe.Headers.Location?.ToString() ?? "";
                var m = TagRe.Match(location);
                if ((int)probe.StatusCode is < 300 or >= 400 || !m.Success
                    || !Version.TryParse(m.Groups[1].Value, out latest!))
                    throw new InvalidOperationException(
                        $"could not determine the latest cloudflared release (HTTP {(int)probe.StatusCode}, Location \"{location}\").");
            }

            if (installed != null && latest <= installed.Value.Version)
            {
                TouchLastCheck();
                log?.Invoke($"cloudflared {installed.Value.Version} is up to date.");
                return;
            }
            if (latest < MinVersion)
                throw new InvalidOperationException(
                    $"latest cloudflared release {latest} is below the supported floor {MinVersion} — not installed.");

            var exePath = PathFor(latest);
            var tmp = exePath + ".tmp";
            // The versioned asset URL redirects once more to GitHub's CDN: follow it.
            using var follow = new HttpClient();
            follow.DefaultRequestHeaders.UserAgent.ParseAdd("ACRLiveTiming");
            using var resp = await follow.GetAsync(string.Format(DownloadUrlFormat, latest),
                HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await DownloadToAsync(resp, tmp, log, ct);

            // Signature check BEFORE the file ever gets an executable name/path.
            try { Authenticode.RequireCloudflareSignature(tmp); }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                throw new InvalidOperationException(
                    $"cloudflared {latest} failed signature verification — download discarded. {ex.Message}");
            }

            if (File.Exists(exePath)) File.Delete(exePath);
            File.Move(tmp, exePath);
            CleanUpOldVersions(exePath, log);
            TouchLastCheck();
            log?.Invoke(installed == null
                ? $"cloudflared {latest} download complete, Cloudflare signature verified ({new FileInfo(exePath).Length / 1024 / 1024} MB) — publishing is ready."
                : $"cloudflared updated {installed.Value.Version} → {latest}, Cloudflare signature verified ({new FileInfo(exePath).Length / 1024 / 1024} MB).");
        }

        static async Task DownloadToAsync(HttpResponseMessage resp, string tmp, Action<string>? log, CancellationToken ct)
        {
            long? total = resp.Content.Headers.ContentLength;
            log?.Invoke(total.HasValue
                ? $"Downloading cloudflared… ({total.Value / 1024 / 1024} MB)"
                : "Downloading cloudflared…");

            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
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

        static bool RecheckDue()
        {
            try
            {
                if (!File.Exists(LastCheckPath)) return true;
                var last = DateTime.Parse(File.ReadAllText(LastCheckPath).Trim(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                return DateTime.UtcNow - last >= RecheckInterval;
            }
            catch { return true; }
        }

        static void TouchLastCheck()
        {
            try { File.WriteAllText(LastCheckPath, DateTime.UtcNow.ToString("O")); } catch { }
        }

        /// <summary>Remove superseded cloudflared binaries from our folder.</summary>
        static void CleanUpOldVersions(string keep, Action<string>? log)
        {
            foreach (var file in Directory.GetFiles(Dir, "cloudflared*.exe"))
            {
                if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    File.Delete(file);
                    log?.Invoke($"Removed old {Path.GetFileName(file)}.");
                }
                catch { /* in use or locked — harmless, retried after the next download */ }
            }
        }

        public void Start(int port)
        {
            if (_proc != null) Stop();   // never orphan a previous (possibly exited) child
            _announcedReady = false;

            var installed = FindInstalled()
                ?? throw new InvalidOperationException("cloudflared is not installed yet.");
            var exePath = installed.Path;

            // Re-verify the signature at EVERY launch, not only at download time:
            // %LOCALAPPDATA% is writable by non-elevated processes of the same user,
            // and this child runs elevated — a swapped file must never be executed
            // unless it is itself a Cloudflare-signed binary. The verifying handle
            // stays open (deny-write) across Process.Start so the file can't be
            // replaced between the check and the launch.
            FileStream? gate = null;
            try
            {
                gate = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                try { Authenticode.RequireCloudflareSignature(exePath); }
                catch (Exception ex)
                {
                    gate.Dispose(); gate = null;
                    try { File.Delete(exePath); } catch { }
                    throw new InvalidOperationException(
                        $"cloudflared failed its pre-launch signature check and was removed ({ex.Message}). " +
                        "Restart the app to re-download it.");
                }

                _proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
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
                _proc.Exited += OnProcessExited;
                _proc.Start();
            }
            finally { gate?.Dispose(); }

            // Tie the child to a kill-on-close job object: if this app dies without a
            // clean shutdown (crash, Task Manager), the OS closes the job handle and
            // kills cloudflared with it — the public tunnel never outlives the app.
            KillOnAppExit.Attach(_proc);
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            Log?.Invoke($"cloudflared {installed.Version} started, waiting for URL…");
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

        void OnProcessExited(object? sender, EventArgs e)
        {
            if (sender is not Process proc || !ReferenceEquals(_proc, proc)) return;

            int exitCode;
            try { exitCode = proc.ExitCode; }
            catch { return; }

            // Clear the reference before notifying subscribers: an immediate retry is
            // then safe, and a late exit from an old process cannot affect it.
            _proc = null;
            _announcedReady = false;
            Log?.Invoke($"cloudflared stopped unexpectedly (exit code {exitCode}). The public link is offline.");
            ExitedUnexpectedly?.Invoke(exitCode);
        }

        public bool Running => _proc != null && !_proc.HasExited;

        public void Stop()
        {
            // best-effort teardown: the process may already have exited on its own
            var proc = _proc;
            _proc = null;
            _announcedReady = false;
            if (proc == null) return;
            proc.Exited -= OnProcessExited;   // a user-requested stop is not a failure
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                    proc.WaitForExit(3000);   // let output events drain before Dispose
                }
            }
            catch { }
            try { proc.Dispose(); } catch { }
        }

        public void Dispose() => Stop();
    }

    /// <summary>
    /// Authenticode verification through WinVerifyTrust (the same policy Explorer and
    /// PowerShell's Get-AuthenticodeSignature apply: chain to a trusted root, revocation
    /// where reachable, timestamp honoured) plus an explicit check of the signer identity.
    /// </summary>
    static class Authenticode
    {
        const string ExpectedSigner = "Cloudflare, Inc.";

        static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        const uint WTD_UI_NONE = 2;
        const uint WTD_REVOKE_WHOLECHAIN = 1;
        const uint WTD_CHOICE_FILE = 1;
        const uint WTD_STATEACTION_VERIFY = 1;
        const uint WTD_STATEACTION_CLOSE = 2;
        const uint WTD_REVOCATION_CHECK_CHAIN = 0x40;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

        /// <summary>Throws unless <paramref name="path"/> carries a valid signature by Cloudflare.</summary>
        public static void RequireCloudflareSignature(string path)
        {
            int status = Verify(path);
            if (status != 0)
                throw new InvalidOperationException($"Authenticode status 0x{status:X8}");

            // WinVerifyTrust proves "signed by someone Windows trusts"; the signer must
            // also be Cloudflare — any other valid code-signing certificate is refused.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            string signer = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (!string.Equals(signer, ExpectedSigner, StringComparison.Ordinal))
                throw new InvalidOperationException($"signed by \"{signer}\", expected \"{ExpectedSigner}\"");
        }

        static int Verify(string path)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
            };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            IntPtr pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = pFile,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_REVOCATION_CHECK_CHAIN,
                };
                Marshal.StructureToPtr(data, pData, false);
                int status = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);

                // Release the state handle WinVerifyTrust allocated for us.
                data = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                Marshal.StructureToPtr(data, pData, true);
                WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);
                return status;
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
                Marshal.FreeHGlobal(pFile);
                Marshal.FreeHGlobal(pData);
            }
        }
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
