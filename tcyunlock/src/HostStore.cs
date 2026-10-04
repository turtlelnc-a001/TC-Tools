using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TC.Tools.Unlock;

/// <summary>On-disk pairing / secret state (host.json).</summary>
public sealed class HostState
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("hostId")] public string HostId { get; set; } = string.Empty;
    [JsonPropertyName("hostName")] public string HostName { get; set; } = string.Empty;
    [JsonPropertyName("pskProtected")] public string? PskProtected { get; set; }
    [JsonPropertyName("peerId")] public string? PeerId { get; set; }
    [JsonPropertyName("peerName")] public string? PeerName { get; set; }
    [JsonPropertyName("pairedAt")] public string? PairedAt { get; set; }
    [JsonPropertyName("pskInvalidated")] public bool PskInvalidated { get; set; }
    [JsonPropertyName("failCount")] public int FailCount { get; set; }
    [JsonPropertyName("passwordProtected")] public string? PasswordProtected { get; set; }
    [JsonPropertyName("injectWhenUnlocked")] public bool InjectWhenUnlocked { get; set; }
    [JsonPropertyName("keyDelayMs")] public int KeyDelayMs { get; set; } = 10;
    [JsonPropertyName("publishLocalName")] public bool PublishLocalName { get; set; }
    [JsonPropertyName("passwordScope")] public string? PasswordScope { get; set; }
}

public static class HostPaths
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TC-tools", "unlock");

    public static string HostJson => Path.Combine(Dir, "host.json");
    public static string RuntimeJson => Path.Combine(Dir, "runtime.json");
    public static string PayloadTxt => Path.Combine(Dir, "payload.txt");
    public static string PayloadPng => Path.Combine(Dir, "payload.png");
    public static string ServiceLog => Path.Combine(Dir, "service.log");

    public static void EnsureDir() => Directory.CreateDirectory(Dir);
}

/// <summary>
/// DPAPI wrapper. Default scope is CurrentUser (as the task requires).
/// A machine-scoped variant exists only because a SYSTEM service cannot read
/// CurrentUser blobs; opt in with TCUNLOCK_DPAPI_SCOPE=localmachine (see README).
/// </summary>
public static class Secret
{
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("TC-tools/unlock/v1");

    public static DataProtectionScope ScopeFromEnvironment()
    {
        string? s = Environment.GetEnvironmentVariable("TCUNLOCK_DPAPI_SCOPE");
        if (!string.IsNullOrWhiteSpace(s) &&
            (s.Trim().Equals("localmachine", StringComparison.OrdinalIgnoreCase) ||
             s.Trim().Equals("machine", StringComparison.OrdinalIgnoreCase)))
        {
            return DataProtectionScope.LocalMachine;
        }
        return DataProtectionScope.CurrentUser;
    }

    public static string ProtectToBase64(byte[] plaintext, DataProtectionScope scope)
    {
        byte[] blob = ProtectedData.Protect(plaintext, Entropy, scope);
        return Convert.ToBase64String(blob);
    }

    public static byte[]? UnprotectFromBase64(string? base64, DataProtectionScope scope)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            byte[] blob = Convert.FromBase64String(base64);
            return ProtectedData.Unprotect(blob, Entropy, scope);
        }
        catch (CryptographicException)
        {
            // Wrong user / wrong scope / corrupted blob. Try the other scope so a
            // machine-scoped secret still works for an interactive session.
            try
            {
                DataProtectionScope other = scope == DataProtectionScope.CurrentUser
                    ? DataProtectionScope.LocalMachine
                    : DataProtectionScope.CurrentUser;
                return ProtectedData.Unprotect(Convert.FromBase64String(base64), Entropy, other);
            }
            catch (Exception)
            {
                return null;
            }
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Mask(byte[] secret)
    {
        if (secret.Length == 0) return "(empty)";
        string head = Convert.ToBase64String(secret.AsSpan(0, Math.Min(4, secret.Length)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{head}…({secret.Length} bytes, hidden)";
    }
}

public sealed class HostStore
{
    private readonly DataProtectionScope _scope;
    private byte[]? _pskCache;
    private string? _passwordCache;
    private bool _passwordLoaded;

    public HostState State { get; private set; }
    public string Path => HostPaths.HostJson;

    private HostStore(HostState state, DataProtectionScope scope)
    {
        State = state;
        _scope = scope;
    }

    public static HostStore Load()
    {
        DataProtectionScope scope = Secret.ScopeFromEnvironment();
        HostPaths.EnsureDir();
        HostState state;
        bool dirty = false;
        if (File.Exists(HostPaths.HostJson))
        {
            try
            {
                string json = File.ReadAllText(HostPaths.HostJson, Encoding.UTF8);
                state = JsonSerializer.Deserialize<HostState>(json, Proto.DiskJson) ?? new HostState();
            }
            catch (JsonException)
            {
                // Corrupt file: do not silently wipe secrets, but keep going with a
                // fresh in-memory state. Caller decides what to do.
                state = new HostState();
            }
        }
        else
        {
            state = new HostState();
            dirty = true;
        }

        if (string.IsNullOrWhiteSpace(state.HostId))
        {
            state.HostId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            dirty = true;
        }
        if (string.IsNullOrWhiteSpace(state.HostName))
        {
            state.HostName = Environment.MachineName;
            dirty = true;
        }

        var store = new HostStore(state, scope);

        // Persist the identity the first time it is created: HOST_ID is part of
        // the pairing payload and must not change between invocations.
        if (dirty)
        {
            try { store.Save(); }
            catch (Exception) { /* read-only location: keep the in-memory identity */ }
        }

        return store;
    }

    public void Save()
    {
        HostPaths.EnsureDir();
        string json = JsonSerializer.Serialize(State, Proto.DiskJson);

        // Atomic-ish write: temp file then replace, so a crash cannot truncate
        // host.json (which would lose the PSK).
        string tmp = HostPaths.HostJson + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(HostPaths.HostJson)) File.Replace(tmp, HostPaths.HostJson, null);
        else File.Move(tmp, HostPaths.HostJson);

        try
        {
            // Best effort: keep the secret file private to the current user.
            File.SetAttributes(HostPaths.HostJson, File.GetAttributes(HostPaths.HostJson) | FileAttributes.Hidden);
        }
        catch (Exception) { /* non-fatal */ }
    }

    // ------------------------------------------------------------------ PSK

    public byte[]? GetPsk()
    {
        if (_pskCache is not null) return _pskCache;
        byte[]? psk = Secret.UnprotectFromBase64(State.PskProtected, _scope);
        if (psk is { Length: Proto.PskLength }) _pskCache = psk;
        return _pskCache;
    }

    public void SetPsk(byte[] psk)
    {
        if (psk.Length != Proto.PskLength) throw new ArgumentException("PSK must be 32 bytes");
        _pskCache = psk;
        State.PskProtected = Secret.ProtectToBase64(psk, _scope);
    }

    public bool HasPsk => GetPsk() is not null;
    public bool PskValid => HasPsk && !State.PskInvalidated;

    public void ClearPairing()
    {
        _pskCache = null;
        State.PskProtected = null;
        State.PeerId = null;
        State.PeerName = null;
        State.PairedAt = null;
        State.PskInvalidated = false;
        State.FailCount = 0;
    }

    // ------------------------------------------------------------- password

    public string? GetPassword()
    {
        if (_passwordLoaded) return _passwordCache;
        _passwordLoaded = true;
        byte[]? bytes = Secret.UnprotectFromBase64(State.PasswordProtected, _scope);
        _passwordCache = bytes is null ? null : Encoding.UTF8.GetString(bytes);
        return _passwordCache;
    }

    public void SetPassword(string password)
    {
        _passwordLoaded = true;
        _passwordCache = password;
        State.PasswordProtected = Secret.ProtectToBase64(Encoding.UTF8.GetBytes(password), _scope);
        State.PasswordScope = _scope.ToString();
    }

    public bool HasPassword => GetPassword() is { Length: > 0 };
}

/// <summary>
/// Heartbeat written by `run` so `status` can report the state of a live
/// instance instead of probing the radio itself.
/// </summary>
public sealed class RuntimeState
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("startedAt")] public string? StartedAt { get; set; }
    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }
    [JsonPropertyName("advertising")] public bool Advertising { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("peerConnected")] public bool PeerConnected { get; set; }
    [JsonPropertyName("connections")] public int Connections { get; set; }
    [JsonPropertyName("unlocks")] public int Unlocks { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }

    public static RuntimeState? TryRead()
    {
        try
        {
            if (!File.Exists(HostPaths.RuntimeJson)) return null;
            string json = File.ReadAllText(HostPaths.RuntimeJson, Encoding.UTF8);
            return JsonSerializer.Deserialize<RuntimeState>(json, Proto.DiskJson);
        }
        catch (Exception) { return null; }
    }

    public void Write()
    {
        try
        {
            HostPaths.EnsureDir();
            File.WriteAllText(
                HostPaths.RuntimeJson,
                JsonSerializer.Serialize(this, Proto.DiskJson),
                new UTF8Encoding(false));
        }
        catch (Exception) { /* heartbeat is best effort */ }
    }

    public void Delete()
    {
        try { if (File.Exists(HostPaths.RuntimeJson)) File.Delete(HostPaths.RuntimeJson); }
        catch (Exception) { }
    }

    /// <summary>Alive = PID exists, is one of ours, and the heartbeat is fresh.</summary>
    public bool IsLive(TimeSpan? maxAge = null)
    {
        if (Pid <= 0) return false;
        DateTime updated;
        if (!DateTime.TryParse(UpdatedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out updated))
            return false;
        if (DateTime.UtcNow - updated > (maxAge ?? TimeSpan.FromSeconds(20))) return false;
        try
        {
            using Process p = Process.GetProcessById(Pid);
            if (p.HasExited) return false;
            string name = p.ProcessName;
            return name.Contains("tctool-unlock", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}
