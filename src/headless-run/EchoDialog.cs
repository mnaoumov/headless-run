using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HeadlessRun;

// The --echo window, shown when there is no console to write to (a shortcut, the Run dialog, Explorer).
// It holds the report, then an editable copy of the launcher command line with that line's arguments
// below it, split again on every keystroke by the same rule the report used, so a person can try a
// variant of the line where it actually runs. Plain Win32 through TrayNative, like the tray icon.
internal sealed unsafe class EchoDialog
{
    internal const string WindowClassName = "HeadlessRunEcho";
    internal const int ReportControlId = 101;
    internal const int LineControlId = 102;
    internal const int ArgumentsControlId = 103;
    internal const int PromptControlId = 104;
    internal const int HintControlId = 105;
    internal const int DialogCancel = 2;
    private const string Prompt = "Command line to try (its arguments, as the C runtime splits them, follow as you type):";
    private const string Hint = "Ctrl+C copies the selection, or the whole box when none is selected. Esc closes.";
    private const int WindowWidth = 760;
    private const int WindowHeight = 600;
    private const int Margin = 10;
    private const int Gap = 6;
    private const int LabelHeight = 18;
    private const int LineHeight = 24;
    private const int ButtonWidth = 88;
    private const int ButtonHeight = 28;
    private const int ReportShareNumerator = 3;
    private const int ReportShareDenominator = 5;
    private const int LabelFontHeightPixels = -13;
    private static EchoDialog? current;
    private static nint windowClassName;
    private nint window;
    private nint report;
    private nint line;
    private nint arguments;
    private nint prompt;
    private nint hint;
    private nint closeButton;
    private nint textFont;
    private nint labelFont;
    private nint savedFocus;

    // Shows the window and returns once the person closes it.
    internal static void Show(string reportText, string commandLine)
    {
        EchoDialog dialog = new();
        current = dialog;
        try
        {
            dialog.Create(reportText, commandLine);
            dialog.Loop();
        }
        finally
        {
            current = null;
            dialog.DeleteFonts();
        }
    }

    // One numbered line per argument, in brackets so leading and trailing spaces and empty arguments show.
    internal static string FormatArguments(string commandLine, string newline)
    {
        var parts = Echo.Split(commandLine);
        StringBuilder text = new();
        for (var index = 0; index < parts.Count; index++)
        {
            text.Append("  ").Append(index).Append(": [").Append(parts[index]).Append(']').Append(newline);
        }

        return text.ToString();
    }

    private void Create(string reportText, string commandLine)
    {
        var instance = TrayNative.GetModuleHandle(null);
        if (windowClassName == 0)
        {
            windowClassName = Marshal.StringToHGlobalUni(WindowClassName);
        }

        TrayNative.WindowClass windowClass = new()
        {
            Size = (uint) sizeof(TrayNative.WindowClass),
            Procedure = (nint) (delegate* unmanaged<nint, uint, nint, nint, nint>) &WindowProcedure,
            Instance = instance,
            Cursor = TrayNative.LoadCursor(0, TrayNative.IdcArrow),
            Background = TrayNative.ColorButtonFace + 1,
            ClassName = windowClassName
        };
        if (TrayNative.RegisterClass(ref windowClass) == 0 && Marshal.GetLastWin32Error() != TrayNative.ErrorClassAlreadyExists)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        window = Check(TrayNative.CreateWindow(0, WindowClassName, Echo.Caption, TrayNative.WsOverlappedWindow,
            TrayNative.UseDefaultPosition, TrayNative.UseDefaultPosition, WindowWidth, WindowHeight, 0, 0, instance, 0));
        const uint editStyle = TrayNative.WsChild | TrayNative.WsVisible | TrayNative.WsTabStop;
        const uint boxStyle = editStyle | TrayNative.WsVerticalScroll | TrayNative.WsHorizontalScroll | TrayNative.EsMultiline
            | TrayNative.EsReadOnly | TrayNative.EsAutoVerticalScroll | TrayNative.EsAutoHorizontalScroll;
        report = Check(TrayNative.CreateWindow(TrayNative.WsExClientEdge, "EDIT", Lines(reportText), boxStyle, 0, 0, 0, 0, window, ReportControlId, instance, 0));
        prompt = Check(TrayNative.CreateWindow(0, "STATIC", Prompt, TrayNative.WsChild | TrayNative.WsVisible, 0, 0, 0, 0, window, PromptControlId, instance, 0));
        line = Check(TrayNative.CreateWindow(TrayNative.WsExClientEdge, "EDIT", commandLine, editStyle | TrayNative.EsAutoHorizontalScroll,
            0, 0, 0, 0, window, LineControlId, instance, 0));
        arguments = Check(TrayNative.CreateWindow(TrayNative.WsExClientEdge, "EDIT", FormatArguments(commandLine, "\r\n"), boxStyle,
            0, 0, 0, 0, window, ArgumentsControlId, instance, 0));
        hint = Check(TrayNative.CreateWindow(0, "STATIC", Hint, TrayNative.WsChild | TrayNative.WsVisible, 0, 0, 0, 0, window, HintControlId, instance, 0));
        closeButton = Check(TrayNative.CreateWindow(0, "BUTTON", "Close", TrayNative.WsChild | TrayNative.WsVisible | TrayNative.WsTabStop,
            0, 0, 0, 0, window, DialogCancel, instance, 0));
        // Zero lifts the edit controls' default 32K limit; a command line can reach 32,767 characters.
        TrayNative.SendMessage(line, TrayNative.EmSetLimitText, 0, 0);
        TrayNative.SendMessage(report, TrayNative.EmSetLimitText, 0, 0);
        TrayNative.SendMessage(arguments, TrayNative.EmSetLimitText, 0, 0);
        textFont = TrayNative.CreateFont(TrayNative.FontHeightPixels, 0, 0, 0, TrayNative.FontWeightNormal, 0, 0, 0, TrayNative.DefaultCharacterSet,
            0, 0, 0, TrayNative.FixedPitchModernFamily, "Consolas");
        labelFont = TrayNative.CreateFont(LabelFontHeightPixels, 0, 0, 0, TrayNative.FontWeightNormal, 0, 0, 0, TrayNative.DefaultCharacterSet,
            0, 0, 0, 0, "Segoe UI");
        SetFont(textFont, report, line, arguments);
        SetFont(labelFont, prompt, hint, closeButton);
        TrayNative.ShowWindow(window, TrayNative.SwShowNormal);
        TrayNative.SetForegroundWindow(window);
        TrayNative.SetFocus(line);
        var length = (int) TrayNative.SendMessage(line, TrayNative.WmGetTextLength, 0, 0);
        TrayNative.SendMessage(line, TrayNative.EmSetSelection, length, length);
    }

    private static void SetFont(nint font, params nint[] controls)
    {
        if (font == 0)
        {
            return;
        }

        foreach (var control in controls)
        {
            TrayNative.SendMessage(control, TrayNative.WmSetFont, font, 1);
        }
    }

    private void Loop()
    {
        while (TrayNative.GetMessage(out var message, 0, 0, 0) > 0)
        {
            if (CopyWholeBox(ref message))
            {
                continue;
            }

            // Tab between the boxes and Esc to close, as in a dialog box. Enter in the line closes nothing.
            if (TrayNative.IsDialogMessage(window, ref message))
            {
                continue;
            }

            TrayNative.TranslateMessage(ref message);
            TrayNative.DispatchMessage(ref message);
        }
    }

    // Ctrl+C in a box with nothing selected copies the whole box, as the message box this replaced did.
    private bool CopyWholeBox(ref TrayNative.Message message)
    {
        if (message.Code != TrayNative.WmKeyDown || message.WParam != TrayNative.VirtualKeyC
            || TrayNative.GetKeyState(TrayNative.VirtualKeyControl) >= 0
            || (message.Window != report && message.Window != line && message.Window != arguments))
        {
            return false;
        }

        uint start = 0;
        uint end = 0;
        TrayNative.SendMessage(message.Window, TrayNative.EmGetSelection, (nint) (&start), (nint) (&end));
        if (start != end)
        {
            return false;
        }

        TrayNative.SendMessage(message.Window, TrayNative.EmSetSelection, 0, -1);
        TrayNative.SendMessage(message.Window, TrayNative.WmCopy, 0, 0);
        TrayNative.SendMessage(message.Window, TrayNative.EmSetSelection, (nint) start, (nint) end);
        return true;
    }

    private static string Lines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
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
            var dialog = current;
            if (dialog is not null && dialog.window == window && dialog.Handle(message, wParam, lParam))
            {
                return 0;
            }
        }
        catch (Exception)
        {
            // An exception must never cross into user32.
        }

        return TrayNative.DefaultWindowProcedure(window, message, wParam, lParam);
    }

    // True when the message was handled and DefWindowProc must not see it.
    private bool Handle(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case TrayNative.WmSize:
                Layout((int) (lParam & TrayNative.LowWordMask), (int) ((lParam >> TrayNative.HighWordShift) & TrayNative.LowWordMask));
                return true;
            case TrayNative.WmActivate:
                // Keep the focused box across a switch to another window and back.
                if ((wParam & TrayNative.LowWordMask) == TrayNative.WaInactive)
                {
                    savedFocus = TrayNative.GetFocus();
                }
                else if (savedFocus != 0)
                {
                    TrayNative.SetFocus(savedFocus);
                }
                else
                {
                    return false;
                }

                return true;
            case TrayNative.WmCommand:
                var id = (int) (wParam & TrayNative.LowWordMask);
                var code = (uint) ((wParam >> TrayNative.HighWordShift) & TrayNative.LowWordMask);
                if (id == LineControlId && code == TrayNative.EnChange)
                {
                    Resplit();
                }
                else if (id == DialogCancel)
                {
                    TrayNative.DestroyWindow(window);
                }

                return true;
            case TrayNative.WmDestroy:
                TrayNative.PostQuitMessage(0);
                return true;
            default:
                return false;
        }
    }

    private void Resplit()
    {
        var length = (int) TrayNative.SendMessage(line, TrayNative.WmGetTextLength, 0, 0);
        var buffer = new char[length + 1];
        var read = TrayNative.GetWindowText(line, buffer, buffer.Length);
        TrayNative.SetWindowText(arguments, FormatArguments(new string(buffer, 0, read), "\r\n"));
    }

    private void Layout(int width, int height)
    {
        var inner = Math.Max(width - (2 * Margin), 0);
        var fixedHeight = (2 * Margin) + LabelHeight + LineHeight + ButtonHeight + (3 * Gap);
        var boxes = Math.Max(height - fixedHeight, 0);
        var reportHeight = boxes * ReportShareNumerator / ReportShareDenominator;
        var argumentsHeight = boxes - reportHeight;
        var y = Margin;
        TrayNative.MoveWindow(report, Margin, y, inner, reportHeight, true);
        y += reportHeight + Gap;
        TrayNative.MoveWindow(prompt, Margin, y, inner, LabelHeight, true);
        y += LabelHeight;
        TrayNative.MoveWindow(line, Margin, y, inner, LineHeight, true);
        y += LineHeight + Gap;
        TrayNative.MoveWindow(arguments, Margin, y, inner, argumentsHeight, true);
        y += argumentsHeight + Gap;
        TrayNative.MoveWindow(hint, Margin, y + Gap, Math.Max(inner - ButtonWidth - Gap, 0), LabelHeight, true);
        TrayNative.MoveWindow(closeButton, width - Margin - ButtonWidth, y, ButtonWidth, ButtonHeight, true);
    }

    private void DeleteFonts()
    {
        if (textFont != 0)
        {
            TrayNative.DeleteObject(textFont);
        }

        if (labelFont != 0)
        {
            TrayNative.DeleteObject(labelFont);
        }
    }
}
