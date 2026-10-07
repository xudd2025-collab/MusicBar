// QQ QRC format adapted from pyqdes (https://github.com/naiyQAQ/pyqdes).
// Copyright (c) 2023 LX Music. MIT License; notice is in LICENSE.
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicBar
{
    // Public lyric responses use hexadecimal QRC, a modified DES sequence and
    // zlib compression. These format constants are not account credentials.
    internal static class QQWordLyricDecoder
    {
        private const int MaximumBytes = 2 * 1024 * 1024;
        private static readonly int[] Initial = { 57,49,41,33,25,17,9,1,59,51,43,35,27,19,11,3,61,53,45,37,29,21,13,5,63,55,47,39,31,23,15,7,56,48,40,32,24,16,8,0,58,50,42,34,26,18,10,2,60,52,44,36,28,20,12,4,62,54,46,38,30,22,14,6 };
        private static readonly int[] Final = Invert(Initial);
        private static readonly int[] Expansion = { 31,0,1,2,3,4,3,4,5,6,7,8,7,8,9,10,11,12,11,12,13,14,15,16,15,16,17,18,19,20,19,20,21,22,23,24,23,24,25,26,27,28,27,28,29,30,31,0 };
        private static readonly int[] Permutation = { 15,6,19,20,28,11,27,16,0,14,22,25,4,17,30,9,1,7,23,13,31,26,2,8,18,12,29,5,21,10,3,24 };
        private static readonly int[] KeyC = { 56,48,40,32,24,16,8,0,57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35 };
        private static readonly int[] KeyD = { 62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,60,52,44,36,28,20,12,4,27,19,11,3 };
        private static readonly int[] Compression = { 13,16,10,23,0,4,2,27,14,5,20,9,22,18,11,3,25,7,15,6,26,19,12,1,40,51,30,36,46,54,29,39,50,44,32,47,43,48,38,55,33,52,45,41,49,35,28,31 };
        private static readonly int[][] Boxes = {
            new[] { 14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0,15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13 },
            new[] { 15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,3,13,4,7,15,2,8,15,12,0,1,10,6,9,11,5,0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9 },
            new[] { 10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12 },
            new[] { 7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,3,15,0,6,10,10,13,8,9,4,5,11,12,7,2,14 },
            new[] { 2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3 },
            new[] { 12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13 },
            new[] { 4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12 },
            new[] { 13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11 }
        };
        private static readonly ulong[][] Keys = {
            Schedule("!@#)(NHL", true), Schedule("123ZXC!@", false), Schedule("!@#)(*$%", true)
        };

        internal static bool IsHexadecimal(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length <= MaximumBytes && value.Length % 16 == 0 &&
                Regex.IsMatch(value, @"\A[0-9a-fA-F]+\z", RegexOptions.CultureInvariant);
        }
        internal static string Decode(string value)
        {
            if (!IsHexadecimal(value)) throw new InvalidOperationException("QQ 逐字歌词编码无效。");
            try
            {
                byte[] data = new byte[value.Length / 2];
                for (int i = 0; i < data.Length; i++) data[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
                foreach (ulong[] keys in Keys)
                    for (int i = 0; i < data.Length; i += 8) DecodeBlock(data, i, keys);
                if (data.Length < 6 || (data[0] & 15) != 8 || (data[0] >> 4) > 7 ||
                    (data[0] * 256 + data[1]) % 31 != 0 || (data[1] & 32) != 0)
                    throw new InvalidDataException("Invalid QRC compression header.");
                using (var input = new MemoryStream(data, 2, data.Length - 2, false))
                using (var inflater = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    byte[] buffer = new byte[8192]; int read;
                    while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > MaximumBytes) throw new InvalidDataException("QRC is too large.");
                        output.Write(buffer, 0, read);
                    }
                    return new UTF8Encoding(false, true).GetString(output.ToArray());
                }
            }
            catch (InvalidDataException ex) { throw new InvalidOperationException("QQ 逐字歌词暂时无法读取。", ex); }
            catch (DecoderFallbackException ex) { throw new InvalidOperationException("QQ 逐字歌词文字编码无效。", ex); }
        }
        private static int[] Invert(int[] table)
        {
            int[] inverse = new int[table.Length];
            for (int i = 0; i < table.Length; i++) inverse[table[i]] = i;
            return inverse;
        }
        private static ulong Select(ulong value, int bits, int[] table)
        {
            ulong result = 0;
            foreach (int bit in table) result = (result << 1) | ((value >> (bits - 1 - bit)) & 1);
            return result;
        }
        private static uint KeyBit(byte[] key, int bit)
        { return (uint)((key[bit / 32 * 4 + 3 - bit % 32 / 8] >> (7 - bit % 8)) & 1); }
        private static ulong[] Schedule(string text, bool reverse)
        {
            byte[] key = Encoding.ASCII.GetBytes(text); uint c = 0, d = 0;
            for (int i = 0; i < 28; i++) { c |= KeyBit(key, KeyC[i]) << (31 - i); d |= KeyBit(key, KeyD[i]) << (31 - i); }
            int[] shifts = { 1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1 };
            ulong[] keys = new ulong[16];
            for (int i = 0; i < 16; i++)
            {
                int shift = shifts[i];
                c = ((c << shift) | (c >> (28 - shift))) & 0xfffffff0;
                d = ((d << shift) | (d >> (28 - shift))) & 0xfffffff0;
                ulong round = 0;
                for (int j = 0; j < 48; j++)
                {
                    uint bit = j < 24 ? (c >> (31 - Compression[j])) & 1 : (d >> (31 - (Compression[j] - 27))) & 1;
                    round = (round << 1) | bit;
                }
                keys[reverse ? 15 - i : i] = round;
            }
            return keys;
        }
        private static void DecodeBlock(byte[] data, int offset, ulong[] keys)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++) value = (value << 8) | data[offset + i / 4 * 4 + 3 - i % 4];
            value = Select(value, 64, Initial);
            uint left = (uint)(value >> 32), right = (uint)value;
            foreach (ulong key in keys)
            {
                ulong expanded = Select(right, 32, Expansion) ^ key; uint substituted = 0;
                for (int j = 0; j < 8; j++)
                {
                    int six = (int)((expanded >> (42 - 6 * j)) & 63);
                    int index = (six & 32) | ((six & 31) >> 1) | ((six & 1) << 4);
                    substituted = (substituted << 4) | (uint)Boxes[j][index];
                }
                uint next = left ^ (uint)Select(substituted, 32, Permutation);
                left = right; right = next;
            }
            value = Select(((ulong)right << 32) | left, 64, Final);
            for (int i = 7; i >= 0; i--) { data[offset + i / 4 * 4 + 3 - i % 4] = (byte)value; value >>= 8; }
        }
    }
}
