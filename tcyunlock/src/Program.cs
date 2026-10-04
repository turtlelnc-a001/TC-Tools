using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TC.Tools.Unlock.Ble;
using TC.Tools.Unlock.Qr;
using TC.Tools.Unlock.Win;
using Windows.Devices.Radios;

namespace TC.Tools.Unlock;

public static class Program
{
    public const string ToolName = "tctool-unlock";
    public const string ToolVersion = "0.2.0-rc2";
    public const int ProtocolVersion = 1;

    private static bool _jsonMode;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await DispatchAsync(args);
        }
        catch (Exception ex)
        {
            // Never let an unhandled exception escape with a stack trace on
            // stdout: the C++ side parses our output.
            Console.Error.WriteLine($"{ToolName}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> DispatchAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string command = args[0].ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();
        _jsonMode = rest.Contains("--json");

        return command switch
        {
            "status" => await CmdStatusAsync(rest),
            "pair" => CmdPair(rest),
            "forget" => CmdForget(rest),
            "set-password" => CmdSetPassword(rest),
            "run" => await CmdRunAsync(rest),
            "autostart" => CmdAutostart(rest),
            "selftest" => CmdSelfTest(rest),
            "qrdump" => CmdQrDump(rest),
            "version" or "--version" or "-v" => CmdVersion(rest),
            "help" or "--help" or "-h" => PrintUsageAndOk(),
            _ => Unknown(command),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"{ToolName}: unknown command '{command}'");
        PrintUsage();
        return 2;
    }

    private static int PrintUsageAndOk()
    {
        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
{TOOL} {ToolVersion} (TC-tools unlock host, protocol {ProtocolVersion})

usage: {TOOL} <command> [options]

  status [--json] [--no-probe] [--enable-radio]
        One-line JSON state: paired/advertising/passwordSet/hostId/hostName/
        peerName/pskValid. Starts a short-lived GATT advertisement probe unless
        --no-probe or a live `run` instance is detected.

  pair [--json] [--qr=ascii|unicode|none] [--no-png]
        Generate a new PSK + pairing payload (QR text + ASCII QR + PNG) and
        save it. Re-pairing invalidates the previously paired phone.

  pair --show [--json]
        Show the saved pairing. The PSK is never printed in clear text.

  pair --payload <json> [--json]
        Import a pairing payload produced by the phone (sets peerId/peerName,
        and adopts its PSK when one is present).

  forget [--json]      Forget the pairing (clears the PSK).
  set-password [--clear] [--json]
        Set the Windows unlock password (interactive, no echo) or clear it.
  run [--duration N] [--json] [--enable-radio] [--quiet]
        Run the GATT server in the foreground until Ctrl+C (or N seconds).
        --quiet is the autostart mode: the console window is hidden and key
        events go to %LOCALAPPDATA%\TC-tools\unlock\service.log (1 MB cap).

  autostart status|enable|disable [--json] [--method auto|task|startup|runkey] [--exe <path>]
        User-level autostart for `run --quiet`. OFF by default, no admin needed,
        idempotent, and `status` probes the real system state (Task Scheduler,
        the Startup folder and the HKCU Run key), not just our own config.
        auto (default) tries a logon task, then a Startup shortcut, then HKCU Run.
  selftest [--json] [--out <path>]
        Offline protocol self-test (no Bluetooth required).

  qrdump --text <payload> [--ecc L|M|Q|H] [--mask 0..7] [--json]
        Diagnostic: dump the QR module matrix (used by tools/verify-qr.py).
  version [--json]
  help

Exit codes: 0 success, 1 failure, 2 usage, 3 already running, 4 self-test failed.
""");
    }

    private const string TOOL = ToolName;

    // ------------------------------------------------------------------ status

    private static async Task<int> CmdStatusAsync(string[] args)
    {
        HostStore store = HostStore.Load();
        byte[]? psk = store.GetPsk();

        bool enableRadio = args.Contains("--enable-radio");
        if (enableRadio) await TryEnableRadioAsync(warn: !_jsonMode);

        bool probe = !args.Contains("--no-probe");
        bool advertising = false;
        string advertisingDetail = "not probed";

        RuntimeState? runtime = RuntimeState.TryRead();
        if (runtime is not null && runtime.IsLive())
        {
            advertising = runtime.Advertising;
            advertisingDetail = $"live instance pid={runtime.Pid} ({runtime.Detail})";
        }
        else if (probe)
        {
            (advertising, advertisingDetail) = await ProbeAdvertisingAsync();
        }

        var payload = new StatusPayload
        {
            Paired = psk is not null,
            Advertising = advertising,
            PasswordSet = store.HasPassword,
            HostId = store.State.HostId,
            HostName = store.State.HostName,
            PeerName = store.State.PeerName ?? string.Empty,
            PskValid = psk is not null && !store.State.PskInvalidated,
            Running = runtime is not null && runtime.IsLive(),
            InjectWhenUnlocked = store.State.InjectWhenUnlocked,
            HostJsonPath = HostPaths.HostJson,
            AdvertisingDetail = advertisingDetail,
            Version = ToolVersion,
            AppVersion = ToolVersion,
            Protocol = ProtocolVersion,
        };

        if (_jsonMode)
        {
            WriteJsonLine(payload);
        }
        else
        {
            Console.WriteLine($"tool            : {ToolName} {ToolVersion} (protocol {ProtocolVersion})");
            Console.WriteLine($"paired          : {payload.Paired}");
            Console.WriteLine($"pskValid        : {payload.PskValid}" + (store.State.PskInvalidated ? "  (PSK INVALIDATED - re-pair required)" : string.Empty));
            Console.WriteLine($"passwordSet     : {payload.PasswordSet}");
            Console.WriteLine($"advertising     : {payload.Advertising}   [{advertisingDetail}]");
            Console.WriteLine($"running         : {payload.Running}");
            Console.WriteLine($"hostId          : {payload.HostId}");
            Console.WriteLine($"hostName        : {payload.HostName}");
            Console.WriteLine($"peerName        : {payload.PeerName ?? "(none)"}");
            Console.WriteLine($"injectWhenLocked: {(store.State.InjectWhenUnlocked ? "injects even when unlocked" : "only injects when locked")}");
            Console.WriteLine($"host.json       : {HostPaths.HostJson}");
        }

        return 0;
    }

    private static async Task<(bool advertising, string detail)> ProbeAdvertisingAsync()
    {
        try
        {
            HostStore store = HostStore.Load();
            using var server = new GattUnlockServer(store);
            GattStartResult result = await server.StartAsync();
            server.Stop();
            return (result.Advertising, result.Detail);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------- pair

    private static int CmdPair(string[] args)
    {
        HostStore store = HostStore.Load();

        if (args.Contains("--show"))
        {
            byte[]? psk = store.GetPsk();
            var shown = new PairShowPayload
            {
                Paired = psk is not null,
                PskValid = psk is not null && !store.State.PskInvalidated,
                PskMasked = psk is null ? null : Secret.Mask(psk),
                HostId = store.State.HostId,
                HostName = store.State.HostName,
                PeerId = store.State.PeerId,
                PeerName = store.State.PeerName,
                PairedAt = store.State.PairedAt,
                FailCount = store.State.FailCount,
                HostJsonPath = HostPaths.HostJson,
                PayloadPath = File.Exists(HostPaths.PayloadTxt) ? HostPaths.PayloadTxt : null,
                PayloadPngPath = File.Exists(HostPaths.PayloadPng) ? HostPaths.PayloadPng : null,
            };

            if (_jsonMode) WriteJsonLine(shown);
            else
            {
                Console.WriteLine($"paired     : {shown.Paired}");
                Console.WriteLine($"pskValid   : {shown.PskValid}");
                Console.WriteLine($"psk        : {shown.PskMasked ?? "(none)"}");
                Console.WriteLine($"hostId     : {shown.HostId}");
                Console.WriteLine($"hostName   : {shown.HostName}");
                Console.WriteLine($"peerId     : {shown.PeerId ?? "(none)"}");
                Console.WriteLine($"peerName   : {shown.PeerName ?? "(none)"}");
                Console.WriteLine($"pairedAt   : {shown.PairedAt ?? "(never)"}");
                Console.WriteLine($"failCount  : {shown.FailCount}");
                Console.WriteLine($"host.json  : {shown.HostJsonPath}");
            }
            return 0;
        }

        int payloadIndex = Array.IndexOf(args, "--payload");
        if (payloadIndex >= 0)
        {
            if (payloadIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("pair --payload requires a JSON argument");
                return 2;
            }
            return CmdPairImport(store, args[payloadIndex + 1]);
        }

        // ---- fresh pairing -------------------------------------------------
        byte[] pskNew = RandomNumberGenerator.GetBytes(Proto.PskLength);
        bool replacing = store.HasPsk;
        store.SetPsk(pskNew);
        store.State.PskInvalidated = false;
        store.State.FailCount = 0;
        store.State.PeerId = null;
        store.State.PeerName = null;
        store.State.PairedAt = DateTime.UtcNow.ToString("o");
        if (string.IsNullOrWhiteSpace(store.State.HostName)) store.State.HostName = Environment.MachineName;
        store.Save();

        var pairing = new PairingPayload
        {
            V = ProtocolVersion,
            P = "tcunlock",
            Id = store.State.HostId,
            Name = store.State.HostName,
            Psk = Proto.Base64UrlEncode(pskNew),
        };

        string payloadJson = Proto.ToCliJson(pairing);

        // payload.txt is raw UTF-8 (a Chinese computer name stays readable when
        // pasted); the console copy is ASCII-escaped so that any code page and
        // any pipe delivers valid JSON.
        HostPaths.EnsureDir();
        File.WriteAllText(HostPaths.PayloadTxt, payloadJson + Environment.NewLine, new UTF8Encoding(false));

        string qrMode = GetOption(args, "--qr") ?? "ascii";
        string? pngPath = null;
        if (!args.Contains("--no-png"))
        {
            pngPath = HostPaths.PayloadPng;
            if (!TryWriteQrPng(payloadJson, pngPath, out string pngError))
            {
                pngPath = null;
                Info($"warning: could not write QR PNG: {pngError}");
            }
        }

        if (_jsonMode)
        {
            WriteJsonLine(new PairResultPayload
            {
                Paired = true,
                Replaced = replacing,
                HostId = store.State.HostId,
                HostName = store.State.HostName,
                Payload = payloadJson,
                PayloadPath = HostPaths.PayloadTxt,
                PayloadPngPath = pngPath,
                PskMasked = Secret.Mask(pskNew),
            });
            return 0;
        }

        Console.WriteLine($"{(replacing ? "RE-PAIRED" : "PAIRED")}  host={store.State.HostName}  id={store.State.HostId}");
        if (replacing)
            Console.WriteLine("note: the previous PSK is gone; the previously paired phone must scan again.");
        Console.WriteLine();
        Console.WriteLine("pairing payload (single line JSON, paste into the phone if scanning is not possible):");
        Console.WriteLine(payloadJson);
        Console.WriteLine();
        Console.WriteLine($"payload.txt : {HostPaths.PayloadTxt}");
        if (pngPath is not null) Console.WriteLine($"payload.png : {pngPath}");
        Console.WriteLine($"psk         : {Secret.Mask(pskNew)}");
        Console.WriteLine();

        if (!qrMode.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (QrCode.TryEncode(Encoding.UTF8.GetBytes(payloadJson), QrEcc.M, out QrMatrix? matrix, out string error))
            {
                Console.WriteLine($"QR (version {matrix!.Version}, ECC {matrix.Ecc}, mask {matrix.Mask}) - "
                                  + (qrMode.Equals("unicode", StringComparison.OrdinalIgnoreCase) ? "unicode half-blocks" : "pure ASCII"));
                Console.WriteLine(qrMode.Equals("unicode", StringComparison.OrdinalIgnoreCase)
                    ? QrCode.ToUnicodeArt(matrix)
                    : QrCode.ToAsciiArt(matrix));
            }
            else
            {
                Console.WriteLine($"QR not generated: {error}");
                Console.WriteLine("(the payload.txt one-line text above is always valid input for the phone)");
            }
        }

        return 0;
    }

    private static int CmdPairImport(HostStore store, string json)
    {
        PairingPayload? incoming;
        try
        {
            incoming = JsonSerializer.Deserialize<PairingPayload>(json, Proto.WireJson);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"pair --payload: invalid JSON: {ex.Message}");
            return 1;
        }

        if (incoming is null || string.IsNullOrWhiteSpace(incoming.Id))
        {
            Console.Error.WriteLine("pair --payload: payload must contain at least an 'id' field");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(incoming.Psk))
        {
            try
            {
                byte[] psk = Proto.Base64UrlDecode(incoming.Psk!);
                if (psk.Length != Proto.PskLength)
                {
                    Console.Error.WriteLine($"pair --payload: PSK must be {Proto.PskLength} bytes, got {psk.Length}");
                    return 1;
                }
                store.SetPsk(psk);
                store.State.PairedAt = DateTime.UtcNow.ToString("o");
                store.State.PskInvalidated = false;
                store.State.FailCount = 0;
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine($"pair --payload: PSK is not valid base64url: {ex.Message}");
                return 1;
            }
        }

        store.State.PeerId = incoming.Id;
        store.State.PeerName = incoming.Name;
        store.Save();

        byte[]? current = store.GetPsk();
        var result = new PairShowPayload
        {
            Paired = current is not null,
            PskValid = current is not null && !store.State.PskInvalidated,
            PskMasked = current is null ? null : Secret.Mask(current),
            HostId = store.State.HostId,
            HostName = store.State.HostName,
            PeerId = store.State.PeerId,
            PeerName = store.State.PeerName,
            PairedAt = store.State.PairedAt,
            FailCount = store.State.FailCount,
            HostJsonPath = HostPaths.HostJson,
        };

        if (_jsonMode) WriteJsonLine(result);
        else
        {
            Console.WriteLine($"imported peer: {result.PeerId} ({result.PeerName ?? "unnamed"})");
            Console.WriteLine($"psk adopted  : {(!string.IsNullOrWhiteSpace(incoming.Psk) ? "yes" : "no (kept existing)")}");
            Console.WriteLine($"paired       : {result.Paired}   pskValid: {result.PskValid}");
            Console.WriteLine($"psk          : {result.PskMasked ?? "(none)"}");
        }
        return 0;
    }

    private static bool TryWriteQrPng(string text, string path, out string error)
    {
        error = string.Empty;
        try
        {
            if (!QrCode.TryEncode(Encoding.UTF8.GetBytes(text), QrEcc.M, out QrMatrix? matrix, out string encodeError))
            {
                error = encodeError;
                return false;
            }
            File.WriteAllBytes(path, QrCode.ToPng(matrix!));
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    // ------------------------------------------------------------------ forget

    private static int CmdForget(string[] args)
    {
        HostStore store = HostStore.Load();
        bool hadPsk = store.HasPsk;
        store.ClearPairing();
        store.Save();

        // since v0.2.0-rc3 / deferred from rc2:
        // rc2 (the frozen binary, sha256 2BC9BB1C…) cleared the PSK but left the
        // exported pairing payload behind, so an old payload.png/payload.txt kept
        // showing a QR whose PSK was already dead. Those files carry no usable key
        // material, so this was a UX issue, not a security one; it was deferred out
        // of rc2 to avoid invalidating the verified rc2 artefacts.
        var removedExports = new List<string>();
        foreach (string exportPath in new[] { HostPaths.PayloadTxt, HostPaths.PayloadPng })
        {
            try
            {
                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                    removedExports.Add(Path.GetFileName(exportPath));
                }
            }
            catch (Exception ex)
            {
                // Deleting an export must never fail the forget itself.
                Console.Error.WriteLine($"{ToolName}: could not delete {exportPath}: {ex.Message}");
            }
        }

        if (_jsonMode)
        {
            WriteJsonLine(new
            {
                forgotten = true,
                hadPsk,
                paired = false,
                hostId = store.State.HostId,
                exportsRemoved = removedExports,
                exportsRemovedCount = removedExports.Count,
            });
        }
        else
        {
            Console.WriteLine(hadPsk
                ? $"pairing forgotten (PSK cleared). hostId {store.State.HostId} kept; run 'pair' to pair a new phone."
                : "nothing to forget: this host was not paired.");

            // Accurate for rc3 (files ARE cleaned). The rc2 behaviour is called out
            // explicitly because rc2 users are told to remove the files by hand.
            Console.WriteLine(removedExports.Count > 0
                ? $"also deleted the stale pairing exports: {string.Join(", ", removedExports)} " +
                  "(rc2 and earlier did not do this - if you are on rc2, delete payload.txt/payload.png yourself)."
                : "no exported pairing files (payload.txt/payload.png) were present to clean up " +
                  "(rc2 and earlier never cleaned them; delete them by hand if you still have them).");
        }
        return 0;
    }

    // ------------------------------------------------------------ set-password

    private static int CmdSetPassword(string[] args)
    {
        HostStore store = HostStore.Load();

        if (args.Contains("--clear"))
        {
            store.SetPassword(string.Empty);
            store.State.PasswordProtected = null;
            store.Save();
            if (_jsonMode) WriteJsonLine(new { passwordSet = false, cleared = true });
            else Console.WriteLine("unlock password cleared.");
            return 0;
        }

        string password;
        if (Console.IsInputRedirected)
        {
            string? line = Console.ReadLine();
            if (line is null)
            {
                Console.Error.WriteLine("set-password: no input on stdin");
                return 1;
            }
            password = line.TrimEnd('\r', '\n');
            Info("warning: stdin is redirected, the password could not be hidden.");
        }
        else
        {
            Console.Write("new unlock password: ");
            password = ReadHidden();
            Console.WriteLine();
            Console.Write("repeat to confirm:   ");
            string again = ReadHidden();
            Console.WriteLine();
            if (!string.Equals(password, again, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("passwords do not match; nothing was saved.");
                return 1;
            }
        }

        if (password.Length == 0)
        {
            Console.Error.WriteLine("empty password; use --clear to remove the stored password.");
            return 1;
        }

        store.SetPassword(password);
        store.Save();

        // Be honest up front: an untypeable character can never be injected.
        InjectPlan plan = InputInjector.Plan(password);
        if (_jsonMode)
        {
            WriteJsonLine(new
            {
                passwordSet = true,
                length = password.Length,
                typeable = plan.Ok,
                unsupported = plan.Unsupported,
                dpapiScope = Secret.ScopeFromEnvironment().ToString(),
                hostJson = HostPaths.HostJson,
            });
        }
        else
        {
            Console.WriteLine($"password saved ({password.Length} characters, DPAPI {Secret.ScopeFromEnvironment()}).");
            if (!plan.Ok)
            {
                Console.WriteLine();
                Console.WriteLine("WARNING: this password cannot be typed by the current keyboard layout:");
                Console.WriteLine($"  {plan.Unsupported}");
                Console.WriteLine("  Unlock attempts will return reason=\"unsupported\" and type nothing.");
                Console.WriteLine("  Use a password made of plain ASCII characters.");
            }
        }
        return 0;
    }

    private static string ReadHidden()
    {
        var sb = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (key.KeyChar != '\0') sb.Append(key.KeyChar);
        }
        return sb.ToString();
    }

    // --------------------------------------------------------------------- run

    private static async Task<int> CmdRunAsync(string[] args)
    {
        // --quiet is the autostart mode: no console output at all (the window is
        // hidden), everything important goes to service.log instead.
        bool quiet = args.Contains("--quiet");
        void Log(string message)
        {
            if (quiet) ServiceLog.Write(message);
            else if (!_jsonMode) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
        }

        if (quiet) Native.HideConsoleWindow();

        HostStore store = HostStore.Load();

        RuntimeState? existing = RuntimeState.TryRead();
        if (existing is not null && existing.IsLive())
        {
            string message = $"another {ToolName} run instance is alive (pid {existing.Pid}); refusing to start a second one.";
            if (quiet) ServiceLog.Write($"[error] {message}");
            else Console.Error.WriteLine(message);
            return 3;
        }

        if (args.Contains("--enable-radio")) await TryEnableRadioAsync(warn: !quiet);

        using var server = new GattUnlockServer(store, Log);

        GattStartResult start = await server.StartAsync();
        if (!start.Ok)
        {
            if (quiet)
            {
                ServiceLog.Write($"[fatal] failed to start: {start.Detail}");
                ServiceLog.Write("[hint] make sure Bluetooth is on (Windows Settings > Bluetooth & devices); " +
                                 "run 'tctool-unlock status --json' for the current state.");
            }
            else if (_jsonMode)
            {
                WriteJsonLine(new { running = false, advertising = false, detail = start.Detail, appVersion = ToolVersion, protocol = ProtocolVersion });
            }
            else
            {
                Console.Error.WriteLine($"failed to start: {start.Detail}");
                Console.Error.WriteLine("hint: make sure Bluetooth is on (Windows Settings > Bluetooth & devices).");
            }
            return 1;
        }

        double? duration = null;
        string? durationArg = GetOption(args, "--duration");
        if (durationArg is not null)
        {
            if (!double.TryParse(durationArg, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            {
                if (quiet) ServiceLog.Write("[error] --duration expects a number of seconds");
                else Console.Error.WriteLine("--duration expects a number of seconds");
                return 2;
            }
            duration = seconds;
        }

        var state = new RuntimeState
        {
            Pid = Environment.ProcessId,
            StartedAt = DateTime.UtcNow.ToString("o"),
            UpdatedAt = DateTime.UtcNow.ToString("o"),
            Advertising = server.Advertising,
            Detail = start.Detail,
            Version = ToolVersion,
        };
        state.Write();

        if (quiet)
        {
            ServiceLog.Write($"[start] {ToolName} {ToolVersion} (protocol {ProtocolVersion}) pid={Environment.ProcessId}");
            ServiceLog.Write($"[start] advertising={server.Advertising} ({start.Detail}); " +
                             $"paired={store.HasPsk} passwordSet={store.HasPassword}");
            if (!store.HasPsk) ServiceLog.Write("[warn] not paired yet - run 'pair'; the phone cannot authenticate.");
            if (!store.HasPassword) ServiceLog.Write("[warn] no unlock password stored - unlock would return no-password.");
            ServiceLog.Write($"[start] service={ProtocolUuids.Service:D} challenge={ProtocolUuids.Challenge:D} command={ProtocolUuids.Command:D}");
        }
        else if (_jsonMode)
        {
            WriteJsonLine(new
            {
                running = true,
                advertising = server.Advertising,
                serviceUuid = ProtocolUuids.Service.ToString("D"),
                challengeUuid = ProtocolUuids.Challenge.ToString("D"),
                commandUuid = ProtocolUuids.Command.ToString("D"),
                paired = store.HasPsk,
                passwordSet = store.HasPassword,
                hostId = store.State.HostId,
                hostName = store.State.HostName,
                detail = start.Detail,
                appVersion = ToolVersion,
                protocol = ProtocolVersion,
            });
        }
        else
        {
            Console.WriteLine($"{ToolName} {ToolVersion} running (protocol {ProtocolVersion})");
            Console.WriteLine($"  service   : {ProtocolUuids.Service:D}");
            Console.WriteLine($"  challenge : {ProtocolUuids.Challenge:D}  (Read + Notify)");
            Console.WriteLine($"  command   : {ProtocolUuids.Command:D}  (Write)");
            Console.WriteLine($"  advertising: {server.Advertising} ({start.Detail})");
            Console.WriteLine($"  paired    : {store.HasPsk}   passwordSet: {store.HasPassword}");
            if (!store.HasPsk)
                Console.WriteLine("  WARNING: not paired yet - run 'pair' first, the phone cannot authenticate.");
            if (!store.HasPassword)
                Console.WriteLine("  WARNING: no unlock password stored - 'unlock' would return no-password.");
            Console.WriteLine("  press Ctrl+C to stop");
            Console.WriteLine();
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => state.Delete();

        var startedAt = Stopwatch.StartNew();
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (duration.HasValue && startedAt.Elapsed.TotalSeconds >= duration.Value) break;
                try { await Task.Delay(500, cts.Token); }
                catch (OperationCanceledException) { break; }

                state.Advertising = server.Advertising;
                state.PeerConnected = server.PeerConnected;
                state.Connections = server.Connections;
                state.Unlocks = server.Unlocks;
                state.Detail = server.LastDetail;
                state.UpdatedAt = DateTime.UtcNow.ToString("o");
                state.Write();
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C.
        }
        finally
        {
            server.Stop();
            state.Delete();
        }

        if (quiet) ServiceLog.Write("[stop] service stopped");
        else if (!_jsonMode) Console.WriteLine("stopped.");
        return 0;
    }

    // ---------------------------------------------------------------- selftest

    /// <summary>
    /// §8 authoritative vectors, frozen by the independent Node.js reference
    /// implementation (tests/unlock/ref-vectors.mjs) and transcribed into
    /// docs/UNLOCK-PROTOCOL.md §8. These constants are EXPECTATIONS: if the
    /// product code ever drifts, this test fails rather than silently agreeing
    /// with itself.
    /// </summary>
    private const string ExpectedKSession = "21f2a1a3890968e1da28553de67b49cab6ab2ffb7f3ecbaec72d284852b9af47";
    private const string ExpectedProof = "353d6fdd6e73a63620389b806711e6c2c726407045d4c7ddae29264a9e8bab10";
    private const string ExpectedPskBase64Url = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    private const string ExpectedSealFrame =
        "000000000000000100000000678def935e80e0eeb8ca5a9ee9bbef97f6a2c4302b392deeb76b81f496665496c6";
    private const string ExpectedSealCiphertext = "678def935e80e0eeb8ca5a9ee9bbef97f6";
    private const string ExpectedSealTag = "a2c4302b392deeb76b81f496665496c6";
    private const string ExpectedIv = "000000000000000100000000";
    private const string TestPayload = "{\"type\":\"unlock\"}";

    private static int CmdSelfTest(string[] args)
    {
        var checks = new List<SelfTestCheck>();
        void Check(string name, bool ok, string? detail = null) => checks.Add(new SelfTestCheck(name, ok, detail));

        byte[] psk = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] nonce = Enumerable.Range(0x20, 32).Select(i => (byte)i).ToArray();
        const string hostId = "3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01";
        const string peerId = "8a7b6c5d-4e3f-4a2b-9c8d-7e6f5a4b3c2d";

        Check("§8 inputs: PSK 00..1f / NONCE 20..3f / 36-byte ASCII UUIDs",
            psk.Length == 32 && psk[0] == 0 && psk[31] == 0x1f &&
            nonce.Length == 32 && nonce[0] == 0x20 && nonce[31] == 0x3f &&
            Encoding.ASCII.GetByteCount(hostId) == 36 && Encoding.ASCII.GetByteCount(peerId) == 36);

        byte[] kSession = Proto.DeriveKSession(psk, nonce);
        byte[] proof = Proto.DeriveProof(psk, nonce, hostId, peerId);

        Check("K_session matches the frozen §8 vector", Proto.ToHex(kSession) == ExpectedKSession,
            Proto.ToHex(kSession));
        Check("PROOF matches the frozen §8 vector", Proto.ToHex(proof) == ExpectedProof,
            Proto.ToHex(proof));
        Check("base64url(PSK) matches the frozen vector (43 chars, no padding)",
            Proto.Base64UrlEncode(psk) == ExpectedPskBase64Url,
            Proto.Base64UrlEncode(psk));

        byte[] frame = Proto.Seal(kSession, 1, Encoding.UTF8.GetBytes(TestPayload));
        Check("SEAL(K_session, 1, \"{\\\"type\\\":\\\"unlock\\\"}\") matches the frozen 45-byte frame",
            Proto.ToHex(frame) == ExpectedSealFrame, Proto.ToHex(frame));
        Check("SEAL frame length identity 12 + len(plaintext) + 16",
            frame.Length == 12 + Encoding.UTF8.GetByteCount(TestPayload) + 16,
            $"{frame.Length} bytes");
        Check("IV layout = counter big-endian (8) || 00000000 (4)",
            Proto.ToHex(frame.AsSpan(0, 12).ToArray()) == ExpectedIv &&
            Proto.ReadCounter(frame) == 1);

        bool opened = Proto.TryOpen(kSession, frame, out byte[] plaintext, out ulong openedCounter, out string openError);
        Check("OPEN round trip returns the original plaintext with counter 1",
            opened && Encoding.UTF8.GetString(plaintext) == TestPayload && openedCounter == 1,
            opened ? $"counter={openedCounter}" : openError);

        // The counter rule the server uses (shared helper, not a re-implementation).
        ulong lastAccepted = 0;
        bool firstAccepted = Proto.ShouldAcceptCounter(openedCounter, lastAccepted);
        if (firstAccepted) lastAccepted = openedCounter;
        bool replayAccepted = Proto.ShouldAcceptCounter(1, lastAccepted);
        Check("counter replay rejected: counter must be strictly greater than the last accepted one",
            firstAccepted && !replayAccepted);
        Check("counter 2 accepted after counter 1", Proto.ShouldAcceptCounter(2, lastAccepted));

        byte[] tamperedTag = (byte[])frame.Clone();
        tamperedTag[^1] ^= 0x01;
        bool tagOpened = Proto.TryOpen(kSession, tamperedTag, out _, out _, out string tagError);
        Check("tampered GCM tag rejected with decrypt-failed",
            !tagOpened && tagError == ErrorCodes.DecryptFailed, tagError);

        byte[] tamperedCiphertext = (byte[])frame.Clone();
        tamperedCiphertext[12] ^= 0x80;
        bool ctOpened = Proto.TryOpen(kSession, tamperedCiphertext, out _, out _, out string ctError);
        Check("tampered ciphertext rejected with decrypt-failed",
            !ctOpened && ctError == ErrorCodes.DecryptFailed, ctError);

        byte[] badPadding = (byte[])frame.Clone();
        badPadding[11] = 0x01;
        bool paddingOpened = Proto.TryOpen(kSession, badPadding, out _, out _, out string paddingError);
        Check("non-zero IV padding rejected with bad-frame",
            !paddingOpened && paddingError == ErrorCodes.BadFrame, paddingError);

        bool shortOpened = Proto.TryOpen(kSession, frame.AsSpan(0, 28).ToArray(), out _, out _, out string shortError);
        Check("28-byte frame rejected with bad-frame",
            !shortOpened && shortError == ErrorCodes.BadFrame, shortError);

        byte[] wrongKey = new byte[32];
        RandomNumberGenerator.Fill(wrongKey);
        bool wrongKeyOpened = Proto.TryOpen(wrongKey, frame, out _, out _, out string wrongKeyError);
        Check("wrong key rejected with decrypt-failed",
            !wrongKeyOpened && wrongKeyError == ErrorCodes.DecryptFailed, wrongKeyError);

        string longPlaintext = "{\"type\":\"unlock\",\"pad\":\"" + new string('x', 180) + "\"}";
        byte[] longFrame = Proto.Seal(kSession, 7, Encoding.UTF8.GetBytes(longPlaintext));
        bool longOpened = Proto.TryOpen(kSession, longFrame, out byte[] longPlaintextBytes, out ulong longCounter, out string longError);
        Check("multi-block plaintext (200 bytes, counter 7) round trips",
            longOpened && Encoding.UTF8.GetString(longPlaintextBytes) == longPlaintext && longCounter == 7 && longError.Length == 0,
            $"{longFrame.Length} byte frame");

        byte[] wrongPeer = Proto.DeriveProof(psk, nonce, hostId, "00000000-0000-4000-8000-000000000000");
        Check("PROOF with a different PEER_ID does not verify",
            !Proto.FixedTimeEquals(wrongPeer, proof));
        Check("FixedTimeEquals accepts identical buffers and rejects different ones",
            Proto.FixedTimeEquals(proof, (byte[])proof.Clone()) && !Proto.FixedTimeEquals(proof, wrongPeer));

        string b64 = Proto.Base64UrlEncode(psk);
        byte[] b64RoundTrip = Proto.Base64UrlDecode(b64);
        Check("base64url round trip is byte-exact and padding-free",
            b64RoundTrip.SequenceEqual(psk) && !b64.Contains('=') && b64.Length == 43);

        Check("§3.1 UUIDs match the frozen protocol constants",
            ProtocolUuids.Service == new Guid("7a1c9e40-2f3d-4b6c-9a11-6c5d3e8f2b01") &&
            ProtocolUuids.Challenge == new Guid("7a1c9e41-2f3d-4b6c-9a11-6c5d3e8f2b01") &&
            ProtocolUuids.Command == new Guid("7a1c9e42-2f3d-4b6c-9a11-6c5d3e8f2b01"));

        Check("frame constants: MinSealedLength=29, MaxNotifyPayload=180, PROOF=32",
            Proto.MinSealedLength == 29 && Proto.MaxNotifyPayload == 180 &&
            Proto.ProofLength == 32 && Proto.PskLength == 32 && Proto.NonceLength == 32);

        // A real "ready" notification must stay inside the §3.2 notify budget.
        byte[] readyPayload = Encoding.UTF8.GetBytes("{\"type\":\"ready\",\"sid\":\"deadbeef\"}");
        byte[] readyFrame = Proto.Seal(kSession, 1, readyPayload);
        Check("host 'ready' notification fits the §3.2 180-byte notify cap",
            readyFrame.Length <= Proto.MaxNotifyPayload, $"{readyFrame.Length} bytes");

        // The worst case is a long error.msg: it must be clamped, never sent over budget.
        var clampMessage = new GattUnlockServer.WireMessage
        {
            Type = "error",
            Code = ErrorCodes.BadFrame,
            Msg = new string('x', 400),
        };
        byte[] clampedJson = NotifyBudget.Serialize(clampMessage, out bool clampTrimmed);
        byte[] clampedFrame = Proto.Seal(kSession, 2, clampedJson);
        Check("§3.2: an over-long error message is clamped so the sealed notify frame stays <= 180 bytes",
            clampTrimmed && clampedJson.Length <= NotifyBudget.MaxPlaintext &&
            clampedFrame.Length <= Proto.MaxNotifyPayload,
            $"{clampedFrame.Length} bytes (plaintext {clampedJson.Length} <= {NotifyBudget.MaxPlaintext})");

        // ---- policy rules that used to be reachable only through a live GATT link.
        // These assert the SAME helpers the host calls (UnlockPolicy / InputInjector),
        // so a future regression in the unlock path shows up without a phone.
        bool throttleCases =
            !UnlockPolicy.ShouldThrottle(1000, 0) &&        // first unlock of the session
            UnlockPolicy.ShouldThrottle(1000, 1000) &&      // immediate retry
            UnlockPolicy.ShouldThrottle(2499, 1000) &&      // 1499 ms later
            !UnlockPolicy.ShouldThrottle(2500, 1000);       // exactly 1500 ms later
        Check("§5.5 throttle: sliding window of 1500 ms, boundary at exactly 1500 ms is allowed",
            throttleCases && UnlockPolicy.UnlockThrottleMs == 1500);

        var failureRun = new List<UnlockPolicy.ProofFailureOutcome>();
        int runningFailCount = 0;
        for (int i = 0; i < UnlockPolicy.MaxProofFailures; i++)
        {
            UnlockPolicy.ProofFailureOutcome outcome = UnlockPolicy.RegisterProofFailure(runningFailCount);
            failureRun.Add(outcome);
            runningFailCount = outcome.FailCount;
        }
        Check("§3.3.1: only the 5th consecutive PROOF failure invalidates the PSK",
            failureRun.Take(4).All(o => !o.InvalidatePsk) &&
            failureRun.Select(o => o.FailCount).SequenceEqual(new[] { 1, 2, 3, 4, 0 }) &&
            failureRun[4].InvalidatePsk);

        Check("§5.4 pre-injection decision matrix (no-password beats not-locked; injectWhenUnlocked overrides)",
            UnlockPolicy.RejectionReason(hasPassword: false, locked: true, injectWhenUnlocked: false) == Reasons.NoPassword &&
            UnlockPolicy.RejectionReason(true, false, false) == Reasons.NotLocked &&
            UnlockPolicy.RejectionReason(true, false, true) == null &&
            UnlockPolicy.RejectionReason(true, true, false) == null);

        const string asciiPassword = "Ab3xYz";
        InjectPlan asciiPlan = InputInjector.Plan(asciiPassword);
        InjectPlan badPlan = InputInjector.Plan("p\u00e4\u4e2d"); // 'ä' (AltGr on many layouts) + '中' (no key at all)
        Check("§5.3: an untypeable character aborts the entire plan (0 keystrokes); ASCII plans len+1 keys",
            asciiPlan.Ok && asciiPlan.Strokes.Count == asciiPassword.Length + 1 &&
            !badPlan.Ok && badPlan.Strokes.Count == 0 && badPlan.Unsupported is not null,
            badPlan.Unsupported);

        Check("§6 error codes and §3.3.2 reasons are exactly the documented set",
            ErrorCodes.BadFrame == "bad-frame" && ErrorCodes.NotAuthenticated == "not-authenticated" &&
            ErrorCodes.AuthFailed == "auth-failed" && ErrorCodes.Replay == "replay" &&
            ErrorCodes.DecryptFailed == "decrypt-failed" && ErrorCodes.NoPasswordSet == "nopsz" &&
            ErrorCodes.Busy == "busy" &&
            Reasons.Ok == "ok" && Reasons.NoPassword == "no-password" && Reasons.NotLocked == "not-locked" &&
            Reasons.InjectFailed == "inject-failed" && Reasons.Unsupported == "unsupported" &&
            Reasons.Throttled == "throttled");

        // Pairing payload / QR pipeline (no Bluetooth involved).
        string pairingJson = "{\"v\":1,\"p\":\"tcunlock\",\"id\":\"" + hostId + "\",\"name\":\"" +
                             new string('X', 16) + "\",\"psk\":\"" + Proto.Base64UrlEncode(psk) + "\"}";
        bool qrOk = QrCode.TryEncode(Encoding.UTF8.GetBytes(pairingJson), QrEcc.M, out QrMatrix? qrMatrix, out string qrError);
        byte[] png = qrOk ? QrCode.ToPng(qrMatrix!) : Array.Empty<byte>();
        Check("pairing payload encodes to QR and renders to PNG",
            qrOk && qrMatrix!.Size == qrMatrix.Version * 4 + 17 && QrCode.ToAsciiArt(qrMatrix).Length > 0 &&
            png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47,
            qrOk ? $"version {qrMatrix!.Version} size {qrMatrix.Size} mask {qrMatrix.Mask}" : qrError);

        bool allPassed = checks.All(c => c.Ok);
        var report = new SelfTestReport
        {
            Tool = ToolName,
            Version = ToolVersion,
            ProtocolVersion = ProtocolVersion,
            Pass = allPassed,
            ChecksPassed = checks.Count(c => c.Ok),
            ChecksTotal = checks.Count,
            Vectors = new SelfTestVectors
            {
                PskHex = Proto.ToHex(psk),
                NonceHex = Proto.ToHex(nonce),
                HostId = hostId,
                PeerId = peerId,
                KSessionHex = Proto.ToHex(kSession),
                ProofHex = Proto.ToHex(proof),
                PskBase64Url = Proto.Base64UrlEncode(psk),
                Seal = new SelfTestSeal
                {
                    Plaintext = TestPayload,
                    Counter = 1,
                    IvHex = Proto.ToHex(frame.AsSpan(0, 12).ToArray()),
                    CiphertextHex = Proto.ToHex(frame.AsSpan(12, frame.Length - 28).ToArray()),
                    TagHex = Proto.ToHex(frame.AsSpan(frame.Length - 16, 16).ToArray()),
                    FrameHex = Proto.ToHex(frame),
                    FrameLen = frame.Length,
                },
            },
            Checks = checks,
        };

        string json = Proto.ToCliJson(report);
        string? outPath = GetOption(args, "--out");
        if (outPath is not null)
        {
            string full = Path.GetFullPath(outPath);
            string? directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(full, json + Environment.NewLine, new UTF8Encoding(false));
            outPath = full;
        }

        if (_jsonMode)
        {
            Console.WriteLine(json);
        }
        else
        {
            Console.WriteLine($"=== {ToolName} selftest (protocol {ProtocolVersion}, no Bluetooth required) ===");
            Console.WriteLine($"PSK            = {Proto.ToHex(psk)}");
            Console.WriteLine($"NONCE          = {Proto.ToHex(nonce)}");
            Console.WriteLine($"HOST_ID        = {hostId}");
            Console.WriteLine($"PEER_ID        = {peerId}");
            Console.WriteLine($"K_session      = {Proto.ToHex(kSession)}");
            Console.WriteLine($"PROOF          = {Proto.ToHex(proof)}");
            Console.WriteLine($"psk_base64url  = {Proto.Base64UrlEncode(psk)}");
            Console.WriteLine($"SEAL plaintext = {TestPayload} (counter=1)");
            Console.WriteLine($"  IV (12B)     = {Proto.ToHex(frame.AsSpan(0, 12).ToArray())}");
            Console.WriteLine($"  ciphertext   = {Proto.ToHex(frame.AsSpan(12, frame.Length - 28).ToArray())}");
            Console.WriteLine($"  tag (16B)    = {Proto.ToHex(frame.AsSpan(frame.Length - 16, 16).ToArray())}");
            Console.WriteLine($"  FRAME({frame.Length}B)   = {Proto.ToHex(frame)}");
            Console.WriteLine();
            foreach (SelfTestCheck check in checks)
            {
                Console.WriteLine($"  [{(check.Ok ? "PASS" : "FAIL")}] {check.Name}" +
                                  (check.Detail is null ? string.Empty : $"   ({check.Detail})"));
            }
            Console.WriteLine();
            Console.WriteLine($"{checks.Count(c => c.Ok)}/{checks.Count} checks passed");
            if (outPath is not null) Console.WriteLine($"json written to: {outPath}");
        }

        return allPassed ? 0 : 4;
    }

    // ------------------------------------------------------------------ qrdump

    /// <summary>
    /// Diagnostic: print the QR module matrix so the encoder can be compared
    /// module-for-module against an independent implementation
    /// (see tools/verify-qr.py). --mask forces a mask pattern 0..7.
    /// </summary>
    private static int CmdQrDump(string[] args)
    {
        string? text = GetOption(args, "--text");
        if (text is null)
        {
            Console.Error.WriteLine("qrdump --text <payload> [--ecc L|M|Q|H] [--mask 0..7] [--json]");
            return 2;
        }

        QrEcc ecc = (GetOption(args, "--ecc") ?? "M").ToUpperInvariant() switch
        {
            "L" => QrEcc.L,
            "Q" => QrEcc.Q,
            "H" => QrEcc.H,
            _ => QrEcc.M,
        };

        int? mask = null;
        string? maskArg = GetOption(args, "--mask");
        if (maskArg is not null)
        {
            if (!int.TryParse(maskArg, out int m) || m < 0 || m > 7)
            {
                Console.Error.WriteLine("--mask must be an integer 0..7");
                return 2;
            }
            mask = m;
        }

        if (!QrCode.TryEncode(Encoding.UTF8.GetBytes(text), ecc, out QrMatrix? matrix, out string error, mask))
        {
            Console.Error.WriteLine($"qrdump: {error}");
            return 1;
        }

        string? pngPath = GetOption(args, "--png");
        if (pngPath is not null)
        {
            try
            {
                File.WriteAllBytes(pngPath, QrCode.ToPng(matrix!));
                if (!_jsonMode) Console.WriteLine($"png written: {Path.GetFullPath(pngPath)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"qrdump: writing {pngPath} failed: {ex.Message}");
                return 1;
            }
        }

        if (_jsonMode)
        {
            var rows = new List<string>(matrix!.Size);
            for (int r = 0; r < matrix.Size; r++)
            {
                var line = new StringBuilder(matrix.Size);
                for (int c = 0; c < matrix.Size; c++) line.Append(matrix[r, c] ? '1' : '0');
                rows.Add(line.ToString());
            }

            WriteJsonLine(new
            {
                text,
                version = matrix.Version,
                ecc = matrix.Ecc.ToString(),
                mask = matrix.Mask,
                size = matrix.Size,
                rows,
            });
            return 0;
        }

        Console.WriteLine($"version={matrix!.Version} ecc={matrix.Ecc} mask={matrix.Mask} size={matrix.Size}");
        for (int r = 0; r < matrix.Size; r++)
        {
            var line = new StringBuilder(matrix.Size);
            for (int c = 0; c < matrix.Size; c++) line.Append(matrix[r, c] ? '#' : '.');
            Console.WriteLine(line.ToString());
        }
        return 0;
    }

    // ----------------------------------------------------------------- version
    private static int CmdVersion(string[] args)
    {
        if (_jsonMode)
        {
            WriteJsonLine(new
            {
                tool = ToolName,
                // The C++ front-end and the installer are versioned separately from
                // the wire protocol, so both are always reported explicitly.
                appVersion = ToolVersion,
                protocol = ProtocolVersion,
                version = ToolVersion,
                protocolVersion = ProtocolVersion,
                targetFramework = "net8.0-windows10.0.19041.0",
                minWindows = "10.0.16299.0",
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            });
        }
        else
        {
            Console.WriteLine($"{ToolName} {ToolVersion} (unlock protocol {ProtocolVersion})");
        }
        return 0;
    }

    // -------------------------------------------------------------- autostart

    private static int CmdAutostart(string[] args)
    {
        string action = args.Length > 0 && !args[0].StartsWith("-") ? args[0].ToLowerInvariant() : "status";
        string? exeOverride = GetOption(args, "--exe");
        string exePath = Autostart.ResolveExePath(exeOverride);
        string method = GetOption(args, "--method") ?? "auto";

        switch (action)
        {
            case "status":
            {
                Autostart.State state = Autostart.Probe(exePath);
                if (_jsonMode)
                {
                    WriteJsonLine(new AutostartPayload
                    {
                        Enabled = state.Enabled,
                        Scope = Autostart.Scope,
                        Method = state.Method,
                        TaskName = state.TaskName,
                        Command = state.Command,
                        ExePath = state.ExePath,
                        ExeExists = state.ExeExists,
                        TaskExists = state.TaskExists,
                        TaskTargetsUs = state.TaskTargetsUs,
                        RunKeyExists = state.RunKeyExists,
                        RunKeyTargetsUs = state.RunKeyTargetsUs,
                        StartupShortcutExists = state.StartupShortcutExists,
                        StartupShortcutTargetsUs = state.StartupShortcutTargetsUs,
                        StartupShortcutPath = state.StartupShortcutPath,
                        Detail = state.Detail,
                        ServiceLog = HostPaths.ServiceLog,
                        AppVersion = ToolVersion,
                        Protocol = ProtocolVersion,
                    });
                }
                else
                {
                    Console.WriteLine($"autostart enabled : {state.Enabled}   (scope: {Autostart.Scope})");
                    Console.WriteLine($"mechanism         : {state.Method}");
                    Console.WriteLine($"task name         : {state.TaskName}   exists={state.TaskExists} ours={state.TaskTargetsUs}");
                    Console.WriteLine($"HKCU Run value    : exists={state.RunKeyExists} ours={state.RunKeyTargetsUs}");
                    Console.WriteLine($"command           : {state.Command}");
                    Console.WriteLine($"executable        : {state.ExePath}   exists={state.ExeExists}");
                    Console.WriteLine($"service log       : {HostPaths.ServiceLog}");
                    Console.WriteLine($"probe detail      : {state.Detail}");
                    if (!state.Enabled)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Autostart is OFF (the default). Enable it with:");
                        Console.WriteLine($"  {ToolName} autostart enable");
                    }
                }
                return 0;
            }

            case "enable":
            {
                bool ok = Autostart.Enable(exePath, method, out string detail);
                Autostart.State state = Autostart.Probe(exePath);
                HostStore store = HostStore.Load();

                if (_jsonMode)
                {
                    WriteJsonLine(new AutostartPayload
                    {
                        Enabled = state.Enabled,
                        Scope = Autostart.Scope,
                        Method = state.Method,
                        TaskName = state.TaskName,
                        Command = state.Command,
                        ExePath = state.ExePath,
                        ExeExists = state.ExeExists,
                        TaskExists = state.TaskExists,
                        TaskTargetsUs = state.TaskTargetsUs,
                        RunKeyExists = state.RunKeyExists,
                        RunKeyTargetsUs = state.RunKeyTargetsUs,
                        StartupShortcutExists = state.StartupShortcutExists,
                        StartupShortcutTargetsUs = state.StartupShortcutTargetsUs,
                        StartupShortcutPath = state.StartupShortcutPath,
                        Detail = detail,
                        ServiceLog = HostPaths.ServiceLog,
                        AppVersion = ToolVersion,
                        Protocol = ProtocolVersion,
                    });
                    return ok ? 0 : 1;
                }

                Console.WriteLine(ok ? "autostart enabled." : "autostart could NOT be enabled.");
                Console.WriteLine($"  mechanism : {state.Method}  (scope: {Autostart.Scope}, no administrator rights required)");
                Console.WriteLine($"  command   : {state.Command}");
                Console.WriteLine($"  detail    : {detail}");
                Console.WriteLine($"  runs at   : user logon, in the user session (needed for SendInput)");
                if (!store.HasPsk)
                    Console.WriteLine("  WARNING: not paired yet - the autostarted service will advertise but nobody can unlock until you run 'pair'.");
                if (!store.HasPassword)
                    Console.WriteLine("  WARNING: no unlock password stored - unlock would return \"no-password\".");
                Console.WriteLine($"  logs      : {HostPaths.ServiceLog}");
                return ok ? 0 : 1;
            }

            case "disable":
            {
                bool ok = Autostart.Disable(exePath, out string detail);
                Autostart.State state = Autostart.Probe(exePath);

                if (_jsonMode)
                {
                    WriteJsonLine(new AutostartPayload
                    {
                        Enabled = state.Enabled,
                        Scope = Autostart.Scope,
                        Method = state.Method,
                        TaskName = state.TaskName,
                        Command = state.Command,
                        ExePath = state.ExePath,
                        ExeExists = state.ExeExists,
                        TaskExists = state.TaskExists,
                        TaskTargetsUs = state.TaskTargetsUs,
                        RunKeyExists = state.RunKeyExists,
                        RunKeyTargetsUs = state.RunKeyTargetsUs,
                        StartupShortcutExists = state.StartupShortcutExists,
                        StartupShortcutTargetsUs = state.StartupShortcutTargetsUs,
                        StartupShortcutPath = state.StartupShortcutPath,
                        Detail = detail,
                        ServiceLog = HostPaths.ServiceLog,
                        AppVersion = ToolVersion,
                        Protocol = ProtocolVersion,
                    });
                    return ok ? 0 : 1;
                }

                Console.WriteLine(ok ? "autostart disabled." : "autostart could NOT be fully disabled.");
                Console.WriteLine($"  detail : {detail}");
                Console.WriteLine($"  state  : enabled={state.Enabled} mechanism={state.Method}");
                return ok ? 0 : 1;
            }

            default:
                Console.Error.WriteLine($"{ToolName} autostart: unknown action '{action}' (expected status|enable|disable)");
                return 2;
        }
    }

    // ----------------------------------------------------------------- helpers

    private static async Task<bool> TryEnableRadioAsync(bool warn)
    {
        try
        {
            IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
            Radio? bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (bluetooth is null)
            {
                if (warn) Console.Error.WriteLine("--enable-radio: no Bluetooth radio reported by Windows");
                return false;
            }
            if (bluetooth.State == RadioState.On) return true;

            RadioAccessStatus status = await bluetooth.SetStateAsync(RadioState.On);
            if (warn) Console.Error.WriteLine($"--enable-radio: SetStateAsync(On) -> {status}");
            await Task.Delay(1200);
            return status == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            if (warn) Console.Error.WriteLine($"--enable-radio failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i][(name.Length + 1)..];
        }
        return null;
    }

    private static void Info(string message)
    {
        if (_jsonMode) Console.Error.WriteLine(message);
        else Console.WriteLine(message);
    }

    /// <summary>Single-line, ASCII-only JSON on stdout (see Proto.CliJson).</summary>
    private static void WriteJsonLine<T>(T value) => Console.WriteLine(Proto.ToCliJson(value));

    // ------------------------------------------------------------- DTOs (CLI)

    private sealed class StatusPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("paired")] public bool Paired { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("advertising")] public bool Advertising { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("passwordSet")] public bool PasswordSet { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("hostId")] public string HostId { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("hostName")] public string HostName { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("peerName")] public string PeerName { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("pskValid")] public bool PskValid { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("running")] public bool Running { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("injectWhenUnlocked")] public bool InjectWhenUnlocked { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("hostJson")] public string HostJsonPath { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("advertisingDetail")] public string AdvertisingDetail { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("protocol")] public int Protocol { get; set; }
    }

    /// <summary>autostart status/enable/disable JSON. Always single-line, ASCII-only.</summary>
    private sealed class AutostartPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("scope")] public string Scope { get; set; } = "user";
        [System.Text.Json.Serialization.JsonPropertyName("method")] public string Method { get; set; } = "none";
        [System.Text.Json.Serialization.JsonPropertyName("taskName")] public string TaskName { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("exePath")] public string ExePath { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("exeExists")] public bool ExeExists { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("taskExists")] public bool TaskExists { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("taskTargetsUs")] public bool TaskTargetsUs { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("runKeyExists")] public bool RunKeyExists { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("runKeyTargetsUs")] public bool RunKeyTargetsUs { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("startupShortcutExists")] public bool StartupShortcutExists { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("startupShortcutTargetsUs")] public bool StartupShortcutTargetsUs { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("startupShortcutPath")] public string StartupShortcutPath { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("detail")] public string Detail { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("serviceLog")] public string ServiceLog { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("protocol")] public int Protocol { get; set; }
    }

    private sealed class PairingPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("v")] public int V { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("p")] public string P { get; set; } = "tcunlock";
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("psk")] public string? Psk { get; set; }
    }

    private sealed class PairShowPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("paired")] public bool Paired { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("pskValid")] public bool PskValid { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("pskMasked")] public string? PskMasked { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("hostId")] public string HostId { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("hostName")] public string HostName { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("peerId")] public string? PeerId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("peerName")] public string? PeerName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("pairedAt")] public string? PairedAt { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("failCount")] public int FailCount { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("hostJson")] public string HostJsonPath { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("payloadTxt")] public string? PayloadPath { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("payloadPng")] public string? PayloadPngPath { get; set; }
    }

    private sealed class PairResultPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("paired")] public bool Paired { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("replaced")] public bool Replaced { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("hostId")] public string HostId { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("hostName")] public string HostName { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("payload")] public string Payload { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("payloadTxt")] public string PayloadPath { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("payloadPng")] public string? PayloadPngPath { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("pskMasked")] public string PskMasked { get; set; } = string.Empty;
    }

    private sealed class SelfTestReport
    {
        [System.Text.Json.Serialization.JsonPropertyName("tool")] public string Tool { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("pass")] public bool Pass { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("checks_passed")] public int ChecksPassed { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("checks_total")] public int ChecksTotal { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("vectors")] public SelfTestVectors Vectors { get; set; } = new();
        [System.Text.Json.Serialization.JsonPropertyName("checks")] public List<SelfTestCheck> Checks { get; set; } = new();
    }

    private sealed class SelfTestVectors
    {
        [System.Text.Json.Serialization.JsonPropertyName("psk_hex")] public string PskHex { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("nonce_hex")] public string NonceHex { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("host_id")] public string HostId { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("peer_id")] public string PeerId { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("k_session_hex")] public string KSessionHex { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("proof_hex")] public string ProofHex { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("psk_base64url")] public string PskBase64Url { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("seal")] public SelfTestSeal Seal { get; set; } = new();
    }

    private sealed class SelfTestSeal
    {
        [System.Text.Json.Serialization.JsonPropertyName("plaintext")] public string Plaintext { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("counter")] public int Counter { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("iv_hex")] public string IvHex { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("ciphertext_hex")] public string CiphertextHex { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("tag_hex")] public string TagHex { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("frame_hex")] public string FrameHex { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("frame_len")] public int FrameLen { get; set; }
    }

    private sealed class SelfTestCheck
    {
        public SelfTestCheck(string name, bool ok, string? detail = null)
        {
            Name = name;
            Ok = ok;
            Detail = detail;
        }

        [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; }
        [System.Text.Json.Serialization.JsonPropertyName("ok")] public bool Ok { get; }
        [System.Text.Json.Serialization.JsonPropertyName("detail")] public string? Detail { get; }
    }
}
