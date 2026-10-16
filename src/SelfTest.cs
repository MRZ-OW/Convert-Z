using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ConvertZ
{
    static class SelfTest
    {
        static int _fails;
        static Action<string> _say;

        static void Check(bool ok, string what)
        {
            if (!ok) { Interlocked.Increment(ref _fails); _say("  FAIL " + what); }
        }

        public static int Run(Action<string> say)
        {
            _say = say;
            _fails = 0;
            var total = Stopwatch.StartNew();
            say("ConvertZ self-test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            say("ffmpeg: " + (Ffmpeg.FfmpegPath ?? "NOT FOUND"));
            if (Ffmpeg.FfmpegPath == null) return 2;

            Pure();
            say("pure checks: " + (_fails == 0 ? "ok" : _fails + " failed"));

            string root = Path.Combine(Path.GetTempPath(), "ConvertZ-selftest");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            var fixtures = MakeFixtures(root);
            say("fixtures: " + fixtures.Count);

            Matrix(fixtures);
            Special(root, fixtures);
            Shims(root);

            say("");
            say(_fails == 0 ? "ALL PASSED" : _fails + " FAILURE(S)");
            say("work dir: " + root);
            return _fails == 0 ? 0 : 1;
        }

        static void Pure()
        {
            foreach (var s in new[] { "plain", "with space", "quote\"inside", @"trail\", @"C:\dir with space\", @"a\\""b", "", "é ünïcödé 名前", "x%1y" })
            {
                var back = Argv("prog " + Ffmpeg.Quote(s));
                Check(back.Length == 2 && back[1] == s, "quote round trip [" + s + "] -> [" + string.Join("|", back) + "]");
            }

            string longName = new string('x', 200) + ".mkv";
            string tip = TrayUi.Fit("ConvertZ 12/40 47% · ", longName, " → MP4");
            Check(tip.Length <= 63 && tip.EndsWith(" → MP4"), "tooltip fits 63 chars: " + tip.Length);

            var mp4 = Formats.EntriesFor(".mp4").Select(e => e.Key.Id).ToList();
            Check(!mp4.Contains("mp4") && mp4[0] == "mkv" && mp4.Contains("mp3"), "mp4 menu: " + string.Join(",", mp4));
            var mkv = Formats.EntriesFor(".MKV");
            Check(mkv[0].Key.Id == "mp4" && mkv.First(e => e.Key.Id == "mp3").Value && !mkv[0].Value, "mkv menu: mp4 first, separator before mp3");
            Check(!Formats.EntriesFor(".jpeg").Any(e => e.Key.Id == "jpg"), ".jpeg doesn't offer JPG");
            Check(!Formats.EntriesFor(".tif").Any(e => e.Key.Id == "tiff"), ".tif doesn't offer TIFF");
            Check(Formats.EntriesFor(".gif").Any(e => e.Key.Id == "png") && !Formats.EntriesFor(".gif").Any(e => e.Key.Id == "gif"), "gif menu");
            Check(Formats.EntriesFor(".txt").Count == 0, ".txt has no menu");
            Check(Formats.AllSourceExtensions().Count() == Formats.AllSourceExtensions().Distinct().Count(), "no extension in two menus");

            var probe = ProbeInfo.Parse(new[]
            {
                "streams.stream.0.index=0", "streams.stream.0.codec_name=\"h264\"", "streams.stream.0.codec_type=\"video\"",
                "streams.stream.0.width=1920", "streams.stream.0.height=1080", "streams.stream.0.pix_fmt=\"yuv420p\"",
                "streams.stream.0.disposition.attached_pic=0",
                "streams.stream.1.index=1", "streams.stream.1.codec_name=\"aac\"", "streams.stream.1.codec_type=\"audio\"", "streams.stream.1.channels=2",
                "streams.stream.2.index=2", "streams.stream.2.codec_name=\"opus\"", "streams.stream.2.codec_type=\"audio\"", "streams.stream.2.channels=2",
                "streams.stream.3.index=3", "streams.stream.3.codec_name=\"subrip\"", "streams.stream.3.codec_type=\"subtitle\"",
                "format.duration=\"12.500000\"", "format.format_name=\"matroska,webm\"",
            });
            Check(probe.Video.Count == 1 && probe.Audio.Count == 2 && probe.Subs.Count == 1 && Math.Abs(probe.Duration - 12.5) < 1e-9, "probe parse");

            var p = Planner.Build(probe, Formats.ById("mp4"), "in.mkv", "out.mp4", new PlanOptions());
            string a = string.Join(" ", p.Args);
            Check(a.Contains("-map 0:0 -map 0:1 -map 0:2 -map 0:3"), "mkv->mp4 keeps every track: " + a);
            Check(a.Contains("-c:v copy") && a.Contains("-c:a:0 copy") && a.Contains("-c:a:1 aac") && a.Contains("-c:s:0 mov_text"), "mkv->mp4 copies h264+aac, re-encodes opus, subs to mov_text: " + a);
            Check(!a.Contains("hwaccel"), "no hwaccel when the video is copied");

            p = Planner.Build(probe, Formats.ById("mp4"), "in.mkv", "out.mp4", new PlanOptions { AllowCopy = false });
            a = string.Join(" ", p.Args);
            Check(a.Contains("-hwaccel cuda -hwaccel_output_format cuda") && a.Contains("scale_cuda") && a.Contains("h264_nvenc") && !a.Contains("-pix_fmt"), "GPU full path: " + a);
            p = Planner.Build(probe, Formats.ById("mp4"), "in.mkv", "out.mp4", new PlanOptions { AllowCopy = false, Path = VideoPath.GpuDecode });
            a = string.Join(" ", p.Args);
            Check(a.Contains("-hwaccel cuda -i") && !a.Contains("scale_cuda") && a.Contains("h264_nvenc") && a.Contains("-pix_fmt yuv420p"), "GPU decode path: " + a);
            p = Planner.Build(probe, Formats.ById("webm"), "in.mkv", "out.webm", new PlanOptions { AllowCopy = false });
            Check(string.Join(" ", p.Args).Contains("av1_nvenc"), "webm uses AV1 NVENC");
            p = Planner.Build(probe, Formats.ById("mp4"), "in.mkv", "out.mp4", new PlanOptions { AllowCopy = false, Path = VideoPath.Cpu });
            a = string.Join(" ", p.Args);
            Check(!a.Contains("hwaccel") && a.Contains("libx264"), "CPU path: " + a);
            p = Planner.Build(probe, Formats.ById("mp3"), "in.mkv", "out.mp3", new PlanOptions());
            Check(string.Join(" ", p.Args).Contains("-map 0:1 -vn"), "audio extraction takes the first track");

            var dir = Path.Combine(Path.GetTempPath(), "ConvertZ-unique-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.mp4"), "x");
            File.WriteAllText(Path.Combine(dir, "a (1).mp4"), "x");
            Check(Path.GetFileName(Converter.UniquePath(dir, "a", ".mp4")) == "a (2).mp4", "unique name skips existing");
            Check(Path.GetFileName(Converter.UniquePath(dir, "b", ".mp4")) == "b.mp4", "unique name plain");
            Check(Path.GetFileName(Converter.TempPathFor(Path.Combine(dir, "a (2).mp4"))) == "a (2).converting.mp4", "temp name keeps extension");
            Directory.Delete(dir, true);
        }

        static string[] Argv(string cmdline)
        {
            int n;
            IntPtr p = CommandLineToArgvW(cmdline, out n);
            var r = new string[n];
            for (int i = 0; i < n; i++) r[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size));
            LocalFree(p);
            return r;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string cmd, out int n);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

        sealed class Fixture
        {
            public string Path;
            public ProbeInfo Probe;
        }

        static readonly string[] Lavfi = { "-f", "lavfi", "-i" };

        static List<Fixture> MakeFixtures(string root)
        {
            string fx = Path.Combine(root, "_src");
            Directory.CreateDirectory(fx);
            string srt = Path.Combine(fx, "subs.srt");
            File.WriteAllText(srt, "1\r\n00:00:00,000 --> 00:00:01,500\r\nHello\r\n\r\n2\r\n00:00:01,500 --> 00:00:02,500\r\nWorld\r\n");
            string cover = Path.Combine(fx, "cover.jpg");
            Gen(new[] { "-f", "lavfi", "-i", "testsrc2=s=200x200", "-frames:v", "1", "-update", "1", cover });

            const string V = "testsrc2=s=320x240:r=25:d=2.4";
            const string A = "sine=f=440:d=2.4:sample_rate=48000";
            var specs = new List<string[]>
            {
                new[] { "OBS rec é 2026-10-15 20-47-10.mkv", "@obs" },
                new[] { "clip.mp4", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest" },
                new[] { "clip.mov", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "prores_ks", "-c:a", "pcm_s16le", "-shortest" },
                new[] { "clip.avi", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "mpeg4", "-c:a", "libmp3lame", "-shortest" },
                new[] { "clip.wmv", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "wmv2", "-c:a", "wmav2", "-shortest" },
                new[] { "clip.flv", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", "sine=f=440:d=2.4:sample_rate=44100", "-c:v", "flv", "-c:a", "libmp3lame", "-shortest" },
                new[] { "clip.webm", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-c:a", "libopus", "-shortest" },
                new[] { "clip.m4v", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest" },
                new[] { "clip.mpg", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "mpeg2video", "-c:a", "mp2", "-shortest" },
                new[] { "clip.mpeg", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "mpeg1video", "-c:a", "mp2", "-f", "mpeg", "-shortest" },
                new[] { "clip.ts", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest" },
                new[] { "clip.mts", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "ac3", "-f", "mpegts", "-shortest" },
                new[] { "clip.m2ts", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "ac3", "-f", "mpegts", "-mpegts_m2ts_mode", "1", "-shortest" },
                new[] { "clip.3gp", "-f", "lavfi", "-i", "testsrc2=s=176x144:r=15:d=2.4", "-f", "lavfi", "-i", "sine=f=440:d=2.4:sample_rate=8000", "-c:v", "h263", "-c:a", "libopencore_amrnb", "-ac", "1", "-shortest" },
                new[] { "clip.ogv", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-c:v", "libtheora", "-c:a", "libvorbis", "-shortest" },
                new[] { "clip.vob", "-f", "lavfi", "-i", "testsrc2=s=720x480:r=30000/1001:d=2.4", "-f", "lavfi", "-i", A, "-c:v", "mpeg2video", "-c:a", "ac3", "-f", "vob", "-shortest" },
                new[] { "hevc 10bit.mkv", "-f", "lavfi", "-i", "testsrc2=s=322x242:r=30:d=2.4", "-f", "lavfi", "-i", A, "-c:v", "libx265", "-pix_fmt", "yuv420p10le", "-x265-params", "log-level=error", "-c:a", "libopus", "-shortest" },
                new[] { "surround video.mkv", "-f", "lavfi", "-i", V, "-f", "lavfi", "-i", A, "-af", "pan=5.1(side)|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "flac", "-shortest" },
                new[] { "no audio.mp4", "-f", "lavfi", "-i", V, "-c:v", "libx264", "-pix_fmt", "yuv420p" },
                new[] { "anim.gif", "-f", "lavfi", "-i", "testsrc2=s=160x120:r=10:d=2" },

                new[] { "song.wav", "-f", "lavfi", "-i", "sine=f=440:d=3", "-ac", "2", "-c:a", "pcm_s16le" },
                new[] { "song 24bit.flac", "-f", "lavfi", "-i", "sine=f=440:d=3:sample_rate=96000", "-ac", "2", "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24" },
                new[] { "song cover.mp3", "@cover" },
                new[] { "song.m4a", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "aac" },
                new[] { "book.m4b", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "aac", "-f", "ipod" },
                new[] { "song.aac", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "aac", "-f", "adts" },
                new[] { "song.ogg", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "libvorbis" },
                new[] { "song.oga", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "libvorbis", "-f", "ogg" },
                new[] { "song.opus", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "libopus" },
                new[] { "song.wma", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "wmav2" },
                new[] { "song.aif", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "pcm_s16be", "-f", "aiff" },
                new[] { "song.aiff", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "pcm_s16be" },
                new[] { "surround.ac3", "-f", "lavfi", "-i", "sine=f=440:d=3", "-af", "pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0", "-c:a", "ac3" },
                new[] { "voice.amr", "-f", "lavfi", "-i", "sine=f=440:d=3:sample_rate=8000", "-c:a", "libopencore_amrnb", "-ac", "1" },
                new[] { "song.mka", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "flac" },
                new[] { "song.wv", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "wavpack" },

                new[] { "logo alpha.png", "-f", "lavfi", "-i", "color=c=red:s=300x200,format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='if(between(X,60,239)*between(Y,50,149),255,0)'", "-frames:v", "1", "-update", "1" },
                new[] { "photo.jpg", "-f", "lavfi", "-i", "testsrc2=s=640x480", "-frames:v", "1", "-update", "1" },
                new[] { "photo.jpeg", "-f", "lavfi", "-i", "testsrc2=s=640x480", "-frames:v", "1", "-update", "1" },
                new[] { "photo.jfif", "-f", "lavfi", "-i", "testsrc2=s=640x480", "-frames:v", "1", "-update", "1", "-c:v", "mjpeg", "-f", "image2" },
                new[] { "pic.bmp", "-f", "lavfi", "-i", "testsrc2=s=320x200", "-frames:v", "1", "-update", "1" },
                new[] { "pic.webp", "-f", "lavfi", "-i", "testsrc2=s=320x200", "-frames:v", "1", "-update", "1", "-c:v", "libwebp" },
                new[] { "pic.tif", "-f", "lavfi", "-i", "testsrc2=s=320x200", "-frames:v", "1", "-update", "1" },
                new[] { "pic.tiff", "-f", "lavfi", "-i", "testsrc2=s=320x200", "-frames:v", "1", "-update", "1" },
                new[] { "pic.ico", "-f", "lavfi", "-i", "testsrc2=s=64x64,format=rgba", "-frames:v", "1", "-update", "1", "-c:v", "png" },
                new[] { "pic.tga", "-f", "lavfi", "-i", "testsrc2=s=320x200", "-frames:v", "1", "-update", "1" },
                new[] { "pic.avif", "-f", "lavfi", "-i", "testsrc2=s=320x200", "-frames:v", "1", "-c:v", "libaom-av1", "-still-picture", "1", "-cpu-used", "8", "-pix_fmt", "yuv420p", "-f", "avif" },
                new[] { "pic.jxl", "-f", "lavfi", "-i", "testsrc2=s=320x200", "-frames:v", "1", "-update", "1", "-c:v", "libjxl" },
            };

            var list = new List<Fixture>();
            foreach (var spec in specs)
            {
                string name = spec[0];
                string sub = Path.Combine(root, Path.GetFileNameWithoutExtension(name) + "_" + Path.GetExtension(name).TrimStart('.'));
                Directory.CreateDirectory(sub);
                string outp = Path.Combine(sub, name);
                var args = new List<string>();
                if (spec[1] == "@obs")
                    args.AddRange(new[] { "-f", "lavfi", "-i", "testsrc2=s=321x241:r=30:d=2.4", "-f", "lavfi", "-i", "sine=f=440:d=2.4:sample_rate=48000",
                        "-f", "lavfi", "-i", "sine=f=880:d=2.4:sample_rate=48000", "-i", srt,
                        "-map", "0", "-map", "1", "-map", "2", "-map", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-c:s", "srt" });
                else if (spec[1] == "@cover")
                    args.AddRange(new[] { "-f", "lavfi", "-i", "sine=f=440:d=3", "-i", cover, "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame",
                        "-c:v", "copy", "-disposition:v", "attached_pic", "-id3v2_version", "3" });
                else args.AddRange(spec.Skip(1));
                args.Add(outp);
                if (!Gen(args.ToArray())) { _say("  (fixture " + name + " could not be generated - skipped)"); continue; }
                list.Add(new Fixture { Path = outp });
            }

            foreach (var f in list)
            {
                string err;
                f.Probe = ProbeInfo.Run(f.Path, out err);
                Check(f.Probe != null, "probe fixture " + f.Path + ": " + err);
            }
            return list.Where(f => f.Probe != null).ToList();
        }

        static bool Gen(string[] args)
        {
            var all = new List<string> { "-hide_banner", "-nostdin", "-y", "-v", "error" };
            all.AddRange(args);
            var r = Ffmpeg.Run(Ffmpeg.FfmpegPath, all, null, null);
            if (r.ExitCode != 0) _say("  gen failed: " + r.Tail(2));
            return r.ExitCode == 0;
        }

        static void Matrix(List<Fixture> fixtures)
        {
            var lines = new Dictionary<Fixture, List<string>>();
            int conversions = 0;
            var gpuSeen = new List<string>();
            Parallel.ForEach(fixtures, new ParallelOptions { MaxDegreeOfParallelism = 4 }, f =>
            {
                var mine = new List<string>();
                string ext = Path.GetExtension(f.Path);
                foreach (var e in Formats.EntriesFor(ext))
                {
                    var t = e.Key;
                    if (f.Probe.Audio.Count == 0 && t.Kind == Kind.Audio) continue;
                    var job = new Job(f.Path, t);
                    bool ok = Converter.Run(job, null);
                    Interlocked.Increment(ref conversions);
                    string verdict = ok ? Verify(f, job) : "conversion failed: " + job.Error;
                    if (verdict != null) { Interlocked.Increment(ref _fails); mine.Add("  FAIL " + Path.GetFileName(f.Path) + " -> " + t.Id + ": " + verdict); }
                    else mine.Add("  ok   " + Path.GetFileName(f.Path) + " -> " + Path.GetFileName(job.Output) + "   [" + job.Summary + "]");
                    if (ok && Planner.EncodesWithNvenc(t) && job.Summary.StartsWith("video ") && !job.Summary.StartsWith("video copy"))
                        lock (gpuSeen) gpuSeen.Add(Path.GetFileName(f.Path) + " -> " + t.Id + ": " + job.Summary.Split(';')[0]);
                }
                lock (lines) lines[f] = mine;
            });
            foreach (var f in fixtures) foreach (var l in lines[f]) _say(l);
            _say("matrix: " + conversions + " conversions");

            var cpu = gpuSeen.Where(s => s.Contains("libx264") || s.Contains("libsvtav1")).ToList();
            _say("video re-encodes: " + gpuSeen.Count + ", on NVENC: " + (gpuSeen.Count - cpu.Count) + ", on CPU: " + cpu.Count);
            if (Converter.NvencBroken)
                _say("  WARN NVENC can't open on this PC (driver / GPU) - re-encodes used the CPU. Run --gpucheck for the reason.");
            else if (cpu.Count > 0)
            {
                Interlocked.Increment(ref _fails);
                _say("  FAIL NVENC works here but these re-encodes fell back to the CPU:");
                foreach (var s in cpu) _say("         " + s);
            }
        }

        static string Verify(Fixture f, Job job)
        {
            var t = job.Target;
            if (job.Output == null || !File.Exists(job.Output)) return "no output";
            if (!string.Equals(Path.GetExtension(job.Output), t.Ext, StringComparison.OrdinalIgnoreCase)) return "wrong extension";
            if (!string.Equals(Path.GetDirectoryName(job.Output), Path.GetDirectoryName(f.Path), StringComparison.OrdinalIgnoreCase)) return "not next to the source";
            if (Directory.GetFiles(Path.GetDirectoryName(f.Path), "*.converting.*").Length > 0) return "temp file left behind";
            string err;
            var o = ProbeInfo.Run(job.Output, out err);
            if (o == null) return "output unreadable: " + err;
            var s = f.Probe;
            switch (t.Kind)
            {
                case Kind.Video:
                    if (t.Id == "gif")
                    {
                        if (o.Video.Count != 1 || o.Video[0].Codec != "gif") return "gif stream";
                        break;
                    }
                    if (o.Video.Count != s.Video.Count) return "video tracks " + o.Video.Count + " != " + s.Video.Count;
                    if (o.Audio.Count != s.Audio.Count) return "audio tracks " + o.Audio.Count + " != " + s.Audio.Count;
                    string vc = o.Video[0].Codec;
                    string[] allowed = t.Id == "webm" ? new[] { "av1", "vp9", "vp8" } : t.Id == "avi" ? new[] { "mpeg4" } : new[] { "h264", "hevc", "av1", s.Video[0].Codec };
                    if (!allowed.Contains(vc)) return "video codec " + vc;
                    if (vc == "h264" && o.Video[0].PixFmt != "yuv420p" && s.Video[0].Codec != "h264") return "pix_fmt " + o.Video[0].PixFmt;
                    if (s.Subs.Count > 0 && t.Id != "avi" && o.Subs.Count != s.Subs.Count) return "subtitles " + o.Subs.Count + " != " + s.Subs.Count;
                    break;
                case Kind.Audio:
                    if (o.Audio.Count != 1) return "audio tracks " + o.Audio.Count;
                    var expect = new Dictionary<string, string[]>
                    {
                        { "mp3", new[] { "mp3" } }, { "m4a", new[] { "aac", "alac" } }, { "wav", new[] { "pcm_s16le", "pcm_s24le" } },
                        { "flac", new[] { "flac" } }, { "ogg", new[] { "vorbis" } }, { "opus", new[] { "opus" } },
                    };
                    if (!expect[t.Id].Contains(o.Audio[0].Codec)) return "audio codec " + o.Audio[0].Codec;
                    if (s.Video.Count == 0 && s.Cover != null && (t.Id == "mp3" || t.Id == "m4a" || t.Id == "flac") && o.Cover == null) return "cover art dropped";
                    break;
                case Kind.Image:
                    if (o.Streams.Count(x => x.Type == "video") != 1) return "image streams";
                    var v = o.Streams.First(x => x.Type == "video");
                    var codecs = new Dictionary<string, string> { { "png", "png" }, { "jpg", "mjpeg" }, { "webp", "webp" }, { "bmp", "bmp" }, { "tiff", "tiff" }, { "ico", "png" } };
                    if (v.Codec != codecs[t.Id]) return "image codec " + v.Codec;
                    var src = s.Video.FirstOrDefault() ?? s.Streams.First(x => x.Type == "video");
                    if (t.Id == "ico")
                    {
                        if (v.Width != v.Height || v.Width > 256) return "icon size " + v.Width + "x" + v.Height;
                    }
                    else if (src.Width > 0 && (v.Width != src.Width || v.Height != src.Height))
                        return "size " + v.Width + "x" + v.Height + " != " + src.Width + "x" + src.Height;
                    break;
            }
            if (t.Kind != Kind.Image && !double.IsNaN(s.Duration) && !double.IsNaN(o.Duration))
            {
                double tol = Math.Max(0.35, s.Duration * 0.1);
                if (Math.Abs(o.Duration - s.Duration) > tol) return "duration " + o.Duration.ToString("0.00") + " vs " + s.Duration.ToString("0.00");
            }
            return null;
        }

        static void Special(string root, List<Fixture> fixtures)
        {
            _say("special:");
            Func<string, Fixture> fx = name => fixtures.FirstOrDefault(f => Path.GetFileName(f.Path) == name);

            var png = fx("logo alpha.png");
            if (png != null)
            {
                string jpg = Path.Combine(Path.GetDirectoryName(png.Path), "logo alpha.jpg");
                using (var bmp = new Bitmap(jpg))
                {
                    var c = bmp.GetPixel(2, 2);
                    Check(c.R > 240 && c.G > 240 && c.B > 240, "alpha -> jpg corner is white (got " + c + ")");
                    var mid = bmp.GetPixel(150, 100);
                    Check(mid.R > 200 && mid.G < 60, "alpha -> jpg keeps the red box (got " + mid + ")");
                }
                _say("  ok   transparent PNG -> JPG on white");
            }

            var obs = fx("OBS rec é 2026-10-15 20-47-10.mkv");
            if (obs != null)
            {
                var job = new Job(obs.Path, Formats.ById("mp4"));
                Check(Converter.Run(job, null), "obs again: " + job.Error);
                Check(Path.GetFileName(job.Output) == "OBS rec é 2026-10-15 20-47-10 (1).mp4", "second conversion gets (1): " + job.Output);
                string err;
                var o = ProbeInfo.Run(job.Output, out err);
                Check(o != null && o.Audio.Count == 2 && o.Subs.Count == 1, "remux keeps 2 audio + 1 subtitle");
                _say("  " + (job.Summary.StartsWith("video copy") ? "ok  " : "FAIL") + " OBS mkv -> mp4 summary: " + job.Summary);
                if (!job.Summary.StartsWith("video copy")) _fails++;
            }

            var flac = fx("song 24bit.flac");
            if (flac != null)
            {
                string err;
                var w = ProbeInfo.Run(Path.Combine(Path.GetDirectoryName(flac.Path), "song 24bit.wav"), out err);
                Check(w != null && w.Audio[0].Codec == "pcm_s24le", "24-bit flac -> wav is 24-bit (" + (w == null ? err : w.Audio[0].Codec) + ")");
                _say("  ok   24-bit FLAC -> 24-bit WAV");
            }

            var silent = fx("no audio.mp4");
            if (silent != null)
            {
                var job = new Job(silent.Path, Formats.ById("mp3"));
                bool ok = Converter.Run(job, null);
                Check(!ok && job.Error != null && job.Error.Contains("no audio"), "no-audio -> mp3 error: " + job.Error);
                Check(!File.Exists(Path.ChangeExtension(silent.Path, ".mp3")), "no-audio -> no mp3 file");
                _say("  ok   no audio track -> \"" + job.Error + "\"");
            }

            string fake = Path.Combine(root, "fake_mp4", "not really.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(fake));
            File.WriteAllText(fake, "this is text, not a video");
            var fj = new Job(fake, Formats.ById("mkv"));
            Check(!Converter.Run(fj, null) && fj.Error != null, "fake mp4 fails");
            Check(Directory.GetFiles(Path.GetDirectoryName(fake)).Length == 1, "fake mp4 leaves no output");
            _say("  ok   non-media file -> \"" + (fj.Error ?? "").Split('\n')[0] + "\"");

            var clip = fx("clip.mov");
            if (clip != null)
            {
                var seen = new List<double>();
                var job = new Job(clip.Path, Formats.ById("mkv"));
                Converter.Run(job, p => { lock (seen) seen.Add(p); });
                Check(seen.Count >= 2 && seen.Last() == 1 && seen.Any(p => p > 0 && p < 1), "progress callbacks: " + string.Join(",", seen.Select(p => p.ToString("0.00"))));
                _say("  ok   progress " + string.Join(" ", seen.Distinct().Select(p => p.ToString("0.00"))));
            }

            string longSrc = Path.Combine(root, "long_mp4", "long.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(longSrc));
            if (Gen(new[] { "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=150", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", longSrc }))
            {
                var job = new Job(longSrc, Formats.ById("webm"));
                double last = 0;
                var running = new ManualResetEventSlim();
                var th = new Thread(() => Converter.Run(job, p => { last = p; if (p > 0 && p < 0.9) running.Set(); }));
                th.Start();
                bool caught = running.Wait(120000);
                job.Cancel = true;
                th.Join(15000);
                Thread.Sleep(500);
                var left = Directory.GetFiles(Path.GetDirectoryName(longSrc)).Select(Path.GetFileName).ToList();
                Check(caught, "conversion reported progress before finishing (cancel window)");
                Check(job.Cancelled, "cancel flagged");
                Check(left.Count == 1, "cancel leaves only the source: " + string.Join(", ", left));
                int orphans = FfmpegRunningOn("long.mp4");
                Check(orphans == 0, "cancel leaves no ffmpeg running (" + orphans + ")");
                _say("  ok   cancel at " + (last * 100).ToString("0") + "% -> nothing left behind");
            }
        }

        static void Shims(string root)
        {
            Action<string, long> dummy = (path, size) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = File.Create(path)) fs.SetLength(size);
            };
            const long Small = 150 * 1024, Big = 60L * 1024 * 1024;

            string choco = Path.Combine(root, "_choco");
            dummy(Path.Combine(choco, @"bin\ffmpeg.exe"), Small);
            dummy(Path.Combine(choco, @"bin\ffprobe.exe"), Small);
            string chocoReal = Path.Combine(choco, @"lib\ffmpeg-full\tools\ffmpeg\bin");
            dummy(Path.Combine(chocoReal, "ffmpeg.exe"), Big);
            dummy(Path.Combine(chocoReal, "ffprobe.exe"), Big);
            Check(string.Equals(Ffmpeg.Unshim(Path.Combine(choco, "bin")), chocoReal, StringComparison.OrdinalIgnoreCase), "chocolatey shim resolves to lib\\...\\bin");

            string scoop = Path.Combine(root, "_scoop");
            string scoopReal = Path.Combine(scoop, @"apps\ffmpeg\current\bin");
            dummy(Path.Combine(scoop, @"shims\ffmpeg.exe"), Small);
            dummy(Path.Combine(scoop, @"shims\ffprobe.exe"), Small);
            dummy(Path.Combine(scoopReal, "ffmpeg.exe"), Big);
            dummy(Path.Combine(scoopReal, "ffprobe.exe"), Big);
            File.WriteAllText(Path.Combine(scoop, @"shims\ffmpeg.shim"), "path = \"" + Path.Combine(scoopReal, "ffmpeg.exe") + "\"\r\n");
            Check(string.Equals(Ffmpeg.Unshim(Path.Combine(scoop, "shims")), scoopReal, StringComparison.OrdinalIgnoreCase), "scoop shim resolves via ffmpeg.shim");
            Check(Ffmpeg.Unshim(chocoReal) == null, "a real build is left alone");
            _say("  ok   chocolatey / scoop shims resolve to the real ffmpeg");

            const string marker = "testsrc2=s=1280x720:r=30:d=900";
            var cancel = new ManualResetEventSlim();
            var th = new Thread(() => Ffmpeg.Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                new[] { "/d", "/c", Ffmpeg.FfmpegPath, "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", marker,
                        "-c:v", "libx264", "-preset", "slow", "-f", "null", "NUL" }, null, () => cancel.IsSet));
            th.Start();
            var sw = Stopwatch.StartNew();
            while (FfmpegRunningOn(marker) == 0 && sw.ElapsedMilliseconds < 20000) Thread.Sleep(100);
            bool up = FfmpegRunningOn(marker) > 0;
            Thread.Sleep(500);
            cancel.Set();
            th.Join(10000);
            Thread.Sleep(500);
            Check(up, "launcher test: child ffmpeg started");
            int left = FfmpegRunningOn(marker);
            Check(left == 0, "cancel through a launcher kills its child ffmpeg (" + left + " left)");
            _say("  " + (up && left == 0 ? "ok  " : "FAIL") + " cancel through a launcher kills the child ffmpeg");
        }

        static int FfmpegRunningOn(string fileName)
        {
            int n = 0;
            using (var q = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='ffmpeg.exe'"))
                foreach (System.Management.ManagementObject o in q.Get())
                {
                    var cl = o["CommandLine"] as string;
                    if (cl != null && cl.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0) n++;
                }
            return n;
        }

        public static int Snapshot(string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (int s in new[] { 16, 24, 32, 48, 256 })
                using (var b = IconArt.App(s)) b.Save(Path.Combine(dir, "app-" + s + ".png"), ImageFormat.Png);
            var states = new[] { 0.0, 0.12, 0.38, 0.62, 0.87, 1.0, -1 };
            foreach (int s in new[] { 16, 24, 32 })
            {
                int z = 4, gap = 6;
                using (var strip = new Bitmap(states.Length * (s * z + gap) + gap, s * z + 2 * gap))
                using (var g = Graphics.FromImage(strip))
                {
                    g.Clear(Color.FromArgb(0x20, 0x20, 0x20));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    for (int i = 0; i < states.Length; i++)
                        using (var icon = IconArt.Progress(s, states[i], 40))
                            g.DrawImage(icon, new Rectangle(gap + i * (s * z + gap), gap, s * z, s * z));
                    strip.Save(Path.Combine(dir, "tray-" + s + ".png"), ImageFormat.Png);
                }
            }
            return 0;
        }
    }
}
