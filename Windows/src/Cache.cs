using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DailyWallpaper
{
    // Lowercase property names keep the on-disk metadata readable and similar to Linux.
    public sealed class Entry
    {
        public string date { get; set; }
        public string filename { get; set; }
        public string title { get; set; }
        public string copyright { get; set; }
        public string source { get; set; }
        public string id { get; set; }
        public string url { get; set; }
        internal string Label { get { return date + " - " + (title ?? filename); } }
    }

    public sealed class State
    {
        public string current { get; set; }
        public List<Entry> wallpapers { get; set; }
        public State() { wallpapers = new List<Entry>(); }
        internal Entry Find(string source, string id)
        {
            return wallpapers.FirstOrDefault(e => e.source == source && e.id == id);
        }
        internal int CurrentIndex { get { return wallpapers.FindIndex(e => e.filename == current); } }
        internal int Neighbour(bool previous)
        {
            if (wallpapers.Count == 0) return -1;
            int index = CurrentIndex;
            if (index < 0) index = wallpapers.Count - 1;
            return (index + (previous ? wallpapers.Count - 1 : 1)) % wallpapers.Count;
        }
        internal void Insert(Entry entry)
        {
            wallpapers.RemoveAll(e => e.filename == entry.filename || (e.source == entry.source && e.id == entry.id));
            wallpapers.Add(entry);
            Sort();
        }
        internal void Sort() { wallpapers = wallpapers.OrderBy(e => e.date, StringComparer.Ordinal).ToList(); }
        internal void Prune(int keep)
        {
            while (wallpapers.Count > keep)
            {
                int index = wallpapers.FindIndex(e => e.filename != current);
                if (index < 0) break;
                wallpapers.RemoveAt(index);
            }
        }
    }

    internal sealed class Cache
    {
        internal readonly string DirectoryPath;
        internal string Images { get { return Path.Combine(DirectoryPath, "images"); } }
        internal string Metadata { get { return Path.Combine(DirectoryPath, "metadata.json"); } }
        internal Cache(string directory) { DirectoryPath = directory; }
        internal string ImagePath(string filename)
        {
            if (!PlainFilename(filename)) throw new AppError(7, "invalid cached filename");
            return Path.Combine(Images, filename);
        }
        internal IDisposable Lock(int timeoutMilliseconds = 15000)
        {
            Directory.CreateDirectory(DirectoryPath);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                try { return new FileStream(Path.Combine(DirectoryPath, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException e)
                {
                    int code = e.HResult & 0xffff;
                    if (code != 32 && code != 33) throw;
                    if (timer.ElapsedMilliseconds >= timeoutMilliseconds) throw new AppError(8, "another daily-wallpaper process is still running");
                    Thread.Sleep(100);
                }
            }
        }
        internal State Load()
        {
            string text;
            try
            {
                using (var stream = new FileStream(Metadata, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
            }
            catch (FileNotFoundException) { return new State(); }
            catch (DirectoryNotFoundException) { return new State(); }
            State state;
            try
            {
                state = Json.Serializer().Deserialize<State>(text);
                if (state == null || state.wallpapers == null) throw new FormatException("invalid state");
                if (state.wallpapers.Any(e => e == null || String.IsNullOrEmpty(e.date) || String.IsNullOrEmpty(e.id) || e.source != "bing"))
                    throw new FormatException("invalid wallpaper entry");
            }
            catch (Exception e) { throw new AppError(7, Metadata + " is corrupt; rename or delete it to start afresh: " + e.Message, e); }
            state.wallpapers.RemoveAll(e => !PlainFilename(e.filename) || !File.Exists(ImagePath(e.filename)));
            state.Sort();
            return state;
        }
        internal void Save(State state)
        {
            AtomicWrite(Metadata, Json.Serializer().Serialize(state) + Environment.NewLine);
        }
        internal static void AtomicWrite(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        internal Download StartDownload()
        {
            Directory.CreateDirectory(Images);
            return new Download(Path.Combine(Images, ".download-" + Guid.NewGuid().ToString("N") + ".tmp"));
        }
        internal void Sweep(State state)
        {
            if (!Directory.Exists(Images)) return;
            var listed = new HashSet<string>(state.wallpapers.Select(e => e.filename), StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(Images))
            {
                string name = Path.GetFileName(path);
                bool download = name.StartsWith(".download-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal);
                try
                {
                    if (download ? DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(1) : !listed.Contains(name))
                        File.Delete(path);
                }
                catch (IOException e) { Console.Error.WriteLine("Cannot delete " + path + ": " + e.Message); }
                catch (UnauthorizedAccessException e) { Console.Error.WriteLine("Cannot delete " + path + ": " + e.Message); }
            }
        }
        internal static bool PlainFilename(string name)
        {
            if (String.IsNullOrEmpty(name) || name == "." || name == ".." || name.EndsWith(".") || name.EndsWith(" ")) return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(name) != name) return false;
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return false;
            if (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '1' && stem[3] <= '9') return false;
            return true;
        }
        internal static string FilenameFor(string date, string id, string extension)
        {
            string stem = new string((date + "_" + id).Select(c =>
                (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' ? c : '_').Take(150).ToArray());
            return stem.TrimStart('.') + "." + extension;
        }
    }

    internal sealed class Download : IDisposable
    {
        internal readonly string TemporaryPath;
        internal FileStream Stream { get; private set; }
        internal Download(string path)
        {
            TemporaryPath = path;
            Stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        internal string Persist(Cache cache, string filename)
        {
            Stream.Flush(true);
            Stream.Dispose(); // Windows cannot move a file opened without delete sharing.
            string destination = cache.ImagePath(filename);
            if (File.Exists(destination)) File.Replace(TemporaryPath, destination, null);
            else File.Move(TemporaryPath, destination);
            return destination;
        }
        public void Dispose()
        {
            Stream.Dispose();
            try { File.Delete(TemporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static class ImageValidation
    {
        internal const long MaxBytes = 50L * 1024 * 1024;
        internal static string Detect(Stream stream)
        {
            if (stream.Length < 1024 || stream.Length > MaxBytes) throw new AppError(5, "image size must be between 1 KiB and 50 MiB");
            var head = new byte[8];
            var tail = new byte[32];
            stream.Position = 0;
            ReadAll(stream, head);
            stream.Position = stream.Length - tail.Length;
            ReadAll(stream, tail);
            if (head[0] == 0xff && head[1] == 0xd8 && head[2] == 0xff)
            {
                for (int i = 0; i < tail.Length - 1; i++) if (tail[i] == 0xff && tail[i + 1] == 0xd9) return "jpg";
                throw new AppError(5, "JPEG data is truncated");
            }
            byte[] png = { 137, 80, 78, 71, 13, 10, 26, 10 };
            byte[] end = { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 };
            if (head.SequenceEqual(png))
            {
                if (tail.Skip(tail.Length - end.Length).SequenceEqual(end)) return "png";
                throw new AppError(5, "PNG data is truncated");
            }
            throw new AppError(5, "download is not a JPEG or PNG");
        }
        private static void ReadAll(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int count = stream.Read(buffer, offset, buffer.Length - offset);
                if (count == 0) throw new AppError(5, "image is truncated");
                offset += count;
            }
        }
    }
}
