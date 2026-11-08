// A black veil over the whole view, for fading between scenes: drawn last, over everything,
// including the reticle and UI, at the opacity ScreenFade sets in _Alpha.
Shader "CardboardXR/Fade"
{
    Properties
    {
        _Alpha ("Opacity", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Overlay+100"
        }

        Pass
        {
            Name "Fade"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Alpha;
            CBUFFER_END

            float4 Vert(float3 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS);
            }

            half4 Frag() : SV_Target
            {
                return half4(0.0, 0.0, 0.0, _Alpha);
            }
            ENDHLSL
        }
    }
}
