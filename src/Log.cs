using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ConvertZ
{
    static class Log
    {
        static readonly object Gate = new object();
        static readonly int Pid = Process.GetCurrentProcess().Id;

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertZ"); }
        }

        public static string FilePath { get { return Path.Combine(Dir, "ConvertZ.log"); } }

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + Pid + "] " + message + Environment.NewLine;
            lock (Gate)
            {
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        Directory.CreateDirectory(Dir);
                        using (var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        {
                            var bytes = Encoding.UTF8.GetBytes(line);
                            fs.Write(bytes, 0, bytes.Length);
                        }
                        Trim();
                        return;
                    }
                    catch (IOException) { Thread.Sleep(20); }
                    catch { return; }
                }
            }
        }

        static void Trim()
        {
            var fi = new FileInfo(FilePath);
            if (fi.Length < 2 * 1024 * 1024) return;
            try
            {
                string text = File.ReadAllText(FilePath, Encoding.UTF8);
                File.WriteAllText(FilePath, text.Substring(text.Length / 2), Encoding.UTF8);
            }
            catch { }
        }
    }
}
