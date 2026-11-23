using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// A process in a hidden Windows pseudo console (ConPTY, Windows 10 1809+): no window, its terminal output is
    /// collected, keystrokes are written to it. Used to drive <c>claude attach</c> for a moment
    /// (<see cref="RemotePromptSender"/>). Disposing kills the process.
    /// </summary>
    internal sealed class PseudoConsole : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Coord { public short X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr handle);
        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
            IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
            ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const int StartfUseStdHandles = 0x00000100;
        private static readonly IntPtr ProcThreadAttributePseudoConsole = (IntPtr)0x00020016;

        private IntPtr _console;
        private IntPtr _attributes;
        private ProcessInformation _process;
        private FileStream? _input;
        private FileStream? _output;
        private readonly StringBuilder _buffer = new StringBuilder();
        private int _disposed;

        public int ProcessId => _process.dwProcessId;

        /// <summary>Starts <paramref name="commandLine"/> in a new pseudo console of the given size.</summary>
        public void Start(string commandLine, string? workingDirectory, short columns = 120, short rows = 40)
        {
            if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)
                || !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
                throw new Win32Exception();
            var hr = CreatePseudoConsole(new Coord { X = columns, Y = rows }, inputRead, outputWrite, 0, out _console);
            // the console holds its own references now
            inputRead.Dispose();
            outputWrite.Dispose();
            if (hr != 0)
            {
                inputWrite.Dispose();
                outputRead.Dispose();
                throw new Win32Exception(hr);
            }
            _input = new FileStream(inputWrite, FileAccess.Write);
            _output = new FileStream(outputRead, FileAccess.Read);

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            _attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(_attributes, 1, 0, ref size)
                || !UpdateProcThreadAttribute(_attributes, 0, ProcThreadAttributePseudoConsole, _console,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var startup = new StartupInfoEx();
            startup.StartupInfo.cb = Marshal.SizeOf(typeof(StartupInfoEx));
            // Never inherit the parent's std handles: with redirected handles (a host process, a test runner) the
            // child would write there instead of into the pseudo console (measured).
            startup.StartupInfo.dwFlags = StartfUseStdHandles;
            startup.lpAttributeList = _attributes;
            if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent, IntPtr.Zero, workingDirectory, ref startup, out _process))
                throw new Win32Exception();

            var reader = new Thread(ReadLoop) { IsBackground = true, Name = "PseudoConsole reader" };
            reader.Start();
        }

        private void ReadLoop()
        {
            var bytes = new byte[8192];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[8192];
            try
            {
                int n;
                while (_output != null && (n = _output.Read(bytes, 0, bytes.Length)) > 0)
                {
                    var count = decoder.GetChars(bytes, 0, n, chars, 0);
                    lock (_buffer)
                        _buffer.Append(chars, 0, count);
                }
            }
            catch { /* console closed */ }
        }

        /// <summary>Everything the process has written to its terminal so far (raw, with escape sequences).</summary>
        public string Output { get { lock (_buffer) return _buffer.ToString(); } }

        public int OutputLength { get { lock (_buffer) return _buffer.Length; } }

        /// <summary>Writes keystrokes / text (UTF-8) to the terminal input.</summary>
        public void Write(string text)
        {
            var input = _input ?? throw new InvalidOperationException("Not started.");
            var bytes = Encoding.UTF8.GetBytes(text);
            input.Write(bytes, 0, bytes.Length);
            input.Flush();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            // only this process, never its tree: whatever `claude attach` may have started (the CLI's daemon, which
            // hosts the background sessions) must live on
            try
            {
                if (_process.dwProcessId != 0)
                    using (var p = System.Diagnostics.Process.GetProcessById(_process.dwProcessId))
                        if (!p.HasExited) p.Kill();
            }
            catch { }
            if (_console != IntPtr.Zero)
                ClosePseudoConsole(_console); // also ends the reader (EOF)
            try { _input?.Dispose(); } catch { }
            try { _output?.Dispose(); } catch { }
            if (_attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attributes);
                Marshal.FreeHGlobal(_attributes);
            }
            if (_process.hProcess != IntPtr.Zero) CloseHandle(_process.hProcess);
            if (_process.hThread != IntPtr.Zero) CloseHandle(_process.hThread);
        }
    }
}
