using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace DailyWallpaper
{
    internal sealed class RemoteWallpaper
    {
        internal string Id, Date, Title, Copyright;
        internal Uri Url;
    }

    internal interface IProvider
    {
        RemoteWallpaper Current();
        void Download(RemoteWallpaper image, Stream output);
    }

    internal sealed class Bing : IProvider, IDisposable
    {
        private readonly Config config;
        private readonly Http http;
        internal Bing(Config config) : this(config, new Http()) { }
        internal Bing(Config config, Http http) { this.config = config; this.http = http; }
        public RemoteWallpaper Current()
        {
            var url = new Uri("https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=" + Uri.EscapeDataString(config.Market));
            return Parse(http.Metadata(url), config.Resolution);
        }
        public void Download(RemoteWallpaper image, Stream output) { http.Download(image.Url, output); }
        public void Dispose() { http.Dispose(); }
        internal static RemoteWallpaper Parse(string text, string resolution)
        {
            try
            {
                var archive = Json.Object(text);
                object value;
                if (!archive.TryGetValue("images", out value)) throw new FormatException("no images listed");
                var images = value as object[];
                if (images == null || images.Length == 0) throw new FormatException("no images listed");
                var image = images[0] as Dictionary<string, object>;
                if (image == null) throw new FormatException("invalid image entry");
                DateTime date;
                if (!DateTime.TryParseExact(Json.String(image, "startdate", ""), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    throw new FormatException("invalid startdate");
                string urlbase = Optional(image, "urlbase");
                string path = urlbase == null ? Optional(image, "url") : urlbase + "_" + resolution + ".jpg";
                if (path == null) throw new FormatException("image has no URL");
                var origin = new Uri("https://www.bing.com");
                var url = new Uri(origin, path);
                if (!BingUrl(url)) throw new FormatException("image URL must use HTTPS on bing.com");
                var idUrl = new Uri(origin, urlbase ?? path);
                string id = idUrl.Query.TrimStart('?').Split('&')
                    .Where(p => p.StartsWith("id=", StringComparison.Ordinal))
                    .Select(p => Uri.UnescapeDataString(p.Substring(3))).FirstOrDefault() ?? (urlbase ?? path);
                // Cache distinct resolutions separately so changing config takes effect.
                if (urlbase != null) id += "_" + resolution;
                return new RemoteWallpaper {
                    Id = id, Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Title = Optional(image, "title"), Copyright = Optional(image, "copyright"), Url = url
                };
            }
            catch (Exception e) { throw new AppError(4, "unexpected response from Bing: " + e.Message, e); }
        }
        private static string Optional(Dictionary<string, object> image, string key)
        {
            object value;
            if (!image.TryGetValue(key, out value) || value == null) return null;
            if (!(value is string)) throw new FormatException(key + " must be a string");
            return String.IsNullOrWhiteSpace((string)value) ? null : (string)value;
        }
        internal static bool BingUrl(Uri url)
        {
            return url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && String.IsNullOrEmpty(url.UserInfo)
                && (url.Host.Equals("bing.com", StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith(".bing.com", StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class Http : IDisposable
    {
        private readonly HttpClient client;
        private readonly int[] delays;
        private readonly Action<int> pause;
        private readonly TimeSpan metadataTimeout;
        internal Http() : this(new HttpClientHandler { AllowAutoRedirect = false }, new[] { 5000, 15000, 30000, 60000 }, Thread.Sleep) { }
        internal Http(HttpMessageHandler handler, int[] delays, Action<int> pause, TimeSpan? metadataTimeout = null)
        {
            // Use the system TLS defaults (and its certificate validation).
            ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("daily-wallpaper/0.1.0");
            this.delays = delays;
            this.pause = pause;
            this.metadataTimeout = metadataTimeout ?? TimeSpan.FromSeconds(30);
        }
        internal string Metadata(Uri url)
        {
            return Get(url, metadataTimeout, (response, token) => {
                using (var body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var buffer = new MemoryStream())
                {
                    Copy(body, buffer, token, 1024 * 1024, 4);
                    return Encoding.UTF8.GetString(buffer.ToArray());
                }
            });
        }
        internal void Download(Uri url, Stream output)
        {
            Get(url, TimeSpan.FromSeconds(120), (response, token) => {
                var type = response.Content.Headers.ContentType;
                if (type == null || !type.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    throw new AppError(5, "server did not return an image Content-Type");
                long? expected = response.Content.Headers.ContentLength;
                if (expected > ImageValidation.MaxBytes) throw new AppError(5, "image is larger than 50 MiB");
                output.Position = 0;
                output.SetLength(0); // A retry must discard any partial body.
                using (var body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                {
                    long count = Copy(body, output, token, ImageValidation.MaxBytes, 5);
                    if (expected.HasValue && expected.Value != count) throw new IOException("incomplete image download");
                    return count;
                }
            });
        }
        private T Get<T>(Uri url, TimeSpan timeout, Func<HttpResponseMessage, CancellationToken, T> read)
        {
            if (!Bing.BingUrl(url)) throw new AppError(4, "refusing unexpected download URL: " + url);
            for (int attempt = 0; ; attempt++)
            {
                string failure;
                try
                {
                    using (var cancellation = new CancellationTokenSource(timeout))
                    using (var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).GetAwaiter().GetResult())
                    {
                        int status = (int)response.StatusCode;
                        if (status >= 200 && status < 300)
                        {
                            // Some .NET Framework response streams ignore cancellation
                            // once a read starts. Closing the response also unblocks them.
                            using (cancellation.Token.Register(response.Dispose)) return read(response, cancellation.Token);
                        }
                        if (status != 429 && status < 500) throw new AppError(4, url + " returned HTTP " + status);
                        failure = url + " returned HTTP " + status;
                    }
                }
                catch (HttpRequestException e)
                {
                    Exception root = e;
                    while (root.InnerException != null) root = root.InnerException;
                    failure = e.Message + (root == e ? "" : " " + root.Message);
                }
                catch (OperationCanceledException) { failure = "request timed out: " + url; }
                catch (ObjectDisposedException) { failure = "response closed or timed out: " + url; }
                catch (IOException e) { failure = "download interrupted: " + e.Message; }
                if (attempt >= delays.Length) throw new AppError(4, failure);
                Console.Error.WriteLine(failure + "; retrying in " + delays[attempt] / 1000 + "s");
                pause(delays[attempt]);
            }
        }
        private static long Copy(Stream input, Stream output, CancellationToken token, long limit, int errorCode)
        {
            byte[] buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                int count = input.ReadAsync(buffer, 0, buffer.Length, token).GetAwaiter().GetResult();
                if (count == 0) return total;
                total += count;
                if (total > limit) throw new AppError(errorCode, "response exceeds the allowed size");
                try { output.Write(buffer, 0, count); }
                catch (IOException e) { throw new AppError(7, "cannot write download: " + e.Message, e); }
            }
        }
        public void Dispose() { client.Dispose(); }
    }
}
