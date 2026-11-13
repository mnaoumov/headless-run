using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace HeadlessRun;

// The facts the --tray menu shows about one job. Quiet: --tray=auto chose the icon, so a missing
// notification area is not reported, a failed start lets the job run on, and the finish does not linger.
internal sealed record TrayJob(string CommandLine, DateTime Started, uint? ProcessId, string? OutputLog, string? ErrorLog,
    OutputCapture Capture, bool Quiet, Action<string> Report);

// --tray: one notification-area icon per launcher, on its own thread with its own message loop,
// for as long as the launcher waits on its job. The hidden owner window's title carries the state
// ("headless-run: running", "headless-run: exited 7"), and its WM_COMMAND ids are the menu's,
// which is what the integration tests drive.
internal sealed unsafe class Tray : IDisposable
{
    internal const string WindowClassName = "HeadlessRunTray";
    internal const string ViewerClassName = "HeadlessRunOutput";
    internal const int CommandShowOutput = 1;
    internal const int CommandHideOutput = 2;
    internal const int CommandToggleOutput = 3;
    internal const int CommandOpenOutputLog = 4;
    internal const int CommandOpenErrorLog = 5;
    internal const int CommandStop = 6;
    internal const int CommandDismiss = 7;
    private const uint CallbackMessage = TrayNative.WmApp + 1;
    private const uint FinishMessage = TrayNative.WmApp + 2;
    private const uint IconId = 1;
    private const nuint RefreshTimer = 1;
    private const nuint LingerTimer = 2;
    private const uint RefreshMilliseconds = 250;
    internal const uint LingerMilliseconds = 5000;
    private const int ShutdownSlackMilliseconds = 10000;
    private const int EditControlId = 1;
    private const int MenuTextCharacters = 100;
    private static Tray? current;
    private static nint windowClassName;
    private static nint viewerClassName;
    private readonly TrayJob job;
    private readonly ManualResetEvent stopRequested = new(false);
    private readonly ManualResetEventSlim ready = new(false);
    private readonly Thread thread;
    private Exception? failure;
    private nint window;
    private nint viewer;
    private nint edit;
    private nint font;
    private uint taskbarCreated;
    private bool iconAdded;
    private bool iconReported;
    private int shownVersion = -1;
    private int? exitCode;
    private bool closed;

    private Tray(TrayJob job)
    {
        this.job = job;
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "headless-run tray"
        };
        thread.SetApartmentState(ApartmentState.STA);
    }

    // Signaled when the user picks Stop.
    public nint StopHandle => stopRequested.SafeWaitHandle.DangerousGetHandle();

    public WaitHandle StopRequested => stopRequested;

    // Starts the tray thread and returns once its windows exist; null when a quiet tray could not start.
    public static Tray? Start(TrayJob job)
    {
        if (current is not null)
        {
            throw new InvalidOperationException("Only one tray icon per launcher.");
        }

        Tray tray = new(job);
        current = tray;
        tray.thread.Start();
        tray.ready.Wait();
        if (tray.failure is not null)
        {
            current = null;
            tray.Dispose();
            if (job.Quiet)
            {
                return null;
            }

            throw new InvalidOperationException("The tray icon could not start: " + tray.failure.Message, tray.failure);
        }

        return tray;
    }

    // The job is over. A stopped job, or a quiet tray, closes at once; a finished one keeps the icon,
    // with its exit code, for LingerMilliseconds or until Dismiss.
    public void Finish(int code, bool stopped)
    {
        if (TrayNative.PostMessage(window, FinishMessage, code, stopped ? 1 : 0))
        {
            thread.Join((int) LingerMilliseconds + ShutdownSlackMilliseconds);
        }
    }

    public void Dispose()
    {
        if (thread.IsAlive)
        {
            Finish(Program.LaunchFailure, true);
        }

        stopRequested.Dispose();
        ready.Dispose();
    }

    private void Run()
    {
        try
        {
            var instance = TrayNative.GetModuleHandle(null);
            Register(ref windowClassName, WindowClassName, instance);
            Register(ref viewerClassName, ViewerClassName, instance);
            window = Check(TrayNative.CreateWindow(0, WindowClassName, Title("running"), TrayNative.WsOverlapped, 0, 0, 0, 0, 0, 0, instance, 0));
            // An elevated launcher still hears Explorer, which runs unelevated.
            taskbarCreated = TrayNative.RegisterWindowMessage("TaskbarCreated");
            TrayNative.ChangeWindowMessageFilterEx(window, taskbarCreated, TrayNative.MessageFilterAllow, 0);
            TrayNative.ChangeWindowMessageFilterEx(window, CallbackMessage, TrayNative.MessageFilterAllow, 0);
            viewer = Check(TrayNative.CreateWindow(0, ViewerClassName, "headless-run: " + job.CommandLine, TrayNative.WsOverlappedWindow,
                TrayNative.UseDefaultPosition, TrayNative.UseDefaultPosition, TrayNative.UseDefaultPosition, TrayNative.UseDefaultPosition, 0, 0, instance, 0));
            edit = Check(TrayNative.CreateWindow(0, "EDIT", "", TrayNative.WsChild | TrayNative.WsVisible | TrayNative.WsVerticalScroll
                | TrayNative.WsHorizontalScroll | TrayNative.EsMultiline | TrayNative.EsReadOnly | TrayNative.EsAutoVerticalScroll
                | TrayNative.EsAutoHorizontalScroll, 0, 0, 0, 0, viewer, EditControlId, instance, 0));
            // Zero lifts the edit control's default 32K limit.
            TrayNative.SendMessage(edit, TrayNative.EmSetLimitText, 0, 0);
            font = TrayNative.CreateFont(TrayNative.FontHeightPixels, 0, 0, 0, TrayNative.FontWeightNormal, 0, 0, 0, TrayNative.DefaultCharacterSet,
                0, 0, 0, TrayNative.FixedPitchModernFamily, "Consolas");
            if (font != 0)
            {
                TrayNative.SendMessage(edit, TrayNative.WmSetFont, font, 1);
            }

            AddIcon();
            TrayNative.SetTimer(window, RefreshTimer, RefreshMilliseconds, 0);
        }
        catch (Exception error)
        {
            failure = error;
            ready.Set();
            return;
        }

        ready.Set();
        try
        {
            while (TrayNative.GetMessage(out var message, 0, 0, 0) > 0)
            {
                TrayNative.TranslateMessage(ref message);
                TrayNative.DispatchMessage(ref message);
            }
        }
        finally
        {
            if (font != 0)
            {
                TrayNative.DeleteObject(font);
            }
        }
    }

    private static void Register(ref nint name, string text, nint instance)
    {
        if (name == 0)
        {
            name = Marshal.StringToHGlobalUni(text);
        }

        TrayNative.WindowClass windowClass = new()
        {
            Size = (uint) sizeof(TrayNative.WindowClass),
            Procedure = (nint) (delegate* unmanaged<nint, uint, nint, nint, nint>) &WindowProcedure,
            Instance = instance,
            ClassName = name
        };
        if (TrayNative.RegisterClass(ref windowClass) == 0 && Marshal.GetLastWin32Error() != TrayNative.ErrorClassAlreadyExists)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static nint Check(nint handle)
    {
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return handle;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            var tray = current;
            if (tray is not null && tray.Handle(window, message, wParam, lParam))
            {
                return 0;
            }
        }
        catch (Exception)
        {
            // An exception must never cross into user32; the menu item simply does nothing.
        }

        return TrayNative.DefaultWindowProcedure(window, message, wParam, lParam);
    }

    // True when the message was handled and DefWindowProc must not see it.
    private bool Handle(nint target, uint message, nint wParam, nint lParam)
    {
        if (target == viewer && viewer != 0)
        {
            switch (message)
            {
                case TrayNative.WmSize:
                    TrayNative.MoveWindow(edit, 0, 0, (int) (lParam & TrayNative.LowWordMask), (int) ((lParam >> TrayNative.HighWordShift) & TrayNative.LowWordMask), true);
                    return true;
                case TrayNative.WmClose:
                    // Closing the viewer only hides it; the job keeps running.
                    TrayNative.ShowWindow(viewer, TrayNative.SwHide);
                    return true;
                default:
                    return false;
            }
        }

        if (target != window || window == 0)
        {
            return false;
        }

        if (message == taskbarCreated && taskbarCreated != 0)
        {
            // Explorer restarted and forgot every icon.
            iconAdded = false;
            AddIcon();
            return true;
        }

        switch (message)
        {
            case CallbackMessage:
                var mouse = (uint) (lParam & TrayNative.LowWordMask);
                if (mouse is TrayNative.WmRightButtonUp or TrayNative.WmContextMenu)
                {
                    ShowMenu();
                }
                else if (mouse == TrayNative.WmLeftButtonDoubleClick)
                {
                    Execute(CommandToggleOutput);
                }

                return true;
            case TrayNative.WmCommand:
                Execute((int) (wParam & TrayNative.LowWordMask));
                return true;
            case TrayNative.WmTimer:
                if ((nuint) wParam == LingerTimer)
                {
                    Close();
                }
                else
                {
                    Refresh(false);
                }

                return true;
            case FinishMessage:
                Finished((int) wParam, lParam != 0);
                return true;
            case TrayNative.WmDestroy:
                TrayNative.PostQuitMessage(0);
                return true;
            default:
                return false;
        }
    }

    private void Execute(int command)
    {
        switch (command)
        {
            case CommandShowOutput:
                Refresh(true);
                TrayNative.ShowWindow(viewer, TrayNative.SwShowNormal);
                TrayNative.SetForegroundWindow(viewer);
                break;
            case CommandHideOutput:
                TrayNative.ShowWindow(viewer, TrayNative.SwHide);
                break;
            case CommandToggleOutput:
                Execute(TrayNative.IsWindowVisible(viewer) ? CommandHideOutput : CommandShowOutput);
                break;
            case CommandOpenOutputLog:
                OpenLog(job.OutputLog);
                break;
            case CommandOpenErrorLog:
                OpenLog(job.ErrorLog);
                break;
            case CommandStop:
                if (exitCode is null)
                {
                    TrayNative.SetWindowText(window, Title("stopping"));
                    stopRequested.Set();
                }

                break;
            case CommandDismiss:
                if (exitCode is not null)
                {
                    Close();
                }

                break;
            default:
                break;
        }
    }

    private void ShowMenu()
    {
        var menu = TrayNative.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            AddItem(menu, 0, Shorten(job.CommandLine), false);
            var started = "Started " + job.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            AddItem(menu, 0, job.ProcessId is { } id ? started + ", PID " + id.ToString(CultureInfo.InvariantCulture) : started, false);
            if (exitCode is { } code)
            {
                AddItem(menu, 0, Outcome(code), false);
            }

            TrayNative.AppendMenu(menu, TrayNative.MfSeparator, 0, null);
            if (TrayNative.IsWindowVisible(viewer))
            {
                AddItem(menu, CommandHideOutput, "Hide output", true);
            }
            else
            {
                AddItem(menu, CommandShowOutput, "Show output", true);
            }

            if (job.OutputLog is not null)
            {
                AddItem(menu, CommandOpenOutputLog, "Open stdout log", true);
            }

            if (job.ErrorLog is not null)
            {
                AddItem(menu, CommandOpenErrorLog, "Open stderr log", true);
            }

            TrayNative.AppendMenu(menu, TrayNative.MfSeparator, 0, null);
            AddItem(menu, exitCode is null ? CommandStop : CommandDismiss, exitCode is null ? "Stop" : "Dismiss", true);
            TrayNative.GetCursorPos(out var point);
            // Without the foreground, the menu would not close when the user clicks elsewhere.
            TrayNative.SetForegroundWindow(window);
            TrayNative.TrackPopupMenu(menu, TrayNative.TpmRightButton, point.X, point.Y, 0, window, 0);
            TrayNative.PostMessage(window, TrayNative.WmNull, 0, 0);
        }
        finally
        {
            TrayNative.DestroyMenu(menu);
        }
    }

    private static void AddItem(nint menu, int command, string text, bool enabled)
    {
        // A single & would underline the next character instead of showing.
        TrayNative.AppendMenu(menu, enabled ? TrayNative.MfString : TrayNative.MfGrayed, (nuint) command, text.Replace("&", "&&", StringComparison.Ordinal));
    }

    private static string Shorten(string text)
    {
        return text.Length <= MenuTextCharacters ? text : string.Concat(text.AsSpan(0, MenuTextCharacters - 1), "…");
    }

    private static string Outcome(int code)
    {
        return code switch
        {
            Program.TimedOut => "Timed out (exit code 124)",
            Program.Stopped => "Stopped (exit code 130)",
            _ => "Exited with code " + code.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string Title(string state)
    {
        return "headless-run: " + state;
    }

    private void OpenLog(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            TrayNative.ShellExecute(window, "open", path, null, null, TrayNative.SwShowNormal);
        }
    }

    private void Refresh(bool force)
    {
        job.Capture.Poll();
        if (!force && !TrayNative.IsWindowVisible(viewer))
        {
            return;
        }

        var (version, text) = job.Capture.Snapshot();
        if (version == shownVersion && !force)
        {
            return;
        }

        shownVersion = version;
        TrayNative.SetWindowText(edit, text);
        TrayNative.SendMessage(edit, TrayNative.EmSetSelection, text.Length, text.Length);
        TrayNative.SendMessage(edit, TrayNative.EmScrollCaret, 0, 0);
    }

    private void Finished(int code, bool stopped)
    {
        exitCode = code;
        Refresh(false);
        TrayNative.SetWindowText(window, Title("exited " + code.ToString(CultureInfo.InvariantCulture)));
        if (stopped || job.Quiet)
        {
            Close();
            return;
        }

        if (iconAdded)
        {
            var data = IconData(TrayNative.NifIcon | TrayNative.NifTip | TrayNative.NifInfo, code == 0 ? TrayNative.IdiInformation : TrayNative.IdiError,
                Outcome(code) + ": " + job.CommandLine);
            Copy(data.InfoTitle, TrayNative.InfoTitleCharacters, "headless-run");
            Copy(data.Info, TrayNative.InfoCharacters, Outcome(code) + "\n" + job.CommandLine);
            data.InfoFlags = (code == 0 ? TrayNative.NiifInfo : TrayNative.NiifError) | TrayNative.NiifRespectQuietTime;
            TrayNative.NotifyIcon(TrayNative.NimModify, ref data);
        }

        TrayNative.SetTimer(window, LingerTimer, LingerMilliseconds, 0);
    }

    private void Close()
    {
        if (closed)
        {
            return;
        }

        closed = true;
        TrayNative.KillTimer(window, RefreshTimer);
        TrayNative.KillTimer(window, LingerTimer);
        if (iconAdded)
        {
            var data = IconData(0, 0, "");
            TrayNative.NotifyIcon(TrayNative.NimDelete, ref data);
            iconAdded = false;
        }

        TrayNative.DestroyWindow(viewer);
        TrayNative.DestroyWindow(window);
    }

    private void AddIcon()
    {
        var data = IconData(TrayNative.NifMessage | TrayNative.NifIcon | TrayNative.NifTip, TrayNative.IdiApplication, "headless-run: " + job.CommandLine);
        iconAdded = TrayNative.NotifyIcon(TrayNative.NimAdd, ref data);
        if (!iconAdded && !iconReported && !job.Quiet)
        {
            // No notification area (no Explorer, or a non-interactive session): the job runs on without the icon.
            iconReported = true;
            job.Report("The tray icon could not be added; the job runs without it.");
        }
    }

    private TrayNative.NotifyIconData IconData(uint flags, nint icon, string tip)
    {
        TrayNative.NotifyIconData data = new()
        {
            Size = (uint) sizeof(TrayNative.NotifyIconData),
            Window = window,
            Id = IconId,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = icon == 0 ? 0 : TrayNative.LoadIcon(0, icon)
        };
        Copy(data.Tip, TrayNative.TipCharacters, tip);
        return data;
    }

    // Copies text into a fixed buffer, truncated and NUL-terminated.
    private static void Copy(char* destination, int capacity, string text)
    {
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }
}
