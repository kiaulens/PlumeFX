// PlumeFX - shared core (no KSP dependencies): runs in the game (PlumeFX.dll) and in the Unity preview.
// Units: "local" positions = in the coordinate system of the celestial body (centre = origin), world = Unity world.
// The smoke volumes hang on the celestial body (children of its transform): Unity moves them along (floating origin,
// Krakensbane, rotation of the planet). Old, distant sections are therefore only rarely recomputed (frame rate).
// C# 5 (compiled for KSP with the .NET Framework csc).
//
// Structure of a plume (solid rocket boosters, modelled on the Space Shuttle):
//   nozzle -> near field (narrow, dense, boiling, glowing jet from the nozzle exit; several boosters: one jet each)
//         -> column head (handover, same width and density) -> standing smoke column (large, calm billows)
//            that spreads over many minutes, twists (the wind turns with altitude) and dissolves into shreds.
//   On the ground: ground cloud of large billows - the jet pushes the smoke onto the ground and sideways, it rises at the edge.
// A volume is a set of chains (capsules) and spheres. Whatever overlaps lies in the SAME volume and is computed in one
// pass: at launch the lowest column section also carries the near field, the booster jets and the ground cloud.
// So there are no ordering errors between volumes drawn separately.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlumeFX
{
    internal static partial class Cfg
    {
        public static bool enabled = true;
        public static bool debugLog = true;
        public static float densityCutoff = 0.004f;
        public static float maxDistance = 600000f;       // m: volumes farther away are not drawn (the column is visible from orbit)
        public static int steps = 64;
        public static int shadowSteps = 4;
        public static float brightness = 1f;
        public static float columnRadius = 3.0f;
        public static float thickness = 25f;
        public static float boil = 0.35f;
        public static bool shadows = true;
        public static float jetFlow = 25f;             // m/s per m of nozzle diameter: the pattern in the jet streams away from the nozzle
        public static float fire = 3f;                 // glow of the hot exhaust at the nozzle
        public static float glow = 1.0f;               // firelight of the flame on the smoke right around it
        public static float flameLength = 7f;          // x nozzle diameter (sea level, full thrust)
        public static float flameBoost = 14f;          // extra flame length on the ground (x nozzle diameter), decreases with altitude
        public static float fireBoost = 1.4f;          // extra brightness of the fire jet on the ground (decreases with altitude)
        public static float jetDensity = 1f;           // density of the smoke right behind the nozzle
        public static bool splitJets = true;           // several boosters with spacing: a jet of its own per booster
        public static bool groundCloud = true;         // ground cloud at launch (in the same volume as the column foot)
        public static float groundSize = 1f;           // size of the ground cloud
        public static float groundRange = 12f;         // reach of the jet to the ground (x nozzle diameter, +25 m)
        public static float groundDrift = 0.7f;        // ground cloud and column foot drift with the surface wind (1 = fully)
        public static float columnFoot = 0.5f;         // column foot after liftoff: radius x size of the ground cloud (0 = off)
        public static float columnCloudStyle = 1f;     // column in the style of the ground cloud (large connected billows; 0 = finer, more ragged billows)
        public static float windDrift = 0.3f;          // how strongly the smoke drifts with the wind (1 = fully)
        public static float windShear = 1f;            // the wind turns and grows with altitude: the trail twists over time
        public static float columnSpacing = 2.0f;
        public static float trailLife = 1500f;         // s: the smoke trail stays at most this long (shorter low down), dissolves slowly
        public static int maxVolumes = 0;              // 0 = unlimited
        public static int updateBudget = 40;           // old, distant sections: this many are recomputed per frame
        public static bool useDepth = true;
        public static float srbStrength = 1f, keroloxStrength = 0f, methaloxStrength = 0f, hydrogenStrength = 0f;
        public static float windSpeed = 3f, windMin = 0.2f, windMax = 1.4f;

        public static void Log(string s) { if (debugLog) Debug.Log("[PlumeFX] " + s); }
    }

    internal class SmokeType
    {
        public string name;
        public Color color;
        public float opacity;     // opacity (extinction)
        public float life;        // s
        public float size;        // radius factor
        public float growth;      // r = r0 * (1 + growth * sqrt(age))
        public float ground;      // ground cloud factor
        public float columnMaxH;  // m above ground (<0 = unlimited)
        public float rise;        // m/s^2
        public float strength;

        public static SmokeType SRB, Kerolox, Methalox, Hydrogen;

        public static void Init()
        {
            SRB = new SmokeType { name = "Solid", color = new Color(0.99f, 0.96f, 0.91f), opacity = 1.0f, life = Cfg.trailLife, size = 1.3f, growth = 0.8f, ground = 2.2f, columnMaxH = -1f, rise = 0.5f, strength = Cfg.srbStrength };
            Kerolox = new SmokeType { name = "Kerolox", color = new Color(0.84f, 0.83f, 0.82f), opacity = 0.35f, life = Cfg.trailLife * 0.3f, size = 0.9f, growth = 0.55f, ground = 1.0f, columnMaxH = -1f, rise = 0.4f, strength = Cfg.keroloxStrength };
            Methalox = new SmokeType { name = "Methalox", color = new Color(0.92f, 0.92f, 0.92f), opacity = 0.2f, life = Cfg.trailLife * 0.2f, size = 0.8f, growth = 0.55f, ground = 0.8f, columnMaxH = -1f, rise = 0.4f, strength = Cfg.methaloxStrength };
            Hydrogen = new SmokeType { name = "Hydrogen", color = new Color(1f, 1f, 1f), opacity = 0.25f, life = Cfg.trailLife * 0.1f, size = 1.0f, growth = 0.5f, ground = 1.0f, columnMaxH = 150f, rise = 0.8f, strength = Cfg.hydrogenStrength };
        }
    }

    internal class Pt
    {
        public Vector3 pos;      // local
        public Vector3 vel;
        public float r0, age, life, dens0, squash, rise, drag, rMin;
        public float rVar = 1f;       // random size, grows in only with age (no jump at the column head)
        public float gMul = 1f;       // growth: smoke born on the ground barely swells (the rocket stays visible)
        public float gNear, gMul0, sq0;   // born on the ground (0..1) with start values: swells once the rocket is high enough
        public float wide = 1f, wideT = 1f, rFootT;   // column foot: width (factor), target and target radius (m) - fills the middle after liftoff
        public float foot;                // 0..1: how much the point belongs to the column foot (1 at the bottom, 0 from ~2.5 cloud sizes up)
        public float ramp = 0.6f;     // s in which the puff swells from small to its size
        public float rStart = 0.55f;  // start size (fraction of r0)
        public float fadeIn = 0.05f;  // s fade-in
        public float lumpW = 1f;      // billows (cauliflower): 1 = full, small = smooth shape (jet right at the nozzle)
        public float jetW;            // share of the fine jet pattern streaming away from the nozzle

        public Pt Clone()
        {
            return new Pt { pos = pos, vel = vel, r0 = r0, age = age, life = life, dens0 = dens0, squash = squash, rise = rise, drag = drag, rMin = rMin, rVar = rVar, gMul = gMul, gNear = gNear, gMul0 = gMul0, sq0 = sq0, wide = wide, wideT = wideT, rFootT = rFootT, foot = foot, ramp = ramp, fadeIn = fadeIn, rStart = rStart, lumpW = lumpW, jetW = jetW };
        }
    }

    internal struct Flame { public Vector3 a, b; public float r, bright; }   // fire jet: nozzle exit -> tip (local)

    // Ground cloud: smoke parcels (spheres) in the same volume as the column foot. They shoot out with the jet (from the
    // trench exits or from the impact point), brake, swell, rise and merge at the head - the cloud grows
    // by new parts, not by scaling up.
    internal class Lobe
    {
        public Vector3 pos, vel;          // local
        public Vector3 origin;            // emission point (trench exit or impact): the parcels fan out from there
        public float r, dens, squash, age;
        public float v0;                  // initial momentum: once the parcel has lost enough of it, it rolls up (the head towers up)
        public float roll;                // 0 = flat, fast layer .. 1 = rolled up (billow, tower further out)
        public float capF = 1f, sqF = 1f, growF = 1f;   // random per parcel: final size, tower shape, growth
        public int stream, seq;           // stream (0/1 = trench exit, 2+ = direction from the impact) and order within it
        public Lobe into;                 // merges into this parcel
        public bool dead;
        // terrain: the parcel lies on the visible ground (pad, terrain), not in the flame shaft under the pad
        public bool gInit;
        public Vector3 gQ;                // where the ground was last probed
        public float gD, gH;
        // still in the fast stream (drawn as a chain: one connected jet instead of separate spheres)
        public bool Streaming { get { return into == null && !dead && age < 2.5f && vel.sqrMagnitude > 15f * 15f; } }
    }

    internal class GroundCloud
    {
        public Vector3 centerL, upL, e1L, e2L, hitNowL;
        public float age, M, R, Rmax, feedS, rJet, tLeave, life, densBase, gNoz;
        public int nExit;                                          // flame trenches: exits in use (0 = none found)
        public Vector3[] exitL = new Vector3[2], exitDir = new Vector3[2];
        public float exitDist;
        public float[] emitAcc = new float[3];
        public int seqNext, radialK;
        public float radialPhase = UnityEngine.Random.value * 6.2832f;
        public bool feeding, dead;
        public float rW, noiseS;          // typical parcel size (r^3-weighted: the big billows) and the matching noise
        public float rRefSm, extH, extV;  // smoothed for drawing: light sampling length, horizontal/vertical extent
        public float clearC;              // 0..1: clear zone around the rocket (the jet blows the smoke aside while it is low)
        // random per launch (not exaggerated): reach, height of the towers, width of the fan; per trench side the strength
        public float reachF = 1f, heightF = 1f, widthF = 1f;
        public float[] exitF = new float[] { 1f, 1f };    // per trench side: amount and momentum (one side dominates)
        public float[] sideH = new float[] { 1f, 1f };    // per trench side: height of the towers
        public bool anyYoung;                             // young parcels present (shader: fine knobs)
        public Vector3 noiseK;
        public List<Lobe> lobes = new List<Lobe>();
    }

    internal class Volume
    {
        public const int MAXP = 128;      // points per volume (shader arrays, bit masks with 128 bits)
        public const int MAXF = 8;        // flames per volume
        public const int COLMAX = 12;     // split columns into short sections: tight boxes -> finer steps
        public const int MERGEMAX = 16;   // old, thinned-out sections may become this long when merged
        public int id;
        public List<Pt> pts = new List<Pt>();   // chain of the smoke column (with its own near field: its points)
        public bool open = true;
        public bool dead;
        public SmokeType type;
        public float growth;
        public GameObject go;
        public MeshRenderer mr;
        public MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        public Vector3 noiseSeed;
        public float noiseScale;          // fixed from the first draw (old smoke: grows with the spreading)
        public Vector3 noiseK, noiseKY;   // offset in noise space (moving the reference point without a jump)
        public Vector3 anchorL;           // reference point of the noise (drifts with the wind)
        public bool anchored;
        public bool jet;                  // own near field while no column carries it (thin air, vacuum)
        public Plume owner;
        public bool fadeStart, fadeEnd;   // overlap with the previous/next section
        public Volume prevSec, nextSec;   // neighbouring sections of the column (old sections are merged)
        public GroundCloud cloud;         // ground cloud (only in the lowest column section)
        // set every frame by the emitter (local)
        public bool host;                 // draws the near field: virtual points, booster jets, flames
        public List<Flame> flames = new List<Flame>();
        public Vector3 jetOriginL;
        public Vector3 fineA, fineB;
        public float fineR, fineStep;     // region with fine steps (narrow jet)
        // update (distant, old sections rarely)
        public int lastUpd = int.MinValue / 2;
        public float accUT, accDt;        // time accumulated since the last simulation
        public Vector3 boxC, boxS;        // local
        public float radiusMax;
        public bool drawable, nearCam;
        public float camDist = 1e9f;
        public float newestAge;
    }

    internal class Plume
    {
        public Volume column, jet, groundVol;
        public bool touched;
        public string dbg = "";
        // near field as virtual points at the head of the open column: from the head to the nozzle (local)
        public bool jetActive;
        public List<Pt> vPts = new List<Pt>();
        public int nVirt;
        public bool virtCut;              // one shared jet: the chain ends flat at the nozzle
        // several boosters with spacing: one chain per nozzle (end, middle, near nozzle, nozzle), in the same volume as the column
        public bool split;
        public List<Pt> jetPts = new List<Pt>();
        public int nJets;
        public List<Flame> flames = new List<Flame>();
        public Vector3 emitL, dirL, fineA, fineB;
        public float fineR, fineStep;
        // shared by all sections of the plume: noise pattern and density reference
        public bool noiseInit;
        public float noiseScale, extRef, jetScale, flameScaleR, flameScaleA;
        public Vector3 noiseSeed, anchorL;
        public Vector3 jetPhase, flamePhase;   // flow in noise space (wrapping, no loss of precision)
        public float flowSpeed, flameSpeed;
        public float fireFog;             // self-glow of the hot smoke at the nozzle
        public float flameI;              // 0..1: long, wide, blazing fire jet (on the ground)
        public float jetH;                // jet length (handover to the column)
        public float vRocket;             // speed of the nozzle (smoothed): slow = much smoke per metre, thick column
        public Vector3 lastEmitL;
        public bool hasLast, vValid;
    }

    internal struct Glow { public Vector3 pos, posB; public float strength; public float radius; }   // line source (local)

    // input per plume and frame (computed by the game or the preview)
    internal class PlumeInput
    {
        public SmokeType type;
        public Vector3 emitW;        // centroid of the nozzle exits (world)
        public Vector3 dir;          // exhaust direction (world)
        public float thr;            // throttle 0..1
        public float dEff;           // effective nozzle diameter (square root of the sum of d^2)
        public float dMax;
        public float spread;         // bundle width (max. distance of a nozzle from the axis)
        public float tTot;           // sum of maximum thrust (kN)
        public float rho;            // air density kg/m^3
        public float hGround;        // height above ground (m)
        public List<Vector3> nozW = new List<Vector3>();   // nozzle exits (world)
        public List<float> nozD = new List<float>();       // exit diameter per nozzle (m)
        public List<Vector3> trenchW = new List<Vector3>(); // exits of the launch pad's flame trenches (world), if any
    }

    internal delegate bool GroundHitFn(Vector3 from, Vector3 dir, float range, out Vector3 hit, out float dist);
    internal delegate float SurfaceRadiusFn(Vector3 local);

    internal class PlumeSystem
    {
        public Material mat;
        public Mesh cube;
        public List<Volume> volumes = new List<Volume>();
        public Dictionary<string, Plume> plumes = new Dictionary<string, Plume>();
        public List<Glow> glows = new List<Glow>();
        public GroundHitFn groundHit;
        public SurfaceRadiusFn surfaceRadius;
        public float bodyRadius = 600000f;
        Transform bodyT;
        Vector4[] bufP = new Vector4[Volume.MAXP], bufB = new Vector4[Volume.MAXP], bufC = new Vector4[Volume.MAXP], bufD = new Vector4[Volume.MAXP], bufE = new Vector4[Volume.MAXP];
        Vector4[] bufG = new Vector4[4], bufG2 = new Vector4[4], bufFA = new Vector4[Volume.MAXF], bufFB = new Vector4[Volume.MAXF];
        // points of a volume while assembling (local)
        int eN;
        Vector3[] eP = new Vector3[Volume.MAXP];
        float[] eR = new float[Volume.MAXP], eD = new float[Volume.MAXP], eSq = new float[Volume.MAXP], eLw = new float[Volume.MAXP], eJw = new float[Volume.MAXP], eYw = new float[Volume.MAXP], eEb = new float[Volume.MAXP], eCs = new float[Volume.MAXP];
        bool[] eLink = new bool[Volume.MAXP];
        int[] eCut = new int[Volume.MAXP];
        float lastRenderTime = -1f;
        Vector3 lastWind;
        List<Lobe> lobeSel = new List<Lobe>();
        int[] stCnt = new int[10], stMin = new int[10], stMax = new int[10];
        static float LobePrio(Lobe L) { return (L.Streaming ? 1000f : 0f) + L.r - (L.into != null ? 500f : 0f); }
        const float SMOOTHK = 0.4f;       // soft union of the billows (fraction of the radius)
        const float WARP = 0.5f;         // displacement of space in the ground cloud (x billow radius): billow shapes without detached pieces
        const float COLWARP = 0.45f;     // the same for the column (x radius of the chain at that point): like the ground cloud, without shreds
        float cRh, cHc;                   // extent of the ground cloud in the volume being built (horizontal, height)
        int frame, rrStart, nextId;
        public int statUpdated;
        public bool forceAll;             // preview: recompute all volumes in this frame
        float lifeSeed = UnityEngine.Random.value * 100f;

        // near field: nodes from the column head to the nozzle (fraction of the jet length), density, billows, jet pattern
        static readonly float[] NX = { 0.6f, 0.25f, 0f };
        static readonly float[] ND = { 1.35f, 1.45f, 1.5f };
        static readonly float[] NL = { 0.85f, 0.55f, 0.2f };
        static readonly float[] NJ = { 0.75f, 1f, 1f };

        public PlumeSystem(Shader shader, Texture3D noise, Mesh cubeMesh)
        {
            mat = new Material(shader);
            mat.SetTexture("_NoiseTex", noise);
            cube = cubeMesh;
        }

        static void Kill(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        public void Clear()
        {
            foreach (Volume v in volumes) Kill(v.go);
            volumes.Clear();
            plumes.Clear();
        }

        public void Dispose()
        {
            Clear();
            Kill(mat);
        }

        // ---------- noise ----------
        // Tileable 3D noise: mix of value noise (fbm) and inverted Worley (cloudy billows)
        public static float NoiseMean = 0.59f;   // mean of the noise texture (displacement of the cloud without drift)

        public static Texture3D MakeNoise(int N)
        {
            System.Random rng = new System.Random(1234);
            int G = 8;
            float[] lat = new float[G * G * G];
            for (int i = 0; i < lat.Length; i++) lat[i] = (float)rng.NextDouble();
            int C = 6;
            Vector3[] fp = new Vector3[C * C * C];
            for (int i = 0; i < fp.Length; i++) fp[i] = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            byte[] nb = new byte[N * N * N];
            double sum = 0.0;
            for (int z = 0; z < N; z++)
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float u = (float)x / N, v = (float)y / N, w = (float)z / N;
                        float val = 0f, amp = 0.5f; int f = 1;
                        for (int o = 0; o < 3; o++) { val += amp * ValueNoise(lat, G, u * G * f, v * G * f, w * G * f); amp *= 0.5f; f *= 2; }
                        val /= 0.875f;
                        float wor = 1f - Mathf.Clamp01(Worley(fp, C, u * C, v * C, w * C) * 1.4f);
                        float n = Mathf.Clamp01(wor * 0.65f + val * 0.5f - 0.05f);
                        nb[x + N * (y + N * z)] = (byte)(n * 255f);
                        sum += n;
                    }
            NoiseMean = (float)(sum / nb.Length);
            // R = noise; G and B = copies shifted against each other: one fetch gives a displacement vector (ground cloud)
            Color32[] px = new Color32[N * N * N];
            int sg = N / 3, sb = (2 * N) / 5;
            for (int z = 0; z < N; z++)
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        byte r = nb[x + N * (y + N * z)];
                        byte g = nb[(x + sg) % N + N * ((y + N / 7) % N + N * ((z + sb) % N))];
                        byte b = nb[(x + sb) % N + N * ((y + sg) % N + N * ((z + N / 5) % N))];
                        px[x + N * (y + N * z)] = new Color32(r, g, b, 255);
                    }
            Texture3D t = new Texture3D(N, N, N, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Bilinear;
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static float ValueNoise(float[] lat, int G, float x, float y, float z)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y), z0 = Mathf.FloorToInt(z);
            float fx = x - x0, fy = y - y0, fz = z - z0;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
            float r = 0f;
            for (int k = 0; k < 2; k++) for (int j = 0; j < 2; j++) for (int i = 0; i < 2; i++)
                    {
                        int xi = ((x0 + i) % G + G) % G, yi = ((y0 + j) % G + G) % G, zi = ((z0 + k) % G + G) % G;
                        float wgt = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy) * (k == 0 ? 1 - fz : fz);
                        r += wgt * lat[xi + G * (yi + G * zi)];
                    }
            return r;
        }

        static float Worley(Vector3[] fp, int C, float x, float y, float z)
        {
            int cx = Mathf.FloorToInt(x), cy = Mathf.FloorToInt(y), cz = Mathf.FloorToInt(z);
            float best = 9f;
            for (int k = -1; k <= 1; k++) for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++)
                    {
                        int xi = cx + i, yi = cy + j, zi = cz + k;
                        int wx = ((xi % C) + C) % C, wy = ((yi % C) + C) % C, wz = ((zi % C) + C) % C;
                        Vector3 p = fp[wx + C * (wy + C * wz)] + new Vector3(xi, yi, zi);
                        float d = (p - new Vector3(x, y, z)).sqrMagnitude;
                        if (d < best) best = d;
                    }
            return Mathf.Sqrt(best);
        }

        // keep the offset in noise space wrapping (the noise tiles with period 50 per axis)
        static Vector3 Wrap50(Vector3 v)
        {
            return new Vector3(v.x - 50f * Mathf.Floor(v.x / 50f), v.y - 50f * Mathf.Floor(v.y / 50f), v.z - 50f * Mathf.Floor(v.z / 50f));
        }

        // ---------- wind (same formula as ChuteFX.SharedWind) ----------
        public static Vector3 SharedWindLocal(int bodyIndex, double ut)
        {
            float s = bodyIndex * 13.7f + 0.37f;
            float pS = Mathf.Clamp01((Mathf.PerlinNoise((float)(ut / 900.0 % 10000.0), s) - 0.2f) / 0.6f);
            float strength = Cfg.windSpeed * Mathf.Lerp(Cfg.windMin, Cfg.windMax, pS);
            float ang = Mathf.PerlinNoise((float)(ut / 1800.0 % 10000.0), s + 5.1f) * 4f * Mathf.PI;
            float gust = 0.6f + 0.8f * Mathf.PerlinNoise((float)(ut * 0.35 % 10000.0), s + 9.7f);
            return new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (strength * gust);   // x = east, z = north
        }

        // wind aloft: turns and grows stronger -> the smoke trail twists over the minutes
        static Vector3 ShearWind(Vector3 w, Vector3 up, float alt)
        {
            Vector3 h = w - up * Vector3.Dot(up, w);
            if (Cfg.windShear <= 0f) return h;
            alt = Mathf.Max(alt, 0f);
            float th = alt / 7000f * Cfg.windShear;
            Vector3 hr = h * Mathf.Cos(th) + Vector3.Cross(up, h) * Mathf.Sin(th);
            return hr * (1f + Mathf.Min(alt / 5000f, 2.5f) * Cfg.windShear);
        }

        // ---------- volumes ----------
        Volume NewVolume(SmokeType t)
        {
            if (Cfg.maxVolumes > 0 && volumes.Count >= Cfg.maxVolumes)
            {
                int victim = -1;
                for (int i = 0; i < volumes.Count && victim < 0; i++) if (!volumes[i].open && volumes[i].cloud == null && !volumes[i].dead) victim = i;   // oldest column sections first
                for (int i = 0; i < volumes.Count && victim < 0; i++) if (!volumes[i].open && !volumes[i].dead) victim = i;
                if (victim >= 0) { Unlink(volumes[victim]); Kill(volumes[victim].go); volumes.RemoveAt(victim); }
                if (volumes.Count >= Cfg.maxVolumes) return null;
            }
            Volume v = new Volume();
            v.id = nextId++;
            v.type = t;
            v.growth = t.growth;
            v.noiseSeed = new Vector3(UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value) * 10f;
            v.go = new GameObject("PlumeFX_Smoke");
            v.go.layer = 0;
            if (bodyT != null) v.go.transform.SetParent(bodyT, false);
            v.go.AddComponent<MeshFilter>().sharedMesh = cube;
            v.mr = v.go.AddComponent<MeshRenderer>();
            v.mr.sharedMaterial = mat;
            v.mr.shadowCastingMode = Cfg.shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            v.mr.receiveShadows = false;
            v.mr.enabled = false;
            volumes.Add(v);
            return v;
        }

        static void Unlink(Volume v)
        {
            if (v.prevSec != null) v.prevSec.nextSec = v.nextSec;
            if (v.nextSec != null) v.nextSec.prevSec = v.prevSec;
            v.prevSec = null; v.nextSec = null;
        }

        void RemoveDead()
        {
            for (int i = volumes.Count - 1; i >= 0; i--)
            {
                Volume v = volumes[i];
                if (!v.dead) continue;
                Unlink(v);
                Kill(v.go);
                volumes.RemoveAt(i);
            }
        }

        public void BeginFrame()
        {
            glows.Clear();
            foreach (Plume p in plumes.Values) p.touched = false;
            foreach (Volume v in volumes) { v.flames.Clear(); v.fineR = 0f; v.host = false; }
        }

        Pt MakePt(Vector3 pL, float r0, float dens, float squash, SmokeType t)
        {
            Pt p = new Pt();
            p.pos = pL;
            p.r0 = r0;
            p.rVar = UnityEngine.Random.Range(0.75f, 1.3f);
            p.dens0 = dens;
            p.squash = squash;
            p.rise = t.rise;
            p.drag = 1.0f;
            p.rMin = surfaceRadius != null ? surfaceRadius(pL) : 0f;
            // low down the trail dissolves faster (turbulence near the ground), high up it stays for a long time.
            // lifetime changes smoothly with altitude: whole stretches of the trail dissolve together (no single spheres)
            float alt = Mathf.Max(0f, pL.magnitude - p.rMin);
            p.life = t.life * (0.72f + 0.28f * Mathf.PerlinNoise(lifeSeed, alt / 1800f)) * Mathf.Lerp(0.45f, 1f, Mathf.SmoothStep(0f, 1f, (alt - 500f) / 14500f));
            return p;
        }

        // column point; born on the ground (gNear > 0): flat and slowed until the rocket is far enough above it (SimPoints)
        Pt ColumnPt(Vector3 pL, float r0, float dens, float sq, float gMul, float gNear, float rStart, float foot, float wideT, float rFootT, SmokeType t)
        {
            Pt p = MakePt(pL, r0, dens, sq, t);
            p.gMul = gMul; p.gMul0 = gMul; p.sq0 = sq; p.gNear = gNear; p.rStart = rStart;
            p.foot = foot; p.wideT = wideT; p.rFootT = rFootT;
            return p;
        }

        // late spreading: after the first minute the trail slowly widens and thins out
        public static float LateSpread(float age) { return 1f + Mathf.Max(0f, age - 45f) / 90f; }

        // billows start small and swell, then the cloud keeps growing slowly
        public static float Radius(Pt p, float growth)
        {
            float k = Mathf.SmoothStep(0f, 1f, p.age / Mathf.Max(p.ramp, 0.01f));
            float sa = Mathf.Sqrt(p.age);
            float v = Mathf.Lerp(1f, p.rVar, Mathf.SmoothStep(0f, 1f, p.age / 1.5f));
            return p.r0 * v * (p.rStart + (1f - p.rStart) * k) * (1f + growth * p.gMul * (Mathf.Min(sa, 2f) + 0.08f * Mathf.Max(sa - 2f, 0f))) * LateSpread(p.age) * p.wide;
        }

        // blazing fire jet on the ground: right behind the nozzle the smoke is thin (the jet is visible), it only gets dense
        // further back (nx = fraction of the jet length from the nozzle)
        static float JetThin(float nx, float boostI)
        {
            return 1f - boostI * 0.8f * Mathf.Clamp01(1f - nx / 0.7f);
        }

        static void SetJetPt(Pt q, Vector3 pos, float r, float dens, float lumpW, float jetW)
        {
            q.pos = pos; q.r0 = r; q.dens0 = dens; q.lumpW = lumpW; q.jetW = jetW;
            q.age = 1f; q.life = 1e9f; q.rStart = 1f; q.ramp = 0.01f; q.fadeIn = 0.01f; q.squash = 1f; q.rVar = 1f;
        }

        // ---------- emitting (one plume) ----------
        public void EmitPlume(string key, PlumeInput a, Transform bt, float dt)
        {
            bodyT = bt;
            Plume pl;
            if (!plumes.TryGetValue(key, out pl)) { pl = new Plume(); plumes[key] = pl; }
            pl.touched = true;
            SmokeType ty = a.type;
            float rho = a.rho;
            float rhoR = rho / 1.225f;
            float densF = Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0f, rhoR)), 0f, 1.3f);
            // in very thin air (~25-30 km on Kerbin) the smoke fades out softly, only the flame stays
            // (there the jet is huge and almost transparent: expensive to draw, hardly visible)
            float hiFade = Mathf.SmoothStep(0f, 1f, (densF - 0.05f) / 0.09f);
            bool thick = rho >= Cfg.densityCutoff && hiFade > 0.02f;
            float expand = Mathf.Clamp(1f / Mathf.Pow(Mathf.Max(rhoR, 0.02f), 0.333f), 1f, 4f);
            Vector3 emitW = a.emitW, dir = a.dir;
            float thr = a.thr, dEff = a.dEff, spread = a.spread, tTot = a.tTot, dMax = a.dMax;
            int nN = a.nozW.Count;
            float R = Mathf.Max(dMax * 0.5f, 0.05f);
            Vector3 emitL = bt.InverseTransformPoint(emitW);
            Vector3 dirL = bt.InverseTransformDirection(dir);

            // sizes: everything in nozzle diameters, grows in thin air (the jet expands)
            float r0 = Mathf.Max(dEff * Cfg.columnRadius * ty.size * expand, spread * 1.4f + dMax * 1.3f);   // column
            const float rStartCol = 0.55f;
            float rHead = r0 * rStartCol;                                    // column head = end of the jet
            // fire jet: long and blazing on the ground (dense air, like a Shuttle launch), shorter and weaker with altitude.
            // The near field (smoke around the jet, handover to the column) keeps its length - the flame reaches into the column
            float fI = Mathf.Pow(Mathf.Clamp01(rhoR), 0.65f);                 // 1 on the ground, ~0.6 at 4 km, ~0.33 at 10 km, ~0.08 at 22 km
            float thrF = (0.55f + 0.45f * thr) * dMax;
            float Lf0 = Cfg.flameLength * thrF * Mathf.Sqrt(expand);          // near field as before (longer in thin air)
            // visible fire jet: long and blazing on the ground, shorter with altitude - without smoke (vacuum) only a short core
            float Lf = (Cfg.flameLength * (0.45f + 0.55f * fI) + Cfg.flameBoost * fI) * thrF * (1f + 0.25f * (Mathf.Sqrt(expand) - 1f));
            float H = Mathf.Max(Lf0 * 1.3f + 0.5f * Mathf.Max(Lf - Lf0, 0f), dMax * 3f) + spread;   // jet length up to the handover to the column
            pl.jetH = H;
            float dens = densF * (0.4f + 0.6f * Mathf.Sqrt(thr)) * hiFade;
            float jd = dens * Cfg.jetDensity;

            if (!pl.noiseInit)
            {
                pl.noiseInit = true;
                // in the style of the ground cloud a coarser pattern (billows as large as in the cloud next to it, no fine shreds)
                pl.noiseScale = 1f / Mathf.Max(r0 * (1f + ty.growth * 4f) * 0.85f * Mathf.Lerp(1f, 1.6f, Mathf.Clamp01(Cfg.columnCloudStyle)), 1f);
                pl.extRef = r0 * (1f + ty.growth * 2f);
                pl.noiseSeed = new Vector3(UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value) * 10f;
                pl.anchorL = emitL;
                pl.jetScale = 1f / (R * 4.5f);          // fine jet pattern: billows ~ nozzle diameter
                pl.flameScaleR = 0.3f / R;              // flame streaks: fine across ...
                pl.flameScaleA = pl.flameScaleR * 0.2f; // ... stretched lengthwise
            }
            pl.flowSpeed = Cfg.jetFlow * dMax;
            pl.flameSpeed = 30f * R;
            // speed of the nozzle (physics steps; frames without a new step do not count)
            if (dt > 0f)
            {
                if (pl.hasLast)
                {
                    float vr = Mathf.Min((emitL - pl.lastEmitL).magnitude / dt, 3000f);
                    pl.vRocket = pl.vValid ? Mathf.Lerp(pl.vRocket, vr, Mathf.Clamp01(dt / 0.4f)) : vr;   // first measurement taken directly (ignition in flight)
                    pl.vValid = true;
                }
                pl.lastEmitL = emitL;
                pl.hasLast = true;
            }
            pl.emitL = emitL;
            pl.dirL = dirL;

            // ground hit of the exhaust jet
            float gRange = dEff * Cfg.groundRange * (ty == SmokeType.SRB ? 1.5f : 1f) + 25f;
            float back = dMax * 4f * expand;
            Vector3 gFrom = emitW - dir * back;
            Vector3 hitW = Vector3.zero; float hitDist = 0f;
            bool hitGround = rho > 0.02f && groundHit != null && groundHit(gFrom, dir, gRange, out hitW, out hitDist);
            float gNoz = hitGround ? hitDist - back : 1e9f;   // nozzle -> ground along the jet
            // if the jet hits the ground, the near field is compressed (the smoke piles up on the pad)
            float squeeze = (hitGround && gNoz < H) ? Mathf.Clamp(gNoz * 0.95f / H, 0.06f, 1f) : 1f;

            // fire jet per nozzle, plus its glow on the smoke right around it
            pl.flames.Clear();
            float boostI = Cfg.flameBoost > 0f ? fI : 0f;
            float bright = Cfg.fire * 0.5f * (0.35f + 0.65f * thr) * (1f + Cfg.fireBoost * boostI) * (0.65f + 0.35f * fI);   // flame core (HDR), weaker higher up
            pl.fireFog = Cfg.fire * (0.4f + 0.6f * thr) * (1f + 0.5f * Cfg.fireBoost * boostI);
            pl.flameI = boostI;
            for (int k = 0; k < nN && k < Volume.MAXF; k++)
            {
                float rk = Mathf.Max((a.nozD.Count > k ? a.nozD[k] : dMax) * 0.5f, 0.05f);
                Vector3 A = bt.InverseTransformPoint(a.nozW[k]);
                float lk = Lf * rk / R;
                pl.flames.Add(new Flame { a = A, b = A + dirL * lk, r = rk, bright = bright });
                if (Cfg.glow > 0f) glows.Add(new Glow { pos = A + dirL * (lk * 0.05f), posB = A + dirL * (lk * 0.75f), strength = Cfg.glow * thr * (1f + 1.5f * boostI), radius = rk * (1.8f + 2.5f * boostI) });
            }

            // ----- near field -----
            bool split = Cfg.splitJets && nN > 1 && spread > dMax * 0.6f;
            pl.split = split;
            while (pl.vPts.Count < 3) pl.vPts.Add(new Pt());
            if (!split)
            {
                pl.nJets = 0;
                // one shared jet: starts exactly at the nozzle exit (as wide as the nozzle), widens towards the column head
                float rN = R * 1.1f + spread;   // slightly larger: the eroded edge then lies on the nozzle rim
                for (int j = 0; j < 3; j++)
                {
                    float rr = rN + (rHead - rN) * Mathf.Pow(NX[j], 1.3f);
                    SetJetPt(pl.vPts[j], emitL + dirL * (NX[j] * H * squeeze), rr, jd * ND[j] * JetThin(NX[j], boostI), NL[j], NJ[j]);
                }
                pl.nVirt = 3;
                pl.virtCut = true;
                pl.fineA = emitL; pl.fineB = emitL + dirL * (H * squeeze); pl.fineR = rHead * 1.2f; pl.fineStep = Mathf.Clamp(R * 0.3f * Mathf.Sqrt(expand), 0.03f, 0.5f);
            }
            else
            {
                // several boosters with spacing: a jet of its own per nozzle, the jets converge on the bundle axis.
                // They are drawn in the same volume as the column (no overlap of separate volumes).
                int want = Mathf.Min(nN, Volume.MAXF);
                while (pl.jetPts.Count < want * 4) pl.jetPts.Add(new Pt());
                float mergeX = H * 0.85f;
                for (int k = 0; k < want; k++)
                {
                    Vector3 nz = pl.flames[k].a;
                    float rk = pl.flames[k].r;
                    Vector3 off = Vector3.ProjectOnPlane(nz - emitL, dirL);
                    // end beyond the confluence (thinning out, overlaps with the column), middle, near nozzle, nozzle
                    for (int j = 0; j < 4; j++)
                    {
                        float f = j == 0 ? 1.15f : NX[j - 1];
                        float x = f * mergeX;
                        Vector3 pw = nz + dirL * (x * squeeze) - off * (Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(f)) * 0.65f);
                        float rr = rk * 1.1f + (rHead * 0.7f - rk * 1.1f) * Mathf.Pow(Mathf.Clamp01(x / H), 1.3f);
                        if (j == 0) SetJetPt(pl.jetPts[k * 4], pw, rr, jd * 0.35f, 0.8f, 0.5f);
                        else SetJetPt(pl.jetPts[k * 4 + j], pw, rr, jd * ND[j - 1] * JetThin(NX[j - 1], boostI), NL[j - 1], NJ[j - 1]);
                    }
                }
                pl.nJets = want;
                // the column starts thin at the confluence and fades in over the booster jets
                SetJetPt(pl.vPts[0], emitL + dirL * (mergeX * 0.6f * squeeze), spread + R * 1.5f, jd * 0.3f, 0.6f, 0.5f);
                pl.nVirt = 1;
                pl.virtCut = false;
                float rk0 = pl.flames.Count > 0 ? pl.flames[0].r : R;
                pl.fineA = emitL; pl.fineB = emitL + dirL * (mergeX * 1.15f * squeeze); pl.fineR = spread + rHead * 0.9f; pl.fineStep = Mathf.Clamp(rk0 * 0.3f * Mathf.Sqrt(expand), 0.03f, 0.5f);
            }

            // ----- smoke column (starts on the ground; smoke born there barely swells) -----
            bool column = thick && (ty.columnMaxH < 0f || a.hGround < ty.columnMaxH);
            Vector3 pL = emitL + dirL * (H * squeeze);
            float gMul = hitGround ? 0.15f + 0.85f * Mathf.Clamp01((gNoz - H * 0.5f) / (H * 2.5f)) : 1f;
            // smoke born on the ground lies flat and wide (as if pushed aside by the jet) instead of wrapping the boosters
            // as a round cloud; it becomes round with altitude
            float gk = Mathf.InverseLerp(0.15f, 1f, gMul);
            float gSq = Mathf.Lerp(0.35f, 1f, gk);
            // slow rocket (liftoff): much smoke per metre -> thick column (cross-section ~ output / speed). The head still
            // starts as wide as the jet end and then swells (no step at the handover)
            // (not the smoke right at the pad: it only swells once the rocket is far enough above it - SimPoints)
            float kSlow = pl.vValid ? Mathf.Lerp(1f, Mathf.Clamp(Mathf.Pow(45f / Mathf.Max(pl.vRocket, 1f), 0.35f), 1f, 1.6f), gk) : 1f;
            float gR0 = r0 * Mathf.Lerp(0.75f, 1f, gk) * kSlow;
            float rSt = rStartCol / kSlow;
            float gNear = 1f - gk;
            // column foot: shortly after liftoff the column becomes really wide at the bottom (fills the middle between the banks of
            // the ground cloud) and tapers to the normal width higher up. Target ~ columnFoot x size of the ground cloud; it only
            // swells once the rocket is far enough above it (SimPoints) - the rocket stays visible
            GroundCloud gcF = (pl.groundVol != null && !pl.groundVol.dead) ? pl.groundVol.cloud : null;
            float foot = 0f, rFootT = 0f, wideT = 1f;
            if (gcF != null && Cfg.columnFoot > 0f)
            {
                float hB = hitGround ? Mathf.Max(0f, gNoz - H * squeeze) : Mathf.Max(0f, a.hGround - H);
                float Rmf = Mathf.Max(gcF.Rmax, 10f);
                foot = 1f - Mathf.SmoothStep(0f, 1f, hB / (2.5f * Rmf));
                float rNorm = gR0 * (1f + 2f * ty.growth);                 // normal final size of this point
                rFootT = Mathf.Lerp(rNorm, Cfg.columnFoot * Rmf, foot);
                wideT = Mathf.Max(1f, rFootT / Mathf.Max(rNorm, 0.5f));
            }
            pl.dbg ="rho=" + rho.ToString("F3") + " h=" + a.hGround.ToString("F0") + " thr=" + thr.ToString("F2") + " d=" + dMax.ToString("F2") + " spread=" + spread.ToString("F2") + " noz=" + nN + " ground=" + (hitGround ? gNoz.ToString("F1") : "-") + " r0=" + r0.ToString("F1") + " flame=" + Lf.ToString("F1") + " jet=" + H.ToString("F1");
            if (column)
            {
                Volume vol = pl.column;
                if (vol == null || !vol.open || vol.dead || vol.pts.Count >= Volume.COLMAX)
                {
                    Pt carry = (vol != null && vol.pts.Count > 0) ? vol.pts[vol.pts.Count - 1] : null;
                    Pt carry2 = (vol != null && vol.pts.Count > 1) ? vol.pts[vol.pts.Count - 2] : null;
                    Volume old = vol;
                    if (old != null && carry2 != null) old.fadeEnd = true;
                    if (old != null) old.open = false;
                    vol = NewVolume(ty);
                    pl.column = vol;
                    if (vol != null)
                    {
                        vol.owner = pl;
                        vol.anchored = true;
                        vol.anchorL = old != null ? old.anchorL : pl.anchorL;   // same pattern across the section boundary
                        if (old != null && carry2 != null) { old.nextSec = vol; vol.prevSec = old; }
                        if (carry2 != null) { vol.pts.Add(carry2.Clone()); vol.fadeStart = true; }
                        if (carry != null) vol.pts.Add(carry.Clone());
                        vol.pts.Add(ColumnPt(pL, gR0, dens, gSq, gMul, gNear, rSt, foot, wideT, rFootT, ty));
                    }
                }
                else
                {
                    // last point = head at the jet end (carried along); far enough from the one before -> it stays in place
                    Pt head = vol.pts[vol.pts.Count - 1];
                    Pt prev = vol.pts.Count >= 2 ? vol.pts[vol.pts.Count - 2] : null;
                    float spacing = Mathf.Max(r0 * Cfg.columnSpacing, 6f);
                    float dist = (pL - (prev != null ? prev.pos : head.pos)).magnitude;
                    if ((pL - head.pos).magnitude > 5000f) { vol.open = false; pl.column = null; }
                    else if (prev == null || dist >= spacing) vol.pts.Add(ColumnPt(pL, gR0, dens, gSq, gMul, gNear, rSt, foot, wideT, rFootT, ty));
                    else
                    {
                        head.pos = pL; head.age = 0f; head.vel = Vector3.zero; head.r0 = gR0; head.dens0 = dens; head.rStart = rSt;
                        head.gMul = gMul; head.gMul0 = gMul; head.squash = gSq; head.sq0 = gSq; head.gNear = gNear;
                        head.foot = foot; head.wideT = wideT; head.rFootT = rFootT; head.wide = 1f;
                    }
                }
            }
            else if (pl.column != null) { pl.column.open = false; pl.column = null; }
            pl.jetActive = pl.column != null && pl.column.open && pl.column.pts.Count > 0;

            // own near-field volume while no column carries the jet (thin air, vacuum)
            if (!pl.jetActive)
            {
                if (pl.jet == null || pl.jet.go == null || pl.jet.dead) { pl.jet = NewVolume(ty); if (pl.jet != null) { pl.jet.jet = true; pl.jet.owner = pl; } }
                if (pl.jet != null)
                {
                    int want = 1 + pl.nVirt;
                    while (pl.jet.pts.Count < want) pl.jet.pts.Add(new Pt());
                    while (pl.jet.pts.Count > want) pl.jet.pts.RemoveAt(pl.jet.pts.Count - 1);
                    SetJetPt(pl.jet.pts[0], pL, rHead, jd * 0.35f, 0.9f, 0.5f);   // without a column the jet thins out
                    for (int j = 0; j < pl.nVirt; j++) { Pt s = pl.vPts[j]; SetJetPt(pl.jet.pts[j + 1], s.pos, s.r0, s.dens0, s.lumpW, s.jetW); }
                }
            }
            else if (pl.jet != null) { pl.jet.open = false; pl.jet.pts.Clear(); pl.jet = null; }

            // give flames, jet pattern, booster jets and fine steps to the volume that draws the near field
            Volume host = pl.jetActive ? pl.column : pl.jet;
            if (host != null)
            {
                host.host = true;
                host.jetOriginL = emitL;
                host.flames.AddRange(pl.flames);
                host.fineA = pl.fineA; host.fineB = pl.fineB; host.fineR = pl.fineR; host.fineStep = pl.fineStep;
            }

            // ----- ground cloud: large billows in the lowest column section (same pass as column foot and jets) -----
            GroundCloud gc = (pl.groundVol != null && !pl.groundVol.dead) ? pl.groundVol.cloud : null;
            if (Cfg.groundCloud && hitGround && rho > 0.02f && pl.column != null)
            {
                Vector3 hitL = bt.InverseTransformPoint(hitW);
                float S = thr * Mathf.Clamp01(1.25f - hitDist / gRange) * Mathf.Clamp01(ty.ground / 2.2f) * ty.strength;
                if (gc != null && (gc.dead || (gc.centerL - hitL).magnitude > gc.Rmax * 1.5f + 10f)) { gc.feeding = false; gc = null; }
                if (gc == null && pl.column.cloud == null)
                {
                    List<Vector3> exitsL = new List<Vector3>();
                    foreach (Vector3 ew in a.trenchW) exitsL.Add(bt.InverseTransformPoint(ew));
                    gc = CreateCloud(hitL, exitsL);
                    pl.column.cloud = gc;
                    pl.groundVol = pl.column;
                }
                if (gc != null)
                {
                    gc.feeding = true;
                    gc.feedS = S;
                    gc.gNoz = gNoz;
                    gc.hitNowL = hitL;
                    gc.Rmax = Mathf.Max(gc.Rmax, Mathf.Max((14f * dEff * Mathf.Sqrt(Mathf.Max(thr, 0.1f)) + 28f * Mathf.Sqrt(tTot / 1000f)) * Cfg.groundSize, gc.exitDist * 0.85f));   // at least as far as the trench exits
                    gc.rJet = Mathf.Max(gc.rJet, dEff * 1.2f + spread);
                    gc.densBase = densF;
                    // firelight at the impact: the cloud glows from inside there
                    if (Cfg.glow > 0f) glows.Add(new Glow { pos = hitL + gc.upL * (gc.rJet * 0.5f), posB = hitL + gc.upL * (gc.rJet * 0.6f), strength = Cfg.glow * (0.6f + 1.0f * boostI) * S, radius = gc.rJet * (2f + 1.5f * boostI) });
                }
            }
            else if (gc != null) gc.feeding = false;
        }

        // Create the ground cloud: impact, orientation and the flame trenches (up to two exits: the farthest one and the one most
        // opposite to it). The smoke parcels are only created while feeding (StepCloud).
        GroundCloud CreateCloud(Vector3 hitL, List<Vector3> exitsL)
        {
            GroundCloud g = new GroundCloud();
            g.centerL = hitL;
            g.hitNowL = hitL;
            g.upL = hitL.normalized;
            g.e1L = Vector3.Cross(g.upL, Mathf.Abs(g.upL.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            g.e2L = Vector3.Cross(g.upL, g.e1L);
            g.life = Cfg.trailLife * 0.3f * UnityEngine.Random.Range(0.9f, 1.1f);
            g.noiseK = new Vector3(UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value) * 50f;
            g.reachF = UnityEngine.Random.Range(0.85f, 1.2f);
            g.heightF = UnityEngine.Random.Range(0.85f, 1.25f);
            g.widthF = UnityEngine.Random.Range(0.85f, 1.2f);
            // one trench side dominates (as in real launches: the big cloud on one side): amount 1.25-1.6 : 1 (the
            // reach only a little - both sides stay long banks), plus higher towers; the weak side lower
            int dom = UnityEngine.Random.value < 0.5f ? 0 : 1;
            float q = Mathf.Sqrt(UnityEngine.Random.Range(1.25f, 1.6f));
            g.exitF[dom] = q * UnityEngine.Random.Range(0.96f, 1.04f);
            g.exitF[1 - dom] = UnityEngine.Random.Range(0.96f, 1.04f) / q;
            g.sideH[dom] = UnityEngine.Random.Range(1.0f, 1.1f);
            g.sideH[1 - dom] = UnityEngine.Random.Range(0.82f, 0.95f);
            g.emitAcc[0] = g.emitAcc[1] = 0.999f;   // the first burst comes at once
            if (exitsL != null && exitsL.Count > 0)
            {
                List<Vector3> dirs = new List<Vector3>(), pts = new List<Vector3>();
                foreach (Vector3 e in exitsL)
                {
                    Vector3 h = Vector3.ProjectOnPlane(e - hitL, g.upL);
                    if (h.magnitude > 3f && h.magnitude < 250f) { dirs.Add(h); pts.Add(e); }
                }
                if (dirs.Count > 0)
                {
                    int i1 = 0;
                    for (int i = 1; i < dirs.Count; i++) if (dirs[i].magnitude > dirs[i1].magnitude) i1 = i;
                    int i2 = i1; float most = 2f;
                    for (int i = 0; i < dirs.Count; i++) { float dd = Vector3.Dot(dirs[i].normalized, dirs[i1].normalized); if (dd < most) { most = dd; i2 = i; } }
                    g.nExit = most < 0.3f ? 2 : 1;   // only one side: everything goes there
                    // emission direction per side slightly turned (+-7 deg): the two clouds are not mirror images
                    g.exitL[0] = pts[i1]; g.exitDir[0] = RotH(g, dirs[i1].normalized, UnityEngine.Random.Range(-0.12f, 0.12f));
                    g.exitL[1] = pts[i2]; g.exitDir[1] = RotH(g, dirs[i2].normalized, UnityEngine.Random.Range(-0.12f, 0.12f));
                    g.exitDist = Mathf.Max(dirs[i1].magnitude, g.nExit == 2 ? dirs[i2].magnitude : 0f);
                }
            }
            // terrain around the impact into the log (heights above the impact point, along and across the trench)
            if (Cfg.debugLog && groundHit != null && bodyT != null)
            {
                Vector3 a1 = g.nExit > 0 ? g.exitDir[0] : g.e1L;
                Vector3 a2 = Vector3.Cross(g.upL, a1);
                string s = "Ground cloud created: trenches ";
                if (g.nExit == 0) s += "none";
                for (int e = 0; e < g.nExit; e++)
                    s += (e > 0 ? ", " : "") + "d=" + Vector3.ProjectOnPlane(g.exitL[e] - hitL, g.upL).magnitude.ToString("F0") + " m h=" + Vector3.Dot(g.exitL[e] - hitL, g.upL).ToString("F1") + " m";
                s += " | ground above impact along:";
                for (int k = -8; k <= 8; k++) s += " " + (k * 10) + ":" + ProbeGround(g, hitL + a1 * (k * 10f)).ToString("F1");
                s += " | across:";
                for (int k = -8; k <= 8; k++) s += " " + (k * 10) + ":" + ProbeGround(g, hitL + a2 * (k * 10f)).ToString("F1");
                Cfg.Log(s);
            }
            return g;
        }

        public string DebugState()
        {
            int vis = 0, pts = 0;
            foreach (Volume v in volumes) { if (v.mr != null && v.mr.enabled) vis++; pts += v.pts.Count; }
            string s = "volumes=" + volumes.Count + " visible=" + vis + " points=" + pts + " rebuilt=" + statUpdated;
            foreach (KeyValuePair<string, Plume> kv in plumes)
            {
                GroundCloud g = kv.Value.groundVol != null ? kv.Value.groundVol.cloud : null;
                s += " | " + kv.Key + ": " + kv.Value.dbg + " col=" + (kv.Value.column != null ? kv.Value.column.pts.Count.ToString() : "-") + " gnd=" + (g != null ? g.R.ToString("F0") + "m" : "-") + " jets=" + kv.Value.nJets;
            }
            return s;
        }

        // close plumes that were not emitted in this frame any more
        public void EndEmit()
        {
            List<string> gone = null;
            foreach (KeyValuePair<string, Plume> kv in plumes)
                if (!kv.Value.touched)
                {
                    Plume pl = kv.Value;
                    pl.jetActive = false;
                    pl.flames.Clear();
                    pl.nJets = 0;
                    if (pl.column != null) pl.column.open = false;
                    if (pl.jet != null) { pl.jet.open = false; pl.jet.pts.Clear(); }
                    if (pl.groundVol != null && pl.groundVol.cloud != null) pl.groundVol.cloud.feeding = false;
                    if (gone == null) gone = new List<string>();
                    gone.Add(kv.Key);
                }
            if (gone != null) foreach (string k in gone) plumes.Remove(k);
        }

        // ---------- motion ----------
        // Young, open and near-camera volumes every frame; old, distant ones collect the time and catch up when recomputed
        static bool IsActive(Volume v)
        {
            if (v.open || v.jet || v.nearCam) return true;
            if (v.cloud != null && (v.cloud.feeding || v.cloud.age < 90f)) return true;
            return v.newestAge < 8f && v.camDist < 3000f;   // young, distant sections: staggered (every 2 frames)
        }

        public void Simulate(float dtUT, float dt, Vector3 windL)
        {
            lastWind = windL;
            if (dt > 0f) foreach (Plume pl in plumes.Values) if (pl.noiseInit) pl.anchorL += windL * (Cfg.windDrift * dt);
            for (int vi = 0; vi < volumes.Count; vi++)
            {
                Volume v = volumes[vi];
                if (v.dead) continue;
                v.accUT += dtUT;
                v.accDt += dt;
                if (IsActive(v)) SimVolume(v);
            }
            RemoveDead();
        }

        void SimVolume(Volume v)
        {
            float dtUT = v.accUT, dt = v.accDt;
            v.accUT = 0f; v.accDt = 0f;
            if (v.jet) { if (v.pts.Count == 0 && !v.open) v.dead = true; return; }
            // catch up on a long accumulated time in sub-steps (motion stays stable)
            int nsub = dt > 0.5f ? Mathf.Min(Mathf.CeilToInt(dt / 0.5f), 120) : 1;
            for (int s = 0; s < nsub; s++)
            {
                SimPoints(v, dtUT / nsub, dt / nsub);
                if (v.cloud != null) { StepCloud(v.cloud, dtUT / nsub, dt / nsub); if (v.cloud.dead) v.cloud = null; }
            }
            if (v.pts.Count == 0 && v.cloud == null && !v.open) { v.dead = true; return; }
            if (!v.open && v.owner != null)
            {
                Decimate(v);
                TryMerge(v);
            }
        }

        void SimPoints(Volume v, float dtUT, float dt)
        {
            Vector3 wv = Vector3.zero;
            if (dt > 0f && v.pts.Count > 0)
            {
                // the noise reference drifts with the wind at the altitude of this section (the pattern sticks to the smoke); the column
                // foot drifts more strongly, like the ground cloud
                Pt mid = v.pts[v.pts.Count / 2];
                wv = ShearWind(lastWind, mid.pos.normalized, mid.pos.magnitude - mid.rMin) * Mathf.Lerp(Cfg.windDrift, Cfg.groundDrift, mid.foot);
                if (v.anchored && !v.jet) v.anchorL += wv * dt;
            }
            for (int i = v.pts.Count - 1; i >= 0; i--)
            {
                Pt p = v.pts[i];
                p.age += dtUT;
                if (p.age >= p.life) { v.pts.RemoveAt(i); continue; }
                if (dt <= 0f) continue;
                // smoke born on the ground: flat and slowed while the rocket is low (it stays visible); once the rocket is
                // far enough above (or the plume has ended), it swells and becomes round - the column foot gets thick like the Shuttle's
                if (p.gNear > 0f)
                {
                    Plume ow = v.owner;
                    float rel = 1f;
                    if (ow != null && ow.touched && ow.jetH > 0f) rel = Mathf.SmoothStep(0f, 1f, ((ow.emitL - p.pos).magnitude - 1.5f * ow.jetH) / (3f * ow.jetH));
                    float gT = Mathf.Lerp(p.gMul0, Mathf.Max(p.gMul0, 1f + 0.2f * p.gNear), rel);
                    if (gT > p.gMul) p.gMul = Mathf.MoveTowards(p.gMul, gT, 0.7f * dt);
                    float sT = Mathf.Lerp(p.sq0, Mathf.Max(p.sq0, 0.85f), rel);
                    if (sT > p.squash) p.squash = Mathf.MoveTowards(p.squash, sT, 0.45f * dt);
                }
                // column foot: swells really wide as soon as the rocket is more than its target size above it (the rocket stays
                // visible), and fills the middle between the banks of the ground cloud
                if (p.wideT > 1.001f && p.wide < p.wideT)
                {
                    Plume ow = v.owner;
                    float rel = 1f;
                    if (ow != null && ow.touched && ow.jetH > 0f)
                        rel = Mathf.SmoothStep(0f, 1f, ((ow.emitL - p.pos).magnitude - (ow.jetH + 1.2f * p.rFootT)) / (ow.jetH + p.rFootT));
                    float wT = Mathf.Lerp(1f, p.wideT, rel);
                    if (wT > p.wide) p.wide = Mathf.MoveTowards(p.wide, wT, 0.45f * dt);
                }
                Vector3 up = p.pos.normalized;
                Vector3 w = ShearWind(lastWind, up, p.pos.magnitude - p.rMin) * Mathf.Lerp(Cfg.windDrift, Cfg.groundDrift, p.foot);
                p.vel += (w - p.vel) * Mathf.Clamp01(p.drag * dt);   // smoke drifts only weakly with the wind
                p.vel += up * (p.rise * Mathf.Exp(-p.age / 40f) * dt);
                p.pos += p.vel * dt;
                float r = Radius(p, v.growth);
                float minR = p.rMin + r * p.squash * 0.6f;
                float rr = p.pos.magnitude;
                if (rr < minR) { p.pos = p.pos / rr * minR; float vr = Vector3.Dot(p.vel, up); if (vr < 0f) p.vel -= up * vr; }
            }
            v.newestAge = v.pts.Count > 0 ? v.pts[v.pts.Count - 1].age : 1e9f;
        }

        // old sections: the billows are much larger than their spacing -> drop every second inner point
        // (the first and last two stay: they belong to the crossfade with the neighbouring sections)
        static void Decimate(Volume v)
        {
            int n = v.pts.Count;
            if (n <= 6 || v.newestAge < 60f) return;
            int m = n / 2;
            float sp = (v.pts[m].pos - v.pts[m - 1].pos).magnitude;
            if (Radius(v.pts[m], v.growth) < 2f * sp) return;
            for (int i = n - 3; i >= 2; i -= 2) v.pts.RemoveAt(i);
        }

        // merge two old, thinned-out neighbouring sections into one (fewer volumes, less overlap)
        void TryMerge(Volume v)
        {
            Volume nx = v.nextSec;
            if (nx == null || nx.dead || nx.open || !v.fadeEnd || !nx.fadeStart || nx.cloud != null) return;
            if (v.newestAge < 60f) return;
            if (v.pts.Count < 2 || nx.pts.Count < 2 || v.pts.Count + nx.pts.Count - 2 > Volume.MERGEMAX) return;
            // bring the neighbours up to date
            if (nx.accDt > 0f || nx.accUT > 0f)
            {
                int ns = nx.accDt > 0.5f ? Mathf.Min(Mathf.CeilToInt(nx.accDt / 0.5f), 120) : 1;
                for (int s = 0; s < ns; s++) SimPoints(nx, nx.accUT / ns, nx.accDt / ns);
                nx.accUT = 0f; nx.accDt = 0f;
            }
            if (nx.pts.Count < 2 || v.pts.Count + nx.pts.Count - 2 > Volume.MERGEMAX) return;
            v.pts.RemoveRange(v.pts.Count - 2, 2);
            v.pts.AddRange(nx.pts);
            v.fadeEnd = nx.fadeEnd;
            v.nextSec = nx.nextSec;
            if (v.nextSec != null) v.nextSec.prevSec = v;
            nx.prevSec = null; nx.nextSec = null;
            nx.pts.Clear();
            nx.dead = true;
            v.newestAge = v.pts[v.pts.Count - 1].age;
            v.lastUpd = int.MinValue / 2;   // redraw at once
        }

        const int MAXPARCELS = 80;        // smoke parcels per ground cloud in total (fast stream + calm ones)
        const int MAXCALM = 36;           // calm parcels: above this the most redundant ones merge
        const float SQFAST = 0.36f;       // fast stream: flat layer along the ground (wall jet), only the head gets tall

        // rotate the horizontal direction about the vertical
        static Vector3 RotH(GroundCloud g, Vector3 d, float a)
        {
            return d * Mathf.Cos(a) + Vector3.Cross(g.upL, d) * Mathf.Sin(a);
        }

        void AddParcel(GroundCloud g, int stream, Vector3 pos, Vector3 vel, float r)
        {
            if (g.lobes.Count >= MAXPARCELS) return;   // (practically never happens: the calm ones are limited all the time)
            g.lobes.Add(new Lobe { stream = stream, seq = g.seqNext++, pos = pos, origin = pos, vel = vel, v0 = Mathf.Max(vel.magnitude, 1f), r = r, squash = SQFAST,
                capF = UnityEngine.Random.Range(0.75f, 1.25f), sqF = UnityEngine.Random.Range(0.85f, 1.15f), growF = UnityEngine.Random.Range(0.8f, 1.2f) });
        }

        // Make room: the most hidden calm parcel (the one sitting deepest, relative to its size, inside a neighbour of about
        // the same size or larger) merges into it - it shrinks in place, the neighbour grows by its volume. So redundant
        // parcels in the crowd at the head disappear first, not the small, flat ones at the pad
        // (the low part of the wedge)
        static bool MergeRedundant(GroundCloud g)
        {
            Lobe sm = null, best = null;
            float bq = -1e9f;
            int n = g.lobes.Count;
            for (int i = 0; i < n; i++)
            {
                Lobe A = g.lobes[i];
                if (A.dead || A.into != null || A.Streaming) continue;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    Lobe B = g.lobes[j];
                    if (B.dead || B.into != null || B.Streaming || B.r < A.r * 0.7f) continue;
                    float q = (A.r + B.r - (B.pos - A.pos).magnitude) / Mathf.Max(A.r, 0.1f);
                    if (q > bq) { bq = q; sm = A; best = B; }
                }
            }
            if (sm == null) return false;
            sm.into = best;
            return true;
        }

        // horizontal distance from the cloud centre relative to the reach (0 = pad, ~1 = front)
        static float XN(GroundCloud g, Vector3 pos, float Rm)
        {
            return Vector3.ProjectOnPlane(pos - g.centerL, g.upL).magnitude / (0.9f * Rm);
        }

        // Final size of a parcel: wedge - small, flat parcels at the pad, large towers further out. Plus the wedge line:
        // the cloud may only be low at the pad and rises outward (height 8 m + 0.6 x distance, top of a
        // parcel ~1.55 r sq) - otherwise the big billows reach up to the rocket (two round balls, a "dumbbell")
        // With flame trenches only the trench streams build the high towers (long banks on both sides, as in real
        // launches); the smoke flowing over the pad from the impact all around stays a lower collar - otherwise, depending on
        // the view direction, a tall tower stands between viewer and rocket. Without trenches it is the whole cloud (ring).
        static bool Apron(GroundCloud g, Lobe L) { return g.nExit > 0 && L.stream >= 2; }

        float CapAt(GroundCloud g, Lobe L, float rCap, float Rm)
        {
            float hz = Vector3.ProjectOnPlane(L.pos - g.centerL, g.upL).magnitude;
            float xN = hz / (0.9f * Rm);
            float hS = L.stream < 2 ? g.sideH[L.stream] : 1f;
            bool apron = Apron(g, L);
            // the cloud keeps growing for a long time (~60 % larger after one and a half minutes): it drifts and spreads
            float ageF = 1f + 0.6f * Mathf.SmoothStep(0f, 1f, (L.age - 8f) / 90f);
            float cap = rCap * L.capF * Mathf.Lerp(0.42f, 1.15f, Mathf.SmoothStep(0f, 1f, (xN - 0.2f) / 0.9f)) * (apron ? 0.8f : 1f) * ageF;
            float wedge = (8f + (apron ? 0.35f : 0.6f) * hz) * g.heightF * hS * ageF / (1.55f * Mathf.Max(SqRest(g, L, xN), 0.5f));
            return Mathf.Min(cap, wedge);
        }

        // shape of a calm parcel: flat at the pad, a tower further out (taller than wide); the collar stays flatter
        static float SqRest(GroundCloud g, Lobe L, float xN)
        {
            float hS = L.stream < 2 ? g.sideH[L.stream] : 1f;
            float w = Mathf.SmoothStep(0f, 1f, (xN - 0.25f) / 0.85f);
            if (Apron(g, L)) return Mathf.Min(Mathf.Lerp(0.5f, 0.85f * g.heightF, w) * L.sqF, 0.95f);
            return Mathf.Min(Mathf.Lerp(0.5f, 1.25f * g.heightF * hS, w) * L.sqF, 1.4f);
        }

        // ground height under a parcel (above the impact plane): median of 5 samples - holes like the flame shaft and
        // single buildings do not count. upper: second-highest sample
        float[] h5 = new float[5];
        float GroundUnder(GroundCloud g, Lobe L, Vector3 hp, float r, float speed, bool upper)
        {
            if (groundHit == null || bodyT == null) return L.gInit ? L.gH : 0f;
            float dd = Mathf.Min(r * 0.4f, 10f) + 4f;
            float mv = Mathf.Max(Mathf.Max(2.5f, 0.08f * r), 0.06f * speed);
            if (L.gInit && (hp - L.gQ).sqrMagnitude < mv * mv && Mathf.Abs(dd - L.gD) < 0.25f * dd) return L.gH;
            h5[0] = ProbeGround(g, hp);
            h5[1] = ProbeGround(g, hp + g.e1L * dd);
            h5[2] = ProbeGround(g, hp - g.e1L * dd);
            h5[3] = ProbeGround(g, hp + g.e2L * dd);
            h5[4] = ProbeGround(g, hp - g.e2L * dd);
            Array.Sort(h5);
            L.gH = upper ? h5[3] : h5[2];
            L.gQ = hp; L.gD = dd; L.gInit = true;
            return L.gH;
        }

        // vertically from above onto the ground (terrain, pad; vessels do not count): height above the impact plane
        float ProbeGround(GroundCloud g, Vector3 pL)
        {
            Vector3 hitW; float dist;
            if (groundHit(bodyT.TransformPoint(pL + g.upL * 90f), bodyT.TransformDirection(-g.upL), 180f, out hitW, out dist))
                return Mathf.Clamp(90f - dist, -40f, 40f);
            return 0f;
        }

        // Ground cloud (reference: Shuttle launch STS-129): smoke parcels shoot out with the pressure of the jet - while the rocket
        // stands on the ground mostly out of the flame trenches (right under it stays clear), as it climbs more and more from the
        // impact over the pad and slower (then smoke stays in the middle too). They brake, swell (entrained air),
        // rise (warm) and pile up at the head: there they merge and tower up. Afterwards the cloud keeps
        // spreading for a while.
        void StepCloud(GroundCloud g, float dtUT, float dt)
        {
            g.age += dtUT;
            if (g.feeding) g.tLeave = 0f; else g.tLeave += dtUT;
            float S = g.feeding ? g.feedS : 0f;
            g.M += S * dtUT;
            if (g.age > g.life && !g.feeding) { g.dead = true; return; }
            Vector3 up = g.upL;
            float Rm = Mathf.Max(g.Rmax, 10f);
            float diss = 1f - Mathf.SmoothStep(0f, 1f, (g.age / g.life - 0.55f) / 0.45f);
            float b = g.densBase * diss * 1.3f;                                                  // dense like real launch clouds
            float ft = g.nExit > 0 ? 1f - Mathf.SmoothStep(0f, 1f, (g.gNoz - 20f) / 50f) : 0f;   // share through the trenches (up to ~70 m altitude)
            float press = Mathf.Clamp01(1.3f - g.gNoz / 60f);                                    // pressure on the ground
            float r0 = g.rJet * 1.1f + 2.5f;                                                     // thick parcels: much smoke, a wide stream
            float burst = 1f + 1.2f * Mathf.Exp(-g.age / 0.7f);                                  // first burst: dense stream

            // ----- new parcels -----
            if (dtUT > 0f && S > 0.01f)
            {
                for (int e = 0; e < g.nExit; e++)
                {
                    g.emitAcc[e] += dtUT * 8f * burst * S * ft * g.exitF[e];
                    while (g.emitAcc[e] >= 1f)
                    {
                        g.emitAcc[e] -= 1f;
                        // different momentum: the parcels come to rest along the whole path (connected from the
                        // exit to the head), the fastest form the head. First the front shoots out (flat and
                        // fast along the ground), then the path fills up
                        Vector3 d = RotH(g, g.exitDir[e], UnityEngine.Random.Range(-0.6f, 0.6f) * g.widthF);
                        float u = UnityEngine.Random.value;
                        if (g.age < 0.5f) u = Mathf.Lerp(0.55f, 1f, u);
                        float v0 = Rm * (0.3f + 1.9f * u) * (0.55f + 0.45f * S) * g.reachF * Mathf.Pow(g.exitF[e], 0.3f);   // reach: both sides a long bank
                        AddParcel(g, e, g.exitL[e] + up * (r0 * 0.2f), d * v0, r0 * 0.85f);
                    }
                }
                // from the impact all around: directions evenly spread (golden angle), reaches scattered - one
                // connected blanket instead of single clouds; without trenches this is the whole ground cloud
                g.emitAcc[2] += dtUT * (g.nExit > 0 ? 9f : 12f) * S * (1f - 0.85f * ft);
                while (g.emitAcc[2] >= 1f)
                {
                    g.emitAcc[2] -= 1f;
                    g.radialK++;
                    float a = g.radialK * 2.39996f + UnityEngine.Random.Range(-0.3f, 0.3f) + g.radialPhase;
                    a -= 6.2832f * Mathf.Floor(a / 6.2832f);
                    Vector3 d = g.e1L * Mathf.Cos(a) + g.e2L * Mathf.Sin(a);
                    float v0 = Rm * (0.15f + 1.2f * UnityEngine.Random.value) * (0.25f + 0.75f * press) * S * g.reachF;
                    AddParcel(g, 2 + (Mathf.FloorToInt(a / 0.7854f) & 7), g.hitNowL + d * (g.rJet * 0.5f) + up * (r0 * 0.2f), d * v0, r0 * 1.0f);
                }
            }

            // ----- motion -----
            Vector3 wh = Vector3.ProjectOnPlane(lastWind, up) * Cfg.groundDrift;   // the cloud drifts with the surface wind
            if (dt > 0f) g.centerL += wh * dt;
            float rCap = Mathf.Max(Rm * 0.55f, 8f);   // big billows
            for (int i = 0; i < g.lobes.Count; i++)
            {
                Lobe L = g.lobes[i];
                if (L.dead) continue;
                if (L.into != null)
                {
                    // merges into another parcel: shrinks in place (moves after it slowly at most - no jump
                    // across the pad), the target grows by its volume
                    Lobe T = L.into;
                    int guard = 0;
                    while (T.into != null && guard++ < 64) T = T.into;
                    if (T.dead || T == L) { L.into = null; }
                    else
                    {
                        L.into = T;
                        if (dtUT > 0f)
                        {
                            Vector3 dv = T.pos - L.pos;
                            float dl = dv.magnitude;
                            if (dl > 0.01f) L.pos += dv / dl * Mathf.Min(dl, 3f * dtUT);
                            float rOld = L.r;
                            L.r *= Mathf.Exp(-dtUT / 0.8f);
                            float add = rOld * rOld * rOld - L.r * L.r * L.r;
                            T.r = Mathf.Min(Mathf.Pow(T.r * T.r * T.r + add, 1f / 3f), Mathf.Max(T.r, CapAt(g, T, rCap, Rm)));
                        }
                        // only remove it when tiny (and faded out by then) - otherwise a remainder still sticking out pops away.
                        // shape and density glide to the target (if the target itself merges into another one, it switches - without a jump)
                        if (dtUT > 0f)
                        {
                            float kf = Mathf.Clamp01(dtUT / 0.4f);
                            L.dens += (T.dens * Mathf.SmoothStep(0f, 1f, L.r / Mathf.Max(0.3f * T.r, 0.01f)) - L.dens) * kf;
                            L.squash += (T.squash - L.squash) * kf;
                        }
                        if (L.r < 0.08f * T.r || L.r < 0.8f) L.dead = true;
                        continue;
                    }
                }
                if (dtUT <= 0f) continue;
                L.age += dtUT;
                Vector3 vh = Vector3.ProjectOnPlane(L.vel, up);
                float vz = Vector3.Dot(L.vel, up);
                float sp = vh.magnitude;
                vh *= Mathf.Exp(-dtUT / (L.stream < 2 ? 0.8f : 1.0f));
                // after braking the cloud keeps spreading for a while: fanning out from the emission point (also sideways)
                Vector3 ow = Vector3.ProjectOnPlane(L.pos - L.origin, up);
                float ol = ow.magnitude;
                if (ol > 0.5f)
                {
                    ow /= ol;
                    // even widening (slow inside, faster outside): the middle does not empty. Decays slowly
                    // (the cloud does not stop quickly, it keeps spreading for a long time)
                    float vs = 2.4f * Mathf.Exp(-L.age / 40f) * (0.4f + Rm / 60f) * Mathf.Clamp(ol / (0.6f * Rm), 0.15f, 1.0f) * g.reachF;
                    float vo = Vector3.Dot(vh, ow);
                    if (vo < vs) vh += ow * ((vs - vo) * Mathf.Clamp01(dtUT / 1f));
                }
                // buoyancy: slow parcels rise (big ones faster), weakens over time
                float slow = 1f - Mathf.SmoothStep(0f, 1f, sp / 25f);
                float wT = 3.2f * Mathf.Sqrt(Mathf.Max(L.r, 1f) / 12f) * Mathf.Exp(-L.age / 40f) * slow;
                vz += (wT - vz) * Mathf.Clamp01(dtUT / 1.5f);
                L.vel = vh + up * vz;
                L.pos += (L.vel + wh) * dtUT;
                // swelling: entrained air (with the distance travelled) and over time. Wedge: at the pad the parcels stay small and
                // flat, further out (where the jet blows everything) the cloud towers up - larger billows, taller than wide
                L.r = Mathf.Min(L.r + (0.28f * sp + 3.2f / (1f + L.age / 45f)) * L.growF * dtUT, Mathf.Max(L.r, CapAt(g, L, rCap, Rm)));
                float sqRest = SqRest(g, L, XN(g, L.pos, Rm));
                // while the parcel is fast, a flat layer along the ground (wall jet: grows mostly in
                // width); once it has lost most of its momentum, it rolls up - the head towers up
                // (stays rolled up, even if the late drifting apart speeds it up a little again)
                L.roll = Mathf.Max(L.roll, Mathf.Max(Mathf.SmoothStep(0f, 1f, (0.6f - sp / L.v0) / 0.35f), Mathf.SmoothStep(0f, 1f, (L.age - 1.5f) / 1.5f)));
                float sqT = Mathf.Lerp(SQFAST, sqRest, L.roll);
                L.squash += (sqT - L.squash) * Mathf.Clamp01(dtUT / (sqT > L.squash ? 0.45f : 0.2f));
                // lies on the visible ground (not below it); the fast stream also follows it downward
                float zc = Vector3.Dot(L.pos - g.centerL, up);
                Vector3 hp = L.pos - up * zc;
                float gh = GroundUnder(g, L, hp, L.r, sp, false);
                float minZ = gh + L.r * L.squash * 0.55f;
                // rise only so far that the parcel stays connected to the ground (no floating billows): the cloud
                // gets taller because the parcels swell and grow together at the head
                // the underside stays on the ground (otherwise the cloud floats): the height comes from size and tower shape
                float maxZ = minZ + L.r * L.squash * (0.1f + 0.35f * Mathf.SmoothStep(0f, 1f, (L.age - 2f) / 20f) + 0.25f * Mathf.SmoothStep(0f, 1f, (L.age - 20f) / 80f));
                if (zc < minZ) { L.pos += up * (minZ - zc); if (vz < 0f) L.vel = vh; }
                else if (zc > maxZ) { L.pos -= up * (zc - maxZ); if (vz > 0f) L.vel = vh; }
                else if (sp > 12f && zc > minZ + 0.5f) L.pos -= up * Mathf.Min(zc - minZ, 5f * dtUT);
                // thins out a little over time (grows and drifts)
                L.dens = b * Mathf.SmoothStep(0f, 1f, L.age / 0.12f) * (1f - 0.3f * Mathf.SmoothStep(0f, 1f, (L.age - 20f) / 120f));
            }

            // ----- cohesion: a calm parcel that loses contact with its nearest neighbour moves towards it
            // (the cloud stays one mass even as it spreads) -----
            if (dtUT > 0f)
                for (int i = 0; i < g.lobes.Count; i++)
                {
                    Lobe A = g.lobes[i];
                    if (A.dead || A.into != null || A.Streaming) continue;
                    Lobe nb = null; float bestEx = 1e9f;
                    for (int j = 0; j < g.lobes.Count; j++)
                    {
                        if (j == i) continue;
                        Lobe B = g.lobes[j];
                        if (B.dead || B.into != null) continue;
                        float ex = (B.pos - A.pos).magnitude - (A.r + B.r);
                        if (ex < bestEx) { bestEx = ex; nb = B; }
                    }
                    if (nb == null) continue;
                    Vector3 dv = nb.pos - A.pos;
                    float dl = dv.magnitude;
                    float gap = dl - 0.7f * (A.r + nb.r);
                    if (gap > 0f && dl > 0.01f) A.pos += dv / dl * Mathf.Min(gap, 2.5f * dtUT);
                }

            // ----- clear zone around the rocket: while it is low and the boosters burn, the jet blows the smoke aside.
            // Calm parcels that grow or drift into the clear cone around the axis are pushed out (not the fast stream
            // from the trenches - it shoots out anyway). The cone: flat, low parcels may come close to the pad
            // (smoke on the ground does not hide the rocket), tall towers stay further away. As the rocket climbs, it closes
            // within a few seconds -----
            if (dtUT > 0f)
            {
                float cT = g.feeding ? S * (1f - Mathf.SmoothStep(0f, 1f, (g.gNoz - 25f) / 50f)) : 0f;
                g.clearC = cT > g.clearC ? cT : Mathf.MoveTowards(g.clearC, cT, dtUT / 3f);
                if (g.clearC > 0.01f)
                    foreach (Lobe L in g.lobes)
                    {
                        if (L.dead || L.Streaming) continue;
                        Vector3 off = Vector3.ProjectOnPlane(L.pos - g.hitNowL, up);
                        float dl = off.magnitude;
                        float top = Vector3.Dot(L.pos - g.centerL, up) + L.r * L.squash;
                        // including warp and knobs (the visible edge reaches beyond r); this margin stays until the
                        // clear zone has closed completely (otherwise the parcels move up to the axis as the rocket climbs and hide it)
                        float need = g.clearC * (0.8f * g.rJet + 3f + 0.5f * Mathf.Max(0f, top - 2f)) + 1.25f * L.r;
                        if (dl >= need) continue;
                        Vector3 dir = dl > 0.1f ? off / dl : RotH(g, g.e1L, L.seq * 2.39996f);
                        L.pos += dir * Mathf.Min(need - dl, 25f * dtUT);
                        float vin = -Vector3.Dot(L.vel, dir);
                        if (vin > 0f) L.vel += dir * vin;
                    }
            }

            // ----- the parcels pile up at the head: a calm one that sits largely inside a bigger one merges into it -----
            int n = g.lobes.Count;
            for (int i = 0; i < n; i++)
            {
                Lobe A = g.lobes[i];
                if (A.dead || A.into != null || A.Streaming) continue;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    Lobe B = g.lobes[j];
                    if (B.dead || B.into != null || B.Streaming || B.r > A.r) continue;
                    float lim = A.r * 0.75f - B.r * 0.15f;
                    if (lim > 0f && (B.pos - A.pos).sqrMagnitude < lim * lim) B.into = A;
                }
            }
            // ----- limit the calm parcels (if the rocket stays on the ground, new ones keep coming) -----
            int calm = 0;
            foreach (Lobe L in g.lobes) if (!L.dead && L.into == null && !L.Streaming) calm++;
            for (int k = 0; k < 8 && calm > MAXCALM; k++) { if (!MergeRedundant(g)) break; calm--; }
            for (int i = g.lobes.Count - 1; i >= 0; i--) if (g.lobes[i].dead) g.lobes.RemoveAt(i);

            // key figures: typical parcel size (for the noise), extent (log)
            float s3 = 0f, s4 = 0f, ext = 0f;
            foreach (Lobe L in g.lobes)
            {
                float r3 = L.r * L.r * L.r;
                s3 += r3; s4 += r3 * L.r;
                ext = Mathf.Max(ext, Vector3.ProjectOnPlane(L.pos - g.centerL, up).magnitude + L.r);
            }
            float rWt = s3 > 0f ? s4 / s3 : r0;
            if (g.rW <= 0f) g.rW = rWt;
            else if (rWt > g.rW && dtUT > 0f) g.rW += (rWt - g.rW) * Mathf.Clamp01(dtUT / 4f);   // only grow, slowly: the pattern does not pump
            g.rW = Mathf.Max(g.rW, 0.33f * Rm);   // the expected billow size from the start: the heads swell at once (no smooth balloons)
            g.noiseS = 0.65f / Mathf.Max(g.rW, 2f);
            g.R = ext;
        }

        // ---------- drawing ----------
        public void Render(Transform bt, Vector3 camPos, Vector3 sunW, float time)
        {
            bodyT = bt;
            frame++;
            Shader.SetGlobalFloat("_PFXTime", time);
            // flow in the jet and in the flame (frame time, smooth even without a new physics step)
            float dtR = lastRenderTime < 0f ? 0f : Mathf.Clamp(time - lastRenderTime, 0f, 0.1f);
            lastRenderTime = time;
            if (dtR > 0f)
                foreach (Plume pl in plumes.Values)
                {
                    if (!pl.noiseInit) continue;
                    pl.jetPhase = Wrap50(pl.jetPhase + pl.dirL * (pl.flowSpeed * dtR * pl.jetScale));
                    pl.flamePhase = Wrap50(pl.flamePhase + pl.dirL * (pl.flameSpeed * dtR * pl.flameScaleA));
                }
            Vector3 camL = bt.InverseTransformPoint(camPos);
            Vector3 sunL = bt.InverseTransformDirection(sunW).normalized;
            int n = volumes.Count;
            int budget = Cfg.updateBudget;
            statUpdated = 0;
            if (n > 0) rrStart = ((rrStart % n) + n) % n;
            for (int k = 0; k < n; k++)
            {
                Volume v = volumes[(rrStart + k) % n];
                if (v.dead) continue;
                bool upd = forceAll || IsActive(v) || frame - v.lastUpd > 100000;
                if (!upd && budget > 0)
                {
                    float dist = Mathf.Max(0f, (v.boxC - camL).magnitude - v.radiusMax);
                    int interval = (dist < 2000f || v.newestAge < 8f) ? 2 : dist < 10000f ? 4 : dist < 50000f ? 8 : 16;
                    if (frame - v.lastUpd >= interval) { upd = true; budget--; }
                }
                if (upd)
                {
                    if (v.accUT > 0f || v.accDt > 0f) SimVolume(v);   // catch up on accumulated time
                    if (v.dead) { v.mr.enabled = false; continue; }
                    v.drawable = BuildVolume(v, sunL);
                    v.lastUpd = frame;
                    statUpdated++;
                }
                v.mr.enabled = v.drawable && Visible(v, camL);
                v.camDist = Mathf.Max(0f, (v.boxC - camL).magnitude - v.radiusMax);
                v.nearCam = v.camDist < 1500f;
            }
            rrStart += Mathf.Max(1, Cfg.updateBudget);
            RemoveDead();
        }

        // distance and horizon: if the planet lies between camera and smoke (view from orbit), do not draw
        bool Visible(Volume v, Vector3 camL)
        {
            Vector3 d = v.boxC - camL;
            float dist = d.magnitude;
            if (dist - v.radiusMax > Cfg.maxDistance) return false;
            if (camL.magnitude > bodyRadius && dist > 1f)
            {
                float t = -Vector3.Dot(camL, d) / (dist * dist);
                if (t > 0f && t < 1f && (camL + d * t).magnitude < bodyRadius * 0.999f - v.radiusMax) return false;
            }
            return true;
        }

        void SetBox(Volume v, Vector3 mn, Vector3 mx)
        {
            v.boxC = (mn + mx) * 0.5f;
            v.boxS = Vector3.Max(mx - mn, new Vector3(0.01f, 0.01f, 0.01f));
            v.radiusMax = v.boxS.magnitude * 0.5f;
            Transform tr = v.go.transform;
            if (bodyT != null && tr.parent != bodyT) tr.SetParent(bodyT, false);
            tr.localPosition = v.boxC;
            tr.localRotation = Quaternion.identity;
            tr.localScale = v.boxS;
        }

        void AddP(Vector3 pos, float r, float d, float sq, float lw, float jw, float yw, float eb, bool link, int cut, float cs = 0f)
        {
            if (eN >= Volume.MAXP) return;
            eP[eN] = pos; eR[eN] = r; eD[eN] = d; eSq[eN] = sq; eLw[eN] = lw; eJw[eN] = jw; eYw[eN] = yw; eEb[eN] = eb; eLink[eN] = link; eCut[eN] = cut; eCs[eN] = cs;
            eN++;
        }

        // Assemble the points: chain of the column (+ near field at the head), booster jets, billows of the ground cloud.
        // Everything in ONE volume: one pass through the smoke, no ordering errors.
        bool BuildVolume(Volume v, Vector3 sunL)
        {
            Plume ow = v.owner;
            if (ow != null && ow.noiseInit)
            {
                v.noiseSeed = ow.noiseSeed;
                if (v.jet)
                {
                    v.noiseScale = ow.noiseScale; v.noiseK = Vector3.zero; v.noiseKY = Vector3.zero;
                    v.anchored = true; v.anchorL = ow.anchorL;
                }
                else if (v.pts.Count > 0)
                {
                    // the billows grow with the spreading smoke: enlarge the pattern about the box centre
                    // (move the reference point without a jump: the offset in noise space compensates for it)
                    float sD = ow.noiseScale / Mathf.Pow(LateSpread(v.pts[v.pts.Count / 2].age), 0.8f);
                    if (v.noiseScale <= 0f || v.boxS == Vector3.zero) v.noiseScale = sD;
                    else if (Mathf.Abs(sD - v.noiseScale) > v.noiseScale * 0.001f)
                    {
                        v.noiseK = Wrap50(v.noiseK + (v.boxC - v.anchorL) * v.noiseScale);
                        v.noiseKY = Wrap50(v.noiseKY + (v.boxC - v.anchorL) * (v.noiseScale * 2.5f));
                        v.anchorL = v.boxC;
                        v.noiseScale = sD;
                    }
                }
            }
            if (!v.anchored && v.pts.Count > 0)
            {
                v.anchored = true;
                v.anchorL = v.pts[0].pos;
                float r0s = 0f; foreach (Pt q in v.pts) r0s += q.r0;
                v.noiseScale = 1f / Mathf.Max(r0s / v.pts.Count * (1f + v.growth * 4f) * 1.3f, 1f);
            }

            eN = 0;
            float rSum = 0f, rMinV = 1e9f, densMax = 0f;
            int nStat = 0;
            bool virt = v.host && ow != null && ow.column == v && ow.jetActive && v.pts.Count > 0 && ow.nVirt > 0;
            int nReal = Mathf.Min(v.pts.Count, Volume.MAXP);
            // 1. chain of the column (with its own near field: its fixed points)
            for (int i = 0; i < nReal; i++)
            {
                Pt p = v.pts[i];
                bool link = i < nReal - 1 || virt;
                float r, dens, sq, lw, jw, yw, eb, cs = 0f;
                if (v.jet)
                {
                    // jet points: fixed shape (no swelling), density directly
                    r = p.r0; dens = p.dens0; sq = 1f; lw = p.lumpW; jw = p.jetW; yw = 1f; eb = 0f;
                }
                else
                {
                    r = Radius(p, v.growth);
                    float ls = LateSpread(p.age);
                    float lf = p.age / p.life;
                    float fade = Mathf.Clamp01((p.age + 0.02f) / p.fadeIn) * (1f - Mathf.SmoothStep(0f, 1f, (lf - 0.55f) / 0.45f));
                    // late spreading thins the smoke (the trail turns translucent before it disappears)
                    dens = p.dens0 * fade * Mathf.Pow(p.r0 * ls / r, 0.6f) / Mathf.Pow(ls, 1.6f);
                    dens *= Mathf.Pow(p.wide, 0.6f);                             // a wide column foot stays as dense as the ground cloud
                    dens *= 1f + 0.6f * Mathf.Exp(-p.age / 2f);                 // young smoke denser: seamless with the dense jet
                    if (ow != null && ow.flameI > 0f) dens *= 1f - 0.5f * ow.flameI * Mathf.Exp(-p.age / 0.6f);   // blazing jet on the ground: the smoke only forms behind it
                    sq = p.squash;
                    lw = 1f;
                    jw = virt ? 0.25f * Mathf.Exp(-p.age / 0.6f) : 0f;          // column head: still some jet pattern
                    yw = Mathf.Exp(-p.age / 4f);                                // young smoke: medium-sized eddies
                    eb = 0.2f * Mathf.SmoothStep(0f, 1f, Mathf.Max((p.age - 120f) / 900f, (lf - 0.5f) / 0.5f));  // old smoke frays only slightly (stays connected)
                    // style of the ground cloud (displacement instead of added bulges: large connected billows, no shreds);
                    // fades in during the first second - the transition at the nozzle stays as before
                    cs = Cfg.columnCloudStyle * Mathf.SmoothStep(0f, 1f, p.age / 1.2f);
                }
                rSum += r; rMinV = Mathf.Min(rMinV, r); nStat++;
                densMax = Mathf.Max(densMax, dens);
                AddP(p.pos, r, dens, sq, lw, jw, yw, eb, link, -1, cs);
            }
            int fadeIn = (v.fadeStart && nReal >= 2) ? 0 : -1;
            int fadeOut = (v.fadeEnd && !virt && nReal >= 2) ? nReal - 2 : -1;
            // 2. near field at the column head (virtual points up to the nozzle or the confluence)
            if (virt)
            {
                for (int j = 0; j < ow.nVirt; j++)
                {
                    Pt q = ow.vPts[j];
                    AddP(q.pos, q.r0, q.dens0, 1f, q.lumpW, q.jetW, 1f, 0f, j < ow.nVirt - 1, -1);
                    densMax = Mathf.Max(densMax, q.dens0);
                }
            }
            // flat end at the nozzle (one shared jet): segments from the head to the nozzle
            if (v.host && ow != null && ow.virtCut && eN >= 2)
            {
                int noz = eN - 1;
                int from = v.jet ? 0 : Mathf.Max(nReal - 1, 0);
                for (int k = from; k < noz; k++) eCut[k] = noz;
            }
            // 3. ground cloud: smoke parcels as spheres (softly united in the shader), the fast streams from the trenches or from the
            // impact as chains (one connected jet). Room for the booster jets stays free; if it is not enough,
            // the smallest calm parcels drop first (the streams stay)
            cRh = 0f; cHc = 0f;
            if (v.cloud != null)
            {
                GroundCloud g = v.cloud;
                int reserve = (v.host && ow != null && ow.split) ? 4 * ow.nJets : 0;
                int avail = Volume.MAXP - eN - reserve;
                float ebC = 0.12f * Mathf.SmoothStep(0f, 1f, (g.age / g.life - 0.4f) / 0.6f);
                lobeSel.Clear();
                foreach (Lobe L in g.lobes) if (!L.dead && L.dens >= 0.002f) lobeSel.Add(L);
                if (lobeSel.Count > avail)
                {
                    // only in an emergency (very many boosters): thin out the streams (every k-th by order, the ends always)
                    for (int s = 0; s < 10; s++) { stCnt[s] = 0; stMin[s] = int.MaxValue; stMax[s] = int.MinValue; }
                    foreach (Lobe L in lobeSel)
                    {
                        if (!L.Streaming) continue;
                        int s = Mathf.Clamp(L.stream, 0, 9);
                        stCnt[s]++; stMin[s] = Mathf.Min(stMin[s], L.seq); stMax[s] = Mathf.Max(stMax[s], L.seq);
                    }
                    for (int i = lobeSel.Count - 1; i >= 0; i--)
                    {
                        Lobe L = lobeSel[i];
                        if (!L.Streaming) continue;
                        int s = Mathf.Clamp(L.stream, 0, 9);
                        int kk = Mathf.Max(1, Mathf.CeilToInt(stCnt[s] / 5f));
                        if (L.seq != stMin[s] && L.seq != stMax[s] && L.seq % kk != 0) lobeSel.RemoveAt(i);
                    }
                }
                if (lobeSel.Count > avail)
                {
                    lobeSel.Sort(delegate (Lobe x, Lobe y) { return LobePrio(y).CompareTo(LobePrio(x)); });
                    lobeSel.RemoveRange(Mathf.Max(avail, 0), lobeSel.Count - Mathf.Max(avail, 0));
                }
                // streams along their path (distance from the emission point): one continuous flat band from the exit to the head
                lobeSel.Sort(delegate (Lobe x, Lobe y)
                {
                    int kx = x.Streaming ? x.stream : 1000, ky = y.Streaming ? y.stream : 1000;
                    if (kx != ky) return kx.CompareTo(ky);
                    if (kx == 1000) return x.seq.CompareTo(y.seq);
                    return (x.pos - x.origin).sqrMagnitude.CompareTo((y.pos - y.origin).sqrMagnitude);
                });
                g.anyYoung = false;
                for (int k = 0; k < lobeSel.Count; k++)
                {
                    Lobe L = lobeSel[k];
                    bool link = false;
                    if (L.Streaming && k + 1 < lobeSel.Count)
                    {
                        Lobe N = lobeSel[k + 1];
                        link = N.Streaming && N.stream == L.stream && (N.pos - L.pos).magnitude < 1.3f * (N.r + L.r);   // only short bridges (disappear unobtrusively)
                    }
                    // young parcels: finer knobs (the eddies only grow with the cloud) - shader: weight in the young channel
                    float yC = Mathf.Exp(-L.age / 2.5f);
                    if (yC > 0.01f) g.anyYoung = true; else yC = 0f;
                    AddP(L.pos, L.r, L.dens, L.squash, 1f, 0f, yC, ebC, link, link ? -2 : -1);
                    rSum += L.r; rMinV = Mathf.Min(rMinV, L.r); nStat++;
                    densMax = Mathf.Max(densMax, L.dens);
                    Vector3 dv = L.pos - g.centerL;
                    float vz = Vector3.Dot(dv, g.upL);
                    cRh = Mathf.Max(cRh, (dv - g.upL * vz).magnitude + L.r);
                    cHc = Mathf.Max(cHc, vz + L.r * L.squash);
                }
            }
            int lightCount = eN;   // light and shadow only with column and ground cloud (the narrow booster jets only cost time there)
            // 4. booster jets (one chain each from the nozzle to the confluence, flat at the nozzle)
            if (v.host && ow != null && ow.split)
            {
                for (int k = 0; k < ow.nJets && eN + 4 <= Volume.MAXP; k++)
                {
                    int b0 = eN;
                    for (int j = 0; j < 4; j++)
                    {
                        Pt q = ow.jetPts[k * 4 + j];
                        AddP(q.pos, q.r0, q.dens0, 1f, q.lumpW, q.jetW, 1f, 0f, j < 3, j < 3 ? b0 + 3 : -1);
                        densMax = Mathf.Max(densMax, q.dens0);
                    }
                }
            }
            int nf = Mathf.Min(v.flames.Count, Volume.MAXF);
            if (eN == 0 || (densMax < 0.003f && nf == 0)) return false;   // empty or practically transparent: do not draw

            // hull spheres of the elements (capsule from point i or standalone sphere) and the box
            float warpA = (v.cloud != null && cRh > 0f) ? WARP * 1.3f * v.cloud.rW : 0f;   // both stages of the displacement
            float warpRW = v.cloud != null ? v.cloud.rW : 1f;
            Vector3 mn = new Vector3(1e9f, 1e9f, 1e9f), mx = -mn;
            for (int i = 0; i < eN; i++)
            {
                bool fromPrev = i > 0 && eLink[i - 1];
                Vector3 cc; float rad;
                if (eLink[i] && i + 1 < eN)
                {
                    float ra = eR[i] * (1f + 0.5f * eLw[i]), rb = eR[i + 1] * (1f + 0.5f * eLw[i + 1]);
                    cc = (eP[i] + eP[i + 1]) * 0.5f;
                    rad = (eP[i] - eP[i + 1]).magnitude * 0.5f + Mathf.Max(ra, rb);
                    float csm = Mathf.Max(eCs[i], eCs[i + 1]);
                    if (csm > 0f) rad += 0.6f * COLWARP * Mathf.Max(eR[i], eR[i + 1]) * csm;   // column in cloud style: displacement
                    if (eCut[i] == -2)   // stream of the ground cloud: softly united and displaced like the parcels (young ones: fine knobs)
                    {
                        float rm = Mathf.Max(eR[i], eR[i + 1]);
                        rad += 0.75f * SMOOTHK * rm + 0.45f * warpA * Mathf.Min(1f, 0.4f + 0.6f * Mathf.Min(eR[i], eR[i + 1]) / Mathf.Max(warpRW, 0.1f)) + 0.15f * warpA * Mathf.Max(eYw[i], eYw[i + 1]);
                    }
                }
                else if (!fromPrev)   // sphere: edge of the soft union and displacement of the cloud (young ones: fine knobs)
                {
                    cc = eP[i];
                    rad = eR[i] * Mathf.Max(1f, eSq[i]) * (1f + 0.5f * eLw[i] * (v.cloud != null ? 0.3f : 1f) + 0.75f * SMOOTHK) + 0.45f * warpA * Mathf.Min(1f, 0.4f + 0.6f * eR[i] / Mathf.Max(warpRW, 0.1f))
                        + (v.cloud != null ? 0.15f * warpA * eYw[i] : 0f);
                }
                else { eLink[i] = false; bufD[i] = Vector4.zero; continue; }
                bufD[i] = new Vector4(cc.x, cc.y, cc.z, rad);
                Vector3 ext = new Vector3(rad, rad, rad);
                mn = Vector3.Min(mn, cc - ext); mx = Vector3.Max(mx, cc + ext);
            }
            for (int f = 0; f < nf; f++)
            {
                Flame fl = v.flames[f];
                Vector3 fe = new Vector3(fl.r, fl.r, fl.r) * (1.6f + 4.2f * (ow != null ? ow.flameI : 0f));   // wider jet on the ground
                mn = Vector3.Min(mn, Vector3.Min(fl.a, fl.b) - fe); mx = Vector3.Max(mx, Vector3.Max(fl.a, fl.b) + fe);
            }
            SetBox(v, mn, mx);
            Vector3 c = v.boxC;
            for (int i = 0; i < Volume.MAXP; i++)
            {
                if (i < eN)
                {
                    Vector3 q = eP[i] - c;
                    bufP[i] = new Vector4(q.x, q.y, q.z, eR[i]);
                    bufB[i] = new Vector4(eD[i], eSq[i], eLw[i], eJw[i]);
                    bufC[i] = new Vector4(eYw[i], eEb[i], eLink[i] ? 1f : 0f, eCut[i]);
                    bufE[i] = new Vector4(eCs[i], 0f, 0f, 0f);
                    if (bufD[i].w > 0f) { bufD[i].x -= c.x; bufD[i].y -= c.y; bufD[i].z -= c.z; }
                }
                else { bufP[i] = new Vector4(0, 0, 0, 0.001f); bufB[i] = Vector4.zero; bufC[i] = new Vector4(0, 0, 0, -1f); bufD[i] = Vector4.zero; bufE[i] = Vector4.zero; }
            }
            if (nStat == 0) { rSum = 1f; rMinV = 1f; nStat = 1; }
            float rRefV = rSum / nStat;
            if (v.cloud != null)
            {
                // ground cloud: light sampling length from the calm typical size (the mean over the drawn elements
                // jumps when a parcel is added or merges - then the shading of the whole cloud jumped)
                rRefV = Mathf.Max(rRefV, 0.6f * v.cloud.rW);
                if (rRefV < v.cloud.rRefSm) rRefV = Mathf.Lerp(v.cloud.rRefSm, rRefV, 0.02f);
                v.cloud.rRefSm = rRefV;
                // extent: grows at once, shrinks only slowly (the transition to the column noise does not jump)
                v.cloud.extH = cRh >= v.cloud.extH ? cRh : Mathf.Lerp(v.cloud.extH, cRh, 0.02f);
                v.cloud.extV = cHc >= v.cloud.extV ? cHc : Mathf.Lerp(v.cloud.extV, cHc, 0.02f);
                cRh = v.cloud.extH; cHc = v.cloud.extV;
            }
            SetCommon(v, sunL, rRefV, rMinV, eN, nf, fadeIn, fadeOut, lightCount);
            return true;
        }

        // light, noise and box into the material block (everything relative to the box centre, axes of the celestial body)
        void SetCommon(Volume v, Vector3 sunL, float rRef, float rMinV, int nPts, int nf, int fadeIn, int fadeOut, int lightCount)
        {
            Plume ow = v.owner;
            Vector3 c = v.boxC;
            Vector3 up = c.normalized;
            float el = Vector3.Dot(up, sunL);
            float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.12f, 0.2f, el));
            Color warm = new Color(1f, 0.55f, 0.32f), white = new Color(1f, 0.97f, 0.92f);
            Color sc = Color.Lerp(warm, white, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.02f, 0.35f, el))) * (1.05f * day * Cfg.brightness);
            Color amb = new Color(0.62f, 0.68f, 0.78f) * (Mathf.Lerp(0.04f, 0.7f, day) * Cfg.brightness);
            Color bounce = new Color(0.95f, 0.92f, 0.8f) * (0.18f * Mathf.Clamp01(el) * day * Cfg.brightness);   // sunlit ground brightens the underside

            for (int g = 0; g < 4; g++) { bufG[g] = new Vector4(0, 0, 0, 0); bufG2[g] = new Vector4(0, 0, 0, 1); }
            int gi = 0;
            for (int g = 0; g < glows.Count && gi < 4; g++)
            {
                if ((glows[g].pos - c).magnitude > v.radiusMax + (glows[g].posB - glows[g].pos).magnitude + glows[g].radius * 20f) continue;
                Vector3 a = glows[g].pos - c, b2 = glows[g].posB - c;
                bufG[gi] = new Vector4(a.x, a.y, a.z, glows[g].strength);
                bufG2[gi] = new Vector4(b2.x, b2.y, b2.z, glows[g].radius);
                gi++;
            }
            for (int f = 0; f < Volume.MAXF; f++)
            {
                if (f < nf)
                {
                    Flame fl = v.flames[f];
                    Vector3 fa = fl.a - c, fb = fl.b - c;
                    bufFA[f] = new Vector4(fa.x, fa.y, fa.z, fl.r);
                    bufFB[f] = new Vector4(fb.x, fb.y, fb.z, fl.bright);
                }
                else { bufFA[f] = Vector4.zero; bufFB[f] = Vector4.zero; }
            }
            float extRef = (ow != null && ow.extRef > 0f) ? ow.extRef : rRef;

            MaterialPropertyBlock b = v.mpb;
            b.SetVectorArray("_Pts", bufP);
            b.SetVectorArray("_PtsB", bufB);
            b.SetVectorArray("_PtsC", bufC);
            b.SetVectorArray("_PtsD", bufD);
            b.SetVectorArray("_PtsE", bufE);
            b.SetFloat("_ColWarp", COLWARP);
            float colStyle = 0f;
            for (int i = 0; i < nPts; i++) if (eCs[i] > 0.01f) { colStyle = 1f; break; }
            b.SetFloat("_ColStyle", colStyle);
            b.SetFloat("_Count", nPts);
            b.SetFloat("_LightCount", lightCount);
            b.SetFloat("_FadeInSeg", fadeIn);
            b.SetFloat("_FadeOutSeg", fadeOut);
            b.SetVector("_BoxMin", -v.boxS * 0.5f);
            b.SetVector("_BoxMax", v.boxS * 0.5f);
            b.SetVector("_BoxSize", v.go.transform.lossyScale);
            b.SetVector("_Up", up);
            b.SetVector("_SunDir", sunL);
            b.SetVector("_SunColor", sc);
            b.SetVector("_AmbientColor", amb);
            b.SetVector("_BounceColor", bounce);
            b.SetVector("_SmokeColor", v.type.color);
            b.SetFloat("_Extinction", v.type.opacity * v.type.strength * Cfg.thickness / Mathf.Max(extRef, 0.5f));
            b.SetFloat("_NoiseScale", v.noiseScale);
            b.SetFloat("_YoungScale", v.noiseScale * 2.5f);
            b.SetVector("_NoiseOrigin", v.anchorL - c);
            b.SetVector("_NoiseK", v.noiseK);
            b.SetVector("_NoiseKY", v.noiseKY);
            b.SetVector("_NoiseOffset", v.noiseSeed);
            b.SetFloat("_Lumpy", 0.85f);
            b.SetFloat("_SmoothK", SMOOTHK);
            b.SetFloat("_NoiseMean", NoiseMean);
            // ground cloud: its own coarse noise matching the size of its billows (softly blended into the column)
            if (v.cloud != null && cRh > 0f)
            {
                Vector3 cc = v.cloud.centerL - c;
                b.SetVector("_CloudC", new Vector4(cc.x, cc.y, cc.z, cRh * 1.25f));   // transition to the column only outside the billows
                b.SetVector("_CloudP", new Vector4(Mathf.Max(cHc, 5f) * 1.25f, v.cloud.noiseS, 1f, WARP * v.cloud.rW));
                b.SetVector("_CloudK", v.cloud.noiseK);
                b.SetFloat("_CloudRW", v.cloud.rW);
                b.SetFloat("_CloudYoung", v.cloud.anyYoung ? 1f : 0f);
            }
            else { b.SetVector("_CloudP", new Vector4(1f, 0.01f, 0f, 0f)); b.SetFloat("_CloudYoung", 0f); }
            b.SetFloat("_Boil", Cfg.boil);
            b.SetFloat("_Erosion", 0.48f);
            if (ow != null)
            {
                Vector3 jo = v.jetOriginL != Vector3.zero ? v.jetOriginL : ow.emitL;
                b.SetVector("_JetOrigin", jo - c);
                b.SetVector("_JetPhase", ow.jetPhase);
                b.SetFloat("_JetScale", ow.jetScale);
                b.SetVector("_FlPhase", ow.flamePhase);
                b.SetVector("_FlScale", new Vector4(ow.flameScaleR, ow.flameScaleA, ow.flameI, 0f));
            }
            else { b.SetVector("_JetOrigin", Vector3.zero); b.SetVector("_JetPhase", Vector3.zero); b.SetFloat("_JetScale", 0.1f); }
            b.SetFloat("_FlameN", nf);
            b.SetFloat("_FireFog", (ow != null && nf > 0) ? ow.fireFog : 0f);
            b.SetVectorArray("_FlA", bufFA);
            b.SetVectorArray("_FlB", bufFB);
            b.SetVector("_FineA", v.fineA - c);
            b.SetVector("_FineB", v.fineB - c);
            b.SetFloat("_FineR", v.fineR);
            b.SetFloat("_FineStep", Mathf.Max(v.fineStep, 0.02f));
            b.SetFloat("_ShadowThreshold", 0.45f);
            b.SetFloat("_Steps", Cfg.steps);
            b.SetFloat("_MaxStep", Mathf.Max(Mathf.Min(rRef * 0.1f, rMinV * 0.12f), 0.15f));
            b.SetFloat("_ShadowSteps", v.cloud != null ? Mathf.Min(Cfg.shadowSteps, 3) : Cfg.shadowSteps);   // large ground cloud: one step less
            b.SetFloat("_ShadowLen", Mathf.Max(rRef * 0.35f, 0.2f));
            b.SetFloat("_UseDepth", Cfg.useDepth ? 1f : 0f);
            b.SetVectorArray("_Glow", bufG);
            b.SetVectorArray("_GlowB", bufG2);
            b.SetVector("_GlowColor", new Color(1f, 0.62f, 0.3f));
            v.mr.SetPropertyBlock(b);
        }
    }
}
