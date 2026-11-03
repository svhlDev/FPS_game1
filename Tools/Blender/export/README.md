# Flying car: Unity export

Game-ready export of the flying car from `Tools/Blender/flying_car_v15_generator_detail.blend` (collection `Export` plus the original small parts).

| File | What it is |
|---|---|
| `flying_car_game.fbx` | Whole car, one hierarchy under `Car_Root`. Meshes, empties (sockets, flame and steam locators) and lights. No animation. |
| `textures/FlyingCar_Body_*.png` | 2048 x 2048 baked maps for the body (`Body` mesh, material `FC_Body_Baked`). |
| `textures/flames/flame_flipbook_t*.png` | Thruster flame flipbooks, see `Tools/Blender/textures/flame_flipbook_README.md`. |

Texture paths in the FBX are relative (`textures\FlyingCar_Body_*.png`), so copy the FBX and the `textures` folder together.

## Import settings (Model tab)

| Setting | Value | Why |
|---|---|---|
| Scale Factor | 1 | The file is in metres (UnitScaleFactor 100 cm). |
| Convert Units | On | 1 Unity unit = 1 m. The car is 4.91 m long, 2.51 m wide and 1.75 m tall overall (front fan bottom to canopy top). |
| Bake Axis Conversion | Off | The file header is the standard Y up / Z front / X right-handed one, so Unity adds no axis rotation. `Car_Root` has no rotation and no scale. |
| Import Lights | Optional | 15 point/spot lights come in. Intensities need retuning in Unity (see Lights). |
| Import Cameras | Off | There are none. |
| Normals | Import | Weighted custom normals from Blender. |
| Tangents | Calculate Mikktspace | The normal map was baked in MikkTSpace on the same triangulation (the FBX is pre-triangulated). |
| Rig / Animation | None / off | |

Orientation in Unity: nose points to +Z, up is +Y, the driver side (Blender left) is -X.

Height: the lowest point (bottom of the front fan) is 0.685 m below `Car_Root`. In Blender the root hovers 1.25 m above the ground, which leaves 0.565 m of clearance. Put the root 1.25 m above the ground for the same hover, or 0.69 m above it to rest the car on the ground.

## Body material (URP Lit)

| Slot | Texture | Texture import |
|---|---|---|
| Base Map | `FlyingCar_Body_BaseColor.png` | sRGB on |
| Metallic Map | `FlyingCar_Body_MetallicSmoothness.png` (R = metallic, A = smoothness) | sRGB off. Smoothness Source: Metallic Alpha. Smoothness slider 1. |
| Normal Map | `FlyingCar_Body_Normal.png` (16 bit, OpenGL +Y) | Texture Type: Normal map |
| Occlusion Map | `FlyingCar_Body_AO.png` | sRGB off |
| Emission Map | `FlyingCar_Body_Emission.png` | sRGB on. Emission colour white, HDR intensity 1.79 (= x 3.463, the PNG is normalised to its peak). |

`FlyingCar_Body_Roughness.png` is the same data as the smoothness alpha, inverted. URP does not need it; it is there for HDRP or other engines.

The base colour already contains the paint, edge wear, corner chips, scratches, dirt, soot and grime from Phase 6, and the colour of the metal parts baked into the body (vents, plates, rivets). The AO is 0.5 m distance and includes the occlusion from the fusion generator on the deck.

## Small part materials

Everything except the body keeps simple flat Blender materials. The FBX only carries their base colour, so set these up by hand in URP Lit. Smoothness = 1 - roughness. Emission values are linear multipliers from Blender; treat them as starting points and match the look with your post exposure and bloom.

| Material | Base colour (linear) | Metallic | Smoothness | Emission | Surface |
|---|---|---|---|---|---|
| FC_DarkMetal | 0.022, 0.024, 0.028 | 1 | 0.62 | | Opaque |
| FC_Gunmetal | 0.075, 0.080, 0.088 | 1 | 0.70 | | Opaque |
| FC_Chrome | 0.198, 0.198, 0.203 | 1 | 0.92 | | Opaque |
| FC_Reflector | 0.92, 0.92, 0.94 | 1 | 0.94 | | Opaque |
| FC_Rubber | 0.012, 0.012, 0.013 | 0 | 0.25 | | Opaque |
| Seal_Rubber (canopy seal) | 0.011, 0.012, 0.013 | 0 | 0.57 | | Opaque. In Blender the roughness varies 0.32 to 0.55 (orange peel); a flat 0.57 smoothness is close. |
| FC_DecalWhite (generator housing) | 0.85, 0.86, 0.84 | 0 | 0.60 | | Opaque |
| FC_Pearl (fins) | 0.85, 0.87, 0.90 | 0.6 | 0.75 | | Opaque |
| FC_SeatFabric | 0.030, 0.031, 0.036 | 0 | 0.15 | | Opaque |
| FC_SeatAccent | 0.00, 0.32, 0.42 | 0 | 0.45 | | Opaque |
| FC_InteriorTrim | 0.035, 0.037, 0.042 | 0 | 0.30 | | Opaque |
| FC_Glass (canopy, headlight lenses) | 0.80, 0.80, 0.81 | 0 | 0.98 | | See Glass below |
| FC_GlowFanRing | black | 0 | 0.6 | 0.00, 0.85, 1.00 x 30 | Opaque |
| FC_GlowCore | black | 0 | 0.6 | 0.10, 0.90, 1.00 x 14 | Opaque |
| FC_GlowFusion (fusion core) | black | 0 | 0.6 | 0.40, 0.90, 1.00 x 25 | Opaque |
| FC_GlowThrustRing | black | 0 | 0.6 | 1.00, 0.36, 0.08 x 10 | Opaque |
| FC_GlowHeadlight | black | 0 | 0.6 | 1.00, 0.97, 0.92 x 45 | Opaque |
| FC_GlowDRL | black | 0 | 0.6 | 0.92, 0.96, 1.00 x 14 | Opaque |
| FC_GlowLightBar | black | 0 | 0.6 | 1.00, 0.95, 0.88 x 8 | Opaque |
| FC_DashScreen | black | 0 | 0.7 | 0.15, 0.70, 1.00 x 6 | Opaque |
| FC_GlowRed (tail, fin tips) | black | 0 | 0.6 | 1.00, 0.012, 0.006 x 5 | Opaque |
| FC_GlowCyan (side strips) | black | 0 | 0.6 | 0.00, 0.85, 1.00 x 18 | Opaque |

In Unity's HDR colour picker the intensity is in stops: intensity = log2(multiplier), for example x 30 is about 4.9.

### Glass

Since v12 the canopy is one thin shell (5 mm, both faces and the rim in the mesh). In Blender it is clear transmission glass: transmission 1, roughness 0.02, IOR 1.45, a light smoked grey tint (0.80, 0.80, 0.81). URP Lit has no refraction, so the closest match is:

- Surface Type Transparent, Blend Mode Alpha, base colour 0.80, 0.80, 0.81 with alpha about 0.12, Smoothness 0.98, Metallic 0.
- Render Face Both is not needed (the shell has real inner and outer faces). Leave it on Front.
- If you want real refraction, use the HDRP Lit shader with Refraction Model Thin and IOR 1.45, or a custom URP shader that samples the opaque texture.

## Hierarchy and pivots

```
Car_Root                      empty, identity transform, custom props: throttle, throttle_FL/FR/RL/RR, throttle_Rear_L/R, steam
  Body                        baked low-poly body, pivot = Car_Root
  Canopy, Canopy_Seal, Fin_L, Fin_R
  Fan_1_Front, Fan_2, Fan_3, Fan_4_Rear     pivot at fan centre, spin about local +Y
  Thruster_L, Thruster_R                    pivot at thruster centre, local +Y points forward (nose)
    FX_Flame_Rear_L / _R                    flame locator at nozzle exit
  Nozzle_Under_FL, _FR, _RL, _RR            corner nozzles
    FX_Flame_FL / FR / RL / RR              flame locator at nozzle exit
  Headlight_L, Headlight_R
  Light_Side_L, Light_Side_R, Light_Tail    emissive strips
  Greeble_Antenna_Cluster, Greeble_LightBar, Greeble_Pipes_L, Greeble_Pipes_R
  Fusion_Generator_Body                     generator housing on the rear deck (driver side), panel grooves cut in
    Fusion_Core, Light_Glow_Fusion
    Greeble_Fusion_*                        27 detail parts (vents, heat sink, pipes, bolts, panels, hatches, lugs, gauge, conduit, arm, clamps)
      Greeble_Fusion_Top_Vent -> FX_Steam_Fusion_TopVent
      Greeble_Fusion_RearRail -> FX_Steam_Fusion_RearVent
  FX_Steam_RoofSlope_L / _R, FX_Steam_Side_*   steam locators on vents that are baked into the body
  Cabin_Origin                              interior contract frame
    Cabin_InnerSkin
    Socket_Seat_L -> Kit_Seat_L
    Socket_Seat_R -> Kit_Seat_R
    Socket_Dash   -> Kit_Dash -> Socket_Yoke -> Kit_Yoke
  Light_Glow_*, Light_Beam_Headlight_L/R, Light_Cabin
```

Fan spin: `fan.Rotate(0f, degreesPerSecond * Time.deltaTime, 0f, Space.Self);`

Positions in Unity (car root space, metres):

| Object | Position |
|---|---|
| Fan_1_Front / Fan_2 / Fan_3 / Fan_4_Rear | (0, -0.611, 1.642) / (0, -0.497, 0.433) / (0, -0.497, -0.775) / (0, -0.589, -1.984) |
| Thruster_L / Thruster_R | (-0.866, -0.359, -2.257) / (0.866, -0.359, -2.257) |
| FX_Flame_FL / FR | (-0.950, -0.671, 1.700) / (0.950, -0.671, 1.700) |
| FX_Flame_RL / RR | (-0.950, -0.671, -2.030) / (0.950, -0.671, -2.030) |
| FX_Flame_Rear_L / Rear_R | (-0.866, -0.359, -2.565) / (0.866, -0.359, -2.565) |
| Fusion_Generator_Body / Fusion_Core | (-0.540, 0.524, -1.992) / (-0.405, 0.635, -2.016) |
| Cabin_Origin | (0, -0.380, 0.535) |

## Flames

The volume flame does not export. The FBX gives six locators instead, placed at the nozzle exits.

- Flame direction is the locator's local +Y (`transform.up`). Corner flames point straight down, rear flames point straight back (-Z).
- A Unity Particle System emits along its own +Z, so add it as a child of the locator with local rotation (-90, 0, 0).
- Locator custom properties: `flame_length` (0.5 m corner, 1.4 m rear), `flame_radius` (0.05 m corner, 0.12 m rear), `throttle_override_property` (the `Car_Root` property that overrides this flame).
- Throttle: each flame uses its own `throttle_*` value on `Car_Root` when it is 0 or above, otherwise the global `throttle`.
- Flipbook setup, scale per throttle, and light formulas are in `Tools/Blender/textures/flame_flipbook_README.md`.

## Steam

The vent steam is a volume in Blender and does not export either. Ten locators mark where it comes out:

| Locator | Position (Unity) | Rises toward (Unity) | Length (m) | Start radius / end radius (m) | Base stretch | Density |
|---|---|---|---|---|---|---|
| FX_Steam_Fusion_TopVent | (-0.655, 0.673, -2.106) | (0, 1, 0) | 0.42 | 0.049 / 0.16 | 1 | 8 |
| FX_Steam_Fusion_RearVent | (-0.544, 0.508, -2.247) | (0, 0.80, -0.60) | 0.32 | 0.015 / 0.12 | 16.6 | 4 |
| FX_Steam_RoofSlope_L / _R | (-1.055, 0.459, -1.574) / (1.068, 0.454, -1.585) | (0, 0.97, -0.26) | 0.55 | 0.079 / 0.22 | 1 | 11 |
| FX_Steam_Side_PodFront_L / _R | (-/+1.246, 0.231, 1.700) | (-/+0.43, 0.88, -0.21) | 0.42 | 0.035 / 0.13 | 5.1 | 5 |
| FX_Steam_Side_PodRear_L / _R | (-/+1.246, 0.231, -2.080) | (-/+0.43, 0.88, -0.21) | 0.42 | 0.035 / 0.13 | 4.6 | 5 |
| FX_Steam_Side_RearBlock_L / _R | (-/+1.268, -0.110, -2.100) | (-/+0.44, 0.87, -0.22) | 0.42 | 0.055 / 0.13 | 3.6 | 5 |

- The steam rises along the locator's local +Y (`transform.up`). Use the same particle child as for the flames, local rotation (-90, 0, 0).
- At the vent the plume is wider along the locator's local Z (`transform.forward`) by the base stretch: slot vents give a flat, wide sheet that rounds out as it rises. Set the particle Shape to a box or edge that long.
- Locator custom properties: `steam_length`, `steam_r0` (start radius), `steam_r1` (end radius), `steam_stretch_x` (base stretch), `steam_density` (relative thickness: the roof vents are the heaviest, the side slots the lightest).
- `Car_Root` has a `steam` property (default 1). In Blender every plume's density is `steam x density`, so 0 turns all steam off. Drive the emission rate the same way.

## Custom properties in Unity

Unity keeps FBX custom properties only if an `AssetPostprocessor` reads them on import. Minimal example (put it in an `Editor` folder):

```csharp
using UnityEditor;
using UnityEngine;

public class FlyingCarFbxProps : AssetPostprocessor
{
    void OnPostprocessGameObjectWithUserProperties(GameObject go, string[] names, object[] values)
    {
        if (!assetPath.EndsWith("flying_car_game.fbx")) return;
        for (int i = 0; i < names.Length; i++)
            Debug.Log($"{go.name}: {names[i]} = {values[i]}");
        // Copy the values you need into a component here, for example throttle and steam on
        // Car_Root, flame_length / flame_radius on FX_Flame_*, steam_* on FX_Steam_*.
    }
}
```

## Interior contract

The cabin follows `Tools/Blender/interior_contract.md` (FC_INTERIOR_V1). In Unity:

- Sockets are the empties `Socket_Seat_L`, `Socket_Seat_R`, `Socket_Dash` and `Socket_Yoke` (on the dashboard). Each kit piece sits on its socket with an identity local transform, checked after import (0 mm, 0 degrees).
- To swap a piece, parent the new prefab to the socket and set its local position and rotation to zero.
- Socket properties `slot`, `side`, `contract` and piece property `kit_slot` come through as FBX user properties (see above).
- The seats are upright (back 15 degrees from vertical) since v11, see the contract for the clearances.

## Lights

Blender light energy (watts) does not convert one to one to Unity intensity. Positions and directions import correctly; set intensities by eye. For the flame glow lights, scale with throttle using the formulas in the flipbook README (corner `3 + 90 x t^1.5` W, rear `10 + 250 x t^1.5` W in Blender). `Light_Glow_Fusion` is the fusion core glow.

## Polycount

Triangles in the export, all objects:

| Group | Triangles |
|---|---|
| Canopy seal (game version, see Re-exporting) | 15,812 |
| Fusion generator (housing, core, 27 detail parts) | 12,550 |
| Fans (4 x 2,372) | 9,488 |
| Body (baked) | 7,210 |
| Canopy | 4,080 |
| Greebles kept as geometry (pipes, antenna cluster, light bar) | 4,080 |
| Interior kit (2 seats, dash, yoke) | 3,600 |
| Headlights (2 x 1,740) | 3,480 |
| Rear thrusters (2 x 1,440) | 2,880 |
| Corner nozzles (4 x 528) | 2,112 |
| Fins (2 x 376) | 752 |
| Cabin inner skin | 420 |
| Light strips | 36 |
| **Total** | **66,500** |

The generator is already game-trimmed: its small parts use one-segment chamfers only on edges sharper than 65 degrees, the bolts are plain hexagons with no bevel, and the added or rebuilt cylinders have 6 to 16 sides. Its heaviest pieces are the housing with its grooves (1,926), the fusion core (1,920) and the coolant pipes (1,104).

High-poly source in the .blend (evaluated, triangles): Propulsion 136,016, Body 70,374, Lights 38,132, Greebles 29,756, Interior 18,272, Glass 111,804 (of which the full-resolution seal is 107,724). Total 404,354. The export is 16.4 % of that.

## Bake and quality

- Body UVs: Smart UV Project, 63.6 % of the 2048 sheet used, 251 px per metre (about 4 mm per pixel) over 42.2 m2 of surface.
- Baked from the high-poly body in Cycles, selected to active with a cage: base colour, tangent normal (OpenGL), AO 0.5 m, roughness, metallic, emission. The bake sources include every vent, plate, rivet strip, decal and light inlay on the body, among them the two roof-slope vents added in v10.
- Base colour is baked with the metallic input of the source materials set to 0 for the bake, so metal parts keep their colour. (The v10 bake read the diffuse pass, which is black on metal, so vents and plates came out black.)
- Low vs high comparison renders (same fixed camera and lighting, flames and steam off): front three-quarter PSNR 27.8 dB, mean absolute difference 0.018, 3.7 % of pixels differ by more than 0.1; rear three-quarter 27.1 dB, 0.016, 3.0 %. Most of the remaining difference is seen through the clear glass and in the bevel highlights. See `Tools/Blender/renders/v14_export/cmp_HP_*.jpg` and `cmp_LP_*.jpg`.

## Export checks done

- FBX header: UnitScaleFactor 100 (metres), UpAxis +Y, FrontAxis +Z, CoordAxis +X (right handed), the same as a standard Blender to Unity export.
- `Car_Root` has identity translation, rotation and scale.
- Round trip: the FBX was re-imported and all 95 objects (58 meshes, 22 empties, 15 lights) were compared to the scene: 0.001 mm worst position error, 0 degree axis error. Every mesh has the same triangle count and the same bounds (within 0.001 mm) as the object it was exported from.
- Every object sits under `Car_Root`, including the steam locators on baked vents.
- 23 materials, every mesh face has one.
- No high-poly, cutter (including the groove cutters), helper or volume (steam, flame) objects in the file.
- Not yet checked inside the Unity editor.

## Re-exporting

Run `Tools/Blender/export/fc_export_unity.py` in the .blend (it is also stored there as the text block `fc_export_unity.py`). It writes `export/flying_car_game.fbx` next to the .blend.

What goes into the file:

- Stand-ins: each object in the `Export` collection has an `export_as` property naming the object it replaces (`Body_LP` for the body, `Canopy_Seal_EX`, and the `*_EX` copies of the fans, thrusters, nozzles, headlights and cabin skin). A new stand-in only needs that property.
- The `DIRECT` list in the script (objects exported as they are), plus everything under `Fusion_Generator_Body` (`DIRECT_TREES`), every empty in the `FX` collection and the glow, beam and cabin lights. Meshes that only carry a volume shader (the steam) are left out.

The script works around three things in Blender's stock FBX export:

- "Apply Transform" places objects nested more than one level deep wrongly (flame and steam locators, sockets, kit pieces, generator parts). The script rebuilds their local matrices.
- Forward X gives a non-standard header ("front = -X"). The script writes the standard one, which matches the data.
- `Car_Root` is reset to the origin while exporting. Exported objects whose parent is not exported (steam locators on vents that are baked into the body) are parented to `Car_Root` for the export and put back afterwards.

After changing the body, the vents on it or the canopy, rebuild the body bake (`Body_LP`) and the seal's game copy:

- The full seal (`Canopy_Seal`, 107,724 triangles) comes from `canopy_seal.py` in the project root. The game copy comes from the same script with three settings changed: `SEAL_NAME = "Canopy_Seal_EX"`, `SAMPLE_MM = 24.0`, `ARC_SEGMENTS = 2`. Hide `Canopy_Seal` from render while it runs, so the script does not treat it as body. Then move `Canopy_Seal_EX` to the `Export` collection, parent it to `Car_Root` (keep transform), set `export_as = "Canopy_Seal"` and hide it from render. It follows the full seal within 1.6 mm for 95 % of its surface and covers 99.2 % of it. About half of the 0.8 % it skips is inside the hull; the rest is a stretch of about 20 cm at the left rear corner of the glass (Blender x -0.9 to -0.7 m), where the game copy has a short gap in the bead.
