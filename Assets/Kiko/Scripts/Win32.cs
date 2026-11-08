// Windows 窗口/屏幕工具：窗口位置、工作区（不含任务栏）、全局鼠标位置。
// 坐标一律是「屏幕物理像素、左上角为原点、y 向下」，和 Tauri 版 pet.js 的逻辑一致（不过那边是逻辑像素）。
// UniWindowController 只负责透明和鼠标穿透；移动窗口、找任务栏都走这里，免得两套坐标系混用。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Kiko
{
    /// 桌面上一个普通窗口（小只模式里她可以站在它的上沿）
    public struct WinInfo { public IntPtr h; public RectInt r; public string title; }

    public static class Win32
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

        const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        const uint MONITOR_DEFAULTTONEAREST = 2;

        static IntPtr _hwnd;
        public static IntPtr Hwnd
        {
            get
            {
                if (_hwnd != IntPtr.Zero) return _hwnd;
                EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
                {
                    var sb = new StringBuilder(64);
                    GetClassName(h, sb, 64);
                    if (sb.ToString() == "UnityWndClass") { _hwnd = h; return false; }
                    return true;
                }, IntPtr.Zero);
                return _hwnd;
            }
        }

        public static RectInt WindowRect
        {
            get
            {
                if (Hwnd == IntPtr.Zero || !GetWindowRect(Hwnd, out var r)) return new RectInt(0, 0, Screen.width, Screen.height);
                return new RectInt(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
        }

        public static void MoveWindow(Vector2Int pos)
        {
            if (Hwnd == IntPtr.Zero) return;
            SetWindowPos(Hwnd, IntPtr.Zero, pos.x, pos.y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        public static Vector2Int Cursor
        {
            get { GetCursorPos(out var p); return new Vector2Int(p.X, p.Y); }
        }

        /// 窗口所在屏幕的工作区（不含任务栏）
        public static RectInt WorkArea
        {
            get
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                var mon = MonitorFromWindow(Hwnd, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return new RectInt(0, 0, Display.main.systemWidth, Display.main.systemHeight);
                var w = mi.rcWork;
                return new RectInt(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
            }
        }

        /// 某一点所在屏幕的工作区（拖到另一块屏上时用）
        public static RectInt WorkAreaAt(Vector2Int pt)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            var mon = MonitorFromPoint(new POINT { X = pt.x, Y = pt.y }, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return WorkArea;
            var w = mi.rcWork;
            return new RectInt(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
        }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder buf, int max);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetWindowAttributeInt(IntPtr h, int attr, out int v, int size);

        static bool VisibleRect(IntPtr h, out RectInt rect)
        {
            rect = default;
            if (!IsWindow(h) || !IsWindowVisible(h) || IsIconic(h)) return false;
            if (DwmGetWindowAttributeInt(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return false; // DWMWA_CLOAKED（别的虚拟桌面 / 挂起的 UWP）
            // DWMWA_EXTENDED_FRAME_BOUNDS：不含 Win10/11 窗口四周看不见的那 7px 阴影边
            if (DwmGetWindowAttribute(h, 9, out var r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out r)) return false;
            rect = new RectInt(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return true;
        }

        /// 桌面上的普通窗口，按 Z 顺序从上往下（不含自己、任务栏、桌面、工具窗、点不中的透明浮层、最小化的）
        public static List<WinInfo> TopWindows()
        {
            var list = new List<WinInfo>();
            var me = Hwnd;
            var cls = new StringBuilder(64);
            var title = new StringBuilder(128);
            EnumWindows((h, _) =>
            {
                if (h == me) return true;
                int ex = GetWindowLong(h, -20); // GWL_EXSTYLE
                if ((ex & 0x80) != 0 || (ex & 0x20) != 0) return true; // WS_EX_TOOLWINDOW / WS_EX_TRANSPARENT（显卡浮层之类）
                if (!VisibleRect(h, out var r) || r.width < 120 || r.height < 80) return true;
                title.Clear();
                if (GetWindowText(h, title, 128) == 0) return true;
                cls.Clear();
                GetClassName(h, cls, 64);
                var c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return true;
                list.Add(new WinInfo { h = h, r = r, title = title.ToString() });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // ---------- 桌面图标的位置（explorer 里的 SysListView32；跨进程读 LVM_GETITEMRECT）----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, UIntPtr size, uint type);
        [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, ref RECT buf, UIntPtr size, out UIntPtr done);
        [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, out RECT buf, UIntPtr size, out UIntPtr done);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static IntPtr DesktopListView()
        {
            var dv = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            for (IntPtr w = IntPtr.Zero; dv == IntPtr.Zero;) // 换过壁纸 / 开过 Win+Tab 后图标层会挪到某个 WorkerW 下面
            {
                w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null);
                if (w == IntPtr.Zero) break;
                dv = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            }
            return dv == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(dv, IntPtr.Zero, "SysListView32", null);
        }

        static bool iconFailLogged;
        /// 桌面图标（图案那一块）的屏幕位置；图标藏起来了 / 读不到就返回空
        public static List<RectInt> DesktopIcons()
        {
            var list = new List<RectInt>();
            try
            {
                var lv = DesktopListView();
                if (lv == IntPtr.Zero || !IsWindowVisible(lv)) return list;
                int n = Math.Min(300, (int)SendMessage(lv, 0x1004, IntPtr.Zero, IntPtr.Zero)); // LVM_GETITEMCOUNT
                if (n <= 0) return list;
                GetWindowThreadProcessId(lv, out uint pid);
                var proc = OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid); // VM_OPERATION | VM_READ | VM_WRITE
                if (proc == IntPtr.Zero) { if (!iconFailLogged) { iconFailLogged = true; Debug.Log("[Kiko] 读不到桌面图标（OpenProcess 失败）"); } return list; }
                var mem = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)16, 0x1000 | 0x2000, 0x04);
                try
                {
                    var o = new POINT();
                    ClientToScreen(lv, ref o);
                    var size = (UIntPtr)(uint)Marshal.SizeOf<RECT>();
                    for (int i = 0; i < n; i++)
                    {
                        var r = new RECT { Left = 1 }; // LVIR_ICON
                        WriteProcessMemory(proc, mem, ref r, size, out _);
                        if (SendMessage(lv, 0x100E, (IntPtr)i, mem) == IntPtr.Zero) continue; // LVM_GETITEMRECT
                        ReadProcessMemory(proc, mem, out r, size, out _);
                        if (r.Right - r.Left > 8 && r.Bottom - r.Top > 8)
                            list.Add(new RectInt(o.X + r.Left, o.Y + r.Top, r.Right - r.Left, r.Bottom - r.Top));
                    }
                }
                finally
                {
                    VirtualFreeEx(proc, mem, UIntPtr.Zero, 0x8000);
                    CloseHandle(proc);
                }
            }
            catch (Exception e) { if (!iconFailLogged) { iconFailLogged = true; Debug.Log("[Kiko] 读桌面图标出错 " + e.Message); } }
            return list;
        }

        /// 某个窗口现在的位置；关掉 / 最小化 / 藏起来了返回 false
        public static bool RectOf(IntPtr h, out RectInt r) => VisibleRect(h, out r);

        /// 某一点所在屏幕的整块（含任务栏）
        public static RectInt MonitorAt(Vector2Int pt)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            var mon = MonitorFromPoint(new POINT { X = pt.x, Y = pt.y }, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return WorkArea;
            var w = mi.rcMonitor;
            return new RectInt(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
        }

        /// 再顶一次到最上层（任务栏也是置顶窗口，谁后置顶谁在上面；坐在任务栏边上时腿要盖在任务栏上）
        public static void BringToTop()
        {
            if (Hwnd != IntPtr.Zero) SetWindowPos(Hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
        }

        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr hMenu, uint flags, UIntPtr id, string text);
        [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "ShowWindow")] static extern bool ShowWindowNative(IntPtr h, int cmd);

        /// Windows 原生右键菜单（在光标处弹出，阻塞到选完），返回选中项的 id，没选返回 0。text 为 "-" 是分隔线
        public static int PopupMenu(params (int id, string text, bool check)[] items)
        {
            var m = CreatePopupMenu();
            foreach (var (id, text, check) in items)
                if (text == "-") AppendMenuW(m, 0x800, UIntPtr.Zero, null);                     // MF_SEPARATOR
                else AppendMenuW(m, check ? 0x8u : 0u, (UIntPtr)(uint)id, text);                 // MF_STRING | MF_CHECKED
            GetCursorPos(out var p);
            SetForegroundWindow(Hwnd); // 不先激活的话点菜单外面菜单不会消失
            int r = TrackPopupMenu(m, 0x0100 | 0x0002, p.X, p.Y, 0, Hwnd, IntPtr.Zero); // TPM_RETURNCMD | TPM_RIGHTBUTTON
            PostMessage(Hwnd, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL
            DestroyMenu(m);
            return r;
        }

        /// 把键盘焦点给这个窗口（打字和她说话）
        public static void Focus() { if (Hwnd != IntPtr.Zero) SetForegroundWindow(Hwnd); }

        /// 藏起 / 显示窗口（被戳烦了躲一会儿）
        public static void ShowWindow(bool show)
        {
            if (Hwnd != IntPtr.Zero) ShowWindowNative(Hwnd, show ? 4 : 0); // SW_SHOWNOACTIVATE / SW_HIDE
        }

        public static bool LeftDown => (GetAsyncKeyState(0x01) & 0x8000) != 0;
        public static bool RightDown => (GetAsyncKeyState(0x02) & 0x8000) != 0;
        public static bool ShiftDown => (GetAsyncKeyState(0x10) & 0x8000) != 0;

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr p, out PROCESS_MEMORY_COUNTERS c, uint cb);

        /// (工作集 MB, 提交内存 MB)。Mono 的 Process.WorkingSet64 在 Unity 里是 0，所以直接问 Windows
        public static (long ws, long commit) Memory
        {
            get
            {
                if (!GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>())) return (0, 0);
                return ((long)c.WorkingSetSize.ToUInt64() / 1048576, (long)c.PagefileUsage.ToUInt64() / 1048576);
            }
        }
#else
        // 编辑器里：用 Unity 自己的窗口坐标模拟，方便在 Game 视图里点着试
        static Vector2Int _fakePos;
        public static IntPtr Hwnd => IntPtr.Zero;
        public static RectInt WindowRect => new RectInt(_fakePos.x, _fakePos.y, Screen.width, Screen.height);
        public static void MoveWindow(Vector2Int pos) => _fakePos = pos;
        public static Vector2Int Cursor
        {
            get
            {
                var m = UnityEngine.Input.mousePosition;
                return new Vector2Int(_fakePos.x + (int)m.x, _fakePos.y + Screen.height - (int)m.y);
            }
        }
        public static RectInt WorkArea => new RectInt(0, 0, Screen.width * 4, Screen.height * 2);
        public static RectInt WorkAreaAt(Vector2Int pt) => WorkArea;
        public static RectInt MonitorAt(Vector2Int pt) => new RectInt(0, 0, WorkArea.width, WorkArea.height + 72); // 假装任务栏 72px
        public static void BringToTop() { }
        public static void Focus() { }
        // 编辑器里假装桌面上有两个窗口（坐标和上面的假工作区一致），用来试小只模式的跳上跳下
        public static List<WinInfo> TopWindows()
        {
            int W = Screen.width, H = Screen.height;
            return new List<WinInfo>
            {
                new() { h = (IntPtr)1, r = new RectInt(W / 2, (int)(H * 1.25f), W, (int)(H * 0.6f)), title = "假窗口A" },
                new() { h = (IntPtr)2, r = new RectInt(W * 2, (int)(H * 0.9f), W, H), title = "假窗口B" },
            };
        }
        // 编辑器里假装桌面左边有一列图标
        public static List<RectInt> DesktopIcons()
        {
            int W = Screen.width, H = Screen.height;
            var l = new List<RectInt>();
            for (int i = 0; i < 4; i++) l.Add(new RectInt(W / 8, (int)(H * (0.6f + i * 0.25f)), 76, 70));
            return l;
        }
        public static bool RectOf(IntPtr h, out RectInt r)
        {
            foreach (var w in TopWindows()) if (w.h == h) { r = w.r; return true; }
            r = default; return false;
        }
        public static bool LeftDown => UnityEngine.Input.GetMouseButton(0);
        public static bool RightDown => UnityEngine.Input.GetMouseButton(1);
        public static bool ShiftDown => UnityEngine.Input.GetKey(KeyCode.LeftShift);
        public static (long ws, long commit) Memory => (0, 0);
        public static int PopupMenu(params (int id, string text, bool check)[] items)
        {
            Debug.Log("[Kiko] (编辑器里不弹原生菜单) " + string.Join(" / ", Array.ConvertAll(items, i => i.text)));
            return 0;
        }
        public static void ShowWindow(bool show) { }
#endif
    }
}
