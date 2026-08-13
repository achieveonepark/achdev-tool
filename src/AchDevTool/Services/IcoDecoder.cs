namespace AchDevTool.Services;

/// <summary>Minimal .ico container reader — just enough to pull out the largest
/// PNG-encoded frame (modern favicons embed PNG, not classic BMP/DIB data).</summary>
public static class IcoDecoder
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[]? ExtractLargestPng(byte[] ico)
    {
        if (ico.Length < 6 || ico[0] != 0 || ico[1] != 0 || ico[2] != 1 || ico[3] != 0)
        {
            return null; // not an ICO container
        }

        var count = ico[4] | (ico[5] << 8);
        byte[]? best = null;
        var bestArea = -1;

        for (var i = 0; i < count; i++)
        {
            var entryOffset = 6 + (i * 16);
            if (entryOffset + 16 > ico.Length)
            {
                break;
            }

            int width = ico[entryOffset];
            int height = ico[entryOffset + 1];
            if (width == 0) width = 256;
            if (height == 0) height = 256;

            var bytesInRes = ReadUInt32(ico, entryOffset + 8);
            var imageOffset = ReadUInt32(ico, entryOffset + 12);

            if (imageOffset + bytesInRes > (uint)ico.Length)
            {
                continue;
            }

            var frame = ico.AsSpan((int)imageOffset, (int)bytesInRes);
            if (!frame.StartsWith(PngSignature))
            {
                continue; // legacy BMP/DIB frame, not supported here
            }

            var area = width * height;
            if (area > bestArea)
            {
                bestArea = area;
                best = frame.ToArray();
            }
        }

        return best;
    }

    private static uint ReadUInt32(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
}
