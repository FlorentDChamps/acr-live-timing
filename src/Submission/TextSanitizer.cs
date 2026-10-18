using System.Text;

namespace ACRLiveTiming.Submission;

/// <summary>Server-supplied text (event and server names, error messages) is shown
/// as plain text only: control characters removed, length capped with "…".</summary>
public static class TextSanitizer
{
    public static string Clean(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, max + 1));
        foreach (char c in value)
        {
            if (char.IsControl(c)) { if (c is '\n' or '\r' or '\t') sb.Append(' '); continue; }
            sb.Append(c);
        }
        string text = sb.ToString().Trim();
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }
}
