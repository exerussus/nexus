using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>Минимальный PNG-кодировщик (RGB 8 бит, фильтр None, zlib из BCL) — для снимков окон без System.Drawing.</summary>
    internal static class Png
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly uint[] CrcTable = BuildCrcTable();

        /// <summary>Пиксели BGRA построчно сверху вниз (как отдаёт GetDIBits с отрицательной высотой) → PNG.</summary>
        public static byte[] EncodeBgra(int width, int height, byte[] bgra)
        {
            if (width <= 0 || height <= 0 || bgra == null || bgra.Length < width * height * 4)
                throw new ArgumentException("неверный размер изображения");

            // строки: байт фильтра (0) + RGB; альфа отброшена — у снимков окон она обычно нулевая
            var stride = width * 3 + 1;
            var raw = new byte[stride * height];
            var o = 0;
            for (var y = 0; y < height; y++)
            {
                raw[o++] = 0;
                var i = y * width * 4;
                for (var x = 0; x < width; x++, i += 4)
                {
                    raw[o++] = bgra[i + 2];
                    raw[o++] = bgra[i + 1];
                    raw[o++] = bgra[i];
                }
            }

            byte[] compressed;
            using (var zs = new MemoryStream())
            {
                using (var z = new ZLibStream(zs, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
                compressed = zs.ToArray();
            }

            using var ms = new MemoryStream();
            ms.Write(Signature, 0, Signature.Length);
            var ihdr = new byte[13];
            WriteBe(ihdr, 0, (uint)width);
            WriteBe(ihdr, 4, (uint)height);
            ihdr[8] = 8;  // бит на канал
            ihdr[9] = 2;  // RGB
            ihdr[10] = 0; // deflate
            ihdr[11] = 0; // стандартные фильтры
            ihdr[12] = 0; // без чересстрочности
            Chunk(ms, "IHDR", ihdr);
            Chunk(ms, "IDAT", compressed);
            Chunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBe(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);
            var t = Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            var crc = 0xFFFFFFFFu;
            crc = Update(crc, t);
            crc = Update(crc, data);
            var c = new byte[4];
            WriteBe(c, 0, crc ^ 0xFFFFFFFFu);
            s.Write(c, 0, 4);
        }

        private static uint Update(uint crc, byte[] data)
        {
            foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            return table;
        }

        private static void WriteBe(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }
    }
}
