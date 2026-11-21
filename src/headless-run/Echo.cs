using System.Runtime.InteropServices;
using System.Text;

namespace HeadlessRun;

// --echo: show what the launcher received and the child command line it would start, then start nothing.
// It reaches the person who asked wherever they are: --stdout FILE, a redirected stdout, the calling
// console, or, when there is no console at all (a shortcut, the Run dialog, Explorer), a window that also
// lets the person edit the command line and see it split again as they type (EchoDialog).
internal static class Echo
{
    internal const string Caption = "headless-run --echo";
    private const string Kernel32Library = "kernel32.dll";
    private const uint FileTypeDisk = 1;
    private const uint FileTypeChar = 2;
    private const uint FileTypePipe = 3;

    internal static int Run(Options options, string commandLine)
    {
        StringBuilder text = new();
        var code = 0;
        text.Append("headless-run --echo: nothing was started.\n\n");
        text.Append("Launcher command line, as received:\n  ").Append(commandLine).Append('\n');
        text.Append("Launcher arguments:\n");
        AppendArguments(text, commandLine);
        if (options.Tail.Length == 0)
        {
            text.Append("\nNo -- tail: pass -- <program> [arguments...] to see the child command line.\n");
        }
        else
        {
            text.Append("\nChild tail, forwarded unchanged:\n  ").Append(options.Tail).Append('\n');
            try
            {
                var (application, childLine, engine) = Program.ResolveCommand(options);
                text.Append("Engine: ").Append(engine is null ? "none, the program runs directly" : engine.Name).Append('\n');
                if (application is not null)
                {
                    text.Append("Interpreter: ").Append(application).Append('\n');
                }

                text.Append("Child command line:\n  ").Append(childLine).Append('\n');
                if (engine is { QuoteCommand: true })
                {
                    // cmd.exe does not split its command line: it runs the tail as a command, %VAR% expanded.
                    text.Append("Tail arguments, as the C runtime splits them for a program cmd.exe starts, before cmd's own expansion:\n");
                    AppendArguments(text, options.Tail);
                }
                else
                {
                    text.Append("Child arguments, as the C runtime splits them:\n");
                    AppendArguments(text, childLine);
                }
            }
            catch (Exception error)
            {
                text.Append("The child command line cannot be built: ").Append(error.Message).Append('\n');
                code = Program.LaunchFailure;
            }
        }

        if (options.Directory is not null)
        {
            text.Append("\nWorking directory: ").Append(options.Directory).Append('\n');
        }

        if (options.Environment.Count != 0)
        {
            text.Append(options.Directory is null ? "\n" : "").Append("Environment overrides:\n");
            foreach (var entry in options.Environment)
            {
                text.Append("  ").Append(entry.Key).Append('=').Append(entry.Value).Append('\n');
            }
        }

        Show(options, text.ToString(), commandLine);
        return code;
    }

    private static void AppendArguments(StringBuilder text, string commandLine)
    {
        text.Append(EchoDialog.FormatArguments(commandLine, "\n"));
    }

    // The Microsoft C runtime's split: the program name up to its closing quote or whitespace, with no
    // backslash escapes, then the usual quote and backslash rules; an unclosed quote runs to the end.
    internal static List<string> Split(string commandLine)
    {
        List<string> arguments = [];
        var position = 0;
        Options.SkipSpace(commandLine, ref position);
        if (position == commandLine.Length)
        {
            return arguments;
        }

        if (commandLine[position] == '"')
        {
            var end = commandLine.IndexOf('"', position + 1);
            var stop = end < 0 ? commandLine.Length : end;
            arguments.Add(commandLine[(position + 1)..stop]);
            position = end < 0 ? commandLine.Length : end + 1;
        }
        else
        {
            var start = position;
            while (position < commandLine.Length && commandLine[position] is not (' ' or '\t'))
            {
                position++;
            }

            arguments.Add(commandLine[start..position]);
        }

        while (true)
        {
            Options.SkipSpace(commandLine, ref position);
            if (position == commandLine.Length)
            {
                return arguments;
            }

            arguments.Add(Options.ReadToken(commandLine, ref position, true));
        }
    }

    private static void Show(Options options, string text, string commandLine)
    {
        if (options.Output is not null)
        {
            Program.CreateParentDirectory(options.Output);
            if (options.Append)
            {
                File.AppendAllText(options.Output, text);
            }
            else
            {
                File.WriteAllText(options.Output, text);
            }

            return;
        }

        if (WriteTo(Native.GetStdHandle(Native.StandardOutputHandle), text))
        {
            return;
        }

        if (Native.AttachConsole(Native.AttachParentProcess) && WriteToConsole(text))
        {
            return;
        }

        if (!Elevation.InteractiveWindowStation())
        {
            throw new InvalidOperationException("--echo has no console and no desktop to show its text on: pass --stdout FILE.");
        }

        EchoDialog.Show(text, commandLine);
    }

    // A file, a pipe or a console takes the text. NUL, a missing handle or anything else does not.
    private static bool WriteTo(nint handle, string text)
    {
        if (handle is 0 or -1)
        {
            return false;
        }

        var type = GetFileType(handle);
        if (type == FileTypeChar && GetConsoleMode(handle, out _))
        {
            return WriteConsole(handle, text, (uint) text.Length, out _, 0);
        }

        if (type is not (FileTypeDisk or FileTypePipe))
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        return Native.WriteFile(handle, bytes, (uint) bytes.Length, out _, 0);
    }

    private static bool WriteToConsole(string text)
    {
        Native.SecurityAttributes security = new()
        {
            Size = Marshal.SizeOf<Native.SecurityAttributes>()
        };
        var console = Native.CreateFile("CONOUT$", Native.GenericRead | Native.GenericWrite, Native.FileShareRead | Native.FileShareWrite,
            ref security, Native.OpenExisting, 0, 0);
        if (console is 0 or -1)
        {
            return false;
        }

        try
        {
            // The prompt the shell printed while this GUI program started is already on the line.
            return WriteTo(console, "\n" + text);
        }
        finally
        {
            Native.CloseHandle(console);
        }
    }

    [DllImport(Kernel32Library, SetLastError = true)]
    private static extern uint GetFileType(nint file);
    [DllImport(Kernel32Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint console, out uint mode);
    [DllImport(Kernel32Library, EntryPoint = "WriteConsoleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsole(nint console, string text, uint length, out uint written, nint reserved);
}
