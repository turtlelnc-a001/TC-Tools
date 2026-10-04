using System.Text;

namespace TC.Tools.Unlock.Qr;

public enum QrEcc
{
    L = 0,
    M = 1,
    Q = 2,
    H = 3,
}

public sealed class QrMatrix
{
    public QrMatrix(int size, QrEcc ecc, int version, int mask, bool[,] modules)
    {
        Size = size;
        Ecc = ecc;
        Version = version;
        Mask = mask;
        Modules = modules;
    }

    public int Size { get; }
    public QrEcc Ecc { get; }
    public int Version { get; }
    /// <summary>Chosen mask pattern 0..7 (diagnostics only).</summary>
    public int Mask { get; }
    /// <summary>[row, col]; true = dark.</summary>
    public bool[,] Modules { get; }

    public bool this[int row, int col] => Modules[row, col];
}

/// <summary>
/// Minimal, dependency-free QR Code encoder (byte mode, versions 1..10, all
/// four ECC levels). Only what `pair` needs: a machine name plus a 43-character
/// PSK is ~160 bytes, which fits version 10-M.
///
/// Layout follows ISO/IEC 18004. Exposed for verification: the encoder can be
/// asked for a specific mask so its matrix can be compared module-for-module
/// against an independent reference implementation.
/// </summary>
public static class QrCode
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static QrCode()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    // version -> ECC level -> (ecCodewordsPerBlock, g1Blocks, g1Data, g2Blocks, g2Data)
    private static readonly int[][][] BlockTable =
    {
        // v1
        new[] { new[] { 7, 1, 19, 0, 0 }, new[] { 10, 1, 16, 0, 0 }, new[] { 13, 1, 13, 0, 0 }, new[] { 17, 1, 9, 0, 0 } },
        // v2
        new[] { new[] { 10, 1, 34, 0, 0 }, new[] { 16, 1, 28, 0, 0 }, new[] { 22, 1, 22, 0, 0 }, new[] { 28, 1, 16, 0, 0 } },
        // v3
        new[] { new[] { 15, 1, 55, 0, 0 }, new[] { 26, 1, 44, 0, 0 }, new[] { 18, 2, 17, 0, 0 }, new[] { 22, 2, 13, 0, 0 } },
        // v4
        new[] { new[] { 20, 1, 80, 0, 0 }, new[] { 18, 2, 32, 0, 0 }, new[] { 26, 2, 24, 0, 0 }, new[] { 16, 4, 9, 0, 0 } },
        // v5
        new[] { new[] { 26, 1, 108, 0, 0 }, new[] { 24, 2, 43, 0, 0 }, new[] { 18, 2, 15, 2, 16 }, new[] { 22, 2, 11, 2, 12 } },
        // v6
        new[] { new[] { 18, 2, 68, 0, 0 }, new[] { 16, 4, 27, 0, 0 }, new[] { 24, 4, 19, 0, 0 }, new[] { 28, 4, 15, 0, 0 } },
        // v7
        new[] { new[] { 20, 2, 78, 0, 0 }, new[] { 18, 4, 31, 0, 0 }, new[] { 18, 2, 14, 4, 15 }, new[] { 26, 4, 13, 1, 14 } },
        // v8
        new[] { new[] { 24, 2, 97, 0, 0 }, new[] { 22, 2, 38, 2, 39 }, new[] { 22, 4, 18, 2, 19 }, new[] { 26, 4, 14, 2, 15 } },
        // v9
        new[] { new[] { 30, 2, 116, 0, 0 }, new[] { 22, 3, 36, 2, 37 }, new[] { 20, 4, 16, 4, 17 }, new[] { 24, 4, 12, 4, 13 } },
        // v10
        new[] { new[] { 18, 2, 68, 2, 69 }, new[] { 26, 4, 43, 1, 44 }, new[] { 24, 6, 19, 2, 20 }, new[] { 28, 6, 15, 2, 16 } },
    };

    public const int MaxVersion = 10;

    public static int DataCodewords(int version, QrEcc ecc)
    {
        int[] spec = BlockTable[version - 1][(int)ecc];
        return spec[1] * spec[2] + spec[3] * spec[4];
    }

    /// <summary>Encodes UTF-8 bytes; picks the smallest version that fits.</summary>
    public static bool TryEncode(ReadOnlySpan<byte> data, QrEcc ecc, out QrMatrix? matrix, out string error, int? forceMask = null)
    {
        matrix = null;
        error = string.Empty;

        int version = 0;
        for (int v = 1; v <= MaxVersion; v++)
        {
            int capacityBits = DataCodewords(v, ecc) * 8;
            int needed = 4 + CharCountBits(v) + 8 * data.Length;
            if (needed <= capacityBits) { version = v; break; }
        }

        if (version == 0)
        {
            error = $"payload of {data.Length} bytes does not fit version {MaxVersion}-{ecc} " +
                    $"({DataCodewords(MaxVersion, ecc)} data codewords)";
            return false;
        }

        byte[] codewords = BuildCodewords(data, version, ecc);
        matrix = BuildMatrix(codewords, version, ecc, forceMask);
        return true;
    }

    private static int CharCountBits(int version) => version <= 9 ? 8 : 16;

    private static byte[] BuildCodewords(ReadOnlySpan<byte> data, int version, QrEcc ecc)
    {
        int totalData = DataCodewords(version, ecc);
        int capacityBits = totalData * 8;

        var bits = new List<bool>(capacityBits + 8);
        void Append(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        Append(0b0100, 4);                       // byte mode
        Append(data.Length, CharCountBits(version));
        foreach (byte b in data) Append(b, 8);

        // Terminator, then pad to a byte boundary, then alternating pad bytes.
        int terminator = Math.Min(4, capacityBits - bits.Count);
        for (int i = 0; i < terminator; i++) bits.Add(false);
        while (bits.Count % 8 != 0) bits.Add(false);

        var payload = new byte[totalData];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i]) payload[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        }
        for (int i = bits.Count / 8, toggle = 0; i < totalData; i++, toggle++)
        {
            payload[i] = (toggle % 2 == 0) ? (byte)0xEC : (byte)0x11;
        }

        // Split into blocks, add Reed-Solomon parity, then interleave.
        int[] spec = BlockTable[version - 1][(int)ecc];
        int ecPerBlock = spec[0];
        var dataBlocks = new List<byte[]>();
        var ecBlocks = new List<byte[]>();
        int offset = 0;

        void AddBlock(int count, int dataLen)
        {
            for (int i = 0; i < count; i++)
            {
                byte[] block = payload.AsSpan(offset, dataLen).ToArray();
                offset += dataLen;
                dataBlocks.Add(block);
                ecBlocks.Add(ComputeEcc(block, ecPerBlock));
            }
        }

        AddBlock(spec[1], spec[2]);
        AddBlock(spec[3], spec[4]);

        var result = new List<byte>(totalData + ecPerBlock * dataBlocks.Count);
        int maxData = dataBlocks.Max(b => b.Length);
        for (int i = 0; i < maxData; i++)
        {
            foreach (byte[] block in dataBlocks)
            {
                if (i < block.Length) result.Add(block[i]);
            }
        }
        for (int i = 0; i < ecPerBlock; i++)
        {
            foreach (byte[] block in ecBlocks) result.Add(block[i]);
        }

        return result.ToArray();
    }

    // ------------------------------------------------------- Reed-Solomon

    private static byte GfMul(byte a, byte b)
        => (a == 0 || b == 0) ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte[] GeneratorPolynomial(int degree)
    {
        byte[] g = { 1 };
        for (int i = 0; i < degree; i++)
        {
            var next = new byte[g.Length + 1];
            for (int j = 0; j < g.Length; j++)
            {
                next[j] ^= g[j];
                next[j + 1] ^= GfMul(g[j], Exp[i]);
            }
            g = next;
        }
        return g;
    }

    private static byte[] ComputeEcc(byte[] data, int ecCount)
    {
        byte[] generator = GeneratorPolynomial(ecCount);
        var remainder = new byte[ecCount];

        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, ecCount - 1);
            remainder[ecCount - 1] = 0;
            if (factor == 0) continue;
            for (int j = 0; j < ecCount; j++)
            {
                remainder[j] ^= GfMul(generator[j + 1], factor);
            }
        }
        return remainder;
    }

    // ------------------------------------------------------------ matrix

    private static QrMatrix BuildMatrix(byte[] codewords, int version, QrEcc ecc, int? forceMask)
    {
        int size = version * 4 + 17;
        var modules = new bool[size, size];
        var isFunction = new bool[size, size];

        void Set(int col, int row, bool dark)
        {
            if (col < 0 || row < 0 || col >= size || row >= size) return;
            modules[row, col] = dark;
            isFunction[row, col] = true;
        }

        // Timing patterns.
        for (int i = 0; i < size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }

        // Finder patterns + separators (distance 4 ring is the separator).
        void Finder(int x, int y)
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(x + dx, y + dy, dist != 2 && dist != 4);
                }
            }
        }

        Finder(3, 3);
        Finder(size - 4, 3);
        Finder(3, size - 4);

        // Alignment patterns.
        int[] positions = AlignmentPositions(version, size);
        for (int i = 0; i < positions.Length; i++)
        {
            for (int j = 0; j < positions.Length; j++)
            {
                bool corner =
                    (i == 0 && j == 0) ||
                    (i == 0 && j == positions.Length - 1) ||
                    (i == positions.Length - 1 && j == 0);
                if (corner) continue;

                int cx = positions[j];
                int cy = positions[i];
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        Set(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }
        }

        // Version information (versions 7+).
        if (version >= 7)
        {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = (version << 12) | rem;

            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3;
                int b = i / 3;
                Set(a, b, bit);
                Set(b, a, bit);
            }
        }

        // Reserve the format-information modules before data placement.
        DrawFormatBits(modules, isFunction, size, ecc, 0);
        Set(8, size - 8, true); // always-dark module

        // Data placement: two-module-wide columns, right to left, zig-zag.
        int bitIndex = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (isFunction[y, x] || bitIndex >= codewords.Length * 8) continue;
                    modules[y, x] = ((codewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                    bitIndex++;
                }
            }
        }

        // Choose the mask with the lowest penalty (or honour forceMask).
        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        if (forceMask.HasValue)
        {
            bestMask = forceMask.Value & 7;
            ApplyMask(modules, isFunction, size, bestMask);
        }
        else
        {
            bool[,] best = null!;
            for (int mask = 0; mask < 8; mask++)
            {
                var candidate = (bool[,])modules.Clone();
                ApplyMask(candidate, isFunction, size, mask);
                DrawFormatBits(candidate, isFunction, size, ecc, mask);
                int penalty = Penalty(candidate, size);
                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    bestMask = mask;
                    best = candidate;
                }
            }
            modules = best;
        }

        DrawFormatBits(modules, isFunction, size, ecc, bestMask);
        Set(8, size - 8, true);
        return new QrMatrix(size, ecc, version, bestMask, modules);
    }

    private static int[] AlignmentPositions(int version, int size)
    {
        if (version == 1) return Array.Empty<int>();
        int numAlign = version / 7 + 2;
        int step = (version == 32) ? 26 : (version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private static void ApplyMask(bool[,] modules, bool[,] isFunction, int size, int mask)
    {
        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++)
            {
                if (isFunction[row, col]) continue;
                bool invert = mask switch
                {
                    0 => (row + col) % 2 == 0,
                    1 => row % 2 == 0,
                    2 => col % 3 == 0,
                    3 => (row + col) % 3 == 0,
                    4 => (row / 2 + col / 3) % 2 == 0,
                    5 => (row * col) % 2 + (row * col) % 3 == 0,
                    6 => ((row * col) % 2 + (row * col) % 3) % 2 == 0,
                    7 => ((row + col) % 2 + (row * col) % 3) % 2 == 0,
                    _ => false,
                };
                if (invert) modules[row, col] = !modules[row, col];
            }
        }
    }

    private static void DrawFormatBits(bool[,] modules, bool[,] isFunction, int size, QrEcc ecc, int mask)
    {
        // ECC level indicator bits: L=01, M=00, Q=11, H=10.
        int eccBits = ecc switch
        {
            QrEcc.L => 1,
            QrEcc.M => 0,
            QrEcc.Q => 3,
            QrEcc.H => 2,
            _ => 0,
        };

        int data = (eccBits << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = ((data << 10) | rem) ^ 0x5412;

        void Put(int col, int row, bool dark)
        {
            modules[row, col] = dark;
            isFunction[row, col] = true;
        }

        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (int i = 0; i <= 5; i++) Put(8, i, Bit(i));
        Put(8, 7, Bit(6));
        Put(8, 8, Bit(7));
        Put(7, 8, Bit(8));
        for (int i = 9; i < 15; i++) Put(14 - i, 8, Bit(i));

        for (int i = 0; i < 8; i++) Put(size - 1 - i, 8, Bit(i));
        for (int i = 8; i < 15; i++) Put(8, size - 15 + i, Bit(i));
        Put(8, size - 8, true);
    }

    private static int Penalty(bool[,] m, int size)
    {
        int penalty = 0;

        // Rule 1: runs of five or more same-coloured modules.
        for (int row = 0; row < size; row++)
        {
            int run = 1;
            for (int col = 1; col < size; col++)
            {
                if (m[row, col] == m[row, col - 1]) run++;
                else { if (run >= 5) penalty += 3 + (run - 5); run = 1; }
            }
            if (run >= 5) penalty += 3 + (run - 5);
        }
        for (int col = 0; col < size; col++)
        {
            int run = 1;
            for (int row = 1; row < size; row++)
            {
                if (m[row, col] == m[row - 1, col]) run++;
                else { if (run >= 5) penalty += 3 + (run - 5); run = 1; }
            }
            if (run >= 5) penalty += 3 + (run - 5);
        }

        // Rule 2: 2x2 blocks of one colour.
        for (int row = 0; row < size - 1; row++)
        {
            for (int col = 0; col < size - 1; col++)
            {
                bool v = m[row, col];
                if (v == m[row, col + 1] && v == m[row + 1, col] && v == m[row + 1, col + 1]) penalty += 3;
            }
        }

        // Rule 3: 1:1:3:1:1 finder-like pattern with four light modules.
        int[] patternA = { 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0 };
        int[] patternB = { 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 };
        int[] sequence = new int[size];

        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++) sequence[col] = m[row, col] ? 1 : 0;
            penalty += CountPattern(sequence, patternA) * 40;
            penalty += CountPattern(sequence, patternB) * 40;
        }
        for (int col = 0; col < size; col++)
        {
            for (int row = 0; row < size; row++) sequence[row] = m[row, col] ? 1 : 0;
            penalty += CountPattern(sequence, patternA) * 40;
            penalty += CountPattern(sequence, patternB) * 40;
        }

        // Rule 4: deviation from a 50% dark ratio.
        int dark = 0;
        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++) if (m[row, col]) dark++;
        }
        int total = size * size;
        int k = Math.Abs(dark * 20 - total * 10) / total;
        penalty += k * 10;

        return penalty;
    }

    private static int CountPattern(int[] haystack, int[] needle)
    {
        int count = 0;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) count++;
        }
        return count;
    }

    // ------------------------------------------------------------ rendering

    /// <summary>
    /// Pure-ASCII rendering (two characters per module) so it survives any
    /// console code page, as the C++ integrator requires.
    /// </summary>
    public static string ToAsciiArt(QrMatrix matrix, int quietZone = 4, string dark = "##", string light = "  ")
    {
        var sb = new StringBuilder();
        int size = matrix.Size;
        string blank = string.Concat(Enumerable.Repeat(light, size + quietZone * 2));

        for (int i = 0; i < quietZone; i++) sb.AppendLine(blank);
        for (int row = 0; row < size; row++)
        {
            sb.Append(string.Concat(Enumerable.Repeat(light, quietZone)));
            for (int col = 0; col < size; col++) sb.Append(matrix[row, col] ? dark : light);
            sb.Append(string.Concat(Enumerable.Repeat(light, quietZone)));
            sb.AppendLine();
        }
        for (int i = 0; i < quietZone; i++) sb.AppendLine(blank);
        return sb.ToString();
    }

    /// <summary>Compact half-block rendering (two module rows per text row).</summary>
    public static string ToUnicodeArt(QrMatrix matrix, int quietZone = 2)
    {
        var sb = new StringBuilder();
        int size = matrix.Size;

        string Row(int row, int from, int to)
        {
            var line = new StringBuilder();
            for (int col = from; col < to; col++)
            {
                bool top = row >= 0 && row < size && matrix[row, col];
                bool bottom = row + 1 >= 0 && row + 1 < size && matrix[row + 1, col];
                line.Append((top, bottom) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    _ => ' ',
                });
            }
            return line.ToString();
        }

        string blank = new string(' ', size + quietZone * 2);
        for (int i = 0; i < quietZone; i++) sb.AppendLine(blank);
        for (int row = -quietZone; row < size + quietZone; row += 2)
        {
            sb.Append(new string(' ', quietZone));
            sb.Append(Row(row, 0, size));
            sb.Append(new string(' ', quietZone));
            sb.AppendLine();
        }
        for (int i = 0; i < quietZone; i++) sb.AppendLine(blank);
        return sb.ToString();
    }

    /// <summary>
    /// Minimal 8-bit greyscale PNG (no external dependency). 8-bit rather than
    /// 1-bit because some viewers and decoders mishandle 1-bit greyscale.
    /// </summary>
    public static byte[] ToPng(QrMatrix matrix, int scale = 8, int quietZone = 4)
    {
        int modules = matrix.Size + quietZone * 2;
        int pixels = modules * scale;
        int rowBytes = pixels; // one byte per pixel

        var raw = new byte[(rowBytes + 1) * pixels];
        int p = 0;
        for (int y = 0; y < pixels; y++)
        {
            raw[p++] = 0; // filter type: none
            int moduleRow = y / scale - quietZone;
            for (int x = 0; x < pixels; x++)
            {
                int moduleCol = x / scale - quietZone;
                bool dark = moduleRow >= 0 && moduleRow < matrix.Size &&
                            moduleCol >= 0 && moduleCol < matrix.Size &&
                            matrix[moduleRow, moduleCol];
                raw[p + x] = dark ? (byte)0x00 : (byte)0xFF;
            }
            p += rowBytes;
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }
            compressed = ms.ToArray();
        }

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, pixels);
        WriteBigEndian(ihdr, 4, pixels);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 0;   // colour type: greyscale
        ihdr[10] = 0;  // deflate
        ihdr[11] = 0;  // adaptive filtering
        ihdr[12] = 0;  // no interlace
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)crc));
        stream.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte x in a) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (byte x in b) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
