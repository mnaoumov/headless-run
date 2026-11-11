using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace HeadlessRun;

// --event-log: one Application-log event per run, written when the run ends. Writing never fails the job.
internal static class JobEventLog
{
    internal const string Source = "headless-run";
    private const ushort InformationType = 0x0004;
    private const ushort WarningType = 0x0002;
    private const ushort ErrorType = 0x0001;
    private const ushort NoCategory = 0;
    private static Stopwatch clock = Stopwatch.StartNew();
    private static DateTime started = DateTime.Now;
    private static Outcome? recorded;
    private static string? recordedDetail;

    internal enum Outcome
    {
        Exited = 1,
        ExitedNonzero = 2,
        TimedOut = 3,
        Stopped = 4,
        LaunchFailure = 5,
        Detached = 6
    }

    // Under --elevate the copy and the caller each record only what they saw themselves; the relayed code is not recorded again.
    internal static bool Relayed;

    // Called first thing in Main, so the duration covers the whole run.
    internal static void Begin()
    {
        clock = Stopwatch.StartNew();
        started = DateTime.Now;
    }

    internal static void Record(Outcome outcome, string? detail = null)
    {
        recorded = outcome;
        recordedDetail = detail;
    }

    // Called once, as Main returns: an outcome recorded earlier wins over one inferred from the code.
    internal static void Write(Options options, int code, string? diagnostic)
    {
        if (!options.EventLog || Relayed && recorded is null)
        {
            return;
        }

        diagnostic ??= recordedDetail;
        var outcome = recorded ?? (diagnostic is not null ? Outcome.LaunchFailure : code == 0 ? Outcome.Exited : Outcome.ExitedNonzero);
        var summary = outcome switch
        {
            Outcome.Exited => "The child exited with code 0.",
            Outcome.ExitedNonzero => $"The child exited with code {code} (0x{code:X8}).",
            Outcome.TimedOut => $"The timeout expired; the child job was terminated (exit code {code}).",
            Outcome.Stopped => $"The job was stopped from the tray; the child job was terminated (exit code {code}).",
            Outcome.LaunchFailure => $"Launcher failure (exit code {code}): {diagnostic ?? "see the error stream for the diagnostic."}",
            _ => "The child started detached."
        };
        StringBuilder text = new();
        text.Append("headless-run: ").Append(summary).Append('\n');
        text.Append("Command: ").Append(options.Tail.Length == 0 ? "(none)" : options.Tail).Append('\n');
        text.Append("Working directory: ").Append(options.Directory ?? Environment.CurrentDirectory).Append('\n');
        text.Append("Started: ").Append(started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        text.Append("; duration: ").Append(clock.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)).Append(" s\n");
        text.Append("Launcher process id: ").Append(Environment.ProcessId);
        var type = outcome switch
        {
            Outcome.Exited or Outcome.Detached => InformationType,
            Outcome.ExitedNonzero or Outcome.Stopped => WarningType,
            _ => ErrorType
        };
        Report(type, (uint) outcome, text.ToString());
    }

    private static void Report(ushort type, uint eventId, string text)
    {
        try
        {
            var source = Native.RegisterEventSource(null, Source);
            if (source == 0)
            {
                return;
            }

            var message = Marshal.StringToHGlobalUni(text);
            try
            {
                Native.ReportEvent(source, type, NoCategory, eventId, 0, 1, 0, [message], 0);
            }
            finally
            {
                Marshal.FreeHGlobal(message);
                Native.DeregisterEventSource(source);
            }
        }
        catch (Exception)
        {
            // The event log is a convenience: a job never fails or changes because it could not be written.
        }
    }
}
