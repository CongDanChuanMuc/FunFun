// ===========================================================================
// Credited by TapLamDang
// Public, free at https://www.facebook.com/profile.php?id=61596126010538
// If you paid for this, you are getting scammed.
// ===========================================================================

// v250run.exe - single-file launcher that reproduces the v250 AutoManager
// activation bypass.
//
// Place this executable in the same directory as AutoManager.exe and run it.
// By default it runs with no console window: double-click it and the activated
// AutoManager opens. Pass --console to get a window with live output. Either
// way a transcript is written to launcher.log in the evidence directory.
// It extracts the embedded StartupHook.dll to %TEMP%\v250run, starts
// AutoManager.exe with DOTNET_STARTUP_HOOKS pointing at that assembly, waits
// for the activated main form, verifies the hook log, captures a screenshot,
// and drives File -> Run Control Manager through UI Automation.
//
// The hook detours
//   System.Net.Http.HttpClient.SendAsync(HttpRequestMessage, HttpCompletionOption, CancellationToken)
// with a 5-byte E9 rel32 jump and returns a synthetic HTTP 200 whose body is
// {"status":"success",...}. No shipped binary is modified, no TLS is broken,
// and no DNS/hosts/certificate/proxy setting is touched.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Automation;

internal sealed class Options
{
    public bool Help;
    public bool ShowConsole;
    public bool NoPause;
    public bool NoMessageBox;
    public bool Force;
    public bool Stop;
    public bool SkipControlManager;
    public bool StrictHash;
    public int TimeoutSeconds = 45;
    public string EvidenceDir;
    public string HookLog;

    public static Options Parse(string[] args)
    {
        Options options = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--console":
                case "--show-console":
                case "-c":
                    options.ShowConsole = true;
                    break;
                case "--no-pause":
                    options.NoPause = true;
                    break;
                case "--no-message-box":
                    options.NoMessageBox = true;
                    break;
                case "--force":
                    options.Force = true;
                    break;
                case "--stop":
                    options.Stop = true;
                    break;
                case "--no-control-manager":
                    options.SkipControlManager = true;
                    break;
                case "--strict-hash":
                    options.StrictHash = true;
                    break;
                case "--timeout":
                    options.TimeoutSeconds = int.Parse(RequireValue(args, ref i, "--timeout"));
                    break;
                case "--evidence":
                    options.EvidenceDir = RequireValue(args, ref i, "--evidence");
                    break;
                case "--hook-log":
                    options.HookLog = RequireValue(args, ref i, "--hook-log");
                    break;
                case "--help":
                case "-h":
                case "/?":
                    options.Help = true;
                    break;
                default:
                    throw new ArgumentException("Unknown option: " + arg);
            }
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException(option + " requires a value.");
        }
        index++;
        return args[index];
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    // bInheritHandles = false keeps the target from inheriting the caller's
    // stdout/stderr pipe handles. Without that, a shell that captures this
    // launcher's output would block until AutoManager itself exits.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcess(
        string lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    public const int AttachParentProcess = -1;
    public const uint StdOutputHandle = 0xFFFFFFF5; // (DWORD)-11

    public const uint MB_ICONERROR = 0x00000010;
    public const uint MB_SETFOREGROUND = 0x00010000;
    public const uint MB_TOPMOST = 0x00040000;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetConsoleCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleOutputCP(uint codePageId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCP(uint codePageId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(uint stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleScreenBufferSize(IntPtr consoleOutput, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleWindowInfo(
        IntPtr consoleOutput, bool absolute, ref SMALL_RECT window);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}

internal static class Program
{
    private const string TargetExeName = "AutoManager.exe";
    private const string ControlManagerExeName = "ControlManager.exe";
    private const string HookResourceName = "StartupHook.dll";

    private const string ExpectedAutoManagerSha256 =
        "1FED2DAE01932139CAD241B55789D4F23D1B19F10C0B038667906E3005D71FF7";

    private static int Main(string[] args)
    {
        Options options;
        string parseError = null;

        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            options = new Options();
            parseError = ex.Message;
        }

        // Nothing writes to Console before this point: a GUI subsystem process
        // has no console of its own, and the first Console access caches the
        // (missing) handle. Attaching first makes SetOut/SetError effective.
        ConsoleSession session = ConsoleSession.Open(
            options.ShowConsole || options.Help || parseError != null);

        Output.EnableConsole(session.HasConsole);

        int exitCode = 1;
        string failure = parseError;

        try
        {
            if (parseError != null)
            {
                Output.ErrorLine("error: " + parseError);
                Output.WriteLine();
                PrintUsage();
                exitCode = 2;
            }
            else if (options.Help)
            {
                PrintUsage();
                exitCode = 0;
            }
            else
            {
                exitCode = Run(options);
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            Output.WriteLine();
            Output.ErrorLine("[FAIL] " + ex.Message);
        }
        finally
        {
            // Only a console this process created needs holding open; when the
            // console belongs to the caller's shell, that shell already does.
            if (session.IsNewConsole && !options.NoPause)
            {
                Output.WriteLine();
                Console.Write("Press Enter to close...");
                try { Console.ReadLine(); }
                catch { }
            }

            session.Dispose();
        }

        // Without a console there is nowhere for the failure to be read, so the
        // windowless default reports it with a dialog instead.
        if (failure != null && !session.HasConsole && !options.NoMessageBox)
        {
            ReportFailure(failure);
        }

        return exitCode;
    }

    private static void ReportFailure(string message)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine(message.Trim());
        builder.AppendLine();
        builder.AppendLine("The v250 activation bypass did not complete.");
        if (!string.IsNullOrEmpty(Output.LogPath))
        {
            builder.AppendLine();
            builder.AppendLine("Log: " + Output.LogPath);
        }
        builder.AppendLine();
        builder.AppendLine("Run \"v250run.exe --console\" for the full output.");

        NativeMethods.MessageBox(
            IntPtr.Zero,
            builder.ToString(),
            "v250run - failed",
            NativeMethods.MB_ICONERROR | NativeMethods.MB_SETFOREGROUND | NativeMethods.MB_TOPMOST);
    }

    private static int Run(Options options)
    {
        string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string targetExe = Path.Combine(exeDir, TargetExeName);

        // Resolve the evidence directory up front: a windowless run has no
        // other place to leave a record, including when it fails early.
        string evidenceDir = string.IsNullOrEmpty(options.EvidenceDir)
            ? Path.Combine(Path.GetTempPath(), "v250run")
            : Path.GetFullPath(options.EvidenceDir);

        try { Directory.CreateDirectory(evidenceDir); }
        catch { }
        Output.SetLogPath(Path.Combine(evidenceDir, "launcher.log"));

        Output.WriteLine("============================================================");
        Output.WriteLine(" v250 AutoManager activation bypass - automated reproduction");
        Output.WriteLine("============================================================");
        Output.WriteLine(" launcher : " + Assembly.GetExecutingAssembly().Location);
        Output.WriteLine(" target   : " + targetExe);
        Output.WriteLine(" console  : " +
                         (Output.ConsoleEnabled ? "attached" : "none (windowless)"));
        Output.WriteLine();

        if (!File.Exists(targetExe))
        {
            throw new FileNotFoundException(
                "AutoManager.exe was not found next to this launcher. " +
                "Place v250run.exe in the same folder as AutoManager.exe.",
                targetExe);
        }

        Directory.CreateDirectory(evidenceDir);

        string hookLog = string.IsNullOrEmpty(options.HookLog)
            ? Path.Combine(evidenceDir, "hook.log")
            : Path.GetFullPath(options.HookLog);
        string screenshotPath = Path.Combine(evidenceDir, "main-ui.png");
        string summaryPath = Path.Combine(evidenceDir, "run.txt");

        // 1. Make sure an earlier instance from this folder is not in the way.
        //    The hook assembly cannot be overwritten while a previous
        //    AutoManager has it mapped. Processes started from other folders are
        //    left alone.
        List<Process> existing = FindTargetProcesses(exeDir);
        if (existing.Count > 0)
        {
            if (!options.Force)
            {
                throw new InvalidOperationException(
                    "v250 processes started from this folder are already running (" +
                    Describe(existing) + "). Close them or pass --force.");
            }

            Output.WriteLine("[0/4] Stopping existing processes: " + Describe(existing));
            foreach (Process process in existing)
            {
                try { process.Kill(); }
                catch { }
            }
            foreach (Process process in existing)
            {
                try { process.WaitForExit(5000); }
                catch { }
            }
            Thread.Sleep(500);
        }

        // 2. Verify the target artifact.
        string targetHash = Sha256(targetExe);
        Output.WriteLine("[1/4] AutoManager.exe SHA-256 " + targetHash);
        if (!string.Equals(targetHash, ExpectedAutoManagerSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (options.StrictHash)
            {
                throw new InvalidOperationException(
                    "AutoManager.exe hash mismatch. Expected " + ExpectedAutoManagerSha256 + ".");
            }
            Warn("AutoManager.exe differs from the assessed build; continuing.");
        }

        // 3. Extract the embedded hook and start the target.
        string hookPath = ExtractHook();
        Output.WriteLine("      hook: " + hookPath + " (" + new FileInfo(hookPath).Length + " bytes)");
        Output.WriteLine("      log : " + hookLog);

        try { if (File.Exists(hookLog)) File.Delete(hookLog); }
        catch { }

        Output.WriteLine();
        Output.WriteLine("[2/4] Starting AutoManager.exe with the startup hook...");
        Process target = StartDetached(targetExe, exeDir, hookPath, hookLog);
        Output.WriteLine("      pid " + target.Id);

        // 4. Wait for the activated UI.
        IntPtr window = WaitForWindow(target, options.TimeoutSeconds);
        string title = WaitForTitle(target, 15);

        Output.WriteLine("      title: " + title);
        bool titleMatches =
            title.StartsWith("AutoPro 250 -", StringComparison.Ordinal) &&
            title.IndexOf("[Client: ", StringComparison.Ordinal) >= 0 &&
            title.IndexOf(" - v", StringComparison.Ordinal) >= 0;

        if (!titleMatches)
        {
            throw new InvalidOperationException(
                "Window title does not match the activated form: \"" + title + "\".");
        }
        Output.WriteLine("[PASS] Activated UI reached without a valid key.");

        // 5. The hook log must show the intercepted license call.
        string log = ReadTextWithRetry(hookLog, 10);
        if (log.IndexOf("SendAsyncHook uri=", StringComparison.Ordinal) < 0 ||
            log.IndexOf("check-key", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "The hook log does not show the intercepted check-key request: " + hookLog);
        }
        Output.WriteLine("[PASS] Hook intercepted the license endpoint.");
        foreach (string line in log.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.IndexOf("patched", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("uri=", StringComparison.Ordinal) >= 0)
            {
                Output.WriteLine("      " + line);
            }
        }

        // 6. Evidence: screenshot of the activated window.
        string screenshot = null;
        try
        {
            CaptureWindow(window, screenshotPath);
            screenshot = screenshotPath;
            Output.WriteLine("[PASS] Screenshot: " + screenshotPath);
        }
        catch (Exception ex)
        {
            Warn("Screenshot failed: " + ex.Message);
        }

        // 7. Optional child verification: File -> Run Control Manager.
        Process controlManager = null;
        string controlManagerVia = null;

        if (!options.SkipControlManager)
        {
            Output.WriteLine();
            Output.WriteLine("[3/4] Invoking File -> Run Control Manager...");
            try
            {
                controlManager = InvokeRunControlManager(window, options.TimeoutSeconds);
                controlManagerVia = "File -> Run Control Manager";
            }
            catch (Exception ex)
            {
                Warn("UI Automation path failed: " + ex.Message);
                Warn("Falling back to starting ControlManager.exe directly.");
                controlManager = StartControlManager(exeDir);
                controlManagerVia = "direct start (fallback)";
            }

            string controlTitle = WaitForTitle(controlManager, 15);
            Output.WriteLine("[PASS] ControlManager.exe started: pid " + controlManager.Id +
                              " title \"" + controlTitle + "\" (" + controlManagerVia + ")");
        }
        else
        {
            Output.WriteLine();
            Output.WriteLine("[3/4] ControlManager step skipped (--no-control-manager).");
        }

        // 8. Summary.
        string[] summary = new string[]
        {
            "v250 activation bypass reproduction",
            "timestamp:      " + DateTime.Now.ToString("o"),
            "target:         " + targetExe,
            "sha256:         " + targetHash,
            "hook:           " + hookPath,
            "hook log:       " + hookLog,
            "pid:            " + target.Id,
            "window title:   " + title,
            "activation:     PASS",
            "controlmanager: " + (controlManager == null
                ? "not launched"
                : "pid " + controlManager.Id + " via " + controlManagerVia),
            "screenshot:     " + (screenshot == null ? "not captured" : screenshot),
            "launcher log:   " + (Output.LogPath == null ? "not written" : Output.LogPath),
            "errors:         none"
        };

        Output.WriteLine();
        Output.WriteLine("------------------------------------------------------------");
        foreach (string line in summary)
        {
            Output.WriteLine(" " + line);
        }
        Output.WriteLine("------------------------------------------------------------");
        Output.WriteLine(" RESULT: BYPASS REPRODUCED");
        Output.WriteLine();
        Output.WriteLine("[4/4] Evidence directory: " + evidenceDir);

        try { File.WriteAllLines(summaryPath, summary, new UTF8Encoding(true)); }
        catch (Exception ex) { Warn("Could not write " + summaryPath + ": " + ex.Message); }

        if (options.Stop)
        {
            Output.WriteLine();
            Output.WriteLine("Stopping v250 processes started from this folder (--stop)...");
            StopTargetProcesses(exeDir);
        }
        else
        {
            Output.WriteLine("AutoManager and ControlManager were left running.");
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Output.WriteLine("v250run.exe - reproduce the v250 AutoManager activation bypass");
        Output.WriteLine();
        Output.WriteLine("Usage: v250run.exe [options]");
        Output.WriteLine();
        Output.WriteLine("Place this file next to AutoManager.exe and run it. It extracts the");
        Output.WriteLine("embedded StartupHook.dll, starts AutoManager.exe with the hook, verifies");
        Output.WriteLine("the activated UI, captures a screenshot, and launches Control Manager.");
        Output.WriteLine();
        Output.WriteLine("By default no console window is created: double-click the exe and the");
        Output.WriteLine("activated AutoManager opens. A transcript is still written to");
        Output.WriteLine("launcher.log in the evidence directory.");
        Output.WriteLine();
        Output.WriteLine("Options:");
        Output.WriteLine("  --console, -c         show a console window with live output");
        Output.WriteLine("  --no-pause            do not wait for Enter when this exe owns the console");
        Output.WriteLine("  --no-message-box      do not show an error dialog when there is no console");
        Output.WriteLine("  --force               stop running v250 processes from this folder first");
        Output.WriteLine("  --stop                stop AutoManager/ControlManager after verification");
        Output.WriteLine("  --no-control-manager  skip the File -> Run Control Manager step");
        Output.WriteLine("  --strict-hash         fail if AutoManager.exe is not the assessed build");
        Output.WriteLine("  --timeout <seconds>   window/child timeout (default 45)");
        Output.WriteLine("  --evidence <dir>      directory for hook.log, run.txt, main-ui.png");
        Output.WriteLine("  --hook-log <path>     explicit hook log path");
        Output.WriteLine("  --help                show this help");
        Output.WriteLine();
        Output.WriteLine("Exit codes: 0 = bypass reproduced, 1 = failed, 2 = bad arguments.");
    }

    private static List<Process> FindTargetProcesses(string exeDir)
    {
        List<Process> found = new List<Process>();
        foreach (string name in new string[] { "AutoManager", "AutoPro250", "ControlManager" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                if (IsFromDirectory(process, exeDir))
                {
                    found.Add(process);
                }
            }
        }
        return found;
    }

    // Only processes whose image lives in the launcher's own folder are
    // considered v250 processes. A copy running from somewhere else belongs to
    // a different session and is never stopped. Anything that cannot be
    // inspected is left alone rather than guessed at.
    private static bool IsFromDirectory(Process process, string directory)
    {
        try
        {
            string imagePath = process.MainModule.FileName;
            string imageDir = Path.GetDirectoryName(imagePath);
            if (string.IsNullOrEmpty(imageDir))
            {
                return false;
            }

            string a = Path.GetFullPath(imageDir).TrimEnd(Path.DirectorySeparatorChar);
            string b = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Describe(List<Process> processes)
    {
        StringBuilder builder = new StringBuilder();
        foreach (Process process in processes)
        {
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(process.ProcessName).Append(':').Append(process.Id);
        }
        return builder.ToString();
    }

    private static void StopTargetProcesses(string exeDir)
    {
        List<Process> targets = new List<Process>();
        foreach (string name in new string[] { "ControlManager", "AutoPro250", "AutoManager" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                if (IsFromDirectory(process, exeDir))
                {
                    targets.Add(process);
                }
            }
        }

        foreach (Process process in targets)
        {
            try { process.Kill(); }
            catch { }
        }

        foreach (Process process in targets)
        {
            try { process.WaitForExit(5000); }
            catch { }
        }
    }

    private static string Sha256(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            byte[] hash = sha.ComputeHash(stream);
            StringBuilder builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash)
            {
                builder.Append(value.ToString("X2"));
            }
            return builder.ToString();
        }
    }

    private static string ExtractHook()
    {
        byte[] bytes = ReadHookBytes();

        string directory = Path.Combine(Path.GetTempPath(), "v250run");
        try
        {
            return WriteHook(directory, bytes);
        }
        catch (IOException)
        {
            return WriteHook(FallbackHookDirectory(), bytes);
        }
        catch (UnauthorizedAccessException)
        {
            return WriteHook(FallbackHookDirectory(), bytes);
        }
    }

    private static string FallbackHookDirectory()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "v250run-" + Process.GetCurrentProcess().Id);
    }

    private static string WriteHook(string directory, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, HookResourceName);

        if (File.Exists(path))
        {
            try { File.SetAttributes(path, FileAttributes.Normal); }
            catch { }
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] ReadHookBytes()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        Stream stream = assembly.GetManifestResourceStream(HookResourceName);

        if (stream == null)
        {
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(HookResourceName, StringComparison.OrdinalIgnoreCase))
                {
                    stream = assembly.GetManifestResourceStream(name);
                    break;
                }
            }
        }

        if (stream == null)
        {
            throw new InvalidOperationException(
                "Embedded " + HookResourceName + " not found. Resources: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        }

        using (stream)
        using (MemoryStream buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    private static IntPtr WaitForWindow(Process process, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(400);

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "AutoManager.exe exited with code " + process.ExitCode +
                    " before showing a window.");
            }

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }
        }

        throw new TimeoutException("No main window after " + timeoutSeconds + " seconds.");
    }

    private static string WaitForTitle(Process process, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    process.ProcessName + " exited with code " + process.ExitCode + ".");
            }

            string title = process.MainWindowTitle;
            if (!string.IsNullOrEmpty(title))
            {
                return title;
            }

            Thread.Sleep(300);
        }

        throw new TimeoutException(
            process.ProcessName + " did not expose a window title within " +
            timeoutSeconds + " seconds.");
    }

    private static string ReadTextWithRetry(string path, int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }
            }
            catch (IOException)
            {
            }

            Thread.Sleep(300);
        }

        return string.Empty;
    }

    private static void CaptureWindow(IntPtr window, string path)
    {
        NativeMethods.RECT rect;
        if (!NativeMethods.GetWindowRect(window, out rect))
        {
            throw new InvalidOperationException("GetWindowRect failed.");
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Window has no drawable area.");
        }

        using (Bitmap bitmap = new Bitmap(width, height))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                IntPtr deviceContext = graphics.GetHdc();
                bool printed;
                try
                {
                    // PrintWindow renders the window itself, so the capture stays
                    // valid when another window overlaps it.
                    printed = NativeMethods.PrintWindow(window, deviceContext, 2);
                }
                finally
                {
                    graphics.ReleaseHdc(deviceContext);
                }

                if (!printed)
                {
                    graphics.CopyFromScreen(
                        rect.Left, rect.Top, 0, 0, new Size(width, height));
                }
            }

            bitmap.Save(path, ImageFormat.Png);
        }
    }

    private static Process InvokeRunControlManager(IntPtr window, int timeoutSeconds)
    {
        try { NativeMethods.SetForegroundWindow(window); }
        catch { }

        AutomationElement root = AutomationElement.FromHandle(window);
        if (root == null)
        {
            throw new InvalidOperationException("Could not obtain the UI Automation root.");
        }

        Condition menuBarCondition = new PropertyCondition(
            AutomationElement.ControlTypeProperty, ControlType.MenuBar);
        AutomationElement menuBar = root.FindFirst(TreeScope.Descendants, menuBarCondition);
        if (menuBar == null)
        {
            throw new InvalidOperationException("Menu bar not found.");
        }

        AutomationElement fileMenu = null;
        foreach (AutomationElement child in
                 menuBar.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            if (string.Equals(child.Current.Name, "File", StringComparison.Ordinal))
            {
                fileMenu = child;
                break;
            }
        }
        if (fileMenu == null)
        {
            throw new InvalidOperationException("File menu not found.");
        }

        object pattern;
        if (!fileMenu.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
        {
            throw new InvalidOperationException("File menu does not expose ExpandCollapsePattern.");
        }
        ((ExpandCollapsePattern)pattern).Expand();
        Thread.Sleep(700);

        // WinForms exposes the dropdown host as the menu item's first child and
        // names it after the menu. The real items are that host's children.
        TreeWalker walker = TreeWalker.ControlViewWalker;
        AutomationElement host = walker.GetFirstChild(fileMenu);
        AutomationElement container =
            (host != null && string.Equals(host.Current.Name, fileMenu.Current.Name, StringComparison.Ordinal))
                ? host
                : fileMenu;

        AutomationElement item = null;
        for (AutomationElement child = walker.GetFirstChild(container);
             child != null;
             child = walker.GetNextSibling(child))
        {
            if (string.Equals(child.Current.Name, "Run Control Manager", StringComparison.Ordinal))
            {
                item = child;
                break;
            }
        }
        if (item == null)
        {
            throw new InvalidOperationException("'Run Control Manager' menu item not found.");
        }

        HashSet<int> before = new HashSet<int>();
        foreach (Process process in Process.GetProcessesByName("ControlManager"))
        {
            before.Add(process.Id);
        }

        object invokePattern;
        if (!item.TryGetCurrentPattern(InvokePattern.Pattern, out invokePattern))
        {
            throw new InvalidOperationException(
                "'Run Control Manager' does not expose InvokePattern.");
        }
        ((InvokePattern)invokePattern).Invoke();

        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(400);
            foreach (Process process in Process.GetProcessesByName("ControlManager"))
            {
                if (!before.Contains(process.Id))
                {
                    return process;
                }
            }
        }

        throw new TimeoutException(
            "ControlManager.exe did not start within " + timeoutSeconds + " seconds.");
    }

    private static Process StartControlManager(string exeDir)
    {
        string path = Path.Combine(exeDir, ControlManagerExeName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("ControlManager.exe was not found.", path);
        }

        return StartDetached(path, exeDir, null, null);
    }

    private static Process StartDetached(
        string fileName, string workingDirectory, string hookPath, string hookLog)
    {
        string previousHooks = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        string previousLog = Environment.GetEnvironmentVariable("V250_HOOK_LOG");

        NativeMethods.STARTUPINFO startupInfo = new NativeMethods.STARTUPINFO();
        startupInfo.cb = Marshal.SizeOf(typeof(NativeMethods.STARTUPINFO));

        NativeMethods.PROCESS_INFORMATION processInfo = new NativeMethods.PROCESS_INFORMATION();

        try
        {
            if (hookPath != null)
            {
                Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", hookPath);
                Environment.SetEnvironmentVariable("V250_HOOK_LOG", hookLog);
            }

            bool created = NativeMethods.CreateProcess(
                fileName,
                "\"" + fileName + "\"",
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                0,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out processInfo);

            if (!created)
            {
                throw new InvalidOperationException(
                    "CreateProcess failed for " + fileName +
                    " (Win32 error " + Marshal.GetLastWin32Error() + ").");
            }
        }
        finally
        {
            if (hookPath != null)
            {
                Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", previousHooks);
                Environment.SetEnvironmentVariable("V250_HOOK_LOG", previousLog);
            }
        }

        NativeMethods.CloseHandle(processInfo.hThread);
        NativeMethods.CloseHandle(processInfo.hProcess);

        return Process.GetProcessById(processInfo.dwProcessId);
    }

    private static void Warn(string message)
    {
        Output.WriteLine("[WARN] " + message);
    }
}

// Owns the console for a GUI-subsystem process. Nothing may touch Console
// before Open() runs, because the first Console access caches the handle.
internal sealed class ConsoleSession : IDisposable
{
    private const int ConsoleWidth = 120;
    private const int ConsoleHeight = 45;
    private const int ConsoleBufferHeight = 900;

    private readonly bool hasConsole;
    private readonly bool allocated;
    private readonly uint outputCodePage;
    private readonly uint inputCodePage;

    private ConsoleSession(bool hasConsole, bool allocated, uint outputCodePage, uint inputCodePage)
    {
        this.hasConsole = hasConsole;
        this.allocated = allocated;
        this.outputCodePage = outputCodePage;
        this.inputCodePage = inputCodePage;
    }

    public bool HasConsole { get { return hasConsole; } }

    // True only when this process created the window, which is the case worth
    // holding open so the output can be read.
    public bool IsNewConsole { get { return allocated; } }

    public static ConsoleSession Open(bool allowNewConsole)
    {
        bool attached = false;
        bool allocated = false;

        // Started from cmd/PowerShell: reuse that console, no window appears.
        try { attached = NativeMethods.AttachConsole(NativeMethods.AttachParentProcess); }
        catch { }

        if (!attached && allowNewConsole)
        {
            try { allocated = NativeMethods.AllocConsole(); }
            catch { }
        }

        if (!attached && !allocated)
        {
            return new ConsoleSession(false, false, 0, 0);
        }

        uint outputCodePage = 0;
        uint inputCodePage = 0;
        try
        {
            outputCodePage = NativeMethods.GetConsoleOutputCP();
            inputCodePage = NativeMethods.GetConsoleCP();
        }
        catch { }

        // The transcript uses non-ASCII text; 65001 renders it correctly and is
        // restored on dispose so a borrowed console is left as it was found.
        try
        {
            NativeMethods.SetConsoleOutputCP(65001);
            NativeMethods.SetConsoleCP(65001);
        }
        catch { }

        if (allocated)
        {
            try { ResizeConsole(ConsoleWidth, ConsoleHeight); }
            catch { }
        }

        UTF8Encoding encoding = new UTF8Encoding(false);

        try
        {
            StreamWriter stdout = new StreamWriter(Console.OpenStandardOutput(), encoding);
            stdout.AutoFlush = true;
            Console.SetOut(stdout);
        }
        catch { }

        try
        {
            StreamWriter stderr = new StreamWriter(Console.OpenStandardError(), encoding);
            stderr.AutoFlush = true;
            Console.SetError(stderr);
        }
        catch { }

        try
        {
            StreamReader stdin = new StreamReader(Console.OpenStandardInput(), encoding);
            Console.SetIn(stdin);
        }
        catch { }

        return new ConsoleSession(true, allocated, outputCodePage, inputCodePage);
    }

    private static void ResizeConsole(int width, int height)
    {
        IntPtr handle = NativeMethods.GetStdHandle(NativeMethods.StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return;
        }

        NativeMethods.COORD size;
        size.X = (short)width;
        size.Y = (short)ConsoleBufferHeight;
        if (!NativeMethods.SetConsoleScreenBufferSize(handle, size))
        {
            return;
        }

        NativeMethods.SMALL_RECT window;
        window.Left = 0;
        window.Top = 0;
        window.Right = (short)(width - 1);
        window.Bottom = (short)(height - 1);
        NativeMethods.SetConsoleWindowInfo(handle, true, ref window);
    }

    public void Dispose()
    {
        if (!hasConsole)
        {
            return;
        }

        try
        {
            if (outputCodePage != 0) NativeMethods.SetConsoleOutputCP(outputCodePage);
            if (inputCodePage != 0) NativeMethods.SetConsoleCP(inputCodePage);
        }
        catch { }
    }
}

// Single output path for both modes: the console when one exists, and always a
// log file once the evidence directory is known. Lines produced before that are
// buffered and replayed into the file.
internal static class Output
{
    private static readonly object Gate = new object();
    private static readonly List<string> Pending = new List<string>();

    private static bool consoleEnabled;
    private static string logPath;

    public static bool ConsoleEnabled
    {
        get { lock (Gate) { return consoleEnabled; } }
    }

    public static string LogPath
    {
        get { lock (Gate) { return logPath; } }
    }

    public static void EnableConsole(bool enabled)
    {
        lock (Gate)
        {
            consoleEnabled = enabled;
            if (!enabled)
            {
                return;
            }

            foreach (string line in Pending)
            {
                try { Console.Out.WriteLine(line); }
                catch { }
            }
            Pending.Clear();
        }
    }

    public static void SetLogPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (Gate)
        {
            if (logPath != null)
            {
                return;
            }

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllLines(path, Pending.ToArray(), new UTF8Encoding(true));
                Pending.Clear();
                logPath = path;
            }
            catch
            {
                logPath = null;
            }
        }
    }

    public static void WriteLine(string line)
    {
        lock (Gate)
        {
            if (consoleEnabled)
            {
                try { Console.Out.WriteLine(line); }
                catch { }
            }
            Append(line);
        }
    }

    public static void WriteLine()
    {
        WriteLine(string.Empty);
    }

    public static void ErrorLine(string message)
    {
        lock (Gate)
        {
            if (consoleEnabled)
            {
                try { Console.Error.WriteLine(message); }
                catch { }
            }
            Append(message);
        }
    }

    private static void Append(string line)
    {
        if (logPath != null)
        {
            try
            {
                File.AppendAllText(logPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
            return;
        }

        Pending.Add(line);
        if (Pending.Count > 4000)
        {
            Pending.RemoveAt(0);
        }
    }
}

// ===========================================================================
// Credited by TapLamDang
// Public, free at https://www.facebook.com/profile.php?id=61596126010538
// If you paid for this, you are getting scammed.
// ===========================================================================
