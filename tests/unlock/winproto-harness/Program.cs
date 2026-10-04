// verify/task-4 — Windows 端产品源码 Protocol.cs 的隔离运行宿主
// ---------------------------------------------------------------------------
// 目的：**运行真实产品代码**（<Compile Include> 直接引用 tcyunlock/src/Protocol.cs，
//       不做任何复制或改写），用 docs/UNLOCK-PROTOCOL.md §8 的权威向量核对它。
//
// 三种模式：
//   （默认）            权威向量模式：输出一行 JSON，交给 ref-vectors.mjs --compare 比对
//   --emit <c> <json> <out>   用产品代码生成 SEAL 帧，写 JSON（供对端 OPEN）
//   --open <in> <expectC>     读取对端产出的 JSON，用**自己派生**的 K_session 解密
//
// 运行：
//   toolchain\dotnet8\dotnet.exe run --project TC-tools\tests\unlock\winproto-harness
using System.Text;
using System.Text.Json;
using TC.Tools.Unlock;

// ---- §8 权威常量（两端一致，来自 docs/UNLOCK-PROTOCOL.md §8）---------------
byte[] psk = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();        // 0x00..0x1f
byte[] nonce = Enumerable.Range(0x20, 32).Select(i => (byte)i).ToArray();    // 0x20..0x3f
const string hostId = "3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01";
const string peerId = "8a7b6c5d-4e3f-4a2b-9c8d-7e6f5a4b3c2d";
const string unlockJson = "{\"type\":\"unlock\"}";

var opts = new JsonSerializerOptions { WriteIndented = true };

// 载荷用"种类名"而不是裸 JSON 传参：PowerShell 5.1 传参给原生 exe 时会吞掉内嵌双引号，
// 直接传 {"type":"ping"} 会变成 {type:ping}。用 kind 彻底规避该 shell 陷阱。
static string JsonFor(string kind) => kind switch
{
    "unlock" => "{\"type\":\"unlock\"}",
    "ping" => "{\"type\":\"ping\"}",
    "bye" => "{\"type\":\"bye\"}",
    _ => throw new ArgumentException($"未知载荷种类: {kind}（可用 unlock|ping|bye）"),
};

// ===========================================================================
// 模式 --emit：产品代码生成帧
// ===========================================================================
if (args.Length >= 4 && args[0] == "--emit")
{
    ulong emitCounter = ulong.Parse(args[1]);
    string json = JsonFor(args[2]);
    string outPath = args[3];

    byte[] emitKey = Proto.DeriveKSession(psk, nonce);
    byte[] emitFrame = Proto.Seal(emitKey, emitCounter, json);

    var emitPayload = new Dictionary<string, object?>
    {
        ["emitter"] = "windows",
        ["counter"] = emitCounter,
        ["plaintext"] = json,
        ["seal_hex"] = Proto.ToHex(emitFrame),
        ["iv_hex"] = Proto.ToHex(emitFrame.AsSpan(0, Proto.IvLength)),
        ["tag_hex"] = Proto.ToHex(emitFrame.AsSpan(emitFrame.Length - Proto.TagLength)),
    };
    File.WriteAllText(outPath, JsonSerializer.Serialize(emitPayload, opts), new UTF8Encoding(false));
    Console.WriteLine($"EMIT windows counter={emitCounter} kind={args[2]} len={emitFrame.Length} -> {outPath}");
    Console.WriteLine($"     seal_hex = {Proto.ToHex(emitFrame)}");
    return 0;
}

// ===========================================================================
// 模式 --open：产品代码解密**对端**产出的帧（key 由本端自行派生，不取自输入文件）
// ===========================================================================
if (args.Length >= 3 && args[0] == "--open")
{
    string inPath = args[1];
    ulong expected = args.Length >= 3 ? ulong.Parse(args[2]) : 0;
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(inPath));
    JsonElement root = doc.RootElement;
    string emitter = root.TryGetProperty("emitter", out JsonElement em) ? em.GetString() ?? "?" : "?";
    string sealHex = root.GetProperty("seal_hex").GetString()!;
    byte[] openFrame = Convert.FromHexString(sealHex);
    // 刻意忽略输入文件里的任何密钥字段：K_session 由本端从 §8 PSK/NONCE 自行派生
    byte[] openKey = Proto.DeriveKSession(psk, nonce);

    bool openedOk = Proto.TryOpen(openKey, openFrame, out byte[] openPlain, out ulong openCounter, out string openErr);
    string openText = openedOk ? Encoding.UTF8.GetString(openPlain) : "";
    bool counterOk = openedOk && openCounter == expected;

    var openPayload = new Dictionary<string, object?>
    {
        ["opener"] = "windows",
        ["input"] = Path.GetFileName(inPath),
        ["emitter"] = emitter,
        ["opened"] = openedOk,
        ["counter"] = openCounter,
        ["expected_counter"] = expected,
        ["counter_ok"] = counterOk,
        ["plaintext"] = openText,
        ["error"] = openErr,
        ["k_session_self_derived"] = Proto.ToHex(openKey),
    };
    Console.WriteLine(JsonSerializer.Serialize(openPayload, opts));
    return (openedOk && counterOk) ? 0 : 1;
}

// ===========================================================================
// 默认模式：§8 权威向量
// ===========================================================================
var stdout = new StringBuilder();
var diag = new List<string>();

byte[] kSession = Proto.DeriveKSession(psk, nonce);
byte[] proof = Proto.DeriveProof(psk, nonce, hostId, peerId);
byte[] frame = Proto.Seal(kSession, 1, unlockJson);
string b64url = Proto.Base64UrlEncode(psk);

// ---- 额外自洽性检查（全部走产品代码）---------------------------------------
diag.Add($"k_session_len={kSession.Length}");
diag.Add($"proof_len={proof.Length}");
diag.Add($"frame_len={frame.Length}");
diag.Add($"frame_len_matches_formula={(frame.Length == Proto.IvLength + Encoding.UTF8.GetByteCount(unlockJson) + Proto.TagLength)}");
diag.Add($"min_sealed_length={Proto.MinSealedLength}");
diag.Add($"iv_padding_valid={Proto.HasValidIvPadding(frame)}");
diag.Add($"read_counter={Proto.ReadCounter(frame)}");

bool opened = Proto.TryOpen(kSession, frame, out byte[] pt, out ulong openedCounter, out string err);
diag.Add($"tryopen_ok={opened}");
diag.Add($"tryopen_counter={openedCounter}");
diag.Add($"tryopen_error={(opened ? "<none>" : err)}");
diag.Add($"tryopen_plaintext={Encoding.UTF8.GetString(pt)}");
diag.Add($"tryopen_plaintext_matches={(Encoding.UTF8.GetString(pt) == unlockJson)}");

// 篡改 tag 必须解密失败（AEAD 完整性）
byte[] tampered = (byte[])frame.Clone();
tampered[^1] ^= 0x01;
bool tamperRejected = !Proto.TryOpen(kSession, tampered, out _, out _, out string tamperErr);
diag.Add($"tampered_tag_rejected={tamperRejected}");
diag.Add($"tampered_tag_error={tamperErr}");

// IV padding 非零必须拒绝（§2.1）
byte[] badPad = (byte[])frame.Clone();
badPad[11] = 0x01;
diag.Add($"nonzero_iv_padding_rejected={!Proto.TryOpen(kSession, badPad, out _, out _, out _)}");

// 过短帧必须拒绝（§3.3.2 ≥29B）
diag.Add($"short_frame_rejected={!Proto.TryOpen(kSession, frame.AsSpan(0, 28), out _, out _, out _)}");

// base64url 往返 + 无填充
byte[] decoded = Proto.Base64UrlDecode(b64url);
diag.Add($"base64url_roundtrip={decoded.SequenceEqual(psk)}");
diag.Add($"base64url_has_padding={b64url.Contains('=')}");
diag.Add($"base64url_len={b64url.Length}");

// UUID 常量（§3.1）
diag.Add($"uuid_service={ProtocolUuids.Service}");
diag.Add($"uuid_challenge={ProtocolUuids.Challenge}");
diag.Add($"uuid_command={ProtocolUuids.Command}");

// 常量时间比较（§3.3.1）
diag.Add($"fixedtime_equal={Proto.FixedTimeEquals(proof, (byte[])proof.Clone())}");
byte[] diff = (byte[])proof.Clone(); diff[0] ^= 0x01;
diag.Add($"fixedtime_unequal={!Proto.FixedTimeEquals(proof, diff)}");

// ---- 输出 ------------------------------------------------------------------
var payloadVec = new Dictionary<string, object?>
{
    ["k_session_hex"] = Proto.ToHex(kSession),
    ["proof_hex"] = Proto.ToHex(proof),
    ["seal_hex"] = Proto.ToHex(frame),
    ["tag_hex"] = Proto.ToHex(frame.AsSpan(frame.Length - Proto.TagLength)),
    ["ciphertext_hex"] = Proto.ToHex(frame.AsSpan(Proto.IvLength, frame.Length - Proto.IvLength - Proto.TagLength)),
    ["iv_hex"] = Proto.ToHex(frame.AsSpan(0, Proto.IvLength)),
    ["psk_base64url"] = b64url,
    ["_source"] = "tcyunlock/src/Protocol.cs (product code, compiled directly)",
    ["_diagnostics"] = diag,
};

Console.WriteLine(JsonSerializer.Serialize(payloadVec, opts));
return 0;
