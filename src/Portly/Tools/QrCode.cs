using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Portly.Tools
{
    /// <summary>
    /// Small QR code encoder (byte mode, error correction level M, versions 1-10: up to 213 bytes, plenty for an address).
    /// Follows ISO/IEC 18004 the way Project Nayuki's MIT-licensed reference implementation does.
    /// </summary>
    public class QrCode
    {
        public readonly int Size;
        readonly bool[,] _dark;      // [y, x]
        readonly bool[,] _function;  // finder, timing, alignment, format and version areas

        // level M, index = version (0 unused)
        static readonly int[] EccPerBlock = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        static readonly int[] Blocks = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
        const int FormatBitsM = 0;

        public bool this[int x, int y] { get { return _dark[y, x]; } }

        QrCode(int version)
        {
            Size = version * 4 + 17;
            _dark = new bool[Size, Size];
            _function = new bool[Size, Size];
        }

        public static QrCode Encode(string text)
        {
            var data = Encoding.UTF8.GetBytes(text);
            int version = 1;
            for (; version <= 10; version++)
            {
                int countBits = version <= 9 ? 8 : 16;
                if (4 + countBits + data.Length * 8 <= DataCodewords(version) * 8) break;
            }
            if (version > 10) throw new ArgumentException("Text too long for a QR code");

            // bit stream: byte mode, length, data, terminator, padding
            var bits = new List<bool>();
            Action<int, int> add = (val, len) => { for (int i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); };
            add(4, 4);
            add(data.Length, version <= 9 ? 8 : 16);
            foreach (var b in data) add(b, 8);
            int capacity = DataCodewords(version) * 8;
            add(0, Math.Min(4, capacity - bits.Count));
            add(0, (8 - bits.Count % 8) % 8);
            for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) add(pad, 8);
            var codewords = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

            var qr = new QrCode(version);
            qr.DrawFunctionPatterns(version);
            qr.DrawCodewords(AddEccAndInterleave(codewords, version));
            // the mask with the lowest penalty
            int best = 0, bestScore = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                qr.ApplyMask(m);
                qr.DrawFormatBits(m);
                int score = qr.Penalty();
                if (score < bestScore) { best = m; bestScore = score; }
                qr.ApplyMask(m); // undo (XOR)
            }
            qr.ApplyMask(best);
            qr.DrawFormatBits(best);
            return qr;
        }

        /// <summary>Black on white with a 4-module quiet zone, <paramref name="scale"/> pixels per module.</summary>
        public Bitmap ToBitmap(int scale)
        {
            int border = 4, px = (Size + border * 2) * scale;
            var bmp = new Bitmap(px, px);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                        if (_dark[y, x]) g.FillRectangle(Brushes.Black, (x + border) * scale, (y + border) * scale, scale, scale);
            }
            return bmp;
        }

        static int RawDataModules(int ver)
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

        static int DataCodewords(int ver) { return RawDataModules(ver) / 8 - EccPerBlock[ver] * Blocks[ver]; }

        void Set(int x, int y, bool dark) { _dark[y, x] = dark; _function[y, x] = true; }

        void DrawFunctionPatterns(int version)
        {
            for (int i = 0; i < Size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
            Finder(3, 3); Finder(Size - 4, 3); Finder(3, Size - 4);
            var align = AlignmentPositions(version);
            int n = align.Length;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0)) Alignment(align[i], align[j]);
            DrawFormatBits(0); // reserved now, real values after masking
            if (version >= 7)
            {
                int rem = version;
                for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                long vbits = (long)version << 12 | (uint)rem;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((vbits >> i) & 1) != 0;
                    int a = Size - 11 + i % 3, b = i / 3;
                    Set(a, b, bit); Set(b, a, bit);
                }
            }
        }

        void Finder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy, dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (x >= 0 && x < Size && y >= 0 && y < Size) Set(x, y, dist != 2 && dist != 4);
                }
        }

        void Alignment(int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    Set(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        static int[] AlignmentPositions(int ver)
        {
            if (ver == 1) return new int[0];
            int numAlign = ver / 7 + 2;
            int step = (ver * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2; // v7: 6, 22, 38
            var result = new int[numAlign];
            result[0] = 6;
            for (int i = numAlign - 1, pos = ver * 4 + 10; i >= 1; i--, pos -= step) result[i] = pos;
            return result;
        }

        void DrawFormatBits(int mask)
        {
            int data = FormatBitsM << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;
            Func<int, bool> bit = i => ((bits >> i) & 1) != 0;
            for (int i = 0; i <= 5; i++) Set(8, i, bit(i));
            Set(8, 7, bit(6)); Set(8, 8, bit(7)); Set(7, 8, bit(8));
            for (int i = 9; i < 15; i++) Set(14 - i, 8, bit(i));
            for (int i = 0; i < 8; i++) Set(Size - 1 - i, 8, bit(i));
            for (int i = 8; i < 15; i++) Set(8, Size - 15 + i, bit(i));
            Set(8, Size - 8, true);
        }

        static byte[] AddEccAndInterleave(byte[] data, int ver)
        {
            int numBlocks = Blocks[ver], eccLen = EccPerBlock[ver];
            int raw = RawDataModules(ver) / 8;
            int numShort = numBlocks - raw % numBlocks, shortLen = raw / numBlocks;
            var divisor = RsDivisor(eccLen);
            var blocks = new List<byte[]>();
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int datLen = shortLen - eccLen + (i < numShort ? 0 : 1);
                var dat = new byte[datLen];
                Array.Copy(data, k, dat, 0, datLen);
                k += datLen;
                var ecc = RsRemainder(dat, divisor);
                var block = new byte[shortLen + 1];
                Array.Copy(dat, block, datLen);
                // short blocks get a placeholder byte that is skipped below
                Array.Copy(ecc, 0, block, block.Length - eccLen, eccLen);
                blocks.Add(block);
            }
            var result = new List<byte>();
            for (int i = 0; i < blocks[0].Length; i++)
                for (int j = 0; j < blocks.Count; j++)
                    if (i != shortLen - eccLen || j >= numShort) result.Add(blocks[j][i]);
            return result.ToArray();
        }

        static byte[] RsDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = Mul(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = Mul(root, 0x02);
            }
            return result;
        }

        static byte[] RsRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (var b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= Mul(divisor[i], factor);
            }
            return result;
        }

        static byte Mul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        void DrawCodewords(byte[] data)
        {
            int i = 0;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < Size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vert : vert;
                        if (!_function[y, x] && i < data.Length * 8)
                        {
                            _dark[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
            }
        }

        void ApplyMask(int mask)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    if (_function[y, x]) continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (invert) _dark[y, x] = !_dark[y, x];
                }
        }

        /// <summary>Simplified penalty (long runs, 2x2 blocks, dark/light balance): enough to pick a readable mask.</summary>
        int Penalty()
        {
            int score = 0, dark = 0;
            for (int y = 0; y < Size; y++)
            {
                int runX = 1, runY = 1;
                for (int x = 0; x < Size; x++)
                {
                    if (_dark[y, x]) dark++;
                    if (x > 0)
                    {
                        if (_dark[y, x] == _dark[y, x - 1]) { runX++; if (runX == 5) score += 3; else if (runX > 5) score++; } else runX = 1;
                        if (_dark[x, y] == _dark[x - 1, y]) { runY++; if (runY == 5) score += 3; else if (runY > 5) score++; } else runY = 1;
                    }
                    if (x > 0 && y > 0 && _dark[y, x] == _dark[y - 1, x] && _dark[y, x] == _dark[y, x - 1] && _dark[y, x] == _dark[y - 1, x - 1]) score += 3;
                }
            }
            int total = Size * Size;
            score += (Math.Abs(dark * 20 - total * 10) + total - 1) / total * 10;
            return score;
        }
    }
}
