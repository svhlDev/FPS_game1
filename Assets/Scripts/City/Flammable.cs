using UnityEngine;

// Something that can catch fire: cars (heat 4), characters (2), props like crates and stalls (3).
// Burning characters take 12 damage a second (CharacterHealth; the player on FirstPersonController).
public class Flammable : MonoBehaviour
{
    public enum Kind { Car, Character, Prop }
    public Kind kind = Kind.Prop;
    public float ignitionHeat = 3f;
    public float Exposure { get; set; }
    public bool Burning { get; private set; }
    public float BurningUntil { get; private set; }
    float extraHeat, extraHeatTime;

    public static Flammable Add(GameObject go, Kind kind)
    {
        var f = go.GetComponent<Flammable>() ?? go.AddComponent<Flammable>();
        f.kind = kind;
        f.ignitionHeat = kind == Kind.Car ? 4f : kind == Kind.Character ? 2f : 3f;
        return f;
    }

    void OnEnable() => FireSystem.Register(this);
    void OnDisable() => FireSystem.Unregister(this);

    // A direct hit by a chunk: heat beyond what the grid says, plus burn damage for characters.
    public void AddHeat(float amount, float damage)
    {
        extraHeat = (Time.time - extraHeatTime < 1.5f ? extraHeat : 0f) + amount;
        extraHeatTime = Time.time;
        if (kind == Kind.Character)
        {
            var fpc = GetComponent<FirstPersonController>();
            if (fpc != null) fpc.Damage(damage, "Burned to death");
            else
            {
                var h = GetComponent<CharacterHealth>();
                if (h != null) h.Damage(new DamageInfo { amount = damage, kind = DamageKind.Fire, point = transform.position + Vector3.up });
            }
        }
        if (extraHeat >= ignitionHeat) Ignite();
    }

    // Out (a pooled pedestrian reused for someone else).
    public void Extinguish() { Burning = false; Exposure = 0f; extraHeat = 0f; }

    public void Ignite()
    {
        switch (kind)
        {
            case Kind.Car:
                var h = GetComponent<VehicleHealth>();
                if (h != null) h.Ignite();
                break;
            case Kind.Character:
                var fpc = GetComponent<FirstPersonController>();
                if (fpc != null) fpc.Ignite();
                else
                {
                    Burning = true;
                    BurningUntil = Time.time + 6f;
                    PedestrianSystem.ReportDanger(transform.position, 6f); // the burning one panics, people around run
                }
                break;
            default:
                Burning = true;
                BurningUntil = Time.time + 60f;
                break;
        }
    }

    void Update()
    {
        if (!Burning || kind == Kind.Car) return;
        if (Time.time > BurningUntil) { Burning = false; return; }
        Effects.Flame(transform.position + Vector3.up * (kind == Kind.Character ? 1f : 0.8f), kind == Kind.Character ? 1.2f : 1.6f);
        if (Random.value < Time.deltaTime * 0.5f) FireSystem.Spray(transform.position + Vector3.up, Vector3.zero, 1, 1f, 3f);
    }
}
