// An invisible surface that hides what's behind it: it writes depth but no color, and draws before
// the scene's opaque objects, so anything behind it fails the depth test and whatever was drawn
// first (the passthrough camera image) shows through instead. The portal uses it to show its world
// only through the doorway from outside, and the real room only through the doorway from inside.
// Double-sided, so a box of it hides its inside from any angle.
Shader "CardboardXR/DepthMask"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            // After the passthrough image (Background), before the scene (Geometry).
            "Queue" = "Geometry-10"
        }

        Pass
        {
            Name "DepthMask"
            Tags { "LightMode" = "UniversalForward" }

            ColorMask 0
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            float4 Vert(Attributes input) : SV_POSITION
            {
                return TransformObjectToHClip(input.positionOS.xyz);
            }

            half4 Frag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
