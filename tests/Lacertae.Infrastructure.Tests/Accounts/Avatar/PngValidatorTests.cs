using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Lacertae.Infrastructure.Accounts.Avatar;

namespace Lacertae.Infrastructure.Tests.Accounts.Avatar;

public sealed class PngValidatorTests
{
    [Theory]
    [InlineData(64, 64)]
    [InlineData(64, 32)]
    public void ValidatesSupportedMinecraftSkinDimensions(int width, int height)
    {
        byte[] png = PngFixtureBuilder.Create(width, height);

        bool valid = PngValidator.TryValidate(png, out PngImageInfo? image);

        Assert.True(valid);
        Assert.Equal(new PngImageInfo(width, height), image);
    }

    [Theory]
    [InlineData(7, 64)]
    [InlineData(257, 64)]
    [InlineData(64, 0)]
    public void RejectsOutOfBoundsDimensions(int width, int height)
    {
        bool valid = PngValidator.TryValidate(
            PngFixtureBuilder.Create(width, height),
            out _);

        Assert.False(valid);
    }

    [Fact]
    public void RejectsInvalidSignatureFilterAndCrc()
    {
        byte[] signature = PngFixtureBuilder.Create(64, 64);
        signature[0] ^= 0xFF;
        Assert.False(PngValidator.TryValidate(signature, out _));

        byte[] filter = PngFixtureBuilder.Create(64, 64, filterByte: 5);
        Assert.False(PngValidator.TryValidate(filter, out _));

        byte[] crc = PngFixtureBuilder.Create(64, 64, corruptCrc: true);
        Assert.False(PngValidator.TryValidate(crc, out _));
    }

    [Fact]
    public void RejectsAnimatedAndUnknownCriticalChunks()
    {
        Assert.False(PngValidator.TryValidate(
            PngFixtureBuilder.Create(64, 64, additionalChunks: [PngFixtureBuilder.Chunk("acTL", new byte[8])]),
            out _));
        Assert.False(PngValidator.TryValidate(
            PngFixtureBuilder.Create(64, 64, additionalChunks: [PngFixtureBuilder.Chunk("ABCD", [])]),
            out _));
    }

    [Fact]
    public void RejectsInvalidFilterStreamAndUnsupportedColorMode()
    {
        Assert.False(PngValidator.TryValidate(
            PngFixtureBuilder.Create(64, 64, extraDecompressedByte: true),
            out _));
        Assert.False(PngValidator.TryValidate(
            PngFixtureBuilder.Create(64, 64, colorType: 3),
            out _));
        Assert.False(PngValidator.TryValidate(
            PngFixtureBuilder.Create(64, 64, interlaceMethod: 1),
            out _));
    }
}

internal static class PngFixtureBuilder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Create(
        int width,
        int height,
        byte filterByte = 0,
        byte colorType = 6,
        byte interlaceMethod = 0,
        bool corruptCrc = false,
        bool extraDecompressedByte = false,
        IReadOnlyList<byte[]>? additionalChunks = null)
    {
        int bytesPerPixel = colorType == 2 ? 3 : 4;
        int rowBytes = checked(width * bytesPerPixel);
        byte[] raw = new byte[checked((rowBytes + 1) * height)];
        for (int row = 0; row < height; row++)
        {
            raw[row * (rowBytes + 1)] = filterByte;
        }

        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
            if (extraDecompressedByte)
            {
                zlib.WriteByte(42);
            }
        }

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), checked((uint)height));
        ihdr[8] = 8;
        ihdr[9] = colorType;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = interlaceMethod;

        using MemoryStream png = new();
        png.Write(Signature);
        png.Write(Chunk("IHDR", ihdr, corruptCrc));
        if (additionalChunks is not null)
        {
            foreach (byte[] chunk in additionalChunks)
            {
                png.Write(chunk);
            }
        }

        byte[] idat = Chunk("IDAT", compressed.ToArray(), corruptCrc);
        png.Write(idat);
        png.Write(Chunk("IEND", [], corruptCrc));
        return png.ToArray();
    }

    public static byte[] Chunk(string type, byte[] data, bool corruptCrc = false)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        byte[] chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), checked((uint)data.Length));
        typeBytes.CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        uint crc = Crc32.Compute(chunk.AsSpan(4, 4 + data.Length));
        if (corruptCrc)
        {
            crc ^= 0xFFFFFFFF;
        }

        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length, 4), crc);
        return chunk;
    }

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
