using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ConvertZ
{
    sealed class TrayUi : ApplicationContext
    {
        readonly JobQueue _queue;
        readonly NotifyIcon _icon;
        readonly ContextMenuStrip _menu;
        readonly System.Windows.Forms.Timer _timer;
        readonly Control _marshal;
        IntPtr _hicon;
        int _lastBucket = int.MinValue;
        string _lastText;
        float _spin;
        bool _closed;

        public volatile float Progress = -1;

        public TrayUi(JobQueue queue)
        {
            _queue = queue;
            _marshal = new Control();
            _marshal.CreateControl();

            _menu = new ContextMenuStrip { ShowImageMargin = false };
            _menu.Opening += (s, e) => BuildMenu();

            _icon = new NotifyIcon { ContextMenuStrip = _menu, Text = "ConvertZ" };
            Render();
            _icon.Visible = true;
            _icon.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var m = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (m != null) m.Invoke(_icon, null);
            };

            _timer = new System.Windows.Forms.Timer { Interval = 150 };
            _timer.Tick += (s, e) => Render();
            _timer.Start();

            var exe = Application.ExecutablePath;
            ThreadPool.QueueUserWorkItem(_ => TrayPromotion.Promote(exe));
            var where = new System.Windows.Forms.Timer { Interval = 1000 };
            where.Tick += (s, e) => { where.Stop(); where.Dispose(); LogPlacement(); };
            where.Start();
        }

        void LogPlacement()
        {
            try
            {
                var f = typeof(NotifyIcon).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic);
                var idf = typeof(NotifyIcon).GetField("id", BindingFlags.Instance | BindingFlags.NonPublic);
                var win = f == null ? null : f.GetValue(_icon) as NativeWindow;
                if (win == null || idf == null) return;
                var nid = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)), hWnd = win.Handle, uID = (uint)(int)idf.GetValue(_icon) };
                RECT r;
                int hr = Shell_NotifyIconGetRect(ref nid, out r);
// in the overflow this returns the ^ button's rect
                Log.Write("tray icon rect " + (hr != 0 ? "unavailable (hr 0x" + hr.ToString("X8") + ")" :
                    r.Left + "," + r.Top + "-" + r.Right + "," + r.Bottom) + " (the ^ button's rect if it sits in the overflow)");
            }
            catch (Exception ex) { Log.Write("tray placement: " + ex.Message); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

        public void Close()
        {
            try { _marshal.BeginInvoke(new Action(ExitThread)); } catch { }
        }

        protected override void ExitThreadCore()
        {
            Hide();
            base.ExitThreadCore();
        }

        public void Hide()
        {
            if (_closed) return;
            _closed = true;
            try { _timer.Stop(); } catch { }
            try { _icon.Visible = false; _icon.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }
            if (_hicon != IntPtr.Zero) { DestroyIcon(_hicon); _hicon = IntPtr.Zero; }
        }

        void Render()
        {
            if (_closed) return;
            float p = Progress;
            int bucket = p < 0 ? -1 : (int)(p * 100);
            if (p < 0) _spin = (_spin + 30) % 360;
            if (bucket != _lastBucket || p < 0)
            {
                _lastBucket = bucket;
                int size = SystemInformation.SmallIconSize.Width;
                using (var bmp = IconArt.Progress(size, p, _spin))
                {
                    IntPtr h = bmp.GetHicon();
                    var old = _hicon;
                    _icon.Icon = Icon.FromHandle(h);
                    _hicon = h;
                    if (old != IntPtr.Zero) DestroyIcon(old);  // free the previous frame
                }
            }
            string text = Tooltip();
            if (text != _lastText) { _icon.Text = text; _lastText = text; }
        }

        string Tooltip()
        {
            var job = _queue.Current;
            if (job == null) return "ConvertZ";
            int index, total;
            _queue.Position(out index, out total);
            string head = "ConvertZ";
            if (total > 1) head += " " + index + "/" + total;
            float p = Progress;
            if (p >= 0) head += " " + (int)(p * 100) + "%";
            return Fit(head + " · ", job.Name, " → " + job.Target.Label.Split(' ')[0]);
        }

        public static string Fit(string head, string name, string tail)
        {
            const int max = 63;
            int room = max - head.Length - tail.Length;
            if (room < 4) return (head + tail).Length <= max ? head + tail : (head + tail).Substring(0, max);
            if (name.Length > room) name = name.Substring(0, room - 1) + "…";
            return head + name + tail;
        }

        void BuildMenu()
        {
            _menu.Items.Clear();
            var job = _queue.Current;
            if (job != null)
            {
                float p = Progress;
                var title = new ToolStripMenuItem(job.Name + "  →  " + job.Target.Label + (p >= 0 ? "   " + (int)(p * 100) + "%" : ""))
                {
                    Enabled = false,
                };
                title.Font = new Font(title.Font, FontStyle.Bold);
                _menu.Items.Add(title);
            }
            int pending = _queue.Pending;
            if (pending > 0) _menu.Items.Add(new ToolStripMenuItem(pending + " more queued") { Enabled = false });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("Cancel", null, (s, e) => _queue.CancelCurrent()));
            if (pending > 0) _menu.Items.Add(new ToolStripMenuItem("Cancel all", null, (s, e) => _queue.CancelAll()));
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);
    }

    static class TrayPromotion
    {
        public static void Promote(string exePath)
        {
            foreach (int delay in new[] { 300, 1200, 3000 })
            {
                Thread.Sleep(delay);
                if (TryPromote(exePath)) return;
            }
        }

        public static bool TryPromote(string exePath)
        {
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return true;  // windows 10
                    foreach (var name in root.GetSubKeyNames())
                    {
                        using (var k = root.OpenSubKey(name, true))
                        {
                            if (k == null) continue;
                            var path = k.GetValue("ExecutablePath") as string;
                            if (path == null || !string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                            if (k.GetValue("IsPromoted") == null)
                            {
                                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                Log.Write("tray icon promoted out of the overflow (" + name + ")");
                            }
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("tray promote: " + ex.Message); return true; }
            return false;
        }
    }
}
