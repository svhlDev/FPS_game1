# Interior contract FC_INTERIOR_V1

Every car body provides a cabin that follows this contract. Every interior kit piece (seat, dashboard, yoke, and later pieces) is authored against it, so any interior plugs into any car.

First implementation: `flying_car_v7_phase6.blend` (collection `FlyingCar/Interior`).

## Conventions

- Units: metres. Axes: +X forward, +Y left, +Z up (Blender). Unity export conversion happens at FBX export, not in the contract.
- Every contract object carries the custom property `contract = "FC_INTERIOR_V1"`.
- Names are fixed. A tool finds sockets by name and by the `slot` and `side` properties.

## What the car provides

All of these live in the collection `Interior_Contract` and are children of `Cabin_Origin`.

| Object | Type | Purpose |
|---|---|---|
| `Cabin_Origin` | Empty (arrows) | Reference frame of the cabin. Sits on the cabin floor, centred in X and Y. Parented to the car root. |
| `Cabin_Volume` | Mesh, hidden in render | Usable interior space: rounded box, corner radius 0.06 m. The car body and glass are cut by it with a live boolean named `Cut_Cabin`, placed before the grooves and bevels. |
| `Cabin_InnerSkin` | Mesh | Interior trim surface, 2 mm inside the volume. Only kept where the exterior behind it is opaque, so window areas stay see-through. Material `FC_InteriorTrim`. |
| `Socket_Seat_L`, `Socket_Seat_R` | Empty | Seat mounts on the floor. |
| `Socket_Dash` | Empty | Dashboard mount on the floor, 0.06 m behind the front wall (clear of the corner radius). |

`Cabin_Origin` custom properties: `cabin_min_local`, `cabin_max_local` (volume bounds in origin space), `floor_offset_m` (0.002, the skin offset), `sockets` (list of socket names).

Socket custom properties: `slot` (`seat`, `dash`, `yoke`, ...), `side` (`L`, `R`, `C`).

## Values on the first car

| Item | Local to `Cabin_Origin` (m) | World (m) |
|---|---|---|
| `Cabin_Origin` | 0, 0, 0 | 0.535, 0, 0.870 |
| Volume bounds | x -1.015 to 1.015, y -1.06 to 1.06, z 0 to 1.30 | x -0.48 to 1.55, z 0.87 to 2.17 |
| `Socket_Seat_L` | 0.025, 0.50, 0.002 | 0.56, 0.50, 0.872 |
| `Socket_Seat_R` | 0.025, -0.50, 0.002 | 0.56, -0.50, 0.872 |
| `Socket_Dash` | 0.953, 0, 0.002 | 1.488, 0, 0.872 |

## How a kit piece is authored

- The object origin is the piece's anchor point. Its axes match the socket: +X forward, +Z up.
- The anchor sits on the mounting surface: floor pieces have their underside at local z = 0.
- Snapping is parenting: `piece.parent = socket`, parent inverse = identity, local transform = identity.
- Custom property `kit_slot` matches the socket's `slot`.
- A piece can carry its own sockets for sub-pieces. The dashboard carries `Socket_Yoke` (slot `yoke`, side `L`, driver side), and the yoke snaps to it the same way.
- Pieces of the same type share one mesh (both seats use the mesh `Kit_Seat`).

| Piece | Slot | Anchor |
|---|---|---|
| `Kit_Seat_L`, `Kit_Seat_R` | seat | Floor, centre of the seat rails. Seat extends forward and back from it. |
| `Kit_Dash` | dash | Floor, centre of the firewall panel. Dashboard extends toward -X. |
| `Kit_Yoke` | yoke | Column base on the dashboard face. Column extends toward -X. |

## Validation (all must pass)

1. Snap error: piece world transform equals socket world transform (0 mm, 0 degrees).
2. Fit: every vertex of every piece is inside `Cabin_Volume`.
3. No overlap between any piece and the body, glass, greebles, lights or propulsion meshes.
4. Gap to glass at least 2 cm.
5. Seats: a 0.91 m sitting height (50th percentile male) at the seat's recline keeps the head below the volume top.

Results on the first car (v11, upright seating): 0 mm / 0 degrees snap error for all four pieces, 0 vertices outside the volume, no overlaps with the car or between pieces, glass gap 31 cm (seats), 6.5 cm (dash), 37 cm (yoke). Head clearance 32.6 cm with the seat back at 15 degrees from vertical. The glass roof above the cabin is 14 cm thick.

Ergonomics on the first car (checked, not part of the contract): shoulder to yoke grip 0.59 m, knee to yoke column 15 cm, eye 24 cm above the dashboard top.

History: the first version had a 0.87 m tall volume and seats reclined 40 degrees (head clearance 6.6 cm). After the canopy was made taller, the volume grew to 1.30 m, the seat sockets moved 0.30 m forward and the yoke column grew 0.15 m so an upright driver reaches it.
