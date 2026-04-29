namespace Pictomancy.DXDraw;
internal static class ShapeSharedShader
{
    // Implicit contract: each shape's cbuffer at b0 declares `float4x4 viewProj` and `float2 pixelToUv`.
    public const string Mixin = """
        Texture2D<float4> _sceneDepth : register(t0);
        SamplerState _occlusionSampler
        {
            Filter = MIN_MAG_MIP_POINT;
            AddressU = CLAMP;
            AddressV = CLAMP;
        };

        // fadeParams : x=OccludedAlpha, y=OcclusionTolerance (m), z=FadeStart (m), w=FadeStop (m).
        float4 applyShared(float4 color, float3 projPos, float4 fadeParams)
        {
            float2 uv = projPos.xy * pixelToUv;
            float sceneNdcZ = _sceneDepth.Sample(_occlusionSampler, uv).r;

            float near = viewProj._m32;
            float shapeWorldZ = near / max(projPos.z, 1e-6);
            float sceneWorldZ = near / max(sceneNdcZ, 1e-6);

            // "behind" = how many meters past scene geometry this shape pixel is. Positive only
            // when shape is occluded; clamped at 0 when in front.
            float behindMeters = max(shapeWorldZ - sceneWorldZ, 0.0);
            float occlusion = behindMeters <= fadeParams.y ? 1.0 : fadeParams.x;

            // Distance fade. FadeStop = +inf disables.
            float distanceFactor = 1.0;
            if (fadeParams.w < 1e10)
            {
                float range = max(fadeParams.w - fadeParams.z, 1e-4);
                distanceFactor = saturate((fadeParams.w - shapeWorldZ) / range);
            }

            color.a *= occlusion * distanceFactor;
            return color;
        }
        """;
}
