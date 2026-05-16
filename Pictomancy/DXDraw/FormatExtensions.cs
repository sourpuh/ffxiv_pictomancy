using Format = SharpDX.DXGI.Format;

namespace Pictomancy.DXDraw;

internal static class FormatExtensions
{
    public static Format ToUNorm(this Format format)
    {
        return format switch
        {
            Format.R8_Typeless => Format.R8_UNorm,
            Format.R8G8_Typeless => Format.R8G8_UNorm,
            Format.R8G8B8A8_Typeless => Format.R8G8B8A8_UNorm,
            Format.R16_Typeless => Format.R16_UNorm,
            Format.R16G16_Typeless => Format.R16G16_UNorm,
            Format.R16G16B16A16_Typeless => Format.R16G16B16A16_UNorm,
            Format.R10G10B10A2_Typeless => Format.R10G10B10A2_UNorm,
            Format.B8G8R8A8_Typeless => Format.B8G8R8A8_UNorm,
            Format.B8G8R8X8_Typeless => Format.B8G8R8X8_UNorm,
            Format.BC1_Typeless => Format.BC1_UNorm,
            Format.BC2_Typeless => Format.BC2_UNorm,
            Format.BC3_Typeless => Format.BC3_UNorm,
            Format.BC7_Typeless => Format.BC7_UNorm,
            _ => format,
        };
    }
}