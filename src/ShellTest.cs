using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ConvertZ
{
    static class ShellTest
    {
        public static int Entry(string[] args)
        {
            if (args.Length < 2) return 2;
            string report = args[0];
            var files = new List<string>();
            string invoke = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--invoke" && i + 1 < args.Length) invoke = args[++i];
                else files.Add(Path.GetFullPath(args[i]));
            }
            using (var w = new StreamWriter(report, false, new UTF8Encoding(false)) { AutoFlush = true })
            {
                try { return Run(w.WriteLine, files, invoke); }
                catch (Exception ex) { w.WriteLine("ERROR " + ex); return 1; }
            }
        }

        static int Run(Action<string> say, List<string> files, string invokeId)
        {
            var pidls = new List<IntPtr>();
            var children = new List<IntPtr>();
            IShellFolder folder = null;
            foreach (var f in files)
            {
                IntPtr pidl;
                uint attrs;
                Hr(SHParseDisplayName(f, IntPtr.Zero, out pidl, 0, out attrs), "SHParseDisplayName " + f);
                pidls.Add(pidl);
                Guid iidFolder = typeof(IShellFolder).GUID;
                IntPtr ppv, child;
                Hr(SHBindToParent(pidl, ref iidFolder, out ppv, out child), "SHBindToParent");
                if (folder == null) folder = (IShellFolder)Marshal.GetObjectForIUnknown(ppv);
                Marshal.Release(ppv);
                children.Add(child);
            }

            Guid iidCm = typeof(IContextMenu).GUID;
            IntPtr pcm;
            Hr(folder.GetUIObjectOf(IntPtr.Zero, (uint)children.Count, children.ToArray(), ref iidCm, IntPtr.Zero, out pcm), "GetUIObjectOf");
            var cm = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);
            var cm3 = Marshal.GetObjectForIUnknown(pcm) as IContextMenu3;
            Marshal.Release(pcm);

            const uint First = 1;
            IntPtr menu = CreatePopupMenu();
            Hr(cm.QueryContextMenu(menu, 0, First, 0x7FFF, 0 /* CMF_NORMAL */), "QueryContextMenu");

            say("selection: " + string.Join(", ", files.Select(Path.GetFileName)));
            IntPtr convert = IntPtr.Zero;
            int convertPos = -1;
            int count = GetMenuItemCount(menu);
            for (int i = 0; i < count; i++)
            {
                string text = Text(menu, i);
                IntPtr sub = GetSubMenu(menu, i);
                say("  " + (IsSeparator(menu, i) ? "----" : text) + (sub != IntPtr.Zero ? "  >" : ""));
                if (text == "Convert to" && sub != IntPtr.Zero) { convert = sub; convertPos = i; }
            }
            if (convert == IntPtr.Zero) { say("RESULT: no \"Convert to\" submenu"); return 1; }

// the submenu is filled on WM_INITMENUPOPUP, like when you hover it
            if (cm3 != null)
            {
                IntPtr res;
                cm3.HandleMenuMsg2(0x0117 /* WM_INITMENUPOPUP */, convert, (IntPtr)convertPos, out res);
            }
            int n = GetMenuItemCount(convert);
            say("Convert to:");
            var entries = new List<KeyValuePair<string, uint>>();
            for (int i = 0; i < n; i++)
            {
                if (IsSeparator(convert, i)) { say("    ----"); continue; }
                string text = Text(convert, i);
                uint id = GetMenuItemID(convert, i);
                entries.Add(new KeyValuePair<string, uint>(text, id));
                say("    " + text);
            }

            var expect = Formats.EntriesFor(Path.GetExtension(files[0])).Select(e => e.Key.Label).ToList();
            bool same = expect.SequenceEqual(entries.Select(e => e.Key));
            say("RESULT: " + (same ? "menu matches (" + entries.Count + " entries)" : "MISMATCH, expected " + string.Join(", ", expect)));
            int code = same ? 0 : 1;

            if (invokeId != null)
            {
                var target = Formats.ById(invokeId);
                var hit = entries.FirstOrDefault(e => target != null && e.Key == target.Label);
                if (hit.Key == null) { say("INVOKE: no entry for " + invokeId); return 1; }
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX)),
                    fMask = 0x4000 /* UNICODE */ | 0x100 /* NOASYNC */ | 0x400 /* FLAG_NO_UI */,
                    lpVerb = (IntPtr)(hit.Value - First),
                    lpVerbW = (IntPtr)(hit.Value - First),
                    nShow = 1,
                };
                int hr = cm.InvokeCommand(ref info);
                say("INVOKE " + hit.Key + ": hr=0x" + hr.ToString("X8"));
                if (hr != 0) code = 1;
            }

            DestroyMenu(menu);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
            return code;
        }

        static string Text(IntPtr menu, int pos)
        {
            var sb = new StringBuilder(512);
            GetMenuString(menu, (uint)pos, sb, sb.Capacity, 0x400 /* MF_BYPOSITION */);
            return sb.ToString().Replace("&", "");
        }

        static bool IsSeparator(IntPtr menu, int pos)
        {
            return (GetMenuState(menu, (uint)pos, 0x400) & 0x800 /* MF_SEPARATOR */) != 0;
        }

        static void Hr(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed", hr);
        }

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellFolder
        {
            [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eaten, out IntPtr pidl, IntPtr attrs);
            [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
            [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint inOut);
            [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
            [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
            [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
        }

        [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, StringBuilder name, uint cch);
        }

        [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu3
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, StringBuilder name, uint cch);
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
            [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            [MarshalAs(UnmanagedType.LPStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPStr)] public string lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.LPStr)] public string lpTitle;
            public IntPtr lpVerbW;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParametersW;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectoryW;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpTitleW;
            public int ptX, ptY;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr pidlLast);

        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
        [DllImport("user32.dll")] static extern IntPtr GetSubMenu(IntPtr menu, int pos);
        [DllImport("user32.dll")] static extern uint GetMenuItemID(IntPtr menu, int pos);
        [DllImport("user32.dll")] static extern uint GetMenuState(IntPtr menu, uint id, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuString(IntPtr menu, uint id, StringBuilder sb, int max, uint flags);
    }
}
