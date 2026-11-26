using System.Runtime.InteropServices;

namespace AirSend.WinAudio;

enum SampleKind { Float32, Int16, Int24, Int32 }

static class PcmConvert
{
    /// <summary>Converts native interleaved frames to stereo float. Downmixes &gt;2 channels, duplicates mono.</summary>
    public static unsafe void ToStereo(IntPtr src, int frames, int channels, SampleKind kind, float[] dest)
    {
        byte* p = (byte*)src;
        int bytesPer = kind switch { SampleKind.Int16 => 2, SampleKind.Int24 => 3, _ => 4 };
        float* tmp = stackalloc float[Math.Min(channels, 8)];
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                float v;
                switch (kind)
                {
                    case SampleKind.Float32: v = *(float*)p; break;
                    case SampleKind.Int16: v = *(short*)p / 32768f; break;
                    case SampleKind.Int24: v = ((p[0] << 8 | p[1] << 16 | p[2] << 24) >> 8) / 8388608f; break;
                    default: v = *(int*)p / 2147483648f; break;
                }
                if (c < 8) tmp[c] = v;
                p += bytesPer;
            }
            float l, r;
            if (channels == 1) l = r = tmp[0];
            else if (channels == 2) { l = tmp[0]; r = tmp[1]; }
            else
            {
                // FL FR FC LFE BL BR (SL SR): standard downmix
                const float k = 0.7071f;
                float c = channels > 2 ? tmp[2] : 0;
                l = tmp[0] + k * c; r = tmp[1] + k * c;
                if (channels >= 6) { l += k * tmp[4]; r += k * tmp[5]; }
                if (channels >= 8) { l += k * tmp[6]; r += k * tmp[7]; }
                l *= 0.5f; r *= 0.5f;
            }
            dest[2 * f] = l; dest[2 * f + 1] = r;
        }
    }
}
