// The gaze reticle: a disc, or a ring when _Inner > 0, with a dark outline, drawn over everything.
// Radii are fractions of the quad's half-width, set every frame by GazeReticle.
Shader "CardboardXR/Reticle"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 0.9)
        _OutlineColor ("Outline Color", Color) = (0, 0, 0, 0.5)
        _Inner ("Inner Radius", Range(0, 1)) = 0
        _Outer ("Outer Radius", Range(0, 1)) = 0.8
        _Outline ("Outline Width", Range(0, 1)) = 0.2
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Overlay"
        }

        Pass
        {
            Name "Reticle"
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
                half4 _Color;
                half4 _OutlineColor;
                float _Inner;
                float _Outer;
                float _Outline;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                // -1 to 1 across the quad.
                float2 offset : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.offset = input.uv * 2.0 - 1.0;
                return output;
            }

            // 1 between the radii and 0 outside, with edges one pixel soft.
            float Band(float radius, float inner, float outer, float pixel)
            {
                return smoothstep(inner - pixel, inner, radius) * (1.0 - smoothstep(outer - pixel, outer, radius));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float radius = length(input.offset);
                float pixel = max(fwidth(radius), 1e-5);
                float fill = Band(radius, _Inner, _Outer, pixel) * _Color.a;
                float edge = Band(radius, _Inner - _Outline, _Outer + _Outline, pixel) * _OutlineColor.a;
                // The fill composited over its outline.
                float alpha = fill + edge * (1.0 - fill);
                half3 color = (_Color.rgb * fill + _OutlineColor.rgb * edge * (1.0 - fill)) / max(alpha, 1e-5);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
