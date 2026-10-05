using System;
using System.IO;

namespace DailyWallpaper
{
    internal static class Program
    {
        private const string Usage = @"Usage: daily-wallpaper <command> [options]

Commands:
  update       Download today's Bing wallpaper if new, apply it, then exit
  info         Show the applied wallpaper and cache status
  previous     Apply the previous (older) cached wallpaper
  next         Apply the next (newer) cached wallpaper
  detach       Retain any active cached images in Pictures (used by uninstall)

Options for update:
  --force      Reapply today's cached image, including the configured mode
  --quiet      Write output to data\last-run.log (used by Task Scheduler)

  -h, --help      Show help
  -V, --version   Show version
";
        [STAThread]
        private static int Main(string[] args)
        {
            bool quiet = Array.IndexOf(args, "--quiet") >= 0;
            TextWriter originalOut = Console.Out, originalError = Console.Error;
            var log = new StringWriter();
            Paths paths = null;
            int code = 0;
            if (quiet) { Console.SetOut(log); Console.SetError(log); }
            try
            {
                if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h" || args[0] == "help")) { Console.Write(Usage); return 0; }
                if (args.Length == 1 && (args[0] == "--version" || args[0] == "-V")) { Console.WriteLine("daily-wallpaper 0.1.0 (Windows)"); return 0; }
                bool force;
                string command = ParseCommand(args, out force);
                paths = Paths.ForUser();
                var config = command == "detach" ? new Config() : Config.Load(paths.ConfigFile);
                var cache = new Cache(paths.Data);
                var wallpaper = new Wallpaper();
                switch (command)
                {
                    case "update": using (var bing = new Bing(config)) Update(config, cache, bing, wallpaper, force); break;
                    case "info": Info(config, cache); break;
                    case "detach":
                        using (cache.Lock()) Wallpaper.Detach(cache.Images, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
                        break;
                    default: Step(config, cache, wallpaper, command == "previous"); break;
                }
            }
            catch (AppError e) { code = e.Code; Console.Error.WriteLine("daily-wallpaper: " + e.Message); if (code == 2) Console.Error.Write(Usage); }
            catch (Exception e) { code = 7; Console.Error.WriteLine("daily-wallpaper: local operation failed: " + e.Message); }
            finally
            {
                if (quiet)
                {
                    Console.SetOut(originalOut); Console.SetError(originalError);
                    try
                    {
                        if (paths != null) Cache.AtomicWrite(Path.Combine(paths.Data, "last-run.log"), DateTimeOffset.Now.ToString("o") + Environment.NewLine + log + "Exit code: " + code + Environment.NewLine);
                    }
                    catch (Exception e) { Console.Error.WriteLine("daily-wallpaper: cannot write last-run.log: " + e.Message); if (code == 0) code = 7; }
                }
                log.Dispose();
            }
            return code;
        }
        internal static string ParseCommand(string[] args, out bool force)
        {
            force = false;
            if (args.Length == 0) throw new AppError(2, "missing command");
            string command = args[0];
            if (command != "update" && command != "info" && command != "previous" && command != "next" && command != "detach") throw new AppError(2, "unknown command: " + command);
            bool quiet = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (command == "update" && args[i] == "--force" && !force) force = true;
                else if (command == "update" && args[i] == "--quiet" && !quiet) quiet = true;
                else throw new AppError(2, "unexpected argument: " + args[i]);
            }
            return command;
        }
        internal static void Update(Config config, Cache cache, IProvider provider, IWallpaper wallpaper, bool force)
        {
            RemoteWallpaper remote = provider.Current();
            // Hold the lock only for local operations; cycling works during downloads.
            using (cache.Lock())
            {
                State state = cache.Load();
                Entry existing = state.Find("bing", remote.Id);
                if (existing != null) { UseCached(config, cache, state, existing, wallpaper, force); return; }
            }
            using (Download download = cache.StartDownload())
            {
                provider.Download(remote, download.Stream);
                string extension = ImageValidation.Detect(download.Stream);
                using (cache.Lock())
                {
                    State state = cache.Load();
                    Entry existing = state.Find("bing", remote.Id);
                    if (existing != null) { UseCached(config, cache, state, existing, wallpaper, force); return; }
                    string filename = Cache.FilenameFor(remote.Date, remote.Id, extension);
                    string path = download.Persist(cache, filename);
                    using (IWallpaperChange change = wallpaper.Apply(path, config.Mode))
                    {
                        var entry = new Entry { date = remote.Date, filename = filename, title = remote.Title, copyright = remote.Copyright, source = "bing", id = remote.Id, url = remote.Url.AbsoluteUri };
                        state.Insert(entry);
                        state.current = filename;
                        state.Prune(config.Keep);
                        cache.Save(state);
                        change.Commit();
                        cache.Sweep(state);
                        Console.WriteLine("Wallpaper set: " + entry.Label);
                    }
                }
            }
        }
        private static void UseCached(Config config, Cache cache, State state, Entry entry, IWallpaper wallpaper, bool force)
        {
            // Match Linux: a normal update preserves a manually selected older
            // cached image when today's wallpaper has already been fetched.
            if (force || state.CurrentIndex < 0)
            {
                Apply(config, cache, state, entry, wallpaper);
                Console.WriteLine("Wallpaper set: " + entry.Label);
            }
            else
            {
                state.Prune(config.Keep);
                cache.Save(state);
                cache.Sweep(state);
                Console.WriteLine("Already up to date: " + entry.Label);
            }
        }
        internal static void Step(Config config, Cache cache, IWallpaper wallpaper, bool previous)
        {
            using (cache.Lock())
            {
                State state = cache.Load();
                int index = state.Neighbour(previous);
                if (index < 0) throw new AppError(7, "no cached wallpapers yet; run 'daily-wallpaper update' first");
                Entry entry = state.wallpapers[index];
                Apply(config, cache, state, entry, wallpaper);
                Console.WriteLine("Wallpaper set: " + entry.Label + " (" + (state.CurrentIndex + 1) + " of " + state.wallpapers.Count + ")");
            }
        }
        private static void Apply(Config config, Cache cache, State state, Entry entry, IWallpaper wallpaper)
        {
            using (IWallpaperChange change = wallpaper.Apply(cache.ImagePath(entry.filename), config.Mode))
            {
                state.current = entry.filename;
                state.Prune(config.Keep);
                cache.Save(state);
                change.Commit();
                cache.Sweep(state);
            }
        }
        private static void Info(Config config, Cache cache)
        {
            State state = cache.Load();
            int index = state.CurrentIndex;
            if (index >= 0)
            {
                Entry entry = state.wallpapers[index];
                if (entry.title != null) Console.WriteLine("Title:      " + entry.title);
                if (entry.copyright != null) Console.WriteLine("Copyright:  " + entry.copyright);
                Console.WriteLine("Date:       " + entry.date);
                Console.WriteLine("Source:     " + entry.source);
                Console.WriteLine("File:       " + cache.ImagePath(entry.filename));
                Console.WriteLine("URL:        " + entry.url);
                Console.WriteLine("Position:   " + (index + 1) + " of " + state.wallpapers.Count);
            }
            else Console.WriteLine("No wallpaper recorded as applied; run 'daily-wallpaper update'.");
            Console.WriteLine("Cache:      " + cache.DirectoryPath + " (keeping " + config.Keep + ")");
        }
    }
}
