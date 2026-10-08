// Cloud deck plane: fbm of smooth value noise in world XZ (integer lattice hash, no per-pixel random),
// slowly scrolling, soft alpha. Seen from below it is lit by the city: an orange/magenta glow, strongest
// over the canyon (a band along _GlowAxis through _GlowCenter). Seen from above it is a dim moonlit grey.
// Octaves fade out as they get smaller than a pixel, so distant cloud doesn't shimmer.
// Soft everywhere it could show a hard line: it thins out over _SoftDepth m where it meets scene geometry
// (a tower piercing the deck), when the camera is within _CameraFade m of its height (flying through
// it), and between _FadeStart and _FadeEnd from the camera (its far edge).
Shader "FPS/CloudDeck"
{
    Properties
    {
        _Scale ("Noise Scale (m per base cell)", Float) = 900
        _Coverage ("Coverage", Range(0, 1)) = 0.55
        _Softness ("Edge Softness", Range(0.01, 0.5)) = 0.2
        _Opacity ("Max Opacity", Range(0, 1)) = 0.85
        _Scroll ("Scroll (m/s, xz)", Vector) = (4, 0, 1.5, 0)
        _Seed ("Seed", Float) = 0
        _TopColor ("Top (moonlit)", Color) = (0.16, 0.16, 0.22, 1)
        [HDR] _UnderColor ("Underside Glow", Color) = (0.55, 0.22, 0.3, 1)
        [HDR] _CanyonGlowColor ("Canyon Glow", Color) = (1.0, 0.45, 0.25, 1)
        _GlowCenter ("Canyon Line (x, z)", Vector) = (0, 0, 0, 0)
        _GlowAxis ("Canyon Direction (x, z)", Vector) = (0, 1, 0, 0)
        _GlowWidth ("Canyon Glow Width (m)", Float) = 250
        _SoftDepth ("Soft Intersection Depth (m)", Float) = 40
        _CameraFade ("Camera Height Fade (m)", Float) = 30
        _FadeStart ("Distance Fade Start (m)", Float) = 1800
        _FadeEnd ("Distance Fade End (m)", Float) = 2400
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "CloudDeck"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "HashPCG.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Scale;
                float _Coverage;
                float _Softness;
                float _Opacity;
                float4 _Scroll;
                float _Seed;
                float4 _TopColor;
                float4 _UnderColor;
                float4 _CanyonGlowColor;
                float4 _GlowCenter;
                float4 _GlowAxis;
                float _GlowWidth;
                float _SoftDepth;
                float _CameraFade;
                float _FadeStart;
                float _FadeEnd;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float fogFactor : TEXCOORD1; };

            Varyings vert(Attributes v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float ValueNoise(float2 p, int octave)
            {
                int2 i = (int2)floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                int s = (int)_Seed * 7 + octave * 131;
                float a = U01(HashInt3(int3(i.x, i.y, s)).x);
                float b = U01(HashInt3(int3(i.x + 1, i.y, s)).x);
                float c = U01(HashInt3(int3(i.x, i.y + 1, s)).x);
                float d = U01(HashInt3(int3(i.x + 1, i.y + 1, s)).x);
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            // 5-octave fbm in [0, 1]. Each octave fades to its mean (0.5) once its cells shrink below
            // ~2 pixels, so distant cloud neither shimmers nor changes coverage.
            float Fbm(float2 p)
            {
                float2 fw = fwidth(p);
                float px = max(fw.x, fw.y);
                float sum = 0.0, norm = 0.0, amp = 0.5, freq = 1.0;
                [unroll] for (int o = 0; o < 5; o++)
                {
                    float keep = 1.0 - smoothstep(0.25, 0.5, px * freq);
                    sum += amp * lerp(0.5, ValueNoise(p * freq + o * 17.3, o), keep);
                    norm += amp;
                    amp *= 0.5;
                    freq *= 2.03;
                }
                return sum / norm;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float t = frac(_Time.y / 2000.0) * 2000.0;  // wrapped for precision
                float2 p = (i.positionWS.xz + _Scroll.xz * t) / _Scale;
                float n = Fbm(p);
                float alpha = smoothstep(1.0 - _Coverage - _Softness, 1.0 - _Coverage + _Softness, n) * _Opacity;

                // Below the plane: the city glow, strongest over the canyon. Above: dim moonlit top.
                bool fromBelow = _WorldSpaceCameraPos.y < i.positionWS.y;
                float2 rel = i.positionWS.xz - _GlowCenter.xy;
                float2 axis = normalize(_GlowAxis.xy);
                float across = abs(rel.x * axis.y - rel.y * axis.x);
                float canyon = exp(-across / max(_GlowWidth, 1.0));
                float3 under = _UnderColor.rgb + _CanyonGlowColor.rgb * canyon;
                float3 col = fromBelow ? under * (0.6 + 0.4 * n) : _TopColor.rgb * (0.7 + 0.3 * n) + under * 0.08;

                // Soft intersection: thin out where scene geometry is just behind the cloud surface.
                float2 screenUV = i.positionCS.xy / _ScaledScreenParams.xy;
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float cloudDepth = LinearEyeDepth(i.positionCS.z, _ZBufferParams);
                alpha *= saturate((sceneDepth - cloudDepth) / max(_SoftDepth, 0.01));

                // Never seen edge-on: fade as the camera reaches the deck's height.
                alpha *= saturate(abs(_WorldSpaceCameraPos.y - i.positionWS.y) / max(_CameraFade, 0.01));

                // Far edge: gone before the camera's far clip (2.5 km) can cut it.
                float dist = distance(_WorldSpaceCameraPos, i.positionWS);
                alpha *= 1.0 - smoothstep(_FadeStart, _FadeEnd, dist);

                // Fade into the distance fog (towards transparent, so it never shows as a hard sheet).
                alpha *= ComputeFogIntensity(i.fogFactor);
                col = MixFog(col, i.fogFactor);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
