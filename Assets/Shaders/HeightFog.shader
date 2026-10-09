// Full-screen height fog / smog (used by HeightFogFeature).
// Profile: full density up to the smog top, then a smooth falloff made of two exponentials
//   density(y) = _SmogDensity * ((1 - w) * exp(-(y - top) / _SmogFalloff) + w * exp(-(y - top) / _SmogFalloffLong))
// (w = _SmogLongWeight), integrated analytically along the view ray, so it thins gradually with height
// and there is no raymarch noise.
// top = _SmogTop + _SmogTopVariation * districtNoise(...), sampled near the CAMERA (at most 300 m along
// the ray), so neighbouring pixels always agree: smooth value noise on an integer lattice (cells
// _DistrictCell m). Sky pixels and anything farther than _SkyDistance count as _SkyDistance away, and
// both fade toward the zenith the same way, so the skyline has no seam and the sky no hard edge.
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
            float _SmogFalloffLong;
            float _SmogLongWeight;
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

            // Integral of exp(-(y - top) / falloff) along y(t) = y0 + dy * t for t in [t0, t1], all above the top:
            //   exp(-a) * f/dy * (1 - exp(-k)),  a = (y(t0) - top) / f,  k = dy (t1 - t0) / f.
            // Written without subtracting two nearly equal exponentials; for tiny |k| (nearly horizontal
            // rays) the series (t1 - t0)(1 - k/2) takes over, so there is no cancellation and no divide by ~0.
            float AboveIntegral(float y0, float dy, float t0, float t1, float top, float falloff)
            {
                float len = t1 - t0;
                if (len <= 0.0) return 0.0;
                float a = (y0 + dy * t0 - top) / falloff;
                float k = dy * len / falloff;
                float term = abs(k) < 1e-3 ? len * (1.0 - 0.5 * k) : falloff / dy * (1.0 - exp(-k));
                return exp(-a) * term;
            }

            // Optical depth along the ray (in units of _SmogDensity) for one falloff.
            // The ray crosses the smog top at most once: rising rays go below-then-above, falling rays
            // above-then-below (either part may be empty once tc is clamped to the ray).
            float OpticalDepth(float y0, float dy, float dist, float top, float falloff)
            {
                // Where the ray crosses the top (a horizontal ray never does: tc lands on 0 or dist).
                float dySafe = abs(dy) < 1e-6 ? (dy < 0.0 ? -1e-6 : 1e-6) : dy;
                float tc = clamp((top - y0) / dySafe, 0.0, dist);
                if (dySafe > 0.0) return tc + AboveIntegral(y0, dy, tc, dist, top, falloff);
                return AboveIntegral(y0, dy, 0.0, tc, top, falloff) + (dist - tc);
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
                // Sky and far geometry share one distance, so towers fade into the sky without a seam.
                dist = sky ? _SkyDistance : min(dist, _SkyDistance);
                posWS = camPos + dir * dist;

                // District top sampled near the camera: neighbouring pixels always agree.
                float2 sampleXZ = camPos.xz + dir.xz * min(dist, 300.0);
                float top = _SmogTop + _SmogTopVariation * ValueNoise(sampleXZ / _DistrictCell);
                float w = saturate(_SmogLongWeight);
                float od = _SmogDensity * ((1.0 - w) * OpticalDepth(camPos.y, dir.y, dist, top, _SmogFalloff)
                                         + w * OpticalDepth(camPos.y, dir.y, dist, top, _SmogFalloffLong));
                float fog = 1.0 - exp(-od);

                // Keep the zenith clear: fade toward straight up, for the sky and (blended in with distance)
                // for far geometry too, so a tower top meeting the sky gets the same fade as the sky behind it.
                float zenithFade = saturate(1.0 - dir.y * 4.0);
                fog *= lerp(1.0, zenithFade, sky ? 1.0 : saturate(dist / _SkyDistance));

                // Darker near the ground, lighter toward the smog top.
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
