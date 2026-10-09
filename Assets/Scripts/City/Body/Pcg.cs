using UnityEngine;

// C# port of Assets/Shaders/HashPCG.hlsl (PCG3D, Jarzynski & Olano 2020), bit-exact with the shader.
// All character generation uses this, never UnityEngine.Random: same seed = same body, on any machine.
public static class Pcg
{
    public static Vector3Int Pcg3d(uint x, uint y, uint z)
    {
        unchecked
        {
            x = x * 1664525u + 1013904223u;
            y = y * 1664525u + 1013904223u;
            z = z * 1664525u + 1013904223u;
            x += y * z; y += z * x; z += x * y;
            x ^= x >> 16; y ^= y >> 16; z ^= z >> 16;
            x += y * z; y += z * x; z += x * y;
            return new Vector3Int((int)x, (int)y, (int)z);
        }
    }

    public static uint Hash(uint x, uint y, uint z) => (uint)Pcg3d(x, y, z).x;

    // Top 24 bits to [0, 1), exact in float (same as the shader's U01).
    public static float U01(uint x) => (x >> 8) * (1f / 16777216f);

    // Stable id for a name (FNV-1a), so each feature draws from its own stream: adding a new random
    // feature later never shifts the values of existing ones.
    public static uint StreamId(string name)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in name) { h ^= c; h *= 16777619u; }
            return h;
        }
    }
}

// A deterministic random stream: (seed, stream) -> values. Value n is Pcg3d(seed, stream, n).
public struct PcgRandom
{
    readonly uint seed, stream;
    uint n;

    public PcgRandom(int seed, string stream) : this(seed, Pcg.StreamId(stream)) { }
    public PcgRandom(int seed, uint stream) { this.seed = unchecked((uint)seed + 32768u); this.stream = stream; n = 0; }

    public uint NextUInt() => Pcg.Hash(seed, stream, n++);
    public float Value() => Pcg.U01(NextUInt());
    public float Range(float a, float b) => a + (b - a) * Value();
    public int Range(int a, int bExclusive) => a + (int)(NextUInt() % (uint)Mathf.Max(1, bExclusive - a));

    // Standard normal (Box-Muller).
    public float Normal()
    {
        float u1 = Mathf.Max(1e-7f, Value()), u2 = Value();
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
    }

    // Symmetric noise in [-1, 1], peaked at 0 (triangular): good for "a few percent" variation.
    public float Signed() => Value() + Value() - 1f;
}
