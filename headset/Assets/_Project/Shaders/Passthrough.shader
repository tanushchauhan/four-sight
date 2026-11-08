// The passthrough screen: the camera image on a quad in front of the AR camera. It's the background,
// so it draws first, ignores depth and writes none, and everything virtual draws over it wherever
// it is. The render state is fixed here rather than in material properties, which URP resets from
// the surface type. It has only a forward pass: a depth-only pass would put the quad in URP's depth
// prepass and cut through virtual things behind it.
Shader "CardboardXR/Passthrough"
{
    Properties
    {
        [MainTexture] _BaseMap ("Camera Image", 2D) = "black" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Background"
        }

        Pass
        {
            Name "Passthrough"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
            CBUFFER_END

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
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return half4(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
