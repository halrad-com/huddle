using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Huddle;

/// <summary>
/// Windows-only. Writes a line of text into another console process's input
/// buffer so it appears as if the user typed and submitted it. Used to wake
/// Claude Code sessions with a new turn without any operator keystroke.
///
/// Architecture: every injection runs in a short-lived helper child process.
/// The parent huddle never touches its own console — doing so (FreeConsole
/// then trying to reattach to parent) is fragile, because huddle may have
/// been launched without a live parent console (Explorer double-click,
/// published single-file launcher, detached launch, etc). Losing the parent
/// console and failing to reattach leaves .NET's cached stdout handles
/// pointing at a dead console and the process dies on the next
/// Console.WriteLine.
///
/// <see cref="Inject"/> spawns <c>huddle.exe --inject &lt;pid&gt; &lt;b64&gt;</c>
/// with <c>CreateNoWindow = true</c> — the child starts with no inherited
/// console, AttachConsoles to the target's console, WriteConsoleInput's the
/// keystrokes, and exits. Windows cleans up the child's console state; the
/// parent is untouched.
///
/// <see cref="InjectInProcess"/> is the in-child implementation — only called
/// from Program.Main when <c>--inject</c> is the first argument.
/// </summary>
public static class PromptInjector
{
    private const uint GENERIC_READ = 0x80000000u;
    private const uint GENERIC_WRITE = 0x40000000u;
    private const uint FILE_SHARE_READ = 0x00000001u;
    private const uint FILE_SHARE_WRITE = 0x00000002u;
    private const uint OPEN_EXISTING = 3u;
    private const ushort KEY_EVENT = 0x0001;
    private const ushort VK_RETURN = 0x0D;
    private const int HELPER_TIMEOUT_MS = 5000;

    // Exit code the helper returns when it declines to inject because the
    // operator is actively at the target console (see OperatorBusy). Distinct
    // from success (0) and failure (1) so the parent treats it as "not
    // delivered, retry later" rather than an error.
    private const int HELD_EXIT = 3;

    // A target console that is in the foreground is treated as "operator busy"
    // — injecting there would interleave with whatever the operator is typing
    // and the submit Enter would fire their half-typed line. We hold until they
    // leave it or go idle. This grace lets a *focused but walked-away* console
    // still receive its nudge: only genuine keyboard/mouse silence this long
    // counts as abandoned. Active typing resets it every keystroke, and normal
    // think-pauses while composing are far shorter, so a multi-minute compose
    // never trips it.
    private const uint AbandonedIdleMs = 180_000;

    // Submit timing. Injected text arrives as a keystroke burst, which the
    // recipient's editor coalesces as a paste; an Enter inside that window
    // becomes a literal newline, not a submit. Drain long enough to exit the
    // window, then follow with a second Enter as a no-op-if-submitted retry.
    private const int PasteWindowDrainMs = 350;
    private const int SecondEnterDelayMs = 300;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    /// <summary>
    /// Inject <paramref name="text"/> as a submitted line into the console owned
    /// by the process with PID <paramref name="targetPid"/>. Returns true on success.
    /// Newlines inside <paramref name="text"/> are collapsed to spaces so a single
    /// call always produces exactly one submitted turn.
    ///
    /// Runs a throwaway child process so the caller's console is never disturbed.
    ///
    /// Unless <paramref name="force"/> is set, the helper declines to inject when
    /// the target console is in the foreground (the operator is likely typing in
    /// it): it returns false without disturbing that half-typed line, and the
    /// caller is expected to retry later (auto-nudges retry via IpcManager's
    /// periodic inbox rescan). Explicit operator actions (the `say` verb) pass
    /// force=true to deliver immediately regardless.
    /// </summary>
    public static bool Inject(int targetPid, string text, Action<string> log, bool force = false)
    {
        if (targetPid <= 0)
        {
            log($"PromptInjector: invalid PID {targetPid}");
            return false;
        }
        if (string.IsNullOrEmpty(text))
        {
            log("PromptInjector: empty text, skipping");
            return false;
        }

        // Collapse newlines so we can't accidentally submit a multi-line prompt
        // mid-stream. A single call = a single turn.
        text = text.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            log("PromptInjector: cannot resolve self exe path (Environment.ProcessPath is null)");
            return false;
        }

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = force ? $"--inject {targetPid} {b64} --force" : $"--inject {targetPid} {b64}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        try
        {
            using var child = Process.Start(psi);
            if (child == null)
            {
                log("PromptInjector: helper child failed to start");
                return false;
            }
            if (!child.WaitForExit(HELPER_TIMEOUT_MS))
            {
                try { child.Kill(); } catch { /* best effort */ }
                log($"PromptInjector: helper timed out after {HELPER_TIMEOUT_MS}ms (PID {targetPid})");
                return false;
            }
            if (child.ExitCode == HELD_EXIT)
            {
                // Operator is at the target console — held on purpose, not an
                // error. Silent: the caller retries and the mail stays visibly
                // in the inbox, so logging every poll would just be spam.
                return false;
            }
            if (child.ExitCode != 0)
            {
                string err;
                try { err = child.StandardError.ReadToEnd().Trim(); }
                catch { err = "<stderr unavailable>"; }
                log($"PromptInjector: helper exit={child.ExitCode} PID {targetPid} — {err}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log($"PromptInjector: helper spawn failed — {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The in-child-process implementation of the actual console injection.
    /// Only called from Program.Main's --inject fast path. Does FreeConsole +
    /// AttachConsole(target) + CreateFile(CONIN$) + WriteConsoleInput, then
    /// exits. Does NOT try to restore any prior console — the child is
    /// exiting anyway, and the parent never had one clobbered.
    /// </summary>
    /// <returns>
    /// Process exit code: 0 = delivered, <see cref="HELD_EXIT"/> = declined
    /// because the operator is at the target console (retry later), 1 = failure.
    /// </returns>
    public static int InjectInProcess(int targetPid, string text, Action<string> log, bool force = false)
    {
        if (targetPid <= 0)
        {
            log($"PromptInjector: invalid PID {targetPid}");
            return 1;
        }
        if (string.IsNullOrEmpty(text))
        {
            log("PromptInjector: empty text, skipping");
            return 1;
        }

        // Detach from any console the OS handed the child (CreateNoWindow
        // should mean no console, but FreeConsole is a no-op if so).
        FreeConsole();

        if (!AttachConsole((uint)targetPid))
        {
            var err = Marshal.GetLastWin32Error();
            log($"PromptInjector: AttachConsole({targetPid}) failed (win32 err={err})");
            return 1;
        }

        // Hold if the operator is actively at this console — injecting now would
        // land in their half-typed line and the submit Enter would fire it. Must
        // run AFTER AttachConsole so GetConsoleWindow() resolves the *target's*
        // console window. force=true (the `say` verb) bypasses the hold.
        if (!force && OperatorBusy())
        {
            log($"PromptInjector: held — target console is foreground (operator active)");
            return HELD_EXIT;
        }

        // AttachConsole does not modify the standard handles, so we must open
        // CONIN$ explicitly.
        var hIn = CreateFileW(
            "CONIN$",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (hIn == INVALID_HANDLE_VALUE)
        {
            var err = Marshal.GetLastWin32Error();
            log($"PromptInjector: CreateFile(CONIN$) failed PID {targetPid} (win32 err={err})");
            return 1;
        }

        try
        {
            // Split into text and submit records and write them separately. The
            // console input buffer fills up: a single WriteConsoleInputW with
            // 1000+ records can succeed-with-partial-write, silently dropping
            // the trailing records. For us that meant the submit Enter was
            // dropped — text appeared in the recipient's prompt and just sat
            // there until a human pressed Enter manually. Chunking text and
            // pacing it gives ConPTY time to drain; sending Enter as its own
            // tiny call guarantees the submit always lands.
            var textRecords = BuildTextRecords(text);
            if (!WriteAllChunked(hIn, textRecords, targetPid, log)) return 1;

            // Pause long enough that the Enter arrives OUTSIDE the recipient's
            // paste-coalescing window. Claude Code's input layer treats a rapid
            // burst of keystrokes as a paste; an Enter that lands inside that
            // window is inserted as a literal newline instead of submitting
            // (2026-07-04: a Claude Code update widened the window past the old
            // 20ms pause — text sat in the composer until a human pressed Enter).
            Thread.Sleep(PasteWindowDrainMs);

            var enterRecords = BuildEnterRecords();
            if (!WriteAllChunked(hIn, enterRecords, targetPid, log)) return 1;

            // Second-chance Enter: if the first was still coalesced into the
            // paste as a newline, this isolated keystroke submits the buffer;
            // if the first already submitted, this lands on an empty composer
            // and is a no-op. Idempotent either way.
            Thread.Sleep(SecondEnterDelayMs);
            if (!WriteAllChunked(hIn, BuildEnterRecords(), targetPid, log)) return 1;

            return 0;
        }
        finally
        {
            CloseHandle(hIn);
            // Intentionally do NOT FreeConsole or reattach to anything — the
            // child is about to exit and the OS will clean up.
        }
    }

    // True when the operator is actively at the target console: the console the
    // child is attached to is the system foreground window AND there has been
    // recent global keyboard/mouse input. Foreground-but-long-idle (walked away)
    // is NOT busy, so a nudge left holding still lands once the console has sat
    // untouched for AbandonedIdleMs. Called only from the child, after
    // AttachConsole(target), so GetConsoleWindow() is the target's window.
    private static bool OperatorBusy()
    {
        var console = GetConsoleWindow();
        if (console == IntPtr.Zero) return false;      // no window — can't be typing at it
        if (GetForegroundWindow() != console) return false; // target not focused — safe to inject
        return GetIdleMilliseconds() < AbandonedIdleMs; // focused + recent input = actively used
    }

    private static uint GetIdleMilliseconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0; // unknown → treat as active (hold, don't stomp)
        return unchecked(GetTickCount() - lii.dwTime);
    }

    // Write `records` to the console input handle. Splits into 256-record
    // chunks with a short sleep between chunks so the consumer can drain.
    // If a single WriteConsoleInputW reports a short write, resubmit the
    // unwritten tail rather than declaring success. Returns false only if a
    // chunk genuinely fails or we can't make progress after several retries.
    private static bool WriteAllChunked(IntPtr hIn, INPUT_RECORD[] records, int targetPid, Action<string> log)
    {
        const int ChunkSize = 256;
        const int MaxStallRetries = 8;

        int offset = 0;
        while (offset < records.Length)
        {
            int remaining = records.Length - offset;
            int want = Math.Min(ChunkSize, remaining);

            var chunk = new INPUT_RECORD[want];
            Array.Copy(records, offset, chunk, 0, want);

            int stalls = 0;
            int progress = 0;
            while (progress < want)
            {
                var slice = progress == 0 ? chunk : chunk[progress..];
                if (!WriteConsoleInputW(hIn, slice, (uint)slice.Length, out uint written))
                {
                    var err = Marshal.GetLastWin32Error();
                    log($"PromptInjector: WriteConsoleInput failed PID {targetPid} (win32 err={err})");
                    return false;
                }
                if (written == 0)
                {
                    stalls++;
                    if (stalls > MaxStallRetries)
                    {
                        log($"PromptInjector: input buffer stalled — wrote 0 records {MaxStallRetries}x PID {targetPid}");
                        return false;
                    }
                    Thread.Sleep(10);
                    continue;
                }
                stalls = 0;
                progress += (int)written;
            }

            offset += want;
            if (offset < records.Length) Thread.Sleep(5);
        }
        return true;
    }

    private static INPUT_RECORD[] BuildTextRecords(string text)
    {
        // Two records per character (down + up). Enter records are emitted
        // separately so the submit always lands even if the text overruns the
        // console input buffer mid-write.
        var records = new INPUT_RECORD[text.Length * 2];
        int r = 0;
        foreach (var ch in text)
        {
            records[r++] = MakeKeyRecord(ch, keyDown: true);
            records[r++] = MakeKeyRecord(ch, keyDown: false);
        }
        return records;
    }

    private static INPUT_RECORD[] BuildEnterRecords()
    {
        return new[]
        {
            MakeReturnRecord(keyDown: true),
            MakeReturnRecord(keyDown: false),
        };
    }

    private static INPUT_RECORD MakeKeyRecord(char ch, bool keyDown)
    {
        return new INPUT_RECORD
        {
            EventType = KEY_EVENT,
            KeyEvent = new KEY_EVENT_RECORD
            {
                bKeyDown = keyDown ? 1 : 0,
                wRepeatCount = 1,
                wVirtualKeyCode = 0,
                wVirtualScanCode = 0,
                UnicodeChar = ch,
                dwControlKeyState = 0,
            }
        };
    }

    private static INPUT_RECORD MakeReturnRecord(bool keyDown)
    {
        // wVirtualScanCode must be the real Enter scan code (0x1C on US
        // keyboards) for ConPTY to translate the keystroke into a "\r" byte
        // on the child TTY. With scan code 0, ConPTY treats this as a
        // "synthetic" event and may drop it, leaving text in the prompt
        // but never submitting.
        return new INPUT_RECORD
        {
            EventType = KEY_EVENT,
            KeyEvent = new KEY_EVENT_RECORD
            {
                bKeyDown = keyDown ? 1 : 0,
                wRepeatCount = 1,
                wVirtualKeyCode = VK_RETURN,
                wVirtualScanCode = 0x1C,
                UnicodeChar = '\r',
                dwControlKeyState = 0,
            }
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteConsoleInputW")]
    private static extern bool WriteConsoleInputW(
        IntPtr hConsoleInput,
        INPUT_RECORD[] lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsWritten);

    // INPUT_RECORD is a tagged union. The EventType selects which variant is
    // active. We only ever emit KEY_EVENTs, so we declare only that variant.
    // The native union begins at offset 4 (ushort EventType + padding for
    // DWORD alignment of the union).
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;          // Win32 BOOL is 4 bytes
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;      // WCHAR, 2 bytes; native uChar union (also 2)
        public uint dwControlKeyState;
    }
}
