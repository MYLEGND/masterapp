using System.Buffers.Binary;

namespace Infrastructure.Social;

/// <summary>
/// Reads an ISO BMFF movie-header duration from the opening bytes of a media
/// stream. iOS-prepared Hacs use fast-start MP4s, placing <c>moov</c> before
/// media data, so an over-limit video is rejected during the initial storage
/// write rather than reaching local FFmpeg. A full ffprobe verification remains
/// mandatory for every video because third-party MP4s can place <c>moov</c> at
/// the end of the file.
/// </summary>
internal sealed class Mp4HeaderDurationProbe
{
    internal const int MaximumHeaderBytes = 50 * 1024;

    private readonly byte[] _header = new byte[MaximumHeaderBytes];
    private int _length;

    internal double? DurationSeconds { get; private set; }

    internal void Inspect(ReadOnlySpan<byte> bytes)
    {
        if (DurationSeconds.HasValue || _length == _header.Length || bytes.IsEmpty)
            return;

        var copied = Math.Min(bytes.Length, _header.Length - _length);
        bytes[..copied].CopyTo(_header.AsSpan(_length));
        _length += copied;
        DurationSeconds = TryReadDurationSeconds(_header.AsSpan(0, _length));
    }

    internal static double? TryReadDurationSeconds(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        while (TryReadBox(bytes, offset, out var box))
        {
            if (box.Type == FourCc("moov"))
            {
                var availablePayload = bytes.Slice(box.PayloadOffset);
                return TryReadMovieHeaderDuration(
                    availablePayload,
                    box.DeclaredPayloadLength);
            }

            if (box.DeclaredSize > int.MaxValue ||
                box.DeclaredSize > (ulong)(bytes.Length - offset))
            {
                return null;
            }

            offset += (int)box.DeclaredSize;
        }

        return null;
    }

    private static double? TryReadMovieHeaderDuration(
        ReadOnlySpan<byte> payload,
        ulong declaredPayloadLength)
    {
        var availableLength = declaredPayloadLength > int.MaxValue
            ? payload.Length
            : Math.Min(payload.Length, (int)declaredPayloadLength);
        var offset = 0;

        while (offset < availableLength &&
               TryReadBox(payload[..availableLength], offset, out var box))
        {
            if (box.Type == FourCc("mvhd"))
            {
                var header = payload.Slice(box.PayloadOffset);
                return TryReadMvhdDuration(header);
            }

            if (box.DeclaredSize > int.MaxValue ||
                box.DeclaredSize > (ulong)(availableLength - offset))
            {
                return null;
            }

            offset += (int)box.DeclaredSize;
        }

        return null;
    }

    private static double? TryReadMvhdDuration(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            return null;

        var version = payload[0];
        return version switch
        {
            0 when payload.Length >= 20 => ToSeconds(
                BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(16, 4))),
            1 when payload.Length >= 32 => ToSeconds(
                BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(20, 4)),
                BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(24, 8))),
            _ => null
        };
    }

    private static double? ToSeconds(uint timeScale, ulong duration) =>
        timeScale == 0
            ? null
            : duration / (double)timeScale;

    private static bool TryReadBox(
        ReadOnlySpan<byte> bytes,
        int offset,
        out Mp4Box box)
    {
        box = default;
        if (offset < 0 || bytes.Length - offset < 8)
            return false;

        var declaredSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
        var type = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 4, 4));
        var headerLength = 8;
        ulong size = declaredSize;

        if (declaredSize == 1)
        {
            if (bytes.Length - offset < 16)
                return false;

            size = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(offset + 8, 8));
            headerLength = 16;
        }
        else if (declaredSize == 0)
        {
            // A size-to-end box cannot be safely followed by another box in a
            // bounded prefix. The full-file probe handles this uncommon case.
            return false;
        }

        if (size < (ulong)headerLength)
            return false;

        box = new Mp4Box(type, size, offset + headerLength, size - (ulong)headerLength);
        return true;
    }

    private static uint FourCc(string value) =>
        BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(value));

    private readonly record struct Mp4Box(
        uint Type,
        ulong DeclaredSize,
        int PayloadOffset,
        ulong DeclaredPayloadLength);
}
