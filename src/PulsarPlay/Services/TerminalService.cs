using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarPlay.Services;

public class TerminalService : IDisposable
{
    private Process? _process;
    private bool _isRunning;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public event Action<string>? OutputReceived;
    public event Action<string>? ErrorReceived;
    public event Action? ProcessExited;

    public bool IsRunning
    {
        get { lock (_lock) return _isRunning; }
    }

    public async Task StartAsync(string shell = "powershell.exe", string? workingDir = null)
    {
        Stop();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = "-NoLogo -NoExit",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = !string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir) ? workingDir : Environment.CurrentDirectory
        };

        if (!string.IsNullOrEmpty(workingDir))
            psi.WorkingDirectory = workingDir;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (s, e) => ProcessExited?.Invoke();

        _process.Start();

        lock (_lock) _isRunning = true;

        _ = ReadOutputAsync(_process.StandardOutput, OutputReceived, token);
        _ = ReadOutputAsync(_process.StandardError, ErrorReceived, token);

        await Task.CompletedTask;
    }

    private async Task ReadOutputAsync(StreamReader reader, Action<string>? handler, CancellationToken token)
    {
        var buffer = new char[1024];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                var text = new string(buffer, 0, bytesRead);
                handler?.Invoke(text);
            }
        }
        catch { }
    }

    public void SendCommand(string command)
    {
        if (_process?.HasExited != false || _process?.StandardInput == null) return;

        try
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }
        catch { }
    }

    public void SendInput(string text)
    {
        if (_process?.HasExited != false || _process?.StandardInput == null) return;

        try
        {
            _process.StandardInput.Write(text);
            _process.StandardInput.Flush();
        }
        catch { }
    }

    public void Resize(int width, int height)
    {
        try
        {
            if (_process?.HasExited == false)
            {
                SetConsoleSize(_process.Id, width, height);
            }
        }
        catch { }
    }

    private void SetConsoleSize(int pid, int width, int height)
    {
        try
        {
            var conout = CreateFile(
                "CONOUT$",
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (conout != IntPtr.Zero)
            {
                var csbi = new CONSOLE_SCREEN_BUFFER_INFO();
                if (GetConsoleScreenBufferInfo(conout, ref csbi))
                {
                    csbi.dwSize.X = (short)width;
                    csbi.dwSize.Y = (short)height;
                    SetConsoleScreenBufferSize(conout, csbi.dwSize);
                }
                CloseHandle(conout);
            }
        }
        catch { }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;
        }

        try
        {
            _cts?.Cancel();
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch { }

        _process?.Dispose();
        _process = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 0x00000003;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, ref CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, COORD dwSize);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public short wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }
}