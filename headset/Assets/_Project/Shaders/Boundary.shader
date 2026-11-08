// Real surfaces ARCore or ARKit detected, marked like the ground after an odradek scan in Death
// Stranding: small X marks, spaced out on a grid, with the surface's edges faded (SurfaceMesh puts
// the fade in UV1.x). A scan reveals them in a wave spreading out from where it began, its crest
// brighter, and they fade away when it ends (RoomBoundary sets the globals). Between scans, with
// the warning on, surfaces within reach across the room show, like a headset's guardian, except at
// floor level. The surface under the gaze shows whole, in the highlight color, with a faint fill
// (PlacementSurface eases _Highlight, and _Appear as a plane is first found). The highlight is a soft
// spot _SpotRadius across around where the gaze meets the surface (_GazePoint), not the whole
// surface, which lit up the floor whenever it was looked at. The marks sit in
// session space, so they stay put on the surface as the plane grows. Drawn over everything, since
// it's a safety warning.
Shader "CardboardXR/Boundary"
{
    Properties
    {
        _Color ("Color", Color) = (0.55, 0.85, 1, 1)
        _Spacing ("Mark Spacing (m)", Float) = 0.2
        _MarkSize ("Mark Arm Length (m)", Float) = 0.03
        _MarkWidth ("Mark Half Width (m)", Float) = 0.004
        _Fill ("Fill Opacity", Range(0, 1)) = 0.025
        _HighlightFill ("Highlighted Fill Opacity", Range(0, 1)) = 0.16
        _Fade ("Warning Fade Distance (m)", Float) = 0.4
        _WaveWidth ("Scan Wave Width (m)", Float) = 0.35
        _HighlightColor ("Highlight Color", Color) = (1, 0.8, 0.3, 1)
        _Highlight ("Highlight", Range(0, 1)) = 0
        _GazePoint ("Gaze Point (world)", Vector) = (0, 0, 0, 0)
        _SpotRadius ("Highlight Spot Radius (m)", Float) = 0.25
        _Appear ("Appear", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            // Just before the transparent queue: over the scene's opaque objects, since it ignores
            // depth, but under UI panels, and under the hand's joints and pointer, which draw later.
            "Queue" = "Transparent-1"
        }

        Pass
        {
            Name "Boundary"
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
                float _Spacing;
                float _MarkSize;
                float _MarkWidth;
                float _Fill;
                float _HighlightFill;
                float _Fade;
                float _WaveWidth;
                half4 _HighlightColor;
                float _Highlight;
                float4 _GazePoint;
                float _SpotRadius;
                float _Appear;
            CBUFFER_END

            // Global, set by RoomBoundary: the head's world position, how far across the room the
            // warning reaches (0 for off), the floor's world height, how much the scan shows (0-1),
            // the scan wave (its world origin in xyz, how far it has spread in w), and world to
            // session space.
            float4 _BoundaryHead;
            float _BoundaryRadius;
            float _BoundaryFloor;
            float _BoundaryScan;
            float4 _BoundaryWave;
            float4x4 _BoundaryWorldToSession;

            // Surfaces less than this above the floor count as floor, in meters.
            static const float k_FloorBand = 0.1;

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

            // How much of an X mark covers this point: arms _MarkSize long and _MarkWidth thick at 45
            // degrees, centered in each _Spacing cell, antialiased over a pixel (pixel meters wide).
            float Mark(float2 position, float pixel)
            {
                float2 local = (frac(position / _Spacing) - 0.5) * _Spacing;
                float2 turned = float2(local.x + local.y, local.x - local.y) * 0.70710678;
                // Never thinner than a pixel, so far marks don't break up.
                float halfWidth = max(_MarkWidth, pixel * 0.75);
                float2 across = 1.0 - saturate((abs(turned) - halfWidth) / pixel);
                float2 along = 1.0 - saturate((abs(turned) - _MarkSize) / pixel);
                float mark = max(across.y * along.x, across.x * along.y);
                // Marks only a few pixels across shimmer: they fade out with distance.
                return mark * saturate((_MarkSize / pixel - 2.0) / 3.0);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 session = mul(_BoundaryWorldToSession, float4(input.positionWS, 1.0)).xyz;
                float pixel = max(max(fwidth(session.x), fwidth(session.z)), 1e-5);
                float mark = Mark(session.xz, pixel);

                // The scan wave: shown behind its front, brightest at the crest.
                float fromOrigin = distance(input.positionWS.xz, _BoundaryWave.xz);
                float revealed = saturate((_BoundaryWave.w - fromOrigin) / _WaveWidth + 1.0);
                float crest = saturate(1.0 - abs(_BoundaryWave.w - fromOrigin) / _WaveWidth);
                float scan = _BoundaryScan * revealed;

                float across = distance(input.positionWS.xz, _BoundaryHead.xz);
                float reach = saturate((_BoundaryRadius - across) / _Fade);
                float aboveFloor = saturate((input.positionWS.y - _BoundaryFloor - k_FloorBand) / 0.05);
                float warning = reach * aboveFloor;

                float spot = smoothstep(0.0, 1.0, saturate(1.0 - distance(input.positionWS, _GazePoint.xyz) / _SpotRadius));
                float highlight = _Highlight * spot;
                float shown = max(max(scan, warning), highlight) * _Appear * smoothstep(0.0, 1.0, input.edge);
                float glow = crest * _BoundaryScan;
                half3 color = lerp(_Color.rgb, _HighlightColor.rgb, highlight) + glow * 0.5;
                float fill = lerp(_Fill, _HighlightFill, highlight) + glow * 0.08;
                return half4(color, shown * saturate(mark * (1.0 + glow) + fill) * _Color.a);
            }
            ENDHLSL
        }
    }
}
