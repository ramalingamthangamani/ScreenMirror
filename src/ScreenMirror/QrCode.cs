using System;
using System.Collections.Generic;
using System.Text;

namespace ScreenMirror;

/// <summary>
/// Minimal QR code generator (byte mode, error correction level M, versions 1-40), used to show the
/// connection link on the PC so a phone can open it by pointing its camera at the screen.
/// Follows ISO/IEC 18004; structure adapted from Project Nayuki's reference implementation.
/// </summary>
public sealed class QrCode
{
    private static readonly int[] EccPerBlockM =
        { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 };
    private static readonly int[] NumBlocksM =
        { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 };
    private const int EclFormatBitsM = 0;

    private readonly bool[,] _modules;
    private readonly bool[,] _isFunction;

    public int Version { get; }
    public int Size { get; }
    public bool this[int x, int y] => x >= 0 && y >= 0 && x < Size && y < Size && _modules[y, x];

    private QrCode(int version)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];
    }

    public static QrCode Encode(string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        int version = 1;
        for (; ; version++)
        {
            if (version > 40) throw new ArgumentException("Text too long for a QR code");
            int countBits = version <= 9 ? 8 : 16;
            if (4 + countBits + data.Length * 8 <= NumDataCodewords(version) * 8) break;
        }

        // Bit stream: mode indicator (byte), character count, data, terminator, padding.
        var bits = new List<bool>();
        void Append(int val, int len) { for (int i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); }
        Append(0x4, 4);
        Append(data.Length, version <= 9 ? 8 : 16);
        foreach (byte b in data) Append(b, 8);
        int capacityBits = NumDataCodewords(version) * 8;
        Append(0, Math.Min(4, capacityBits - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) Append(pad, 8);

        var codewords = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        var qr = new QrCode(version);
        qr.DrawFunctionPatterns();
        qr.DrawCodewords(qr.AddEccAndInterleave(codewords));

        int bestMask = 0;
        long minPenalty = long.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            qr.ApplyMask(mask);
            qr.DrawFormatBits(mask);
            long penalty = qr.Penalty();
            if (penalty < minPenalty) { bestMask = mask; minPenalty = penalty; }
            qr.ApplyMask(mask); // XOR again to undo
        }
        qr.ApplyMask(bestMask);
        qr.DrawFormatBits(bestMask);
        return qr;
    }

    /// <summary>Renders the code as a compact SVG (one path, 4-module quiet zone).</summary>
    public string ToSvg(int border = 4, string dark = "#000", string light = "#fff")
    {
        var sb = new StringBuilder();
        int dim = Size + border * 2;
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {dim} {dim}\" shape-rendering=\"crispEdges\">");
        sb.Append($"<rect width=\"100%\" height=\"100%\" fill=\"{light}\"/><path fill=\"{dark}\" d=\"");
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                if (_modules[y, x]) sb.Append($"M{x + border},{y + border}h1v1h-1z");
        sb.Append("\"/></svg>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ construction

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (int i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        int[] align = AlignmentPositions();
        int n = align.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0))
                    DrawAlignment(align[i], align[j]);

        DrawFormatBits(0); // reserve; overwritten after masking
        DrawVersion();
    }

    private void DrawFinder(int cx, int cy)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size) SetFunction(x, y, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private void DrawFormatBits(int mask)
    {
        int data = EclFormatBitsM << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;
        static bool Bit(int v, int i) => ((v >> i) & 1) != 0;

        for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

        for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true); // always dark
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        int rem = Version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        int bits = Version << 12 | rem;
        for (int i = 0; i < 18; i++)
        {
            bool bit = ((bits >> i) & 1) != 0;
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1) return Array.Empty<int>();
        int num = Version / 7 + 2;
        int step = Version == 32 ? 26 : (Version * 4 + num * 2 + 1) / (num * 2 - 2) * 2;
        var result = new int[num];
        result[0] = 6;
        for (int i = num - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private static int NumRawDataModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    private static int NumDataCodewords(int ver) => NumRawDataModules(ver) / 8 - EccPerBlockM[ver] * NumBlocksM[ver];

    private byte[] AddEccAndInterleave(byte[] data)
    {
        int numBlocks = NumBlocksM[Version];
        int blockEccLen = EccPerBlockM[Version];
        int rawCodewords = NumRawDataModules(Version) / 8;
        int numShortBlocks = numBlocks - rawCodewords % numBlocks;
        int shortBlockLen = rawCodewords / numBlocks;

        var blocks = new List<byte[]>(numBlocks);
        byte[] divisor = RsDivisor(blockEccLen);
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var dat = new byte[datLen];
            Array.Copy(data, k, dat, 0, datLen);
            k += datLen;
            byte[] ecc = RsRemainder(dat, divisor);
            var block = new byte[shortBlockLen + 1];
            Array.Copy(dat, block, datLen);
            Array.Copy(ecc, 0, block, block.Length - blockEccLen, blockEccLen);
            blocks.Add(block);
        }

        var result = new byte[rawCodewords];
        int idx = 0;
        for (int i = 0; i < shortBlockLen + 1; i++)
            for (int j = 0; j < numBlocks; j++)
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks) // skip the padding byte of short blocks
                    result[idx++] = blocks[j][i];
        return result;
    }

    private static byte[] RsDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = RsMultiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = RsMultiply(root, 0x02);
        }
        return result;
    }

    private static byte[] RsRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= RsMultiply(divisor[i], factor);
        }
        return result;
    }

    private static byte RsMultiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < Size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
            }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert && !_isFunction[y, x]) _modules[y, x] = !_modules[y, x];
            }
    }

    private long Penalty()
    {
        const int N1 = 3, N2 = 3, N3 = 40, N4 = 10;
        long result = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int a = 0; a < Size; a++)
            {
                bool runColor = false;
                int run = 0;
                var history = new int[7];
                for (int b = 0; b < Size; b++)
                {
                    bool m = pass == 0 ? _modules[a, b] : _modules[b, a];
                    if (m == runColor)
                    {
                        run++;
                        if (run == 5) result += N1;
                        else if (run > 5) result++;
                    }
                    else
                    {
                        FinderPenaltyAddHistory(run, history);
                        if (!runColor) result += FinderPenaltyCountPatterns(history) * N3;
                        runColor = m;
                        run = 1;
                    }
                }
                result += FinderPenaltyTerminateAndCount(runColor, run, history) * N3;
            }
        }

        for (int y = 0; y < Size - 1; y++)
            for (int x = 0; x < Size - 1; x++)
            {
                bool c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) result += N2;
            }

        int dark = 0;
        foreach (bool m in _modules) if (m) dark++;
        int total = Size * Size;
        int k = (int)((Math.Abs(dark * 20L - total * 10L) + total - 1) / total) - 1;
        result += Math.Max(0, k) * N4;
        return result;
    }

    private int FinderPenaltyCountPatterns(int[] h)
    {
        int n = h[1];
        bool core = n > 0 && h[2] == n && h[3] == n * 3 && h[4] == n && h[5] == n;
        return (core && h[0] >= n * 4 && h[6] >= n ? 1 : 0) + (core && h[6] >= n * 4 && h[0] >= n ? 1 : 0);
    }

    private int FinderPenaltyTerminateAndCount(bool runColor, int run, int[] history)
    {
        if (runColor)
        {
            FinderPenaltyAddHistory(run, history);
            run = 0;
        }
        run += Size;
        FinderPenaltyAddHistory(run, history);
        return FinderPenaltyCountPatterns(history);
    }

    private void FinderPenaltyAddHistory(int run, int[] history)
    {
        if (history[0] == 0) run += Size; // light border before the first run
        Array.Copy(history, 0, history, 1, history.Length - 1);
        history[0] = run;
    }
}
