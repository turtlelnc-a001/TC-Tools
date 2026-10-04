// TC-tools Unlock — 第三方实现交叉验证（.NET 8 / System.Security.Cryptography）
// -----------------------------------------------------------------------------
// 用途：用**第五个独立实现**复核 tests/unlock/vectors.json 的权威值。
//   - HMAC-SHA256 用 .NET 的 HMACSHA256（与 node:crypto / CPython / OpenSSL CLI 独立）
//   - AES-256-GCM 用 .NET 的 AesGcm（Windows CNG 后端），与 node:crypto(OpenSSL)
//     以及 ref-vectors.mjs 中手工 CTR+GHASH 实现相互独立
//   - base64url 用 Convert.ToBase64String 去填充 + 字符表替换
// 直接读取 vectors.json 并断言逐字节相等：一致则退出码 0，否则打印差异并返回 1。
//
// 运行：
//   toolchain\dotnet8\dotnet.exe run --project TC-tools\tests\unlock\dotnet-crosscheck
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TcUnlock.CrossCheck;

internal static class Program
{
    private const string HostId = "3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01";
    private const string PeerId = "8a7b6c5d-4e3f-4a2b-9c8d-7e6f5a4b3c2d";
    private const string LabelSession = "TCUNLOCK-SESSION-V1";
    private const string LabelProof = "TCUNLOCK-PROOF-V1";
    private const string UnlockJson = "{\"type\":\"unlock\"}";
    private const ulong Counter = 1;

    private static int _failures;

    private static int Main()
    {
        Console.WriteLine("=== .NET 8 交叉验证（独立于 node:crypto / CPython / OpenSSL CLI / 手工 GHASH）===");
        Console.WriteLine($"runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS         : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine();

        byte[] psk = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();          // 0x00..0x1f
        byte[] nonce = Enumerable.Range(0x20, 32).Select(i => (byte)i).ToArray();      // 0x20..0x3f

        // ---- K_session = HMAC-SHA256(PSK, NONCE || "TCUNLOCK-SESSION-V1") ----
        byte[] kSession = HMACSHA256.HashData(psk, Cat(nonce, Ascii(LabelSession)));

        // ---- PROOF = HMAC-SHA256(PSK, NONCE || "TCUNLOCK-PROOF-V1" || HOST_ID || PEER_ID) ----
        byte[] proof = HMACSHA256.HashData(
            psk,
            Cat(nonce, Ascii(LabelProof), Ascii(HostId), Ascii(PeerId)));

        // ---- SEAL(K_session, 1, '{"type":"unlock"}') ----
        byte[] iv = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(iv, Counter);   // 前 8 字节大端，后 4 字节保持 0x00
        byte[] plaintext = Encoding.UTF8.GetBytes(UnlockJson);
        byte[] ct = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using (var gcm = new AesGcm(kSession, 16))
        {
            gcm.Encrypt(iv, plaintext, ct, tag);
        }
        byte[] frame = [.. iv, .. ct, .. tag];

        string b64url = Convert.ToBase64String(psk)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Console.WriteLine($"K_session  = {Hex(kSession)}");
        Console.WriteLine($"PROOF      = {Hex(proof)}");
        Console.WriteLine($"psk_b64url = {b64url}");
        Console.WriteLine($"IV (12B)   = {Hex(iv)}");
        Console.WriteLine($"ciphertext = {Hex(ct)}   ({ct.Length}B)");
        Console.WriteLine($"tag        = {Hex(tag)}");
        Console.WriteLine($"frame      = {Hex(frame)}   ({frame.Length}B)");
        Console.WriteLine($"lengths    : K_session={kSession.Length}B PROOF={proof.Length}B frame={frame.Length}B");
        Console.WriteLine();

        // ---- 与 vectors.json 比对 -------------------------------------------------
        string vecPath = LocateVectorsJson();
        Console.WriteLine($"--- 与 {vecPath} 逐字段比对 ---");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(vecPath));
        JsonElement root = doc.RootElement;
        JsonElement seal = root.GetProperty("seal");

        Check("k_session", Hex(kSession), root.GetProperty("k_session_hex").GetString());
        Check("proof", Hex(proof), root.GetProperty("proof_hex").GetString());
        Check("psk_base64url", b64url, root.GetProperty("psk_base64url").GetString());
        Check("seal.iv", Hex(iv), seal.GetProperty("iv_hex").GetString());
        Check("seal.ciphertext", Hex(ct), seal.GetProperty("ciphertext_hex").GetString());
        Check("seal.tag", Hex(tag), seal.GetProperty("tag_hex").GetString());
        Check("seal.frame", Hex(frame), seal.GetProperty("frame_hex").GetString());
        Check("seal.frame_len", frame.Length.ToString(),
            seal.GetProperty("frame_len").GetInt32().ToString());

        // 密钥长度断言（协议 §2）
        Check("k_session_len", kSession.Length.ToString(), "32");
        Check("proof_len", proof.Length.ToString(), "32");

        Console.WriteLine();
        if (_failures > 0)
        {
            Console.WriteLine($"=== .NET CROSSCHECK: {_failures} MISMATCH(ES) ===");
            return 1;
        }
        Console.WriteLine("=== .NET CROSSCHECK: ALL MATCH ===");
        return 0;
    }

    private static void Check(string name, string got, string? expected)
    {
        bool ok = string.Equals(got, expected, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"[{(ok ? "ok " : "FAIL")}] {name}");
        Console.WriteLine($"        got      = {got}");
        if (!ok)
        {
            Console.WriteLine($"        expected = {expected}");
            _failures++;
        }
    }

    /// <summary>从当前目录逐级向上查找 tests/unlock/vectors.json</summary>
    private static string LocateVectorsJson()
    {
        DirectoryInfo? dir = new(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "vectors.json");
            if (File.Exists(candidate)) return candidate;
            string nested = Path.Combine(dir.FullName, "tests", "unlock", "vectors.json");
            if (File.Exists(nested)) return nested;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到 tests/unlock/vectors.json（请从 TC-tools 目录或任意子目录运行）");
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
