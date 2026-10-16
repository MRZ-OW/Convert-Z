using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ConvertZ
{
    sealed class StreamInfo
    {
        public int Index;
        public string Type = "", Codec = "", PixFmt = "", SampleFmt = "";
        public int Width, Height, Channels, BitsRaw;
        public bool AttachedPic;

        public bool HasAlpha
        {
            get
            {
                string f = PixFmt;
                return f.StartsWith("yuva") || f.StartsWith("ya") || f.Contains("rgba") || f.Contains("bgra")
                    || f.Contains("argb") || f.Contains("abgr") || f.StartsWith("gbrap") || f == "pal8";
            }
        }

        public bool Lossless
        {
            get
            {
                return Codec.StartsWith("pcm_") || Codec == "flac" || Codec == "alac" || Codec == "wavpack"
                    || Codec == "ape" || Codec == "tta" || Codec == "truehd" || Codec == "mlp";
            }
        }

        public bool HighBitDepth
        {
            get { return BitsRaw > 16 || SampleFmt == "s32" || SampleFmt == "s32p" || Codec == "pcm_f32le" || Codec == "pcm_s24le" || Codec == "pcm_s32le"; }
        }
    }

    sealed class ProbeInfo
    {
        public double Duration = double.NaN;
        public string FormatName = "";
        public List<StreamInfo> Streams = new List<StreamInfo>();

        public List<StreamInfo> Video { get { return Streams.Where(s => s.Type == "video" && !s.AttachedPic).ToList(); } }
        public List<StreamInfo> Audio { get { return Streams.Where(s => s.Type == "audio").ToList(); } }
        public List<StreamInfo> Subs { get { return Streams.Where(s => s.Type == "subtitle").ToList(); } }
        public StreamInfo Cover
        {
            get { return Streams.FirstOrDefault(s => s.Type == "video" && s.AttachedPic && (s.Codec == "mjpeg" || s.Codec == "png")); }
        }

        public static ProbeInfo Run(string path, out string error)
        {
            error = null;
            var lines = new List<string>();
            var r = Ffmpeg.Run(Ffmpeg.FfprobePath, new[]
            {
                "-v", "error", "-hide_banner",
                "-show_entries",
                "format=duration,format_name:stream=index,codec_type,codec_name,pix_fmt,width,height,sample_fmt,channels,bits_per_raw_sample:stream_disposition=attached_pic",
                "-of", "flat", path,
            }, l => { lock (lines) lines.Add(l); }, null);
            if (r.ExitCode != 0)
            {
                error = r.Tail(2);
                if (error.Length == 0) error = "ffprobe exit code " + r.ExitCode;
                return null;
            }
            return Parse(lines);
        }

        public static ProbeInfo Parse(IEnumerable<string> lines)
        {
            var info = new ProbeInfo();
            var byIdx = new SortedDictionary<int, StreamInfo>();
            foreach (var raw in lines)
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string key = raw.Substring(0, eq), val = raw.Substring(eq + 1).Trim();
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"') val = val.Substring(1, val.Length - 2);
                if (key == "format.duration")
                {
                    double d;
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0) info.Duration = d;
                    continue;
                }
                if (key == "format.format_name") { info.FormatName = val; continue; }
                if (!key.StartsWith("streams.stream.")) continue;
                string rest = key.Substring("streams.stream.".Length);
                int dot = rest.IndexOf('.');
                int idx;
                if (dot <= 0 || !int.TryParse(rest.Substring(0, dot), out idx)) continue;
                StreamInfo s;
                if (!byIdx.TryGetValue(idx, out s)) byIdx[idx] = s = new StreamInfo { Index = idx };
                string field = rest.Substring(dot + 1);
                int n;
                int.TryParse(val, out n);
                switch (field)
                {
                    case "index": s.Index = n; break;
                    case "codec_type": s.Type = val; break;
                    case "codec_name": s.Codec = val; break;
                    case "pix_fmt": s.PixFmt = val; break;
                    case "sample_fmt": s.SampleFmt = val; break;
                    case "width": s.Width = n; break;
                    case "height": s.Height = n; break;
                    case "channels": s.Channels = n; break;
                    case "bits_per_raw_sample": s.BitsRaw = n; break;
                    case "disposition.attached_pic": s.AttachedPic = n == 1; break;
                }
            }
            info.Streams.AddRange(byIdx.Values);
            return info;
        }
    }
}
