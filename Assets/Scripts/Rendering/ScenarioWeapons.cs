using System.Collections;
using System.IO;
using UnityEngine;

// The weapons pass, in a built player: -scenario weapons (the player starts unarmed).
//   the armory rack is up beside the police station door with six weapons; E takes each one into its
//   slot (the rack's copy goes, then restocks); each weapon fires and does its thing: scatter = 9 beams,
//   arc = a chain, limpet = a charge that sticks and goes off on command, repulsor = a blast that moves
//   things, singularity = an orb that opens and collapses; ammo goes down and E on the rack refills it.
public partial class ScenarioTest
{
    IEnumerator WeaponsTest()
    {
        var fpc = FirstPersonController.Instance;
        var w = fpc.GetComponent<PlayerWeapon>();
        var interact = fpc.GetComponent<PlayerInteract>();
        var cam = fpc.playerCamera;
        string dir = Path.GetDirectoryName(Application.dataPath);
        int playerMask = 1 << Mathf.Max(0, LayerMask.NameToLayer("Player"));
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
        yield return new WaitForSeconds(1f);

        // ---------- 0. the rack ----------
        var station = PoliceStation.Find();
        var rack = WeaponRack.All.Count > 0 ? WeaponRack.All[0] : WeaponRack.EnsureAtStation();
        if (rack == null || station == null) { Log($"FAIL no armory rack (station {(station != null ? "found" : "missing")})"); yield break; }
        rack.reportTheft = false; // the test isn't about the police
        float fromDoor = station.door != null ? Vector3.Distance(Flat2(rack.transform.position), Flat2(station.door.position)) : -1f;
        Log($"rack at {rack.transform.position}, {fromDoor:0.0} m from the station door, {rack.Slots.Count} slots");
        Log(fromDoor > 0f && fromDoor < 12f && rack.Slots.Count == Weapon.KindCount ? "PASS the armory rack stands by the station door with every weapon" : "FAIL rack placement / slots");
        Log(!w.Armed ? "PASS the player starts unarmed" : "FAIL the player starts armed");

        Vector3 fwd = rack.transform.forward;
        fpc.Heal();
        fpc.PlaceAt(Ground(rack.transform.position + fwd * 1.5f), Quaternion.LookRotation(-fwd));
        yield return new WaitForSeconds(0.8f);
        LookAt(rack.transform.position + Vector3.up * 1.4f);
        yield return null; yield return null;
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "weapons_rack.png"));
        yield return null; yield return null;

        // ---------- 1. take every weapon ----------
        int took = 0;
        foreach (var s in rack.Slots)
        {
            LookAt(rack.SlotCenter(s));
            yield return null; yield return null;
            string p = interact.DebugPrompt();
            bool used = interact.DebugUse();
            yield return null;
            bool ok = w.Has(s.kind) && !s.Stocked && p.Contains("take");
            if (ok) took++;
            Log($"  slot {s.kind}: prompt '{p}', used {used}, have {w.Has(s.kind)}, rack copy {(s.Stocked ? "still there" : $"gone, restocks in {s.RestockIn:0} s")}");
        }
        Log(took == Weapon.KindCount ? "PASS E takes each weapon off the rack into its slot" : $"FAIL took {took}/{Weapon.KindCount}");

        // Face the street to fire.
        Vector3 target = Ground(rack.transform.position + fwd * 14f) + Vector3.up * 0.3f;
        fpc.DebugLook(0f, Quaternion.LookRotation(fwd).eulerAngles.y);
        yield return new WaitForSeconds(0.3f);

        IEnumerator Ready(Weapon.Kind k)
        {
            w.Select(k);
            float t = Time.time;
            while (Time.time - t < 1.5f && !w.Aiming) yield return null;
            yield return new WaitForSeconds(0.45f);
            LookAt(target);
            yield return null; yield return null;
        }

        // ---------- 2. scatter: nine beams a trigger pull ----------
        yield return Ready(Weapon.Kind.Scatter);
        int beams0 = LaserWeapon.Shots, ammo0 = w.Gun.ammo;
        bool fired = w.DebugFire();
        yield return null;
        Log($"scatter: fired {fired}, beams {LaserWeapon.Shots - beams0}, ammo {ammo0} -> {w.Gun.ammo}");
        Log(fired && LaserWeapon.Shots - beams0 >= PlayerWeapon.ScatterBeams && w.Gun.ammo == ammo0 - 1 ? "PASS the scatter laser fires 9 beams for one cell" : "FAIL scatter");

        // ---------- 3. arc caster ----------
        yield return Ready(Weapon.Kind.Arc);
        int casts0 = ArcCaster.Casts;
        fired = w.DebugFire();
        yield return new WaitForSeconds(0.1f);
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "weapons_arc.png"));
        yield return null;
        Log($"arc: fired {fired}, casts {ArcCaster.Casts - casts0}, chain {ArcCaster.LastChainLength}");
        Log(fired && ArcCaster.Casts == casts0 + 1 ? "PASS the arc caster casts" : "FAIL arc");

        // ---------- 4. limpet: sticks, waits, goes off on command ----------
        yield return Ready(Weapon.Kind.Limpet);
        int stuck0 = LimpetCharge.Stuck, boom0 = LimpetCharge.Exploded;
        fired = w.DebugFire();
        float tl = Time.time;
        while (Time.time - tl < 3f && LimpetCharge.Stuck == stuck0) yield return null;
        yield return new WaitForSeconds(0.5f);
        bool waited = LimpetCharge.Exploded == boom0;
        int set = w.Detonate();
        yield return new WaitForSeconds(0.5f);
        Log($"limpet: fired {fired}, stuck {LimpetCharge.Stuck - stuck0}, still armed before detonating {waited}, detonated {set}, exploded {LimpetCharge.Exploded - boom0}");
        Log(fired && LimpetCharge.Stuck > stuck0 && waited && LimpetCharge.Exploded > boom0 ? "PASS limpet charges stick and go off on command" : "FAIL limpet");

        // ---------- 5. repulsor ----------
        yield return Ready(Weapon.Kind.Repulsor);
        int blasts0 = Repulsor.Blasts;
        fired = w.DebugFire();
        yield return new WaitForSeconds(0.15f);
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "weapons_repulsor.png"));
        yield return null;
        Log($"repulsor: fired {fired}, blasts {Repulsor.Blasts - blasts0}, moved {Repulsor.LastMoved}");
        Log(fired && Repulsor.Blasts == blasts0 + 1 ? "PASS the repulsor blasts" : "FAIL repulsor");

        // ---------- 6. singularity ----------
        yield return Ready(Weapon.Kind.Singularity);
        int open0 = SingularityOrb.Opened, coll0 = SingularityOrb.Collapsed;
        fired = w.DebugFire();
        float ts = Time.time;
        while (Time.time - ts < 3f && SingularityOrb.Opened == open0) yield return null;
        yield return new WaitForSeconds(1.2f);
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "weapons_singularity.png"));
        yield return null;
        while (Time.time - ts < 8f && SingularityOrb.Collapsed == coll0) yield return null;
        Log($"singularity: fired {fired}, opened {SingularityOrb.Opened - open0}, collapsed {SingularityOrb.Collapsed - coll0} after {Time.time - ts:0.0} s");
        Log(fired && SingularityOrb.Opened > open0 && SingularityOrb.Collapsed > coll0 ? "PASS the singularity opens and collapses" : "FAIL singularity");

        // ---------- 7. refill at the rack ----------
        var scatter = w.Get(Weapon.Kind.Scatter);
        int before = scatter.ammo;
        fpc.Heal();
        fpc.PlaceAt(Ground(rack.transform.position + fwd * 1.5f), Quaternion.LookRotation(-fwd));
        yield return new WaitForSeconds(0.6f);
        var slot = rack.Slots[(int)Weapon.Kind.Scatter];
        LookAt(rack.SlotCenter(slot));
        yield return null; yield return null;
        string rp = interact.DebugPrompt();
        interact.DebugUse();
        yield return null;
        Log($"refill: prompt '{rp}', scatter {before} -> {scatter.ammo}/{scatter.maxAmmo}");
        Log(rp.Contains("refill") && scatter.ammo == scatter.maxAmmo ? "PASS E on the rack refills a weapon you carry" : "FAIL refill");
    }
}
