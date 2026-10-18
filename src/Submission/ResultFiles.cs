using System.IO;
using System.Text;

namespace ACRLiveTiming.Submission;

/// <summary>The Results folder next to the settings file: a byte-for-byte copy of
/// every payload sent. Files are never deleted by the app.</summary>
public static class ResultFiles
{
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Results");

    /// <summary>Writes the copy of one send; returns its path, or throws.</summary>
    public static string SaveCopy(byte[] body, Association destination, DateTime localTime)
    {
        Directory.CreateDirectory(Folder);
        string stem = $"{localTime:yyyyMMdd-HHmmss}_{Safe(destination.Host)}_{Safe(destination.EventId)}";
        string path = Path.Combine(Folder, stem + ".json");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(Folder, $"{stem}_{n}.json");
        File.WriteAllBytes(path, body);
        return path;
    }

    /// <summary>Characters a file name cannot hold become '_'; long values are cut.</summary>
    public static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (char c in value) sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        string name = sb.ToString().Trim().TrimEnd('.');
        if (name.Length > 60) name = name[..60];
        return name.Length == 0 ? "_" : name;
    }
}
