// ===========================================================================
// Credited by TapLamDang
// Public, free at https://www.facebook.com/profile.php?id=61596126010538
// If you paid for this, you are getting scammed.
// ===========================================================================

using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class StartupHook
{
    private static readonly string LogPath = ResolveLogPath();
    private const uint PageExecuteReadWrite = 0x40;

    private static string ResolveLogPath()
    {
        string overridePath = Environment.GetEnvironmentVariable("V250_HOOK_LOG");
        if (!string.IsNullOrEmpty(overridePath))
        {
            return overridePath;
        }
        return @"C:\Users\Reez\AppData\Local\Temp\opencode\hook-marker.txt";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

    public static void Initialize()
    {
        try
        {
            ControlChannel.Start(LogPath);

            Application.ThreadException += (sender, e) =>
                Log("THREADEX " + e.Exception);

            AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
                Log("FIRSTCHANCE " + e.Exception.GetType().FullName + ": " + e.Exception.Message +
                    " @ " + (e.Exception.TargetSite == null ? "?" : e.Exception.TargetSite.ToString()));

            MethodInfo target = typeof(HttpClient).GetMethod(
                "SendAsync",
                new[] { typeof(HttpRequestMessage), typeof(HttpCompletionOption), typeof(CancellationToken) });
            if (target == null)
            {
                Log("SendAsync(HttpRequestMessage,HttpCompletionOption,CancellationToken) not found");
                return;
            }

            MethodInfo replacement = typeof(StartupHook).GetMethod("SendAsyncHook", BindingFlags.Public | BindingFlags.Static);
            RuntimeHelpers.PrepareMethod(target.MethodHandle);
            RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

            IntPtr from = target.MethodHandle.GetFunctionPointer();
            IntPtr to = replacement.MethodHandle.GetFunctionPointer();
            long delta = to.ToInt64() - (from.ToInt64() + 5);
            if (delta >= int.MinValue && delta <= int.MaxValue)
            {
                byte[] stub = new byte[5];
                stub[0] = 0xE9;
                BitConverter.GetBytes((int)delta).CopyTo(stub, 1);
                WriteCode(from, stub);
                Log("patched rel32 " + target.DeclaringType.FullName + "." + target.Name + " 0x" + from.ToInt64().ToString("X") + " -> 0x" + to.ToInt64().ToString("X") + " delta=" + delta);
            }
            else
            {
                byte[] stub = new byte[12];
                stub[0] = 0x48;
                stub[1] = 0xB8;
                BitConverter.GetBytes(to.ToInt64()).CopyTo(stub, 2);
                stub[10] = 0xFF;
                stub[11] = 0xE0;
                WriteCode(from, stub);
                Log("patched abs64 " + target.DeclaringType.FullName + "." + target.Name + " 0x" + from.ToInt64().ToString("X") + " -> 0x" + to.ToInt64().ToString("X"));
            }
        }
        catch (Exception ex)
        {
            Log("INIT EX " + ex);
        }
    }

    private static void WriteCode(IntPtr target, byte[] stub)
    {
        uint oldProtect;
        if (!VirtualProtect(target, (UIntPtr)stub.Length, PageExecuteReadWrite, out oldProtect))
            throw new InvalidOperationException("VirtualProtect failed: " + Marshal.GetLastWin32Error());

        Marshal.Copy(stub, 0, target, stub.Length);
        VirtualProtect(target, (UIntPtr)stub.Length, oldProtect, out oldProtect);
        FlushInstructionCache(GetCurrentProcess(), target, (UIntPtr)stub.Length);
    }

    public static Task<HttpResponseMessage> SendAsyncHook(object client, object request, int completionOption, CancellationToken cancellationToken)
    {
        Log("SendAsyncHook entered");

        string uri = "?";
        try { uri = request.GetType().GetProperty("RequestUri").GetValue(request, null).ToString(); } catch { }
        Log("SendAsyncHook uri=" + uri + " option=" + completionOption);

        HttpResponseMessage response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        response.Content = new StringContent(CannedBody(), Encoding.UTF8, "application/json");
        return Task.FromResult(response);
    }

    // The canned licence response is data, not code: scenarios select it so the
    // same hook can drive the success, locked, update and error branches.
    private static string CannedBody()
    {
        string file = Environment.GetEnvironmentVariable("V250_LICENSE_FILE");
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
        {
            return File.ReadAllText(file, Encoding.UTF8);
        }

        string inline = Environment.GetEnvironmentVariable("V250_LICENSE_BODY");
        if (!string.IsNullOrEmpty(inline))
        {
            return inline;
        }

        return "{\"status\":\"success\",\"message\":\"local-poc\",\"user\":\"tester\",\"option\":\"1\",\"server\":\"local\",\"ip\":\"127.0.0.1\"}";
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, DateTime.Now.ToString("o") + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }
}

// ===========================================================================
// Credited by TapLamDang
// Public, free at https://www.facebook.com/profile.php?id=61596126010538
// If you paid for this, you are getting scammed.
// ===========================================================================
