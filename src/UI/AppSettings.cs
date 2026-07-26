using System.IO;
using System.Text.Json;

namespace ACRLiveTiming.UI
{
    /// <summary>Persisted position + size of one overlay (null = use the default).</summary>
    public sealed class OverlayState
    {
        public bool Open { get; set; }
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        // per-overlay chrome (each overlay is configured independently in the IHM)
        public bool Topmost { get; set; }
        public bool Locked { get; set; }
        public int Opacity { get; set; } = 80;   // background opacity, %
    }

    /// <summary>
    /// Whole-IHM state saved next to the exe as a plain JSON .config, so options,
    /// the page text and the overlay layout survive a restart. Best-effort: a missing
    /// or corrupt file just yields defaults, and a write failure is swallowed.
    /// </summary>
    public sealed class AppSettings
    {
        // options
        public int Port { get; set; } = 8080;
        public double PenaltyPct { get; set; } = 50;
        public double ProgressWindowKm { get; set; } = 0.8;   // max span of the auto-fitting progression window (km)
        public bool FinishGating { get; set; } = true;
        public bool HideNations { get; set; }
        public double ReplayPause { get; set; } = 0;
        public bool Dark { get; set; }
        public bool HasTheme { get; set; }   // false on first run => follow the OS

        // page text
        public string PageTitle { get; set; } = "";
        public string PageDescription { get; set; } = "";

        // main window bounds (null on first run => the XAML defaults are used)
        public double? WinLeft { get; set; }
        public double? WinTop { get; set; }
        public double? WinWidth { get; set; }
        public double? WinHeight { get; set; }
        public bool WinMaximized { get; set; }

        // overlays (topmost / locked / opacity now live per-overlay in OverlayState)
        public OverlayState Table { get; set; } = new();
        public OverlayState Progress { get; set; } = new();

        static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        /// <summary>ACRLiveTiming.config, alongside the exe (app base dir, which is the
        /// real exe folder even for a single-file publish).</summary>
        public static string Path =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "ACRLiveTiming.config");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(Path))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
            }
            catch { /* corrupt / unreadable -> defaults */ }
            return new AppSettings();
        }

        public void Save()
        {
            try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Opts)); }
            catch { /* read-only dir / locked -> skip, non-fatal */ }
        }
    }
}
