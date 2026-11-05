// Windows 窗口/屏幕工具：窗口位置、工作区（不含任务栏）、全局鼠标位置。
// 坐标一律是「屏幕物理像素、左上角为原点、y 向下」，和 Tauri 版 pet.js 的逻辑一致（不过那边是逻辑像素）。
// UniWindowController 只负责透明和鼠标穿透；移动窗口、找任务栏都走这里，免得两套坐标系混用。
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Kiko
{
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

        public static bool LeftDown => (GetAsyncKeyState(0x01) & 0x8000) != 0;
        public static bool RightDown => (GetAsyncKeyState(0x02) & 0x8000) != 0;
        public static bool ShiftDown => (GetAsyncKeyState(0x10) & 0x8000) != 0;
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
        public static bool LeftDown => UnityEngine.Input.GetMouseButton(0);
        public static bool RightDown => UnityEngine.Input.GetMouseButton(1);
        public static bool ShiftDown => UnityEngine.Input.GetKey(KeyCode.LeftShift);
#endif
    }
}
