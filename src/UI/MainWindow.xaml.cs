using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ACRLiveTiming.Model;
using ACRLiveTiming.Net;
using ACRLiveTiming.Tunnel;
using ACRLiveTiming.Web;
using Microsoft.Win32;

namespace ACRLiveTiming.UI
{
    public partial class MainWindow : Window
    {
        static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        readonly Engine _engine = new();
        readonly RawSocketSniffer _sniffer = new();
        readonly WebServer _web;
        readonly CloudflaredRunner _tunnel = new();
        DispatcherTimer? _timer;
        List<string> _lastStageKey = new();
        PcapWriter? _pcap;
        volatile bool _replaying;
        volatile bool _stopReplay;
        volatile bool _hideNations;    // privacy: strip nationality from the shared view
        readonly List<OverlayWindow> _overlays = new();   // open streaming overlays
        OverlayWindow? _tableOverlay, _progressOverlay;
        readonly AppSettings _settings = AppSettings.Load();
        bool _settingsLoaded;          // gates SaveSettings until ApplySettings has run
        volatile string? _stateJson;   // cached /state payload, invalidated on Matrix.Changed

        public MainWindow()
        {
            InitializeComponent();
            Title = "ACR Live Timing " + AppInfo.Version;   // so bug reports carry a version
            RestoreWindowBounds();   // before the window is shown => no reposition flicker
            // saved theme if any, else follow the OS light/dark setting on first run
            ApplyTheme(_settings.HasTheme ? _settings.Dark : OsPrefersDark());
            _web = new WebServer(BuildStateJson,
                () => (_engine.Matrix.PageTitle, _engine.Matrix.PageDescription));
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        // ---- theme -----------------------------------------------------------

        bool _dark;
        bool _syncingTheme;   // guards the toggle<->ApplyTheme round-trip

        void ThemeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingTheme) return;
            ApplyTheme(ThemeToggle.IsChecked == true);
        }

        void ApplyTheme(bool dark)
        {
            _dark = dark;
            var palette = dark ? DarkPalette : LightPalette;
            foreach (var kv in palette)
                Resources[kv.Key] = new SolidColorBrush(kv.Value);
            if (ThemeToggle != null && ThemeToggle.IsChecked != dark)
            {
                _syncingTheme = true;         // avoid re-entering via Checked/Unchecked
                ThemeToggle.IsChecked = dark;
                _syncingTheme = false;
            }
            if (ThemeLabel != null) ThemeLabel.Text = dark ? "Dark mode" : "Light mode";
            UpdateState();   // StateDot pulls its colour from the palette
        }

        static bool OsPrefersDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
            }
            catch { /* registry unreadable — fall back to light */ }
            return false;
        }

        static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        static readonly Dictionary<string, Color> LightPalette = new()
        {
            ["Brush.Window"]        = C(0xF5, 0xF6, 0xF8),
            ["Brush.Card"]          = C(0xFF, 0xFF, 0xFF),
            ["Brush.Input"]         = C(0xFF, 0xFF, 0xFF),
            ["Brush.Console"]       = C(0xFB, 0xFC, 0xFD),
            ["Brush.Border"]        = C(0xD0, 0xD7, 0xDE),
            ["Brush.BorderHover"]   = C(0xBF, 0xC7, 0xCF),
            ["Brush.Text"]          = C(0x1F, 0x23, 0x28),
            ["Brush.ButtonText"]    = C(0x24, 0x29, 0x2F),
            ["Brush.ButtonBg"]      = C(0xFF, 0xFF, 0xFF),
            ["Brush.ButtonHover"]   = C(0xF3, 0xF4, 0xF6),
            ["Brush.ButtonPressed"] = C(0xE7, 0xE9, 0xEC),
            ["Brush.Muted"]         = C(0x65, 0x6D, 0x76),
            ["Brush.Faint"]         = C(0x8C, 0x95, 0x9F),
            ["Brush.Accent"]        = C(0x1A, 0x7F, 0x37),   // connected dot / switch on
            ["Brush.DotIdle"]       = C(0x8B, 0x94, 0x9E),   // searching dot
            ["Brush.SwitchOff"]     = C(0xCD, 0xD3, 0xDA),
            ["Brush.SwitchOn"]      = C(0x65, 0x6D, 0x76),   // grey, no colour accent
            ["Brush.SwitchThumb"]   = C(0xFF, 0xFF, 0xFF),
            ["Brush.ScrollTrack"]      = C(0xEC, 0xEE, 0xF1),
            ["Brush.ScrollThumb"]      = C(0xC4, 0xCB, 0xD3),
            ["Brush.ScrollThumbHover"] = C(0xAE, 0xB6, 0xBF),
        };

        static readonly Dictionary<string, Color> DarkPalette = new()
        {
            ["Brush.Window"]        = C(0x0E, 0x11, 0x16),
            ["Brush.Card"]          = C(0x16, 0x1B, 0x22),
            ["Brush.Input"]         = C(0x0D, 0x11, 0x17),
            ["Brush.Console"]       = C(0x0D, 0x11, 0x17),
            ["Brush.Border"]        = C(0x30, 0x36, 0x3D),
            ["Brush.BorderHover"]   = C(0x44, 0x4C, 0x56),
            ["Brush.Text"]          = C(0xE6, 0xED, 0xF3),
            ["Brush.ButtonText"]    = C(0xE6, 0xED, 0xF3),
            ["Brush.ButtonBg"]      = C(0x21, 0x26, 0x2D),
            ["Brush.ButtonHover"]   = C(0x2A, 0x31, 0x39),
            ["Brush.ButtonPressed"] = C(0x30, 0x36, 0x3D),
            ["Brush.Muted"]         = C(0x8B, 0x94, 0x9E),
            ["Brush.Faint"]         = C(0x6E, 0x76, 0x81),
            ["Brush.Accent"]        = C(0x3F, 0xB9, 0x50),
            ["Brush.DotIdle"]       = C(0x8B, 0x94, 0x9E),
            ["Brush.SwitchOff"]     = C(0x3A, 0x42, 0x4C),
            ["Brush.SwitchOn"]      = C(0x8B, 0x94, 0x9E),   // grey, no colour accent
            ["Brush.SwitchThumb"]   = C(0xE6, 0xED, 0xF3),
            ["Brush.ScrollTrack"]      = C(0x16, 0x1B, 0x22),
            ["Brush.ScrollThumb"]      = C(0x3A, 0x42, 0x4C),
            ["Brush.ScrollThumbHover"] = C(0x4C, 0x55, 0x61),
        };

        /// <summary>
        /// /state payload, serialized once per matrix change instead of once per poll
        /// (several viewers × short poll interval would otherwise rebuild the same
        /// JSON). Cache only kept if nothing changed during serialization, so a poll
        /// can never pin a stale snapshot.
        /// </summary>
        string BuildStateJson()
        {
            var cached = _stateJson;
            if (cached != null) return cached;
            long version = _engine.Matrix.Version;
            var view = _engine.Matrix.BuildView();
            // privacy: drop nationality before it ever reaches the shared page (the
            // Rows list is freshly built per call, so mutating it here is safe).
            if (_hideNations)
                foreach (var row in view.Rows) row.Nation = null;
            var json = JsonSerializer.Serialize(view, JsonOpts);
            if (_engine.Matrix.Version == version)
            {
                _stateJson = json;
                // Re-check AFTER publishing: a change can slip between the check and
                // the assignment (its Changed handler nulls the cache, which we would
                // overwrite with a pre-change snapshot — pinned until the next change).
                // Version bumps before Changed fires, so a second read catches it.
                if (_engine.Matrix.Version != version) _stateJson = null;
            }
            return json;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            _engine.Log += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));
            _engine.StateChanged += _ => Dispatcher.BeginInvoke(() => UpdateState());
            _engine.Matrix.Changed += OnMatrixChanged;

            _sniffer.Log += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));
            _sniffer.Packet += OnLivePacket;
            _tunnel.Log += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));
            _tunnel.UrlFound += url => Dispatcher.BeginInvoke(() =>
            {
                PublicUrlBox.Text = url;
                AppendLog("Public URL: " + url + "  (wait for \"link is now live\" before opening)");
            });
            _tunnel.Ready += () => Dispatcher.BeginInvoke(() =>
                AppendLog("Tunnel connected — link is now live: " + PublicUrlBox.Text));

            _web.Log += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));

            ApplySettings();   // restore saved options / text (theme already applied)

            // start sniffing
            try
            {
                var ip = RawSocketSniffer.PickLocalIp();
                if (ip == null) { AppendLog("No network interface found."); }
                else { _sniffer.Start(ip); }
            }
            catch (Exception ex) { AppendLog("Sniff error: " + ex.Message); }

            // 1s tick: connection-loss detection
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                _engine.Tick();
                if (_pcap != null)
                    PcapStatus.Text = $"{_pcap.Count} packets → {Path.GetFileName(_pcap.Path)}";
            };
            _timer.Start();

            UpdateState();

            // pre-download cloudflared in the background
            _ = CloudflaredRunner.EnsureExeAsync(msg => Dispatcher.BeginInvoke(() => AppendLog(msg)))
                .ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Dispatcher.BeginInvoke(() => AppendLog("cloudflared not downloaded: " + t.Exception.GetBaseException().Message));
                });

            AppendLog("Ready. Join an ACR lobby — the server is detected automatically.");

            // startup restore is done: enable saving and write once now, so the
            // .config file exists from the first run even if the process is later
            // killed (e.g. Stop Debugging) instead of closed gracefully.
            _settingsLoaded = true;
            SaveSettings();
        }

        void OnMatrixChanged()
        {
            _stateJson = null;   // next /state poll re-serializes
            // page pulls fresh data via /state polling; just refresh the stage list
            Dispatcher.BeginInvoke(() => RebuildStagesIfChanged());
        }

        // ---- UI events -------------------------------------------------------

        void StartWebBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_web.Running) { StopWeb(); return; }   // button doubles as Stop
            if (!int.TryParse(PortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
            {
                AppendLog("Invalid port."); return;
            }
            try
            {
                _web.Start(port);
                var lan = _sniffer.LocalIp?.ToString() ?? "localhost";
                LocalUrlBox.Text = $"http://{lan}:{port}/";
                StartWebBtn.Content = "Stop";
                PortBox.IsEnabled = false;
                PublishBtn.IsEnabled = true;
                SetOverlayControlsEnabled(true);   // overlays load from the server
                // reopen overlays that were open when the app last closed
                if (_settings.Table.Open) OverlayTableToggle.IsChecked = true;
                if (_settings.Progress.Open) OverlayProgressToggle.IsChecked = true;
                SaveSettings();   // checkpoint the options set before racing
            }
            catch (Exception ex) { AppendLog("Web server error: " + ex.Message); }
        }

        // overlay controls only make sense while the web server is up (overlays load
        // their page from it) — disabled otherwise
        void SetOverlayControlsEnabled(bool on)
        {
            OverlayTableToggle.IsEnabled = on;
            TableTopmostToggle.IsEnabled = on;
            TableLockToggle.IsEnabled = on;
            OverlayProgressToggle.IsEnabled = on;
            ProgTopmostToggle.IsEnabled = on;
            ProgLockToggle.IsEnabled = on;
            TableCopyUrlBtn.IsEnabled = on;
            ProgCopyUrlBtn.IsEnabled = on;
        }

        // Copy the OBS Browser Source URL for an overlay view. Uses the LAN IP (so it
        // also works when OBS runs on another PC on the network) and rides the view's
        // configured background opacity in ?bg=, matching the desktop overlay's look.
        void CopyOverlayUrl(string view, int opacity)
        {
            var host = _sniffer.LocalIp?.ToString() ?? "localhost";
            var url = $"http://{host}:{_web.Port}/?view={view}&bg={opacity}";
            try { Clipboard.SetText(url); AppendLog("OBS source URL copied: " + url); }
            catch (Exception ex) { AppendLog("Clipboard error: " + ex.Message); }
        }
        void CopyTableUrl_Click(object sender, RoutedEventArgs e)
            => CopyOverlayUrl("classification", ParseOpacity(TableOpacityBox.Text, _settings.Table.Opacity));
        void CopyProgUrl_Click(object sender, RoutedEventArgs e)
            => CopyOverlayUrl("progress", ParseOpacity(ProgOpacityBox.Text, _settings.Progress.Opacity));

        void StopWeb()
        {
            if (_tunnel.Running) StopTunnel();   // tunnel needs the web server; stop it first
            // overlays load from the server: close them so they don't sit blank. This is
            // a FORCED close (server going down), not a user close — capture which were
            // open first, because each Close() fires the Closed handler that flips
            // _settings.*.Open to false; we restore it so a later Start reopens them.
            bool tableWasOpen = _tableOverlay != null;
            bool progWasOpen = _progressOverlay != null;
            _tableOverlay?.Close();
            _progressOverlay?.Close();
            _settings.Table.Open = tableWasOpen;
            _settings.Progress.Open = progWasOpen;
            _web.Stop();
            LocalUrlBox.Text = "";
            StartWebBtn.Content = "Start";
            PortBox.IsEnabled = true;
            PublishBtn.IsEnabled = false;
            SetOverlayControlsEnabled(false);
            SaveSettings();   // persist the preserved open-intent
            AppendLog("Web server stopped.");
        }

        void PublishBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_tunnel.Running) { StopTunnel(); return; }   // button doubles as Unpublish
            if (!_web.Running) { AppendLog("Start the web server first."); return; }
            if (!CloudflaredRunner.IsInstalled)
            {
                AppendLog("cloudflared not ready yet (downloading).");
                return;
            }
            // Privacy notice: a public link exposes third-party data. Confirm first.
            var confirm = MessageBox.Show(this,
                "Publishing creates a public link on the internet. Anyone who has the link "
                + "can see the leaderboard — including other drivers' pseudonyms"
                + (_hideNations ? "" : " and nationalities")
                + " — for as long as this app stays open.\n\n"
                + "Only share it with people who should see it. Continue?",
                "Publish a public link?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
            try { _tunnel.Start(_web.Port); PublishBtn.Content = "Unpublish"; }
            catch (Exception ex) { AppendLog("Tunnel error: " + ex.Message); }
        }

        void StopTunnel()
        {
            _tunnel.Stop();
            PublicUrlBox.Text = "";
            PublishBtn.Content = "Publish";
            AppendLog("Tunnel stopped — the public link is now offline.");
        }

        // Keep the identity enrichment (nations, name aliases, pseudo↔progression
        // bindings): those replicate only on join / channel-open and won't come back
        // mid-session. Replay (below) still does a full reset — a new capture is a new
        // session.
        void ResetBtn_Click(object sender, RoutedEventArgs e) => _engine.ManualReset(keepIdentity: true);

        void OpenLocalBtn_Click(object sender, RoutedEventArgs e) => OpenUrl(LocalUrlBox.Text);
        void OpenPublicBtn_Click(object sender, RoutedEventArgs e) => OpenUrl(PublicUrlBox.Text);
        void CopyPublicBtn_Click(object sender, RoutedEventArgs e) => CopyUrl(PublicUrlBox.Text);

        void CopyUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) { AppendLog("No URL yet."); return; }
            try { Clipboard.SetText(url); AppendLog("Link copied to the clipboard."); }
            catch (Exception ex) { AppendLog("Copy error: " + ex.Message); }
        }

        void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) { AppendLog("No URL yet."); return; }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex) { AppendLog("Open URL error: " + ex.Message); }
        }

        void SavePcap_Checked(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save capture",
                Filter = "pcap capture (*.pcap)|*.pcap",
                FileName = $"acr_live_{DateTime.Now:yyyyMMdd_HHmmss}.pcap",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != true)
            {
                SavePcapCheck.IsChecked = false;   // user cancelled
                return;
            }
            try
            {
                _pcap = new PcapWriter(dlg.FileName);
                _sniffer.RawIp += _pcap.WriteIpPacket;
                AppendLog("Recording pcap → " + dlg.FileName);
            }
            catch (Exception ex)
            {
                AppendLog("pcap error: " + ex.Message);
                SavePcapCheck.IsChecked = false;
            }
        }

        void OnLivePacket(string ip, int port, byte[] payload)
        {
            if (_replaying) return;   // ignore live traffic while replaying a file
            _engine.OnPacket(ip, port, payload);
        }

        async void ReplayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_replaying) { _stopReplay = true; return; }   // button doubles as Stop replay
            var dlg = new OpenFileDialog
            {
                Title = "Replay a capture",
                Filter = "pcap capture (*.pcap)|*.pcap|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            var file = dlg.FileName;

            _replaying = true;
            _stopReplay = false;
            ReplayBtn.Content = "Stop replay";
            _engine.ManualReset();

            // Pace the replay so the evolution is visible on the page: after each
            // packet that actually changes the classification (a finisher, a new run
            // or a stage label), wait the configured pause. 0 = replay full speed.
            double pause = 0;
            var pauseText = ReplayPauseBox.Text.Trim().Replace(',', '.');
            if (double.TryParse(pauseText, NumberStyles.Float, CultureInfo.InvariantCulture, out var pv) && pv >= 0)
                pause = pv;

            AppendLog($"Replaying {file} … (pause {pause:0.##}s per update)");
            bool stopped = false;
            try
            {
                int count = await Task.Run(async () =>
                {
                    int c = 0;
                    var delay = TimeSpan.FromSeconds(pause);
                    foreach (var (ip, port, payload) in PcapReplay.ReadUdp(file))
                    {
                        if (_stopReplay) { stopped = true; break; }
                        long before = _engine.Matrix.Version;
                        _engine.OnPacket(ip, port, payload);
                        c++;
                        if (pause > 0 && _engine.Matrix.Version != before) await Task.Delay(delay);
                    }
                    return c;
                });
                AppendLog(stopped ? $"Replay stopped ({count} UDP packets)." : $"Replay done ({count} UDP packets).");
                _engine.RefreshInfoAsync();   // decode nation/car from the replayed data
            }
            catch (Exception ex) { AppendLog("Replay error: " + ex.Message); }
            finally { _replaying = false; _stopReplay = false; ReplayBtn.Content = "Replay a .pcap…"; }
        }

        void ReplayPauseStep_Click(object sender, RoutedEventArgs e)
        {
            double step = double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
            var text = ReplayPauseBox.Text.Trim().Replace(',', '.');
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cur);
            var next = Math.Max(0, Math.Round(cur + step, 1));
            ReplayPauseBox.Text = next.ToString("0.#", CultureInfo.InvariantCulture);
        }

        void SavePcap_Unchecked(object sender, RoutedEventArgs e) => StopPcap();

        void FinishGating_Changed(object sender, RoutedEventArgs e)
        {
            if (_engine != null) _engine.Matrix.FinishGating = FinishGatingCheck.IsChecked == true;
        }

        void HideNations_Changed(object sender, RoutedEventArgs e)
        {
            _hideNations = HideNationsCheck.IsChecked == true;
            _stateJson = null;   // re-serialize the shared view with the new privacy setting
        }

        void StopPcap()
        {
            if (_pcap == null) return;
            _sniffer.RawIp -= _pcap.WriteIpPacket;
            long n = _pcap.Count;
            _pcap.Dispose();
            _pcap = null;
            AppendLog($"pcap saved ({n} packets).");
            PcapStatus.Text = "";
        }

        void PctBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_engine == null) return;   // designer / pre-init safety
            var text = PctBox.Text.Trim().Replace(',', '.');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct >= 0 && pct <= 500)
                _engine.Matrix.Pct = pct / 100.0;
        }

        void ProgressWindowBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_engine == null) return;   // designer / pre-init safety
            var text = ProgressWindowBox.Text.Trim().Replace(',', '.');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var km) && km >= 0.1 && km <= 50)
                _engine.Matrix.ProgressWindowKm = km;
        }

        // classification: fit+zoom on WIDTH, height only reveals more rows
        void OverlayTableToggle_Checked(object sender, RoutedEventArgs e)
        {
            var o = OpenOverlay("classification", "Classification", 380, 620, "table", false, OverlayTableToggle, _settings.Table);
            _tableOverlay = o;
            if (o == null) return;
            _settings.Table.Open = true;
            o.Closed += (_, _) => { if (_shuttingDown) return; CaptureBounds(o, _settings.Table); _settings.Table.Open = false; _tableOverlay = null; OverlayTableToggle.IsChecked = false; SaveSettings(); };
            SaveSettings();
        }
        void OverlayTableToggle_Unchecked(object sender, RoutedEventArgs e) => _tableOverlay?.Close();

        // progress: fit+zoom on HEIGHT, width stretches the bar length
        void OverlayProgressToggle_Checked(object sender, RoutedEventArgs e)
        {
            var o = OpenOverlay("progress", "Progress", 960, 150, ".progwrap", true, OverlayProgressToggle, _settings.Progress);
            _progressOverlay = o;
            if (o == null) return;
            _settings.Progress.Open = true;
            o.Closed += (_, _) => { if (_shuttingDown) return; CaptureBounds(o, _settings.Progress); _settings.Progress.Open = false; _progressOverlay = null; OverlayProgressToggle.IsChecked = false; SaveSettings(); };
            SaveSettings();
        }
        void OverlayProgressToggle_Unchecked(object sender, RoutedEventArgs e) => _progressOverlay?.Close();

        OverlayWindow? OpenOverlay(string view, string title, double w, double h,
                                   string? fitSelector, bool byHeight, ToggleButton toggle, OverlayState saved)
        {
            if (!_web.Running)
            {
                AppendLog("Start the web server first — overlays load from it.");
                toggle.IsChecked = false;
                return null;
            }
            // localhost: the overlay runs on this PC, no need for the LAN IP. bg= sets
            // the initial background opacity so the first paint is already correct.
            var url = $"http://localhost:{_web.Port}/?view={view}&bg={saved.Opacity}";
            try
            {
                var overlay = new OverlayWindow(url, title, w, h, fitSelector, byHeight, saved);
                overlay.Closed += (_, _) => _overlays.Remove(overlay);
                _overlays.Add(overlay);
                overlay.Show();
                overlay.SetTopmost(saved.Topmost);          // apply THIS overlay's state
                overlay.SetClickThrough(saved.Locked);
                overlay.SetBackgroundOpacity(saved.Opacity);
                return overlay;
            }
            catch (Exception ex) { AppendLog("Overlay error: " + ex.Message); toggle.IsChecked = false; return null; }
        }

        static void CaptureBounds(OverlayWindow o, OverlayState s)
        {
            s.Left = o.Left; s.Top = o.Top;
            s.Width = o.ActualWidth; s.Height = o.ActualHeight;
        }

        // ---- per-overlay chrome (topmost / lock / opacity), applied to the live
        //      window if open and always persisted ---------------------------------
        void TableTopmost_Changed(object sender, RoutedEventArgs e)
        {
            _settings.Table.Topmost = TableTopmostToggle.IsChecked == true;
            _tableOverlay?.SetTopmost(_settings.Table.Topmost);
            SaveSettings();
        }
        void TableLock_Changed(object sender, RoutedEventArgs e)
        {
            _settings.Table.Locked = TableLockToggle.IsChecked == true;
            _tableOverlay?.SetClickThrough(_settings.Table.Locked);
            SaveSettings();
        }
        void ProgTopmost_Changed(object sender, RoutedEventArgs e)
        {
            _settings.Progress.Topmost = ProgTopmostToggle.IsChecked == true;
            _progressOverlay?.SetTopmost(_settings.Progress.Topmost);
            SaveSettings();
        }
        void ProgLock_Changed(object sender, RoutedEventArgs e)
        {
            _settings.Progress.Locked = ProgLockToggle.IsChecked == true;
            _progressOverlay?.SetClickThrough(_settings.Progress.Locked);
            SaveSettings();
        }

        void TableOpacity_Changed(object sender, TextChangedEventArgs e)
            => ApplyOverlayOpacity(TableOpacityBox, _settings.Table, _tableOverlay);
        void ProgOpacity_Changed(object sender, TextChangedEventArgs e)
            => ApplyOverlayOpacity(ProgOpacityBox, _settings.Progress, _progressOverlay);
        void TableOpacityStep_Click(object sender, RoutedEventArgs e) => StepOpacity(TableOpacityBox, sender);
        void ProgOpacityStep_Click(object sender, RoutedEventArgs e) => StepOpacity(ProgOpacityBox, sender);

        void ApplyOverlayOpacity(TextBox box, OverlayState s, OverlayWindow? o)
        {
            int pct = ParseOpacity(box.Text, s.Opacity);
            s.Opacity = pct;
            o?.SetBackgroundOpacity(pct);
            SaveSettings();
        }
        // steppers just nudge the text by ±10 (clamped); the box's TextChanged then
        // applies + persists it.
        static void StepOpacity(TextBox box, object sender)
        {
            int step = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
            int next = Math.Clamp(ParseOpacity(box.Text, 80) + step, 0, 100);
            box.Text = next.ToString(CultureInfo.InvariantCulture);
        }
        static int ParseOpacity(string text, int fallback)
            => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
               ? Math.Clamp(v, 0, 100) : fallback;

        void PageTitleBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_engine == null) return;   // designer / pre-init safety
            _engine.Matrix.PageTitle = PageTitleBox.Text;
        }

        void PageDescBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_engine == null) return;
            _engine.Matrix.PageDescription = PageDescBox.Text;
        }

        void ProgressWindowStep_Click(object sender, RoutedEventArgs e)
        {
            double step = double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
            var text = ProgressWindowBox.Text.Trim().Replace(',', '.');
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cur);
            var next = Math.Max(0.1, Math.Round(cur + step, 1));
            ProgressWindowBox.Text = next.ToString("0.0", CultureInfo.InvariantCulture);
        }

        // ---- stage checkboxes ------------------------------------------------

        void RebuildStagesIfChanged()
        {
            var view = _engine.Matrix.BuildView();
            var key = view.AllStages.Select(s => s.Id + "=" + s.Name).ToList();
            if (key.SequenceEqual(_lastStageKey)) return;   // avoid churn
            _lastStageKey = key;

            StagesPanel.Children.Clear();
            if (view.AllStages.Count == 0)
            {
                StagesPanel.Children.Add(new TextBlock
                {
                    Text = "(no stage yet)",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x65, 0x6D, 0x76))
                });
                return;
            }
            foreach (var stage in view.AllStages)
            {
                // full special name, ellipsized if it overflows the narrow panel,
                // with the untruncated name available on hover
                var label = new TextBlock
                {
                    Text = stage.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = stage.Name
                };
                var checkBox = new CheckBox
                {
                    Content = label,
                    IsChecked = !stage.Discarded,
                    Margin = new Thickness(0, 2, 0, 2),
                    Tag = stage.Id
                };
                checkBox.Checked += StageToggle;
                checkBox.Unchecked += StageToggle;
                StagesPanel.Children.Add(checkBox);
            }
        }

        void StageToggle(object sender, RoutedEventArgs e)
        {
            var checkBox = (CheckBox)sender;
            _engine.Matrix.SetDiscarded((string)checkBox.Tag, checkBox.IsChecked != true);
        }

        // ---- helpers ---------------------------------------------------------

        void UpdateState()
        {
            bool connected = _engine.State == SnifferState.Connected;
            StateText.Text = connected ? "Connected" : "Searching server…";
            StateDot.Fill = (Brush)Resources[connected ? "Brush.Accent" : "Brush.DotIdle"];
            ServerText.Text = connected ? $"Server {_engine.ServerIp}:{_engine.ServerPort}" : "";
        }

        void AppendLog(string message)
        {
            ConsoleBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
            // long-session guard: keep the log box bounded (drop the oldest half)
            if (ConsoleBox.Text.Length > 400_000)
                ConsoleBox.Text = ConsoleBox.Text.Substring(ConsoleBox.Text.Length - 200_000);
            ConsoleBox.ScrollToEnd();
        }

        // ---- settings persistence -------------------------------------------

        // push saved values into the controls; their TextChanged/Checked handlers
        // then propagate to the engine (pct, progression window, gating, nations…).
        void ApplySettings()
        {
            var inv = CultureInfo.InvariantCulture;
            PortBox.Text = _settings.Port.ToString(inv);
            PctBox.Text = _settings.PenaltyPct.ToString(inv);
            ProgressWindowBox.Text = _settings.ProgressWindowKm.ToString("0.0", inv);
            ReplayPauseBox.Text = _settings.ReplayPause.ToString(inv);
            FinishGatingCheck.IsChecked = _settings.FinishGating;
            HideNationsCheck.IsChecked = _settings.HideNations;
            PageTitleBox.Text = _settings.PageTitle;
            PageDescBox.Text = _settings.PageDescription;
            // per-overlay chrome
            TableTopmostToggle.IsChecked = _settings.Table.Topmost;
            TableLockToggle.IsChecked = _settings.Table.Locked;
            TableOpacityBox.Text = _settings.Table.Opacity.ToString(inv);
            ProgTopmostToggle.IsChecked = _settings.Progress.Topmost;
            ProgLockToggle.IsChecked = _settings.Progress.Locked;
            ProgOpacityBox.Text = _settings.Progress.Opacity.ToString(inv);
        }

        // restore the saved main-window size/position, keeping the title bar on a
        // currently-visible monitor (the display layout may have changed since). No
        // saved bounds (first run) => the XAML defaults stand.
        void RestoreWindowBounds()
        {
            var s = _settings;
            if (s.WinWidth is double w && w > 200) Width = w;
            if (s.WinHeight is double h && h > 200) Height = h;
            if (s.WinLeft is double l && s.WinTop is double t)
            {
                double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
                double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
                if (l < vx + vw - 40 && l + Width > vx + 40 && t >= vy - 1 && t < vy + vh - 40)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = l; Top = t;
                }
            }
            if (s.WinMaximized) WindowState = WindowState.Maximized;
        }

        void SaveSettings()
        {
            // ignore the control-change churn ApplySettings() causes during load;
            // real saves only after startup has finished restoring the controls.
            if (!_settingsLoaded) return;
            var inv = CultureInfo.InvariantCulture;
            if (int.TryParse(PortBox.Text.Trim(), out var port)) _settings.Port = port;
            _settings.PenaltyPct = ParseD(PctBox.Text, _settings.PenaltyPct);
            _settings.ProgressWindowKm = ParseD(ProgressWindowBox.Text, _settings.ProgressWindowKm);
            _settings.ReplayPause = ParseD(ReplayPauseBox.Text, _settings.ReplayPause);
            _settings.FinishGating = FinishGatingCheck.IsChecked == true;
            _settings.HideNations = HideNationsCheck.IsChecked == true;
            _settings.Dark = _dark; _settings.HasTheme = true;
            _settings.PageTitle = PageTitleBox.Text;
            _settings.PageDescription = PageDescBox.Text;
            // per-overlay chrome (topmost / locked / opacity are written on change too,
            // this keeps them in sync on a full save)
            _settings.Table.Topmost = TableTopmostToggle.IsChecked == true;
            _settings.Table.Locked = TableLockToggle.IsChecked == true;
            _settings.Table.Opacity = ParseOpacity(TableOpacityBox.Text, _settings.Table.Opacity);
            _settings.Progress.Topmost = ProgTopmostToggle.IsChecked == true;
            _settings.Progress.Locked = ProgLockToggle.IsChecked == true;
            _settings.Progress.Opacity = ParseOpacity(ProgOpacityBox.Text, _settings.Progress.Opacity);
            // capture live bounds of still-open overlays (closed ones kept their last)
            if (_tableOverlay != null) { CaptureBounds(_tableOverlay, _settings.Table); _settings.Table.Open = true; }
            if (_progressOverlay != null) { CaptureBounds(_progressOverlay, _settings.Progress); _settings.Progress.Open = true; }
            // main window bounds — RestoreBounds gives the NORMAL rect even when maximized
            var b = WindowState == WindowState.Maximized ? RestoreBounds
                                                         : new Rect(Left, Top, Width, Height);
            if (b.Width > 200 && b.Height > 200)
            {
                _settings.WinLeft = b.Left; _settings.WinTop = b.Top;
                _settings.WinWidth = b.Width; _settings.WinHeight = b.Height;
            }
            _settings.WinMaximized = WindowState == WindowState.Maximized;
            _settings.Save();
        }

        static double ParseD(string text, double fallback)
            => double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float,
                               CultureInfo.InvariantCulture, out var v) ? v : fallback;

        bool _shuttingDown;   // suppresses the overlay Closed handlers during app exit,
                              // so the Open=true saved just below survives to next launch

        void OnClosed(object? sender, EventArgs e)
        {
            SaveSettings();   // before closing overlays, while their bounds are live
            _shuttingDown = true;
            // close any open overlays so the app actually exits (OnLastWindowClose)
            foreach (var overlay in _overlays.ToList()) overlay.Close();
            _timer?.Stop();
            StopPcap();
            _tunnel.Dispose();
            _web.Dispose();
            _sniffer.Dispose();
        }
    }
}
