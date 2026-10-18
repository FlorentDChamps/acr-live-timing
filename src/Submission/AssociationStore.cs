using System.IO;
using System.Text.Json;

namespace ACRLiveTiming.Submission;

/// <summary>
/// The associations of this installation, in ACRLiveTiming.pairings next to the
/// settings file. Only <see cref="AssociationState.Active"/> ones are written:
/// an ended, refused or expired association lives until the app closes. Tokens
/// stay DPAPI-encrypted in the file; writes are atomic.
/// </summary>
public sealed class AssociationStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public string ClientInstanceId { get; private set; } = Guid.NewGuid().ToString();
    public List<Association> Items { get; } = [];
    public event Action? Changed;

    public AssociationStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppContext.BaseDirectory, "ACRLiveTiming.pairings");
    }

    public IEnumerable<Association> Active => Items.Where(a => a.State == AssociationState.Active);

    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path));
            if (file == null) return;
            if (Guid.TryParse(file.ClientInstanceId, out _)) ClientInstanceId = file.ClientInstanceId;
            foreach (var a in file.Associations)
                if (a.State == AssociationState.Active && a.HasToken) Items.Add(a);
        }
        catch { /* damaged or unreadable: start empty, like the settings file */ }
    }

    /// <summary>Persist and notify. Best-effort: a write failure only costs the
    /// associations across a restart.</summary>
    public void Save()
    {
        try
        {
            var file = new StoreFile
            {
                ClientInstanceId = ClientInstanceId,
                Associations = [.. Active.Where(a => a.HasToken)]
            };
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* read-only folder: keep going in memory */ }
        Changed?.Invoke();
    }

    /// <summary>Delete the tokens whose time is up: expiresAt passed.</summary>
    public bool ExpireTokens(DateTimeOffset now)
    {
        bool changed = false;
        foreach (var a in Items)
        {
            if (a.HasToken && a.ExpiresAt is DateTimeOffset expires && expires <= now)
            {
                a.DeleteToken(AssociationState.Expired);
                a.LastSend = "Expired — a new key is needed.";
                changed = true;
            }
        }
        if (changed) Save();
        return changed;
    }

    private sealed class StoreFile
    {
        public string ClientInstanceId { get; set; } = "";
        public List<Association> Associations { get; set; } = [];
    }
}
