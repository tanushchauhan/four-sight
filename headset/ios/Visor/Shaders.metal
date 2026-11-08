#include <metal_stdlib>
using namespace metal;

struct EyeUniforms {
    // Eye frustum projection terms (Matrix4x4.Frustum in LensDistortion.cs).
    float lensM00;
    float lensM11;
    float lensM02;
    float lensM12;
    // Camera image frustum center and size at unit distance (PassthroughController.cs).
    float frustumCenterX;
    float frustumCenterY;
    float frustumSizeX;
    float frustumSizeY;
    // Widened AR display transform mapping uncropped quad UV (0..1, bottom-left) to sensor UV (top-left).
    float wideM00;
    float wideM01;
    float wideM10;
    float wideM11;
    float wideM20;
    float wideM21;
};

struct EyeVarying {
    float4 position [[position]];
    float2 uv;
};

struct WarpIn {
    float2 ndc [[attribute(0)]];
    float2 uv [[attribute(1)]];
};

struct WarpVarying {
    float4 position [[position]];
    float2 uv;
};

vertex EyeVarying eyeVertex(uint vid [[vertex_id]]) {
    float2 corner = float2(float((vid << 1) & 2), float(vid & 2));
    EyeVarying out;
    out.position = float4(corner * 2.0 - 1.0, 0.0, 1.0);
    // UV origin at bottom-left, so uv (0, 0) corresponds to NDC (-1, -1).
    out.uv = corner * 0.5;
    return out;
}

fragment float4 eyeFragment(
    EyeVarying in [[stage_in]],
    constant EyeUniforms &eye [[buffer(0)]],
    texture2d<float> luma [[texture(0)]],
    texture2d<float> chroma [[texture(1)]]
) {
    constexpr sampler colorSampler(address::clamp_to_edge, filter::linear);

    // 1. Unproject eye texture UV through the Cardboard eye frustum to a ray (x, y) at unit distance.
    // Straight ahead through the lens optical axis is ray = (0, 0).
    float2 ndc = in.uv * 2.0 - 1.0;
    float2 ray = float2(
        (ndc.x + eye.lensM02) / eye.lensM00,
        (ndc.y + eye.lensM12) / eye.lensM11
    );

    // 2. Intersect ray with the passthrough camera quad (PassthroughController.cs):
    // Quad center is frustumCenter, size is frustumSize, local UV is 0..1 from bottom-left.
    float2 quadUV = (ray - float2(eye.frustumCenterX, eye.frustumCenterY)) / float2(eye.frustumSizeX, eye.frustumSizeY) + 0.5;

    // The camera sees a narrower angle than a Cardboard lens (~68x42 deg vs 80x80 deg),
    // so outside the camera quad stays dark border, matching PassthroughController.cs.
    if (quadUV.x < 0.0 || quadUV.x > 1.0 || quadUV.y < 0.0 || quadUV.y > 1.0) {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    // 3. Map uncropped quad UV to raw camera sensor texture UV via the widened display transform.
    float2 texUV = float2(
        quadUV.x * eye.wideM00 + quadUV.y * eye.wideM10 + eye.wideM20,
        quadUV.x * eye.wideM01 + quadUV.y * eye.wideM11 + eye.wideM21
    );

    if (texUV.x < 0.0 || texUV.x > 1.0 || texUV.y < 0.0 || texUV.y > 1.0) {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    // 4. Convert ARKit YCbCr (BT.601 full-range, matching ARKitBackground.shader) to sRGB.
    float y = luma.sample(colorSampler, texUV).r;
    float2 cbcr = chroma.sample(colorSampler, texUV).rg;
    float3 rgb = float3(
        y + 1.4020 * cbcr.y - 0.7010,
        y - 0.3441 * cbcr.x - 0.7141 * cbcr.y + 0.5291,
        y + 1.7720 * cbcr.x - 0.8860
    );
    return float4(saturate(rgb), 1.0);
}

vertex WarpVarying warpVertex(WarpIn in [[stage_in]]) {
    WarpVarying out;
    out.position = float4(in.ndc, 0.0, 1.0);
    out.uv = in.uv;
    return out;
}

fragment float4 warpFragment(
    WarpVarying in [[stage_in]],
    texture2d<float> eye [[texture(0)]]
) {
    constexpr sampler colorSampler(address::clamp_to_edge, filter::linear);
    // DistortionMesh UVs have (0, 0) at bottom-left; Metal render target texture has (0, 0) at top-left.
    return eye.sample(colorSampler, float2(in.uv.x, 1.0 - in.uv.y));
}
