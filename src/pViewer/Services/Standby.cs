using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace pViewer.Services;

/// <summary>What a pViewer just started hands to the copy kept ready: what to open, and from where.</summary>
internal sealed record StandbyRequest(string WorkingDirectory, string[] Args, string TracePath, long StartTicksUtc)
{
    private const int Version = 1;

    public void Write(Stream stream)
    {
        var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Version);
        writer.Write(WorkingDirectory);
        writer.Write(TracePath);
        writer.Write(StartTicksUtc);
        writer.Write(Args.Length);
        foreach (string arg in Args) writer.Write(arg);
        writer.Flush();
    }

    /// <returns>null for a request of another version (never mixed: the caller starts normally).</returns>
    public static StandbyRequest? Read(Stream stream)
    {
        var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != Version) return null;
        string directory = reader.ReadString();
        string trace = reader.ReadString();
        long start = reader.ReadInt64();
        int count = reader.ReadInt32();
        if (count is < 0 or > 10000) return null;
        var args = new string[count];
        for (int i = 0; i < count; i++) args[i] = reader.ReadString();
        return new StandbyRequest(directory, args, trace, start);
    }
}

/// <summary>
/// "Keep pViewer ready": one hidden pViewer, already started and warmed up, waits in the background.
/// A pViewer started from Explorer hands it the file and exits before loading WPF, and the waiting
/// copy shows its window at once; it then starts its own replacement. Each window is still its own
/// process, as without this.
///
/// This class is what both sides share and has nothing of WPF in it: the process that only hands
/// over must stay light. The waiting side is in App.Standby.cs.
/// </summary>
internal static class Standby
{
    /// <summary>Command line of the copy that waits.</summary>
    public const string Argument = "--standby";

    /// <summary>
    /// Development: a suffix for every name below, so that a test build neither talks to the copy
    /// of an installed pViewer nor is found by it. It also lets a build that was not installed wait.
    /// </summary>
    private static readonly string Scope = Environment.GetEnvironmentVariable("PVIEWER_STANDBY_SCOPE") ?? "";

    /// <summary>Exists while a copy waits or warms up (also checked by the installer: see pViewer.iss).</summary>
    public static string MutexName => "pViewer.Standby" + Scope;

    /// <summary>Exists only while the waiting copy can take a request.</summary>
    public static string ReadyName => "pViewer.Standby.Ready" + Scope;

    /// <summary>Set to make the waiting copy exit (update, uninstall, option turned off).</summary>
    public static string ExitName => "pViewer.Standby.Exit" + Scope;

    public static string PipeName
    {
        get
        {
            ProcessIdToSessionId((uint)Environment.ProcessId, out uint session);
            return $"pViewer.Standby.{session}{Scope}";
        }
    }

    public static bool IsStandbyStart(string[] args) => args.Length == 1 && args[0] == Argument;

    /// <summary>
    /// Only a copy installed by the setup waits in the background: its uninstaller and its updates
    /// know how to stop it. A hidden process of a portable copy would keep its folder locked.
    /// </summary>
    public static bool Allowed => Scope.Length > 0 || File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>
    /// Hands the command line to the waiting copy. False = there is none (or it did not answer):
    /// start normally. Never throws.
    /// </summary>
    public static bool TryHandOver(string[] args)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ReadyName, out var ready)) return false;
            ready.Dispose();
            string trace = Environment.GetEnvironmentVariable("PVIEWER_STARTUP_TRACE") ?? "";
            long start = trace.Length > 0 ? System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks : 0;
            var request = new StandbyRequest(Environment.CurrentDirectory, args, trace, start);
            var client = pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            var exchange = Task.Run(() =>
            {
                client.Connect(300);
                // The window that appears belongs to the other process: Windows lets it come to the
                // front only if this one, started by the user, allows it.
                int processId = new BinaryReader(client, Encoding.UTF8, leaveOpen: true).ReadInt32();
                AllowSetForegroundWindow(processId);
                request.Write(client);
                return client.ReadByte() == 1;
            });
            return exchange.Wait(2000) && exchange.Result;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            // Also with an exchange still waiting: the other side then gives up too.
            try { pipe?.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>Waiting side: answers one connected pViewer. null = no valid request (go on waiting).</summary>
    public static StandbyRequest? Serve(Stream pipe)
    {
        try
        {
            var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
            writer.Write(Environment.ProcessId);
            writer.Flush();
            var request = StandbyRequest.Read(pipe);
            if (request is null) return null;
            // The other side starts normally if this does not arrive: only then is the file ours.
            pipe.WriteByte(1);
            pipe.Flush();
            return request;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Starts the copy that waits, if the option is on and none is there yet.</summary>
    public static void EnsureRunning(AppSettings settings)
    {
        if (!settings.KeepReady || !Allowed || Environment.ProcessPath is not { } exe) return;
        try
        {
            if (IsRunning()) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, Argument)
            {
                UseShellExecute = false,
                // Not the folder of the pictures: a hidden process would keep it from being renamed or deleted.
                WorkingDirectory = AppContext.BaseDirectory,
            })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
    }

    public static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>Asks the waiting copy to exit, and waits up to <paramref name="waitMs"/> for it to be gone.</summary>
    public static void RequestExit(int waitMs = 0)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ExitName, out var exit)) return;
            using (exit) exit.Set();
            for (int waited = 0; waited < waitMs && IsRunning(); waited += 50) Thread.Sleep(50);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }
    }

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
