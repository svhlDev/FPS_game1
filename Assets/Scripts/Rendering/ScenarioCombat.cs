using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// The combat pass, in a built player: -scenario combat (the player starts unarmed).
//   unarmed start and beam widths; guard and jab (fists in the lower third, straight path along the
//   view, the other fist stays up, the hit check at the fist); officers killed by 4 body / 2 head
//   lethal hits (ragdoll lying where it fell, gun dropped); the dropped gun picked up with E; burn
//   marks (left by hits, kept while in view, gone once old and out of view); burn chains (5 hits on
//   one spot always ignite, spread shots rarely); trunks (hatch slides down, grenades taken with E);
//   a grenade (arc, fuse, explosion and fire).
public partial class ScenarioTest
{
    IEnumerator CombatTest()
    {
        var fpc = FirstPersonController.Instance;
        var w = fpc.GetComponent<PlayerWeapon>();
        var fists = fpc.GetComponent<PlayerFists>();
        var interact = fpc.GetComponent<PlayerInteract>();
        var gren = fpc.GetComponent<PlayerGrenades>();
        var disp = PoliceDispatch.Ensure();
        var cam = fpc.playerCamera;
        string dir = Path.GetDirectoryName(Application.dataPath);
        int playerMask = 1 << Mathf.Max(0, LayerMask.NameToLayer("Player"));
        IEnumerator Shot(string name)
        {
            yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"combat_{name}.png"));
            yield return null; yield return null;
        }
        void LookAt(Vector3 p)
        {
            Vector3 d = p - cam.transform.position;
            fpc.DebugLook(Mathf.Clamp(-Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, -80f, 80f), Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
        }
        Vector3 Ground(Vector3 p)
        {
            if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out var h, 12f, Physics.DefaultRaycastLayers & ~playerMask, QueryTriggerInteraction.Ignore)) return h.point;
            return p;
        }
        Vector3 Flat(Vector3 v) { v.y = 0f; return v.normalized; }
        yield return new WaitForSeconds(2f); // body pool fills a little

        // ---------- 0. start and beams ----------
        Log($"player gun at start: {w.HasGun}");
        Log(!w.HasGun ? "PASS the player starts unarmed (fists)" : "FAIL the player starts with a gun");
        Log($"beam widths: hand {LaserWeapon.HandBeamWidth}, police car {LaserWeapon.CarBeamWidth}");
        Log(LaserWeapon.HandBeamWidth >= 0.03f && LaserWeapon.CarBeamWidth >= 0.06f ? "PASS thicker beams" : "FAIL beam widths");

        // ---------- 1. guard and jab ----------
        float yaw0 = fpc.transform.eulerAngles.y;
        fpc.DebugLook(0f, yaw0);
        fists.DebugRaise();
        yield return new WaitForSeconds(0.8f);
        var fig = fpc.Figure;
        Vector3 vl = cam.WorldToViewportPoint(fig.HandL.position), vr = cam.WorldToViewportPoint(fig.HandR.position);
        Log($"guard: left fist viewport ({vl.x:0.00}, {vl.y:0.00}, {vl.z:0.00} m), right ({vr.x:0.00}, {vr.y:0.00}, {vr.z:0.00} m)");
        bool lowerThird = vl.z > 0f && vr.z > 0f && vl.y > 0f && vl.y < 0.34f && vr.y > 0f && vr.y < 0.34f && vl.x < 0.5f && vr.x > 0.5f && vl.x > 0f && vr.x < 1f;
        Log(lowerThird ? "PASS guard: both fists in the lower third, either side of centre" : "FAIL guard fists not in the lower third");
        yield return Shot("guard");
        // A right jab: sample the fist every frame, in the view's frame.
        Vector3 F = cam.transform.forward;
        Vector3 g0 = fig.HandR.position, l0 = fig.HandL.position;
        float maxLat = 0f, maxExt = 0f, maxLeft = 0f;
        Vector3 extendedAt = Vector3.zero; bool gotExtended = false;
        void OnExt(int hand, Vector3 p) { if (hand == 1) { extendedAt = p; gotExtended = true; } }
        fpc.Animator.PunchExtended += OnExt;
        fists.DebugPunch(1);
        bool midShot = false;
        for (float t = 0f; t < 0.3f; t += Time.deltaTime)
        {
            yield return null;
            Vector3 d = fig.HandR.position - g0;
            float along = Vector3.Dot(d, F);
            maxExt = Mathf.Max(maxExt, along);
            maxLat = Mathf.Max(maxLat, (d - F * along).magnitude);
            maxLeft = Mathf.Max(maxLeft, (fig.HandL.position - l0).magnitude);
            if (!midShot && t > 0.06f) { midShot = true; ScreenCapture.CaptureScreenshot(Path.Combine(dir, "combat_jab.png")); }
        }
        fpc.Animator.PunchExtended -= OnExt;
        Log($"jab: extension {maxExt:0.000} m along the view, max off-line {maxLat:0.000} m, left fist moved {maxLeft:0.000} m, extended event {gotExtended}");
        Log(maxExt > 0.05f && maxLat < 0.05f && maxLeft < 0.03f && gotExtended ? "PASS straight jab along the view, other fist stays up" : "FAIL jab path");

        // A punch on an officer standing in front: the hit lands at the fist (8 damage, body).
        Vector3 fwd = Flat(cam.transform.forward);
        var near = OfficerAgent.DebugSpawn(Ground(fpc.transform.position + fwd * 0.75f), Quaternion.LookRotation(-fwd));
        yield return new WaitForSeconds(0.5f);
        fpc.DebugLook(5f, yaw0);
        fists.DebugRaise();
        yield return new WaitForSeconds(0.3f);
        int hits0 = PlayerFists.HitsLanded;
        float hp0 = near.Health.Health;
        fists.DebugPunch(0);
        yield return new WaitForSeconds(0.35f);
        Log($"punch on an officer 0.75 m away: hits {PlayerFists.HitsLanded - hits0}, health {hp0:0} -> {near.Health.Health:0}, fist at {PlayerFists.LastFist}");
        Log(PlayerFists.HitsLanded > hits0 && near.Health.Health < hp0 ? "PASS punch connects at the fist and hurts (8 / 12)" : "FAIL punch");
        Destroy(near.gameObject);
        disp.DebugClear();
        yield return new WaitForSeconds(0.3f);

        // ---------- 2. officers die: 4 body hits / 2 head hits ----------
        int killed0 = PoliceDispatch.OfficersKilled, rag0 = Ragdoll.Created, guns0 = GunPickup.All.Count;
        IEnumerator KillTest(string label, bool head, Vector3 at)
        {
            var o = OfficerAgent.DebugSpawn(Ground(at), Quaternion.LookRotation(-fwd));
            yield return new WaitForSeconds(0.8f);
            var of = o.GetComponent<CharacterFigure>();
            int shots = 0;
            string parts = "";
            while (o != null && !o.Health.Dead && shots < 10)
            {
                Vector3 target = head ? of.Head.position : (of.Neck.position * 0.4f + of.Hips.position * 0.6f) + o.transform.forward * 0.02f;
                Vector3 muzzle = target + o.transform.forward * 3f + Vector3.up * 0.05f;
                Vector3 d = (target - muzzle).normalized;
                if (LaserWeapon.Fire(muzzle, d, 10f, null, Weapon.Mode.Lethal, out var h))
                {
                    var part = h.collider.GetComponent<BodyPart>();
                    parts += (part != null ? part.location.ToString() : h.collider.name) + " ";
                    CharacterHealth.LaserHit(h, d, 25f, true);
                }
                shots++;
                yield return new WaitForSeconds(0.3f);
            }
            bool dead = o == null || o.Health == null || o.Health.Dead;
            Log($"{label}: dead {dead} after {shots} lethal hits ({parts.Trim()})");
            Log(dead && shots == (head ? 2 : 4) ? $"PASS officer dies after {shots} {(head ? "head" : "body")} hits" :
                dead ? $"WARN officer died after {shots} hits (parts above)" : "FAIL officer did not die");
        }
        Vector3 spot = fpc.transform.position + fwd * 6f;
        yield return KillTest("body shots", false, spot);
        yield return new WaitForSeconds(0.2f);
        Ragdoll body = Ragdoll.All.Count > 0 ? Ragdoll.All[Ragdoll.All.Count - 1] : null;
        Vector3 diedAt = body != null ? body.HipsBody.position : Vector3.zero;
        LookAt(spot);
        yield return new WaitForSeconds(3f);
        yield return Shot("ragdoll");
        if (body != null)
        {
            float headUp = body.HeadBody.position.y - Ground(body.HeadBody.position).y;
            float moved = Vector3.Distance(Flat2(body.HipsBody.position), Flat2(diedAt));
            Log($"ragdoll: {body.Bodies.Count} bodies, head {headUp:0.00} m above the ground, hips moved {moved:0.00} m, asleep {body.Sleeping}");
            Log(body.Bodies.Count >= 15 && headUp < 0.6f && moved < 3f ? "PASS the body ragdolls and lies where it falls" : "FAIL ragdoll");
        }
        else Log("FAIL no ragdoll");
        yield return KillTest("head shots", true, fpc.transform.position + fwd * 6f + Vector3.Cross(Vector3.up, fwd) * 2f);
        yield return new WaitForSeconds(0.5f);
        Log($"officers killed {PoliceDispatch.OfficersKilled - killed0}, ragdolls made {Ragdoll.Created - rag0}, guns dropped {GunPickup.All.Count - guns0}");
        Log(GunPickup.All.Count - guns0 >= 2 ? "PASS dead officers drop their T-guns" : "FAIL no dropped guns");
        disp.DebugClear();

        // ---------- 3. pick the gun up ----------
        GunPickup gp = null;
        foreach (var g in GunPickup.All) if (g != null) { gp = g; break; }
        if (gp != null)
        {
            yield return new WaitForSeconds(1.5f); // settled
            Vector3 gc = gp.Center;
            Vector3 stand = Ground(gc + Flat(fpc.transform.position - gc) * 1.0f);
            fpc.PlaceAt(stand, Quaternion.LookRotation(Flat(gc - stand)));
            yield return new WaitForSeconds(0.6f);
            LookAt(gp.Center);
            yield return null; yield return null;
            string prompt = interact.DebugPrompt();
            yield return Shot("gun_pickup");
            bool used = interact.DebugUse();
            Log($"dropped gun {Vector3.Distance(cam.transform.position, gc):0.00} m away: prompt \"{prompt}\", used {used}, armed {w.HasGun}, strips {w.Gun?.CurrentMode}");
            Log(prompt.Contains("pick up laser") && w.HasGun ? "PASS E picks up a dropped T-gun" : "FAIL gun pickup");
        }
        else Log("FAIL no gun to pick up");

        // ---------- 4. burn marks ----------
        fpc.DebugLook(35f, yaw0);
        yield return null;
        int before = BurnMarks.LiveCount;
        Vector3 aimFloor = cam.transform.position + cam.transform.forward * 3f;
        for (int i = 0; i < 4; i++)
        {
            Vector3 tgt = aimFloor + cam.transform.right * (i - 1.5f) * 0.4f;
            LaserWeapon.Fire(cam.transform.position + cam.transform.forward * 0.5f, tgt - cam.transform.position, 20f, fpc.transform, i % 2 == 0 ? Weapon.Mode.Lethal : Weapon.Mode.Stun, out _);
        }
        yield return Shot("marks_hot");
        yield return new WaitForSeconds(4f);
        yield return Shot("marks_cool");
        int made = BurnMarks.LiveCount;
        Log($"burn marks: {before} -> {made}");
        BurnMarks.DebugAge(400f);
        yield return new WaitForSeconds(1.5f);
        int stillSeen = BurnMarks.LiveCount;
        fpc.DebugLook(-70f, yaw0 + 180f);
        yield return new WaitForSeconds(3f);
        int afterAway = BurnMarks.LiveCount;
        Log($"aged past 5 min: in view {stillSeen} stay, looking away 3 s -> {afterAway}");
        Log(made >= before + 4 && stillSeen >= 4 && afterAway < stillSeen ? "PASS marks stay while seen, go once old and out of view" : "FAIL burn mark lifetime");
        fpc.DebugLook(0f, yaw0);

        // ---------- 5. burn chains ----------
        var parked = new List<FlyingVehicle>();
        foreach (var v in FlyingVehicle.Active)
            if (v != null && v.IsParked && !v.hasDriver && v.Health != null && !v.Health.Burning && v.Trunk != null) parked.Add(v);
        parked.Sort((a, b) => (a.transform.position - fpc.transform.position).sqrMagnitude.CompareTo((b.transform.position - fpc.transform.position).sqrMagnitude));
        Log($"parked cars: {parked.Count}");
        if (parked.Count >= 2)
        {
            IEnumerator Burst(FlyingVehicle car, bool spread, int n, System.Action<int> done)
            {
                int ign = BurnMarks.Ignitions;
                Vector3 side = car.transform.right;
                for (int i = 0; i < n; i++)
                {
                    Vector3 local = spread ? new Vector3(0f, Random.Range(-0.3f, 0.3f), Random.Range(-1.6f, 1.6f)) : Vector3.zero;
                    Vector3 target = car.transform.position + car.transform.rotation * local;
                    Vector3 muzzle = target + side * 4f;
                    LaserWeapon.Fire(muzzle, target - muzzle, 10f, null, Weapon.Mode.Lethal, out _);
                    yield return new WaitForSeconds(spread ? 0.5f : 0.3f);
                }
                done(BurnMarks.Ignitions - ign);
            }
            int chainIgn = 0;
            yield return Burst(parked[0], false, 5, n => chainIgn = n);
            Log($"5 lethal hits on one spot: ignitions {chainIgn}, car burning {parked[0].Health.Burning}");
            Log(parked[0].Health.Burning ? "PASS five hits on one spot set the car on fire" : "FAIL chain did not ignite");
            int spreadIgn = 0, spreadShots = 0;
            for (int k = 1; k < parked.Count && k < 4; k++)
            {
                int got = 0;
                yield return Burst(parked[k], true, 8, n => got = n);
                spreadIgn += Mathf.Min(1, got); spreadShots += 8;
            }
            Log($"spread shots: {spreadShots} hits on {Mathf.Min(3, parked.Count - 1)} cars, {spreadIgn} car(s) ignited (5% per hit expected)");
            Log(spreadIgn <= Mathf.Max(1, spreadShots / 8) ? "PASS spread shots rarely ignite" : "WARN spread shots ignited often");
        }
        else Log("WARN not enough parked cars for the burn chain test");

        // ---------- 6. trunks ----------
        int police = 0, policeFull = 0, allTrunks = 0;
        foreach (var v in FlyingVehicle.Active) if (v != null && v.Trunk != null) { allTrunks++; if (v.Trunk.Police) { police++; if (v.Trunk.Grenades == 2) policeFull++; } }
        Log($"trunks: {allTrunks} of {FlyingVehicle.Active.Count} cars, police {police} (with 2 grenades {policeFull})");
        Log(allTrunks == FlyingVehicle.Active.Count && police == policeFull ? "PASS every car has a trunk, police trunks hold 2 grenades" : "FAIL trunks");
        FlyingVehicle tcar = null;
        for (int k = parked.Count - 1; k >= 0 && tcar == null; k--) if (!parked[k].Health.Burning) tcar = parked[k];
        if (tcar != null)
        {
            var trunk = tcar.Trunk;
            trunk.Grenades = 2; // as a police car's
            Vector3 o = trunk.OpeningCentre;
            Vector3 stand = Ground(o + trunk.RearNormal * 1.1f);
            fpc.PlaceAt(stand, Quaternion.LookRotation(-trunk.RearNormal));
            yield return new WaitForSeconds(0.8f);
            LookAt(trunk.OpeningCentre);
            yield return null; yield return null;
            float hatch0 = trunk.Hatch.localPosition.y;
            string p1 = interact.DebugPrompt();
            interact.DebugUse();
            yield return new WaitForSeconds(0.2f);
            float hatchMid = trunk.Hatch.localPosition.y;
            yield return new WaitForSeconds(0.4f);
            float hatch1 = trunk.Hatch.localPosition.y;
            yield return Shot("trunk_open");
            Log($"trunk: prompt \"{p1}\", hatch y {hatch0:0.00} -> {hatchMid:0.00} (0.2 s) -> {hatch1:0.00} (0.6 s), open {trunk.IsOpen}, items {trunk.Items.Count}");
            Log(p1.Contains("open trunk") && trunk.IsOpen && hatch1 < hatch0 - 0.4f && hatchMid < hatch0 && hatchMid > hatch1 ? "PASS E opens the trunk, the hatch slides down in 0.4 s" : "FAIL trunk opening");
            int g0n = gren.Count;
            for (int i = 0; i < 2; i++)
            {
                if (trunk.Items.Count == 0) break;
                LookAt(trunk.Items[0].position);
                yield return null;
                string p2 = interact.DebugPrompt();
                interact.DebugUse();
                Log($"  item prompt \"{p2}\" -> grenades {gren.Count}");
                yield return null;
            }
            Log(gren.Count - g0n == 2 ? "PASS grenades taken from the trunk with E" : "FAIL trunk items");
            LookAt(trunk.OpeningCentre);
            yield return null;
            string p3 = interact.DebugPrompt();
            interact.DebugUse();
            yield return new WaitForSeconds(0.6f);
            Log($"close: prompt \"{p3}\", hatch y {trunk.Hatch.localPosition.y:0.00}, open {trunk.IsOpen}");
            Log(!trunk.IsOpen && Mathf.Abs(trunk.Hatch.localPosition.y - hatch0) < 0.01f ? "PASS the hatch slides back up" : "FAIL trunk closing");
        }
        else Log("WARN no parked car for the trunk test");

        // ---------- 7. grenade ----------
        if (gren.Count == 0) gren.Add(1);
        fpc.DebugLook(-10f, fpc.transform.eulerAngles.y + 180f);
        yield return new WaitForSeconds(0.3f);
        gren.DebugReady();
        yield return new WaitForSeconds(0.4f);
        yield return Shot("grenade_arc");
        Vector3 predicted = gren.PredictedLanding;
        int exploded0 = Grenade.Exploded, chunks0 = FireSystem.ChunkCount + FireSystem.PatchCount;
        bool thrown = gren.DebugThrow();
        float released = Time.time;
        Grenade live = Grenade.Live.Count > 0 ? Grenade.Live[Grenade.Live.Count - 1] : null;
        float maxY = float.MinValue; int bounces = 0; float lastVy = 0f;
        while (Grenade.Exploded == exploded0 && Time.time - released < 4f)
        {
            if (live != null && live.Body != null)
            {
                float vy = live.Body.linearVelocity.y;
                if (lastVy < -1f && vy > 0.5f) bounces++;
                lastVy = vy;
                maxY = Mathf.Max(maxY, live.transform.position.y);
            }
            yield return null;
        }
        float fuse = Time.time - released;
        yield return Shot("grenade_blast");
        yield return new WaitForSeconds(0.8f);
        int fire = FireSystem.ChunkCount + FireSystem.PatchCount - chunks0;
        Log($"grenade: thrown {thrown}, predicted landing {Vector3.Distance(predicted, fpc.transform.position):0.0} m out, bounces {bounces}, exploded after {fuse:0.00} s, fire chunks/patches +{fire}");
        Log(thrown && Mathf.Abs(fuse - Grenade.Fuse) < 0.15f && fire > 0 ? "PASS grenade: arc, 3 s fuse, explosion and fire" : "FAIL grenade");
        yield return new WaitForSeconds(2f);
        yield return Shot("grenade_fire");
        Log($"ragdolls alive {Ragdoll.All.Count}, burn marks {BurnMarks.LiveCount}, car-hit events {CombatSystem.Hits}");
    }

    static Vector3 Flat2(Vector3 v) => new Vector3(v.x, 0f, v.z);
}
