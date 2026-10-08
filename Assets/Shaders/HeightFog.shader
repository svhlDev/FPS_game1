// Full-screen height fog / smog (used by HeightFogFeature).
// density(y) = _SmogDensity                                   for y <= top
//            = _SmogDensity * exp(-(y - top) / _SmogFalloff)    for y >  top
// integrated analytically along the view ray from the camera to the depth-buffer point (sky: _SkyDistance).
// top = _SmogTop + _SmogTopVariation * districtNoise(endpoint.xz): smooth value noise on an integer
// lattice (cells _DistrictCell m), so whole districts have higher or lower smog with no per-pixel noise.
// Colour goes from _SmogLowColor near the ground to _SmogHighColor near the smog top.
Shader "Hidden/FPS/HeightFog"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "HeightFog"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "HashPCG.hlsl"

            float _SmogTop;
            float _SmogTopVariation;
            float _SmogFalloff;
            float _SmogDensity;
            float _DistrictCell;
            float _SkyDistance;
            float4 _SmogLowColor;
            float4 _SmogHighColor;

            // Smooth value noise in [0, 1) on an integer lattice.
            float ValueNoise(float2 p)
            {
                int2 i = (int2)floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                float a = U01(HashInt3(int3(i.x, i.y, 17)).x);
                float b = U01(HashInt3(int3(i.x + 1, i.y, 17)).x);
                float c = U01(HashInt3(int3(i.x, i.y + 1, 17)).x);
                float d = U01(HashInt3(int3(i.x + 1, i.y + 1, 17)).x);
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            // Integral of density along y(t) = y0 + dy * t for t in [t0, t1], all above the top.
            float AboveIntegral(float y0, float dy, float t0, float t1, float top)
            {
                if (t1 <= t0) return 0.0;
                float ya = y0 + dy * t0, yb = y0 + dy * t1;
                if (abs(dy) < 1e-3) // nearly horizontal: midpoint rule
                    return (t1 - t0) * exp(-(0.5 * (ya + yb) - top) / _SmogFalloff);
                return _SmogFalloff / dy * (exp(-(ya - top) / _SmogFalloff) - exp(-(yb - top) / _SmogFalloff));
            }

            // Optical depth along the ray (in units of _SmogDensity).
            // The ray crosses the smog top at most once: rising rays go below-then-above, falling rays
            // above-then-below (either part may be empty once tc is clamped to the ray).
            float OpticalDepth(float y0, float dy, float dist, float top)
            {
                if (abs(dy) < 1e-5)
                    return y0 <= top ? dist : AboveIntegral(y0, 0.0, 0.0, dist, top);
                float tc = clamp((top - y0) / dy, 0.0, dist);
                if (dy > 0.0) return tc + AboveIntegral(y0, dy, tc, dist, top);
                return AboveIntegral(y0, dy, 0.0, tc, top) + (dist - tc);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                float rawDepth = SampleSceneDepth(uv);
            #if UNITY_REVERSED_Z
                bool sky = rawDepth <= 1e-7;
            #else
                bool sky = rawDepth >= 1.0 - 1e-7;
            #endif
                float3 camPos = _WorldSpaceCameraPos;
                float3 posWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 ray = posWS - camPos;
                float dist = length(ray);
                float3 dir = ray / max(dist, 1e-4);
                if (sky)
                {
                    dist = _SkyDistance;
                    posWS = camPos + dir * dist;
                }

                float top = _SmogTop + _SmogTopVariation * ValueNoise(posWS.xz / _DistrictCell);
                float od = _SmogDensity * OpticalDepth(camPos.y, dir.y, dist, top);
                float fog = 1.0 - exp(-od);

                // Darker near the ground, warmer and brighter toward the smog top.
                // Mean height of the ray inside the smog layer, relative to its top.
                float h = saturate(0.5 * (clamp(camPos.y, 0.0, top) + clamp(posWS.y, 0.0, top)) / max(top, 1.0));
                float3 smog = lerp(_SmogLowColor.rgb, _SmogHighColor.rgb, h);
                col.rgb = lerp(col.rgb, smog, fog);
                return col;
            }
            ENDHLSL
        }
    }
}
