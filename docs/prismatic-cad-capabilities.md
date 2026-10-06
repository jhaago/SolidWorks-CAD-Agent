# V2 slots, regular polygons and blind cuts

This increment extends the current V2 image-design intake branch. It uses the same approved job planner, native command registry and document ownership boundary. Image discussion remains separate from CAD execution.

## What you can request

- An obround slot, rotated to a specified angle, as a boss profile or cut profile.
- A regular triangle through a 32-sided polygon, as a boss or cut profile.
- A blind extruded cut with an explicit depth, as well as existing Through All cuts.

Examples:

> Create a new plate 100 × 60 × 10 mm. Add a Through All obround slot centred at sketch (-20,0), overall length 30 mm, width 10 mm, axis 30 degrees counterclockwise. Add a hexagonal pocket at (20,0), circumcircle diameter 16 mm, first vertex at 0 degrees, depth 3 mm from the original base sketch plane into the positive extrusion. The underside opening is intentional. Save as a new file without overwriting.

> Create a new hexagonal block using a regular six-sided profile, circumcircle diameter 16 mm, first vertex at 15 degrees. Extrude 5 mm and save as a new part.

Use the Desktop job prompt or the existing paired mobile CAD job submission. Review the proposed geometry and assumptions, then approve execution through the existing controls. A reference-image Design Brief learns these capabilities from the same shared command protocol; it still needs its own design approval before planning.

## Precise parameter meanings

| Command | Parameters | Meaning |
|---|---|---|
| AddSlot | centerXmm, centerYmm, lengthMm, widthMm, angleDegrees | Length is the total tip-to-tip length, not the distance between arc centres. Length > width > 0. Angle rotates the long axis counterclockwise from sketch X. |
| AddRegularPolygon | centerXmm, centerYmm, sides, diameterMm, angleDegrees | Integer sides 3–32. Diameter is the circumcircle diameter, not across flats. Angle positions the first vertex counterclockwise from sketch X. |
| CutExtrude | endCondition: Blind, depthMm | Required positive finite depth in mm, measured from the sketch plane into the positive-direction boss. |
| CutExtrude | endCondition: ThroughAll | Existing behaviour preserved. Do not supply depthMm. |

All profile fields are required. Angles range from -360 to 360 degrees. Exact field names/types, finite numbers, profile degeneracy and unit-conversion underflow are checked before native mutation. Slots use native lines and semicircular arcs; polygons use native lines. Resulting sketches and extruded features remain editable SOLIDWORKS features.

## Current limits

Sketch creation still uses Top, Front or Right origin planes. The agent cannot select an arbitrary face or create an offset sketch plane. A blind cut from the base plane opens on the underside of a positive normal extrusion. A requested top-face pocket must not silently become an underside pocket; clarify the design or report the unsupported placement.

These profiles are coordinate-defined, not dimension-constrained sketches. They do not add fillets, chamfers, feature patterns, revolutions, lofts or freeform surfaces. Arbitrary scan/mesh reconstruction remains unsupported.

Simulation validates profile requests but refuses to claim geometric verification of slot/polygon solids or blind pockets. Use the native SOLIDWORKS runtime for these models. Existing simulated plate behaviour is preserved.

## Next increments

1. Stable references to owned faces/edges and offset planes, enabling intentional face-based pockets.
2. Named sketch entities, dimensions and relations for fully constrained editable sketches.
3. Fillets/chamfers and safe feature selection.
4. Revolves and patterns, followed by bounded loft/sweep support.

Validation evidence is recorded in [the Windows acceptance note](windows-validation-prismatic-2026-10-06.md). Normal CI tests require no SOLIDWORKS installation; native tests remain opt-in.
