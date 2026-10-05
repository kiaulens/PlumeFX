// PlumeFX - offline preview: simulates a rocket launch with the real core + shader and saves images/video frames.
// Called from the command line (Editor/PlumePreviewMenu.cs -> PlumePreviewRunner.Run).
// Output: <project>/Preview/shots/<view>_<time>.png and optionally <project>/Preview/frames/<view>_NNNN.png
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PlumeFX
{
    public static class PlumePreviewRunner
    {
        const float R = 600000f;          // Kerbin radius
        const float FPS = 30f;

        class View { public string name; public Camera cam; }

        // PFX_TRENCH=1: launch pad like the KSC (measured in game, log line "Ground cloud created"): the jet hits the pad 11.3 m
        // below the nozzles (the rocket stands on a mount that the ground probe does not see). The pad
        // lies 4.8 m above the terrain (along up to 15 m, across from -15 to +22 m, then a slope up to +60 m), the
        // trench exits lie 11.5 m north and south (+-Z) at pad height
        static bool trenchMode;
        const float PadTop = 4.8f;
        const float MountTop = 15.4f;     // launch mount (visual only): the nozzles 11.3 m above the pad

        static float PadHeight(float x, float z)
        {
            if (!trenchMode) return 0.6f;
            if (Mathf.Abs(z) > 15f) return 0f;
            if (x >= -15f && x <= 22f) return PadTop;
            if (x > 22f && x < 60f) return PadTop * (1f - (x - 22f) / 38f);
            return 0f;
        }

        static void AddBox(List<GameObject> junk, Material m, Vector3 c, Vector3 size)
        {
            GameObject bx = GameObject.CreatePrimitive(PrimitiveType.Cube); junk.Add(bx);
            bx.transform.position = c;
            bx.transform.localScale = size;
            bx.GetComponent<Renderer>().sharedMaterial = m;
        }

        public static void Run(Shader shader, string outDir, float duration, float[] shotTimes, string videoView, int width, int height)
        {
            Directory.CreateDirectory(Path.Combine(outDir, "shots"));
            string framesDir = Path.Combine(outDir, "frames");
            if (!string.IsNullOrEmpty(videoView))
            {
                if (Directory.Exists(framesDir)) foreach (string f in Directory.GetFiles(framesDir, "*.png")) File.Delete(f);
                Directory.CreateDirectory(framesDir);
            }
            UnityEngine.Random.InitState(42);
            float dbg = 0f;
            string ed = Environment.GetEnvironmentVariable("PFX_DEBUG");
            if (!string.IsNullOrEmpty(ed)) float.TryParse(ed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dbg);
            Shader.SetGlobalFloat("_PFXDebug", dbg);
            Cfg.debugLog = true;
            // settings for comparisons (e.g. PFX_FLAMEBOOST=0 = short flame only)
            string efb = Environment.GetEnvironmentVariable("PFX_FLAMEBOOST");
            if (!string.IsNullOrEmpty(efb)) float.TryParse(efb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out Cfg.flameBoost);
            string efr = Environment.GetEnvironmentVariable("PFX_FIRE");
            if (!string.IsNullOrEmpty(efr)) float.TryParse(efr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out Cfg.fire);
            SmokeType.Init();

            // ---------- scene ----------
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
            QualitySettings.shadowDistance = 2500f;
            QualitySettings.shadowCascades = 4;
            QualitySettings.antiAliasing = 0;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.65f, 0.8f);
            RenderSettings.ambientEquatorColor = new Color(0.5f, 0.55f, 0.55f);
            RenderSettings.ambientGroundColor = new Color(0.25f, 0.3f, 0.2f);
            Shader skyShader = Shader.Find("Skybox/Procedural");
            if (skyShader != null) { Material sky = new Material(skyShader); sky.SetFloat("_AtmosphereThickness", 0.9f); RenderSettings.skybox = sky; }

            List<GameObject> junk = new List<GameObject>();
            GameObject bodyGo = new GameObject("Body"); junk.Add(bodyGo);
            bodyGo.transform.position = new Vector3(0f, -R, 0f);
            Transform bt = bodyGo.transform;

            GameObject sunGo = new GameObject("Sun"); junk.Add(sunGo);
            Light sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.15f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;
            float sunEl = 38f;   // environment variable PFX_SUNEL: sun elevation in degrees (e.g. 80 = almost vertical)
            string se = Environment.GetEnvironmentVariable("PFX_SUNEL");
            if (!string.IsNullOrEmpty(se)) float.TryParse(se, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sunEl);
            sunGo.transform.rotation = Quaternion.Euler(sunEl, 140f, 0f);
            Vector3 sunW = -sunGo.transform.forward;

            Shader std = Shader.Find("Standard");
            Material grass = new Material(std); grass.color = new Color(0.33f, 0.45f, 0.2f); grass.SetFloat("_Glossiness", 0.05f);
            Material concrete = new Material(std); concrete.color = new Color(0.6f, 0.6f, 0.6f); concrete.SetFloat("_Glossiness", 0.1f);
            Material metal = new Material(std); metal.color = new Color(0.78f, 0.78f, 0.78f); metal.SetFloat("_Glossiness", 0.4f);
            Material dark = new Material(std); dark.color = new Color(0.15f, 0.15f, 0.15f);

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane); junk.Add(ground);
            ground.transform.localScale = new Vector3(3000f, 1f, 3000f);
            ground.GetComponent<Renderer>().sharedMaterial = grass;
            trenchMode = Environment.GetEnvironmentVariable("PFX_TRENCH") == "1";
            if (trenchMode)
            {
                AddBox(junk, concrete, new Vector3(3.5f, PadTop * 0.5f, 0f), new Vector3(37f, PadTop, 30f));                    // pad
                GameObject sl = GameObject.CreatePrimitive(PrimitiveType.Cube); junk.Add(sl);                                     // slope
                float ang = Mathf.Atan2(PadTop, 38f);
                sl.transform.position = new Vector3(41f + 0.3f * Mathf.Sin(ang), PadTop * 0.5f - 0.3f * Mathf.Cos(ang), 0f);
                sl.transform.rotation = Quaternion.Euler(0f, 0f, -ang * Mathf.Rad2Deg);
                sl.transform.localScale = new Vector3(Mathf.Sqrt(38f * 38f + PadTop * PadTop), 0.6f, 30f);
                sl.GetComponent<Renderer>().sharedMaterial = concrete;
                AddBox(junk, dark, new Vector3(0f, (PadTop + MountTop) * 0.5f, 0f), new Vector3(7f, MountTop - PadTop, 7f));   // launch mount
            }
            else
            {
                GameObject pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder); junk.Add(pad);
                pad.transform.localScale = new Vector3(60f, 0.6f, 60f);
                pad.transform.position = new Vector3(0f, 0.6f, 0f);
                pad.GetComponent<Renderer>().sharedMaterial = concrete;
            }
            // a few reference objects (water tower / tanks)
            for (int i = 0; i < 3; i++)
            {
                GameObject tw = GameObject.CreatePrimitive(PrimitiveType.Cylinder); junk.Add(tw);
                tw.transform.localScale = new Vector3(3f, 9f, 3f);
                tw.transform.position = new Vector3((trenchMode ? 62f : 40f) + i * 9f, 9f, 35f - i * 30f);
                tw.GetComponent<Renderer>().sharedMaterial = metal;
            }

            // rocket: core stage + 4 solid boosters (as in the in-game test)
            GameObject rocket = new GameObject("Rocket"); junk.Add(rocket);
            Transform rt = rocket.transform;
            GameObject core = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            core.transform.SetParent(rt, false); core.transform.localScale = new Vector3(2.5f, 8f, 2.5f); core.transform.localPosition = new Vector3(0f, 9f, 0f);
            core.GetComponent<Renderer>().sharedMaterial = metal;
            List<Transform> nozzles = new List<Transform>();
            int nBoost = 1;   // test: a single booster (environment variable PFX_BOOST=4 for four)
            string eb = Environment.GetEnvironmentVariable("PFX_BOOST");
            if (!string.IsNullOrEmpty(eb)) int.TryParse(eb, out nBoost);
            for (int i = 0; i < nBoost; i++)
            {
                float a = i * Mathf.PI * 0.5f + Mathf.PI * 0.25f;
                Vector3 off = nBoost == 1 ? Vector3.zero : new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 1.95f;
                GameObject b = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                b.transform.SetParent(rt, false); b.transform.localScale = new Vector3(1.3f, nBoost == 1 ? 7f : 4.5f, 1.3f); b.transform.localPosition = off + new Vector3(0f, nBoost == 1 ? 8f : 5.5f, 0f);
                if (nBoost == 1) core.SetActive(false);
                b.GetComponent<Renderer>().sharedMaterial = metal;
                GameObject nz = new GameObject("Nozzle" + i);
                nz.transform.SetParent(rt, false); nz.transform.localPosition = off + new Vector3(0f, 0.5f, 0f);   // nozzle exit = lower edge of the bell
                nz.transform.localRotation = Quaternion.LookRotation(Vector3.down);
                nozzles.Add(nz.transform);
                GameObject bell = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                bell.transform.SetParent(rt, false); bell.transform.localScale = new Vector3(1.0f, 0.4f, 1.0f); bell.transform.localPosition = off + new Vector3(0f, 0.9f, 0f);
                bell.GetComponent<Renderer>().sharedMaterial = dark;
            }
            foreach (Collider c in rocket.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(c);

            // cameras
            List<View> views = new List<View>();
            string[] names = { "side", "chase", "top", "below", "near", "padclose", "nozzle", "far", "orbit", "spectator", "trench", "wide", "shadow", "front", "behind", "launch", "behindhigh" };
            foreach (string n in names)
            {
                GameObject cg = new GameObject("Cam_" + n); junk.Add(cg);
                Camera cam = cg.AddComponent<Camera>();
                cam.enabled = false;
                cam.fieldOfView = 55f;
                cam.nearClipPlane = 0.5f;
                cam.farClipPlane = 60000f;
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.depthTextureMode = DepthTextureMode.Depth;
                cam.allowHDR = false;
                if (n == "orbit") { cam.nearClipPlane = 50f; cam.farClipPlane = 2000000f; cam.fieldOfView = 30f; }
                if (n == "far") { cam.nearClipPlane = 5f; cam.farClipPlane = 200000f; }
                views.Add(new View { name = n, cam = cam });
            }

            // ---------- core ----------
            GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cube = tmp.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(tmp.GetComponent<Collider>());
            tmp.GetComponent<Renderer>().enabled = false;
            junk.Add(tmp);
            Texture3D noise = PlumeSystem.MakeNoise(64);
            PlumeSystem sys = new PlumeSystem(shader, noise, cube);
            sys.bodyRadius = R;
            sys.surfaceRadius = delegate (Vector3 local) { return R + 0.6f; };
            sys.groundHit = delegate (Vector3 from, Vector3 dir, float range, out Vector3 hit, out float dist)
            {
                hit = Vector3.zero; dist = 0f;
                if (dir.y >= -0.05f) return false;
                if (!trenchMode)
                {
                    if (from.y <= 0f) return false;
                    float d = (from.y - 0.6f) / -dir.y;
                    if (d > range) return false;
                    hit = from + dir * d; dist = d; return true;
                }
                // sample the height field (from just above the pad, 0.25 m steps), then refine
                float t0 = from.y > PadTop + 1f ? (from.y - PadTop - 1f) / -dir.y : 0f;
                float prev = t0;
                for (float tt = t0; tt <= range; tt += 0.25f)
                {
                    Vector3 q = from + dir * tt;
                    if (q.y <= PadHeight(q.x, q.z))
                    {
                        float a0 = prev, a1 = tt;
                        for (int k = 0; k < 8; k++) { float tm = (a0 + a1) * 0.5f; Vector3 qm = from + dir * tm; if (qm.y <= PadHeight(qm.x, qm.z)) a1 = tm; else a0 = tm; }
                        hit = from + dir * a1; dist = a1; return true;
                    }
                    prev = tt;
                }
                return false;
            };
            Vector3 windL = new Vector3(2.5f, 0f, 1.2f);

            RenderTexture rtx = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            Texture2D read = new Texture2D(width, height, TextureFormat.RGB24, false);

            // ---------- trajectory ----------
            float baseY = trenchMode ? MountTop + 0.2f : 1.2f;
            Vector3 pos = new Vector3(0f, baseY, 0f), vel = Vector3.zero;
            float pitch = 0f;   // degrees, pitch towards +X
            float dt = 1f / FPS;
            int frame = 0;
            int nextShot = 0;
            float t = 0f;
            float hold = 0f;           // PFX_HOLD: the rocket stays on the ground this long (too little thrust), engines running
            string eh = Environment.GetEnvironmentVariable("PFX_HOLD");
            if (!string.IsNullOrEmpty(eh)) float.TryParse(eh, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hold);
            float nozD = 0.95f, thrustK = 250f;   // PFX_NOZD (m), PFX_THRUST (kN per booster): big boosters
            string end = Environment.GetEnvironmentVariable("PFX_NOZD");
            if (!string.IsNullOrEmpty(end)) float.TryParse(end, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out nozD);
            string eth = Environment.GetEnvironmentVariable("PFX_THRUST");
            if (!string.IsNullOrEmpty(eth)) float.TryParse(eth, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out thrustK);
            float accel = 21f;         // PFX_ACC: thrust acceleration m/s^2 (e.g. 11.5 = slow launch, TWR ~1.2)
            string eac = Environment.GetEnvironmentVariable("PFX_ACC");
            if (!string.IsNullOrEmpty(eac)) float.TryParse(eac, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out accel);
            bool cloudLog = Environment.GetEnvironmentVariable("PFX_CLOUDLOG") == "1";
            float nextCloudLog = 0f;
            float boosterTime = 60f;   // environment variable PFX_BURN: burn time in s
            string eburn = Environment.GetEnvironmentVariable("PFX_BURN");
            if (!string.IsNullOrEmpty(eburn)) float.TryParse(eburn, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out boosterTime);
            System.Diagnostics.Stopwatch swGpu = new System.Diagnostics.Stopwatch();
            double gpuAcc = 0; int gpuN = 0; float nextGpuLog = 0f;
            float gpuWin = 5f;   // environment variable PFX_GPUWIN: measuring window in s
            string egw = Environment.GetEnvironmentVariable("PFX_GPUWIN");
            if (!string.IsNullOrEmpty(egw)) float.TryParse(egw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out gpuWin);
            System.Diagnostics.Stopwatch swSim = new System.Diagnostics.Stopwatch(), swRen = new System.Diagnostics.Stopwatch();
            int nRen = 0; double maxRen = 0;
            while (t <= duration + 1e-4f)
            {
                // move the rocket: ramp up briefly on the ground, then accelerate at ~2.2 g, pitch over slowly from 8 s
                float thrust = Mathf.Clamp01(t / 0.6f);
                if (t > 8f) pitch = Mathf.Min(45f, (t - 8f) * 2.2f);
                rt.rotation = Quaternion.Euler(0f, 0f, -pitch);
                Vector3 upR = rt.up;
                if (t > Mathf.Max(0.8f, hold)) vel += (upR * accel - Vector3.up * 9.81f) * dt;
                pos += vel * dt;
                if (pos.y < baseY) { pos.y = baseY; if (vel.y < 0f) vel.y = 0f; }
                rt.position = pos;

                // input as in the plugin: combine the nozzles
                bool on = t < boosterTime;
                sys.BeginFrame();
                if (on)
                {
                    PlumeInput pin = new PlumeInput();
                    pin.type = SmokeType.SRB;
                    float d = nozD;    // nozzle exit (bell 1.0 m), measured from the part geometry in game
                    Vector3 posSum = Vector3.zero, dirSum = Vector3.zero;
                    foreach (Transform nz in nozzles) { posSum += nz.position; dirSum += nz.forward; }
                    pin.emitW = posSum / nozzles.Count;
                    pin.dir = dirSum.normalized;
                    pin.thr = thrust;
                    pin.dEff = Mathf.Sqrt(d * d * nozzles.Count);
                    pin.dMax = d;
                    float spread = 0f;
                    foreach (Transform nz in nozzles) spread = Mathf.Max(spread, Vector3.ProjectOnPlane(nz.position - pin.emitW, pin.dir).magnitude);
                    pin.spread = spread;
                    foreach (Transform nz in nozzles) { pin.nozW.Add(nz.position); pin.nozD.Add(d); }
                    if (trenchMode) { pin.trenchW.Add(new Vector3(0f, PadTop - 0.3f, 11.5f)); pin.trenchW.Add(new Vector3(0f, PadTop - 0.3f, -11.5f)); }
                    pin.tTot = thrustK * nozzles.Count;
                    pin.rho = 1.225f * Mathf.Exp(-pos.y / 5600f);
                    pin.hGround = pos.y;
                    sys.EmitPlume("rocket/SRB", pin, bt, dt);
                }
                swSim.Start();
                sys.EndEmit();
                sys.Simulate(dt, dt, windL);
                swSim.Stop();

                // debugging: print the billows of the ground cloud (PFX_LOBES=10,30)
                string elb = Environment.GetEnvironmentVariable("PFX_LOBES");
                if (!string.IsNullOrEmpty(elb))
                    foreach (string ts in elb.Split(','))
                    {
                        float tl; if (!float.TryParse(ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out tl)) continue;
                        if (t + dt * 0.5f < tl || t - dt * 0.5f >= tl) continue;
                        foreach (Volume vv in sys.volumes)
                        {
                            if (vv.cloud == null) continue;
                            GroundCloud gg = vv.cloud;
                            Debug.Log("PLUMEFX_LOBES t=" + t.ToString("F1") + " R=" + gg.R.ToString("F1") + " rW=" + gg.rW.ToString("F1") + " gNoz=" + gg.gNoz.ToString("F1") + " S=" + gg.feedS.ToString("F2") + " n=" + gg.lobes.Count);
                            for (int li = 0; li < gg.lobes.Count; li++)
                            {
                                Lobe L = gg.lobes[li];
                                Vector3 dv = L.pos - gg.centerL;
                                float vz = Vector3.Dot(dv, gg.upL);
                                Debug.Log("PLUMEFX_LOBE " + li + " st" + L.stream + " #" + L.seq + (L.Streaming ? " STREAM" : "") + (L.into != null ? " MERGING" : "") + " r=" + L.r.ToString("F1") + " d=" + L.dens.ToString("F3") + " v=" + L.vel.magnitude.ToString("F0") + " age=" + L.age.ToString("F1") + " hz=" + (dv - gg.upL * vz).magnitude.ToString("F1") + " z=" + vz.ToString("F1") + " sq=" + L.squash.ToString("F2") + " xz=" + L.pos.x.ToString("F0") + "," + L.pos.z.ToString("F0"));
                            }
                        }
                    }
                if (cloudLog && t >= nextCloudLog)
                {
                    nextCloudLog = t + 1f;
                    foreach (Volume vv in sys.volumes)
                    {
                        if (vv.cloud == null) continue;
                        GroundCloud gg = vv.cloud;
                        int nStr = 0, nCalm = 0, nAuf = 0; float rMax = 0f;
                        foreach (Lobe L in gg.lobes) { if (L.into != null) nAuf++; else if (L.Streaming) nStr++; else nCalm++; rMax = Mathf.Max(rMax, L.r); }
                        Debug.Log("PLUMEFX_CLOUD t=" + t.ToString("F0") + " n=" + gg.lobes.Count + " stream=" + nStr + " calm=" + nCalm + " merging=" + nAuf + " rW=" + gg.rW.ToString("F1") + " rMax=" + rMax.ToString("F1") + " R=" + gg.R.ToString("F0") + " S=" + gg.feedS.ToString("F2"));
                    }
                }
                bool shot = nextShot < shotTimes.Length && t + dt * 0.5f >= shotTimes[nextShot];
                bool vid = !string.IsNullOrEmpty(videoView);
                string shotViews = Environment.GetEnvironmentVariable("PFX_SHOTVIEWS");   // only these views as stills (faster)
                if (shot || vid)
                {
                    foreach (View v in views)
                    {
                        bool wantShot = shot && (string.IsNullOrEmpty(shotViews) || ("," + shotViews + ",").Contains("," + v.name + ","));
                        bool wantVid = vid && ("," + videoView + ",").Contains("," + v.name + ",");
                        if (!wantShot && !wantVid) continue;
                        PlaceCamera(v, rt, pos, t);
                        sys.forceAll = wantShot;   // still: bring all sections up to date
                        double t0 = swRen.Elapsed.TotalMilliseconds;
                        swRen.Start();
                        sys.Render(bt, v.cam.transform.position, sunW, t);
                        swRen.Stop();
                        if (!wantShot) { nRen++; maxRen = Math.Max(maxRen, swRen.Elapsed.TotalMilliseconds - t0); }
                        sys.forceAll = false;
                        v.cam.targetTexture = rtx;
                        swGpu.Reset(); swGpu.Start();
                        v.cam.Render();
                        RenderTexture.active = rtx;
                        read.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                        read.Apply();
                        swGpu.Stop();
                        if (wantVid)
                        {
                            gpuAcc += swGpu.Elapsed.TotalMilliseconds; gpuN++;
                            if (t >= nextGpuLog)
                            {
                                Debug.Log("PLUMEFX_GPU t=" + t.ToString("F0") + " h=" + pos.y.ToString("F0") + " rho=" + (1.225f * Mathf.Exp(-pos.y / 5600f)).ToString("F4") + " ms=" + (gpuAcc / Math.Max(gpuN, 1)).ToString("F1") + " vol=" + sys.volumes.Count);
                                gpuAcc = 0; gpuN = 0; nextGpuLog = t + gpuWin;
                            }
                        }
                        RenderTexture.active = null;
                        v.cam.targetTexture = null;
                        byte[] png = read.EncodeToPNG();
                        if (wantShot) File.WriteAllBytes(Path.Combine(outDir, Path.Combine("shots", v.name + "_" + shotTimes[nextShot].ToString("00.0").Replace(',', '.') + "s.png")), png);
                        if (wantVid) File.WriteAllBytes(Path.Combine(framesDir, v.name + "_" + frame.ToString("0000") + ".png"), png);
                    }
                    if (shot) nextShot++;
                }
                frame++;
                t += dt;
            }
            int vols = sys.volumes.Count, pts = 0;
            foreach (Volume v in sys.volumes) pts += v.pts.Count;
            Debug.Log("PLUMEFX_PREVIEW_DONE frames=" + frame + " volumes=" + vols + " points=" + pts + " rocketAlt=" + pos.y.ToString("F0")
                + " sim=" + (swSim.Elapsed.TotalMilliseconds / Math.Max(frame, 1)).ToString("F3") + "ms/frame render=" + (nRen > 0 ? (swRen.Elapsed.TotalMilliseconds / nRen).ToString("F3") : "-") + "ms (max " + maxRen.ToString("F2") + ")");

            sys.Dispose();
            foreach (GameObject g in junk) if (g != null) UnityEngine.Object.DestroyImmediate(g);
            UnityEngine.Object.DestroyImmediate(rtx);
        }

        static void PlaceCamera(View v, Transform rocket, Vector3 rpos, float t)
        {
            Transform c = v.cam.transform;
            switch (v.name)
            {
                case "side":        // from the side, shows pad + column
                    {
                        float ry = Mathf.Min(rpos.y, 900f);
                        float h = Mathf.Max(ry * 0.45f, 12f);
                        Vector3 look = new Vector3(Mathf.Min(rpos.x, 600f) * 0.5f, h, 0f);
                        float dist = 140f + ry * 0.9f;
                        c.position = look + new Vector3(-0.35f, 0.08f, -1f).normalized * dist;
                        c.LookAt(look);
                        break;
                    }
                case "chase":       // KSP chase camera: diagonally above the rocket, looking down along it
                    {
                        c.position = rpos + rocket.up * 22f + new Vector3(-26f, 6f, -30f);
                        c.LookAt(rpos - rocket.up * 25f);
                        break;
                    }
                case "top":         // looking down onto the pad
                    {
                        c.position = new Vector3(-50f, 170f, -110f);
                        c.LookAt(new Vector3(0f, 0f, 0f));
                        break;
                    }
                case "near":        // close beside the nozzles: transition jet -> cloud
                    {
                        Vector3 side = Vector3.Cross(rocket.up, Vector3.forward).normalized;
                        c.position = rpos - rocket.up * 22f - side * 30f + Vector3.forward * -45f;
                        c.LookAt(rpos - rocket.up * 22f);
                        break;
                    }
                case "nozzle":      // very close beside the nozzle: jet and transition in detail
                    {
                        Vector3 side = Vector3.Cross(rocket.up, Vector3.forward).normalized;
                        Vector3 look = rpos + rocket.up * 0.5f - rocket.up * 5f;
                        c.position = look - side * 4f + Vector3.forward * -15f + rocket.up * 1.5f;
                        c.LookAt(look);
                        break;
                    }
                case "far":         // 7 km to the side: the whole trail over minutes (spreading, twisting, dissolving)
                    {
                        c.position = new Vector3(-2000f, 1500f, -7000f);
                        c.LookAt(new Vector3(3000f, 4500f, 0f));
                        break;
                    }
                case "orbit":       // from 85 km altitude down onto the launch site
                    {
                        c.position = new Vector3(-90000f, 85000f, -60000f);
                        c.LookAt(new Vector3(6000f, 6000f, 0f));
                        break;
                    }
                case "spectator":   // spectator 130 m from the launch site: ground cloud
                    {
                        c.position = new Vector3(-115f, 10f, -60f);
                        c.LookAt(new Vector3(0f, 14f, 0f));
                        break;
                    }
                case "padclose":    // close to the pad: launch and jet on the ground
                    {
                        float y0 = trenchMode ? PadTop : 0f;
                        c.position = new Vector3(-22f, 5f + y0, -26f);
                        c.LookAt(new Vector3(0f, Mathf.Min(rpos.y - y0, 30f) * 0.6f + 3f + y0, 0f));
                        break;
                    }
                case "front":       // 300 m in front of the pad, across the trenches, slightly raised
                    {
                        c.position = new Vector3(-300f, 55f, 0f);
                        c.LookAt(new Vector3(0f, 15f, 0f));
                        break;
                    }
                case "behindhigh":  // behind the pad, raised (~330 m, 120 m high), trenches left/right
                    {
                        c.position = new Vector3(-330f, 120f, 20f);
                        c.LookAt(new Vector3(0f, 30f + Mathf.Min(rpos.y, 300f) * 0.25f, 0f));
                        break;
                    }
                case "launch":      // ~180 m diagonally in front of the pad, trenches left/right
                    {
                        c.position = new Vector3(-250f, 58f, -70f);
                        c.LookAt(new Vector3(0f, 20f, 0f));
                        break;
                    }
                case "behind":      // behind and above the pad, follows the rocket (column at the engine)
                    {
                        c.position = new Vector3(-180f, 90f + Mathf.Min(rpos.y, 400f) * 0.5f, -60f);
                        c.LookAt(new Vector3(0f, Mathf.Min(rpos.y, 400f) * 0.8f, 0f));
                        break;
                    }
                case "shadow":      // straight down: ground cloud and its shadow (sun from the north-west, shadow to the south-east)
                    {
                        c.position = new Vector3(90f, 650f, -90f);
                        c.LookAt(new Vector3(90f, 0f, -89.9f));
                        break;
                    }
                case "wide":        // 450 m away, across the trenches: the whole ground cloud
                    {
                        c.position = new Vector3(-430f, 35f, -140f);
                        c.LookAt(new Vector3(0f, 40f, 0f));
                        break;
                    }
                case "trench":      // from the side onto a trench exit (south): smoke shoots out and towers up
                    {
                        c.position = new Vector3(-70f, 9f, -125f);
                        c.LookAt(new Vector3(0f, 10f, -50f));
                        break;
                    }
                case "below":       // from the ground up to the rocket
                    {
                        c.position = new Vector3(-90f, 4f, -120f);
                        c.LookAt(rpos - rocket.up * 10f);
                        break;
                    }
            }
        }
    }
}
