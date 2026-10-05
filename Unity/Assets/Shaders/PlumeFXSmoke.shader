// PlumeFX - volumetric smoke and fire jet (ray marching)
// One volume = a box that hangs on the celestial body. Everything is computed in "box space": axes of the celestial body,
// origin at the box centre, metres. Inside are up to 128 points as freely combined elements: chains (capsules between
// linked points: smoke column, exhaust jets per booster) and single spheres (billows of the ground cloud). Everything that
// overlaps (column foot, jets, ground cloud) lies in the same volume and is computed in ONE pass.
// Density: shape (distance to the capsule) bulged with coarse noise (billows) and eroded with fine noise (edge).
// Exhaust jet at the nozzle: fine noise that sticks to the nozzle and streams away from it; jet chains end flat in the nozzle exit plane.
// Fire jet: glowing core per nozzle (emission + absorption), plus glowing smoke (heat field) right behind the nozzle.
// Light: Beer-Lambert, shadow ray towards the sun, multiple scattering, Henyey-Greenstein, sky light, firelight of the flame.
// Second pass: ShadowCaster - the smoke casts shadows on terrain and vessels.
Shader "PlumeFX/Smoke"
{
    Properties
    {
        _NoiseTex ("Noise", 3D) = "white" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    #define MAXP 128
    #define MAXF 8

    sampler3D _NoiseTex;
    float4 _Pts[MAXP];     // xyz position (box space), w radius
    float4 _PtsB[MAXP];    // x density, y squash (vertical), z billow weight, w jet weight (fine, streaming noise)
    float4 _PtsC[MAXP];    // x young weight (medium-sized pattern), y extra erosion, z 1 = linked to the next point (capsule),
                           // w index of the nozzle point for the flat end (-1 = none)
    float4 _PtsD[MAXP];    // hull sphere of the element that starts at point i (xyz centre, w radius; 0 = no element)
    float4 _PtsE[MAXP];    // x column in the style of the ground cloud (0..1): displacement instead of added bulges (no shreds)
    float _ColWarp;        // displacement of the column in cloud style (x radius of the chain at that point)
    float _ColStyle;       // 1 = the volume has column points in cloud style (otherwise the displacement field is skipped)
    float _Count;
    float _LightCount;     // light/shadow only with the first elements (column, ground cloud; without the small booster jets)
    float _FadeInSeg, _FadeOutSeg;   // crossfade with the neighbouring section: this segment fades in or out (-1 = none)
    float3 _BoxMin, _BoxMax;
    float3 _BoxSize;       // world size of the box (conversion object space -> box space)
    float3 _Up;
    float _NoiseScale;
    float _YoungScale;     // noise frequency of young smoke (finer than the old column)
    float3 _NoiseOffset;
    float3 _NoiseOrigin;   // moves with the wind -> the noise sticks to the smoke
    float3 _NoiseK;        // offset in noise space (old smoke: the pattern grows along, the reference point is moved without a jump)
    float3 _NoiseKY;       // the same for the medium-sized pattern
    float3 _JetOrigin;     // nozzle: the fine jet noise sticks to it ...
    float3 _JetPhase;      // ... and streams away from it (offset in noise space, accumulated by the plugin)
    float _JetScale;
    float _Erosion;
    float _Lumpy;          // coarse billows (cauliflower)
    float _SmoothK;        // soft union of the billows (ground cloud): no visible sphere edges between them
    float4 _CloudC;        // ground cloud: xyz centre (box space, impact plane), w horizontal extent
    float4 _CloudP;        // x height, y noise frequency (matching the billow size), z 1 = cloud present, w displacement (m)
    float _NoiseMean;      // mean of the noise texture
    float _CloudRW;        // typical parcel size of the ground cloud: smaller parcels are displaced correspondingly less
    float3 _CloudK;        // offset in the noise space of the cloud
    float _CloudYoung;     // 1 = young parcels present (ground cloud): fine knobs and a finer edge there
    float _Boil;           // boiling in the jet
    float _Extinction;
    float _ShadowThreshold;
    float _PFXTime;        // time (set by the plugin; _Time does not run in editor rendering)
    float _PFXDebug;       // debugging (preview): 1 = light without shadow/occlusion, 2 = without pixel offset, 3 = without entry search

    // fire jet
    float _FlameN;
    float4 _FlA[MAXF];     // xyz nozzle exit, w radius
    float4 _FlB[MAXF];     // xyz flame tip, w brightness
    float3 _FlPhase;       // flow of the flame streaks (noise space)
    float4 _FlScale;       // x across, y along (1/m)

    // world -> box space (the box hangs on the celestial body: only rotation and translation, size = scale of the cube)
    float3 toBox(float3 w) { return mul(unity_WorldToObject, float4(w, 1.0)).xyz * _BoxSize; }
    float3 dirToBox(float3 d) { return normalize(mul((float3x3)unity_WorldToObject, d) * _BoxSize); }

    float2 boxHit(float3 ro, float3 rd)
    {
        float3 inv = 1.0 / rd;
        float3 t0 = (_BoxMin - ro) * inv;
        float3 t1 = (_BoxMax - ro) * inv;
        float3 tmin = min(t0, t1), tmax = max(t0, t1);
        return float2(max(max(tmin.x, tmin.y), tmin.z), min(min(tmax.x, tmax.y), tmax.z));
    }

    // Preselection per pixel: which elements the ray touches at all (bit masks for up to 128 elements).
    // Large volumes (column foot + booster jets + ground cloud) have ~50 elements; each density lookup then checks only a few.
    static uint4 gMaskP;   // density: elements on the view ray (128 bits)
    static uint4 gMaskL;   // light/shadow: elements near the ray (without the narrow booster jets)
    static float gSeg;     // debugging: 1 = strongest element is a chain (column/jet), 0 = sphere (billow)
    static float gWC;      // debugging: share of the cloud noise
    static float gSCl, gSCo;   // strongest element of the ground cloud or of the column/jets at this point (which noise applies)
    static float gRLoc;    // smallest radius of the elements at this point (step size: larger steps in large billows)
    static float gYC;      // ground cloud: share of young parcels at this point (soft over their shape - no seam to the old smoke)
    static float gCS;      // column: cloud style of the strongest element at this point

    uint4 rayMask(float3 ro, float3 rd, float t0, float t1, float inflate, int n)
    {
        uint4 m = uint4(0u, 0u, 0u, 0u);
        [loop] for (int i = 0; i < n; i++)
        {
            float4 bs = _PtsD[i];
            if (bs.w <= 0.0) continue;
            float tc = clamp(dot(bs.xyz - ro, rd), t0, t1);
            float3 q = ro + rd * tc - bs.xyz;
            float rr = bs.w + inflate;
            if (dot(q, q) <= rr * rr)
            {
                uint b = 1u << (uint)(i & 31);
                if (i < 32) m.x |= b;
                else if (i < 64) m.y |= b;
                else if (i < 96) m.z |= b;
                else m.w |= b;
            }
        }
        return m;
    }

    // one element: capsule (point i linked to i+1) or sphere (standalone point)
    // soft maximum (polynomial): transition with a fillet instead of an edge
    float smaxK(float a, float b, float k)
    {
        float h = saturate(0.5 + 0.5 * (a - b) / k);
        return lerp(b, a, h) + k * h * (1.0 - h);
    }

    void elemAt(int i, float3 p0, float3 dw, float3 dwF, float3 dwc, float light, int fin, int fout, inout float best, inout float sC, inout float sA, inout float sB, inout float dens, inout float lumpW, inout float jetW,
                inout float youngW, inout float eroB, inout float cut, inout float gap)
    {
        // test the hull at the undisplaced position (it contains the largest displacement): safe distance for skipping
        float4 bs = _PtsD[i];
        float3 db = p0 - bs.xyz;
        float dd = dot(db, db);
        if (dd > bs.w * bs.w) { gap = min(gap, sqrt(dd) - bs.w); return; }   // outside its hull
        gap = 0.0;
        float4 ca = _PtsC[i];
        bool seg = ca.z > 0.5;
        bool isCloud = !seg || ca.w < -1.5;
        float rE = seg ? min(_Pts[i].w, _Pts[i + 1].w) : _Pts[i].w;
        gRLoc = min(gRLoc, rE);
        float4 a = _Pts[i];
        float4 ab = seg ? _Pts[i + 1] : a;
        float4 ba = _PtsB[i];
        float4 bb = seg ? _PtsB[i + 1] : ba;
        float4 cb = seg ? _PtsC[i + 1] : ca;
        float3 sv = ab.xyz - a.xyz;
        float l2 = dot(sv, sv);
        // Parcels of the ground cloud (spheres and their fast streams, cut index -2) at the displaced position - small parcels
        // less (they stay in one piece). Vertically only as far as the parcel is tall (flat layers would otherwise get
        // holes). Young parcels (ca.x) additionally with the fine stage: knobs matching their still small size.
        // Column in cloud style (_PtsE.x) displaced the same way, with its own pattern and as far as its radius at that point
        // (determined at the undisplaced position: equal at the joints of the chain, no seam); jet at the nozzle undisplaced
        float3 p = p0;
        float yc = 0.0, cs = 0.0;
        if (isCloud)
        {
            yc = seg ? 0.5 * (ca.x + cb.x) : ca.x;
            float kw = saturate(0.4 + 0.6 * rE / max(_CloudRW, 0.1));   // small parcels (stream) a little less, but never smooth
            float3 d = dw * kw + dwF * (0.35 * yc);
            p = p0 + (d - _Up * (dot(d, _Up) * (1.0 - _PtsB[i].y)));
        }
        else if (_ColStyle > 0.5)
        {
            float t0 = l2 > 1e-6 ? saturate(dot(p0 - a.xyz, sv) / l2) : 0.0;
            cs = lerp(_PtsE[i].x, seg ? _PtsE[i + 1].x : _PtsE[i].x, t0);
            if (cs > 0.001)
            {
                float3 d = dwc * (_ColWarp * lerp(a.w, ab.w, t0) * cs);
                p = p0 + (d - _Up * (dot(d, _Up) * (1.0 - lerp(ba.y, bb.y, t0))));
            }
        }
        float tr = l2 > 1e-6 ? dot(p - a.xyz, sv) / l2 : 0.0;
        float t = saturate(tr);
        // section boundary: the shared segment fades out here and fades in in the neighbouring section -> no seam
        float fw2 = 1.0;
        if (light < 0.5)
        {
            if (i == fin) fw2 *= smoothstep(0.0, 1.0, tr);
            if (i == fout) fw2 *= 1.0 - smoothstep(0.0, 1.0, tr);
        }
        float3 c = a.xyz + sv * t;
        float r = lerp(a.w, ab.w, t);
        float sq = lerp(ba.y, bb.y, t);
        float3 v = p - c;
        float vu = dot(v, _Up);
        v += _Up * vu * (1.0 / max(sq, 0.05) - 1.0);
        float s = 1.0 - length(v) / r;
        // Chains: hard maximum (otherwise beads at every joint of the column). Billows: remember the two strongest - they
        // are united softly (only these two: many weak contributions outside the cloud must not add up to
        // single cloudlets)
        if (seg) sC = max(sC, s);
        else if (s > sA) { sB = sA; sA = s; }
        else if (s > sB) sB = s;
        if (isCloud) { gSCl = max(gSCl, s); gYC = max(gYC, yc * saturate(s * 3.0 + 0.6)); } else gSCo = max(gSCo, s);
        if (s > best)
        {
            best = s;
            dens = lerp(ba.x, bb.x, t) * fw2;
            lumpW = lerp(ba.z, bb.z, t);
            jetW = lerp(ba.w, bb.w, t);
            youngW = isCloud ? 0.0 : lerp(ca.x, cb.x, t);   // the young channel of the cloud is its own weight (gYC), not the column pattern
            eroB = lerp(ca.y, cb.y, t);
            gSeg = seg ? 1.0 : 0.0;
            gCS = cs;
            cut = 1.0;
            if (ca.w >= 0.0)
            {
                // flat in the nozzle exit plane (normal = jet direction of the last segment of this chain)
                int k = (int)ca.w;
                float3 pe = _Pts[k].xyz;
                float3 nrm = normalize(_Pts[k - 1].xyz - pe);
                cut = saturate(dot(p - pe, nrm) / max(_Pts[k].w * 0.15, 0.02));
            }
        }
    }

    // Shape: 0 outside .. 1 in the core; only the preselected elements.
    // light = 1: for shadow/ambient light without section crossfade (otherwise the end of each section looks freely lit -> bright cap)
    float shapeAt(float3 p, float3 dw, float3 dwF, float3 dwc, float light, out float dens, out float lumpW, out float jetW, out float youngW, out float eroB, out float cut, out float gap)
    {
        float best = -1.0;
        float sC = -1.0, sA = -1.0, sB = -1.0;
        gap = 1e9;
        gRLoc = 1e9;
        gSCl = -1.0; gSCo = -1.0;
        gYC = 0.0;
        gCS = 0.0;
        dens = 0.0;
        lumpW = 1.0;
        jetW = 0.0;
        youngW = 0.0;
        eroB = 0.0;
        cut = 1.0;
        int fin = (int)_FadeInSeg, fout = (int)_FadeOutSeg;
        uint4 m = light > 0.5 ? gMaskL : gMaskP;
        [loop] while (m.x != 0u)
        {
            int i = (int)firstbitlow(m.x);
            m.x &= m.x - 1u;
            elemAt(i, p, dw, dwF, dwc, light, fin, fout, best, sC, sA, sB, dens, lumpW, jetW, youngW, eroB, cut, gap);
        }
        [loop] while (m.y != 0u)
        {
            int i = 32 + (int)firstbitlow(m.y);
            m.y &= m.y - 1u;
            elemAt(i, p, dw, dwF, dwc, light, fin, fout, best, sC, sA, sB, dens, lumpW, jetW, youngW, eroB, cut, gap);
        }
        [loop] while (m.z != 0u)
        {
            int i = 64 + (int)firstbitlow(m.z);
            m.z &= m.z - 1u;
            elemAt(i, p, dw, dwF, dwc, light, fin, fout, best, sC, sA, sB, dens, lumpW, jetW, youngW, eroB, cut, gap);
        }
        [loop] while (m.w != 0u)
        {
            int i = 96 + (int)firstbitlow(m.w);
            m.w &= m.w - 1u;
            elemAt(i, p, dw, dwF, dwc, light, fin, fout, best, sC, sA, sB, dens, lumpW, jetW, youngW, eroB, cut, gap);
        }
        // billows united softly with each other and with the column (the bonus stays limited)
        float kk = max(_SmoothK, 1e-3);
        return smaxK(sC, smaxK(sA, sB, kk), kk);
    }

    // Noise in noise space qq: coarse billows (lump) and fine structure (n). Tiles with period 50 per axis.
    void noiseQ(float3 qq, float3 osc, out float lump, out float n)
    {
        float3 q0 = qq * 0.4 + _NoiseOffset * 0.5;
        lump = tex3Dlod(_NoiseTex, float4(q0, 0)).r * 0.66 + tex3Dlod(_NoiseTex, float4(q0 * 2.3 + 0.5, 0)).r * 0.26
             + tex3Dlod(_NoiseTex, float4(q0 * 5.1 + 0.9, 0)).r * 0.08;
        float3 q = qq + _NoiseOffset;
        n = tex3Dlod(_NoiseTex, float4(q + osc * 0.35, 0)).r * 0.75
          + tex3Dlod(_NoiseTex, float4(q * 2.3 + 0.31 - osc * 0.5, 0)).r * 0.25;
    }

    // Share of the ground cloud at a point (soft, spatial - no seam between column and cloud): there a noise
    // that matches the size of the billows (otherwise the fine column pattern breaks the large billows into small shreds)
    float cloudW(float3 p)
    {
        if (_CloudP.z < 0.5) return 0.0;
        float3 dv = p - _CloudC.xyz;
        float vz = dot(dv, _Up);
        float hz = length(dv - _Up * vz);
        float e = max(hz / _CloudC.w, vz / _CloudP.x);
        return 1.0 - smoothstep(0.85, 1.15, e);
    }

    // Ground cloud: space is displaced softly (one fetch = vector from R, G, B). This makes billow shapes, but - unlike
    // added bulges - no detached cloudlets above the cloud. w = share of the cloud at this point
    // dwF = the fine stage alone (m): young, small parcels get more of it (knobs matching their size)
    float3 cloudWarp(float3 p, float w, out float lumpC, out float3 dwF)
    {
        lumpC = _NoiseMean;
        dwF = 0;
        if (w <= 0.01) return 0;
        float3 q = ((p - _CloudC.xyz) * _CloudP.y + _CloudK) * 0.4 + _NoiseOffset * 0.5;
        float3 v = tex3Dlod(_NoiseTex, float4(q, 0)).rgb;
        // second, finer stage: medium-sized knobs (cauliflower) on the large billows - also without detached pieces
        float3 v2 = tex3Dlod(_NoiseTex, float4(q * 2.6 + 0.37, 0)).rgb;
        lumpC = v.r * 0.75 + v2.r * 0.25;
        float A = _CloudP.w * w;
        dwF = (v2 - _NoiseMean) * A;
        return ((v - _NoiseMean) + (v2 - _NoiseMean) * 0.3) * A;
    }

    // Column in cloud style: displacement field from the column pattern - the same fetches as its coarse billows (costs hardly
    // more). Unit ~+-0.5, per element times the radius at that point x _ColWarp. Reuse vA, vB for the billows
    float3 colWarp(float3 p, out float3 vA, out float3 vB)
    {
        float3 q0 = ((p - _NoiseOrigin) * _NoiseScale + _NoiseK) * 0.4 + _NoiseOffset * 0.5;
        vA = tex3Dlod(_NoiseTex, float4(q0, 0)).rgb;
        vB = tex3Dlod(_NoiseTex, float4(q0 * 2.3 + 0.5, 0)).rgb;
        return (vA - _NoiseMean) + (vB - _NoiseMean) * 0.3;
    }

    // fine structure (erosion) of the ground cloud
    float cloudN(float3 qq)
    {
        float3 q = qq + _NoiseOffset;
        return tex3Dlod(_NoiseTex, float4(q, 0)).r * 0.75 + tex3Dlod(_NoiseTex, float4(q * 2.3 + 0.31, 0)).r * 0.25;
    }

    // density; nOut = noise at this point (for the hot spots in the glowing jet)
    float densityN(float3 p, out float nOut, out float gap)
    {
        nOut = 0.5;
        float wc0 = cloudW(p);
        float lumpC;
        float3 dwF;
        float3 dw = cloudWarp(p, wc0, lumpC, dwF);
        float3 cvA = 0, cvB = 0, dwc = 0;
        if (_ColStyle > 0.5) dwc = colWarp(p, cvA, cvB);
        float dens, lumpW, jetW, youngW, eroB, cut;
        float s = shapeAt(p, dw, dwF, dwc, 0.0, dens, lumpW, jetW, youngW, eroB, cut, gap);
        float la = _Lumpy * lumpW;
        if (s <= -la * 0.5 || dens <= 0.0 || cut <= 0.0) return 0.0;
        // Eddy size grows away from the nozzle: jet (fine, streaming) -> young smoke (medium) -> old column (large, calm).
        // Column in cloud style: hardly any fine young pattern (that gave the many small shreds)
        float wJ = saturate(jetW);
        float wC = (1.0 - wJ) * wc0 * smoothstep(-0.12, 0.12, gSCl - gSCo);
        gWC = wC;
        float wY = (1.0 - wJ - wC) * saturate(youngW) * (1.0 - 0.75 * gCS);
        float wB = 1.0 - wJ - wC - wY;
        float lump = 0.0, n = 0.0, wSum = 0.0, w2 = 0.0;
        float l1, n1;
        if (wB > 0.01)
        {
            if (_ColStyle > 0.5)
            {
                // coarse billows from the fetches already made for the displacement, third stage and fine structure as in noiseQ
                float3 qq = (p - _NoiseOrigin) * _NoiseScale + _NoiseK;
                l1 = cvA.r * 0.66 + cvB.r * 0.26 + tex3Dlod(_NoiseTex, float4((qq * 0.4 + _NoiseOffset * 0.5) * 5.1 + 0.9, 0)).r * 0.08;
                float3 q = qq + _NoiseOffset;
                n1 = tex3Dlod(_NoiseTex, float4(q, 0)).r * 0.75 + tex3Dlod(_NoiseTex, float4(q * 2.3 + 0.31, 0)).r * 0.25;
            }
            else noiseQ((p - _NoiseOrigin) * _NoiseScale + _NoiseK, 0, l1, n1);
            lump += wB * l1; n += wB * n1; wSum += wB; w2 += wB * wB;
        }
        if (wY > 0.01) { noiseQ((p - _NoiseOrigin) * _YoungScale + _NoiseKY + 17.3, 0, l1, n1); lump += wY * l1; n += wY * n1; wSum += wY; w2 += wY * wY; }
        if (wC > 0.01)
        {
            float3 qc = (p - _CloudC.xyz) * _CloudP.y + _CloudK;
            n1 = cloudN(qc);
            // young parcels: finer edge (the eddies are still small), faded in softly over their shape
            if (_CloudYoung > 0.5 && gYC > 0.01) n1 = lerp(n1, cloudN(qc * 2.4 + 0.53), gYC * 0.75);
            lump += wC * lumpC; n += wC * n1; wSum += wC; w2 += wC * wC;
        }
        if (wJ > 0.01)
        {
            float tt = _PFXTime;
            float3 osc = float3(sin(tt * 7.1), sin(tt * 5.3 + 1.3), sin(tt * 6.2 + 2.1)) * _Boil;
            noiseQ((p - _JetOrigin) * _JetScale - _JetPhase, osc, l1, n1);
            lump += wJ * l1; n += wJ * n1; wSum += wJ; w2 += wJ * wJ;
        }
        // mixed patterns: restore the contrast (otherwise the transition becomes smooth and washed out)
        float kc = wSum / sqrt(max(w2, 1e-4));
        lump = saturate(0.5 + (lump / max(wSum, 1e-4) - 0.5) * kc);
        n = saturate(0.5 + (n / max(wSum, 1e-4) - 0.5) * kc);
        nOut = saturate(lump * 0.6 + n * 0.4);
        // bulges far outside the billows fade out in the ground cloud: otherwise single small cloudlets appear above the cloud
        // in the ground cloud (and the column in cloud style) hardly any added bulges (the shape comes from the displacement), and only
        // near the surface
        float wS = max(wC, gCS);
        s += (lump - 0.5) * la * lerp(1.0, 0.15 * smoothstep(-0.4, -0.02, s), wS);
        if (s <= 0.0) return 0.0;
        float shape = saturate(s * 2.0);
        // edge: narrower in the ground cloud (relative to the billow radius) - otherwise the edges of large billows are metres wide and blurred
        float d = smoothstep(0.0, lerp(0.18, 0.08, max(wC, 0.8 * gCS)), shape - (1.0 - n) * (_Erosion + eroB));
        return d * dens * cut;
    }

    float densityAt(float3 p) { float n, g; return densityN(p, n, g); }

    // Density for light and shadow: without section crossfade, only the coarse billows, soft (no threshold:
    // otherwise the edges of the shadow samples show up as fine lines on the cloud)
    float densityLight(float3 p)
    {
        float wc = cloudW(p);
        float lumpC;
        float3 dwF;
        float3 dw = cloudWarp(p, wc, lumpC, dwF);
        float3 cvA = 0, cvB = 0, dwc = 0;
        if (_ColStyle > 0.5) dwc = colWarp(p, cvA, cvB);
        float dens, lumpW, jetW, youngW, eroB, cut, gap;
        float s = shapeAt(p, dw, dwF, dwc, 1.0, dens, lumpW, jetW, youngW, eroB, cut, gap);
        float la = _Lumpy * lumpW;
        if (s <= -la * 0.5 || dens <= 0.0 || cut <= 0.0) return 0.0;
        float lump = 0.04;
        if (wc < 0.99)
        {
            if (_ColStyle > 0.5) lump += (1.0 - wc) * (cvA.r * 0.66 + cvB.r * 0.26);
            else
            {
                float3 q0 = ((p - _NoiseOrigin) * _NoiseScale + _NoiseK) * 0.4 + _NoiseOffset * 0.5;
                lump += (1.0 - wc) * (tex3Dlod(_NoiseTex, float4(q0, 0)).r * 0.66 + tex3Dlod(_NoiseTex, float4(q0 * 2.3 + 0.5, 0)).r * 0.26);
            }
        }
        lump += wc * lumpC * 0.92;
        s += (lump - 0.5) * la * (1.0 - 0.85 * max(wc, gCS));
        if (s <= 0.0) return 0.0;
        return smoothstep(0.0, 0.5, s) * (1.0 - 0.6 * saturate(eroB)) * dens * cut;
    }

    // Heat of the exhaust (0..1): white-hot at the nozzle, cools along the jet and outward.
    // The dense smoke there glows itself (glowing fog), then it turns into white smoke.
    float heatAt(float3 p)
    {
        float h = 0.0;
        int nf = (int)_FlameN;
        [loop] for (int i = 0; i < nf; i++)
        {
            float3 A = _FlA[i].xyz;
            float3 ax = _FlB[i].xyz - A;
            float L2 = max(dot(ax, ax), 1e-4);
            float u = dot(p - A, ax) / L2;
            if (u <= -0.02 || u >= 1.2) continue;
            float R = _FlA[i].w;
            float rho = length(p - A - ax * saturate(u));
            float wI = _FlScale.z;                      // long, blazing jet on the ground
            float rf = R * (1.0 + (1.8 + 4.5 * wI) * saturate(u));   // the hot region widens with the jet
            float along = 1.0 - smoothstep(0.2 + 0.3 * wI, 1.15, u);
            float radial = 1.0 - smoothstep(0.7, 1.8, rho / rf);
            h = max(h, along * radial);
        }
        return h;
    }

    float3 fireColor(float h)
    {
        // solid-fuel flame: white core, yellow, orange towards the cool edge and the end
        float3 c = lerp(float3(0.9, 0.42, 0.1), float3(1.0, 0.7, 0.3), saturate(h * 2.5));
        c = lerp(c, float3(1.0, 0.86, 0.55), saturate((h - 0.45) * 3.0));
        return lerp(c, float3(1.0, 0.97, 0.9), saturate((h - 0.8) * 4.0));
    }

    // Fire jet: glowing, streaked core right out of the nozzle. em = emission per m, sig = extinction per m
    void flameAt(float3 p, out float3 em, out float sig)
    {
        em = 0;
        sig = 0;
        int nf = (int)_FlameN;
        [loop] for (int i = 0; i < nf; i++)
        {
            float3 A = _FlA[i].xyz;
            float3 ax = _FlB[i].xyz - A;
            float L2 = max(dot(ax, ax), 1e-4);
            float u = dot(p - A, ax) / L2;
            if (u <= 0.0 || u >= 1.0) continue;
            float R = _FlA[i].w;
            float3 rv = p - A - ax * u;
            float rho = length(rv);
            // as wide as the nozzle exit, widens, tapers towards the tip. On the ground (wI) wider and longer
            // blazing, like the fire jet of the Shuttle boosters at launch
            float wI = _FlScale.z;
            float rf = R * (0.95 + (0.45 + 3.0 * wI) * u) * (1.0 - (0.75 - 0.4 * wI) * smoothstep(0.4 + 0.2 * wI, 1.0, u));   // several boosters: the jets merge
            if (rho >= rf * 1.45) continue;
            float L = sqrt(L2);
            float3 dax = ax / L;
            float3 q = rv * _FlScale.x + dax * (u * L * _FlScale.y) - _FlPhase + float3(i * 3.7, i * 1.3, i * 5.1);
            float nn = tex3Dlod(_NoiseTex, float4(q, 0)).r * 0.65 + tex3Dlod(_NoiseTex, float4(q * 2.1 + 0.37, 0)).r * 0.35;
            float c = 1.0 - rho / rf;
            float f = smoothstep(0.0, 0.55, c + (nn - 0.5) * 0.8);
            float along = smoothstep(0.0, 0.025, u) * (1.0 - smoothstep(0.3 + 0.3 * nn + 0.3 * wI, 1.0, u));
            float k = f * along;
            if (k <= 0.0) continue;
            float temp = saturate((0.6 + 0.55 * c + (nn - 0.5) * 0.5) * (1.0 - (0.55 - 0.25 * wI) * u));
            float s = k * 1.3 / rf;
            sig += s;
            em += fireColor(temp) * (s * _FlB[i].w * (0.2 + 0.8 * temp * temp));
        }
    }
    ENDCG

    SubShader
    {
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" "IgnoreProjector"="True" }

        Pass
        {
            Cull Front
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float3 _SunDir;
            float3 _SunColor;
            float3 _AmbientColor;
            float3 _BounceColor;   // light reflected from the sunlit ground
            float3 _SmokeColor;
            float _Steps;
            float _MaxStep;        // m: steps at most this long (fixed fineness instead of a fixed step count)
            float _ShadowSteps;
            float _ShadowLen;
            float _UseDepth;
            float4 _Glow[4];       // xyz flame start, w strength
            float4 _GlowB[4];      // xyz flame end, w radius
            float3 _GlowColor;
            float3 _FineA, _FineB; // region with fine steps (narrow jet at the nozzle)
            float _FineR, _FineStep;
            float _FireFog;        // self-glow of the hot smoke at the nozzle

            struct v2f { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float4 spos : TEXCOORD1; };

            v2f vert(float4 v : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v);
                o.wpos = mul(unity_ObjectToWorld, v).xyz;
                o.spos = ComputeScreenPos(o.pos);
                return o;
            }

            float hg(float c, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5);
            }

            // fixed pixel pattern (no flicker from frame to frame)
            float ign(float2 p) { return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715)))); }

            // step size: fine in the jet region, outside it grows with the distance (never jumps into it)
            float stepAt(float3 p, float baseStep)
            {
                if (_FineR <= 0.0) return baseStep;
                float3 sg = _FineB - _FineA;
                float3 cp = _FineA + sg * saturate(dot(p - _FineA, sg) / max(dot(sg, sg), 1e-6));
                return clamp(length(p - cp) - _FineR, _FineStep, baseStep);
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 roW = _WorldSpaceCameraPos;
                float3 rdW = normalize(i.wpos - roW);
                // compute in box space (the box hangs on the celestial body); distances along the ray stay the same
                float3 ro = toBox(roW);
                float3 rd = dirToBox(rdW);
                float2 th = boxHit(ro, rd);
                float tn = max(th.x, 0.0), tf = th.y;
                if (_UseDepth > 0.5)
                {
                    float2 suv = i.spos.xy / i.spos.w;
                    float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, suv);
                    float eye = LinearEyeDepth(raw);
                    float3 fwd = -UNITY_MATRIX_V[2].xyz;
                    float sceneT = eye / max(dot(rdW, fwd), 1e-3);
                    tf = min(tf, sceneT);
                }
                if (tf <= tn) discard;
                gMaskP = rayMask(ro, rd, tn, tf, 0.0, (int)_Count);
                if ((gMaskP.x | gMaskP.y | gMaskP.z | gMaskP.w) == 0u) discard;   // the ray touches no element
                gMaskL = rayMask(ro, rd, tn, tf, _ShadowLen * 3.5, (int)_LightCount);

                // base step: fine enough for the billows, larger on long paths (never cut off)
                float len = tf - tn;
                float baseStep = max(min(len / _Steps, _MaxStep), len / 400.0);
                float cosT = dot(rd, _SunDir);
                float phase = (hg(cosT, 0.3) * 0.6 + hg(cosT, -0.2) * 0.4) * 0.8;

                float T = 1.0;
                float3 Ls = 0;     // scattered light (smoke)
                float3 Le = 0;     // emission (flame)
                float st = stepAt(ro + rd * tn, baseStep);
                float t = tn + st * (abs(_PFXDebug - 2.0) < 0.5 ? 0.5 : ign(i.pos.xy));
                float tPrev = tn;
                float wasEmpty = 1.0;
                float jit = ign(i.pos.xy);
                float3 Slast = 0;
                float haveS = 0.0;
                [loop] for (int k = 0; k < 768; k++)
                {
                    if (t >= tf) break;
                    float3 p = ro + rd * t;
                    float nn, gap;
                    float d = densityN(p, nn, gap);
                    if (d > 0.002 && wasEmpty > 0.5 && k > 0 && abs(_PFXDebug - 3.0) > 0.5)
                    {
                        // search the entry into the smoke exactly: otherwise the steps show up as a grid/terraces on the surface
                        float a0 = tPrev, a1 = t;
                        [loop] for (int bs = 0; bs < 4; bs++)
                        {
                            float tm = 0.5 * (a0 + a1);
                            if (densityAt(ro + rd * tm) > 0.002) a1 = tm; else a0 = tm;
                        }
                        // first sample behind the surface slightly offset per pixel: otherwise all samples lie at the same
                        // distance from the surface and draw contour lines (fine lines) into the cloud
                        t = a1 + jit * min(st * 0.35, 1.5);
                        p = ro + rd * t;
                        d = densityN(p, nn, gap);
                    }
                    wasEmpty = d > 0.002 ? 0.0 : 1.0;
                    // base step by the size of the elements at this point: in the large billows of the ground cloud
                    // (30-60 m) not as fine as at the small column billows of the volume
                    float stLoc = gRLoc < 1e8 ? clamp(0.08 * gRLoc, baseStep, max(baseStep, len / _Steps)) : baseStep;
                    float3 em = 0;
                    float sf = 0.0;
                    if (_FlameN > 0.5) flameAt(p, em, sf);
                    float ss = d > 0.002 ? d * _Extinction : 0.0;
                    float sig = ss + sf;
                    st = stepAt(p, stLoc);
                    if (gap > st && sf <= 0.0) st = gap;   // empty space up to the next element: in one jump
                    if (sig > 1e-5)
                    {
                        // small steps in dense smoke (at most ~40 % coverage per step): soft, calm surface
                        st = clamp(0.7 / sig, 0.03, st);   // at most ~50 % coverage per step; thin smoke: base step
                        float a = 1.0 - exp(-sig * st);
                        // only recompute the light if the sample contributes noticeably (deep in the smoke or very thin: take the last value)
                        if (ss > 0.0 && haveS > 0.5 && T * a < 0.004)
                        {
                            Ls += T * a * (ss / sig) * Slast;
                        }
                        else if (ss > 0.0)
                        {
                            float od = 0.0;
                            int nss = (int)_ShadowSteps;
                            [loop] for (int s = 1; s <= nss; s++)
                                od += densityLight(p + _SunDir * (_ShadowLen * s));
                            float odS = od * _Extinction * _ShadowLen;
                            float Ts = exp(-odS);
                            float Tms = exp(-odS * 0.25) * 0.22 + exp(-odS * 0.06) * 0.1;
                            float powder = 1.0 - exp(-ss * 2.0 * _ShadowLen);
                            float3 sun = _SunColor * (Ts * phase * lerp(0.7, 1.0, powder) + Tms);
                            // Sky light: from above AND from the side facing the camera (otherwise a vertical
                            // column turns almost black under a high sun), plus light from the sunlit ground
                            // one sample each (instead of two) is enough for the soft sky light and saves time in large clouds
                            float odUp = 2.0 * densityLight(p + _Up * (_ShadowLen * 2.5));
                            float3 sideDir = -rd - _Up * dot(-rd, _Up);
                            float sideL = length(sideDir);
                            sideDir = sideL > 1e-3 ? sideDir / sideL : _Up;
                            float odSide = 2.0 * densityLight(p + sideDir * (_ShadowLen * 2.5));
                            float occ = 0.45 * exp(-odUp * _Extinction * _ShadowLen * 0.5) + 0.55 * exp(-odSide * _Extinction * _ShadowLen * 0.5);
                            if (abs(_PFXDebug - 1.0) < 0.5) { occ = 1.0; sun = _SunColor * 0.5; }
                            float3 amb = (_AmbientColor + _BounceColor) * lerp(0.55, 1.0, occ);
                            float3 glow = 0;
                            [unroll] for (int g = 0; g < 4; g++)
                            {
                                float3 ga = _Glow[g].xyz, gb = _GlowB[g].xyz;
                                float3 sg = gb - ga;
                                float tg = saturate(dot(p - ga, sg) / max(dot(sg, sg), 1e-4));
                                float3 dv = p - (ga + sg * tg);
                                float r = _GlowB[g].w;
                                float q = 1.0 + dot(dv, dv) / (r * r);
                                float fall = _Glow[g].w / (q * q);   // fast falloff: the light stays near the flame
                                float3 gc = lerp(float3(1.0, 0.92, 0.75), _GlowColor, saturate(tg * 0.9 + length(dv) / (r * 4.0)));
                                glow += gc * fall;
                            }
                            float3 S = _SmokeColor * (sun + amb + glow);
                            if (abs(_PFXDebug - 4.0) < 0.5) S *= lerp(float3(1.0, 0.35, 0.35), float3(0.35, 0.45, 1.0), gWC);   // red = column noise, blue = cloud noise
                            if (abs(_PFXDebug - 5.0) < 0.5) S *= lerp(float3(0.35, 0.45, 1.0), float3(1.0, 0.35, 0.35), gSeg);  // red = chain, blue = billow
                            // glowing fog right behind the nozzle: hot spots follow the streaming jet pattern
                            if (_FireFog > 0.0 && _FlameN > 0.5)
                            {
                                float h = heatAt(p);
                                if (h > 0.003)
                                {
                                    float he = saturate(h * (0.85 + 1.8 * (nn - 0.5)));   // hotter and cooler spots: boiling, glowing fog
                                    S += fireColor(he) * (_FireFog * pow(he, 1.5));
                                }
                            }
                            Ls += T * a * (ss / sig) * S;
                            Slast = S;
                            haveS = 1.0;
                        }
                        Le += T * a * (em / sig);
                        T *= 1.0 - a;
                        if (T < 0.01) break;
                    }
                    tPrev = t;
                    t += st;
                }
                float alpha = 1.0 - T;
                float lumE = dot(Le, float3(0.3, 0.5, 0.2));
                if (alpha < 0.002 && lumE < 0.003) discard;
                float3 c = 0;
                if (alpha > 1e-4)
                {
                    // tone curve on the luminance: bright areas keep their detail instead of burning out flat white
                    c = Ls / alpha;
                    float lum = max(dot(c, float3(0.3, 0.5, 0.2)), 1e-4);
                    float lt = 1.0 - exp(-lum * 2.0);
                    c = c * (lt / lum);
                    c = c / max(1.0, max(c.r, max(c.g, c.b)));
                    c *= alpha;
                }
                // flame core softly limited (at most ~2): in the game with HDR/bloom it glows over without blinding
                float3 fl = Le * (2.0 * (1.0 - exp(-lumE * 0.5)) / max(lumE, 1e-4));
                return float4(c + fl, alpha);
            }
            ENDCG
        }

        // shadow: ray along the light direction through the volume, shade from sufficient coverage on
        Pass
        {
            Tags { "LightMode"="ShadowCaster" }
            Cull Front
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vertS
            #pragma fragment fragS
            #pragma target 5.0
            #pragma multi_compile_shadowcaster

            struct v2fs { V2F_SHADOW_CASTER; float3 wpos : TEXCOORD1; };

            v2fs vertS(appdata_base v)
            {
                v2fs o;
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            // depth = where the smoke starts along the light ray (not the back of the box): otherwise for the ground cloud,
            // whose box reaches below the ground, the ground lies "in front of" the shadow caster and stays lit
            float4 fragS(v2fs i, out float outDepth : SV_Depth) : SV_Target
            {
                outDepth = i.pos.z;
                float3 rd = dirToBox(normalize(-UNITY_MATRIX_V[2].xyz));   // view direction of the shadow camera (light)
                float3 ro = toBox(i.wpos) - rd * 100000.0;
                float2 th = boxHit(ro, rd);
                float tn = th.x, tf = th.y;
                if (tf <= tn) discard;
                gMaskL = rayMask(ro, rd, tn, tf, 0.0, (int)_LightCount);
                gMaskP = gMaskL;
                if ((gMaskL.x | gMaskL.y | gMaskL.z | gMaskL.w) == 0u) discard;
                const int steps = 24;
                float stepLen = (tf - tn) / steps;
                float od = 0.0;
                float tHit = -1.0;
                float odEdge = 0.35 * -log(_ShadowThreshold) / max(_Extinction * stepLen, 1e-5);   // edge of the shadow
                float t = tn + stepLen * 0.5;
                [loop] for (int k = 0; k < steps; k++)
                {
                    od += densityLight(ro + rd * t);
                    if (tHit < 0.0 && od > odEdge) tHit = t;
                    t += stepLen;
                }
                float T = exp(-od * _Extinction * stepLen);
                if (T > _ShadowThreshold) discard;
                #if !defined(SHADOWS_CUBE)
                if (tHit < 0.0) tHit = tf;
                float3 wp = mul(unity_ObjectToWorld, float4((ro + rd * tHit) / _BoxSize, 1.0)).xyz;
                float4 cp = UnityApplyLinearShadowBias(mul(UNITY_MATRIX_VP, float4(wp, 1.0)));
                outDepth = cp.z / cp.w;
                #endif
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }
    }
}
