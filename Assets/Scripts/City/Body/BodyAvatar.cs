using System.Collections.Generic;
using UnityEngine;

// Phase 6: a Humanoid Avatar for a generated 2-arm, 2-leg body, built at runtime, so Unity's humanoid
// retargeting can drive any generated proportions (standard humanoid clips through an Animator, or a
// muscle-space pose copied from another body with HumanPoseHandler). An option alongside
// FigureAnimator, which stays the default.
// Mecanim takes the skeleton description as the T-pose: the arms are described held out sideways
// (our rest pose has them hanging), everything else as built.
public static class BodyAvatar
{
    static readonly (string human, string joint)[] Map =
    {
        ("Hips", "Hips"), ("Spine", "Spine"), ("Chest", "Chest"), ("Neck", "Neck"), ("Head", "Head"),
        ("LeftUpperArm", "ShoulderL"), ("LeftLowerArm", "ElbowL"), ("LeftHand", "WristL"),
        ("RightUpperArm", "ShoulderR"), ("RightLowerArm", "ElbowR"), ("RightHand", "WristR"),
        ("LeftUpperLeg", "HipL"), ("LeftLowerLeg", "KneeL"), ("LeftFoot", "AnkleL"),
        ("RightUpperLeg", "HipR"), ("RightLowerLeg", "KneeR"), ("RightFoot", "AnkleR"),
    };

    // Null for bodies that aren't 2-arm, 2-leg humans (extra arms stay procedural).
    public static Avatar Build(CharacterFigure f)
    {
        if (!f.Generated || f.Plan == null || f.Plan.legs.Count != 2 || f.Plan.arms.Count < 2) return null;
        var sk = f.GetComponent<BodySkeleton>();
        var human = new List<HumanBone>();
        foreach (var (h, j) in Map)
        {
            if (f.Plan.Index(j) < 0) continue;
            human.Add(new HumanBone { humanName = h, boneName = j, limit = new HumanLimit { useDefaultValues = true } });
        }
        // Skeleton: the root, then every joint in its T-pose transform (local to its parent).
        var skeleton = new List<SkeletonBone>
        {
            new SkeletonBone { name = f.name, position = Vector3.zero, rotation = Quaternion.identity, scale = Vector3.one },
        };
        foreach (var b in sk.Bones)
        {
            var spec = b.spec;
            Quaternion rot = spec.restRotation;
            // Arms out to the sides (they hang along -Y; Z -90 points the left one along -X).
            if (spec.kind == BoneKind.UpperArm && spec.side != 0 && spec.limb >= 0 && f.Plan.arms[spec.limb].girdle == 0)
                rot = Quaternion.Euler(0f, 0f, spec.side < 0 ? -90f : 90f) * rot;
            skeleton.Add(new SkeletonBone { name = b.joint.name, position = spec.localPos, rotation = rot, scale = Vector3.one });
        }
        var desc = new HumanDescription
        {
            human = human.ToArray(),
            skeleton = skeleton.ToArray(),
            upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
            armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0f, hasTranslationDoF = false,
        };
        var avatar = AvatarBuilder.BuildHumanAvatar(f.gameObject, desc);
        avatar.name = "Avatar " + f.Plan.sheet;
        return avatar;
    }

    // Copy one body's pose onto another in muscle space (any proportions). The body position isn't
    // copied (HumanPose.bodyPosition is normalized per body and mixes spaces between Get and Set):
    // the target's hips go to its own height plus the source's bob, scaled by height.
    public static void CopyPose(HumanPoseHandler from, CharacterFigure fromFig, HumanPoseHandler to, CharacterFigure toFig, ref HumanPose scratch)
    {
        from.GetHumanPose(ref scratch);
        to.SetHumanPose(ref scratch);
        float bob = (fromFig.Hips.localPosition.y - fromFig.HipHeight) * toFig.Height / Mathf.Max(0.01f, fromFig.Height);
        toFig.Hips.localPosition = new Vector3(0f, toFig.HipHeight + bob, 0f);
    }

    // Optional humanoid driver: an Animator with the generated avatar (FigureAnimator should be off on
    // the same body while clips play).
    public static Animator AddAnimator(CharacterFigure f, RuntimeAnimatorController controller = null)
    {
        var avatar = Build(f);
        if (avatar == null || !avatar.isValid) return null;
        var an = f.gameObject.GetComponent<Animator>();
        if (an == null) an = f.gameObject.AddComponent<Animator>(); // (no ?? on UnityEngine.Object)
        an.avatar = avatar;
        an.runtimeAnimatorController = controller;
        an.applyRootMotion = false;
        return an;
    }
}
