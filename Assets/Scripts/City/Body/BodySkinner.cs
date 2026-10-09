using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

// Phase 5: skin weights and skinned renderers for a generated body.
//   Weights: per vertex, the distance to every bone's core (the SDF's own per-bone distance, so the
//   flattened torso, mitten hands and the skull count as they're shaped), weight = a smooth falloff of
//   that distance. Only the vertex's own bone (nearest core: its muscle, breast, fat; boosted) and that
//   bone's parent and children take part, and a neighbour only near the joint they share (fading out
//   over jointReach x the joint's radius): a back vertex near the arm in the A-pose doesn't follow the
//   arm, an armpit vertex does. The 4 strongest are kept and normalized. At a joint the two bones are
//   about equally near, so elbows and knees split their weight and bend smoothly.
//   Digits: a hand vertex on (or near) a finger or thumb segment goes to that segment's bone, blending
//   into the one before it (or the hand, at the knuckle) and the one after it over knuckle x the radius
//   either side of each joint.
//   Bind poses: each bone's transform in the pose the mesh was generated in (the A-pose), relative to the
//   body root, so the mesh follows the skeleton from any pose.
//   Renderers: body and head as two SkinnedMeshRenderers (first person hides only the head).
public static class BodySkinner
{
    // Defaults from BodyRules.skinning.
    public class Settings
    {
        public Settings()
        {
            var r = BodyRules.Default.skinning;
            falloffPower = r.falloffPower; softness = r.softness; ownBoneBoost = r.ownBoneBoost; jointReach = r.jointReach;
        }
        public float falloffPower = 4f;     // weight = 1 / (distance + softness)^power
        public float softness = 0.012f;     // m: how far a joint's influence spreads
        public float ownBoneBoost = 3f;     // the vertex's own (nearest-core) bone
        public float jointReach = 1.6f;     // neighbours count within this many joint radii of the joint
    }

    [BurstCompile]
    internal struct WeightJob : IJobParallelFor
    {
        public BodySdfKernel kernel;
        [ReadOnly] public NativeArray<Vector3> positions;
        public float power, softness, boost, reach, knuckle;
        [WriteOnly] public NativeArray<BoneWeight> weights;
        public void Execute(int v)
        {
            Vector3 p = positions[v];
            int n = kernel.bones.Length;
            int i0 = 0, i1 = 0, i2 = 0, i3 = 0;
            float w0 = 0f, w1 = 0f, w2 = 0f, w3 = 0f;
            float best = float.MaxValue; int own = 0;
            for (int i = 0; i < n; i++) { float d = kernel.BoneCore(i, p); if (d < best) { best = d; own = i; } }
            var ob = kernel.bones[own];
            for (int i = 0; i < n; i++)
            {
                // Own bone, its parent (joint at the own bone's start) or a child (joint at its start).
                float near;
                if (i == own) near = boost;
                else
                {
                    var bi = kernel.bones[i];
                    Vector3 joint;
                    if (i == ob.parent) joint = ob.o;
                    else if (bi.parent == own) joint = bi.o;
                    else continue;
                    float jr = Mathf.Max(Mathf.Max(ob.outer.y, bi.outer.y), 0.02f) * reach;
                    near = Mathf.Clamp01(1.5f - (p - joint).magnitude / jr);
                    if (near <= 0f) continue;
                }
                float d = Mathf.Max(0f, kernel.BoneCore(i, p));
                float w = near / Mathf.Pow(d + softness, power);
                // Insert into the top 4.
                if (w > w0) { i3 = i2; w3 = w2; i2 = i1; w2 = w1; i1 = i0; w1 = w0; i0 = i; w0 = w; }
                else if (w > w1) { i3 = i2; w3 = w2; i2 = i1; w2 = w1; i1 = i; w1 = w; }
                else if (w > w2) { i3 = i2; w3 = w2; i2 = i; w2 = w; }
                else if (w > w3) { i3 = i; w3 = w; }
            }
            float sum = w0 + w1 + w2 + w3;
            if (sum <= 0f) { i0 = own; w0 = 1f; sum = 1f; }
            // Digits: the nearest sausage of this hand takes its share (all of it past the knuckle).
            if (ob.kind == (int)BoneKind.Hand && kernel.digits.IsCreated)
            {
                int dj = -1; float dd = float.MaxValue;
                for (int j = 0; j < kernel.digits.Length; j++)
                {
                    var dg = kernel.digits[j];
                    if (dg.hand != own) continue;
                    Vector3 pa = p - dg.a, ba = dg.b - dg.a;
                    float h = Mathf.Clamp01(Vector3.Dot(pa, ba) / Mathf.Max(1e-8f, ba.sqrMagnitude));
                    float d = (pa - ba * h).magnitude - dg.r;
                    if (d < dd) { dd = d; dj = j; }
                }
                if (dj >= 0)
                {
                    var dg = kernel.digits[dj];
                    float t = Vector3.Dot(p - dg.a, (dg.b - dg.a).normalized);
                    float blend = Mathf.Max(1e-4f, knuckle * dg.r);
                    float onDigit = Mathf.Clamp01(1.5f - Mathf.Max(0f, dd) / dg.r);
                    float fromStart = Mathf.SmoothStep(0f, 1f, 0.5f + t / (2f * blend));
                    if (dg.parentBone != own)
                    {
                        // Past the knuckle: all on the digit, split between this segment and its neighbours.
                        float toParent = 1f - fromStart;
                        float toChild = dg.childBone >= 0 ? 1f - Mathf.SmoothStep(0f, 1f, 0.5f + (dg.length - t) / (2f * blend)) : 0f;
                        float self = Mathf.Max(0f, 1f - toParent - toChild);
                        float hand = 1f - onDigit;   // (only hand vertices near a digit come here)
                        i0 = dg.bone; w0 = self * onDigit;
                        i1 = dg.parentBone; w1 = toParent * onDigit;
                        i2 = dg.childBone >= 0 ? dg.childBone : own; w2 = dg.childBone >= 0 ? toChild * onDigit : 0f;
                        i3 = own; w3 = hand;
                        sum = w0 + w1 + w2 + w3;
                        if (sum <= 0f) { w0 = 1f; sum = 1f; }
                        weights[v] = new BoneWeight
                        {
                            boneIndex0 = i0, weight0 = w0 / sum, boneIndex1 = i1, weight1 = w1 / sum,
                            boneIndex2 = i2, weight2 = w2 / sum, boneIndex3 = i3, weight3 = w3 / sum,
                        };
                        return;
                    }
                    float toNext = dg.childBone >= 0 ? 1f - Mathf.SmoothStep(0f, 1f, 0.5f + (dg.length - t) / (2f * blend)) : 0f;
                    float share = fromStart * onDigit;
                    if (toNext > 0f && share > 0f)
                    {
                        // First segment near its far joint: shares with the next (all on the digit by then).
                        i0 = dg.bone; w0 = share * (1f - toNext);
                        i1 = dg.childBone; w1 = share * toNext;
                        i2 = own; w2 = 1f - share; i3 = own; w3 = 0f;
                        sum = w0 + w1 + w2;
                        weights[v] = new BoneWeight { boneIndex0 = i0, weight0 = w0 / sum, boneIndex1 = i1, weight1 = w1 / sum, boneIndex2 = i2, weight2 = w2 / sum, boneIndex3 = i3, weight3 = 0f };
                        return;
                    }
                    if (share > 0f)
                    {
                        // Scale the others down; the digit replaces the weakest.
                        float k = (1f - share) / sum;
                        w0 *= k; w1 *= k; w2 *= k; w3 *= k;
                        if (share >= w3) { i3 = dg.bone; w3 = share; }
                        sum = w0 + w1 + w2 + w3;
                    }
                }
            }
            weights[v] = new BoneWeight
            {
                boneIndex0 = i0, weight0 = w0 / sum, boneIndex1 = i1, weight1 = w1 / sum,
                boneIndex2 = i2, weight2 = w2 / sum, boneIndex3 = i3, weight3 = w3 / sum,
            };
        }
    }

    public static BoneWeight[] Weights(BodySDF sdf, Mesh mesh, Settings s = null)
    {
        s ??= new Settings();
        var kernel = sdf.BuildKernel(Allocator.TempJob);
        var pos = new NativeArray<Vector3>(mesh.vertices, Allocator.TempJob);
        var w = new NativeArray<BoneWeight>(pos.Length, Allocator.TempJob);
        new WeightJob { kernel = kernel, positions = pos, power = s.falloffPower, softness = s.softness, boost = s.ownBoneBoost, reach = s.jointReach,
                        knuckle = BodyRules.Default.hands.knuckleBlend, weights = w }
            .Schedule(pos.Length, 64).Complete();
        var result = w.ToArray();
        pos.Dispose(); w.Dispose(); kernel.Dispose();
        return result;
    }

    // Bind poses: inverse of each bone's A-pose transform relative to the body root.
    public static Matrix4x4[] BindPoses(BodySDF sdf)
    {
        var b = new Matrix4x4[sdf.bind.Length];
        for (int i = 0; i < b.Length; i++) b[i] = sdf.bind[i].inverse;
        return b;
    }

    // Split a mesh into body and head parts (triangles with at least two head vertices go to the head),
    // each with its own vertices, weights and the shared bind poses.
    public static (Mesh body, Mesh head) Split(Mesh mesh, BoneWeight[] weights, Matrix4x4[] bindposes)
    {
        var verts = mesh.vertices; var normals = mesh.normals;
        var uv1 = new List<Vector2>(); mesh.GetUVs(1, uv1);
        var uv2 = new List<Vector2>(); mesh.GetUVs(2, uv2);
        var tris = mesh.triangles;
        bool IsHead(int v) => (BoneKind)(int)uv1[v].x == BoneKind.Head;
        Mesh Build(string name, System.Func<int, bool> take)
        {
            var map = new Dictionary<int, int>();
            var nv = new List<Vector3>(); var nn = new List<Vector3>(); var n1 = new List<Vector2>(); var n2 = new List<Vector2>();
            var nw = new List<BoneWeight>(); var nt = new List<int>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                if (!take(t)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = tris[t + k];
                    if (!map.TryGetValue(v, out int m))
                    {
                        m = nv.Count; map[v] = m;
                        nv.Add(verts[v]); nn.Add(normals[v]); n1.Add(uv1[v]); n2.Add(uv2[v]); nw.Add(weights[v]);
                    }
                    nt.Add(m);
                }
            }
            var mm = new Mesh { name = name, indexFormat = nv.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mm.SetVertices(nv); mm.SetNormals(nn); mm.SetUVs(1, n1); mm.SetUVs(2, n2);
            mm.SetTriangles(nt, 0);
            mm.boneWeights = nw.ToArray();
            mm.bindposes = bindposes;
            mm.RecalculateBounds();
            return mm;
        }
        int Heads(int t) => (IsHead(tris[t]) ? 1 : 0) + (IsHead(tris[t + 1]) ? 1 : 0) + (IsHead(tris[t + 2]) ? 1 : 0);
        return (Build(mesh.name + " body", t => Heads(t) < 2), Build(mesh.name + " head", t => Heads(t) >= 2));
    }

    public static SkinnedMeshRenderer AddRenderer(Transform root, string name, Mesh mesh, Transform[] bones, Transform rootBone, Material mat, bool shadows)
    {
        var go = new GameObject(name);
        go.layer = root.gameObject.layer;
        go.transform.SetParent(root, false);
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = bones;
        smr.rootBone = rootBone;
        smr.sharedMaterial = mat;
        smr.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        smr.receiveShadows = shadows;
        // Bounds around the root bone, big enough for any pose (arms up, lying down).
        smr.localBounds = new Bounds(Vector3.zero, Vector3.one * 2.6f);
        return smr;
    }
}
