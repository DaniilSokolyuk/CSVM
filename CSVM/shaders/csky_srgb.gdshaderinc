#ifndef CSKY_SRGB_INC
#define CSKY_SRGB_INC

// sRGB EOTF — Godot's exact texture-linearisation curve.
//
// The original's DX7 fixed-function pipeline multiplied texture x baked vertex colour
// (D3DTOP_MODULATE) in GAMMA (sRGB) space. We render in linear space, so a naive multiply comes
// out too bright and desaturated wherever the baked colour is < 1 — i.e. across the ~30% of the
// world's corners that carry painted shadow/AO. Linearising the vertex colour before the
// (already-linear) texture multiply reproduces the gamma-space product. See M2-polish item 6.
//
// Used by every fullbright pass — the world/deck/dome bias shader, the cloud billboards, the
// cylindrical facades, and Clutter's trees (which share the world's baked-lighting model).
// It was copy-pasted into four shaders before 2026-07-23; this file is the single source.

vec3 csky_srgb_to_linear(vec3 c) {
    vec3 higher = pow((c + vec3(0.055)) * (1.0 / 1.055), vec3(2.4));
    vec3 lower = c * (1.0 / 12.92);
    // per component: c <= 0.04045 -> linear toe, else the power curve
    return mix(higher, lower, step(c, vec3(0.04045)));
}

// The inverse curve: a linear value back to the byte the original's framebuffer held.
vec3 csky_linear_to_srgb(vec3 l) {
    vec3 higher = 1.055 * pow(l, vec3(1.0 / 2.4)) - vec3(0.055);
    vec3 lower = l * 12.92;
    return mix(higher, lower, step(l, vec3(0.0031308)));
}

#endif
