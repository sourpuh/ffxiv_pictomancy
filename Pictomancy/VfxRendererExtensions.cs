using Pictomancy.DXDraw;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Pictomancy;
public static class VfxRendererExtensions
{
    const float DefaultHeight = 7;

    const string OmenCircle = "general_1bf";
    const string OmenLine = "general02f";
    const string OmenRectangle = "general_x02f";

    const string OmenFan15 = "gl_fan015_0x";
    const string OmenFan20 = "gl_fan020_0f";
    const string OmenFan30 = "gl_fan030_1bf";
    const string OmenFan45 = "gl_fan045_1bf";
    const string OmenFan60 = "gl_fan060_1bf";
    const string OmenFan80 = "gl_fan80_o0g";
    const string OmenFan90 = "gl_fan090_1bf";
    //const string OmenFan100 = "er_gl_fan100_o0v"; // Too dark
    const string OmenFan120 = "gl_fan120_1bf";
    const string OmenFan130 = "gl_fan130_0x";
    const string OmenFan135 = "gl_fan135_c0g";
    //const string OmenFan145 = "m0501_fan145_d1"; // Too dark and also scaled weird
    const string OmenFan150 = "gl_fan150_1bf";
    const string OmenFan180 = "gl_fan180_1bf";
    const string OmenFan210 = "gl_fan210_1bf";
    const string OmenFan225 = "gl_fan225_c0g";
    const string OmenFan270 = "gl_fan270_0100af";

    const string OmenDonut0_7 = "gl_sircle_5003bf";
    const string OmenDonut0_9 = "gl_sircle_7006x";
    const string OmenDonut1_11 = "gl_sircle_1034bf";
    const string OmenDonut1_5 = "gl_circle_5007_x1";
    const string OmenDonut1_66 = "gl_sircle_6010bf";
    const string OmenDonut1_8 = "gl_sircle_1703x";
    const string OmenDonut2 = "gl_sircle_2004bv";
    const string OmenDonut2_15 = "gl_sircle_7015k1";
    const string OmenDonut2_33 = "gl_sircle_3007bx";
    const string OmenDonut2_5 = "gl_sircle_2005bf";
    const string OmenDonut2_62 = "gl_sircle_1905bf";
    const string OmenDonut2_66 = "gl_sircle_3008bf";
    const string OmenDonut2_8 = "gl_sircle_1805r1";
    const string OmenDonut3 = "gl_sircle_4012c";
    const string OmenDonut3_11 = "x6r8_b4_donut13m_4_01k1";
    const string OmenDonut3_2 = "gl_sircle_2508_o0t1";
    const string OmenDonut3_33 = "x6r8_b4_donut13m_4_01k1";
    const string OmenDonut3_7 = "gl_sircle_1907y0x";
    const string OmenDonut4 = "gl_sircle_2008bi";
    const string OmenDonut4_66 = "gl_sircle_3014bf";
    const string OmenDonut5 = "gl_sircle_2010bf";
    const string OmenDonut5_5 = "gl_sircle_2011v";
    const string OmenDonut6 = "gl_sircle_5030c"; // makes noise; replace if another 6 is found
    const string OmenDonut6_33 = "gl_sircle_1610_o0v";
    const string OmenDonut6_66 = "gl_sircle_1510bx";
    const string OmenDonut7_1 = "gl_sircle_1710_o0p";
    const string OmenDonut7_33 = "gl_sircle_2316_o0p";
    const string OmenDonut7_5 = "gl_sircle_2015bx";
    const string OmenDonut8_2 = "gl_sircle_1109w";
    const string OmenDonut8_9 = "gl_sircle_1715w";
    const string OmenDonut9 = "gl_sircle_2018w";

    static readonly List<(string, float)> OmenDonutHoleSize = [
        (OmenDonut0_7, 0.07f),
        (OmenDonut0_9, 0.09f),
        (OmenDonut1_11, 0.111f),
        (OmenDonut1_5, 0.15f),
        (OmenDonut1_66, 0.166f),
        (OmenDonut1_8, 0.18f),
        (OmenDonut2, 0.2f),
        (OmenDonut2_15, 0.215f),
        (OmenDonut2_33, 0.233f),
        (OmenDonut2_5, 0.25f),
        (OmenDonut2_62, 0.262f),
        (OmenDonut2_66, 0.266f),
        (OmenDonut2_8, 0.28f),
        (OmenDonut3, 0.3f),
        (OmenDonut3_11, 0.311f),
        (OmenDonut3_2, 0.32f),
        (OmenDonut3_33, 0.333f),
        (OmenDonut3_7, 0.37f),
        (OmenDonut4, 0.4f),
        (OmenDonut4_66, 0.466f),
        (OmenDonut5, 0.5f),
        (OmenDonut5_5, 0.55f),
        (OmenDonut6, 0.6f),
        (OmenDonut6_33, 0.633f),
        (OmenDonut6_66, 0.666f),
        (OmenDonut7_1, 0.71f),
        (OmenDonut7_33, 0.733f),
        (OmenDonut7_5, 0.75f),
        (OmenDonut8_2, 0.82f),
        (OmenDonut8_9, 0.89f),
        (OmenDonut9, 0.9f),
     ];

    private static string? GetOmenConeForAngle(int angleWidth)
    {
        return angleWidth switch
        {
            15 => OmenFan15,
            20 => OmenFan20,
            30 => OmenFan30,
            45 => OmenFan45,
            60 => OmenFan60,
            80 => OmenFan80,
            90 => OmenFan90,
            // 100 => OmenFan100,
            120 => OmenFan120,
            130 => OmenFan130,
            135 => OmenFan135,
            // 145 => OmenFan145,
            150 => OmenFan150,
            180 => OmenFan180,
            210 => OmenFan210,
            225 => OmenFan225,
            270 => OmenFan270,
            360 => OmenCircle,
            _ => null,
        };
    }

    private static string? GetDonutOmen(float innerRadius, float outerRadius)
    {
        var desiredRatio = innerRadius / outerRadius;
        var x = OmenDonutHoleSize.Where(x =>
        {
            var comparison = desiredRatio - x.Item2;

            return comparison >= 0 && comparison < 0.01f;
        }).FirstOrDefault();
        return x.Item1;
    }

    private static (string, float) GetDonutHoleOmen(float innerRadius)
    {
        var (omen, actualInnerRadius) = OmenDonutHoleSize[0];
        return (omen, innerRadius / actualInnerRadius);
    }

    public static void AddLine(this VfxRenderer r, string id, Vector3 start, Vector3 stop, float halfWidth, Vector4? color = null)
    {
        float rotation = MathF.Atan2(stop.X - start.X, stop.Z - start.Z);
        float length = Vector2.Distance(new Vector2(stop.X, stop.Z), new Vector2(start.X, start.Z));
        AddLine(r, id, start, length, halfWidth, rotation, color);
    }

    public static void AddLine(this VfxRenderer r, string id, Vector3 start, float length, float halfWidth, float rotation, Vector4? color = null)
    {
        r.AddOmen(id, OmenLine, start, new(halfWidth, DefaultHeight, length), rotation, color);
    }

    public static void AddRectangle(this VfxRenderer r, string id, Vector3 origin, float halfWidth, float halfLength, float rotation = 0, Vector4? color = null)
    {
        r.AddOmen(id, OmenRectangle, origin, new(halfWidth, DefaultHeight, halfLength), rotation, color);
    }

    public static void AddCircle(this VfxRenderer r, string id, Vector3 origin, float radius, Vector4? color = null)
    {
        r.AddOmen(id, OmenCircle, origin, new(radius, DefaultHeight, radius), 0, color);
    }

    public static bool AddCone(this VfxRenderer r, string id, Vector3 origin, float radius, float rotation, int angleWidth, Vector4? color = null)
    {
        var omen = GetOmenConeForAngle(angleWidth);
        if (omen != null)
            r.AddOmen(id, omen, origin, new(radius, DefaultHeight, radius), rotation, color);
        return omen != null;
    }

    // Add a donut with a specified inner radius. No outer radius.
    public static void AddDonutHole(this VfxRenderer r, string id, Vector3 origin, float innerRadius, Vector4? color = null)
    {
        var (omen, scale) = GetDonutHoleOmen(innerRadius);
        r.AddOmen(id, omen, origin, new(scale, DefaultHeight, scale), 0, color);
    }

    public static bool AddDonut(this VfxRenderer r, string id, Vector3 origin, float innerRadius, float outerRadius, Vector4? color = null)
    {
        var omen = GetDonutOmen(innerRadius, outerRadius);
        if (omen != null)
            r.AddOmen(id, omen, origin, new(outerRadius, DefaultHeight, outerRadius), 0, color);
        return omen != null;
    }

    public static bool AddFan(this VfxRenderer r, string id, Vector3 origin, float innerRadius, float outerRadius, float minAngle, float maxAngle, Vector4? color = null)
    {
        bool isCircle = maxAngle - minAngle >= 2 * MathF.PI - 0.000001;
        if (innerRadius <= 0)
        {
            if (isCircle)
            {
                r.AddCircle(id, origin, outerRadius, color);
                return true;
            }
            else
            {
                float angle = maxAngle - minAngle;
                float rotation = minAngle + angle / 2;
                return r.AddCone(id, origin, outerRadius, -rotation, (int)MathF.Round(angle / (MathF.PI * 2) * 360, 0), color);
            }
        }
        else
        {
            if (isCircle)
            {
                if (innerRadius > 1 && outerRadius > 50)
                {
                    r.AddDonutHole(id, origin, innerRadius, color);
                    return true;
                }
                else
                    return r.AddDonut(id, origin, innerRadius, outerRadius, color);
            }
        }
        return false;
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
