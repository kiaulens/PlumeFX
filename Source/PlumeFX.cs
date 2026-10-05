// PlumeFX - volumetric rocket smoke for KSP 1.12 (ray-marching shader from its own AssetBundle)
// This file: KSP side (settings, finding engines, ground/water, wind, sun).
// The smoke logic is in the shared core PlumeCore.cs (also used by the Unity preview).
// Shader, core and preview: Unity/ in the repository (Unity 2019.4.18f1).
//
// Build (C# 5, .NET Framework csc), both files together - see build.ps1 in the repository:
//   csc.exe -target:library -nostdlib -noconfig -out:..\GameData\PlumeFX\Plugins\PlumeFX.dll
//     -r:<KSP>\KSP_x64_Data\Managed\{mscorlib,System,System.Core,UnityEngine,UnityEngine.CoreModule,
//     UnityEngine.PhysicsModule,UnityEngine.AssetBundleModule,UnityEngine.UI,UnityEngine.ParticleSystemModule,Assembly-CSharp}.dll
//     -codepage:65001 PlumeFX.cs ..\Unity\Assets\PlumeFX\Core\PlumeCore.cs
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PlumeFX
{
    internal static partial class Cfg
    {
        public static bool stockPadSmoke = false;   // KSP's own smoke from the flame trenches (LaunchPadFX); off: our smoke streams out there

        public static void Load()
        {
            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("PLUMEFX");
            if (nodes != null && nodes.Length > 0)
            {
                ConfigNode n = nodes[0];
                n.TryGetValue("enabled", ref enabled);
                n.TryGetValue("debugLog", ref debugLog);
                n.TryGetValue("densityCutoff", ref densityCutoff);
                n.TryGetValue("maxDistance", ref maxDistance);
                n.TryGetValue("steps", ref steps);
                n.TryGetValue("shadowSteps", ref shadowSteps);
                n.TryGetValue("brightness", ref brightness);
                n.TryGetValue("columnRadius", ref columnRadius);
                n.TryGetValue("thickness", ref thickness);
                n.TryGetValue("boil", ref boil);
                n.TryGetValue("shadows", ref shadows);
                n.TryGetValue("jetFlow", ref jetFlow);
                n.TryGetValue("glow", ref glow);
                n.TryGetValue("fire", ref fire);
                n.TryGetValue("flameLength", ref flameLength);
                n.TryGetValue("flameBoost", ref flameBoost);
                n.TryGetValue("fireBoost", ref fireBoost);
                n.TryGetValue("jetDensity", ref jetDensity);
                n.TryGetValue("windDrift", ref windDrift);
                n.TryGetValue("groundCloud", ref groundCloud);
                n.TryGetValue("splitJets", ref splitJets);
                n.TryGetValue("columnSpacing", ref columnSpacing);
                n.TryGetValue("groundRange", ref groundRange);
                n.TryGetValue("groundSize", ref groundSize);
                n.TryGetValue("groundDrift", ref groundDrift);
                n.TryGetValue("columnFoot", ref columnFoot);
                n.TryGetValue("columnCloudStyle", ref columnCloudStyle);
                n.TryGetValue("stockPadSmoke", ref stockPadSmoke);
                n.TryGetValue("windShear", ref windShear);
                n.TryGetValue("trailLife", ref trailLife);
                n.TryGetValue("updateBudget", ref updateBudget);
                n.TryGetValue("maxVolumes", ref maxVolumes);
                n.TryGetValue("useDepth", ref useDepth);
                n.TryGetValue("srbStrength", ref srbStrength);
                n.TryGetValue("keroloxStrength", ref keroloxStrength);
                n.TryGetValue("methaloxStrength", ref methaloxStrength);
                n.TryGetValue("hydrogenStrength", ref hydrogenStrength);
            }
            ConfigNode[] chute = GameDatabase.Instance.GetConfigNodes("CHUTEFX");
            if (chute != null && chute.Length > 0)
            {
                chute[0].TryGetValue("windSpeed", ref windSpeed);
                chute[0].TryGetValue("windMin", ref windMin);
                chute[0].TryGetValue("windMax", ref windMax);
            }
        }
    }

    internal static class Classifier
    {
        public static SmokeType Classify(List<Propellant> props)
        {
            HashSet<string> n = new HashSet<string>();
            foreach (Propellant p in props) if (p != null && p.name != null) n.Add(p.name);
            if (n.Count == 0) return null;
            if (n.Contains("IntakeAir") || n.Contains("IntakeAtm")) return null;
            if (n.Contains("SolidFuel")) return SmokeType.SRB;
            string[] clean = { "XenonGas", "ArgonGas", "Lithium", "NuclearSaltWater", "Antimatter", "FissionPellets",
                               "FissionParticles", "FissionPulses", "EnrichedUranium", "LqdHe3", "LqdDeuterium", "ThermalPower" };
            foreach (string c in clean) if (n.Contains(c)) return null;
            if (n.Contains("LqdHydrogen")) return SmokeType.Hydrogen;
            if (n.Contains("LqdMethane")) return SmokeType.Methalox;
            if (n.Contains("LiquidFuel") && n.Contains("Oxidizer")) return SmokeType.Kerolox;
            if (n.Contains("LiquidFuel")) return SmokeType.Hydrogen;
            if (n.Contains("MonoPropellant")) return null;
            if (n.Contains("Oxidizer")) return SmokeType.Kerolox;
            return null;
        }
    }

    internal class Eng
    {
        public ModuleEngines m;
        public SmokeType type;
        public float d;          // nozzle exit (m)
        public float zExit;      // exit plane along the thrust direction, from the thrustTransform (m)
        public string src = "";
        public bool announced;
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class PlumeFXController : MonoBehaviour
    {
        const int TerrainMask = 1 << 15;
        static Shader smokeShader;
        static Texture3D noiseTex;
        static bool loadTried;

        PlumeSystem sys;
        Dictionary<ModuleEngines, Eng> engines = new Dictionary<ModuleEngines, Eng>();
        CelestialBody body;
        float nextScan;
        double lastUT;
        float nextDbg;
        float nextPadScan;
        List<Vector3> trenchL = new List<Vector3>();   // exits of the flame trenches (local to the celestial body)
        int padsAnnounced = -1;
        static System.Reflection.FieldInfo padPsField;   // LaunchPadFX.ps is not public

        void Start()
        {
            Cfg.Load();
            SmokeType.Init();
            LoadAssets();
            if (smokeShader != null)
            {
                GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Mesh cube = tmp.GetComponent<MeshFilter>().sharedMesh;
                Destroy(tmp);
                sys = new PlumeSystem(smokeShader, noiseTex, cube);
                sys.groundHit = GroundHit;
                sys.surfaceRadius = SurfaceRadius;
            }
            lastUT = Planetarium.GetUniversalTime();
            Cfg.Log("loaded, enabled=" + Cfg.enabled + " shader=" + (smokeShader != null ? smokeShader.name + (smokeShader.isSupported ? " (ok)" : " (NOT supported)") : "MISSING"));
        }

        void OnDestroy()
        {
            if (sys != null) sys.Dispose();
        }

        static void LoadAssets()
        {
            if (loadTried) return;
            loadTried = true;
            string dir = Path.GetDirectoryName(typeof(PlumeFXController).Assembly.Location);
            string path = Path.Combine(Path.Combine(dir, ".."), Path.Combine("Shaders", "plumefx.shaders"));
            AssetBundle ab = AssetBundle.LoadFromFile(Path.GetFullPath(path));
            if (ab == null) { Debug.Log("[PlumeFX] AssetBundle not found: " + path); return; }
            foreach (Shader s in ab.LoadAllAssets<Shader>()) if (s.name == "PlumeFX/Smoke") smokeShader = s;
            ab.Unload(false);
            noiseTex = PlumeSystem.MakeNoise(64);
        }

        // ---------- main loop ----------
        void LateUpdate()
        {
            if (!Cfg.enabled || sys == null || !FlightGlobals.ready) return;
            Vessel av = FlightGlobals.ActiveVessel;
            if (av == null) return;
            double ut = Planetarium.GetUniversalTime();
            float dtUT = (float)Math.Max(0.0, Math.Min(ut - lastUT, 600.0));
            lastUT = ut;
            if (av.mainBody != body) { body = av.mainBody; sys.Clear(); sys.bodyRadius = (float)body.Radius; }
            if (Time.time >= nextScan) { Scan(); nextScan = Time.time + 1f; }
            if (Time.time >= nextPadScan) { ScanPads(); nextPadScan = Time.time + 5f; }
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (Cfg.useDepth && FlightCamera.fetch != null)
            {
                // depth texture for all flight cameras: distant smoke (view from orbit) is hidden by the terrain too
                Camera[] cams = FlightCamera.fetch.cameras;
                if (cams != null) foreach (Camera c in cams) if (c != null) c.depthTextureMode |= DepthTextureMode.Depth;
                if (cam != null) cam.depthTextureMode |= DepthTextureMode.Depth;
            }

            bool rails = TimeWarp.CurrentRate > 1f && TimeWarp.WarpMode == TimeWarp.Modes.HIGH;
            Transform bt = body.transform;
            Vector3 windL = bt.InverseTransformDirection(WindWorld(av));
            sys.BeginFrame();
            // emit every frame (also without a new physics step, then with dt = 0), otherwise the plume counts as ended.
            // Also without an atmosphere: the flame stays visible (there is no smoke there).
            if (!rails) Emit(dtUT, bt);
            sys.EndEmit();
            sys.Simulate(dtUT, Mathf.Min(dtUT, 0.1f), windL);
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            Vector3 sunW = sun != null ? ((Vector3)(sun.position - body.position)).normalized : Vector3.up;
            sys.Render(bt, cam != null ? cam.transform.position : Vector3.zero, sunW, Time.time);
            if (Cfg.debugLog && sys.plumes.Count > 0 && Time.time >= nextDbg) { nextDbg = Time.time + 1f; Cfg.Log("State: " + sys.DebugState() + " cam=" + (cam != null ? (cam.transform.position - av.transform.position).magnitude.ToString("F0") : "-")); }
        }

        // Launch pad: switch off KSP's smoke from the flame trenches (LaunchPadFX, particles at the trench exits) and remember
        // the exits - the ground cloud then streams out exactly there (KSC: to the north and south)
        void ScanPads()
        {
            trenchL.Clear();
            LaunchPadFX[] fxs = UnityEngine.Object.FindObjectsOfType<LaunchPadFX>();
            if (fxs == null) return;
            if (padPsField == null) padPsField = typeof(LaunchPadFX).GetField("ps", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            foreach (LaunchPadFX fx in fxs)
            {
                if (fx == null) continue;
                ParticleSystem[] pss = padPsField != null ? padPsField.GetValue(fx) as ParticleSystem[] : null;
                if (pss == null) pss = fx.GetComponentsInChildren<ParticleSystem>(true);
                bool off = !Cfg.stockPadSmoke && Cfg.groundCloud;
                if (off) fx.enabled = false;
                foreach (ParticleSystem ps in pss)
                {
                    if (ps == null) continue;
                    if (off)
                    {
                        ParticleSystem.EmissionModule em = ps.emission;
                        em.enabled = false;
                        if (ps.isPlaying) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    }
                    trenchL.Add(body.transform.InverseTransformPoint(ps.transform.position));
                }
            }
            if (trenchL.Count != padsAnnounced)
            {
                padsAnnounced = trenchL.Count;
                Cfg.Log("Launch pads: " + fxs.Length + " LaunchPadFX, " + trenchL.Count + " trench exits" + (!Cfg.stockPadSmoke && Cfg.groundCloud ? " (KSP trench smoke off)" : ""));
            }
        }

        void Scan()
        {
            List<Vessel> vs = FlightGlobals.VesselsLoaded;
            HashSet<ModuleEngines> seen = new HashSet<ModuleEngines>();
            for (int i = 0; i < vs.Count; i++)
            {
                Vessel v = vs[i];
                if (v == null || v.parts == null) continue;
                for (int p = 0; p < v.parts.Count; p++)
                {
                    List<ModuleEngines> ms = v.parts[p].FindModulesImplementing<ModuleEngines>();
                    for (int k = 0; k < ms.Count; k++)
                    {
                        ModuleEngines m = ms[k];
                        seen.Add(m);
                        if (engines.ContainsKey(m)) continue;
                        Eng e = new Eng();
                        e.m = m;
                        e.type = Classifier.Classify(m.propellants);
                        int nt = Math.Max(1, m.thrustTransforms != null ? m.thrustTransforms.Count : 1);
                        NozzleGeometry(m, nt, out e.d, out e.zExit, out e.src);
                        engines[m] = e;
                    }
                }
            }
            List<ModuleEngines> dead = null;
            foreach (ModuleEngines m in engines.Keys) if (m == null || !seen.Contains(m)) { if (dead == null) dead = new List<ModuleEngines>(); dead.Add(m); }
            if (dead != null) foreach (ModuleEngines m in dead) engines.Remove(m);
        }

        // Nozzle exit from the part geometry: all vertices of the model relative to the first thrustTransform.
        // Exit plane = geometry farthest along the thrust direction, diameter = largest distance from the thrust axis
        // in a narrow band before it (rim of the nozzle bell). Fallback: bottom node size, then estimated from the thrust.
        // (Renderer bounds in world axes gave ~5 m instead of ~1 m for the Kickback, the jet was far too wide.)
        static void NozzleGeometry(ModuleEngines m, int nt, out float d, out float zExit, out string src)
        {
            d = 0f; zExit = 0f; src = "";
            Transform tt = (m.thrustTransforms != null && m.thrustTransforms.Count > 0) ? m.thrustTransforms[0] : null;
            if (tt != null)
            {
                try
                {
                    float lim = 1e9f;   // several nozzles: only the geometry around this nozzle
                    for (int k = 1; k < nt; k++) if (m.thrustTransforms[k] != null) lim = Mathf.Min(lim, 0.5f * (m.thrustTransforms[k].position - tt.position).magnitude);
                    Vector3 o = tt.position, f = tt.forward;
                    List<Vector2> pts = new List<Vector2>();   // x = along the thrust, y = distance from the axis
                    float zMin = 1e9f, zMax = -1e9f;
                    foreach (MeshFilter mf in m.part.FindModelComponents<MeshFilter>())
                    {
                        if (mf == null || mf.sharedMesh == null || !mf.gameObject.activeInHierarchy) continue;
                        Renderer rr = mf.GetComponent<Renderer>();
                        if (rr == null || !rr.enabled) continue;
                        Material mt = rr.sharedMaterial;
                        if (mt != null && mt.shader != null)
                        {
                            string sn = mt.shader.name;
                            if (sn.Contains("Waterfall") || sn.Contains("Particle") || sn.Contains("Additive")) continue;   // exhaust effects, not geometry
                        }
                        Mesh mesh = mf.sharedMesh;
                        if (!mesh.isReadable) continue;
                        Vector3[] vs = mesh.vertices;
                        Transform t = mf.transform;
                        for (int i = 0; i < vs.Length; i++)
                        {
                            Vector3 rel = t.TransformPoint(vs[i]) - o;
                            float ax = Vector3.Dot(rel, f);
                            float rad = (rel - f * ax).magnitude;
                            if (rad > lim) continue;
                            pts.Add(new Vector2(ax, rad));
                            if (ax < zMin) zMin = ax;
                            if (ax > zMax) zMax = ax;
                        }
                    }
                    if (pts.Count > 8 && zMax > zMin && zMax > -3f && zMax < 3f)
                    {
                        float band = Mathf.Clamp((zMax - zMin) * 0.04f, 0.03f, 0.25f);
                        float rMax = 0f;
                        foreach (Vector2 q in pts) if (q.x >= zMax - band && q.y > rMax) rMax = q.y;
                        if (rMax > 0.04f)
                        {
                            d = Mathf.Clamp(rMax * 2f, 0.1f, 8f);
                            zExit = zMax;
                            src = "geometry";
                        }
                    }
                }
                catch (Exception ex) { Cfg.Log("Nozzle geometry " + m.part.partInfo.name + ": " + ex.Message); }
            }
            if (d <= 0f)
            {
                AttachNode bn = m.part.FindAttachNode("bottom");
                if (bn != null)
                {
                    float nd = bn.size <= 0 ? 0.625f : bn.size * 1.25f;
                    d = nd * 0.7f / Mathf.Sqrt(nt);
                    src = "node size";
                }
            }
            if (d <= 0f)
            {
                d = Mathf.Clamp(0.045f * Mathf.Sqrt(Mathf.Max(1f, m.maxThrust / nt)), 0.15f, 4f);
                src = "thrust";
            }
        }

        Vector3 WindWorld(Vessel av)
        {
            if (!body.atmosphere) return Vector3.zero;
            Vector3 up = ((Vector3)(av.GetWorldPos3D() - body.position)).normalized;
            Vector3 north = Vector3.ProjectOnPlane(body.transform.up, up);
            if (north.sqrMagnitude < 1e-6f) north = Vector3.ProjectOnPlane(body.transform.forward, up);
            north.Normalize();
            Vector3 east = Vector3.Cross(up, north);
            Vector3 w = PlumeSystem.SharedWindLocal(body.flightGlobalsIndex, Planetarium.GetUniversalTime());
            return east * w.x + north * w.z;
        }

        double TerrainAlt(double lat, double lon)
        {
            if (body.pqsController == null) return 0.0;
            return body.pqsController.GetSurfaceHeight(body.GetRelSurfaceNVector(lat, lon)) - body.Radius;
        }

        float SurfaceRadius(Vector3 local)
        {
            Vector3d wp = body.transform.TransformPoint(local);
            double ta = TerrainAlt(body.GetLatitude(wp), body.GetLongitude(wp));
            if (body.ocean && ta < 0.0) ta = 0.0;
            return (float)(body.Radius + ta);
        }

        bool GroundHit(Vector3 from, Vector3 dir, float range, out Vector3 hit, out float dist)
        {
            RaycastHit h;
            if (Physics.Raycast(from, dir, out h, range, TerrainMask)) { hit = h.point; dist = h.distance; return true; }
            hit = Vector3.zero; dist = 0f;
            if (body.ocean)
            {
                Vector3d bp = body.position;
                Vector3 up = ((Vector3)((Vector3d)from - bp)).normalized;
                double alt = ((Vector3d)from - bp).magnitude - body.Radius;
                float down = -Vector3.Dot(dir, up);
                if (alt > 0.0 && down > 0.2f)
                {
                    float d = (float)(alt / down);
                    if (d < range)
                    {
                        Vector3 p = from + dir * d;
                        Vector3d pd = (Vector3d)p;
                        if (TerrainAlt(body.GetLatitude(pd), body.GetLongitude(pd)) < 0.0) { hit = p; dist = d; return true; }
                    }
                }
            }
            return false;
        }

        // ---------- emitting ----------
        // All active nozzles of a vessel with the same smoke type form ONE plume (booster exhausts merge).
        class Agg
        {
            public SmokeType type; public Vessel v;
            public Vector3 posSum, dirSum; public float wSum, area, tMaxSum, thrW, dMax;
            public List<Vector3> noz = new List<Vector3>();
            public List<float> nozD = new List<float>();
        }

        void Emit(float dt, Transform bt)
        {
            Dictionary<string, Agg> aggs = new Dictionary<string, Agg>();
            foreach (Eng e in engines.Values)
            {
                ModuleEngines m = e.m;
                if (m == null || m.part == null || m.vessel == null || m.vessel.mainBody != body) continue;
                bool on = m.EngineIgnited && !m.flameout && m.finalThrust > 0.01f && m.thrustTransforms != null && m.thrustTransforms.Count > 0;
                if (!on || e.type == null || e.type.strength <= 0f) continue;
                int nt = m.thrustTransforms.Count;
                float tMax = Mathf.Max(1f, m.maxThrust / nt);
                float thr = Mathf.Clamp01((m.finalThrust / nt) / tMax);
                if (!e.announced) { e.announced = true; Cfg.Log("Engine " + m.part.partInfo.name + ": " + e.type.name + " nozzle " + e.d.ToString("F2") + " m (" + e.src + ", exit " + e.zExit.ToString("F2") + " m) x" + nt); }
                string key = m.vessel.id.ToString() + "/" + e.type.name;
                Agg a;
                if (!aggs.TryGetValue(key, out a)) { a = new Agg { type = e.type, v = m.vessel }; aggs[key] = a; }
                for (int k = 0; k < nt; k++)
                {
                    Transform tt = m.thrustTransforms[k];
                    if (tt == null) continue;
                    Vector3 dir = tt.forward;
                    Vector3 noz = tt.position + dir * e.zExit;   // the jet starts exactly in the exit plane of the nozzle
                    float w = tMax * thr;
                    a.posSum += noz * w;
                    a.dirSum += dir * w;
                    a.wSum += w;
                    a.area += e.d * e.d;
                    a.tMaxSum += tMax;
                    a.thrW += thr * tMax;
                    a.noz.Add(noz);
                    a.nozD.Add(e.d);
                    a.dMax = Mathf.Max(a.dMax, e.d);
                }
            }
            foreach (KeyValuePair<string, Agg> kv in aggs)
            {
                Agg a = kv.Value;
                if (a.wSum <= 0f) continue;
                PlumeInput pin = new PlumeInput();
                pin.type = a.type;
                pin.emitW = a.posSum / a.wSum;
                pin.dir = a.dirSum.normalized;
                pin.thr = Mathf.Clamp01(a.thrW / Mathf.Max(a.tMaxSum, 1f));
                pin.dEff = Mathf.Sqrt(a.area);
                pin.dMax = a.dMax;
                float spread = 0f;
                foreach (Vector3 nz in a.noz) spread = Mathf.Max(spread, Vector3.ProjectOnPlane(nz - pin.emitW, pin.dir).magnitude);
                pin.spread = spread;
                pin.nozW.AddRange(a.noz);
                pin.nozD.AddRange(a.nozD);
                pin.tTot = a.tMaxSum;
                pin.rho = (float)a.v.atmDensity;
                // flame trenches nearby (only at the pad): the ground cloud streams to their exits
                foreach (Vector3 eL in trenchL)
                {
                    Vector3 eW = bt.TransformPoint(eL);
                    if ((eW - pin.emitW).sqrMagnitude < 300f * 300f) pin.trenchW.Add(eW);
                }
                float hG = (float)a.v.heightFromTerrain;
                pin.hGround = hG < 0f ? (float)a.v.altitude : hG;
                sys.EmitPlume(kv.Key, pin, bt, dt);
            }
        }
    }
}
