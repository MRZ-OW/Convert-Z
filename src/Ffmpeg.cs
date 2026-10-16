using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ConvertZ
{
    static class Ffmpeg
    {
        static string _ffmpeg, _ffprobe;

        public static string FfmpegPath { get { Locate(); return _ffmpeg; } }
        public static string FfprobePath { get { Locate(); return _ffprobe; } }

        static void Locate()
        {
            if (_ffmpeg != null) return;
            foreach (var dir in CandidateDirs())
            {
                try
                {
                    string a = Path.Combine(dir, "ffmpeg.exe"), b = Path.Combine(dir, "ffprobe.exe");
                    if (!File.Exists(a) || !File.Exists(b)) continue;
                    _ffmpeg = a;
                    _ffprobe = b;
                    string real = Unshim(dir);
                    if (real != null) { _ffmpeg = Path.Combine(real, "ffmpeg.exe"); _ffprobe = Path.Combine(real, "ffprobe.exe"); }
                    return;
                }
                catch { }
            }
        }

        internal static string Unshim(string dir)
        {
            string exe = Path.Combine(dir, "ffmpeg.exe");
            if (new FileInfo(exe).Length > 2 * 1024 * 1024 || Directory.GetFiles(dir, "avcodec*.dll").Length > 0) return null;
// scoop: ffmpeg.shim has  path = "...\ffmpeg.exe"
            string shim = Path.Combine(dir, "ffmpeg.shim");
            if (File.Exists(shim))
            {
                foreach (var line in File.ReadAllLines(shim))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0 || line.Substring(0, eq).Trim() != "path") continue;
                    string target = Path.GetDirectoryName(line.Substring(eq + 1).Trim().Trim('"'));
                    if (File.Exists(Path.Combine(target, "ffmpeg.exe")) && File.Exists(Path.Combine(target, "ffprobe.exe"))) return target;
                }
            }
// chocolatey: bin\ffmpeg.exe is a launcher for lib\ffmpeg*\...\ffmpeg.exe
            var bin = new DirectoryInfo(dir);
            if (bin.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && bin.Parent != null)
            {
                string lib = Path.Combine(bin.Parent.FullName, "lib");
                if (Directory.Exists(lib))
                {
                    var hits = Directory.GetDirectories(lib, "*ffmpeg*")
                        .SelectMany(p => Directory.GetFiles(p, "ffmpeg.exe", SearchOption.AllDirectories))
                        .Where(f => File.Exists(Path.Combine(Path.GetDirectoryName(f), "ffprobe.exe")) && new FileInfo(f).Length > 2 * 1024 * 1024)
                        .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                        .ToList();
                    if (hits.Count > 0) return Path.GetDirectoryName(hits[0]);
                }
            }
            return null;
        }

        static IEnumerable<string> CandidateDirs()
        {
            yield return AppDomain.CurrentDomain.BaseDirectory;
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                string path = null;
                try { path = Environment.GetEnvironmentVariable("PATH", scope); } catch { }
                if (string.IsNullOrEmpty(path)) continue;
                foreach (var raw in path.Split(';'))
                {
                    var d = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                    if (d.Length > 0 && seen.Add(d)) yield return d;
                }
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, @"Microsoft\WinGet\Links");
            string pkgs = Path.Combine(local, @"Microsoft\WinGet\Packages");
            if (Directory.Exists(pkgs))
            {
                var bins = new List<string>();
                try
                {
                    foreach (var pkg in Directory.GetDirectories(pkgs, "*FFmpeg*"))
                        foreach (var sub in Directory.GetDirectories(pkg))
                            if (File.Exists(Path.Combine(sub, @"bin\ffmpeg.exe"))) bins.Add(Path.Combine(sub, "bin"));
                }
                catch { }
                foreach (var b in bins.OrderByDescending(b => File.GetLastWriteTimeUtc(Path.Combine(b, "ffmpeg.exe")))) yield return b;
            }
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(pf, @"ffmpeg\bin");
            yield return @"C:\ffmpeg\bin";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"scoop\shims");
            yield return @"C:\ProgramData\chocolatey\bin";
        }

        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string JoinArgs(IEnumerable<string> args) { return string.Join(" ", args.Select(Quote)); }

        public sealed class RunResult
        {
            public int ExitCode;
            public bool Cancelled;
            public List<string> Stderr = new List<string>();

            static readonly string[] Boilerplate =
            {
                "Terminating thread with return code", "Task finished with error code", "Nothing was written into output file",
                "Error sending frames to consumers", "Could not open encoder before EOF", "Conversion failed!",
                "Error while filtering", "Error while processing the decoded data",
            };

            public string Tail(int lines)
            {
                List<string> all;
                lock (Stderr) all = Stderr.Where(l => l.Trim().Length > 0 && !l.StartsWith("Svt[")).ToList();
                var useful = all.Where(l => !Boilerplate.Any(b => l.Contains(b))).ToList();
                if (useful.Count == 0) useful = all;
                return string.Join("\n", useful.Skip(Math.Max(0, useful.Count - lines))
                    .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @" @ [0-9a-fA-F]{8,16}\]", "]")));
            }
        }

        public static RunResult Run(string exe, IList<string> args, Action<string> onStdout, Func<bool> cancel)
        {
            var result = new RunResult();
            var psi = new ProcessStartInfo(exe, JoinArgs(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath(),
            };
            using (var p = new Process { StartInfo = psi })
            {
                var stdoutDone = new ManualResetEventSlim();
                var stderrDone = new ManualResetEventSlim();
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) { stdoutDone.Set(); return; }
                    if (onStdout != null) onStdout(e.Data);
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) { stderrDone.Set(); return; }
                    lock (result.Stderr)
                    {
                        result.Stderr.Add(e.Data);
                        if (result.Stderr.Count > 400) result.Stderr.RemoveRange(0, 200);
                    }
                };
                p.Start();
                Job.Add(p);
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(100))
                {
                    if (cancel != null && cancel())
                    {
                        result.Cancelled = true;
                        KillTree(p.Id);  // kill children too (chocolatey/scoop shims)
                        p.WaitForExit(5000);
                        break;
                    }
                }
                if (!result.Cancelled) p.WaitForExit();
                stdoutDone.Wait(2000);
                stderrDone.Wait(2000);
                result.ExitCode = p.HasExited ? p.ExitCode : -1;
            }
            return result;
        }

        static void KillTree(int pid)
        {
            var children = new List<int>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap != (IntPtr)(-1))
            {
                try
                {
                    var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                    for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                        if (e.th32ParentProcessID == (uint)pid && e.th32ProcessID != (uint)pid) children.Add((int)e.th32ProcessID);
                }
                finally { CloseHandle(snap); }
            }
            foreach (var c in children) KillTree(c);
            try { using (var p = Process.GetProcessById(pid)) p.Kill(); } catch { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static class Job
        {
            static IntPtr _handle;

            public static void Add(Process p)
            {
                try
                {
                    if (_handle == IntPtr.Zero)
                    {
                        var h = CreateJobObject(IntPtr.Zero, null);
                        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                        info.BasicLimitInformation.LimitFlags = 0x2000;  // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        int len = Marshal.SizeOf(info);
                        IntPtr buf = Marshal.AllocHGlobal(len);
                        try
                        {
                            Marshal.StructureToPtr(info, buf, false);
                            if (!SetInformationJobObject(h, 9 /* ExtendedLimitInformation */, buf, (uint)len))
                                throw new Win32Exception();
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                        _handle = h;
                    }
                    AssignProcessToJobObject(_handle, p.Handle);
                }
                catch (Exception ex) { Log.Write("job object: " + ex.Message); }
            }

            [StructLayout(LayoutKind.Sequential)]
            struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass, SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

            [StructLayout(LayoutKind.Sequential)]
            struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern IntPtr CreateJobObject(IntPtr attrs, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        }
    }
}
