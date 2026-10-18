// Фон «Сейчас играет» (tasks/0035): жидкая обложка + лава-лампа + пульс на бас. Перевод один в один с fluid.vert и
// fluid.frag (GLSL ES 3.00) из melogoldLinux — эталона для всех платформ; Direct3D 11, профили vs_4_0 и ps_4_0.
// Компилируется при первом показе (D3DCompile), ошибка компиляции — запасной фон из tasks/0034; в CI — fxc (ci.yml).

cbuffer Constants : register(b0)
{
    float4 uPal[6];   // rgb — цвет палитры (0..1, как есть), w — его доля
    float4 uBlob[2];  // силы капель: [0] — 0..3, [1] — 4..5
    float4 uRes;      // x, y — размер области в пикселях
    float4 uA;        // x uLiquid, y uLiquidFlow, z uLavaFlow, w uPulse
    float4 uB;        // x uLavaMix, y uLoud, z uLuma, w uLiquidBase
    float4 uC;        // x uBlobScale, y uZoom, z uLodBase, w uLodPulse
    float4 uD;        // x uBright, y uSat, z uPalCount
};

Texture2D<float4> uCover : register(t0);
SamplerState uSampler : register(s0);   // трилинейная, MIRRORED_REPEAT

struct VsOut
{
    float4 position : SV_Position;
    float2 vUv : TEXCOORD0;
};

// Один треугольник на весь экран. vUv — от левого нижнего угла, как в GL.
VsOut vs_main(uint id : SV_VertexID)
{
    float2 pos = float2((float)((id << 1) & 2), (float)(id & 2));
    VsOut output;
    output.vUv = pos;
    output.position = float4(pos * 2.0 - 1.0, 0.0, 1.0);
    return output;
}

float2 rot(float2 p, float a)
{
    float c = cos(a), s = sin(a);
    // GLSL: mat2(c, s, -s, c) * p
    return float2(c * p.x - s * p.y, s * p.x + c * p.y);
}

float3 blurAt(float2 uv, float lod)
{
    uv.y = 1.0 - uv.y;
    float r = exp2(lod) / 256.0 * 1.4;
    float3 c = uCover.SampleLevel(uSampler, uv, lod).rgb * 0.36;
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float a = float(i) * 1.5708 + 0.785;
        c += uCover.SampleLevel(uSampler, uv + r * float2(cos(a), sin(a)), lod).rgb * 0.16;
    }
    return c;
}

float4 ps_main(VsOut input) : SV_Target
{
    float uLiquid = uA.x, uLiquidFlow = uA.y, uLavaFlow = uA.z, uPulse = uA.w;
    float uLavaMix = uB.x, uLoud = uB.y, uLuma = uB.z, uLiquidBase = uB.w;
    float uBlobScale = uC.x, uZoom = uC.y, uLodBase = uC.z, uLodPulse = uC.w;
    float uBright = uD.x, uSat = uD.y;
    int uPalCount = (int)(uD.z + 0.5);
    float2 vUv = input.vUv;

    float aspect = uRes.x / uRes.y;
    float pulse = clamp(uPulse, 0.0, 1.0);
    // Пульс на бас: удар чуть приближает фон, собирает размытие и поднимает свет.
    float2 p = (vUv - 0.5) * float2(aspect, 1.0) * (1.0 - uZoom * pulse);
    float lod = lerp(uLodBase, uLodPulse, pulse);

    // Жидкая обложка: течение искажает само себя; сила и скорость — от своей полосы.
    float2 q = rot(p, uLiquidFlow * 0.045);
    float2 q2 = rot(p * 0.82, -uLiquidFlow * 0.065 + 1.5708) + 0.18 * float2(sin(uLiquidFlow * 0.059), cos(uLiquidFlow * 0.043));
    float amp = uLiquidBase + 0.62 * uLiquid;
    [unroll]
    for (int i = 0; i < 3; i++)
    {
        float fi = float(i);
        q += amp * 0.32 * float2(sin(q.y * 2.3 + uLiquidFlow * 0.7 + fi * 1.3), cos(q.x * 2.0 - uLiquidFlow * 0.55 + fi * 2.1));
        q2 += amp * 0.28 * float2(cos(q2.y * 1.9 - uLiquidFlow * 0.6 + fi), sin(q2.x * 2.4 + uLiquidFlow * 0.5 + fi * 1.7));
    }
    float3 col = lerp(blurAt(0.5 + q * 0.62, lod), blurAt(0.5 + q2 * 0.62, lod + 0.5), 0.38);

    // Лава-лампа: капли цветов обложки, размер — доля цвета и своя сила звука.
    float3 acc = float3(0.0, 0.0, 0.0);
    float field = 0.0;
    [loop]
    for (int j = 0; j < 6; j++)
    {
        if (j >= uPalCount) break;
        float fj = float(j);
        float share = uPal[j].w;
        float blob = uBlob[j >> 2][j & 3];
        float rad = (0.13 + 0.48 * sqrt(share)) * uBlobScale * (1.0 + 0.55 * blob);
        float2 c = float2(sin(uLavaFlow * 0.21 + fj * 2.4) * aspect * 0.42, cos(uLavaFlow * 0.17 + fj * 1.7) * 0.42);
        c += 0.07 * float2(sin(uLavaFlow * 0.9 + fj), cos(uLavaFlow * 1.1 + fj * 2.0));
        float f = rad * rad / (dot(p - c, p - c) + 0.003);
        field += f;
        acc += uPal[j].rgb * f;
    }
    float3 lavaCol = acc / max(field, 1e-3);
    float m = smoothstep(0.7, 1.8, field);
    col = lerp(col * 0.82, lavaCol, m * uLavaMix + 0.08);

    float k = clamp(0.30 / max(uLuma, 0.01), 0.30, 1.0) * uBright * (1.0 + 0.16 * pulse + 0.08 * uLoud);
    float l = dot(col, float3(0.2126, 0.7152, 0.0722));
    col = lerp(float3(l, l, l), col, uSat) * k;
    float v = smoothstep(0.55, 1.0, length((vUv - 0.5) / 0.75));
    col *= 1.0 - 0.3 * v;
    return float4(clamp(col, 0.0, 1.0), 1.0);
}
