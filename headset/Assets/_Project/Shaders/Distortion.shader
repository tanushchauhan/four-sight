// Draws a Cardboard distortion mesh: vertices are already normalized device coordinates for the
// whole screen, and UVs index the eye texture. Used by StereoRig's compositor camera.
Shader "CardboardXR/Distortion"
{
    Properties
    {
        _MainTex ("Eye Texture", 2D) = "black" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Overlay"
        }

        Pass
        {
            Name "Distortion"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float2 ndc = input.positionOS.xy;
                // URP renders some cameras into textures stored upside down; follow its projection.
                ndc.y *= _ProjectionParams.x;
                output.positionCS = float4(ndc, UNITY_NEAR_CLIP_VALUE, 1.0);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
            }
            ENDHLSL
        }
    }
}
