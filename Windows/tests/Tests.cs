using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DailyWallpaper
{
    internal static class Tests
    {
        private static int passed;
        private static readonly Uri ImageUrl = new Uri("https://www.bing.com/th?id=test_UHD.jpg");
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Test("config defaults, shipped config, Linux mode aliases", ConfigDefaults);
                Test("invalid config types, values and unknown keys", InvalidConfig);
                Test("Bing parsing, real calendar dates and trusted HTTPS URLs", BingParsing);
                Test("CLI options and usage errors", Commands);
                Test("JPEG/PNG signatures and truncated downloads", Images);
                Test("cache ordering, duplicates, cycling and pruning", StateBehaviour);
                Test("Windows atomic replacement, delete sharing and missing entries", CacheRoundTrip);
                Test("Windows exclusive lock and release", ExclusiveLock);
                Test("download file closure, persistence and cleanup", DownloadLifecycle);
                Test("sweep retains live downloads and removes abandoned files", Sweep);
                Test("update, offline cycling, cached reapply and retention", UpdateLifecycle);
                Test("failed image download/apply preserves current metadata", FailedUpdate);
                Test("failed metadata save rolls the desktop back", FailedSave);
                Test("concurrent updates recheck state after downloading", ConcurrentUpdate);
                Test("HTTP retries transient status and rejects permanent errors", HttpStatus);
                Test("HTTP interrupted body is retried without partial bytes", HttpPartialBody);
                Test("HTTP rejects non-image, oversized and incomplete bodies", HttpValidation);
                Test("HTTP body timeout closes streams that ignore cancellation", HttpBodyTimeout);
                Test("native Windows wallpaper API can read monitor state", NativeProbe);
                if (args.Contains("--online")) Test("live Bing metadata and UHD image download", Online);
                Console.WriteLine(passed + " tests passed.");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        private static void Assert(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
        private static void Equal<T>(T actual, T expected) { Assert(Object.Equals(actual, expected), "expected " + expected + "; got " + actual); }
        private static void Error(int code, Action action)
        {
            try { action(); } catch (AppError e) { Equal(e.Code, code); return; }
            throw new Exception("expected AppError " + code);
        }
        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("expected " + typeof(T).Name);
        }
        private static void ConfigDefaults()
        {
            var config = Config.Parse("{}");
            Equal(config.Market, "en-US"); Equal(config.Resolution, "UHD"); Equal(config.Keep, 7); Equal(config.Mode, Position.Fill);
            string shipped = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "dist", "config.json");
            Equal(Config.Parse(File.ReadAllText(shipped)).Mode, Position.Fill);
            Equal(Config.Parse("{\"wallpaper\":{\"mode\":\"zoom\"},\"cache\":{\"keep\":3}}").Keep, 3);
            Equal(Config.Parse("{\"wallpaper\":{\"mode\":\"spanned\"}}").Mode, Position.Span);
            Equal(Config.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json")).Keep, 7);
        }
        private static void InvalidConfig()
        {
            foreach (string bad in new[] { "", "null", "[]", "{\"typo\":1}", "{\"provider\":\"other\"}", "{\"market\":\"en US\"}",
                "{\"resolution\":\"../x\"}", "{\"cache\":{\"keep\":0}}", "{\"cache\":{\"keep\":2.5}}", "{\"cache\":{\"keep\":\"7\"}}",
                "{\"cache\":null}", "{\"wallpaper\":{\"mod\":\"fill\"}}", "{\"wallpaper\":{\"mode\":\"none\"}}" })
                Throws<Exception>(() => Config.Parse(bad));
            using (var temp = new Scratch())
            {
                string file = Path.Combine(temp.Root, "config.json"); File.WriteAllText(file, "{");
                Error(3, () => Config.Load(file));
            }
        }
        private const string Sample = "{\"images\":[{\"startdate\":\"20261003\",\"urlbase\":\"/th?id=OHR.Test_EN-US123\",\"title\":\"Test title\",\"copyright\":\"Test credit\"}]}";
        private static void BingParsing()
        {
            RemoteWallpaper image = Bing.Parse(Sample, "UHD");
            Equal(image.Id, "OHR.Test_EN-US123_UHD"); Equal(image.Date, "2026-10-03"); Equal(image.Title, "Test title");
            Equal(image.Url.AbsoluteUri, "https://www.bing.com/th?id=OHR.Test_EN-US123_UHD.jpg");
            Assert(Bing.Parse(Sample, "1920x1080").Id != image.Id);
            var fallback = Bing.Parse("{\"images\":[{\"startdate\":\"20261003\",\"url\":\"/th?id=X.jpg\",\"title\":\"\"}]}", "UHD");
            Equal(fallback.Title, null); Equal(fallback.Id, "X.jpg");
            foreach (string bad in new[] { "<html>", "{}", "{\"images\":[]}", Sample.Replace("20261003", "20260230"),
                Sample.Replace("/th?id=OHR.Test_EN-US123", "http://www.bing.com/x"), Sample.Replace("/th?id=OHR.Test_EN-US123", "//evil.example/x"),
                Sample.Replace("/th?id=OHR.Test_EN-US123", "https://bing.com.evil.example/x") }) Error(4, () => Bing.Parse(bad, "UHD"));
        }
        private static void Commands()
        {
            bool force = false; Equal(Program.ParseCommand(new[] { "update", "--quiet", "--force" }, out force), "update"); Assert(force);
            foreach (string[] args in new[] { new string[0], new[] { "bogus" }, new[] { "info", "--quiet" }, new[] { "update", "--force", "--force" } })
                Error(2, () => Program.ParseCommand(args, out force));
        }
        private static byte[] Jpeg()
        {
            byte[] bytes = new byte[4096]; bytes[0] = 255; bytes[1] = 216; bytes[2] = 255; bytes[bytes.Length - 2] = 255; bytes[bytes.Length - 1] = 217; return bytes;
        }
        private static void Images()
        {
            Equal(ImageValidation.Detect(new MemoryStream(Jpeg())), "jpg");
            byte[] png = new byte[4096]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
            new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 }.CopyTo(png, png.Length - 12);
            Equal(ImageValidation.Detect(new MemoryStream(png)), "png");
            foreach (byte[] bad in new[] { new byte[0], Jpeg().Take(512).ToArray(), Jpeg().Take(3000).ToArray(), new byte[4096], png.Take(3000).ToArray() })
                Error(5, () => ImageValidation.Detect(new MemoryStream(bad)));
        }
        private static Entry EntryFor(int day) { return new Entry { date = "2026-10-" + day.ToString("00"), filename = day + ".jpg", id = day.ToString(), source = "bing", url = ImageUrl.AbsoluteUri }; }
        private static void StateBehaviour()
        {
            var state = new State(); state.Insert(EntryFor(3)); state.Insert(EntryFor(1)); state.Insert(EntryFor(2)); state.Insert(EntryFor(2));
            Equal(String.Join(",", state.wallpapers.Select(e => e.id)), "1,2,3");
            Equal(state.Neighbour(false), 0); Equal(state.Neighbour(true), 1);
            state.current = "1.jpg"; Equal(state.Neighbour(true), 2); Equal(state.Neighbour(false), 1);
            state.Prune(2); Equal(String.Join(",", state.wallpapers.Select(e => e.id)), "1,3");
            state.Prune(1); Equal(state.wallpapers[0].id, "1"); Equal(new State().Neighbour(true), -1);
            foreach (string bad in new[] { "", ".", "..", "../a.jpg", "a\\b.jpg", "C:\\a.jpg", "a.jpg:stream", "a.jpg.", "NUL.jpg", "COM1.jpg" }) Assert(!Cache.PlainFilename(bad), bad);
            Assert(Cache.PlainFilename(Cache.FilenameFor("2026-10-03", "../../bad:*", "jpg")));
        }
        private static void CacheRoundTrip()
        {
            using (var temp = new Scratch())
            {
                Cache cache = temp.Cache; Equal(cache.Load().wallpapers.Count, 0); Directory.CreateDirectory(cache.Images);
                var state = new State(); state.Insert(EntryFor(1)); state.Insert(EntryFor(2));
                File.WriteAllBytes(cache.ImagePath("2.jpg"), Jpeg()); state.current = "2.jpg";
                using (cache.Lock()) cache.Save(state);
                Equal(cache.Load().wallpapers.Count, 1); Equal(cache.Load().current, "2.jpg");
                using (var reader = new FileStream(cache.Metadata, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (cache.Lock()) { state.current = null; cache.Save(state); }
                Equal(cache.Load().current, null);
                File.WriteAllText(cache.Metadata, "{bad"); Error(7, () => cache.Load());
            }
        }
        private static void ExclusiveLock()
        {
            using (var temp = new Scratch())
            {
                using (temp.Cache.Lock()) Error(8, () => temp.Cache.Lock(0));
                using (temp.Cache.Lock(0)) { }
            }
        }
        private static void DownloadLifecycle()
        {
            using (var temp = new Scratch())
            {
                string path;
                using (var download = temp.Cache.StartDownload()) { path = download.TemporaryPath; download.Stream.WriteByte(1); }
                Assert(!File.Exists(path));
                using (var download = temp.Cache.StartDownload())
                {
                    byte[] image = Jpeg(); download.Stream.Write(image, 0, image.Length);
                    path = download.Persist(temp.Cache, "a b's.jpg");
                }
                Assert(File.ReadAllBytes(path).SequenceEqual(Jpeg()));
                using (var download = temp.Cache.StartDownload()) { download.Stream.Write(Jpeg(), 0, Jpeg().Length); download.Persist(temp.Cache, "a b's.jpg"); }
                Equal(Directory.GetFiles(temp.Cache.Images).Length, 1);
            }
        }
        private static void Sweep()
        {
            using (var temp = new Scratch())
            {
                Directory.CreateDirectory(temp.Cache.Images); var state = new State(); state.Insert(EntryFor(1));
                foreach (string name in new[] { "1.jpg", "2.jpg", ".download-fresh.tmp", ".download-stale.tmp" }) File.WriteAllText(temp.Cache.ImagePath(name), "x");
                File.SetLastWriteTimeUtc(temp.Cache.ImagePath(".download-stale.tmp"), DateTime.UtcNow.AddHours(-2));
                temp.Cache.Sweep(state); Equal(Directory.GetFiles(temp.Cache.Images).Length, 2);
                Assert(File.Exists(temp.Cache.ImagePath("1.jpg"))); Assert(File.Exists(temp.Cache.ImagePath(".download-fresh.tmp")));
            }
        }
        private static void UpdateLifecycle()
        {
            using (var temp = new Scratch())
            {
                var config = new Config { Keep = 2 }; var provider = new FakeProvider(); var wallpaper = new FakeWallpaper();
                Program.Update(config, temp.Cache, provider, wallpaper, false); Equal(wallpaper.Applies, 1); Equal(provider.Downloads, 1);
                provider.Day = 2; Program.Update(config, temp.Cache, provider, wallpaper, false);
                Program.Step(config, temp.Cache, wallpaper, true); string previous = temp.Cache.Load().current;
                Program.Update(config, temp.Cache, provider, wallpaper, false); Equal(temp.Cache.Load().current, previous); Equal(provider.Downloads, 2);
                Program.Update(config, temp.Cache, provider, wallpaper, true); Assert(temp.Cache.Load().current != previous); Equal(provider.Downloads, 2);
                provider.Day = 3; Program.Update(config, temp.Cache, provider, wallpaper, false);
                Equal(temp.Cache.Load().wallpapers.Count, 2); Equal(Directory.GetFiles(temp.Cache.Images).Length, 2);
                Program.Step(config, temp.Cache, wallpaper, false); Equal(temp.Cache.Load().wallpapers[temp.Cache.Load().CurrentIndex].date, "2026-10-02");
            }
        }
        private static void FailedUpdate()
        {
            using (var temp = new Scratch())
            {
                var config = new Config(); var provider = new FakeProvider(); var wallpaper = new FakeWallpaper();
                Program.Update(config, temp.Cache, provider, wallpaper, false); string metadata = File.ReadAllText(temp.Cache.Metadata);
                provider.Day = 2; provider.BadImage = true; Error(5, () => Program.Update(config, temp.Cache, provider, wallpaper, false));
                Equal(File.ReadAllText(temp.Cache.Metadata), metadata); Equal(Directory.GetFiles(temp.Cache.Images).Length, 1);
                provider.BadImage = false; wallpaper.Fail = true; Error(6, () => Program.Update(config, temp.Cache, provider, wallpaper, false));
                Equal(File.ReadAllText(temp.Cache.Metadata), metadata);
            }
        }
        private static void FailedSave()
        {
            using (var temp = new Scratch())
            {
                var wallpaper = new FakeWallpaper { OnApply = () => Directory.CreateDirectory(temp.Cache.Metadata) };
                Throws<IOException>(() => Program.Update(new Config(), temp.Cache, new FakeProvider(), wallpaper, false));
                Equal(wallpaper.Current, null); Equal(wallpaper.Rollbacks, 1);
            }
        }
        private static void ConcurrentUpdate()
        {
            using (var temp = new Scratch())
            {
                var wallpaper = new FakeWallpaper(); var provider = new FakeProvider(); var other = new FakeProvider();
                provider.OnDownload = () => Program.Update(new Config(), temp.Cache, other, wallpaper, false);
                Program.Update(new Config(), temp.Cache, provider, wallpaper, false);
                Equal(wallpaper.Applies, 1); Equal(temp.Cache.Load().wallpapers.Count, 1); Equal(Directory.GetFiles(temp.Cache.Images).Length, 1);
            }
        }
        private static HttpResponseMessage Response(int status, byte[] bytes, string type)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type); return response;
        }
        private static Http Client(Handler handler) { return new Http(handler, new[] { 0, 0 }, ms => { }); }
        private static void HttpStatus()
        {
            var handler = new Handler(n => Response(n == 1 ? 503 : n == 2 ? 429 : 200, Encoding.UTF8.GetBytes(Sample), "application/json"));
            using (var http = Client(handler)) Equal(http.Metadata(ImageUrl), Sample); Equal(handler.Calls, 3);
            handler = new Handler(n => Response(403, new byte[0], "text/html"));
            using (var http = Client(handler)) Error(4, () => http.Metadata(ImageUrl)); Equal(handler.Calls, 1);
            handler = new Handler(n => Response(503, new byte[0], "text/html"));
            using (var http = Client(handler)) Error(4, () => http.Metadata(ImageUrl)); Equal(handler.Calls, 3);
        }
        private static void HttpPartialBody()
        {
            var handler = new Handler(n => {
                if (n > 1) return Response(200, Jpeg(), "image/jpeg");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg"); return response;
            });
            using (var http = Client(handler))
            using (var output = new MemoryStream()) { http.Download(ImageUrl, output); Assert(output.ToArray().SequenceEqual(Jpeg())); }
            Equal(handler.Calls, 2);
        }
        private static void HttpValidation()
        {
            var handler = new Handler(n => Response(200, new byte[4096], "text/html"));
            using (var http = Client(handler)) using (var output = new MemoryStream()) Error(5, () => http.Download(ImageUrl, output));
            handler = new Handler(n => { var response = Response(200, Jpeg(), "image/jpeg"); response.Content.Headers.ContentLength = ImageValidation.MaxBytes + 1; return response; });
            using (var http = Client(handler)) using (var output = new MemoryStream()) Error(5, () => http.Download(ImageUrl, output));
            handler = new Handler(n => { var response = Response(200, Jpeg(), "image/jpeg"); response.Content.Headers.ContentLength = 10000; return response; });
            using (var http = Client(handler)) using (var output = new MemoryStream()) Error(4, () => http.Download(ImageUrl, output));
            Equal(handler.Calls, 3);
        }
        private static void NativeProbe() { Assert(Wallpaper.Probe() > 0); }
        private static void HttpBodyTimeout()
        {
            var stream = new StalledStream();
            var handler = new Handler(n => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            using (var http = new Http(handler, new int[0], ms => { }, TimeSpan.FromMilliseconds(50))) Error(4, () => http.Metadata(ImageUrl));
            Assert(stream.Closed); Equal(handler.Calls, 1);
        }
        private static void Online()
        {
            using (var temp = new Scratch())
            using (var provider = new Bing(new Config()))
            using (var download = temp.Cache.StartDownload())
            {
                RemoteWallpaper image = provider.Current(); provider.Download(image, download.Stream);
                Equal(ImageValidation.Detect(download.Stream), "jpg");
                Console.WriteLine("Downloaded " + download.Stream.Length + " bytes: " + image.Date + " - " + image.Title);
            }
        }
        private sealed class Scratch : IDisposable
        {
            internal readonly string Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scratch-" + Guid.NewGuid().ToString("N") + " spaces & 'quotes'");
            internal Cache Cache { get { return new Cache(Path.Combine(Root, "data")); } }
            internal Scratch() { Directory.CreateDirectory(Root); }
            public void Dispose()
            {
                string build = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(Root).StartsWith(build, StringComparison.OrdinalIgnoreCase)) throw new Exception("unsafe scratch cleanup");
                Directory.Delete(Root, true);
            }
        }
        private sealed class FakeProvider : IProvider
        {
            internal int Day = 1, Downloads;
            internal bool BadImage;
            internal Action OnDownload;
            public RemoteWallpaper Current() { return new RemoteWallpaper { Id = Day + "_UHD", Date = "2026-10-" + Day.ToString("00"), Title = "Image " + Day, Url = ImageUrl }; }
            public void Download(RemoteWallpaper image, Stream output)
            {
                Downloads++; if (OnDownload != null) OnDownload(); byte[] bytes = BadImage ? new byte[20] : Jpeg(); output.Write(bytes, 0, bytes.Length);
            }
        }
        private sealed class FakeWallpaper : IWallpaper
        {
            internal int Applies, Rollbacks;
            internal string Current;
            internal bool Fail;
            internal Action OnApply;
            public IWallpaperChange Apply(string path, Position position)
            {
                if (Fail) throw new AppError(6, "injected apply failure");
                var change = new FakeChange(this, Current); Current = path; Applies++;
                if (OnApply != null) OnApply(); return change;
            }
            private sealed class FakeChange : IWallpaperChange
            {
                private readonly FakeWallpaper wallpaper;
                private readonly string original;
                private bool committed;
                internal FakeChange(FakeWallpaper wallpaper, string original) { this.wallpaper = wallpaper; this.original = original; }
                public void Commit() { committed = true; }
                public void Dispose() { if (!committed) { wallpaper.Current = original; wallpaper.Rollbacks++; } }
            }
        }
        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<int, HttpResponseMessage> respond;
            internal int Calls;
            internal Handler(Func<int, HttpResponseMessage> respond) { this.respond = respond; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { return Task.FromResult(respond(++Calls)); }
        }
        private sealed class BrokenStream : MemoryStream
        {
            internal BrokenStream() : base(new byte[1024]) { }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (Position > 0) throw new IOException("injected connection drop");
                return base.ReadAsync(buffer, offset, count, token);
            }
        }
        private sealed class StalledStream : MemoryStream
        {
            private readonly TaskCompletionSource<int> pending = new TaskCompletionSource<int>();
            internal bool Closed;
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { return pending.Task; }
            protected override void Dispose(bool disposing) { Closed = true; pending.TrySetCanceled(); base.Dispose(disposing); }
        }
    }
}
