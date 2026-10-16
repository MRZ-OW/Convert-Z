using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ConvertZ
{
    static class ShellMenu
    {
        public const string Verb = "ConvertZ";
        const string Sfa = @"Software\Classes\SystemFileAssociations";
        const int SeparatorBefore = 0x20;  // ECF_SEPARATORBEFORE

        public static int Register(string exe)
        {
            Unregister(false);
            int keys = 0;
            foreach (var ext in Formats.AllSourceExtensions())
            {
                var entries = Formats.EntriesFor(ext);
                if (entries.Count == 0) continue;
                using (var k = Registry.CurrentUser.CreateSubKey(Sfa + "\\" + ext + @"\shell\" + Verb))
                {
                    k.SetValue("MUIVerb", "Convert to");
                    k.SetValue("Icon", "\"" + exe + "\",0");
                    k.SetValue("SubCommands", "");
// Player keeps the verb visible with more than 15 files selected
                    k.SetValue("MultiSelectModel", "Player");
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var t = entries[i].Key;
// subkeys are listed in name order
                        string name = (i + 1).ToString("00", CultureInfo.InvariantCulture) + t.Id;
                        using (var s = k.CreateSubKey(@"shell\" + name))
                        {
                            s.SetValue("MUIVerb", t.Label);
                            s.SetValue("MultiSelectModel", "Player");
                            if (entries[i].Value) s.SetValue("CommandFlags", SeparatorBefore, RegistryValueKind.DWord);
                            using (var c = s.CreateSubKey("command"))
                                c.SetValue("", CommandLine(exe, t.Id));
                        }
                    }
                }
                keys++;
            }
            Notify();
            Log.Write("registered the Convert to menu on " + keys + " extensions -> " + exe);
            return keys;
        }

        public static string CommandLine(string exe, string targetId)
        {
            return "\"" + exe + "\" --to " + targetId + " \"%1\"";
        }

        public static int Unregister(bool notify = true)
        {
            int removed = 0;
            using (var sfa = Registry.CurrentUser.OpenSubKey(Sfa, true))
            {
                if (sfa == null) return 0;
                foreach (var ext in sfa.GetSubKeyNames())
                {
                    using (var shell = sfa.OpenSubKey(ext + @"\shell", true))
                    {
                        if (shell == null) continue;
                        using (var mine = shell.OpenSubKey(Verb)) if (mine == null) continue;
                        shell.DeleteSubKeyTree(Verb, false);
                        removed++;
                    }
                    DeleteIfEmpty(sfa, ext + @"\shell");
                    DeleteIfEmpty(sfa, ext);
                }
            }
            if (notify) { Notify(); Log.Write("unregistered the Convert to menu from " + removed + " extensions"); }
            return removed;
        }

        static void DeleteIfEmpty(RegistryKey parent, string sub)
        {
            try
            {
                using (var k = parent.OpenSubKey(sub))
                {
                    if (k == null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
                }
                parent.DeleteSubKey(sub, false);
            }
            catch { }
        }

        public static Dictionary<string, List<string[]>> Read()
        {
            var map = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
            using (var sfa = Registry.CurrentUser.OpenSubKey(Sfa))
            {
                if (sfa == null) return map;
                foreach (var ext in sfa.GetSubKeyNames())
                {
                    using (var shell = sfa.OpenSubKey(ext + @"\shell\" + Verb + @"\shell"))
                    {
                        if (shell == null) continue;
                        var list = new List<string[]>();
                        foreach (var name in shell.GetSubKeyNames())
                        {
                            using (var s = shell.OpenSubKey(name))
                            using (var c = shell.OpenSubKey(name + @"\command"))
                            {
                                list.Add(new[]
                                {
                                    name, s.GetValue("MUIVerb") as string ?? "",
                                    Convert.ToString(s.GetValue("CommandFlags") ?? 0, CultureInfo.InvariantCulture),
                                    c == null ? "" : c.GetValue("") as string ?? "",
                                });
                            }
                        }
                        map[ext] = list;
                    }
                }
            }
            return map;
        }

        static void Notify()
        {
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
