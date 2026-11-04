using System.Collections;
using System.IO;
using UnityEngine;

// The T-gun redesign and the stun rework, in a built player: -scenario tgunstun
//   model: at rest collapsed, chrome seam, handle line in the mode colour, gap glow and beams off, muzzle at the gap;
//   firing: snaps open in 0.05 s, the beam leaves from the muzzle, closes in about 0.12 s; B swaps colours;
//   stun: 10 hits on an officer through the real gun, slower with each, no drain under steady hits, frozen
//   in pose for 3 s, then reset; drain after 3 s idle; a pedestrian and the player the same way.
public partial class ScenarioTest
{
    IEnumerator TGunStunTest()
    {
        var fpc = FirstPersonController.Instance;
        var w = fpc.GetComponent<PlayerWeapon>();
        var disp = PoliceDispatch.Ensure();
        var cam = fpc.playerCamera;
        string dir = Path.GetDirectoryName(Application.dataPath);
        IEnumerator Shot(string name)
        {
            yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"tgunstun_{name}.png"));
            yield return null; yield return null;
        }
        void LookAt(Vector3 p)
        {
            Vector3 d = p - cam.transform.position;
            fpc.DebugLook(Mathf.Clamp(-Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, -80f, 80f), Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
        }
        yield return new WaitForSeconds(2f);

        // ---------- the model ----------
        var gun = w.Gun;
        var act = gun.Action;
        var root = gun.transform;
        float tris = 0;
        foreach (var mf in gun.GetComponentsInChildren<MeshFilter>(true)) for (int sm = 0; sm < mf.sharedMesh.subMeshCount; sm++) tris += mf.sharedMesh.GetIndexCount(sm) / 3f;
        Vector3 up = root.InverseTransformDirection(act.Upper.up), fwd = root.InverseTransformDirection(gun.muzzle.forward);
        Vector3 restPos = act.Upper.localPosition;
        Log($"model: {tris:0} triangles, scale {root.localScale.x:0.000} / upper {act.Upper.localScale.x:0.000}, muzzle {gun.muzzle.localPosition.x:0.0000} {gun.muzzle.localPosition.y:0.0000} {gun.muzzle.localPosition.z:0.0000}, grip {gun.grip.localPosition.magnitude:0.0000}, upper up {up.x:0.00} {up.y:0.00} {up.z:0.00}, muzzle forward {fwd.x:0.00} {fwd.y:0.00} {fwd.z:0.00}");
        Log(act.Upper.up == root.up || Vector3.Dot(up, Vector3.up) > 0.999f ? "PASS the Upper slides along the gun's up axis" : "FAIL upper axis");
        Log(Vector3.Dot(fwd, Vector3.forward) > 0.999f && gun.muzzle.localPosition.z > 0.09f && gun.muzzle.localPosition.z < 0.1f && gun.muzzle.localPosition.y > 0.07f && gun.muzzle.localPosition.y < 0.08f ? "PASS muzzle forward, at the gap's front and mid-height" : "FAIL muzzle");
        Log(Mathf.Abs(root.localScale.x - 1f) < 1e-3f && Mathf.Abs(act.Upper.localScale.x - 1f) < 1e-3f ? "PASS no scale on the imported model" : "FAIL import scale");
        var baked = act.Upper.GetComponent<Renderer>().sharedMaterial;
        Log(baked != null && baked.GetTexture("_BaseMap") != null && baked.GetTexture("_BumpMap") != null ? "PASS baked material loaded" : "FAIL baked material");
        var wires = gun.transform.Find("Receiver/Receiver_Wires").GetComponent<Renderer>();
        Log(wires.sharedMaterials.Length == 3 && wires.sharedMaterials[1] != null && wires.enabled ? "PASS wires on the lower bar, with their materials" : "FAIL wires");
        var beamBody = act.Beams.GetComponent<Renderer>();
        Log(!beamBody.enabled ? "PASS energy beams off at rest" : "FAIL energy beams at rest");
        var seam = gun.transform.Find("Receiver/Receiver_Seam").GetComponent<Renderer>();
        var seamMat = seam.sharedMaterial;
        Log(seam.enabled && seamMat != null && seamMat != LaserWeapon.ModeMat(Weapon.Mode.Stun) && seamMat != LaserWeapon.ModeMat(Weapon.Mode.Lethal) ? "PASS seam strips are chrome, no glow" : "FAIL seam strips");

        w.Draw();
        fpc.DebugLook(8f, fpc.transform.eulerAngles.y);
        yield return new WaitForSeconds(1f);
        bool restOk = act.Openness == 0f && !act.GapGlow[0].enabled && !act.GapGlow[1].enabled;
        bool blue = gun.ModeParts.Count == 1 && gun.ModeParts[0].name == "Upper_HandleGlow" && gun.ModeParts[0].sharedMaterial == LaserWeapon.ModeMat(Weapon.Mode.Stun);
        Log(restOk && blue && gun.ModeParts[0].enabled ? "PASS at rest: collapsed, gap glow off, handle line on, blue in stun" : "FAIL rest state");
        yield return Shot("fp_rest_stun");

        // ---------- firing ----------
        Vector3 Mz() => gun.muzzle.position;
        int shots = LaserWeapon.Shots;
        Vector3 m0 = Mz();
        bool fired = w.DebugFire();
        float t0 = Time.time, tOpen = -1f, tClosed = -1f, maxLift = 0f, slowest = 0f;
        bool gapSeen = false, beamsSeen = false; float maxBeam = 0f;
        float originErr = Vector3.Distance(LaserWeapon.LastOrigin, m0);
        while (Time.time - t0 < 0.6f)
        {
            yield return null;
            slowest = Mathf.Max(slowest, Time.deltaTime);
            maxLift = Mathf.Max(maxLift, (act.Upper.localPosition - restPos).y);
            gapSeen |= act.GapGlow[0].enabled;
            beamsSeen |= beamBody.enabled;
            maxBeam = Mathf.Max(maxBeam, act.Beams.localScale.y);
            if (tOpen < 0f && act.Openness >= 1f) tOpen = Time.time - t0;
            if (tOpen >= 0f && tClosed < 0f && act.Openness <= 0f) { tClosed = Time.time - t0; break; }
        }
        Log($"firing: fired {fired}, beam from the muzzle within {originErr * 100f:0.00} cm, open after {tOpen:0.000} s, lift {maxLift * 100f:0.00} cm, shut after {tClosed:0.000} s, gap glow seen {gapSeen}, slowest frame {slowest * 1000f:0.0} ms");
        Log(fired && LaserWeapon.Shots > shots && originErr < 0.01f ? "PASS the beam leaves from the muzzle" : "FAIL beam origin");
        Log(tOpen > 0f && tOpen < TGunAction.SnapTime + 2f * slowest + 0.01f && Mathf.Abs(maxLift - act.lift) < 0.002f ? "PASS snaps open along the stem in about 0.05 s" : "FAIL snap");
        Log(tClosed > 0f && tClosed < TGunAction.SnapTime + TGunAction.CloseTime + 4f * slowest + 0.01f && Vector3.Distance(act.Upper.localPosition, restPos) < 1e-4f && !act.GapGlow[0].enabled ? "PASS closes in about 0.12 s, back to rest, gap glow off" : "FAIL close");

        float beamLength = act.Beams.GetComponent<MeshFilter>().sharedMesh.bounds.size.y * maxBeam;
        Log($"energy beams: seen {beamsSeen}, length at full open {beamLength * 100f:0.00} cm, off again {!beamBody.enabled}");
        Log(beamsSeen && Mathf.Abs(beamLength - 0.0578f) < 0.001f && !beamBody.enabled ? "PASS energy beams span the open gap, only while open" : "FAIL energy beams");

        // Close-ups in the hand, held open, in both modes (a camera beside the gun; the player's body is shown).
        fpc.zoom.Snap(0.3f);
        fpc.DebugLook(8f, fpc.transform.eulerAngles.y);
        yield return new WaitForSeconds(0.6f);
        var camGo = new GameObject("GunCam");
        var gc = camGo.AddComponent<Camera>();
        gc.depth = cam.depth + 10f; gc.fieldOfView = 30f; gc.nearClipPlane = 0.02f;
        IEnumerator CloseUp(string name)
        {
            yield return null;
            Vector3 c = root.TransformPoint(new Vector3(0f, 0.02f, 0f));
            camGo.transform.position = c - root.right * 0.5f + root.up * 0.12f + root.forward * 0.08f;
            camGo.transform.LookAt(c, root.up);
            yield return null;
            yield return Shot(name);
        }
        yield return CloseUp("close_rest_stun");
        act.Hold = true; act.Fire();
        yield return new WaitForSeconds(0.2f);
        yield return CloseUp("close_open_stun");
        act.Hold = false;
        yield return new WaitForSeconds(0.3f);
        w.ToggleMode();
        bool red = gun.ModeParts[0].sharedMaterial == LaserWeapon.ModeMat(Weapon.Mode.Lethal) && seam.sharedMaterial == seamMat;
        yield return new WaitForSeconds(0.35f);
        yield return CloseUp("close_rest_lethal");
        act.Hold = true; act.Fire();
        yield return new WaitForSeconds(0.2f);
        bool gapRed = act.GapGlow[0].sharedMaterial == LaserWeapon.BeamMat(Weapon.Mode.Lethal) && beamBody.sharedMaterial == LaserWeapon.BeamMat(Weapon.Mode.Lethal);
        yield return CloseUp("close_open_lethal");
        act.Hold = false;
        Log(red && gapRed ? "PASS B swaps handle line, gap glow and energy beams to red; the chrome stays" : "FAIL mode colours");
        yield return new WaitForSeconds(0.3f);
        Destroy(camGo);
        fpc.zoom.Snap(0f);
        w.ToggleMode();
        yield return new WaitForSeconds(0.4f);

        // Rapid fire keeps working (a shot during the close opens it again).
        w.ToggleMode(); yield return new WaitForSeconds(0.35f);
        float minOpen = 1f; int rapid = 0;
        for (float t = 0f; t < 1.2f; t += Time.deltaTime)
        {
            if (w.DebugFire()) rapid++;
            if (rapid > 0) minOpen = Mathf.Min(minOpen, act.Openness);
            yield return null;
        }
        Log($"rapid lethal: {rapid} shots in 1.2 s, lowest openness between shots {minOpen:0.00}");
        w.ToggleMode(); yield return new WaitForSeconds(0.4f);

        // ---------- stun on an officer, through the real gun ----------
        Vector3 me = fpc.transform.position;
        Vector3 ahead = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
        Vector3 spot = me + ahead * 7f;
        var off = OfficerAgent.DebugSpawn(spot, Quaternion.LookRotation(-ahead));
        yield return new WaitForSeconds(1f);
        off.Goal = off.transform.position; off.StopDistance = 0.5f;

        // Speed per level: the officer runs to a far goal while its meter is set directly.
        var sb = new System.Text.StringBuilder("speed curve % by level:");
        for (int i = 0; i < StunMeter.LevelsToFreeze; i++) sb.Append($" {i}:{StunMeter.SpeedAt(i) * 100f:0}");
        Log(sb.ToString());
        IEnumerator Measure(System.Action<float> result)
        {
            Vector3 goal = off.transform.position + ahead * 40f;
            off.SpeedOverride = 5f;
            float t = 0f; Vector3 p0 = Vector3.zero;
            for (; t < 0.8f; t += Time.deltaTime) { off.Goal = goal; if (t < 0.3f) p0 = off.transform.position; yield return null; }
            result(Vector3.Distance(p0, off.transform.position) / 0.5f);
            off.SpeedOverride = null;
            off.Goal = off.transform.position;
        }
        float s0 = 0f, s5 = 0f, s9 = 0f;
        yield return Measure(v => s0 = v);
        for (int i = 0; i < 5; i++) off.StunHit();
        yield return Measure(v => s5 = v);
        for (int i = 0; i < 4; i++) off.StunHit();
        yield return Measure(v => s9 = v);
        Log($"officer speed: level 0 {s0:0.00} m/s, level 5 {s5:0.00} m/s, level 9 {s9:0.00} m/s (factors {StunMeter.SpeedAt(5):0.00}, {StunMeter.SpeedAt(9):0.00})");
        Log(s0 > 3f && s5 < s0 * 0.75f && s9 < s5 * 0.5f && s9 > 0f ? "PASS slower with each level" : "FAIL slow-down");
        off.StunState.Reset();
        yield return new WaitForSeconds(0.2f);

        // Ten hits with the player's gun at the stun fire rate.
        off.Goal = off.transform.position; off.StopDistance = 0.5f;
        for (float t = 0f; t < 1f; t += Time.deltaTime) { LookAt(off.transform.position + Vector3.up * 1f); yield return null; }
        float firstHit = -1f, frozeAt = -1f; int hits = 0, levelErrors = 0;
        for (float t = 0f; t < 14f && frozeAt < 0f; t += Time.deltaTime)
        {
            LookAt(off.transform.position + Vector3.up * 1f);
            float before = off.StunState.Level;
            if (w.DebugFire())
            {
                float after = off.StunState.Level;
                if (after > before + 0.5f)
                {
                    hits++;
                    if (firstHit < 0f) firstHit = Time.time;
                    if (hits < StunMeter.LevelsToFreeze && Mathf.Abs(after - hits) > 0.05f) levelErrors++;
                    if (off.Frozen) frozeAt = Time.time;
                }
            }
            yield return null;
        }
        Log($"hits to freeze: {hits} in {frozeAt - firstHit:0.0} s (level drift errors {levelErrors}), frozen {off.Frozen}");
        Log(hits == StunMeter.LevelsToFreeze && off.Frozen && levelErrors == 0 ? "PASS 10 hits freeze an officer, no drain under steady hits" : "FAIL hits to freeze");
        Log(frozeAt - firstHit > 4.5f && frozeAt - firstHit < 7.5f ? "PASS about 6 s at the stun fire rate" : "FAIL freeze time");

        // Frozen in pose: no joint, arm or position moves for 3 s.
        var fig = off.GetComponent<CharacterFigure>();
        Transform[] joints = { fig.Hips, fig.ElbowR, fig.WristR, fig.ElbowL, off.Gun.transform };
        var rots = new Quaternion[joints.Length];
        yield return null;
        for (int i = 0; i < joints.Length; i++) rots[i] = joints[i].localRotation;
        Vector3 posA = off.transform.position;
        yield return Shot("officer_frozen");
        float worst = 0f;
        while (off.Frozen)
        {
            for (int i = 0; i < joints.Length; i++) worst = Mathf.Max(worst, Quaternion.Angle(rots[i], joints[i].localRotation));
            yield return null;
        }
        float frozenFor = Time.time - frozeAt;
        Log($"frozen for {frozenFor:0.00} s: worst joint change {worst:0.000} deg, moved {Vector3.Distance(posA, off.transform.position):0.000} m, level after {off.StunState.Level:0.0}");
        Log(Mathf.Abs(frozenFor - StunMeter.FreezeTime) < 0.15f && worst < 0.5f && Vector3.Distance(posA, off.transform.position) < 0.02f ? "PASS frozen in pose for 3 s" : "FAIL freeze");
        Log(off.StunState.Level == 0f ? "PASS the meter resets after the freeze" : "FAIL meter reset");

        // Drain: nothing for 3 s, then one level a second.
        for (int i = 0; i < 6; i++) off.StunHit();
        yield return new WaitForSeconds(2.8f);
        float l28 = off.StunState.Level;
        yield return new WaitForSeconds(1.2f);
        float l40 = off.StunState.Level;
        yield return new WaitForSeconds(2f);
        float l60 = off.StunState.Level;
        Log($"drain from 6: {l28:0.00} at 2.8 s, {l40:0.00} at 4 s, {l60:0.00} at 6 s");
        Log(Mathf.Abs(l28 - 6f) < 0.01f && Mathf.Abs(l40 - 5f) < 0.15f && Mathf.Abs(l60 - 3f) < 0.2f ? "PASS drains from 3 s without a hit, 1 level per second" : "FAIL drain");
        off.StunState.Reset();

        // ---------- an officer firing the new gun ----------
        off.StunState.Reset();
        off.Armed = true;
        off.Goal = off.transform.position;
        Vector3 chest = fpc.transform.position + Vector3.up * 1.2f;
        int offShots = 0, offHits = 0; float offOrigin = 0f; bool offOpened = false;
        for (float t = 0f; t < 4f; t += Time.deltaTime)
        {
            off.AimAt(chest);
            Vector3 muzzle = off.Gun.muzzle.position;
            int before = LaserWeapon.Shots;
            if (off.FireStun(chest, 30f, 0.5f)) offHits++;
            if (LaserWeapon.Shots > before) { offShots++; offOrigin = Mathf.Max(offOrigin, Vector3.Distance(LaserWeapon.LastOrigin, muzzle)); }
            offOpened |= off.Gun.Action.Openness > 0f;
            yield return null;
        }
        Log($"officer firing: {offShots} shots, {offHits} on the player, origin within {offOrigin * 100f:0.00} cm of its muzzle, gun opened {offOpened}");
        Log(offShots > 0 && offOrigin < 0.01f && offOpened && offHits > 0 ? "PASS officers fire from the new muzzle and the gun snaps open" : "FAIL officer firing");

        // ---------- a pedestrian ----------
        var peds = PedestrianSystem.Instance;
        PedestrianSystem.Ped ped = null; Collider pc = null;
        if (peds != null && peds.NearAgents.Count == 0 && peds.All.Count > 0)
        {
            fpc.PlaceAt(peds.All[0].pos + Vector3.up * 1f, Quaternion.identity);
            for (float t = 0f; t < 6f && peds.NearAgents.Count == 0; t += Time.deltaTime) yield return null;
        }
        if (peds != null)
            foreach (var p in peds.NearAgents)
            {
                if (p.near == null) continue;
                pc = p.near.GetComponentInChildren<Collider>();
                if (pc != null) { ped = p; break; }
            }
        if (ped == null) Log($"WARN no near pedestrian to test (system {peds != null}, pedestrians {(peds != null ? peds.All.Count : 0)}; run with -scene <a scene with a crowd>)");
        else
        {
            PedestrianSystem.Shot(pc, false, me);
            bool ran = ped.state == PedestrianSystem.State.Fleeing;
            for (int i = 0; i < 8; i++) PedestrianSystem.Shot(pc, false, me);
            float slow = ped.stun.SpeedFactor;
            PedestrianSystem.Shot(pc, false, me);
            bool pedFrozen = ped.state == PedestrianSystem.State.Frozen && ped.stun.Frozen;
            yield return new WaitForSeconds(0.1f);
            Vector3 pp = ped.pos;
            yield return new WaitForSeconds(2.5f);
            float pedMoved = Vector3.Distance(pp, ped.pos);
            yield return new WaitForSeconds(0.7f);
            Log($"pedestrian: ran after the first hit {ran}, speed factor at 9 levels {slow:0.00}, frozen at 10 {pedFrozen}, moved {pedMoved:0.000} m while frozen, then {ped.state}, level {ped.stun.Level:0.0}");
            Log(ran && pedFrozen && pedMoved < 0.05f && ped.state != PedestrianSystem.State.Frozen && ped.stun.Level == 0f ? "PASS pedestrian: builds up, freezes 3 s, runs on" : "FAIL pedestrian stun");
        }

        // ---------- the player ----------
        fpc.StunState.Reset();
        for (int i = 0; i < 3; i++) fpc.StunHit();
        bool stillMoves = !fpc.Stunned && fpc.StunState.SpeedFactor < 0.8f && fpc.StunState.SpeedFactor > 0.6f;
        for (int i = 0; i < 7; i++) fpc.StunHit();
        bool playerFrozen = fpc.Stunned && fpc.Animator != null;
        yield return new WaitForSeconds(0.1f);
        bool animFrozen = fpc.Animator != null && fpc.Animator.Frozen;
        yield return new WaitForSeconds(3f);
        Log($"player: 3 hits speed factor ok {stillMoves}, frozen at 10 {playerFrozen}, animator frozen {animFrozen}, after 3 s stunned {fpc.Stunned}, level {fpc.StunState.Level:0.0}");
        Log(stillMoves && playerFrozen && animFrozen && !fpc.Stunned && fpc.StunState.Level == 0f ? "PASS player: same rules" : "FAIL player stun");

        // Cars are unchanged.
        Log(gun.carHiccup == 0.5f ? "PASS cars: still a 0.5 s hiccup, no damage" : "FAIL car hiccup");
        Destroy(off.gameObject);
    }
}
