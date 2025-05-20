using ImGuiNET;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Pictomancy.DXDraw;
internal static class ColorExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ToUint(this Vector4 color)
    {
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 ToVector4(this uint color)
    {
        return ImGui.ColorConvertU32ToFloat4(color);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 AlphaDXToVFX(this uint color)
    {
        var colorVec = color.ToVector4();
        if (colorVec.W <= 0.31372549019f)
            colorVec.W *= 3.1875f;
        else
        {
            var remainder = colorVec.W - 0.31372549019f;
            colorVec.W = 1 + remainder * 1.96f;
        }
        return colorVec;
    }
}
