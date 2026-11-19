// Rimlight edge glow.
// Draws a rounded-rect band of light hugging the screen edge, with a soft inward falloff.
// Only four thin quads along the edges are rasterised — the screen interior is never touched.
// Output is premultiplied alpha for a DirectComposition swap chain.

cbuffer Params : register(b0)
{
    float2 ViewSize;     // swap-chain size in px
    float2 RectOrigin;   // light rect (px, inside the window) — differs from the window when avoiding the taskbar
    float2 RectSize;
    float  Thickness;    // solid band width, px
    float  Glow;         // inward falloff distance, px
    float  Radius;       // corner radius, px
    float  Intensity;    // master opacity 0..1 (beat boost already folded in)
    float  Rotation;     // gradient offset around the loop, 0..1
    float  BandExtent;   // how far in from the edge we rasterise, px
    float4 Color0;       // up to four colours, OKLab (L, a, b, -), best first
    float4 Color1;
    float4 Color2;
    float4 Color3;
    float4 ColorWeights; // share of the loop for each colour (0 = unused); fades smoothly
    float  Boost;        // 0..1 extra whitening of the rim on beat peaks
    float  Floor;        // brightness of the border outside the lit arcs (1 = arcs off)
    float  ArcSoft;      // max half-width of an arc's soft ends, loop units
    float  _pad0;
    float4 ArcPosA;      // up to 8 lit arcs from the flow engine: centre (0..1 around the loop)
    float4 ArcPosB;
    float4 ArcWidA;      //   width (fraction of the loop)
    float4 ArcWidB;
    float4 ArcGainA;     //   strength 0..1
    float4 ArcGainB;
};

struct VSOut
{
    float4 pos : SV_Position;
    float2 px  : TEXCOORD0;
};

VSOut VSMain(uint vid : SV_VertexID)
{
    uint quad = vid / 6;
    uint c = vid % 6;
    float2 uv = float2((c == 1 || c == 4 || c == 5) ? 1.0 : 0.0,
                       (c == 2 || c == 3 || c == 5) ? 1.0 : 0.0);
    float D = BandExtent;
    float2 o = RectOrigin;
    float2 s = RectSize;
    float4 r;
    if (quad == 0)      r = float4(o.x,           o.y,           o.x + s.x, o.y + D);         // top
    else if (quad == 1) r = float4(o.x,           o.y + s.y - D, o.x + s.x, o.y + s.y);       // bottom
    else if (quad == 2) r = float4(o.x,           o.y + D,       o.x + D,   o.y + s.y - D);   // left
    else                r = float4(o.x + s.x - D, o.y + D,       o.x + s.x, o.y + s.y - D);   // right

    float2 p = lerp(r.xy, r.zw, uv);
    VSOut v;
    v.px = p;
    v.pos = float4(p.x / ViewSize.x * 2.0 - 1.0, 1.0 - p.y / ViewSize.y * 2.0, 0.0, 1.0);
    return v;
}

static const float HALF_PI = 1.57079632679;

float sdRoundRect(float2 p, float2 halfSize, float r)
{
    float2 q = abs(p) - halfSize + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// Arc-length position (0..1) around the rect perimeter, clockwise from the top-left.
// Corner zones interpolate by angle so the gradient stays continuous through the corners.
float perimeterT(float2 p, float W, float H, float Rz)
{
    float L = 2.0 * (W + H);
    float s;
    if (p.x < Rz && p.y < Rz)
        s = -Rz + 2.0 * Rz * (atan2(Rz - p.y, Rz - p.x) / HALF_PI);
    else if (p.x > W - Rz && p.y < Rz)
        s = W - Rz + 2.0 * Rz * (atan2(p.x - (W - Rz), Rz - p.y) / HALF_PI);
    else if (p.x > W - Rz && p.y > H - Rz)
        s = W + H - Rz + 2.0 * Rz * (atan2(p.y - (H - Rz), p.x - (W - Rz)) / HALF_PI);
    else if (p.x < Rz && p.y > H - Rz)
        s = 2.0 * W + H - Rz + 2.0 * Rz * (atan2(Rz - p.x, p.y - (H - Rz)) / HALF_PI);
    else
    {
        float dt = p.y, dr = W - p.x, db = H - p.y, dl = p.x;
        float m = min(min(dt, dr), min(db, dl));
        if (m == dt)      s = p.x;
        else if (m == dr) s = W + p.y;
        else if (m == db) s = W + H + (W - p.x);
        else              s = 2.0 * W + H + (H - p.y);
    }
    return frac(s / L + 1.0);
}

float circDist(float a, float b) { return abs(frac(a - b + 0.5) - 0.5); }

// Brightest of the lit arcs at loop position t (0 outside every arc).
float arcMask(float t)
{
    float pos[8]  = { ArcPosA.x, ArcPosA.y, ArcPosA.z, ArcPosA.w, ArcPosB.x, ArcPosB.y, ArcPosB.z, ArcPosB.w };
    float wid[8]  = { ArcWidA.x, ArcWidA.y, ArcWidA.z, ArcWidA.w, ArcWidB.x, ArcWidB.y, ArcWidB.z, ArcWidB.w };
    float gain[8] = { ArcGainA.x, ArcGainA.y, ArcGainA.z, ArcGainA.w, ArcGainB.x, ArcGainB.y, ArcGainB.z, ArcGainB.w };
    float m = 0.0;
    [unroll] for (int i = 0; i < 8; i++)
    {
        if (gain[i] <= 0.001) continue;
        float halfW = wid[i] * 0.5;
        float soft = min(ArcSoft, max(halfW * 0.9, 0.004));
        float a = gain[i] * (1.0 - smoothstep(halfW - soft, halfW + soft, circDist(t, pos[i])));
        m = max(m, a);
    }
    return m;
}

// Smooth multi-colour gradient around the loop: each colour owns a segment sized by its weight and
// blends softly into its neighbours (weighted gaussians, normalised). Mixing happens in OKLab.
float3 gradient(float t)
{
    float w[4]  = { ColorWeights.x, ColorWeights.y, ColorWeights.z, ColorWeights.w };
    float3 c[4] = { Color0.xyz, Color1.xyz, Color2.xyz, Color3.xyz };
    float total = max(w[0] + w[1] + w[2] + w[3], 1e-4);
    float start = 0.0, ksum = 0.0;
    float3 lab = float3(0, 0, 0);
    [unroll] for (int i = 0; i < 4; i++)
    {
        float s = w[i] / total;
        if (s > 1e-4)
        {
            float centre = start + s * 0.5;
            float sigma = 0.30 * s + 0.02;
            float d = circDist(t, centre);
            float k = s * exp(-(d * d) / (2.0 * sigma * sigma));
            lab += k * c[i];
            ksum += k;
            start += s;
        }
    }
    return lab / max(ksum, 1e-6);
}

float3 oklabToLinear(float3 c)
{
    float l_ = c.x + 0.3963377774 * c.y + 0.2158037573 * c.z;
    float m_ = c.x - 0.1055613458 * c.y - 0.0638541728 * c.z;
    float s_ = c.x - 0.0894841775 * c.y - 1.2914855480 * c.z;
    float l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
    return float3(
         4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
        -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
        -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
}

float3 linearToSrgb(float3 c)
{
    c = saturate(c);
    float3 lo = 12.92 * c;
    float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
    return lerp(hi, lo, step(c, 0.0031308));
}

float4 PSMain(VSOut i) : SV_Target
{
    float2 p = i.px - RectOrigin;
    float2 halfSize = RectSize * 0.5;
    float e = -sdRoundRect(p - halfSize, halfSize, Radius); // distance inward from the rim (px)
    if (e <= -1.0) return float4(0, 0, 0, 0);

    float rimAA = saturate(e + 0.5);                 // anti-aliased outer edge (matters at corners)
    float T = Thickness;
    float core = 1.0 - smoothstep(T - 0.75, T + 0.75, e);

    // Glow: steep exponential halo behind the core (reference: ~60% just inside the line,
    // half brightness at ~0.2 x Glow, gone by Glow), faded smoothly to zero at the far end.
    float g = 0.0;
    if (Glow > 0.5)
    {
        float x = max(e - T, 0.0);
        float halfDist = Glow * 0.2;
        g = 0.6 * exp2(-x / halfDist) * (1.0 - smoothstep(Glow * 0.6, Glow, x));
    }
    float intensity = core + (1.0 - core) * g;

    float Rz = clamp(max(Radius, BandExtent), 1.0, min(RectSize.x, RectSize.y) * 0.5);
    float baseT = perimeterT(p, RectSize.x, RectSize.y, Rz);

    // Moving light from the flow engine: lit arcs at full brightness, the rest at Floor.
    if (Floor < 0.999) intensity *= lerp(Floor, 1.0, arcMask(baseT));

    // Multi-colour gradient around the loop (drifts with Rotation).
    float3 lab = gradient(frac(baseT + Rotation));

    // Beat peaks push the rim toward white-hot.
    lab.x = lerp(lab.x, 1.0, Boost * core * 0.35);
    lab.yz *= 1.0 - Boost * core * 0.25;

    float3 rgb = linearToSrgb(oklabToLinear(lab));
    float a = saturate(intensity * Intensity) * rimAA;
    return float4(rgb * a, a);
}
