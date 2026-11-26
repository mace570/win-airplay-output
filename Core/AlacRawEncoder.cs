namespace AirSend.Core;

/// <summary>
/// Wraps 16-bit stereo PCM in an "uncompressed" (escape) ALAC frame. Lossless, ~1.4 Mbit/s,
/// and accepted by every AirPlay receiver that understands AppleLossless.
/// </summary>
public static class AlacRawEncoder
{
    public const int FramesPerPacket = 352;

    /// <param name="pcm">interleaved L,R 16-bit samples (frames*2 values)</param>
    public static int Encode(ReadOnlySpan<short> pcm, int frames, Span<byte> output)
    {
        var w = new BitWriter(output);
        bool partial = frames != FramesPerPacket;
        w.Write(1, 3);   // element type: ID_CPE (channel pair)
        w.Write(0, 4);   // element instance tag
        w.Write(0, 12);  // unused header bits
        w.Write(partial ? 1u : 0u, 1); // has explicit sample count
        w.Write(0, 2);   // bytes shifted
        w.Write(1, 1);   // escape flag = uncompressed
        if (partial) w.Write((uint)frames, 32);
        for (int i = 0; i < frames * 2; i++)
            w.Write((ushort)pcm[i], 16);
        w.Write(7, 3);   // ID_END
        return w.BytesUsed;
    }

    public static int MaxSize(int frames) => frames * 4 + 16;

    ref struct BitWriter
    {
        readonly Span<byte> _buf;
        int _bitPos;
        public BitWriter(Span<byte> buf) { _buf = buf; _bitPos = 0; _buf.Clear(); }
        public int BytesUsed => (_bitPos + 7) / 8;

        public void Write(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                if (((value >> i) & 1) != 0)
                    _buf[_bitPos >> 3] |= (byte)(0x80 >> (_bitPos & 7));
                _bitPos++;
            }
        }
    }
}
