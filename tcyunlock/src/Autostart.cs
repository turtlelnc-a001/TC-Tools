using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TC.Tools.Unlock;

/// <summary>Minimal IShellLink/IPersistFile interop used to create and read the
/// Startup-folder shortcut (no WScript.Shell dependency, which is being
/// deprecated on Windows 11).</summary>
[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkCoClass
{
}

[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    void GetIDList(out IntPtr ppidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
    void GetHotkey(out short pwHotkey);
    void SetHotkey(short wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
    void Resolve(IntPtr hwnd, uint fFlags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

[ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    void GetClassID(out Guid pClassID);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
}

/// <summary>
/// User-level autostart management (v0.2.0-rc2 requirement: "whether the PC side
/// autostarts is the user's choice", default OFF).
///
/// Two mechanisms are supported, both user-level and neither requiring
/// administrator rights:
///   * "task"   - a Task Scheduler logon task (default). Task Scheduler starts
///                the process without allocating a console window, and the task
///                is visible/manageable in Task Scheduler.
///   * "runkey" - HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
///
/// <see cref="Probe"/> always asks the SYSTEM (schtasks.exe / the registry), never
/// our own config file, so hand-deleted tasks are reported truthfully.
/// </summary>
public static class Autostart
{
    public const string TaskName = "TC-tools Unlock Service";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "TC-tools Unlock";
    public const string StartupShortcutName = "TC-tools Unlock Service.lnk";
    public const string Arguments = "run --quiet";
    public const string Scope = "user";

    public sealed record State(
        bool Enabled,
        string Method,
        string TaskName,
        bool TaskExists,
        bool TaskTargetsUs,
        bool RunKeyExists,
        bool RunKeyTargetsUs,
        bool StartupShortcutExists,
        bool StartupShortcutTargetsUs,
        string StartupShortcutPath,
        string ExePath,
        bool ExeExists,
        string Command,
        string Detail);

    /// <summary>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup</summary>
    public static string StartupFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    public static string StartupShortcutPath =>
        Path.Combine(StartupFolder, StartupShortcutName);

    /// <summary>
    /// The binary a logon entry should launch. When we ourselves were started as
    /// `dotnet tctool-unlock.dll`, ProcessPath is dotnet.exe, so fall back to the
    /// apphost next to the assembly.
    /// </summary>
    public static string ResolveExePath(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);

        string? process = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(process) &&
            !Path.GetFileName(process).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(process);
        }

        string candidate = Path.Combine(AppContext.BaseDirectory, "tctool-unlock.exe");
        return Path.GetFullPath(File.Exists(candidate) ? candidate : process ?? candidate);
    }

    public static string CommandLine(string exePath) => $"\"{exePath}\" {Arguments}";

    /// <summary>
    /// schtasks.exe by absolute path: PATH can be minimal (autostart runs from a
    /// logon task and from launchers with a restricted environment), and relying
    /// on PATH lookup would make autostart management fail intermittently.
    /// </summary>
    private static string SchtasksPath
    {
        get
        {
            try
            {
                string system = Environment.SystemDirectory;
                if (!string.IsNullOrEmpty(system))
                {
                    string candidate = Path.Combine(system, "schtasks.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (Exception) { }
            return "schtasks.exe";
        }
    }

    // -------------------------------------------------------------- probing

    public static State Probe(string exePath)
    {
        (bool taskExists, bool taskTargetsUs, string taskDetail) = ProbeTask(exePath);
        (bool shortcutExists, bool shortcutTargetsUs, string shortcutDetail) = ProbeStartupShortcut(exePath);
        (bool runKeyExists, bool runKeyTargetsUs, string runKeyDetail) = ProbeRunKey(exePath);

        bool enabled = taskTargetsUs || shortcutTargetsUs || runKeyTargetsUs;
        string method = taskTargetsUs ? "task"
            : shortcutTargetsUs ? "startup"
            : runKeyTargetsUs ? "runkey"
            : "none";

        var details = new List<string> { taskDetail, shortcutDetail, runKeyDetail };
        if (taskExists && !taskTargetsUs) details.Add("a task with our name exists but points elsewhere");

        return new State(
            Enabled: enabled,
            Method: method,
            TaskName: TaskName,
            TaskExists: taskExists,
            TaskTargetsUs: taskTargetsUs,
            RunKeyExists: runKeyExists,
            RunKeyTargetsUs: runKeyTargetsUs,
            StartupShortcutExists: shortcutExists,
            StartupShortcutTargetsUs: shortcutTargetsUs,
            StartupShortcutPath: StartupShortcutPath,
            ExePath: exePath,
            ExeExists: File.Exists(exePath),
            Command: CommandLine(exePath),
            Detail: string.Join("; ", details));
    }

    private static (bool exists, bool targetsUs, string detail) ProbeTask(string exePath)
    {
        (int exitCode, string stdout, string stderr) = Run(SchtasksPath, $"/query /tn \"{TaskName}\" /xml");
        string output = stdout + stderr;

        if (exitCode != 0)
        {
            return (false, false, $"task '{TaskName}' not registered (schtasks exit {exitCode})");
        }

        bool targetsUs = LooksLikeOurTask(output, exePath);
        return (true, targetsUs, targetsUs
            ? $"task '{TaskName}' registered and points at this executable"
            : $"task '{TaskName}' exists but its action does not match");
    }

    private static bool LooksLikeOurTask(string xml, string exePath)
    {
        if (string.IsNullOrEmpty(xml)) return false;

        string exeName = Path.GetFileName(exePath);
        bool hasExe = xml.Contains(exeName, StringComparison.OrdinalIgnoreCase);
        bool hasArgs = xml.Contains("run", StringComparison.OrdinalIgnoreCase) &&
                       xml.Contains("--quiet", StringComparison.OrdinalIgnoreCase);
        if (!hasExe || !hasArgs) return false;

        // When the path is decodable, require the full directory too. A path with
        // characters the console code page cannot express is accepted on the
        // file-name match above (still a real match, just less strict).
        string? dir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrEmpty(dir) && xml.Contains(dir, StringComparison.OrdinalIgnoreCase)) return true;
        return !IsAscii(dir ?? string.Empty);
    }

    private static bool IsAscii(string s) => s.All(c => c <= 0x7F);

    private static (bool exists, bool targetsUs, string detail) ProbeRunKey(string exePath)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            object? value = key?.GetValue(RunValueName);
            if (value is not string text || string.IsNullOrWhiteSpace(text))
            {
                return (false, false, "HKCU Run value not present");
            }

            bool targetsUs = text.Contains(Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase) &&
                             text.Contains("--quiet", StringComparison.OrdinalIgnoreCase);
            return (true, targetsUs, targetsUs
                ? "HKCU Run value present and points at this executable"
                : "HKCU Run value present but points elsewhere");
        }
        catch (Exception ex)
        {
            return (false, false, $"HKCU Run probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------- startup folder shortcut

    /// <summary>
    /// Probes the Startup-folder shortcut by LOADING the .lnk and reading its
    /// target/arguments back, so a shortcut that was edited or replaced by hand is
    /// reported truthfully.
    /// </summary>
    private static (bool exists, bool targetsUs, string detail) ProbeStartupShortcut(string exePath)
    {
        string path = StartupShortcutPath;
        if (!File.Exists(path))
        {
            return (false, false, $"startup shortcut '{StartupShortcutName}' not present");
        }

        try
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(path, 0 /* STGM_READ */);

            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var arguments = new StringBuilder(1024);
            link.GetArguments(arguments, arguments.Capacity);

            string targetText = target.ToString();
            string argumentText = arguments.ToString();
            bool targetsUs =
                Path.GetFileName(targetText).Equals(Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase) &&
                argumentText.Contains("--quiet", StringComparison.OrdinalIgnoreCase);

            return (true, targetsUs, targetsUs
                ? "startup shortcut present and points at this executable"
                : $"startup shortcut present but points at '{targetText} {argumentText}'");
        }
        catch (Exception ex)
        {
            return (true, false, $"startup shortcut present but unreadable: {Describe(ex)}");
        }
    }

    private static bool EnableStartupShortcut(string exePath, out string detail)
    {
        try
        {
            HostPaths.EnsureDir();
            string folder = StartupFolder;
            if (string.IsNullOrEmpty(folder))
            {
                detail = "cannot resolve the per-user Startup folder";
                return false;
            }
            Directory.CreateDirectory(folder);

            string path = StartupShortcutPath;
            // Overwriting an existing shortcut keeps this idempotent.
            if (File.Exists(path)) File.Delete(path);

            var link = (IShellLinkW)new ShellLinkCoClass();
            link.SetPath(exePath);
            link.SetArguments(Arguments);
            string? workingDirectory = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(workingDirectory)) link.SetWorkingDirectory(workingDirectory);
            link.SetDescription("TC-tools Bluetooth unlock host (user-level autostart)");
            link.SetShowCmd(7 /* SW_SHOWMINNOACTIVE: launch minimized; run --quiet hides it */);
            ((IPersistFile)link).Save(path, true);

            (bool exists, bool targetsUs, string probe) = ProbeStartupShortcut(exePath);
            if (!exists || !targetsUs)
            {
                detail = $"shortcut written but verification failed: {probe}";
                return false;
            }

            detail = $"startup shortcut '{path}' created (user scope, no elevation): {CommandLine(exePath)}";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"startup shortcut creation failed: {Describe(ex)}";
            return false;
        }
    }

    private static bool DisableStartupShortcut(out string detail)
    {
        try
        {
            string path = StartupShortcutPath;
            if (File.Exists(path))
            {
                File.Delete(path);
                detail = "startup shortcut deleted";
            }
            else
            {
                detail = "no startup shortcut to delete";
            }
            return true;
        }
        catch (Exception ex)
        {
            detail = $"startup shortcut deletion failed: {Describe(ex)}";
            return false;
        }
    }

    // ------------------------------------------------------------ enable/disable

    public static bool Enable(string exePath, string method, out string detail)
    {
        if (!File.Exists(exePath))
        {
            detail = $"refusing to register autostart: {exePath} does not exist";
            return false;
        }

        if (method.Equals("runkey", StringComparison.OrdinalIgnoreCase))
        {
            return EnableRunKey(exePath, out detail);
        }

        if (method.Equals("task", StringComparison.OrdinalIgnoreCase))
        {
            return EnableTask(exePath, out detail);
        }

        if (method.Equals("startup", StringComparison.OrdinalIgnoreCase))
        {
            return EnableStartupShortcut(exePath, out detail);
        }

        // "auto" (default) tries, in order:
        //   1. a logon task       - no console window at all; needs the right to
        //                           create tasks (refused for standard users on
        //                           many systems, including this one).
        //   2. Startup shortcut   - pure file operation inside HKCU, always
        //                           available to a standard user, and visible to
        //                           the user in shell:startup.
        //   3. HKCU Run value     - last resort; some security products and
        //                           policies deny writes to the Run key.
        // All three are user-level and require no administrator rights.
        var attempts = new List<string>();

        if (EnableTask(exePath, out string taskDetail))
        {
            detail = taskDetail;
            return true;
        }
        attempts.Add($"task: {taskDetail}");

        if (EnableStartupShortcut(exePath, out string shortcutDetail))
        {
            detail = $"{string.Join("; ", attempts)}; fell back to the Startup folder: {shortcutDetail}";
            return true;
        }
        attempts.Add($"startup: {shortcutDetail}");

        if (EnableRunKey(exePath, out string runDetail))
        {
            detail = $"{string.Join("; ", attempts)}; fell back to the HKCU Run key: {runDetail}";
            return true;
        }
        attempts.Add($"runkey: {runDetail}");

        detail = string.Join("; ", attempts);
        return false;
    }

    private static bool EnableTask(string exePath, out string detail)
    {
        // /f makes it idempotent: an existing task is overwritten, never duplicated.
        // No /rl HIGHEST: a user-level task must not require elevation.
        string args = $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\" {Arguments}\" /sc onlogon /f";
        (int exitCode, string stdout, string stderr) = Run(SchtasksPath, args);

        if (exitCode != 0)
        {
            detail = $"schtasks /create failed (exit {exitCode}): {Trim(stdout + stderr)}";
            return false;
        }

        State state = Probe(exePath);
        if (!state.TaskTargetsUs)
        {
            detail = $"task created but verification failed: {state.Detail}";
            return false;
        }

        detail = $"logon task '{TaskName}' registered (user scope, no elevation): {CommandLine(exePath)}";
        return true;
    }

    private static bool EnableRunKey(string exePath, out string detail)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("cannot open HKCU Run key");
            key.SetValue(RunValueName, CommandLine(exePath), RegistryValueKind.String);

            State state = Probe(exePath);
            if (!state.RunKeyTargetsUs)
            {
                detail = $"run key written but verification failed: {state.Detail}";
                return false;
            }

            detail = $"HKCU Run value '{RunValueName}' set to {CommandLine(exePath)}";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"HKCU Run write failed: {Describe(ex)}";
            return false;
        }
    }

    /// <summary>Compact exception rendering (type + message + first stack frames).</summary>
    private static string Describe(Exception ex)
    {
        string text = ex.ToString().Replace('\r', ' ').Replace('\n', ' ');
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ");
        return text.Length <= 400 ? text : text[..400];
    }

    public static bool Disable(string exePath, out string detail)
    {
        var notes = new List<string>();
        bool ok = true;

        // Task: a missing task is success, not an error (idempotent disable).
        (int exitCode, string stdout, string stderr) = Run(SchtasksPath, $"/delete /tn \"{TaskName}\" /f");
        if (exitCode == 0)
        {
            notes.Add($"logon task '{TaskName}' deleted");
        }
        else
        {
            string text = stdout + stderr;
            if (text.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("不存在", StringComparison.Ordinal) ||
                text.Contains("找不到", StringComparison.Ordinal))
            {
                notes.Add("no logon task to delete");
            }
            else
            {
                ok = false;
                notes.Add($"schtasks /delete failed (exit {exitCode}): {Trim(text)}");
            }
        }

        // Startup shortcut: also idempotent.
        if (!DisableStartupShortcut(out string shortcutDetail))
        {
            ok = false;
        }
        notes.Add(shortcutDetail);

        // Run key: also idempotent.
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                notes.Add("HKCU Run value removed");
            }
            else
            {
                notes.Add("no HKCU Run value to remove");
            }
        }
        catch (Exception ex)
        {
            ok = false;
            notes.Add($"HKCU Run removal failed: {ex.GetType().Name}: {ex.Message}");
        }

        State state = Probe(exePath);
        if (state.Enabled)
        {
            ok = false;
            notes.Add($"still enabled after disable: {state.Detail}");
        }

        detail = string.Join("; ", notes);
        return ok;
    }

    // ------------------------------------------------------------------ helpers

    private static string Trim(string text)
    {
        text = text.Trim();
        return text.Length <= 300 ? text : text[..300] + "...";
    }

    private static (int exitCode, string stdout, string stderr) Run(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(psi);
            if (process is null) return (-1, string.Empty, $"could not start {fileName}");

            using var outStream = new MemoryStream();
            using var errStream = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(outStream);
            process.StandardError.BaseStream.CopyTo(errStream);
            process.WaitForExit(20000);

            return (process.HasExited ? process.ExitCode : -1, Decode(outStream.ToArray()), Decode(errStream.ToArray()));
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// schtasks.exe writes in the console code page and sometimes as UTF-16;
    /// decode defensively because the executable path may contain CJK characters.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        int oddNuls = 0;
        int limit = Math.Min(bytes.Length, 400);
        for (int i = 1; i < limit; i += 2)
        {
            if (bytes[i] == 0) oddNuls++;
        }
        if (oddNuls > limit / 4) return Encoding.Unicode.GetString(bytes);

        try { return Encoding.UTF8.GetString(bytes); }
        catch (Exception) { return Encoding.Latin1.GetString(bytes); }
    }
}

/// <summary>
/// Append-only service log with a 1 MB cap, used by `run --quiet` (the autostart
/// mode, where there is no console to print to).
/// </summary>
public static class ServiceLog
{
    public const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Path => HostPaths.ServiceLog;

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                HostPaths.EnsureDir();
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string rolled = Path + ".1";
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(Path, rolled);
                }

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                File.AppendAllText(Path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception)
            {
                // Logging must never take the service down.
            }
        }
    }
}
