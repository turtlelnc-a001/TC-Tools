using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TC.Tools.Unlock;

/// <summary>
/// Byte-level implementation of docs/UNLOCK-PROTOCOL.md v1.0 §2 / §2.1.
///
/// Everything in here is deliberately literal: no streaming, no culture, no
/// BOM, ASCII labels without a trailing NUL. If this file and the protocol doc
/// ever disagree, the doc wins.
/// </summary>
public static class Proto
{
    public const int PskLength = 32;
    public const int NonceLength = 32;
    public const int ProofLength = 32;
    public const int IvLength = 12;
    public const int TagLength = 16;
    /// <summary>12 (IV) + 1 (shortest plaintext) + 16 (GCM tag) = 29.</summary>
    public const int MinSealedLength = IvLength + 1 + TagLength;
    /// <summary>§3.2 pragmatic cap for a single notification.</summary>
    public const int MaxNotifyPayload = 180;

    public const string LabelSession = "TCUNLOCK-SESSION-V1";
    public const string LabelProof = "TCUNLOCK-PROOF-V1";

    private static readonly byte[] SessionLabelBytes = Encoding.ASCII.GetBytes(LabelSession);
    private static readonly byte[] ProofLabelBytes = Encoding.ASCII.GetBytes(LabelProof);

    // ---------------------------------------------------------------- labels

    /// <summary>K_session = HMAC-SHA256(PSK, NONCE || "TCUNLOCK-SESSION-V1")</summary>
    public static byte[] DeriveKSession(byte[] psk, ReadOnlySpan<byte> nonce)
    {
        RequirePsk(psk);
        if (nonce.Length != NonceLength)
            throw new ArgumentException($"NONCE must be {NonceLength} bytes, got {nonce.Length}", nameof(nonce));

        byte[] message = new byte[nonce.Length + SessionLabelBytes.Length];
        nonce.CopyTo(message);
        SessionLabelBytes.CopyTo(message.AsSpan(nonce.Length));
        return Hmac(psk, message);
    }

    /// <summary>
    /// PROOF = HMAC-SHA256(PSK, NONCE || "TCUNLOCK-PROOF-V1" || HOST_ID || PEER_ID)
    /// HOST_ID / PEER_ID are the ASCII bytes of the lowercase hyphenated UUID
    /// strings (36 bytes each) with no trailing NUL.
    /// </summary>
    public static byte[] DeriveProof(byte[] psk, ReadOnlySpan<byte> nonce, string hostId, string peerId)
    {
        RequirePsk(psk);
        if (nonce.Length != NonceLength)
            throw new ArgumentException($"NONCE must be {NonceLength} bytes, got {nonce.Length}", nameof(nonce));

        byte[] host = Encoding.ASCII.GetBytes(hostId ?? string.Empty);
        byte[] peer = Encoding.ASCII.GetBytes(peerId ?? string.Empty);

        byte[] message = new byte[nonce.Length + ProofLabelBytes.Length + host.Length + peer.Length];
        int o = 0;
        nonce.CopyTo(message);
        o += nonce.Length;
        ProofLabelBytes.CopyTo(message.AsSpan(o));
        o += ProofLabelBytes.Length;
        host.CopyTo(message.AsSpan(o));
        o += host.Length;
        peer.CopyTo(message.AsSpan(o));
        return Hmac(psk, message);
    }

    private static byte[] Hmac(byte[] key, byte[] message)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(message);
    }

    private static void RequirePsk(byte[] psk)
    {
        if (psk is null) throw new ArgumentNullException(nameof(psk));
        if (psk.Length != PskLength)
            throw new ArgumentException($"PSK must be {PskLength} bytes, got {psk.Length}", nameof(psk));
    }

    // ------------------------------------------------------------------ AEAD

    /// <summary>
    /// SEAL(key, counter, plaintext) -> IV || ciphertext || tag.
    /// IV = counter (8 bytes big-endian) || 4 bytes 0x00.
    /// </summary>
    public static byte[] Seal(byte[] key, ulong counter, ReadOnlySpan<byte> plaintext)
    {
        if (key is null || key.Length != 32)
            throw new ArgumentException("SEAL key must be 32 bytes", nameof(key));

        byte[] frame = new byte[IvLength + plaintext.Length + TagLength];
        Span<byte> iv = frame.AsSpan(0, IvLength);
        iv.Clear();                                   // bytes 8..11 stay 0x00
        BinaryPrimitives.WriteUInt64BigEndian(iv, counter);

        using var gcm = new AesGcm(key, TagLength);
        gcm.Encrypt(
            iv,
            plaintext,
            frame.AsSpan(IvLength, plaintext.Length),
            frame.AsSpan(IvLength + plaintext.Length, TagLength));
        return frame;
    }

    public static byte[] Seal(byte[] key, ulong counter, string plaintextUtf8)
        => Seal(key, counter, Encoding.UTF8.GetBytes(plaintextUtf8));

    /// <summary>Counter is carried in the clear in the first 8 bytes of the frame.</summary>
    public static ulong ReadCounter(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < IvLength) throw new ArgumentException("frame too short", nameof(frame));
        return BinaryPrimitives.ReadUInt64BigEndian(frame[..8]);
    }

    /// <summary>True when the 4 padding bytes of the IV are zero, as §2.1 requires.</summary>
    public static bool HasValidIvPadding(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < IvLength) return false;
        return frame[8] == 0 && frame[9] == 0 && frame[10] == 0 && frame[11] == 0;
    }

    /// <summary>OPEN(key, counter, frame) -> plaintext, or a §6 error code.</summary>
    public static bool TryOpen(byte[] key, ReadOnlySpan<byte> frame, out byte[] plaintext, out ulong counter, out string error)
    {
        plaintext = Array.Empty<byte>();
        counter = 0;
        error = string.Empty;

        if (key is null || key.Length != 32) { error = ErrorCodes.DecryptFailed; return false; }
        if (frame.Length < MinSealedLength) { error = ErrorCodes.BadFrame; return false; }
        if (!HasValidIvPadding(frame)) { error = ErrorCodes.BadFrame; return false; }

        counter = ReadCounter(frame);
        ReadOnlySpan<byte> iv = frame[..IvLength];
        ReadOnlySpan<byte> ciphertext = frame.Slice(IvLength, frame.Length - IvLength - TagLength);
        ReadOnlySpan<byte> tag = frame[^TagLength..];

        byte[] buffer = new byte[ciphertext.Length];
        try
        {
            using var gcm = new AesGcm(key, TagLength);
            gcm.Decrypt(iv, ciphertext, tag, buffer);
        }
        catch (CryptographicException)
        {
            counter = 0;
            error = ErrorCodes.DecryptFailed;
            return false;
        }

        plaintext = buffer;
        return true;
    }

    /// <summary>Constant-time 32/36-byte comparison (§3.3.1).</summary>
    public static bool FixedTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>
    /// §2.1 replay rule, shared by the GATT host and the self-test so the test
    /// exercises the same predicate the server uses: a counter is only accepted
    /// when it is STRICTLY greater than the last accepted one.
    /// </summary>
    public static bool ShouldAcceptCounter(ulong incoming, ulong lastAccepted) => incoming > lastAccepted;

    // ------------------------------------------------------------- encoding

    /// <summary>base64url without padding (§4.1).</summary>
    public static string Base64UrlEncode(ReadOnlySpan<byte> data)
    {
        string s = Convert.ToBase64String(data);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Base64UrlDecode(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        string s = text.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("invalid base64url length");
        }
        return Convert.FromBase64String(s);
    }

    public static string ToHex(ReadOnlySpan<byte> data) => Convert.ToHexString(data).ToLowerInvariant();

    public static bool TryFromHex(string? hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { bytes = Convert.FromHexString(hex.Trim()); return true; }
        catch (FormatException) { return false; }
    }

    // ----------------------------------------------------------------- JSON

    /// <summary>
    /// Compact UTF-8 JSON, single line, non-ASCII kept as raw UTF-8 (the
    /// pairing payload carries a possibly-Chinese computer name).
    /// </summary>
    public static readonly JsonSerializerOptions WireJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Same as <see cref="WireJson"/> but pretty, for host.json on disk.</summary>
    public static readonly JsonSerializerOptions DiskJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// CLI --json output. STRICTLY ASCII: the C++ integrator captures our
    /// stdout through a cmd pipe, which delivers console-codepage (GBK) bytes,
    /// so any raw non-ASCII (Chinese computer/phone name) would be mojibake.
    /// The default encoder escapes everything non-ASCII as \uXXXX, which is
    /// pure ASCII on the wire and decodes to the same string.
    /// Always single-line (WriteIndented = false).
    /// </summary>
    public static readonly JsonSerializerOptions CliJson = new()
    {
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ToWireJson<T>(T value) => JsonSerializer.Serialize(value, WireJson);

    /// <summary>Single-line, ASCII-only JSON for the CLI contract.</summary>
    public static string ToCliJson<T>(T value) => JsonSerializer.Serialize(value, CliJson);
}

/// <summary>§6 error codes.</summary>
public static class ErrorCodes
{
    public const string BadFrame = "bad-frame";
    public const string NotAuthenticated = "not-authenticated";
    public const string AuthFailed = "auth-failed";
    public const string Replay = "replay";
    public const string DecryptFailed = "decrypt-failed";
    public const string NoPasswordSet = "nopsz";
    public const string Busy = "busy";
}

/// <summary>§3.3.2 unlock reasons.</summary>
public static class Reasons
{
    public const string Ok = "ok";
    public const string NoPassword = "no-password";
    public const string NotLocked = "not-locked";
    public const string InjectFailed = "inject-failed";
    public const string Unsupported = "unsupported";
    public const string Throttled = "throttled";
}

/// <summary>§3.1 fixed UUIDs. Not configurable by design.</summary>
public static class ProtocolUuids
{
    public static readonly Guid Service = new("7a1c9e40-2f3d-4b6c-9a11-6c5d3e8f2b01");
    public static readonly Guid Challenge = new("7a1c9e41-2f3d-4b6c-9a11-6c5d3e8f2b01");
    public static readonly Guid Command = new("7a1c9e42-2f3d-4b6c-9a11-6c5d3e8f2b01");
}
