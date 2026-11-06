using System.Buffers.Binary;
using System.Text;

namespace Reorder.Services;

public enum StripResult
{
    Stripped,
    AlreadyClean,
    Unsupported,
}

/// <summary>
/// Losslessly removes EXIF / XMP / text metadata from JPEG, PNG and WebP files
/// without re-encoding pixel data. The EXIF orientation tag is preserved (as a
/// minimal EXIF block) so that rotated photos still display correctly.
/// </summary>
public static class MetadataStripper
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    private static readonly byte[] IccHeader = "ICC_PROFILE\0"u8.ToArray();

    public static bool IsSupported(string path) => GetKind(path) != FileKind.Other;

    public static StripResult Strip(string path)
    {
        var kind = GetKind(path);
        if (kind == FileKind.Other) return StripResult.Unsupported;

        byte[] data = File.ReadAllBytes(path);
        byte[]? result = kind switch
        {
            FileKind.Jpeg => StripJpeg(data),
            FileKind.Png => StripPng(data),
            FileKind.Webp => StripWebp(data),
            _ => null,
        };
        // Output is deterministic, so a file we already cleaned yields identical bytes.
        if (result is null || result.AsSpan().SequenceEqual(data)) return StripResult.AlreadyClean;

        var created = File.GetCreationTimeUtc(path);
        var modified = File.GetLastWriteTimeUtc(path);
        string temp = path + ".reorder.tmp";
        try
        {
            File.WriteAllBytes(temp, result);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* ignore */ }
            throw;
        }
        File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, modified);
        return StripResult.Stripped;
    }

    private enum FileKind { Other, Jpeg, Png, Webp }

    private static FileKind GetKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jfif" or ".jpe" => FileKind.Jpeg,
        ".png" => FileKind.Png,
        ".webp" => FileKind.Webp,
        _ => FileKind.Other,
    };

    // ---------------------------------------------------------------- JPEG

    private static byte[]? StripJpeg(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8)
            throw new InvalidDataException(Loc.Format("Error_InvalidFile", "JPEG"));

        var kept = new List<(int Start, int Length, byte Marker)>();
        ushort orientation = 1;
        bool removed = false;
        int scanStart = -1;
        int pos = 2;

        while (pos < d.Length)
        {
            if (d[pos] != 0xFF) throw new InvalidDataException(Loc.Format("Error_CorruptFile", "JPEG"));
            int start = pos;
            while (pos < d.Length && d[pos] == 0xFF) pos++; // fill bytes
            if (pos >= d.Length) break;
            byte marker = d[pos++];

            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                kept.Add((start, pos - start, marker));
                continue;
            }
            if (marker == 0xD9 || marker == 0xDA)
            {
                scanStart = start;
                break;
            }

            if (pos + 2 > d.Length) throw new InvalidDataException(Loc.Format("Error_CorruptFile", "JPEG"));
            int len = (d[pos] << 8) | d[pos + 1];
            if (len < 2 || pos + len > d.Length) throw new InvalidDataException(Loc.Format("Error_CorruptFile", "JPEG"));
            var payload = d.AsSpan(pos + 2, len - 2);
            int end = pos + len;

            if (KeepJpegSegment(marker, payload))
            {
                kept.Add((start, end - start, marker));
            }
            else
            {
                removed = true;
                if (marker == 0xE1 && payload.StartsWith(ExifHeader))
                    orientation = ReadTiffOrientation(payload[ExifHeader.Length..]) ?? orientation;
            }
            pos = end;
        }

        if (scanStart < 0) throw new InvalidDataException(Loc.Get("Error_JpegNoData"));

        // Drop anything appended after the primary image (e.g. MPF secondary images).
        int tailEnd = FindJpegEnd(d, scanStart);
        if (tailEnd < d.Length) removed = true;
        if (!removed) return null;

        using var ms = new MemoryStream(d.Length);
        ms.Write(d, 0, 2); // SOI
        int i = 0;
        if (kept.Count > 0 && kept[0].Marker == 0xE0) // keep JFIF APP0 first
        {
            ms.Write(d, kept[0].Start, kept[0].Length);
            i = 1;
        }
        if (orientation != 1)
        {
            byte[] tiff = BuildOrientationTiff(orientation);
            int segLen = 2 + ExifHeader.Length + tiff.Length;
            ms.WriteByte(0xFF);
            ms.WriteByte(0xE1);
            ms.WriteByte((byte)(segLen >> 8));
            ms.WriteByte((byte)segLen);
            ms.Write(ExifHeader);
            ms.Write(tiff);
        }
        for (; i < kept.Count; i++)
            ms.Write(d, kept[i].Start, kept[i].Length);
        ms.Write(d, scanStart, tailEnd - scanStart);
        return ms.ToArray();
    }

    private static bool KeepJpegSegment(byte marker, ReadOnlySpan<byte> payload) => marker switch
    {
        0xE0 => true,                              // JFIF
        0xE2 => payload.StartsWith(IccHeader),     // ICC profile only (drop MPF / FlashPix)
        0xEE => true,                              // Adobe (affects color transform)
        >= 0xE1 and <= 0xEF => false,              // EXIF, XMP, IPTC, vendor data...
        0xFE => false,                             // comment
        _ => true,                                 // DQT, DHT, SOF, DRI...
    };

    /// <summary>Returns the index just past the EOI marker of the primary image.</summary>
    private static int FindJpegEnd(byte[] d, int pos)
    {
        while (pos + 1 < d.Length)
        {
            if (d[pos] != 0xFF) { pos++; continue; } // entropy-coded data
            byte m = d[pos + 1];
            if (m == 0xFF) { pos++; continue; }
            if (m == 0x00 || (m >= 0xD0 && m <= 0xD7)) { pos += 2; continue; } // stuffing / RST
            if (m == 0xD9) return pos + 2;
            if (pos + 4 > d.Length) return d.Length;
            int len = (d[pos + 2] << 8) | d[pos + 3];
            pos += 2 + len;
        }
        return d.Length;
    }

    // ---------------------------------------------------------------- PNG

    private static byte[]? StripPng(byte[] d)
    {
        if (!d.AsSpan().StartsWith(PngSignature)) throw new InvalidDataException(Loc.Format("Error_InvalidFile", "PNG"));

        var kept = new List<(int Start, int Length, string Type)>();
        ushort orientation = 1;
        bool removed = false;
        int pos = PngSignature.Length;

        while (pos + 12 <= d.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos));
            if (len > (uint)(d.Length - pos - 12)) throw new InvalidDataException(Loc.Format("Error_CorruptFile", "PNG"));
            string type = Encoding.ASCII.GetString(d, pos + 4, 4);
            int total = 12 + (int)len;

            if (type is "eXIf" or "tEXt" or "zTXt" or "iTXt" or "tIME")
            {
                removed = true;
                if (type == "eXIf")
                    orientation = ReadTiffOrientation(d.AsSpan(pos + 8, (int)len)) ?? orientation;
            }
            else
            {
                kept.Add((pos, total, type));
            }
            pos += total;
            if (type == "IEND") break;
        }
        if (pos < d.Length) removed = true; // trailing garbage after IEND
        if (!removed) return null;

        using var ms = new MemoryStream(d.Length);
        ms.Write(PngSignature);
        bool exifWritten = orientation == 1;
        foreach (var chunk in kept)
        {
            if (!exifWritten && chunk.Type == "IDAT")
            {
                WritePngChunk(ms, "eXIf", BuildOrientationTiff(orientation));
                exifWritten = true;
            }
            ms.Write(d, chunk.Start, chunk.Length);
        }
        return ms.ToArray();
    }

    private static void WritePngChunk(Stream s, string type, byte[] data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)data.Length);
        s.Write(buf);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }

    // ---------------------------------------------------------------- WebP

    private static byte[]? StripWebp(byte[] d)
    {
        if (d.Length < 12 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || Encoding.ASCII.GetString(d, 8, 4) != "WEBP")
            throw new InvalidDataException(Loc.Format("Error_InvalidFile", "WebP"));

        long riffEnd = Math.Min(d.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)));
        var kept = new List<(int Start, int Length, string FourCC)>();
        ushort orientation = 1;
        bool removed = false;
        bool hasVp8x = false;
        int pos = 12;

        while (pos + 8 <= riffEnd)
        {
            string cc = Encoding.ASCII.GetString(d, pos, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos + 4));
            if (size > (uint)(d.Length - pos - 8)) throw new InvalidDataException(Loc.Format("Error_CorruptFile", "WebP"));
            int total = (int)Math.Min(8L + size + (size & 1), d.Length - pos);

            if (cc is "EXIF" or "XMP ")
            {
                removed = true;
                if (cc == "EXIF")
                {
                    var payload = d.AsSpan(pos + 8, (int)size);
                    if (payload.StartsWith(ExifHeader)) payload = payload[ExifHeader.Length..];
                    orientation = ReadTiffOrientation(payload) ?? orientation;
                }
            }
            else
            {
                if (cc == "VP8X") hasVp8x = true;
                kept.Add((pos, total, cc));
            }
            pos += total;
        }
        if (!removed) return null;

        bool addExif = hasVp8x && orientation != 1;
        using var ms = new MemoryStream(d.Length);
        ms.Write("RIFF"u8);
        ms.Write(new byte[4]); // size placeholder
        ms.Write("WEBP"u8);
        foreach (var chunk in kept)
        {
            if (chunk.FourCC == "VP8X" && chunk.Length > 8)
            {
                byte[] copy = d.AsSpan(chunk.Start, chunk.Length).ToArray();
                byte flags = (byte)(copy[8] & ~(0x08 | 0x04)); // clear EXIF + XMP flags
                if (addExif) flags |= 0x08;
                copy[8] = flags;
                ms.Write(copy);
            }
            else
            {
                ms.Write(d, chunk.Start, chunk.Length);
            }
        }
        if (addExif)
        {
            byte[] tiff = BuildOrientationTiff(orientation);
            Span<byte> header = stackalloc byte[8];
            "EXIF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)tiff.Length);
            ms.Write(header);
            ms.Write(tiff); // 26 bytes, even length, no padding needed
        }

        byte[] result = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        return result;
    }

    // ---------------------------------------------------------------- TIFF helpers

    private static ushort? ReadTiffOrientation(ReadOnlySpan<byte> t)
    {
        if (t.Length < 8) return null;
        bool le;
        if (t[0] == 'I' && t[1] == 'I') le = true;
        else if (t[0] == 'M' && t[1] == 'M') le = false;
        else return null;

        uint ifd = ReadU32(t, 4, le);
        if (ifd > (uint)t.Length - 2) return null;
        int count = ReadU16(t, (int)ifd, le);
        for (int i = 0; i < count; i++)
        {
            int e = (int)ifd + 2 + i * 12;
            if (e + 12 > t.Length) break;
            if (ReadU16(t, e, le) == 0x0112)
            {
                ushort value = ReadU16(t, e + 8, le);
                return value is >= 1 and <= 8 ? value : null;
            }
        }
        return null;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> s, int o, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(s[o..]) : BinaryPrimitives.ReadUInt16BigEndian(s[o..]);

    private static uint ReadU32(ReadOnlySpan<byte> s, int o, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(s[o..]) : BinaryPrimitives.ReadUInt32BigEndian(s[o..]);

    /// <summary>Builds a minimal little-endian TIFF block containing only the Orientation tag.</summary>
    private static byte[] BuildOrientationTiff(ushort orientation)
    {
        var b = new byte[26];
        b[0] = (byte)'I'; b[1] = (byte)'I'; b[2] = 42;   // header
        b[4] = 8;                                        // IFD0 offset
        b[8] = 1;                                        // entry count
        b[10] = 0x12; b[11] = 0x01;                      // tag 0x0112 Orientation
        b[12] = 3;                                       // type SHORT
        b[14] = 1;                                       // count
        b[18] = (byte)orientation; b[19] = (byte)(orientation >> 8);
        return b;                                        // next IFD offset = 0
    }

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
