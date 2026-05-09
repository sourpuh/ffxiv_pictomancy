namespace Pictomancy.DXDraw;
internal static class ShapeSharedShader
{
    public const string Preamble = """
        static const float PI = 3.14159265359;

        // Approximation for infinity in reverse Z.
        static const float SKY_DEPTH_EPS = 1e-6;
        // Threshold for when character pixels are slightly non-black in sceneInfo.
        static const float CHARACTER_MASK_THRESHOLD = 0.05;
        // Disable fade when fadeStop is at or beyond this.
        static const float NO_FADE_SENTINEL = 1e10;
        // For projected shapes, fraction of the half-extent that fades to zero at the edges.
        static const float HEIGHT_FADE_BAND = 0.2;
        // Divide-by-zero guard for fade / height / half-extent denominators.
        static const float DIVIDE_EPS = 1e-4;
        // Fresnel rim shading parameters.
        static const float RIM_FALLOFF_POW = 4.0;
        static const float RIM_INTENSITY = 0.6;

        Texture2D<float4> _sceneDepth : register(t0);
        Texture2D<float4> _sceneInfo : register(t1);
        Texture2D<float4> _sceneNormal : register(t2);
        SamplerState _sceneSampler
        {
            Filter = MIN_MAG_MIP_POINT;
            AddressU = CLAMP;
            AddressV = CLAMP;
        };
    """;

    // Requires cbuffer fields: `viewProj`, `pixelToUv`.
    public const string Mixin = """
        float4 applyOcclusionFade(float4 color, float sceneNdcZ, float pixelNdcZ, float2 occlusionParams)
        {
            float near = viewProj._m32;
            float pixelWorldZ = near / max(pixelNdcZ, SKY_DEPTH_EPS);
            float sceneWorldZ = near / max(sceneNdcZ, SKY_DEPTH_EPS);
            float occludedAlpha = occlusionParams.x;
            float occlusionTolerance = occlusionParams.y;

            float behindMeters = max(pixelWorldZ - sceneWorldZ, 0.0);
            float occlusionAlpha = behindMeters <= occlusionTolerance ? 1.0 : occludedAlpha;

            color.a *= occlusionAlpha;
            return color;
        }

        float4 applyDistanceFade(float4 color, float pixelNdcZ, float2 fadeParams)
        {
            float near = viewProj._m32;
            float fadeStart = fadeParams.x;
            float fadeStop = fadeParams.y;
            if (fadeStop < NO_FADE_SENTINEL)
            {
                float pixelWorldZ = near / max(pixelNdcZ, SKY_DEPTH_EPS);
                float range = max(fadeStop - fadeStart, DIVIDE_EPS);
                color.a *= saturate((fadeStop - pixelWorldZ) / range);
            }
            return color;
        }

        float4 applyShared(float4 color, float3 projPos, float2 occlusionParams, float2 fadeParams)
        {
            float2 uv = projPos.xy * pixelToUv;
            float sceneNdcZ = _sceneDepth.Sample(_sceneSampler, uv).r;
            float pixelNdcZ = projPos.z;

            color = applyOcclusionFade(color, sceneNdcZ, pixelNdcZ, occlusionParams);
            color = applyDistanceFade(color, pixelNdcZ, fadeParams);

            return color;
        }
        """;

    // Requires cbuffer fields: `renderTargetSize`, `invViewProj`, `cameraPos`, plus Mixin's.
    public const string ProjectedMixin = """
        float sampleSceneNdcZ(float2 uv)
        {
            // Clip if the source pixel is character
            float3 sceneInfo = _sceneInfo.Sample(_sceneSampler, uv).rgb;
            clip(max(sceneInfo.r, max(sceneInfo.g, sceneInfo.b)) - CHARACTER_MASK_THRESHOLD);
            return _sceneDepth.Sample(_sceneSampler, uv).r;
        }

        float3 getWorldPos(float2 projPos, float sceneNdcZ)
        {
            float2 ndcXY;
            ndcXY.x = projPos.x / renderTargetSize.x * 2.0 - 1.0;
            ndcXY.y = 1.0 - projPos.y / renderTargetSize.y * 2.0;
            float4 worldH = mul(float4(ndcXY, sceneNdcZ, 1.0), invViewProj);
            return worldH.xyz / worldH.w;
        }

        float4 applyFresnelRim(float4 color, float2 uv, float3 world)
        {
            // Normal is packed [0,1]; decode to [-1,1].
            float3 N = normalize(_sceneNormal.Sample(_sceneSampler, uv).rgb * 2.0 - 1.0);
            float3 V = normalize(cameraPos - world);
            float rim = pow(1.0 - saturate(abs(dot(N, V))), RIM_FALLOFF_POW);
            color.rgb += rim * RIM_INTENSITY;
            return color;
        }
        """;
}
