#ifndef FPS_HASH_PCG_INCLUDED
#define FPS_HASH_PCG_INCLUDED

// Integer hashing for procedural shaders. Only ever feed it integer coordinates (floored cells,
// quantized seeds): the result is then exactly constant across a cell, at any distance and any
// world position. No float multiply-and-frac hashes.

// PCG3D (Jarzynski & Olano, "Hash Functions for GPU Rendering", 2020).
uint3 Pcg3d(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v;
}

// Top 24 bits to a float in [0, 1). Exact in float, never reaches 1.
float U01(uint x) { return (float)(x >> 8) * (1.0 / 16777216.0); }

// Signed ints to a hash; offset keeps typical negative coordinates in a sane unsigned range.
uint3 HashInt3(int3 p) { return Pcg3d((uint3)(p + 32768)); }

#endif
