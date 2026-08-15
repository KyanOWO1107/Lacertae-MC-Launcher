using System.Buffers.Binary;
using System.IO.Compression;

namespace Lacertae.Infrastructure.Accounts.Avatar;

public sealed record PngImageInfo(int Width, int Height);

/// <summary>
/// Performs bounded structural and scanline validation for non-animated skin PNGs.
/// </summary>
public static class PngValidator
{
    private const int MaximumPngBytes = 1 * 1024 * 1024;
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool TryValidate(ReadOnlySpan<byte> png, out PngImageInfo? image)
    {
        image = null;
        if (png.Length < Signature.Length || png.Length > MaximumPngBytes ||
            !png[..Signature.Length].SequenceEqual(Signature))
        {
            return false;
        }

        using MemoryStream compressedIdat = new();
        int offset = Signature.Length;
        int width = 0;
        int height = 0;
        byte colorType = 0;
        bool seenHeader = false;
        bool seenIdat = false;
        bool idatClosed = false;

        try
        {
            while (offset < png.Length)
            {
                if (png.Length - offset < 12)
                {
                    return false;
                }

                uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
                if (declaredLength > int.MaxValue || declaredLength > png.Length - offset - 12)
                {
                    return false;
                }

                int dataLength = (int)declaredLength;
                ReadOnlySpan<byte> type = png.Slice(offset + 4, 4);
                ReadOnlySpan<byte> data = png.Slice(offset + 8, dataLength);
                uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset + 8 + dataLength, 4));
                if (!IsChunkType(type) || Crc32.Compute(png.Slice(offset + 4, 4 + dataLength)) != expectedCrc)
                {
                    return false;
                }

                if (!seenHeader && !type.SequenceEqual("IHDR"u8))
                {
                    return false;
                }

                if (type.SequenceEqual("IHDR"u8))
                {
                    if (seenHeader || seenIdat || dataLength != 13)
                    {
                        return false;
                    }

                    uint widthValue = BinaryPrimitives.ReadUInt32BigEndian(data);
                    uint heightValue = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    if (widthValue is < 8 or > 256 || heightValue is < 8 or > 256 ||
                        data[8] != 8 || data[9] is not (2 or 6) ||
                        data[10] != 0 || data[11] != 0 || data[12] != 0)
                    {
                        return false;
                    }

                    width = (int)widthValue;
                    height = (int)heightValue;
                    colorType = data[9];
                    seenHeader = true;
                }
                else if (type.SequenceEqual("IDAT"u8))
                {
                    if (!seenHeader || idatClosed)
                    {
                        return false;
                    }

                    compressedIdat.Write(data);
                    seenIdat = true;
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    if (!seenHeader || !seenIdat || dataLength != 0 || png.Length != offset + 12)
                    {
                        return false;
                    }

                    if (!ValidateScanlines(compressedIdat.ToArray(), width, height, colorType))
                    {
                        return false;
                    }

                    image = new PngImageInfo(width, height);
                    return true;
                }
                else if (type.SequenceEqual("PLTE"u8))
                {
                    if (!seenHeader || seenIdat || dataLength == 0 || dataLength > 768 || dataLength % 3 != 0)
                    {
                        return false;
                    }
                }
                else if (type.SequenceEqual("acTL"u8) ||
                         type.SequenceEqual("fcTL"u8) ||
                         type.SequenceEqual("fdAT"u8) ||
                         IsCriticalChunk(type))
                {
                    return false;
                }
                else if (seenIdat)
                {
                    idatClosed = true;
                }

                offset += dataLength + 12;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        return false;
    }

    private static bool ValidateScanlines(byte[] compressed, int width, int height, byte colorType)
    {
        if (compressed.Length == 0)
        {
            return false;
        }

        int bytesPerPixel = colorType == 2 ? 3 : 4;
        int rowBytes = checked(width * bytesPerPixel);
        byte[] row = new byte[rowBytes + 1];
        using MemoryStream compressedStream = new(compressed, writable: false);
        using ZLibStream zlib = new(compressedStream, CompressionMode.Decompress, leaveOpen: true);
        for (int rowIndex = 0; rowIndex < height; rowIndex++)
        {
            zlib.ReadExactly(row);
            if (row[0] > 4)
            {
                return false;
            }
        }

        if (zlib.ReadByte() != -1 || compressedStream.Position != compressedStream.Length)
        {
            return false;
        }

        return true;
    }

    private static bool IsChunkType(ReadOnlySpan<byte> type)
    {
        if (type.Length != 4)
        {
            return false;
        }

        foreach (byte value in type)
        {
            if (!((value >= (byte)'A' && value <= (byte)'Z') ||
                  (value >= (byte)'a' && value <= (byte)'z')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCriticalChunk(ReadOnlySpan<byte> type) => type[0] is >= (byte)'A' and <= (byte)'Z';

    private static class Crc32
    {
        public static uint Compute(ReadOnlySpan<byte> bytes)
        {
            uint value = 0xFFFFFFFF;
            foreach (byte current in bytes)
            {
                value ^= current;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value >> 1) ^ (0xEDB88320u & (uint)-(int)(value & 1));
                }
            }

            return ~value;
        }
    }
}
