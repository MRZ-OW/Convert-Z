using System;
using System.IO;
using System.Linq;

namespace ConvertZ
{
    static class GpuCheck
    {
        public static int Run(Action<string> say)
        {
            string ff = Ffmpeg.FfmpegPath;
            if (ff == null) { say("ffmpeg: NOT FOUND (winget install Gyan.FFmpeg)"); return 2; }
            say("ffmpeg: " + ff);
            var vlines = new System.Collections.Generic.List<string>();
            Ffmpeg.Run(ff, new[] { "-hide_banner", "-version" }, l => vlines.Add(l), null);
            if (vlines.Count > 0) say("        " + vlines[0]);
            if (!Driver(say)) return 4;

            string dir = Path.Combine(Path.GetTempPath(), "ConvertZ-gpucheck-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                string src = Path.Combine(dir, "src.mp4");
                var gen = Ffmpeg.Run(ff, new[]
                {
                    "-hide_banner", "-nostdin", "-y", "-v", "error",
                    "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=2",
                    "-f", "lavfi", "-i", "sine=f=440:d=2",
                    "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", src,
                }, null, null);
                if (gen.ExitCode != 0) { say("could not make the test clip: " + gen.Tail(2)); return 2; }
                string perr;
                var probe = ProbeInfo.Run(src, out perr);
                if (probe == null) { say("ffprobe failed: " + perr); return 2; }

                var checks = new[]
                {
                    Tuple.Create("H.264 on GPU, decode+encode (MP4/MOV/MKV)", "mp4", VideoPath.GpuFull),
                    Tuple.Create("H.264 on GPU, decode on GPU (fallback)   ", "mp4", VideoPath.GpuDecode),
                    Tuple.Create("AV1 on GPU (WebM)                         ", "webm", VideoPath.GpuFull),
                    Tuple.Create("GPU decode only (GIF / AVI)               ", "avi", VideoPath.GpuDecode),
                };
                bool all = true;
                foreach (var c in checks)
                {
                    var target = Formats.ById(c.Item2);
                    string outp = Path.Combine(dir, "out-" + c.Item3 + target.Ext);
                    var plan = Planner.Build(probe, target, src, outp, new PlanOptions { AllowCopy = false, Path = c.Item3 });
                    var r = Ffmpeg.Run(ff, plan.Args, null, null);
                    bool ok = r.ExitCode == 0 && File.Exists(outp) && new FileInfo(outp).Length > 0;
                    all &= ok;
                    say((ok ? "  OK      " : "  FAILED  ") + c.Item1 + (ok ? "" : "\n            " + r.Tail(2).Replace("\n", "\n            ")));
                    Log.Write("gpucheck " + c.Item1.Trim() + ": " + (ok ? "ok" : "failed: " + r.Tail(2).Replace("\n", " | ")));
                }
                if (!all)
                    say("\nFailed GPU paths fall back to the CPU automatically. If the message mentions the driver /\n" +
                        "nvenc API version, update the NVIDIA driver and run install.ps1 again.");
                return all ? 0 : 3;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static bool Driver(Action<string> say)
        {
            string smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (!File.Exists(smi)) { say("GPU:    no NVIDIA driver found"); return false; }
            var lines = new System.Collections.Generic.List<string>();
            var r = Ffmpeg.Run(smi, new[] { "--query-gpu=name,driver_version", "--format=csv,noheader" }, l => lines.Add(l), null);
            lines = lines.Where(l => l.Trim().Length > 0).ToList();
            say("GPU:    " + (lines.Count > 0 ? string.Join("; ", lines) : "nvidia-smi exit " + r.ExitCode));
            return r.ExitCode == 0 && lines.Count > 0;
        }
    }
}
