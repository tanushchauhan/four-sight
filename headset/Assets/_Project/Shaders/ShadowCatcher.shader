// Shadows of virtual objects on real surfaces, as they'd fall on the camera image in passthrough:
// a detected plane draws nothing but darkens what's behind it (the camera image) where the main
// light's shadow map says it's in shadow. Multiplies, so it never brightens, and its edges fade
// with the surface's (SurfaceMesh's UV1.x). _ShadowCatcherStrength, a global PassthroughLighting
// sets, is how dark a full shadow gets; 0 (out of passthrough) leaves the image untouched. Drawn
// before the surface marks (CardboardXR/Boundary), with depth tested, so objects in front cover it.
// Real surfaces have no depth, so a floor shadow under a table would show through the tabletop:
// surfaces draw highest first (SurfaceMesh.SortingOrder) and each claims its pixels in a stencil
// bit, so a lower surface's shadow only shows where no higher surface is in the way.
// The shadow is softened here rather than by URP's soft shadows, whose blur is a shadow-map texel
// or two (a few millimeters at this range) and looked hard on the phones: it's sampled at
// _Softness meters around each point on the surface, in a disk of k_Taps, and averaged.
Shader "CardboardXR/ShadowCatcher"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent-2"
        }

        Pass
        {
            Name "ShadowCatcher"
            Tags { "LightMode" = "UniversalForward" }

            Blend DstColor Zero
            Stencil
            {
                Ref 64
                ReadMask 64
                WriteMask 64
                Comp NotEqual
                Pass Replace
            }
            Cull Off
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"

            float _ShadowCatcherStrength;

            // The penumbra's radius on the surface, in meters.
            static const float _Softness = 0.025;
            // A Poisson disk: points spread evenly over the unit disk.
            static const int k_Taps = 8;
            static const float2 k_Disk[k_Taps] =
            {
                float2(-0.613, 0.617), float2(0.170, -0.040), float2(-0.299, 0.791), float2(0.645, 0.493),
                float2(-0.651, -0.717), float2(0.421, 0.027), float2(-0.818, -0.264), float2(-0.007, -0.946),
            };

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 fade : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float edge : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.edge = input.fade.x;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Turned per pixel, so the taps' pattern doesn't show as banding.
                float angle = InterleavedGradientNoise(input.positionCS.xy, 0) * TWO_PI;
                float2x2 turn = float2x2(cos(angle), -sin(angle), sin(angle), cos(angle));
                half lit = MainLightRealtimeShadow(TransformWorldToShadowCoord(input.positionWS));
                [unroll]
                for (int i = 0; i < k_Taps; i++)
                {
                    float2 offset = mul(turn, k_Disk[i]) * _Softness;
                    float3 position = input.positionWS + float3(offset.x, 0.0, offset.y);
                    lit += MainLightRealtimeShadow(TransformWorldToShadowCoord(position));
                }
                lit /= k_Taps + 1;
                // Fade out toward the end of the shadow distance rather than cutting off.
                half fade = GetMainLightShadowFade(input.positionWS);
                lit = lerp(lit, 1.0, fade);
                half shade = (1.0 - lit) * _ShadowCatcherStrength * smoothstep(0.0, 1.0, input.edge);
                return half4((1.0 - shade).xxx, 1.0);
            }
            ENDHLSL
        }
    }
}
