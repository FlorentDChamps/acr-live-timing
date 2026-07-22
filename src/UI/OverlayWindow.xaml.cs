using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ACRLiveTiming.UI
{
    /// <summary>
    /// Independent, resizable, always-on-top streaming overlay. Hosts the local
    /// classification page (WebView2) in a single-widget mode (?view=classification /
    /// ?view=progress) on a transparent canvas, so it can float over the game or be
    /// captured in OBS. Loads from the app's own web server — that must be running.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        readonly string _fitSelector;   // CSS selector to fit the window to ("" = no fit)
        readonly bool _byHeight;        // true: fit+zoom on HEIGHT, false: on WIDTH
        readonly bool _hasSavedFitSize; // saved size present on the fit axis => keep it
        double _baseWidth;              // width  at which the page renders at zoom 1
        double _baseHeight;             // height at which the page renders at zoom 1

        // Per-overlay resize semantics: one axis fits the content then drives the
        // zoom; the OTHER axis reflows the page (more visible area / longer layout).
        //   Classification: axis = WIDTH  -> width zooms, height just reveals rows.
        //   Progress:       axis = HEIGHT -> height zooms, width stretches the bar.
        public OverlayWindow(string url, string title, double width, double height,
                             string? fitSelector = null, bool byHeight = false,
                             OverlayState? saved = null)
        {
            InitializeComponent();
            Title = TitleLabel.Text = title;
            Width = width; Height = height;
            _baseWidth = width; _baseHeight = height;
            _fitSelector = fitSelector ?? "";
            _byHeight = byHeight;
            // restore a previously saved position/size; when the size on the fit axis
            // is known, keep it instead of re-fitting to content on load.
            if (saved != null)
            {
                if (saved.Width is double sw) Width = sw;
                if (saved.Height is double sh) Height = sh;
                if (saved.Left is double sl) Left = sl;
                if (saved.Top is double st) Top = st;
                _hasSavedFitSize = _byHeight ? saved.Height.HasValue : saved.Width.HasValue;
            }
            // transparent WebView2 background must be set before the first paint so
            // the page's see-through canvas shows the desktop/game behind it.
            Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            SizeChanged += (_, _) => ApplyZoom();
            SetChrome(false);   // hidden until the cursor enters the window
            Loaded += async (_, _) =>
            {
                // async void handler: an uncaught throw here (typically the WebView2
                // Evergreen Runtime missing on a fresh machine) would escape every
                // caller's try/catch and take the whole app down.
                try
                {
                    await Web.EnsureCoreWebView2Async();
                    Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                    Web.CoreWebView2.Navigate(url);
                    ApplyZoom();
                    if (_fitSelector.Length > 0) _ = FitBaseWhenReadyAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "The overlay could not start its embedded browser (WebView2).\n\n"
                        + ex.Message + "\n\n"
                        + "If the WebView2 Runtime is not installed, get it from:\n"
                        + "https://developer.microsoft.com/microsoft-edge/webview2/",
                        "ACR Live Timing", MessageBoxButton.OK, MessageBoxImage.Error);
                    Close();
                }
            };
            // Poll the cursor to show the chrome (drag bar, outline, grip) only while
            // the mouse is over the window. WPF's IsMouseOver can't see it: WebView2
            // is a separate child HWND (airspace), so hovering the content wouldn't
            // register — polling the OS cursor vs the window rect covers the whole
            // window, WebView2 included.
            _chromeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _chromeTimer.Tick += (_, _) =>
            {
                UpdateChrome(); ReassertTopmost();
                if (++_probeTick >= 20) { _probeTick = 0; _ = ProbeZoomAsync(); }
            };
            _chromeTimer.Start();
            Closed += (_, _) => _chromeTimer.Stop();
        }

        readonly DispatcherTimer _chromeTimer;
        bool _chromeShown;
        int _probeTick;
        bool _probing;

        /// <summary>
        /// Self-healing zoom. After a display-mode change (typically the game flipping
        /// the screen 1080p -> 4K fullscreen) WebView2 can keep rasterizing at the old
        /// scale: the window keeps its pixel size but the page suddenly paints at half
        /// size (a quarter of the window). The zoom-axis CSS size is a fixed invariant
        /// (= the fitted content size, whatever the window size), so re-measure it every
        /// ~3s and multiply the zoom by the drift ratio — corrects the desync whatever
        /// its cause, and is a no-op (±2%) when everything is healthy.
        /// </summary>
        async Task ProbeZoomAsync()
        {
            if (_probing || Web.CoreWebView2 == null) return;
            _probing = true;
            try
            {
                var js = await Web.CoreWebView2.ExecuteScriptAsync(
                    _byHeight ? "window.innerHeight" : "window.innerWidth");
                double chrome = _byHeight ? 8 + DragBar.ActualHeight : 8;
                double want = (_byHeight ? _baseHeight : _baseWidth) - chrome;
                if (double.TryParse(js, NumberStyles.Float, CultureInfo.InvariantCulture, out var got)
                    && got > 0 && want > 0 && Math.Abs(got - want) / want > 0.02)
                    Web.ZoomFactor = Math.Clamp(Web.ZoomFactor * got / want, 0.25, 5.0);
            }
            catch { /* transient: navigation or shutdown mid-probe */ }
            finally { _probing = false; }
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

        void UpdateChrome()
        {
            // locked (click-through) = pure display: never show the chrome
            if (_locked) { if (_chromeShown) SetChrome(false); return; }
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetCursorPos(out var c) || !GetWindowRect(hwnd, out var r))
                return;
            bool inside = c.X >= r.Left && c.X < r.Right && c.Y >= r.Top && c.Y < r.Bottom;
            if (inside != _chromeShown) SetChrome(inside);
        }

        // ---- external control (driven from the main window) ----------------------

        const int GWL_EXSTYLE = -20;
        const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
        bool _locked;
        bool _topmost;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                        int X, int Y, int cx, int cy, uint uFlags);

        static readonly IntPtr HWND_TOPMOST = new(-1);
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

        /// <summary>Set the overlay's background opacity (0..100 %). Drives the page's
        /// --ov-alpha CSS variable live; the initial value also rides in the ?bg= URL
        /// param so the very first paint is already correct.</summary>
        public void SetBackgroundOpacity(int pct)
        {
            pct = Math.Clamp(pct, 0, 100);
            if (Web.CoreWebView2 == null) return;
            var a = (pct / 100.0).ToString(CultureInfo.InvariantCulture);
            _ = Web.CoreWebView2.ExecuteScriptAsync(
                $"document.documentElement.style.setProperty('--ov-alpha','{a}')");
        }

        /// <summary>Keep this overlay above other windows (or not).</summary>
        public void SetTopmost(bool on)
        {
            _topmost = on;
            Topmost = on;
            if (on) ReassertTopmost();
        }

        // WPF's Topmost alone loses the fight against a borderless-fullscreen game
        // that grabs the foreground (it re-asserts its own topmost z-order). Re-push
        // ourselves to HWND_TOPMOST each tick, WITHOUT activating (SWP_NOACTIVATE) so
        // the game keeps input focus and doesn't minimise.
        void ReassertTopmost()
        {
            if (!_topmost) return;
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero)
                SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>Click-through: mouse events pass to whatever is behind (the game).
        /// Adds WS_EX_TRANSPARENT to the window's extended style. The chrome is hidden
        /// while locked since it can't be clicked anyway — unlock from the main window.</summary>
        public void SetClickThrough(bool on)
        {
            _locked = on;
            var h = new WindowInteropHelper(this).EnsureHandle();
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            ex = on ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED : ex & ~WS_EX_TRANSPARENT;
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
            if (on && _chromeShown) SetChrome(false);
        }

        // reserve the chrome's layout (opacity toggle, not collapse) so the content
        // never jumps and the fit/zoom base stays stable; hidden = fully transparent.
        void SetChrome(bool show)
        {
            _chromeShown = show;
            double o = show ? 1.0 : 0.0;
            DragBar.Opacity = o;
            Frame.Opacity = o;
            Grip.Opacity = o;
            DragBar.IsHitTestVisible = show;
        }

        // Resizing the zoom axis scales the whole widget instead of reflowing it:
        // holding the CSS layout size constant (= base) and scaling to fill. The
        // other axis is left alone, so the page reflows freely along it.
        // The ratio is taken over the WEB AREA (window minus the fixed chrome: the
        // 4px resize rings and, vertically, the drag bar) — the chrome doesn't zoom,
        // so a whole-window ratio under-zooms ever more as the window grows and the
        // widget ends up hugging one edge with a dead band after it.
        void ApplyZoom()
        {
            if (Web.CoreWebView2 == null) return;
            double chrome = _byHeight ? 8 + DragBar.ActualHeight : 8;
            double avail  = (_byHeight ? ActualHeight : ActualWidth)  - chrome;
            double basis  = (_byHeight ? _baseHeight  : _baseWidth)   - chrome;
            double z = basis > 0 && avail > 0 ? avail / basis : 1.0;
            Web.ZoomFactor = Math.Clamp(z, 0.25, 5.0);
        }

        // ---- borderless resize (WM_NCHITTEST over the transparent ring) ----------

        const int WM_NCHITTEST = 0x0084;
        const int WM_DISPLAYCHANGE = 0x007E;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                  HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        const double BorderDip = 8;   // grab band on every edge

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(HitTestHook);
        }

        IntPtr HitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Display-mode change (the game flipping the screen resolution, e.g. a
            // 1080p desktop entering a 4K fullscreen game): WebView2 can keep
            // compositing at the old scale — the page paints in the top-left quarter
            // of the window, the rest transparent. The DOM is unaffected (innerWidth
            // reads normal) so the zoom probe cannot see it; only pushing fresh host
            // bounds resets the presentation. Nudge once the mode change settles.
            if (msg == WM_DISPLAYCHANGE) _ = NudgeWebAsync();

            if (msg != WM_NCHITTEST) return IntPtr.Zero;

            GetWindowRect(hwnd, out var r);
            // lParam packs the screen cursor: low word X, high word Y (signed for
            // multi-monitor). Work entirely in device pixels vs the window rect.
            int x = (short)((long)lParam & 0xFFFF);
            int y = (short)(((long)lParam >> 16) & 0xFFFF);
            var src = HwndSource.FromHwnd(hwnd);
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            int b = (int)(BorderDip * scale);

            int wx = x - r.Left, wy = y - r.Top;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            bool left = wx <= b, right = wx >= w - b, top = wy <= b, bottom = wy >= h - b;

            int code =
                top && left ? HTTOPLEFT : top && right ? HTTOPRIGHT :
                bottom && left ? HTBOTTOMLEFT : bottom && right ? HTBOTTOMRIGHT :
                left ? HTLEFT : right ? HTRIGHT : top ? HTTOP : bottom ? HTBOTTOM : 0;

            if (code == 0) return IntPtr.Zero;   // interior: let normal input flow
            handled = true;
            return new IntPtr(code);
        }

        bool _nudging;

        // Shrink the browser control by 1 DIP then hand it back to star sizing: the
        // two bounds updates force WebView2 to recomposite at the current display
        // scale. The window itself never moves or resizes, so the user's saved
        // overlay geometry is untouched.
        async Task NudgeWebAsync()
        {
            if (_nudging) return;
            _nudging = true;
            try
            {
                await Task.Delay(500);   // let the display-mode change finish first
                if (!IsLoaded || Web.CoreWebView2 == null) return;
                Web.Width = Math.Max(1, Web.ActualWidth - 1);
                UpdateLayout();
                Web.Width = double.NaN;
                ApplyZoom();
            }
            catch { /* transient: shutdown mid-nudge */ }
            finally { _nudging = false; }
        }

        /// <summary>Once the widget has rendered (data has arrived), size the window's
        /// zoom axis to hug the content, so it starts at zoom 1. The other axis is
        /// left as-is. One-shot: a later manual resize is never overridden.</summary>
        async Task FitBaseWhenReadyAsync()
        {
            string dim = _byHeight ? "height" : "width";
            for (int i = 0; i < 30 && IsLoaded; i++)
            {
                var js = await Web.CoreWebView2.ExecuteScriptAsync(
                    $"(function(){{var e=document.querySelector('{_fitSelector}');" +
                    $"return e?Math.ceil(e.getBoundingClientRect().{dim}):0;}})()");
                if (double.TryParse(js, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c > 0)
                {
                    if (_byHeight)
                        // window height = top ring + drag bar + widget + bottom ring
                        _baseHeight = 4 + DragBar.ActualHeight + c + 4;
                    else
                        // window width = left ring + widget + right ring
                        _baseWidth = 4 + c + 4;
                    // grow to the fitted base only when no saved size overrides it
                    if (!_hasSavedFitSize)
                    {
                        if (_byHeight) Height = _baseHeight; else Width = _baseWidth;
                    }
                    ApplyZoom();   // zoom == 1 at the fitted base (or the saved size's ratio)
                    return;
                }
                await Task.Delay(500);
            }
        }

        void DragBar_Down(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }
    }
}
