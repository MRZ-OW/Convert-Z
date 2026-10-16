using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ConvertZ
{
    sealed class JobQueue
    {
        readonly object _gate = new object();
        readonly Queue<Job> _pending = new Queue<Job>();
        bool _closing;
        int _finished;

        public readonly List<Job> Failed = new List<Job>();
        public Job Current { get; private set; }

        public bool TryAdd(IEnumerable<Job> jobs)
        {
            lock (_gate)
            {
                if (_closing) return false;
                foreach (var j in jobs) _pending.Enqueue(j);
                return true;
            }
        }

        public Job Next()
        {
            lock (_gate)
            {
                if (Current != null) _finished++;
                if (_pending.Count == 0) { _closing = true; Current = null; return null; }
                Current = _pending.Dequeue();
                return Current;
            }
        }

        public void Finish(Job j, bool ok)
        {
            lock (_gate) { if (!ok && !j.Cancelled) Failed.Add(j); }
        }

        public int Pending { get { lock (_gate) return _pending.Count; } }

        public void Position(out int index, out int total)
        {
            lock (_gate) { index = _finished + 1; total = _finished + 1 + _pending.Count; }
        }

        public void CancelCurrent()
        {
            var c = Current;
            if (c != null) c.Cancel = true;
        }

        public void CancelAll()
        {
            lock (_gate)
            {
                foreach (var j in _pending) j.Cancelled = true;
                _pending.Clear();
            }
            CancelCurrent();
        }
    }

    static class Ipc
    {
        const string Magic = "CZ1";

        public static string MutexName { get { return @"Local\ConvertZ.Queue"; } }

        public static string PipeName
        {
            get { return "ConvertZ." + Environment.UserName + "." + Process.GetCurrentProcess().SessionId; }
        }

        public static bool TrySend(string targetId, IList<string> paths, int connectTimeoutMs)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    c.Connect(connectTimeoutMs);
                    var msg = Encoding.UTF8.GetBytes(Magic + "\t" + targetId + "\t" + string.Join("\t", paths) + "\n");
                    c.Write(msg, 0, msg.Length);
                    c.Flush();
                    string reply = ReadLine(c, 5000);
                    return reply == "OK";
                }
            }
            catch (Exception ex)
            {
                Log.Write("handoff failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        public static string ReadLine(PipeStream s, int timeoutMs)
        {
            var buf = new List<byte>();
            var one = new byte[1024];
            var sw = Stopwatch.StartNew();
            while (buf.Count < 1 << 20)
            {
                int left = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (left <= 0) return null;
                var ar = s.BeginRead(one, 0, one.Length, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(left)) return null;
                int n = s.EndRead(ar);
                if (n <= 0) return null;
                for (int i = 0; i < n; i++)
                {
                    if (one[i] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray());
                    buf.Add(one[i]);
                }
            }
            return null;
        }

        public static bool TryParse(string line, out string targetId, out List<string> paths)
        {
            targetId = null;
            paths = null;
            if (line == null) return false;
            var parts = line.Split('\t');
            if (parts.Length < 3 || parts[0] != Magic) return false;
            targetId = parts[1];
            paths = parts.Skip(2).Where(p => p.Length > 0).ToList();
            return paths.Count > 0;
        }
    }

    sealed class PipeListener
    {
        readonly JobQueue _queue;
        readonly object _gate = new object();
        NamedPipeServerStream _current;
        Thread _thread;
        volatile bool _stopping;

        public PipeListener(JobQueue queue) { _queue = queue; }

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "ConvertZ pipe" };
            _thread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            lock (_gate) { if (_current != null) _current.Dispose(); }
            if (_thread != null) _thread.Join(2000);
        }

        void Loop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream s;
                try { s = Create(); }
                catch (Exception ex) { Log.Write("pipe create: " + ex.Message); Thread.Sleep(200); continue; }
                lock (_gate)
                {
                    if (_stopping) { s.Dispose(); return; }
                    _current = s;
                }
                try
                {
                    var ar = s.BeginWaitForConnection(null, null);
                    while (!ar.AsyncWaitHandle.WaitOne(250)) if (_stopping) return;
                    s.EndWaitForConnection(ar);
                    Handle(s);
                }
                catch (Exception ex) { if (!_stopping) Log.Write("pipe: " + ex.Message); }
                finally
                {
                    lock (_gate) _current = null;
                    s.Dispose();
                }
            }
        }

        void Handle(NamedPipeServerStream s)
        {
            string line = Ipc.ReadLine(s, 3000);
            string id;
            List<string> paths;
            bool ok = false;
            if (Ipc.TryParse(line, out id, out paths))
            {
                var target = Formats.ById(id);
                if (target != null) ok = _queue.TryAdd(paths.Select(p => new Job(p, target)));
                Log.Write((ok ? "queued " : "refused (closing) ") + paths.Count + " -> " + id);
            }
            var reply = Encoding.UTF8.GetBytes(ok ? "OK\n" : "NO\n");
            s.Write(reply, 0, reply.Length);
            s.Flush();
            try { s.WaitForPipeDrain(); } catch { }
        }

        static NamedPipeServerStream Create()
        {
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(Ipc.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, sec);
        }
    }
}
