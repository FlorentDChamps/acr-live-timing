using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACRLiveTiming.UI;

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
/// file yields defaults, an unreadable one is set aside before defaults replace it,
/// and a write failure is swallowed.
/// </summary>
public sealed class AppSettings
{
    // options
    public int Port { get; set; } = 8080;
    // a driver without a time on a stage counts the slowest finish plus this (s)
    public double PenaltySeconds { get; set; } = 300;
    public bool DnfNoRejoin { get; set; }
    public double ProgressWindowKm { get; set; } = 0.8;   // max span of the auto-fitting progression window (km)
    public bool ProgressFixed { get; set; } = true;       // freeze the window at the max span (no auto-fit)
    public bool HideSplits { get; set; }
    public bool HideNations { get; set; }
    public double ReplayPause { get; set; } = 0;
    // automatically named session files kept in the sessions folder (below 1: all)
    public int SessionsKept { get; set; } = 5;
    public bool Dark { get; set; }
    public bool HasTheme { get; set; }   // false on first run => follow the OS
    // Latest update version dismissed with the "Discard" button. A different
    // release version is still offered normally on the next startup.
    public string DismissedUpdateVersion { get; set; } = "";

    // page text
    public string PageTitle { get; set; } = "";
    public string PageDescription { get; set; } = "";

    // The webhook can post as the application, so never persist it in clear text.
    // DPAPI ties the encrypted value to this Windows user on this PC.
    public string DiscordWebhookProtected { get; set; } = "";
    [JsonIgnore]
    public string DiscordWebhook
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DiscordWebhookProtected)) return "";
            try
            {
                byte[] data = Convert.FromBase64String(DiscordWebhookProtected);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
            }
            catch { return ""; }
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) { DiscordWebhookProtected = ""; return; }
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser);
            DiscordWebhookProtected = Convert.ToBase64String(data);
        }
    }

    // main window bounds (null on first run => the XAML defaults are used)
    public double? WinLeft { get; set; }
    public double? WinTop { get; set; }
    public double? WinWidth { get; set; }
    public double? WinHeight { get; set; }
    public bool WinMaximized { get; set; }

    // overlays (topmost / locked / opacity now live per-overlay in OverlayState)
    public OverlayState Table { get; set; } = new();
    public OverlayState Progress { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>ACRLiveTiming.config, alongside the exe (app base dir, which is the
    /// real exe folder even for a single-file publish).</summary>
    public static string Path =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "ACRLiveTiming.config");

    /// <summary>Why the saved settings could not be used, for the log; null when
    /// they loaded or there were none yet.</summary>
    [JsonIgnore]
    public string? LoadProblem { get; private set; }
    // the file could not be read nor set aside: never write over it this session
    private bool _keepFile;

    public static AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path))
                ?? throw new JsonException("the file holds no settings");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Defaults for this session, but never at the cost of the saved file:
            // the next save would overwrite it. Keep a copy, or keep the file itself.
            var settings = new AppSettings();
            string kept = System.IO.Path.Combine(AppContext.BaseDirectory,
                $"ACRLiveTiming.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.config");
            try
            {
                File.Copy(Path, kept);
                settings.LoadProblem = $"Settings could not be read ({ex.Message}); defaults are used and the old file is kept as {System.IO.Path.GetFileName(kept)}.";
            }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
            {
                settings._keepFile = true;
                settings.LoadProblem = $"Settings could not be read ({ex.Message}); defaults are used and this session will not save over them.";
            }
            return settings;
        }
    }

    public void Save()
    {
        if (_keepFile) return;
        // Written beside it, then swapped in: a crash or a power cut mid-write
        // leaves the previous file whole instead of a truncated one.
        string temp = Path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, Opts);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // read-only dir / locked / a value JSON cannot hold -> skip, non-fatal
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}
