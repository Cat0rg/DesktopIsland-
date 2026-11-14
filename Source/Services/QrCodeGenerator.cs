using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIsland.Services;

/// <summary>
/// Lightweight self-contained ISO/IEC 18004 QR Code encoder (Byte mode, ECC-L, Versions 1..6)
/// rendering crisp WPF BitmapSource images without external dependencies.
/// </summary>
public static class QrCodeGenerator
{
    private readonly record struct VersionSpec(int Version, int Size, int DataCodewords, int EcPerBlock, int Blocks, int[] AlignCenters);

    private static readonly VersionSpec[] Specs =
    {
        new(1, 21, 19, 7, 1, Array.Empty<int>()),
        new(2, 25, 34, 10, 1, new[] { 6, 18 }),
        new(3, 29, 55, 15, 1, new[] { 6, 22 }),
        new(4, 33, 80, 20, 1, new[] { 6, 26 }),
        new(5, 37, 108, 26, 1, new[] { 6, 30 }),
        new(6, 41, 136, 18, 2, new[] { 6, 34 })
    };

    private static readonly byte[] GfExp = new byte[512];
    private static readonly byte[] GfLog = new byte[256];

    static QrCodeGenerator()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            GfExp[i] = (byte)x;
            GfExp[i + 255] = (byte)x;
            GfLog[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0)
                x ^= 0x11D;
        }
    }

    public static BitmapSource? RenderBitmap(string text, int scalePx = 5)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var bytes = Encoding.UTF8.GetBytes(text);
        VersionSpec? chosen = null;
        foreach (var spec in Specs)
        {
            if (bytes.Length <= spec.DataCodewords - 2)
            {
                chosen = spec;
                break;
            }
        }

        if (chosen == null)
        {
            // Trim overly long text cleanly so QR still generates for long URLs/tokens within 134 bytes
            int maxLen = Specs[^1].DataCodewords - 2;
            Array.Resize(ref bytes, maxLen);
            chosen = Specs[^1];
        }

        var v = chosen.Value;
        var data = EncodeDataCodewords(bytes, v.DataCodewords);
        var stream = InterleaveWithEcc(data, v.Blocks, v.EcPerBlock);

        var bestMatrix = BuildBestMatrix(v, stream);
        return RenderMatrixToBitmap(bestMatrix, v.Size, scalePx);
    }

    private static byte[] EncodeDataCodewords(byte[] payload, int capacityCodewords)
    {
        var bits = new List<bool>(capacityCodewords * 8);
        void PushBits(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
                bits.Add(((value >> i) & 1) != 0);
        }

        // Byte mode indicator (0100) + 8-bit character count (V1..V6)
        PushBits(0b0100, 4);
        PushBits(payload.Length, 8);
        foreach (byte b in payload)
            PushBits(b, 8);

        int maxBits = capacityCodewords * 8;
        int terminator = Math.Min(4, maxBits - bits.Count);
        for (int i = 0; i < terminator; i++) bits.Add(false);
        while (bits.Count % 8 != 0) bits.Add(false);

        var outBytes = new byte[capacityCodewords];
        int byteIdx = 0;
        for (int i = 0; i < bits.Count; i += 8)
        {
            byte val = 0;
            for (int b = 0; b < 8; b++)
                val = (byte)((val << 1) | (bits[i + b] ? 1 : 0));
            outBytes[byteIdx++] = val;
        }

        bool alt = true;
        while (byteIdx < capacityCodewords)
        {
            outBytes[byteIdx++] = alt ? (byte)0xEC : (byte)0x11;
            alt = !alt;
        }
        return outBytes;
    }

    private static byte[] ComputeRsRemainder(byte[] data, int ecCount)
    {
        var gen = new byte[ecCount + 1];
        gen[0] = 1;
        for (int i = 0; i < ecCount; i++)
        {
            byte root = GfExp[i];
            for (int j = i + 1; j >= 1; j--)
                gen[j] = (byte)(gen[j] ^ GfMul(gen[j - 1], root));
        }

        var rem = new byte[ecCount];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, ecCount - 1);
            rem[ecCount - 1] = 0;
            for (int j = 0; j < ecCount; j++)
                rem[j] ^= GfMul(gen[j + 1], factor);
        }
        return rem;
    }

    private static byte GfMul(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return GfExp[GfLog[a] + GfLog[b]];
    }

    private static byte[] InterleaveWithEcc(byte[] data, int blocks, int ecPerBlock)
    {
        int blockSize = data.Length / blocks;
        var dataBlocks = new byte[blocks][];
        var eccBlocks = new byte[blocks][];
        for (int b = 0; b < blocks; b++)
        {
            dataBlocks[b] = new byte[blockSize];
            Array.Copy(data, b * blockSize, dataBlocks[b], 0, blockSize);
            eccBlocks[b] = ComputeRsRemainder(dataBlocks[b], ecPerBlock);
        }

        var result = new List<byte>(data.Length + blocks * ecPerBlock);
        for (int i = 0; i < blockSize; i++)
            for (int b = 0; b < blocks; b++)
                result.Add(dataBlocks[b][i]);

        for (int i = 0; i < ecPerBlock; i++)
            for (int b = 0; b < blocks; b++)
                result.Add(eccBlocks[b][i]);

        return result.ToArray();
    }

    private static bool[,] BuildBestMatrix(VersionSpec spec, byte[] codewords)
    {
        int n = spec.Size;
        var isFunction = new bool[n, n];
        var baseModules = new bool[n, n];

        void SetFunc(int r, int c, bool dark)
        {
            if (r >= 0 && r < n && c >= 0 && c < n)
            {
                isFunction[r, c] = true;
                baseModules[r, c] = dark;
            }
        }

        void DrawFinder(int top, int left)
        {
            for (int dr = -1; dr <= 7; dr++)
            {
                for (int dc = -1; dc <= 7; dc++)
                {
                    int r = top + dr, c = left + dc;
                    if (r < 0 || r >= n || c < 0 || c >= n) continue;
                    bool border = dr == -1 || dr == 7 || dc == -1 || dc == 7;
                    bool outer = dr == 0 || dr == 6 || dc == 0 || dc == 6;
                    bool center = dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4;
                    SetFunc(r, c, !border && (outer || center));
                }
            }
        }

        DrawFinder(0, 0);
        DrawFinder(0, n - 7);
        DrawFinder(n - 7, 0);

        // Alignment patterns
        for (int i = 0; i < spec.AlignCenters.Length; i++)
        {
            for (int j = 0; j < spec.AlignCenters.Length; j++)
            {
                int cr = spec.AlignCenters[i], cc = spec.AlignCenters[j];
                if (isFunction[cr, cc]) continue;
                for (int dr = -2; dr <= 2; dr++)
                    for (int dc = -2; dc <= 2; dc++)
                        SetFunc(cr + dr, cc + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
            }
        }

        // Timing patterns & dark module
        for (int i = 8; i < n - 8; i++)
        {
            SetFunc(6, i, i % 2 == 0);
            SetFunc(i, 6, i % 2 == 0);
        }
        SetFunc(n - 8, 8, true);

        // Reserve format information zones
        for (int i = 0; i <= 8; i++)
        {
            if (i != 6) { isFunction[8, i] = true; isFunction[i, 8] = true; }
        }
        for (int i = n - 8; i < n; i++) isFunction[8, i] = true;
        for (int i = n - 7; i < n; i++) isFunction[i, 8] = true;

        // Place codewords along upward/downward zig-zag columns
        int bitTotal = codewords.Length * 8;
        int bitIdx = 0;
        bool up = true;
        for (int right = n - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int step = 0; step < n; step++)
            {
                int r = up ? (n - 1 - step) : step;
                for (int colOffset = 0; colOffset < 2; colOffset++)
                {
                    int c = right - colOffset;
                    if (!isFunction[r, c] && bitIdx < bitTotal)
                    {
                        bool bit = ((codewords[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        baseModules[r, c] = bit;
                        bitIdx++;
                    }
                }
            }
            up = !up;
        }

        // Evaluate masks 0..7 and select lowest penalty
        int bestPenalty = int.MaxValue;
        bool[,] best = baseModules;
        for (int mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[,])baseModules.Clone();
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    if (isFunction[r, c]) continue;
                    bool flip = mask switch
                    {
                        0 => (r + c) % 2 == 0,
                        1 => r % 2 == 0,
                        2 => c % 3 == 0,
                        3 => (r + c) % 3 == 0,
                        4 => (r / 2 + c / 3) % 2 == 0,
                        5 => (r * c) % 2 + (r * c) % 3 == 0,
                        6 => ((r * c) % 2 + (r * c) % 3) % 2 == 0,
                        _ => ((r + c) % 2 + (r * c) % 3) % 2 == 0
                    };
                    if (flip) candidate[r, c] = !candidate[r, c];
                }
            }

            WriteFormatInfo(candidate, n, mask);
            int p = ComputePenalty(candidate, n);
            if (p < bestPenalty)
            {
                bestPenalty = p;
                best = candidate;
            }
        }
        return best;
    }

    private static void WriteFormatInfo(bool[,] m, int n, int mask)
    {
        int d = (1 << 3) | mask; // ECC-L = 01
        int rem = d << 10;
        for (int i = 4; i >= 0; i--)
            if (((rem >> (i + 10)) & 1) != 0)
                rem ^= 0x537 << i;
        int format = ((d << 10) | rem) ^ 0x5412;

        for (int i = 0; i < 6; i++)
            m[8, i] = ((format >> (14 - i)) & 1) != 0;
        m[8, 7] = ((format >> 8) & 1) != 0;
        m[8, 8] = ((format >> 7) & 1) != 0;
        m[7, 8] = ((format >> 6) & 1) != 0;
        for (int i = 9; i < 15; i++)
            m[14 - i, 8] = ((format >> (14 - i)) & 1) != 0;

        for (int i = 0; i < 7; i++)
            m[n - 1 - i, 8] = ((format >> (14 - i)) & 1) != 0;
        for (int i = 7; i < 15; i++)
            m[8, n - 15 + i] = ((format >> (14 - i)) & 1) != 0;
    }

    private static int ComputePenalty(bool[,] m, int n)
    {
        int penalty = 0;
        for (int r = 0; r < n; r++)
        {
            int runLen = 1;
            for (int c = 1; c < n; c++)
            {
                if (m[r, c] == m[r, c - 1]) runLen++;
                else { if (runLen >= 5) penalty += 3 + (runLen - 5); runLen = 1; }
            }
            if (runLen >= 5) penalty += 3 + (runLen - 5);
        }
        for (int c = 0; c < n; c++)
        {
            int runLen = 1;
            for (int r = 1; r < n; r++)
            {
                if (m[r, c] == m[r - 1, c]) runLen++;
                else { if (runLen >= 5) penalty += 3 + (runLen - 5); runLen = 1; }
            }
            if (runLen >= 5) penalty += 3 + (runLen - 5);
        }
        for (int r = 0; r < n - 1; r++)
            for (int c = 0; c < n - 1; c++)
                if (m[r, c] == m[r, c + 1] && m[r, c] == m[r + 1, c] && m[r, c] == m[r + 1, c + 1])
                    penalty += 3;
        return penalty;
    }

    private static BitmapSource RenderMatrixToBitmap(bool[,] m, int n, int scale)
    {
        const int quiet = 2;
        int imgModules = n + quiet * 2;
        int px = imgModules * scale;
        int stride = px * 4;
        var buf = new byte[px * stride];

        for (int y = 0; y < px; y++)
        {
            int mr = y / scale - quiet;
            for (int x = 0; x < px; x++)
            {
                int mc = x / scale - quiet;
                bool dark = mr >= 0 && mr < n && mc >= 0 && mc < n && m[mr, mc];
                byte v = dark ? (byte)13 : (byte)250;
                int idx = y * stride + x * 4;
                buf[idx] = v;
                buf[idx + 1] = v;
                buf[idx + 2] = dark ? (byte)20 : (byte)255;
                buf[idx + 3] = 255;
            }
        }

        var bmp = BitmapSource.Create(px, px, 96, 96, PixelFormats.Bgra32, null, buf, stride);
        bmp.Freeze();
        return bmp;
    }
}
