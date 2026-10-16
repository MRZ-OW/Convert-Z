using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ConvertZ
{
    sealed class Job
    {
        public readonly string Input;
        public readonly Target Target;
        public volatile bool Cancel;

        public string Output;
        public string Error;
        public bool Cancelled;
        public string Summary;

        public Job(string input, Target target) { Input = input; Target = target; }

        public string Name { get { return Path.GetFileName(Input); } }
    }

    static class Converter
    {
        public static volatile bool NvencBroken, CudaBroken;

        public static bool Run(Job job, Action<double> progress)
        {
            if (progress == null) progress = _ => { };
            if (!File.Exists(job.Input)) { job.Error = "File not found."; return false; }
            if (Ffmpeg.FfmpegPath == null)
            {
                job.Error = "ffmpeg was not found. Install it with:  winget install Gyan.FFmpeg";
                return false;
            }

            string perr;
            var probe = ProbeInfo.Run(job.Input, out perr);
            if (probe == null)
            {
                string why = (perr ?? "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
                int cut = why.IndexOf(job.Input + ":", StringComparison.OrdinalIgnoreCase);
                if (cut >= 0) why = why.Substring(cut + job.Input.Length + 1).Trim();
                job.Error = "ffmpeg can't read this file - it may be damaged, or not really a " +
                            Path.GetExtension(job.Input).TrimStart('.').ToUpperInvariant() + " file." + (why.Length > 0 ? "\n(" + why + ")" : "");
                return false;
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(job.Input));
            string stem = Path.GetFileNameWithoutExtension(job.Input);
            string ext = job.Target.Ext;
            double duration = probe.Duration;

            var opts = new PlanOptions { AllowCopy = true, Path = StartPath(job.Target) };
            for (int attempt = 0; attempt < 6; attempt++)
            {
                string final = UniquePath(dir, stem, ext);
                string temp = TempPathFor(final);
                var plan = Planner.Build(probe, job.Target, job.Input, temp, opts);
                if (plan.Error != null) { job.Error = plan.Error; return false; }

                Log.Write("run " + job.Target.Id + " <- " + job.Input + " :: " + plan.Summary);
                Log.Write("  ffmpeg " + Ffmpeg.JoinArgs(plan.Args));
                progress(double.IsNaN(duration) ? -1 : 0);

                var r = Ffmpeg.Run(Ffmpeg.FfmpegPath, plan.Args, line => OnProgressLine(line, duration, progress), () => job.Cancel);
                if (r.Cancelled)
                {
                    TryDelete(temp);
                    job.Cancelled = true;
                    Log.Write("  cancelled");
                    return false;
                }
                if (r.ExitCode == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0)
                {
                    final = UniquePath(dir, stem, ext);
                    File.Move(temp, final);
                    job.Output = final;
                    job.Summary = plan.Summary;
                    progress(1);
                    Log.Write("  ok -> " + final);
                    return true;
                }

                TryDelete(temp);
                string tail = r.Tail(4);
                Log.Write("  failed (exit " + r.ExitCode + "): " + tail.Replace("\n", " | "));
                NoteGpuFailure(tail);

                if (plan.UsedCopy) { opts.AllowCopy = false; continue; }
                if (plan.Path == VideoPath.GpuFull) { opts.Path = (NvencBroken || CudaBroken) ? VideoPath.Cpu : VideoPath.GpuDecode; continue; }
                if (plan.Path == VideoPath.GpuDecode) { opts.Path = VideoPath.Cpu; continue; }
                job.Error = Friendly(tail, r.ExitCode);
                return false;
            }
            job.Error = "Gave up after several attempts.";
            return false;
        }

        static VideoPath StartPath(Target t)
        {
            if (CudaBroken) return VideoPath.Cpu;
            if (NvencBroken && Planner.EncodesWithNvenc(t)) return VideoPath.Cpu;
            return VideoPath.GpuFull;
        }

        static void NoteGpuFailure(string tail)
        {
            string s = tail.ToLowerInvariant();
            if (s.Contains("nvenc") && (s.Contains("driver") || s.Contains("no capable devices") || s.Contains("cannot load")
                || s.Contains("openencodesessionex") || s.Contains("api version")))
                NvencBroken = true;
            if (s.Contains("cannot load nvcuda") || s.Contains("device creation failed") || s.Contains("no device available"))
                CudaBroken = true;
        }

        static void OnProgressLine(string line, double duration, Action<double> progress)
        {
            if (line.StartsWith("progress=end")) { progress(0.999); return; }
            if (double.IsNaN(duration)) return;
// older builds call it out_time_ms, still microseconds
            if (!line.StartsWith("out_time_us=") && !line.StartsWith("out_time_ms=")) return;
            long us;
            if (!long.TryParse(line.Substring(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out us) || us < 0) return;
            double f = us / 1e6 / duration;
            progress(Math.Max(0, Math.Min(0.99, f)));
        }

        static string Friendly(string tail, int exit)
        {
            string s = tail.ToLowerInvariant();
            if (s.Contains("permission denied")) return "Can't write in this folder (permission denied).";
            if (s.Contains("no space left")) return "The disk is full.";
            if (tail.Trim().Length == 0) return "ffmpeg failed (exit code " + exit + ").";
            return tail.Split('\n').Last().Trim();
        }

        public static string UniquePath(string dir, string stem, string ext)
        {
            string p = Path.Combine(dir, stem + ext);
            for (int n = 1; File.Exists(p) || Directory.Exists(p); n++)
                p = Path.Combine(dir, stem + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + ext);
            return p;
        }

        public static string TempPathFor(string final)
        {
            return Path.Combine(Path.GetDirectoryName(final), Path.GetFileNameWithoutExtension(final) + ".converting" + Path.GetExtension(final));
        }

        static void TryDelete(string path)
        {
            for (int i = 0; i < 10; i++)
            {
                try { if (File.Exists(path)) File.Delete(path); return; }
                catch { System.Threading.Thread.Sleep(100); }
            }
        }
    }
}
