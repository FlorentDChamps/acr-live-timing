using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ACRLiveTiming.Updates
{
    /// <summary>Looks up the latest stable GitHub release. It only returns a direct
    /// download link; the browser performs the download after the user agrees.</summary>
    public sealed class UpdateChecker
    {
        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
        static readonly Regex VersionPrefix = new(@"^v?(\d+(?:\.\d+){1,3})", RegexOptions.Compiled);

        public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
        {
            if (!TryGetRepository(out var owner, out var repository)) return null;
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ACRLiveTiming", AppInfo.Version));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(body, cancellationToken: cancellationToken);
            if (release == null || release.Draft || release.Prerelease ||
                !TryParseVersion(release.TagName, out var latest) || !TryParseVersion(AppInfo.Version, out var current))
                return null;

            // Compare the numeric version only: a 1.0.0-dev build is already based
            // on 1.0.0 for update-notification purposes, while 1.0.1 still prompts.
            if (latest <= current) return null;

            var asset = release.Assets.FirstOrDefault(candidate =>
                candidate.Name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(candidate => candidate.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (asset == null || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var download) ||
                download.Scheme != Uri.UriSchemeHttps)
                return null;

            var releasePage = new Uri($"https://github.com/{owner}/{repository}/releases/tag/{Uri.EscapeDataString(release.TagName)}");
            return new AvailableUpdate(latest.ToString(), download, releasePage);
        }

        static bool TryGetRepository(out string owner, out string repository)
        {
            owner = repository = "";
            if (!Uri.TryCreate(AppInfo.RepositoryUrl, UriKind.Absolute, out var url) ||
                !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            owner = parts[0]; repository = parts[1];
            return true;
        }

        static bool TryParseVersion(string value, out Version version)
        {
            var match = VersionPrefix.Match(value);
            if (match.Success && Version.TryParse(match.Groups[1].Value, out var parsed))
            {
                version = parsed;
                return true;
            }
            version = new Version(0, 0);
            return false;
        }

        sealed class GitHubRelease
        {
            [JsonPropertyName("tag_name")] public string TagName { get; init; } = "";
            [JsonPropertyName("draft")] public bool Draft { get; init; }
            [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
            [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; init; } = new();
        }

        sealed class GitHubAsset
        {
            [JsonPropertyName("name")] public string Name { get; init; } = "";
            [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; init; } = "";
        }
    }

    public sealed record AvailableUpdate(string Version, Uri DownloadUrl, Uri ReleaseUrl);
}
