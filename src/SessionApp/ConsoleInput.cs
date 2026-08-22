using System.Runtime.InteropServices;
using System.Threading;

namespace SessionApp;

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
internal static class ConsoleInput
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
    /// Borrow the console owning <paramref name="pid"/> for the duration of <paramref name="body"/>,
    /// which is handed a writer for that console's input buffer.
    /// </summary>
    private static bool Borrow(int pid, Func<Action<string>, bool> body)
    {
        lock (Gate)
        {
            IgnoreCtrlC.Arm();

            FreeConsole();                       // drop whatever a previous borrow left attached
            if (!AttachConsole((uint)pid)) return false;
            try
            {
                var conin = CreateFileW("CONIN$", GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (conin == InvalidHandle) return false;

                try { return body(text => Write(conin, text)); }
                finally { CloseHandle(conin); }
            }
            catch { return false; }
            finally { FreeConsole(); }
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInput(IntPtr handle, INPUT_RECORD[] buffer, uint length, out uint written);
}
