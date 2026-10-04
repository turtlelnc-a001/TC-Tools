using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TC.Tools.Unlock.Win;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace TC.Tools.Unlock.Ble;

public sealed record GattStartResult(bool Ok, string Detail, bool Advertising);

/// <summary>
/// GATT server side of docs/UNLOCK-PROTOCOL.md §3.
///
/// Deliberately event-driven only (ReadRequested / WriteRequested /
/// SubscribedClientsChanged / NotifyValueAsync(IBuffer) /
/// GattServiceProvider.StartAdvertising) so that every API used here carries a
/// WinRT minimum-OS of 10.0.15063 or lower, i.e. below the Windows 10 1709
/// (16299) floor. No synchronous 19041+ characteristic APIs are used.
/// </summary>
public sealed class GattUnlockServer : IDisposable
{
    /// <summary>§3.3.1: five consecutive PROOF failures invalidate the PSK.</summary>
    public const int MaxProofFailures = UnlockPolicy.MaxProofFailures;

    /// <summary>§5.5: two unlocks inside this window are rejected as "throttled".</summary>
    public const int UnlockThrottleMs = UnlockPolicy.UnlockThrottleMs;

    /// <summary>§3.3.1 PROOF frame length.</summary>
    private const int IdentLength = 36;

    /// <summary>
    /// How long to wait for a follow-up long-write fragment before concluding
    /// that an ambiguous buffer really is a corrupt frame (see Reassemble).
    /// </summary>
    private const int WriteSettleMs = 120;

    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(5);

    private readonly HostStore _store;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _frameGate = new(1, 1);

    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _challengeChar;
    private GattLocalCharacteristic? _commandChar;

    private Session? _session;
    private byte[] _readValue = new byte[Proto.NonceLength];
    private int _writeSeq;
    private readonly object _writeLock = new();

    public bool Advertising { get; private set; }
    public int Connections { get; private set; }
    public int Unlocks { get; private set; }
    public bool PeerConnected { get; private set; }
    public string LastDetail { get; private set; } = "not started";

    public GattUnlockServer(HostStore store, Action<string>? log = null)
    {
        _store = store;
        _log = log ?? (_ => { });
    }

    // ------------------------------------------------------------- lifecycle

    public async Task<GattStartResult> StartAsync()
    {
        RandomNumberGenerator.Fill(_readValue);

        GattServiceProviderResult created = await GattServiceProvider.CreateAsync(ProtocolUuids.Service);
        if (created.ServiceProvider is null)
        {
            string hint = created.Error == BluetoothError.RadioNotAvailable
                ? "Bluetooth radio is off or unavailable (BluetoothError.RadioNotAvailable). " +
                  "Turn Bluetooth on in Windows Settings, or pass --enable-radio."
                : $"GattServiceProvider.CreateAsync failed: {created.Error}";
            LastDetail = hint;
            _log($"[gatt] {hint}");
            return new GattStartResult(false, hint, false);
        }

        _provider = created.ServiceProvider;

        var challengeParams = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = GattProtectionLevel.Plain,
            WriteProtectionLevel = GattProtectionLevel.Plain,
            UserDescription = "TC-tools unlock challenge",
        };
        GattLocalCharacteristicResult challengeResult =
            await _provider.Service.CreateCharacteristicAsync(ProtocolUuids.Challenge, challengeParams);
        if (challengeResult.Characteristic is null)
        {
            LastDetail = $"challenge characteristic failed: {challengeResult.Error}";
            _log($"[gatt] {LastDetail}");
            return new GattStartResult(false, LastDetail, false);
        }
        _challengeChar = challengeResult.Characteristic;

        var commandParams = new GattLocalCharacteristicParameters
        {
            // §3.3 says exactly "Write". WriteWithoutResponse is deliberately NOT
            // declared: an ATT Write Command cannot carry a long write and gives
            // no confirmation, so a >MTU SEALED frame from Android would be
            // silently dropped. Write (with response) supports ATT
            // Prepare/Execute long writes, which is what a 32-byte PROOF and a
            // >20-byte SEALED frame need on a default 23-byte MTU.
            CharacteristicProperties = GattCharacteristicProperties.Write,
            ReadProtectionLevel = GattProtectionLevel.Plain,
            WriteProtectionLevel = GattProtectionLevel.Plain,
            UserDescription = "TC-tools unlock command",
        };
        GattLocalCharacteristicResult commandResult =
            await _provider.Service.CreateCharacteristicAsync(ProtocolUuids.Command, commandParams);
        if (commandResult.Characteristic is null)
        {
            LastDetail = $"command characteristic failed: {commandResult.Error}";
            _log($"[gatt] {LastDetail}");
            return new GattStartResult(false, LastDetail, false);
        }
        _commandChar = commandResult.Characteristic;

        _challengeChar.ReadRequested += OnReadRequested;
        _challengeChar.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _commandChar.WriteRequested += OnWriteRequested;
        _provider.AdvertisementStatusChanged += OnAdvertisementStatusChanged;

        _provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = true,
            IsConnectable = true,
        });

        // StartedWithoutAllAdvertisementData still means the radio accepted the
        // advertisement (some of the optional payload was dropped).
        for (int i = 0; i < 30; i++)
        {
            GattServiceProviderAdvertisementStatus status = _provider.AdvertisementStatus;
            if (status is GattServiceProviderAdvertisementStatus.Started
                or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
            {
                Advertising = true;
                LastDetail = $"advertising ({status})";
                _log($"[gatt] advertising: {status}, service {ProtocolUuids.Service:D}");
                return new GattStartResult(true, LastDetail, true);
            }
            await Task.Delay(100);
        }

        GattServiceProviderAdvertisementStatus final = _provider.AdvertisementStatus;
        Advertising = false;
        LastDetail = $"advertisement status is {final} (expected Started)";
        _log($"[gatt] {LastDetail}");
        return new GattStartResult(false, LastDetail, false);
    }

    public void Stop()
    {
        try { _provider?.StopAdvertising(); } catch (Exception) { }
        Advertising = false;
        LastDetail = "stopped";
    }

    public void Dispose()
    {
        try
        {
            if (_challengeChar is not null)
            {
                _challengeChar.ReadRequested -= OnReadRequested;
                _challengeChar.SubscribedClientsChanged -= OnSubscribedClientsChanged;
            }
            if (_commandChar is not null) _commandChar.WriteRequested -= OnWriteRequested;
            if (_provider is not null) _provider.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
        }
        catch (Exception) { }

        try { _provider?.StopAdvertising(); } catch (Exception) { }
        _frameGate.Dispose();
    }

    // ------------------------------------------------------------ GATT events

    private void OnAdvertisementStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        Advertising = sender.AdvertisementStatus is GattServiceProviderAdvertisementStatus.Started
            or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData;
        _log($"[gatt] advertisement status -> {sender.AdvertisementStatus} (error={args.Error})");
    }

    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        int count = sender.SubscribedClients.Count;
        PeerConnected = count > 0;
        if (count > 0)
        {
            Connections++;
            Session session = EnsureSession();
            int maxNotify = sender.SubscribedClients.Count > 0
                ? sender.SubscribedClients[0].MaxNotificationSize
                : 0;
            _log($"[gatt] peer subscribed ({count}); connections={Connections}; peer MTU notify max={maxNotify}");
            _ = session;
        }
        else
        {
            _log("[gatt] peer unsubscribed");
        }
    }

    private async void OnReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        GattReadRequest? request = null;
        try { request = await args.GetRequestAsync(); }
        catch (Exception ex) { _log($"[gatt] read request failed: {ex.Message}"); return; }
        if (request is null) return;

        try
        {
            byte[] value = _readValue;
            uint offset = request.Offset;
            if (offset > value.Length)
            {
                // ATT 0x07 = Invalid offset.
                request.RespondWithProtocolError(0x07);
                _log($"[gatt] read offset {offset} out of range");
                return;
            }

            int remaining = value.Length - (int)offset;
            int count = request.Length > 0 ? Math.Min(remaining, (int)request.Length) : remaining;
            request.RespondWithValue(CryptographicBuffer.CreateFromByteArray(value.AsSpan((int)offset, count).ToArray()));
            EnsureSession().LastActivityUtc = DateTime.UtcNow;
            _log($"[gatt] challenge read: offset={offset} requested={request.Length} served={count}");
        }
        catch (Exception ex)
        {
            _log($"[gatt] read respond failed: {ex.Message}");
        }
    }

    private async void OnWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        GattWriteRequest? request = null;
        try { request = await args.GetRequestAsync(); }
        catch (Exception ex) { _log($"[gatt] write request failed: {ex.Message}"); return; }
        if (request is null) return;

        byte[] data;
        try { data = ToByteArray(request.Value); }
        catch (Exception ex) { _log($"[gatt] write payload unreadable: {ex.Message}"); return; }

        // ACK the ATT write before doing any work, so a slow unlock can never
        // stall the ATT layer (the phone learns the real outcome by notify).
        if (request.Option == GattWriteOption.WriteWithResponse)
        {
            try { request.Respond(); } catch (Exception) { }
        }

        Session session = EnsureSession();
        uint offset = request.Offset;
        byte[] assembled;
        lock (_writeLock)
        {
            if (offset == 0) session.WriteBuffer = data;
            else if (offset == session.WriteBuffer.Length)
            {
                byte[] merged = new byte[session.WriteBuffer.Length + data.Length];
                session.WriteBuffer.CopyTo(merged, 0);
                data.CopyTo(merged, session.WriteBuffer.Length);
                session.WriteBuffer = merged;
            }
            else
            {
                // Gap or overlap: start over from this fragment rather than
                // splicing garbage together.
                _log($"[gatt] write offset discontinuity ({offset} vs {session.WriteBuffer.Length}), restarting frame");
                session.WriteBuffer = data;
            }

            assembled = session.WriteBuffer;
            _writeSeq++;
        }

        int seq = _writeSeq;
        _log($"[gatt] write {data.Length}B at offset {offset} ({request.Option}); buffer={assembled.Length}B");

        try
        {
            await HandleFrameAsync(session, assembled, seq);
        }
        catch (Exception ex)
        {
            _log($"[gatt] frame handling error: {ex}");
        }
    }

    // ---------------------------------------------------------- frame handling

    private async Task HandleFrameAsync(Session session, byte[] frame, int seq)
    {
        session.LastActivityUtc = DateTime.UtcNow;

        // 1) PROOF: exactly 32 bytes, only valid before authentication. (§3.3.1)
        if (!session.Authenticated && frame.Length == Proto.ProofLength)
        {
            ClearWriteBuffer(session);
            await HandleProofAsync(session, frame);
            return;
        }

        // 2) IDENT: 36 bytes, ASCII lowercase UUID, only before authentication.
        //    Additive extension; see README "PEER_ID".
        if (!session.Authenticated && frame.Length == IdentLength && LooksLikeUuid(frame))
        {
            ClearWriteBuffer(session);
            session.IdentPeerId = Encoding.ASCII.GetString(frame);
            _log($"[proto] peer identified as {session.IdentPeerId}");
            return;
        }

        // 3) SEALED command frame (>= 29 bytes), only after authentication.
        if (frame.Length >= Proto.MinSealedLength)
        {
            if (!session.Authenticated)
            {
                if (await SettledAsync(seq)) return;
                ClearWriteBuffer(session);
                _log("[proto] sealed frame before authentication -> dropped (no key to answer with)");
                return;
            }

            ulong counter = Proto.ReadCounter(frame);
            if (!Proto.ShouldAcceptCounter(counter, session.LastIncomingCounter))
            {
                ClearWriteBuffer(session);
                _log($"[proto] replay: counter {counter} <= {session.LastIncomingCounter}");
                await SendErrorAsync(session, ErrorCodes.Replay, $"counter {counter} not greater than {session.LastIncomingCounter}");
                return;
            }

            if (Proto.TryOpen(session.KSession!, frame, out byte[] plaintext, out counter, out string error))
            {
                ClearWriteBuffer(session);
                session.LastIncomingCounter = counter;
                await DispatchAsync(session, plaintext, counter);
                return;
            }

            // Could be a long write still in flight. Give the peer a moment to
            // send the rest before declaring the frame corrupt.
            if (await SettledAsync(seq)) return;
            _log($"[proto] open failed: {error}");
            await SendErrorAsync(session, error, "AEAD verification failed");
            return;
        }

        // 4) Ambiguous / incomplete.
        if (await SettledAsync(seq)) return;

        ClearWriteBuffer(session);
        if (!session.Authenticated && frame.Length < Proto.MinSealedLength && frame.Length != Proto.ProofLength)
        {
            _log($"[proto] bad frame pre-auth: {frame.Length} bytes");
            return;
        }

        _log($"[proto] bad frame: {frame.Length} bytes");
        await SendErrorAsync(session, ErrorCodes.BadFrame, $"unexpected frame length {frame.Length}");
    }

    /// <summary>
    /// True when more write data arrived while we waited, meaning this buffer is
    /// still being assembled and the newer event owns the decision.
    /// </summary>
    private async Task<bool> SettledAsync(int seq)
    {
        await Task.Delay(WriteSettleMs);
        lock (_writeLock)
        {
            return _writeSeq != seq;
        }
    }

    private void ClearWriteBuffer(Session session)
    {
        lock (_writeLock) { session.WriteBuffer = Array.Empty<byte>(); }
    }

    private static bool LooksLikeUuid(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != IdentLength) return false;
        for (int i = 0; i < frame.Length; i++)
        {
            byte b = frame[i];
            bool isHyphen = i is 8 or 13 or 18 or 23;
            if (isHyphen)
            {
                if (b != (byte)'-') return false;
                continue;
            }
            bool hex = (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'f');
            if (!hex) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ PROOF

    private async Task HandleProofAsync(Session session, byte[] proof)
    {
        byte[]? psk = _store.GetPsk();
        if (psk is null)
        {
            _log("[proto] PROOF received but this host is not paired (no PSK)");
            return;
        }

        if (_store.State.PskInvalidated)
        {
            _log("[proto] PROOF received but the PSK is invalidated; re-pair required");
            return;
        }

        // Frozen protocol §3.3.1 allows exactly two candidates: the PEER_ID from
        // the IDENT frame received on THIS connection, or the empty string when
        // no IDENT arrived. The value stored at pairing time is deliberately NOT
        // used here: accepting it would mask a genuine PEER_ID mismatch (e.g.
        // after the phone re-paired with a new id).
        var candidates = new List<string>(2);
        if (!string.IsNullOrEmpty(session.IdentPeerId)) candidates.Add(session.IdentPeerId);
        else candidates.Add(string.Empty);

        bool matched = false;
        foreach (string peerId in candidates)
        {
            byte[] expected = Proto.DeriveProof(psk, session.Nonce, _store.State.HostId, peerId);
            if (Proto.FixedTimeEquals(expected, proof))
            {
                matched = true;
                if (peerId.Length > 0 && _store.State.PeerId != peerId)
                {
                    _store.State.PeerId = peerId;
                    _store.Save();
                }
                break;
            }
        }

        if (!matched)
        {
            UnlockPolicy.ProofFailureOutcome outcome = UnlockPolicy.RegisterProofFailure(_store.State.FailCount);
            _store.State.FailCount = outcome.FailCount;
            _log($"[proto] PROOF REJECTED (failCount={outcome.FailCount}/{MaxProofFailures})");

            if (outcome.InvalidatePsk)
            {
                _store.State.PskInvalidated = true;
                session.Dead = true;
                _log("[proto] too many PROOF failures -> PSK invalidated, re-pair required");
            }

            _store.Save();
            return;
        }

        _store.State.FailCount = 0;
        _store.Save();

        session.Authenticated = true;
        session.KSession = Proto.DeriveKSession(psk, session.Nonce);
        session.OutgoingCounter = 0;
        session.LastIncomingCounter = 0;
        session.LastUnlockTicks = 0;

        // §3.2: refresh the read value immediately so the old proof cannot be
        // replayed against this connection.
        RandomNumberGenerator.Fill(_readValue);

        string sid = Proto.ToHex(RandomNumberGenerator.GetBytes(4));
        session.Sid = sid;
        _log($"[proto] PROOF accepted; sid={sid}");

        await SendSealedAsync(session, new WireMessage { Type = "ready", Sid = sid });
    }

    // --------------------------------------------------------------- dispatch

    private async Task DispatchAsync(Session session, byte[] plaintext, ulong counter)
    {
        IncomingMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<IncomingMessage>(plaintext, Proto.WireJson);
        }
        catch (JsonException ex)
        {
            _log($"[proto] sealed payload is not valid JSON: {ex.Message}");
            await SendErrorAsync(session, ErrorCodes.BadFrame, "payload is not valid JSON");
            return;
        }

        if (message?.Type is null)
        {
            await SendErrorAsync(session, ErrorCodes.BadFrame, "missing type");
            return;
        }

        switch (message.Type)
        {
            case "unlock":
                if (!_frameGate.Wait(0))
                {
                    await SendErrorAsync(session, ErrorCodes.Busy, "still processing previous command");
                    return;
                }
                try { await HandleUnlockAsync(session); }
                finally { _frameGate.Release(); }
                break;

            case "ping":
                _log($"[proto] ping (counter={counter})");
                await SendSealedAsync(session, new WireMessage { Type = "pong", Sid = session.Sid });
                break;

            case "bye":
                _log("[proto] peer said bye; session closed");
                session.Dead = true;
                break;

            default:
                await SendErrorAsync(session, ErrorCodes.BadFrame, $"unknown type '{message.Type}'");
                break;
        }
    }

    private async Task HandleUnlockAsync(Session session)
    {
        DesktopLockInfo lockInfo = SessionDesktop.Query();

        long now = Environment.TickCount64;
        if (UnlockPolicy.ShouldThrottle(now, session.LastUnlockTicks))
        {
            _log($"[unlock] throttled ({now - session.LastUnlockTicks}ms since the previous unlock)");
            session.LastUnlockTicks = now;   // sliding window: sustained retries stay throttled
            await SendUnlockResultAsync(session, false, Reasons.Throttled, lockInfo.Locked, false);
            return;
        }
        session.LastUnlockTicks = now;

        string password = _store.GetPassword() ?? string.Empty;
        string? rejection = UnlockPolicy.RejectionReason(
            !string.IsNullOrEmpty(password), lockInfo.Locked, _store.State.InjectWhenUnlocked);

        if (rejection == Reasons.NoPassword)
        {
            _log("[unlock] no password configured");
            await SendUnlockResultAsync(session, false, rejection, lockInfo.Locked, false);
            return;
        }

        if (rejection == Reasons.NotLocked)
        {
            _log($"[unlock] refused: {lockInfo.Detail}, injectWhenUnlocked=false");
            await SendUnlockResultAsync(session, false, rejection, false, false);
            return;
        }

        // §5.3: validate EVERY character before sending ANY keystroke.
        InjectPlan plan = InputInjector.Plan(password);
        if (!plan.Ok)
        {
            _log($"[unlock] unsupported password character: {plan.Unsupported} (nothing was typed)");
            await SendUnlockResultAsync(session, false, Reasons.Unsupported, lockInfo.Locked, false);
            return;
        }

        bool sent = InputInjector.Send(plan, _store.State.KeyDelayMs, out string detail);
        if (!sent)
        {
            _log($"[unlock] injection failed: {detail}");
            await SendUnlockResultAsync(session, false, Reasons.InjectFailed, lockInfo.Locked, false);
            return;
        }

        Unlocks++;
        _log($"[unlock] password injected ({detail}); desktop was '{lockInfo.DesktopName}'");
        await SendUnlockResultAsync(session, true, Reasons.Ok, lockInfo.Locked, true);
    }

    private Task SendUnlockResultAsync(Session session, bool ok, string reason, bool locked, bool sent)
        => SendSealedAsync(session, new WireMessage
        {
            Type = "unlock_result",
            Sid = session.Sid,
            Ok = ok,
            Reason = reason,
            Locked = locked,
            Sent = sent,
        });

    private Task SendErrorAsync(Session session, string code, string message)
    {
        if (!session.Authenticated || session.KSession is null)
        {
            // No session key yet: answering would turn the host into an offline
            // PSK-verification oracle (known plaintext under a PSK-derived key).
            _log($"[proto] pre-auth error (not sent, no session key): {code}: {message}");
            return Task.CompletedTask;
        }

        _log($"[proto] -> error {code}: {message}");
        return SendSealedAsync(session, new WireMessage { Type = "error", Code = code, Msg = message });
    }

    private async Task SendSealedAsync(Session session, WireMessage message)
    {
        if (session.KSession is null)
        {
            _log("[proto] cannot notify: no session key");
            return;
        }

        byte[] json = NotifyBudget.Serialize(message, out bool trimmed);
        if (trimmed)
        {
            _log($"[proto] message was clamped to fit the {Proto.MaxNotifyPayload}-byte notify budget");
        }

        ulong counter = ++session.OutgoingCounter;
        byte[] frame = Proto.Seal(session.KSession, counter, json);

        if (frame.Length > Proto.MaxNotifyPayload)
        {
            // NotifyBudget.Serialize guarantees this; keep the guard so a future
            // message shape cannot silently break §3.2.
            _log($"[error] refusing to send a {frame.Length}-byte notification (> {Proto.MaxNotifyPayload})");
            return;
        }

        await NotifyAsync(frame);
    }

    private async Task NotifyAsync(byte[] payload)
    {
        GattLocalCharacteristic? characteristic = _challengeChar;
        if (characteristic is null) return;

        try
        {
            if (characteristic.SubscribedClients.Count == 0)
            {
                _log($"[gatt] no subscriber; dropping {payload.Length}-byte notification");
                return;
            }

            var results = await characteristic.NotifyValueAsync(CryptographicBuffer.CreateFromByteArray(payload));
            foreach (GattClientNotificationResult result in results)
            {
                if (result.Status != GattCommunicationStatus.Success)
                {
                    _log($"[gatt] notify status {result.Status} protocolError={result.ProtocolError}");
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[gatt] notify failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    private Session EnsureSession()
    {
        DateTime now = DateTime.UtcNow;
        Session? session = _session;
        if (session is null || session.Dead || now - session.LastActivityUtc > SessionIdleTimeout)
        {
            RandomNumberGenerator.Fill(_readValue);
            session = new Session
            {
                Nonce = (byte[])_readValue.Clone(),
                CreatedUtc = now,
                LastActivityUtc = now,
            };
            _session = session;
            _log($"[session] new session; challenge={Proto.ToHex(session.Nonce)}");
        }

        session.LastActivityUtc = now;
        return session;
    }

    private static byte[] ToByteArray(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private sealed class Session
    {
        public byte[] Nonce = new byte[Proto.NonceLength];
        public bool Authenticated;
        public byte[]? KSession;
        public ulong LastIncomingCounter;
        public ulong OutgoingCounter;
        public long LastUnlockTicks;
        public DateTime CreatedUtc;
        public DateTime LastActivityUtc;
        public bool Dead;
        public string Sid = string.Empty;
        public string? IdentPeerId;
        public byte[] WriteBuffer = Array.Empty<byte>();
    }

    /// <summary>§3.3.2 host -> peer messages. Null fields are omitted.</summary>
    internal sealed class WireMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("sid")] public string? Sid { get; set; }
        [JsonPropertyName("ok")] public bool? Ok { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("locked")] public bool? Locked { get; set; }
        [JsonPropertyName("sent")] public bool? Sent { get; set; }
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
    }

    /// <summary>§3.3.2 peer -> host sealed payloads.</summary>
    private sealed class IncomingMessage
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("counter")] public long? Counter { get; set; }
        [JsonPropertyName("sid")] public string? Sid { get; set; }
    }
}

/// <summary>
/// Enforces the §3.2 notification budget: a notification carries a bare
/// SEAL frame, so the plaintext must satisfy
/// 12 (IV) + len(plaintext) + 16 (tag) &lt;= 180, i.e. len(plaintext) &lt;= 152.
/// Longer messages have their free-text "msg" shortened rather than being sent
/// over budget (a truncated frame would be undecodable on the phone).
/// </summary>
internal static class NotifyBudget
{
    public const int MaxPlaintext = Proto.MaxNotifyPayload - Proto.IvLength - Proto.TagLength;

    public static byte[] Serialize(GattUnlockServer.WireMessage message, out bool trimmed)
    {
        trimmed = false;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, Proto.WireJson);
        if (json.Length <= MaxPlaintext) return json;

        if (message.Msg is not null)
        {
            trimmed = true;
            int overflow = json.Length - MaxPlaintext;
            string msg = message.Msg;
            int keep = msg.Length - overflow - 4;

            // JSON escaping can make the encoded form longer than the source, so
            // shrink until it actually fits instead of trusting the arithmetic.
            while (keep > 0)
            {
                message.Msg = msg[..keep] + "...";
                json = JsonSerializer.SerializeToUtf8Bytes(message, Proto.WireJson);
                if (json.Length <= MaxPlaintext) return json;
                keep -= Math.Max(1, (json.Length - MaxPlaintext) / 2 + 1);
            }

            message.Msg = "...";
            json = JsonSerializer.SerializeToUtf8Bytes(message, Proto.WireJson);
            if (json.Length <= MaxPlaintext) return json;
        }

        // Last resort: the smallest message with the same meaning.
        var minimal = new GattUnlockServer.WireMessage
        {
            Type = "error",
            Code = message.Code ?? ErrorCodes.BadFrame,
            Msg = "message too long",
        };
        return JsonSerializer.SerializeToUtf8Bytes(minimal, Proto.WireJson);
    }
}
