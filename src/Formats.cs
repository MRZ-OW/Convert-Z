using System;
using System.Collections.Generic;
using System.Linq;

namespace ConvertZ
{
    enum Kind { Video, Audio, Image }

    sealed class Target
    {
        public readonly string Id;
        public readonly string Ext;
        public readonly string Label;
        public readonly Kind Kind;

        public Target(string id, string ext, string label, Kind kind)
        {
            Id = id; Ext = ext; Label = label; Kind = kind;
        }

        public override string ToString() { return Id; }
    }

    sealed class Menu
    {
        public readonly string Name;
        public readonly string[] Sources;
        public readonly string[] Targets;

        public Menu(string name, string[] sources, string[] targets)
        {
            Name = name; Sources = sources; Targets = targets;
        }
    }

    static class Formats
    {
        public static readonly Target[] All =
        {
            new Target("mp4",  ".mp4",  "MP4",           Kind.Video),
            new Target("mkv",  ".mkv",  "MKV",           Kind.Video),
            new Target("webm", ".webm", "WebM",          Kind.Video),
            new Target("mov",  ".mov",  "MOV",           Kind.Video),
            new Target("avi",  ".avi",  "AVI",           Kind.Video),
            new Target("gif",  ".gif",  "GIF",           Kind.Video),
            new Target("mp3",  ".mp3",  "MP3",           Kind.Audio),
            new Target("m4a",  ".m4a",  "M4A (AAC)",     Kind.Audio),
            new Target("wav",  ".wav",  "WAV",           Kind.Audio),
            new Target("flac", ".flac", "FLAC",          Kind.Audio),
            new Target("ogg",  ".ogg",  "OGG (Vorbis)",  Kind.Audio),
            new Target("opus", ".opus", "Opus",          Kind.Audio),
            new Target("png",  ".png",  "PNG",           Kind.Image),
            new Target("jpg",  ".jpg",  "JPG",           Kind.Image),
            new Target("webp", ".webp", "WebP",          Kind.Image),
            new Target("bmp",  ".bmp",  "BMP",           Kind.Image),
            new Target("tiff", ".tiff", "TIFF",          Kind.Image),
            new Target("ico",  ".ico",  "ICO (icon)",    Kind.Image),
        };

        public static readonly Menu[] Menus =
        {
            new Menu("video",
                new[] { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg",
                        ".ts", ".mts", ".m2ts", ".3gp", ".ogv", ".vob" },
                new[] { "mp4", "mkv", "webm", "mov", "avi", "gif", "-", "mp3", "m4a", "wav", "flac" }),
            new Menu("gif",
                new[] { ".gif" },
                new[] { "mp4", "webm", "mov", "mkv", "-", "png" }),
            new Menu("audio",
                new[] { ".mp3", ".wav", ".flac", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".wma",
                        ".aif", ".aiff", ".ac3", ".amr", ".mka", ".wv", ".ape" },
                new[] { "mp3", "m4a", "wav", "flac", "ogg", "opus" }),
            new Menu("image",
// no heic/heif yet: phone photos are tile grids, untested
                new[] { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".tga",
                        ".avif", ".jxl" },
                new[] { "png", "jpg", "webp", "bmp", "tiff", "ico" }),
        };

        public static Target ById(string id)
        {
            foreach (var t in All)
                if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        public static Menu MenuFor(string ext)
        {
            ext = ext.ToLowerInvariant();
            foreach (var m in Menus)
                if (Array.IndexOf(m.Sources, ext) >= 0) return m;
            return null;
        }

        public static List<KeyValuePair<Target, bool>> EntriesFor(string ext)
        {
            var list = new List<KeyValuePair<Target, bool>>();
            var menu = MenuFor(ext);
            if (menu == null) return list;
            string self = Canonical(ext);
            bool sep = false;
            foreach (var id in menu.Targets)
            {
                if (id == "-") { sep = list.Count > 0; continue; }
                if (id == self) continue;
                list.Add(new KeyValuePair<Target, bool>(ById(id), sep));
                sep = false;
            }
            return list;
        }

        public static string Canonical(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".jpeg": case ".jfif": return "jpg";
                case ".tif": return "tiff";
                case ".oga": return "ogg";
                default: return ext.TrimStart('.').ToLowerInvariant();
            }
        }

        public static IEnumerable<string> AllSourceExtensions()
        {
            return Menus.SelectMany(m => m.Sources);
        }
    }
}
