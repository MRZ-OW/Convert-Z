using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ConvertZ
{
    enum VideoPath
    {
        GpuFull,
        GpuDecode,
        Cpu,
    }

    sealed class PlanOptions
    {
        public bool AllowCopy = true;
        public VideoPath Path = VideoPath.GpuFull;
        public bool HwDecode = true;
    }

    sealed class Plan
    {
        public readonly List<string> Args = new List<string>();
        public string Error;
        public bool UsedCopy;
        public bool UsedHwDecode;
        public VideoPath? Path;
        public string Summary = "";

        public void Add(params string[] a) { Args.AddRange(a); }
    }

    static class Planner
    {
        public const string H264Gpu = "h264_nvenc", Av1Gpu = "av1_nvenc";

        const string OpusLayouts = "aformat=channel_layouts=mono|stereo|3.0|quad|5.0|5.1|6.1|7.1";

        public static bool EncodesWithNvenc(Target t)
        {
            return t.Id == "mp4" || t.Id == "mov" || t.Id == "mkv" || t.Id == "webm";
        }

        public static Plan Build(ProbeInfo p, Target t, string input, string output, PlanOptions o)
        {
            var plan = new Plan();
            plan.Add("-hide_banner", "-nostdin", "-y", "-v", "error", "-nostats", "-progress", "pipe:1");
            switch (t.Kind)
            {
                case Kind.Video:
                    if (t.Id == "gif") Gif(p, input, o, plan);
                    else VideoFile(p, t, input, o, plan);
                    break;
                case Kind.Audio: AudioFile(p, t, input, o, plan); break;
                case Kind.Image: ImageFile(p, t, input, plan); break;
            }
            plan.Args.Add(output);
            return plan;
        }

        static readonly string[] TextSubs = { "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text" };

        static bool VideoCopyOk(string target, string codec)
        {
            switch (target)
            {
                case "mp4": return codec == "h264" || codec == "hevc" || codec == "av1";
                case "mov": return codec == "h264" || codec == "hevc";
                case "mkv": return codec != "rawvideo";
                case "webm": return codec == "vp8" || codec == "vp9" || codec == "av1";
                default: return false;
            }
        }

        static bool AudioCopyOk(string target, string codec)
        {
            switch (target)
            {
                case "mp4": case "mov": return codec == "aac" || codec == "mp3" || codec == "ac3" || codec == "eac3" || codec == "alac";
                case "mkv": return codec.Length > 0;
                case "webm": return codec == "opus" || codec == "vorbis";
                default: return false;
            }
        }

        static void VideoFile(ProbeInfo p, Target t, string input, PlanOptions o, Plan plan)
        {
            var vids = p.Video;
            if (vids.Count == 0) { plan.Error = "This file has no video track."; return; }
            var auds = p.Audio;

            bool vcopy = o.AllowCopy && vids.All(v => VideoCopyOk(t.Id, v.Codec));
            VideoPath path = o.Path;
            if (path == VideoPath.GpuFull && (t.Id == "avi" || !o.HwDecode)) path = VideoPath.GpuDecode;

            if (!vcopy)
            {
                if (path == VideoPath.GpuFull) plan.Add("-hwaccel", "cuda", "-hwaccel_output_format", "cuda");
                else if (path == VideoPath.GpuDecode && o.HwDecode) plan.Add("-hwaccel", "cuda");
                plan.UsedHwDecode = path != VideoPath.Cpu && o.HwDecode;
            }
            plan.Add("-i", input);

// keep every track (OBS multi-track recordings)
            foreach (var v in vids) plan.Add("-map", "0:" + v.Index);
            foreach (var a in auds) plan.Add("-map", "0:" + a.Index);

            var subs = new List<KeyValuePair<StreamInfo, string>>();
            foreach (var s in p.Subs)
            {
                bool text = TextSubs.Contains(s.Codec);
                string codec = null;
                if (t.Id == "mp4" || t.Id == "mov") codec = text ? "mov_text" : null;
                else if (t.Id == "webm") codec = text ? "webvtt" : null;
                else if (t.Id == "mkv") codec = s.Codec == "mov_text" ? "srt" : text ? (o.AllowCopy ? "copy" : "srt") : (o.AllowCopy ? "copy" : null);
                if (codec != null) subs.Add(new KeyValuePair<StreamInfo, string>(s, codec));
            }
            foreach (var s in subs) plan.Add("-map", "0:" + s.Key.Index);

            string vsum;
            if (vcopy)
            {
                plan.Add("-c:v", "copy");
                if ((t.Id == "mp4" || t.Id == "mov") && vids.Any(v => v.Codec == "hevc")) plan.Add("-tag:v", "hvc1");
                plan.UsedCopy = true;
                vsum = "video copy";
            }
            else
            {
                plan.Path = path;
                bool odd = vids.Any(v => (v.Width & 1) == 1 || (v.Height & 1) == 1);
                const string even = "trunc(iw/2)*2:trunc(ih/2)*2";
                bool gpuEnc = path != VideoPath.Cpu && t.Id != "avi";
                if (path == VideoPath.GpuFull)
                    plan.Add("-vf", "scale_cuda=w=trunc(iw/2)*2:h=trunc(ih/2)*2:format=yuv420p");
                else if (odd)
                    plan.Add("-vf", "scale=" + even);

                switch (t.Id)
                {
                    case "webm":
                        if (gpuEnc) plan.Add("-c:v", Av1Gpu, "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "30", "-b:v", "0", "-spatial-aq", "1");
                        else plan.Add("-c:v", "libsvtav1", "-preset", "8", "-crf", "32");
                        break;
                    case "avi":
                        plan.Add("-c:v", "mpeg4", "-vtag", "XVID", "-q:v", "3");
                        break;
                    default:
                        if (gpuEnc) plan.Add("-c:v", H264Gpu, "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "21", "-b:v", "0", "-spatial-aq", "1", "-profile:v", "high");
                        else plan.Add("-c:v", "libx264", "-preset", "fast", "-crf", "20");
                        break;
                }
                if (path != VideoPath.GpuFull) plan.Add("-pix_fmt", "yuv420p");
                vsum = "video " + (t.Id == "avi" ? "mpeg4" : t.Id == "webm" ? (gpuEnc ? Av1Gpu : "libsvtav1") : (gpuEnc ? H264Gpu : "libx264"))
                     + " [" + path + "]";
            }

            var asum = new List<string>();
            for (int i = 0; i < auds.Count; i++)
            {
                var a = auds[i];
                string n = i.ToString(CultureInfo.InvariantCulture);
                if (o.AllowCopy && AudioCopyOk(t.Id, a.Codec))
                {
                    plan.Add("-c:a:" + n, "copy");
                    plan.UsedCopy = true;
                    asum.Add("copy");
                    continue;
                }
                bool multi = a.Channels > 2;
                switch (t.Id)
                {
                    case "webm":
                        plan.Add("-c:a:" + n, "libopus", "-b:a:" + n, multi ? "256k" : "128k");
                        if (multi) plan.Add("-filter:a:" + n, OpusLayouts, "-mapping_family:a:" + n, "1");
                        asum.Add("opus");
                        break;
                    case "avi": plan.Add("-c:a:" + n, "libmp3lame", "-q:a:" + n, "2"); asum.Add("mp3"); break;
                    default: plan.Add("-c:a:" + n, "aac", "-b:a:" + n, multi ? "384k" : "192k"); asum.Add("aac"); break;
                }
            }
            for (int i = 0; i < subs.Count; i++) plan.Add("-c:s:" + i.ToString(CultureInfo.InvariantCulture), subs[i].Value);

            if (t.Id == "mp4" || t.Id == "mov") plan.Add("-movflags", "+faststart");
            plan.Add("-max_muxing_queue_size", "4096");
            plan.Summary = vsum + "; audio " + (asum.Count == 0 ? "none" : string.Join(",", asum)) + "; subs " + subs.Count;
        }

        static void Gif(ProbeInfo p, string input, PlanOptions o, Plan plan)
        {
            var v = p.Video.FirstOrDefault();
            if (v == null) { plan.Error = "This file has no video track."; return; }
            var path = o.Path == VideoPath.Cpu ? VideoPath.Cpu : VideoPath.GpuDecode;
            if (path == VideoPath.GpuDecode && o.HwDecode) { plan.Add("-hwaccel", "cuda"); plan.UsedHwDecode = true; }
            plan.Path = path;
            plan.Add("-i", input);
            plan.Add("-filter_complex",
                "[0:" + v.Index + "]fps=15,scale='min(640,iw)':-1:flags=lanczos,split[a][b];" +
                "[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle");
            plan.Add("-an", "-sn", "-dn", "-loop", "0");
            plan.Summary = "gif palette [" + path + "]";
        }

        static void AudioFile(ProbeInfo p, Target t, string input, PlanOptions o, Plan plan)
        {
            var a = p.Audio.FirstOrDefault();
            if (a == null) { plan.Error = "This file has no audio track."; return; }
            plan.Add("-i", input, "-map", "0:" + a.Index);

            var cover = p.Video.Count == 0 ? p.Cover : null;
            bool keepCover = cover != null && (t.Id == "mp3" || t.Id == "m4a" || t.Id == "flac");
            if (keepCover) plan.Add("-map", "0:" + cover.Index, "-c:v", "copy", "-disposition:v:0", "attached_pic");
            else plan.Add("-vn");
            plan.Add("-sn", "-dn");

            bool copy = false;
            switch (t.Id)
            {
                case "mp3":
                    copy = o.AllowCopy && a.Codec == "mp3";
                    if (copy) plan.Add("-c:a", "copy"); else plan.Add("-c:a", "libmp3lame", "-q:a", "2");
                    plan.Add("-id3v2_version", "3");
                    break;
                case "m4a":
                    copy = o.AllowCopy && (a.Codec == "aac" || a.Codec == "alac");
                    if (copy) plan.Add("-c:a", "copy"); else plan.Add("-c:a", "aac", "-b:a", a.Lossless ? "256k" : "192k");
                    plan.Add("-movflags", "+faststart");
                    break;
                case "wav":
                    plan.Add("-c:a", a.HighBitDepth ? "pcm_s24le" : "pcm_s16le");
                    break;
                case "flac":
                    copy = o.AllowCopy && a.Codec == "flac";
                    if (copy) plan.Add("-c:a", "copy");
                    else if (a.Lossless && a.HighBitDepth) plan.Add("-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24");
                    else plan.Add("-c:a", "flac", "-sample_fmt", "s16");
                    break;
                case "ogg":
                    copy = o.AllowCopy && a.Codec == "vorbis";
                    if (copy) plan.Add("-c:a", "copy"); else plan.Add("-c:a", "libvorbis", "-q:a", "6");
                    break;
                case "opus":
                    copy = o.AllowCopy && a.Codec == "opus";
                    if (copy) plan.Add("-c:a", "copy");
                    else if (a.Channels > 2) plan.Add("-af", OpusLayouts, "-c:a", "libopus", "-mapping_family", "1", "-b:a", "256k");
                    else plan.Add("-c:a", "libopus", "-b:a", a.Lossless ? "160k" : "128k");
                    break;
            }
            plan.UsedCopy = copy;
            plan.Summary = "audio " + (copy ? "copy" : "encode") + (keepCover ? " + cover" : "");
        }

        static void ImageFile(ProbeInfo p, Target t, string input, Plan plan)
        {
            var v = p.Video.FirstOrDefault() ?? p.Streams.FirstOrDefault(s => s.Type == "video");
            if (v == null) { plan.Error = "No picture found in this file."; return; }
            plan.Add("-i", input);
            switch (t.Id)
            {
                case "jpg":
                    if (v.HasAlpha && v.Width > 0 && v.Height > 0)
// jpg has no alpha, flatten onto white
                        plan.Add("-filter_complex", "color=c=white:s=" + v.Width + "x" + v.Height + "[bg];[bg][0:v]overlay=shortest=1:format=auto");
                    plan.Add("-c:v", "mjpeg", "-q:v", "2");
                    break;
                case "png": plan.Add("-c:v", "png"); break;
                case "webp": plan.Add("-c:v", "libwebp", "-quality", "90"); break;
                case "bmp": plan.Add("-c:v", "bmp"); break;
                case "tiff": plan.Add("-c:v", "tiff", "-compression_algo", "lzw"); break;
                case "ico":
                    int big = Math.Max(v.Width, v.Height);
                    int s = big <= 0 ? 256 : Math.Min(256, big);
                    string ss = s.ToString(CultureInfo.InvariantCulture);
                    plan.Add("-vf", "scale=w=" + ss + ":h=" + ss + ":force_original_aspect_ratio=decrease:flags=lanczos,format=rgba," +
                                    "pad=" + ss + ":" + ss + ":(ow-iw)/2:(oh-ih)/2:color=black@0");
                    plan.Add("-c:v", "png");
                    break;
            }
            plan.Add("-frames:v", "1", "-update", "1", "-an", "-sn", "-dn");
            plan.Summary = "image " + t.Id;
        }
    }
}
