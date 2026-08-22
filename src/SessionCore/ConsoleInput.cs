using System.Runtime.InteropServices;
using System.Threading;

namespace SessionCore;

/// <summary>
/// Types into a terminal we do not own, without touching the foreground.
///
/// A process may borrow another's console with <c>AttachConsole</c> — which is already how
/// the session's window is found (see <see cref="LiveSessions"/>) — and once attached it can
/// push key records straight into that console's input buffer. Windows Terminal hands every
/// tab a pseudoconsole, and the records come out the other side as ordinary keystrokes, so
/// this reaches the exact tab with no window raised, nothing focused, and nothing stolen
/// from whatever the operator is doing.
///
/// Two gestures are enough to restart a session in place: make Claude quit, then type the
/// resume command at the shell it hands the terminal back to.
/// </summary>
public static class ConsoleInput
{
    /// <summary>
    /// One process may be attached to one console at a time, and title reads share the same
    /// borrow, so every use of a foreign console queues on this.
    /// </summary>
    internal static readonly object Gate = new();

    /// <summary>
    /// Ask the Claude session at <paramref name="pid"/> to quit, without ever sending what
    /// might be sitting half-typed in its input box.
    ///
    /// Ctrl+C is the gesture rather than <c>/exit</c> precisely because of that box: typing
    /// <c>/exit</c> into a box that already holds a draft appends to the draft and submits
    /// the whole thing as a prompt. The first Ctrl+C clears the box instead — the draft is
    /// discarded, never sent — and a quick second and third take the CLI up on its own
    /// "press Ctrl-C again to exit". They have to be quick: leave more than about a second
    /// between them and each one re-arms rather than exiting.
    /// </summary>
    public static bool SendExitGesture(int pid) => Borrow(pid, write =>
    {
        write(Ctrl_C);                  // clears any draft, or interrupts
        Thread.Sleep(1500);
        write(Ctrl_C);                  // "press Ctrl-C again to exit"
        Thread.Sleep(250);
        write(Ctrl_C);                  // ...taken up on, inside its window
        return true;
    });

    /// <summary>Type a command at the shell holding <paramref name="pid"/>'s console and run it.</summary>
    public static bool SendLine(int pid, string line) => Borrow(pid, write =>
    {
        write(line);
        Thread.Sleep(120);              // let the shell echo before it is told to run
        write("\r");
        return true;
    });

    /// <summary>
    /// The title the CLI painted on its terminal, read by borrowing the session's own
    /// console. It is what names the exact Windows Terminal tab a session sits in, since
    /// no edge of the process tree does.
    /// </summary>
    public static string ReadTitle(int pid)
    {
        lock (Gate)
        {
            bool had = HasOwnConsole;
            try
            {
                FreeConsole();                   // drop whatever a previous borrow left attached
                if (!AttachConsole((uint)pid)) return "";
                try
                {
                    var buffer = new System.Text.StringBuilder(1024);
                    GetConsoleTitle(buffer, (uint)buffer.Capacity);
                    return buffer.ToString();
                }
                finally { FreeConsole(); }
            }
            catch { return ""; }
            finally { RestoreOwnConsole(had); }
        }
    }

    /// <summary>
    /// Borrow the console owning <paramref name="pid"/> for the duration of <paramref name="body"/>,
    /// which is handed a writer for that console's input buffer.
    /// </summary>
    private static bool Borrow(int pid, Func<Action<string>, bool> body)
    {
        lock (Gate)
        {
            IgnoreCtrlC.Arm();

            bool had = HasOwnConsole;
            FreeConsole();                       // drop whatever a previous borrow left attached
            if (!AttachConsole((uint)pid)) { RestoreOwnConsole(had); return false; }
            try
            {
                var conin = CreateFileW("CONIN$", GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (conin == InvalidHandle) return false;

                try { return body(text => Write(conin, text)); }
                finally { CloseHandle(conin); }
            }
            catch { return false; }
            finally
            {
                FreeConsole();
                RestoreOwnConsole(had);
            }
        }
    }

    // --- giving our own console back ----------------------------------------

    /// <summary>
    /// True when this process has a console of its own to lose. The WPF app has none, so
    /// borrowing costs it nothing; SessionCli is a console program printing to the very
    /// console that <see cref="FreeConsole"/> is about to detach it from.
    /// </summary>
    private static bool HasOwnConsole => GetConsoleWindow() != IntPtr.Zero;

    /// <summary>
    /// Re-attach to the console we were sharing before the borrow and reopen the standard
    /// streams onto it. Without this a console program that restarts a session goes mute
    /// halfway through: its stdout handle refers to a console it is no longer attached to,
    /// so everything it prints afterwards — the result of the very operation — is dropped.
    ///
    /// The parent's console is the right one to come back to: a console program launched
    /// from a shell shares that shell's console rather than owning one. Redirected output
    /// is a pipe, unaffected by any of this, and is left alone.
    /// </summary>
    private static void RestoreOwnConsole(bool had)
    {
        if (!had) return;

        // Read the encoding before re-attaching: a caller that asked for UTF-8 output must
        // still get it on the streams we hand back, or the report comes out mojibake.
        var encoding = TryGetOutputEncoding();
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;

        try
        {
            if (!Console.IsOutputRedirected)
                Console.SetOut(Writer(Console.OpenStandardOutput(), encoding));
            if (!Console.IsErrorRedirected)
                Console.SetError(Writer(Console.OpenStandardError(), encoding));
        }
        catch (IOException) { /* nothing left to write to; the caller's exit code still lands */ }
    }

    private static StreamWriter Writer(Stream stream, System.Text.Encoding? encoding) =>
        encoding is null
            ? new StreamWriter(stream) { AutoFlush = true }
            : new StreamWriter(stream, encoding) { AutoFlush = true };

    private static System.Text.Encoding? TryGetOutputEncoding()
    {
        try { return Console.OutputEncoding; }
        catch { return null; }
    }

    private static void Write(IntPtr conin, string text)
    {
        var records = new INPUT_RECORD[text.Length * 2];
        int i = 0;
        foreach (char c in text)
        {
            records[i++] = Key(c, down: true);
            records[i++] = Key(c, down: false);
        }
        WriteConsoleInput(conin, records, (uint)records.Length, out _);
    }

    private const string Ctrl_C = "";

    // A control character still needs its key identity: the terminal reads the virtual-key
    // code and modifier state, not only the character, to decide what it is looking at.
    private static INPUT_RECORD Key(char c, bool down) => new()
    {
        EventType = KEY_EVENT,
        bKeyDown = down ? 1 : 0,
        wRepeatCount = 1,
        wVirtualKeyCode = c switch { '\r' => 0x0D, '' => 0x1B, '' => 0x43, _ => 0 },
        wVirtualScanCode = 0,
        UnicodeChar = c,
        dwControlKeyState = c == '' ? LEFT_CTRL_PRESSED : 0,
    };

    /// <summary>
    /// A Ctrl+C written into a console is delivered to <em>every</em> process attached to it,
    /// and we are one of them the moment we attach — unguarded, the app is killed by the
    /// keystroke it just sent.
    ///
    /// This registers a handler that swallows it rather than setting the NULL "ignore Ctrl+C"
    /// attribute, because that attribute is inherited by child processes: it would silently
    /// take Ctrl+C away from every terminal the app launches, which is the one key an
    /// operator most needs while Claude is working.
    /// </summary>
    private static class IgnoreCtrlC
    {
        private static HandlerRoutine? _handler;   // held forever: the OS keeps the pointer

        public static void Arm()
        {
            if (_handler is not null) return;
            _handler = type => type is CTRL_C_EVENT or CTRL_BREAK_EVENT;
            SetConsoleCtrlHandler(_handler, true);
        }
    }

    // --- P/Invoke -----------------------------------------------------------

    private const ushort KEY_EVENT = 1;
    private const uint LEFT_CTRL_PRESSED = 0x0008;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint CTRL_C_EVENT = 0, CTRL_BREAK_EVENT = 1;
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_RECORD
    {
        public ushort EventType;
        public ushort Padding;
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public ushort UnicodeChar;
        public uint dwControlKeyState;
    }

    private delegate bool HandlerRoutine(uint controlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine? handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetConsoleTitle(System.Text.StringBuilder title, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInput(IntPtr handle, INPUT_RECORD[] buffer, uint length, out uint written);
}
