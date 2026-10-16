using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ConvertZ
{
    static class Program
    {
        static TrayUi _ui;

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);

            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            try
            {
                switch (mode)
                {
                    case "--to": return Convert(args);
                    case "--register": ShellMenu.Register(Application.ExecutablePath); return 0;
                    case "--unregister": ShellMenu.Unregister(); return 0;
                    case "--prime": return Prime();
                    case "--gpucheck": return Report(args, 1, GpuCheck.Run);
                    case "--selftest": return Report(args, 1, SelfTest.Run);
                    case "--shelltest": return ShellTest.Entry(args.Skip(1).ToArray());
                    case "--snapshot": return SelfTest.Snapshot(args.Length > 1 ? args[1] : "snapshots");
                    default:
                        MessageBox.Show("ConvertZ adds \"Convert to\" to the right-click menu of video, audio and image files.\n\n" +
                                        "Install it with install.ps1, then right-click a file.", "ConvertZ",
                                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Crash(ex);
                return 1;
            }
        }

        static int Report(string[] args, int at, Func<Action<string>, int> run)
        {
            string path = args.Length > at ? args[at] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, args[0].TrimStart('-') + ".log");
            var sb = new StringBuilder();
            int code;
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true })
            {
                code = run(line =>
                {
                    w.WriteLine(line);
                    lock (sb) sb.AppendLine(line);
                });
            }
            return code;
        }

        static int Convert(string[] args)
        {
            var target = args.Length > 1 ? Formats.ById(args[1]) : null;
            var files = args.Skip(2).Where(a => a.Length > 0).Select(a => Path.GetFullPath(a)).ToList();
            if (target == null || files.Count == 0)
            {
                MessageBox.Show("Usage: ConvertZ.exe --to <format> <file>", "ConvertZ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }

            using (var mutex = new Mutex(false, Ipc.MutexName))
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(30))
                {
                    bool owned;
                    try { owned = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }  // previous owner crashed
                    if (owned)
                    {
                        List<Job> failed;
                        try { failed = Own(files.Select(f => new Job(f, target)).ToList()); }
                        finally { mutex.ReleaseMutex(); }  // release before any dialog so new clicks get a fresh owner
                        if (failed.Count > 0) ShowFailures(failed);
                        return failed.Count > 0 ? 1 : 0;
                    }
                    if (Ipc.TrySend(target.Id, files, 2000)) return 0;
                    Thread.Sleep(50);
                }
            }
            Log.Write("gave up handing off " + string.Join(", ", files));
            MessageBox.Show("ConvertZ couldn't start the conversion (another ConvertZ is stuck?).\n\nLog: " + Log.FilePath,
                "ConvertZ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        static List<Job> Own(List<Job> first)
        {
            Log.Write("owner start: " + first.Count + " job(s), ffmpeg " + (Ffmpeg.FfmpegPath ?? "NOT FOUND"));
            var queue = new JobQueue();
            queue.TryAdd(first);
            var listener = new PipeListener(queue);
            listener.Start();

            _ui = new TrayUi(queue);
            var ui = _ui;
            var worker = new Thread(() =>
            {
                try
                {
                    Job j;
                    while ((j = queue.Next()) != null)
                    {
                        if (j.Cancelled) continue;
                        ui.Progress = -1;
                        bool ok;
                        try { ok = Converter.Run(j, p => ui.Progress = (float)p); }
                        catch (Exception ex) { ok = false; j.Error = ex.Message; Log.Write("job crashed: " + ex); }
                        if (!ok && !j.Cancelled) Log.Write("FAILED " + j.Input + " -> " + j.Target.Id + ": " + j.Error);
                        queue.Finish(j, ok);
                    }
                }
                finally { ui.Close(); }
            }) { IsBackground = true, Name = "ConvertZ worker" };
            worker.Start();

            Application.Run(ui);
            ui.Hide();
            listener.Stop();
            worker.Join(2000);
            Log.Write("owner exit: " + queue.Failed.Count + " failed");
            return queue.Failed;
        }

        static void ShowFailures(List<Job> failed)
        {
            var sb = new StringBuilder();
            sb.AppendLine(failed.Count == 1 ? "This file couldn't be converted:" : failed.Count + " files couldn't be converted:");
            sb.AppendLine();
            foreach (var j in failed.Take(6))
            {
                sb.AppendLine(j.Name + "  →  " + j.Target.Label);
                sb.AppendLine("    " + (j.Error ?? "unknown error").Replace("\n", "\n    "));
                sb.AppendLine();
            }
            if (failed.Count > 6) sb.AppendLine("... and " + (failed.Count - 6) + " more.").AppendLine();
            sb.Append("Details: " + Log.FilePath);
            TopMostMessage(sb.ToString(), MessageBoxIcon.Warning);
        }

        static void TopMostMessage(string text, MessageBoxIcon icon)
        {
            using (var owner = new Form
            {
                TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000), Size = new System.Drawing.Size(1, 1),
            })
            {
                owner.Show();
                MessageBox.Show(owner, text, "ConvertZ", MessageBoxButtons.OK, icon);
            }
        }

        static void Crash(Exception ex)
        {
            try { Log.Write("CRASH: " + ex); } catch { }
            try { if (_ui != null) _ui.Hide(); } catch { }
        }

        static int Prime()
        {
            var queue = new JobQueue();
            var ui = new TrayUi(queue) { Progress = 0.62f };
            _ui = ui;
            var t = new System.Windows.Forms.Timer { Interval = 4500 };
            t.Tick += (s, e) => { t.Stop(); ui.Close(); };
            t.Start();
            Application.Run(ui);
            ui.Hide();
            bool ok = TrayPromotion.TryPromote(Application.ExecutablePath);
            return ok ? 0 : 3;
        }
    }
}
