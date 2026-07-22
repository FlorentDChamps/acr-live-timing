using System.Reflection;

namespace ACRLiveTiming
{
    /// <summary>
    /// Product version, read once from the assembly metadata. A release is stamped
    /// from its git tag and reads as a clean "1.2.3"; anything else is a prerelease
    /// and also carries the commit, so a tester's report points at exact source.
    /// </summary>
    public static class AppInfo
    {
        public static readonly string Version = Read();

        static string Read()
        {
            // InformationalVersion is "<semver>[+<commit sha>]" — the sha comes from
            // the SDK's SourceLink support, which reads it straight out of the repo.
            var raw = Assembly.GetExecutingAssembly()
                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                          ?.InformationalVersion
                      ?? "0.0.0";

            int plus = raw.IndexOf('+');
            string semver = plus < 0 ? raw : raw[..plus];

            // No sha available, or a release (no "-dev" suffix): the number is enough.
            if (plus < 0 || !semver.Contains('-')) return semver;

            string sha = raw[(plus + 1)..];
            return semver + "+" + (sha.Length > 7 ? sha[..7] : sha);
        }
    }
}
