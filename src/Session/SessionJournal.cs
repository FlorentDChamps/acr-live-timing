using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ACRLiveTiming.Model;
using ACRLiveTiming.Net;

namespace ACRLiveTiming.Session;

/// <summary>What a session's .json holds besides its packets.</summary>
public sealed class SessionFile
{
    public string Format { get; set; } = "acrlt-session/1";
    /// <summary>Packet indexes before which the organiser reset the board. A reset
    /// keeps the identities decoded so far, which the game does not send again, so
    /// the session after a reset starts as a copy of the one before it.</summary>
    public List<long> Resets { get; set; } = [];
    public SessionSettings Settings { get; set; } = new();
}

/// <summary>
/// The sessions folder: every packet the engine decodes is appended to
/// <c>session_yyyyMMdd_HHmmss.pcap</c>, with the organiser's choices in the .json
/// of the same name, so a session can be loaded back after the app was closed or
/// crashed. A file opens with the first packet of a locked server, never before.
/// <b>Reset session</b> ends the session and starts a new one from a copy of its
/// packets, so both can be loaded. Only the most recent automatically named
/// sessions are kept; a renamed one is never deleted.
/// </summary>
public sealed partial class SessionJournal(string folder, Func<int> keep, Func<SessionSettings> settings, Action<string> log)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [GeneratedRegex(@"^session_\d{8}_\d{6}(_\d+)?$")]
    private static partial Regex AutoName { get; }

    private readonly Lock _lock = new();
    private PcapWriter? _writer;
    private List<long> _resets = [];
    private bool _paused;
    private bool _failed;

    public string Folder => folder;

    /// <summary>The session file being written, null when none is open.</summary>
    public string? CurrentPath { get { lock (_lock) return _writer?.Path; } }

    /// <summary>True while packets must not be recorded: a replay or the load of a
    /// session file that will be continued as it is.</summary>
    public bool Paused
    {
        get { lock (_lock) return _paused; }
        set { lock (_lock) _paused = value; }
    }

    public static string SettingsPath(string pcapPath) => Path.ChangeExtension(pcapPath, ".json");

    /// <summary>The .json next to a session's .pcap; null when there is none or it
    /// cannot be read (the packets alone still rebuild the board).</summary>
    public static SessionFile? ReadSettings(string pcapPath)
    {
        try
        {
            string path = SettingsPath(pcapPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Records one decoded packet, opening a new session file first when
    /// none is open.</summary>
    public void Write(string ip, int port, byte[] payload, DateTime utc)
    {
        lock (_lock)
        {
            if (_paused || _failed) return;
            try
            {
                _writer ??= Open();
                _writer.WriteUdp(ip, port, payload, utc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(ex);
            }
        }
    }

    /// <summary>The board was reset. The session so far is kept as it was (its
    /// .json is already up to date: written on every change); a new session goes
    /// on from a copy of its packets, with the reset noted at their end.
    /// <paramref name="split"/> false only notes the reset in the open session: a
    /// file being loaded into a new session replays its own resets.</summary>
    public void OnReset(bool split = true)
    {
        lock (_lock)
        {
            if (_writer == null || _paused) return;
            long index = _writer.Count;
            if (!split)
            {
                _resets.Add(index);
                WriteSettings();
                return;
            }
            string previous = _writer.Path;
            var resets = _resets;
            _writer.Dispose();
            _writer = null;
            try
            {
                string name = NewName();
                string path = Path.Combine(folder, name + ".pcap");
                File.Copy(previous, path);
                _writer = PcapWriter.Append(path);
                _resets = [.. resets, index];
                WriteSettings();
                log($"Session reset: {Path.GetFileName(previous)} kept, new session file {path}");
                Prune(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Fail(ex);
            }
        }
    }

    /// <summary>Writes the organiser's current choices to the open session's .json.</summary>
    public void SaveSettings()
    {
        lock (_lock)
        {
            if (_writer != null && !_paused) WriteSettings();
        }
    }

    /// <summary>Goes on in the given session file (a loaded one), after its last
    /// complete packet.</summary>
    public void Continue(string pcapPath, SessionFile? file)
    {
        lock (_lock)
        {
            CloseLocked();
            try
            {
                _writer = PcapWriter.Append(pcapPath);
                _resets = file?.Resets.Where(index => index <= _writer.Count).ToList() ?? [];
                WriteSettings();
                log($"Session file: {pcapPath} (continued)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _writer?.Dispose();
                _writer = null;
                log($"Session file not continued ({ex.Message}); the next packets start a new one.");
            }
        }
    }

    public void Close()
    {
        lock (_lock) CloseLocked();
    }

    private void CloseLocked()
    {
        _writer?.Dispose();
        _writer = null;
        _resets = [];
        _failed = false;
    }

    // A free automatic name, from the local time.
    private string NewName()
    {
        Directory.CreateDirectory(folder);
        string stem = $"session_{DateTime.Now:yyyyMMdd_HHmmss}";
        string name = stem;
        for (int n = 2; File.Exists(Path.Combine(folder, name + ".pcap")) || File.Exists(Path.Combine(folder, name + ".json")); n++)
            name = $"{stem}_{n}";
        return name;
    }

    private PcapWriter Open()
    {
        string name = NewName();
        var writer = new PcapWriter(Path.Combine(folder, name + ".pcap"));
        _writer = writer;
        _resets = [];
        WriteSettings();
        log($"Session file: {writer.Path}");
        Prune(name);
        return writer;
    }

    // Caller holds _lock and _writer is set. Written aside then moved over, so a
    // crash never leaves half a file.
    private void WriteSettings()
    {
        string path = SettingsPath(_writer!.Path);
        try
        {
            var file = new SessionFile { Resets = [.. _resets], Settings = settings() };
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Session settings not saved: {ex.Message}");
        }
    }

    // Keeps the most recently written automatically named sessions, the open one
    // included; a value below 1 in the config keeps them all.
    private void Prune(string current)
    {
        int kept = keep();
        if (kept < 1) return;
        try
        {
            var sessions = new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => f.Extension is ".pcap" or ".json" && AutoName.IsMatch(Path.GetFileNameWithoutExtension(f.Name)))
                .GroupBy(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.Equals(g.Key, current, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => g.Max(f => f.LastWriteTimeUtc))
                .Skip(kept - 1)
                .ToList();
            int removed = 0;
            foreach (var file in sessions.SelectMany(g => g))
            {
                try { file.Delete(); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use: next time */ }
            }
            if (removed > 0) log($"Removed {removed} old session file(s) from {folder}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Old sessions not cleaned up: {ex.Message}");
        }
    }

    private void Fail(Exception ex)
    {
        _failed = true;
        _writer?.Dispose();
        _writer = null;
        log($"Session file stopped: {ex.Message}");
    }

    /// <summary>Rebuilds a board from a session file (or any pcap): every packet
    /// goes through <paramref name="engine"/> as recorded, the resets noted in
    /// <paramref name="file"/> are made at their place, then its settings are put
    /// back. Returns the number of packets read.</summary>
    public static long Load(Engine engine, string pcapPath, SessionFile? file)
    {
        var resets = file?.Resets.ToHashSet() ?? [];
        long index = 0;
        foreach (var (ip, port, payload, utc) in PcapReplay.ReadUdp(pcapPath))
        {
            if (resets.Contains(index)) engine.ManualReset(keepIdentity: true);
            engine.OnPacket(ip, port, payload, utc, recorded: true);
            index++;
        }
        if (resets.Contains(index)) engine.ManualReset(keepIdentity: true);
        if (file != null) engine.Matrix.ApplySettings(file.Settings);
        return index;
    }
}
