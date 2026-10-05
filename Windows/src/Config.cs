using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace DailyWallpaper
{
    internal sealed class AppError : Exception
    {
        internal readonly int Code;
        internal AppError(int code, string message) : base(message) { Code = code; }
        internal AppError(int code, string message, Exception cause) : base(message, cause) { Code = code; }
    }

    internal sealed class Paths
    {
        internal readonly string Root;
        internal string ConfigFile { get { return Path.Combine(Root, "config.json"); } }
        internal string Data { get { return Path.Combine(Root, "data"); } }
        internal Paths(string root) { Root = Path.GetFullPath(root); }
        internal static Paths ForUser()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrEmpty(local)) throw new AppError(3, "cannot locate LocalAppData");
            return new Paths(Path.Combine(local, "daily-wallpaper"));
        }
    }

    internal sealed class Config
    {
        internal string Market = "en-US";
        internal string Resolution = "UHD";
        internal int Keep = 7;
        internal Position Mode = Position.Fill;

        internal static Config Load(string path)
        {
            try
            {
                using (var file = new StreamReader(path, Encoding.UTF8, true)) return Parse(file.ReadToEnd());
            }
            catch (FileNotFoundException) { return new Config(); }
            catch (DirectoryNotFoundException) { return new Config(); }
            catch (Exception e) { throw new AppError(3, "cannot load " + path + ": " + e.Message, e); }
        }

        internal static Config Parse(string text)
        {
            var root = Json.Object(text);
            Json.Keys(root, "provider", "market", "resolution", "cache", "wallpaper");
            var config = new Config();
            if (Json.String(root, "provider", "bing") != "bing") throw new FormatException("provider must be bing");
            config.Market = Json.String(root, "market", config.Market);
            config.Resolution = Json.String(root, "resolution", config.Resolution);
            if (!Regex.IsMatch(config.Market, @"\A[a-zA-Z0-9]+(?:-[a-zA-Z0-9]+)*\z"))
                throw new FormatException("market must be a Bing region such as en-US");
            if (!Regex.IsMatch(config.Resolution, @"\A(?:UHD|[1-9][0-9]{1,4}x[1-9][0-9]{1,4})\z"))
                throw new FormatException("resolution must be UHD or a size such as 1920x1080");
            if (root.ContainsKey("cache"))
            {
                var cache = Json.Child(root, "cache");
                Json.Keys(cache, "keep");
                if (cache.ContainsKey("keep"))
                {
                    if (!(cache["keep"] is int)) throw new FormatException("cache.keep must be an integer");
                    config.Keep = (int)cache["keep"];
                    if (config.Keep < 1) throw new FormatException("cache.keep must be at least 1");
                }
            }
            if (root.ContainsKey("wallpaper"))
            {
                var wallpaper = Json.Child(root, "wallpaper");
                Json.Keys(wallpaper, "mode");
                switch (Json.String(wallpaper, "mode", "fill"))
                {
                    case "center": case "centered": config.Mode = Position.Center; break;
                    case "tile": case "wallpaper": config.Mode = Position.Tile; break;
                    case "stretch": case "stretched": config.Mode = Position.Stretch; break;
                    case "fit": case "scaled": config.Mode = Position.Fit; break;
                    case "fill": case "zoom": config.Mode = Position.Fill; break;
                    case "span": case "spanned": config.Mode = Position.Span; break;
                    default: throw new FormatException("wallpaper.mode must be center, tile, stretch, fit, fill or span");
                }
            }
            return config;
        }
    }

    internal static class Json
    {
        internal static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024, RecursionLimit = 32 };
        }
        internal static Dictionary<string, object> Object(string text)
        {
            var result = Serializer().DeserializeObject(text) as Dictionary<string, object>;
            if (result == null) throw new FormatException("expected a JSON object");
            return result;
        }
        internal static Dictionary<string, object> Child(Dictionary<string, object> parent, string key)
        {
            object value;
            if (!parent.TryGetValue(key, out value) || !(value is Dictionary<string, object>))
                throw new FormatException(key + " must be an object");
            return (Dictionary<string, object>)value;
        }
        internal static string String(Dictionary<string, object> obj, string key, string fallback)
        {
            object value;
            if (!obj.TryGetValue(key, out value)) return fallback;
            if (!(value is string)) throw new FormatException(key + " must be a string");
            return (string)value;
        }
        internal static void Keys(Dictionary<string, object> obj, params string[] allowed)
        {
            foreach (string key in obj.Keys)
                if (Array.IndexOf(allowed, key) < 0) throw new FormatException("unknown config key: " + key);
        }
    }
}
