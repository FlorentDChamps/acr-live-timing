using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ACRLiveTiming.Discord
{
    /// <summary>Host-side Discord webhook client. The page renderer only returns a
    /// PNG; the webhook remains local to the IHM at all times.</summary>
    public sealed class DiscordPublisher
    {
        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        const string LogoAttachment = "attachment://acr-live-timing.png";
        static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        static readonly Lazy<byte[]?> LogoPng = new(CreateLogoPng);

        public async Task SendLiveAlertAsync(string webhook, string publicUrl, string title, string description)
        {
            var text = string.IsNullOrWhiteSpace(description) ? "" : description.Trim() + "\n\n";
            text = $"## 🚨 · [{title}]({publicUrl})\n\n" + text
                + "🔴 **Live now!**";
            await PostWithFilesAsync(webhook, new WebhookPayload
            {
                Embeds = new[] { new Embed(null, Truncate(text, 4096), null, LogoPng.Value == null ? null : LogoAttachment) }
            }, LogoPng.Value == null ? Array.Empty<(string, byte[])>() : new[] { ("acr-live-timing.png", LogoPng.Value) });
        }

        public async Task SendStandingsAsync(string webhook, string title, DateTime sentAt, byte[] png)
            => await SendResultsImageAsync(webhook, title, "🏆", "Standings", sentAt, "standings.png", png);

        public async Task SendRallyAsync(string webhook, string title, string rallyName, DateTime sentAt, byte[] png)
            => await SendResultsImageAsync(webhook, title, "🏁", rallyName, sentAt, "rally.png", png);

        async Task SendResultsImageAsync(string webhook, string title, string emoji, string scope, DateTime sentAt, string fileName, byte[] png)
        {
            var payload = new WebhookPayload
            {
                Embeds = new[]
                {
                    new Embed(null, $"## {emoji} · {Truncate($"{title} · {scope} · {sentAt:yyyy-MM-dd}", 256)}",
                              "attachment://" + fileName, LogoPng.Value == null ? null : LogoAttachment)
                }
            };
            var files = new List<(string name, byte[] bytes)> { (fileName, png) };
            if (LogoPng.Value != null) files.Add(("acr-live-timing.png", LogoPng.Value));
            await PostWithFilesAsync(webhook, payload, files);
        }

        async Task PostWithFilesAsync(string webhook, WebhookPayload payload, IReadOnlyList<(string name, byte[] bytes)> files)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"), "payload_json");
            for (int i = 0; i < files.Count; i++)
                form.Add(new ByteArrayContent(files[i].bytes) { Headers = { ContentType = new("image/png") } },
                         $"files[{i}]", files[i].name);
            using var response = await Http.PostAsync(webhook, form);
            await EnsureSuccessAsync(response);
        }

        static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;
            var detail = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Discord returned {(int)response.StatusCode}: {Truncate(detail, 300)}");
        }

        static byte[]? CreateLogoPng()
        {
            try
            {
                var resource = Application.GetResourceStream(
                    new Uri("pack://application:,,,/assets/icon.ico", UriKind.Absolute));
                if (resource?.Stream == null) return null;
                using var stream = resource.Stream;
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bitmap = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).FirstOrDefault();
                if (bitmap == null) return null;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var png = new MemoryStream();
                encoder.Save(png);
                return png.ToArray();
            }
            catch { return null; } // logo is cosmetic; never block the webhook
        }

        static string Truncate(string value, int limit)
            => value.Length <= limit ? value : value[..Math.Max(0, limit - 1)] + "…";

        sealed class WebhookPayload
        {
            [JsonPropertyName("embeds")]
            public IReadOnlyList<Embed> Embeds { get; init; } = Array.Empty<Embed>();
        }

        sealed record Embed(
            [property: JsonPropertyName("title")] string? Title,
            [property: JsonPropertyName("description")] string? Description,
            [property: JsonIgnore] string? ImageUrl,
            [property: JsonIgnore] string? AuthorIconUrl)
        {
            [JsonPropertyName("color")]
            public int Color => 0xA33A43;
            [JsonPropertyName("image")]
            public object? Image => ImageUrl == null ? null : new { url = ImageUrl };
            [JsonPropertyName("author")]
            public object Author => new { name = "ACR Live Timing", url = AppInfo.RepositoryUrl, icon_url = AuthorIconUrl };
        }
    }
}
