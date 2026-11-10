using System.Runtime.InteropServices;

namespace HeadlessRun;

// The user32, shell32 and gdi32 surface behind --tray. Plain Win32, so the NativeAOT build needs no UI framework.
internal static class TrayNative
{
    private const string User32Library = "user32.dll";
    private const string Shell32Library = "shell32.dll";
    private const string Gdi32Library = "gdi32.dll";
    private const string Kernel32Library = "kernel32.dll";
    internal const int TipCharacters = 128;
    internal const int InfoCharacters = 256;
    internal const int InfoTitleCharacters = 64;
    internal const uint WmNull = 0;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmSize = 0x0005;
    internal const uint WmSetText = 0x000C;
    internal const uint WmClose = 0x0010;
    internal const uint WmSetFont = 0x0030;
    internal const uint WmCommand = 0x0111;
    internal const uint WmTimer = 0x0113;
    internal const uint WmLeftButtonDoubleClick = 0x0203;
    internal const uint WmRightButtonUp = 0x0205;
    internal const uint WmContextMenu = 0x007B;
    internal const uint WmApp = 0x8000;
    internal const uint EmSetSelection = 0x00B1;
    internal const uint EmScrollCaret = 0x00B7;
    internal const uint EmSetLimitText = 0x00C5;
    internal const uint WsOverlapped = 0;
    internal const uint WsOverlappedWindow = 0x00CF0000;
    internal const uint WsChild = 0x40000000;
    internal const uint WsVisible = 0x10000000;
    internal const uint WsVerticalScroll = 0x00200000;
    internal const uint WsHorizontalScroll = 0x00100000;
    internal const uint EsMultiline = 0x0004;
    internal const uint EsAutoVerticalScroll = 0x0040;
    internal const uint EsAutoHorizontalScroll = 0x0080;
    internal const uint EsReadOnly = 0x0800;
    internal const int UseDefaultPosition = unchecked((int) 0x80000000);
    internal const int SwHide = 0;
    internal const int SwShowNormal = 1;
    internal const uint MfString = 0;
    internal const uint MfGrayed = 1;
    internal const uint MfSeparator = 0x800;
    internal const uint TpmRightButton = 2;
    internal const uint NimAdd = 0;
    internal const uint NimModify = 1;
    internal const uint NimDelete = 2;
    internal const uint NifMessage = 1;
    internal const uint NifIcon = 2;
    internal const uint NifTip = 4;
    internal const uint NifInfo = 0x10;
    internal const uint NiifInfo = 1;
    internal const uint NiifError = 3;
    internal const uint NiifRespectQuietTime = 0x80;
    internal const nint IdiApplication = 32512;
    internal const nint IdiError = 32513;
    internal const nint IdiInformation = 32516;
    internal const uint MessageFilterAllow = 1;
    internal const int ErrorClassAlreadyExists = 1410;
    internal const int FontHeightPixels = -14;
    internal const int FontWeightNormal = 400;
    internal const uint DefaultCharacterSet = 1;
    internal const uint FixedPitchModernFamily = 0x31;
    internal const uint HandleFlagInherit = 1;
    internal const int LowWordMask = 0xFFFF;
    internal const int HighWordShift = 16;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Window;
        public uint Code;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public Point Cursor;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[TipCharacters];
        public uint State;
        public uint StateMask;
        public fixed char Info[InfoCharacters];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[InfoTitleCharacters];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [DllImport(User32Library, EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport(User32Library, EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindow(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);
    [DllImport(User32Library, EntryPoint = "DefWindowProcW")]
    internal static extern nint DefaultWindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [DllImport(User32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint window);
    [DllImport(User32Library, EntryPoint = "GetMessageW")]
    internal static extern int GetMessage(out Message message, nint window, uint first, uint last);
    [DllImport(User32Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref Message message);
    [DllImport(User32Library, EntryPoint = "DispatchMessageW")]
    internal static extern nint DispatchMessage(ref Message message);
    [DllImport(User32Library, EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport(User32Library)]
    internal static extern void PostQuitMessage(int code);
    [DllImport(User32Library, EntryPoint = "SendMessageW")]
    internal static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport(User32Library, SetLastError = true)]
    internal static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint procedure);
    [DllImport(User32Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(nint window, nuint id);
    [DllImport(User32Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint window, int command);
    [DllImport(User32Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);
    [DllImport(User32Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);
    [DllImport(User32Library, EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowText(nint window, string text);
    [DllImport(User32Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MoveWindow(nint window, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);
    [DllImport(User32Library, SetLastError = true)]
    internal static extern nint CreatePopupMenu();
    [DllImport(User32Library, EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport(User32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport(User32Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(nint menu);
    [DllImport(User32Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);
    [DllImport(User32Library, EntryPoint = "LoadIconW")]
    internal static extern nint LoadIcon(nint instance, nint name);
    [DllImport(User32Library, EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string name);
    [DllImport(User32Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint status);
    [DllImport(Kernel32Library, EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? name);
    [DllImport(Shell32Library, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport(Shell32Library, EntryPoint = "ShellExecuteW", CharSet = CharSet.Unicode)]
    internal static extern nint ShellExecute(nint window, string verb, string file, string? parameters, string? directory, int show);
    [DllImport(Gdi32Library, EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)]
    internal static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint characterSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport(Gdi32Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint handle);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(out nint read, out nint write, ref Native.SecurityAttributes security, uint size);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
}
