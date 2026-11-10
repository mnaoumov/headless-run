using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HeadlessRun;

// Under --tray, a stream that is not redirected to a file goes through a pipe: the launcher copies
// every byte to the stream's original destination, when there is one, and to the output viewer.
internal sealed class Tee : IDisposable
{
    private const int ReadBufferBytes = 4096;
    private readonly nint destination;
    private readonly OutputCapture.Source source;
    private nint read;
    private nint write;
    private Thread? thread;

    // destination: the handle the child would otherwise get, or 0 when it would get NUL.
    public Tee(nint destination, OutputCapture.Source source)
    {
        this.destination = destination;
        this.source = source;
        Native.SecurityAttributes security = new()
        {
            Size = Marshal.SizeOf<Native.SecurityAttributes>(),
            Inherit = 1
        };
        if (!TrayNative.CreatePipe(out read, out write, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // Only the write end goes to the child; the launcher keeps the read end to itself.
        if (!TrayNative.SetHandleInformation(read, TrayNative.HandleFlagInherit, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    // The handle the child writes to.
    public nint ChildHandle => write;

    // After process creation: drop the launcher's copy of the write end, so the pipe ends
    // when the child and its descendants have closed theirs, and start copying.
    public void Start()
    {
        Native.CloseHandle(write);
        write = 0;
        var handle = read;
        read = 0;
        thread = new Thread(() => Copy(handle))
        {
            IsBackground = true,
            Name = "headless-run tee"
        };
        thread.Start();
    }

    public void Join(int milliseconds)
    {
        thread?.Join(milliseconds);
    }

    public void Dispose()
    {
        if (write != 0)
        {
            Native.CloseHandle(write);
            write = 0;
        }

        if (read != 0)
        {
            Native.CloseHandle(read);
            read = 0;
        }
    }

    private void Copy(nint handle)
    {
        try
        {
            using FileStream stream = new(new SafeFileHandle(handle, true), FileAccess.Read, 1);
            var buffer = new byte[ReadBufferBytes];
            var forward = destination != 0;
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                // A destination that went away must not stall the child: keep draining for the viewer.
                if (forward && !Native.WriteFile(destination, buffer, (uint) count, out _, 0))
                {
                    forward = false;
                }

                source.Write(buffer.AsSpan(0, count));
            }
        }
        catch (IOException)
        {
            // The pipe broke: the job is over.
        }
    }
}
