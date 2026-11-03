# Thruster flame flipbooks

Baked from the `ThrusterFlame` volume shader in `flying_car_v8_flames.blend` (scene `FlameBake`). The same shader drives all six flames in `flying_car_v9_rear_flames.blend`.

| File | Throttle | Use |
|---|---|---|
| `flame_flipbook_t010.png` | 0.1 | Idle: short, tight, no shock diamonds |
| `flame_flipbook_t050.png` | 0.5 | Cruise |
| `flame_flipbook_t100.png` | 1.0 | Boost: long flame, shock diamonds |

## Sheet layout

- Sheet size: 1024 x 2048 px, RGBA PNG.
- Grid: 8 columns x 8 rows = 64 frames, each frame 128 x 256 px.
- Order: row by row, starting at the top left. Frame 1 is top left, frame 8 is top right, frame 9 starts the second row, frame 64 is bottom right. This matches Unity's Texture Sheet Animation order.
- Loop: frame 64 flows back into frame 1 without a jump. Play the 64 frames in a cycle.
- Orientation: side view. The nozzle exit is at the top centre of each frame, about 4 cm below the top edge, and the flame points down.
- Scale: one frame covers 0.31 m wide x 0.62 m tall. The full boost flame is about 0.5 m long.

## Colour and alpha

- Colour is the AgX view of the render, stored as straight (not premultiplied) colour.
- Alpha is derived from brightness: `alpha = clamp(max(R, G, B) x 1.25, 0, 1)`. The flame is emission only, so the raw render had no alpha.
- Import settings: sRGB on, Alpha Source "Input Texture Alpha", Alpha Is Transparency on, Wrap Mode Clamp, no mipmap bias needed.

## Unity particle system setup

- Renderer: Billboard (camera facing), or Stretched Billboard along the nozzle direction for a flame that stays aligned at shallow angles.
- Material: URP Particles/Unlit (or Built-in Particles/Standard Unlit).
  - Additive blending works best for fire: the black areas add nothing.
  - Alpha blending also works with the provided alpha.
- Texture Sheet Animation module:
  - Mode: Grid, Tiles X = 8, Tiles Y = 8.
  - Animation: Whole Sheet.
  - Time Mode: Lifetime, Frame over Time: linear curve from 0 to 64, Cycles: 1, or Time Mode FPS = 24 for a 2.7 s loop.
  - Start Frame: random between 0 and 63 per particle, so neighbouring thrusters do not flicker in sync.
- Throttle: swap between the three sheets, or blend two sheets in a custom shader using the car's throttle (0.1, 0.5, 1.0). Scale the particle size with throttle as well: length factor is 0.3 + 0.7 x throttle.
- Light: add an orange point light at each nozzle exit. In Blender its energy is `3 + 90 x throttle^1.5` W; convert to Unity intensity to taste.

## Emitter positions (car root space, metres, Blender axes)

| Thruster | Nozzle exit | Flame direction |
|---|---|---|
| FL | (1.700, 0.950, -0.671) | straight down |
| FR | (1.700, -0.950, -0.671) | straight down |
| RL | (-2.030, 0.950, -0.671) | straight down |
| RR | (-2.030, -0.950, -0.671) | straight down |

The car root sits 1.25 m above the ground, so the exits are 0.579 m above the ground.

## Rear thrusters

The two rear thrusters use the same sheets, scaled up. In Blender they are the same shader with a larger size.

| Flame | Max length | Radius | Scale vs corner flame |
|---|---|---|---|
| Corner (FL, FR, RL, RR) | 0.5 m | 0.05 m | 1 x 1 |
| Rear (Rear_L, Rear_R) | 1.4 m | 0.12 m | 2.4 wide x 2.8 long |

| Thruster | Nozzle exit (car root space) | Flame direction |
|---|---|---|
| Rear_L | (-2.565, 0.866, -0.359) | straight back (-X) |
| Rear_R | (-2.565, -0.866, -0.359) | straight back (-X) |

- Rear light energy in Blender: `10 + 250 x throttle^1.5` W (corner lights: `3 + 90 x throttle^1.5` W).
- Throttle overrides on the car root: `throttle_Rear_L`, `throttle_Rear_R` (below 0 means use the global `throttle`).
