using System.IO;
using System.Net;
using System.Reflection;
using System.Text;

namespace ACRLiveTiming.Web
{
    /// <summary>
    /// Tiny embedded web server (HttpListener, no ASP.NET dependency). Serves the
    /// single-page UI at "/" and the live classification JSON at "/state". The page
    /// polls "/state" on a short interval — plain short GETs proxy reliably through
    /// Cloudflare Tunnel (a long-lived SSE stream does not: HTTP/2 stream resets).
    /// Runs elevated (the app is admin for the raw socket), so binding
    /// http://+:{port}/ needs no urlacl reservation.
    /// </summary>
    public sealed class WebServer : IDisposable
    {
        readonly Func<string> _jsonProvider;
        readonly Func<(string title, string description)>? _metaProvider;
        readonly string _indexHtml;
        const string DefaultTitle = "ACR Live Timing";
        HttpListener? _listener;
        CancellationTokenSource? _cts;
        Task? _task;

        public int Port { get; private set; }
        public bool Running { get; private set; }
        public string LocalUrl => $"http://localhost:{Port}/";
        public event Action<string>? Log;

        public WebServer(Func<string> jsonProvider,
                         Func<(string title, string description)>? metaProvider = null)
        {
            _jsonProvider = jsonProvider;
            _metaProvider = metaProvider;
            _indexHtml = LoadIndexHtml();
        }

        public void Start(int port)
        {
            Port = port;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{port}/");
            listener.Start();
            _listener = listener;
            Running = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _task = Task.Run(() => AcceptLoop(listener, token));
            Log?.Invoke($"Web server started: {LocalUrl}");
        }

        async Task AcceptLoop(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception ex)
                {
                    // listener stopped/disposed = normal shutdown (silent); anything
                    // else must not leave a dead server that still claims Running.
                    if (!ct.IsCancellationRequested)
                    {
                        Running = false;
                        Log?.Invoke("Web server stopped unexpectedly: " + ex.Message);
                    }
                    break;
                }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            try
            {
                // GET/HEAD only (HEAD matters: link-preview bots probe with it, and
                // writing a body to a HEAD response makes HttpListener throw — the
                // bot used to get a connection reset)
                string method = ctx.Request.HttpMethod;
                if (method != "GET" && method != "HEAD")
                {
                    ctx.Response.StatusCode = 405;
                    ctx.Response.Headers["Allow"] = "GET, HEAD";
                    ctx.Response.Close();
                    return;
                }
                string path = ctx.Request.Url?.AbsolutePath ?? "/";
                if (path == "/" || path == "/index.html")
                    Serve(ctx, RenderIndex(), "text/html; charset=utf-8");
                else if (path == "/state")
                    Serve(ctx, _jsonProvider(), "application/json; charset=utf-8");
                else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
            }
            catch (Exception ex)
            {
                Log?.Invoke("HTTP handler error: " + ex.Message);
                try { ctx.Response.Abort(); } catch { /* client already gone */ }
            }
        }

        void Serve(HttpListenerContext ctx, string body, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = contentType;
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store";
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.ContentLength64 = bytes.Length;
            if (ctx.Request.HttpMethod != "HEAD")
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        /// <summary>
        /// Inject the operator-set title/description into the page &lt;head&gt; so link
        /// previews (Discord, Facebook, Twitter, …) show them — crawlers read the raw
        /// HTML and do NOT run the JS that fills the on-page block. Rebuilt per request
        /// so a title change is reflected the next time a crawler fetches the page.
        /// </summary>
        string RenderIndex()
        {
            var (title, desc) = _metaProvider?.Invoke() ?? ("", "");
            title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title.Trim();
            // meta content is a single-line attribute: collapse newlines to spaces
            desc = (desc ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

            var t = WebUtility.HtmlEncode(title);
            var d = WebUtility.HtmlEncode(desc);
            var meta = new StringBuilder();
            meta.Append("<meta property=\"og:title\" content=\"").Append(t).Append("\">\n");
            meta.Append("<meta name=\"twitter:title\" content=\"").Append(t).Append("\">\n");
            if (desc.Length > 0)
            {
                meta.Append("<meta property=\"og:description\" content=\"").Append(d).Append("\">\n");
                meta.Append("<meta name=\"twitter:description\" content=\"").Append(d).Append("\">\n");
            }
            meta.Append("<meta property=\"og:type\" content=\"website\">\n");
            meta.Append("<meta name=\"twitter:card\" content=\"summary\">");

            // Bake the current state into the #statecache element so first paint needs
            // no /state round-trip AND a copy saved with the browser's "Save page as"
            // (either mode) reopens offline with the standings + working settings
            // panel. Every '<' is escaped to its unicode form (backslash-u-003c) so a
            // driver name can never close the script element; still valid JSON.
            const string cacheTag = "<script type=\"application/json\" id=\"statecache\">";
            var state = _jsonProvider().Replace("<", "\\u003c");

            return _indexHtml
                .Replace("<!--HEAD_META-->", meta.ToString())
                .Replace(cacheTag + "</script>", cacheTag + state + "</script>")
                .Replace("<title>" + DefaultTitle + "</title>", "<title>" + t + "</title>");
        }

        static string LoadIndexHtml()
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
                if (name.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = asm.GetManifestResourceStream(name);
                    if (stream == null) continue;
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
            return "<html><body><h1>index.html missing</h1></body></html>";
        }

        public void Stop()
        {
            Running = false;
            // best-effort shutdown: each step may throw if Start() never ran.
            // Reusable: a later Start() reassigns _listener/_cts/_task.
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
        }

        public void Dispose() => Stop();
    }
}
