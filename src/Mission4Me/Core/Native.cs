using System;
using System.Runtime.InteropServices;

namespace Mission4Me.Core;

/// <summary>P/Invoke mínimo: estilos de janela, posição da barra de tarefas e broadcast de tema.</summary>
internal static class Native
{
    // ---- Estilos de janela -------------------------------------------------
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x00000020;   // clique atravessa
    public const long WS_EX_TOOLWINDOW  = 0x00000080;   // fora do Alt+Tab
    public const long WS_EX_LAYERED     = 0x00080000;
    public const long WS_EX_NOACTIVATE  = 0x08000000;   // nunca rouba foco

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ---- Z-order / posição -------------------------------------------------
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    // ---- Barra de tarefas (AppBar) -----------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    public const uint ABM_GETTASKBARPOS = 0x00000005;

    [DllImport("shell32.dll")]
    public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    // ---- Tela cheia (vídeo, jogo, apresentação) ----------------------------
    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);
    public const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    public static bool IsSomethingFullscreen()
    {
        try
        {
            if (SHQueryUserNotificationState(out int s) != 0) return false;
            return s is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        }
        catch { return false; }
    }

    // ---- Broadcast de mudança de tema --------------------------------------
    public static readonly IntPtr HWND_BROADCAST = new(0xffff);
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_THEMECHANGED = 0x031A;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string? lParam,
                                                   uint flags, uint timeoutMs, out UIntPtr result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
