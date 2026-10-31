# V2 prismatic CAD capability increment

Base: `feature/v2-image-design-intake`, commit `19f1dbd`. Work branch: `feature/v2-cad-profiles-and-pockets`. User authorized continued V2 design and development without additional permission checkpoints. Main must remain unchanged.

Extend the existing whitelisted planner/bridge architecture with useful native editable profiles and depth-controlled cuts. Preserve the image discussion boundary, explicit design approval, CAD execution approval, ownership, millimetre conversion, cancellation and workspace policy.

## Commands

- `AddSlot`: centre coordinates in mm, total end-to-end `lengthMm`, `widthMm`, and required `angleDegrees` measured counterclockwise from sketch X. Length must exceed width. Build a closed obround using two lines and two semicircular arcs.
- `AddRegularPolygon`: centre coordinates in mm, integer `sides` from 3 to 32, circumcircle `diameterMm`, required `angleDegrees` locating the first vertex. Use closed native lines; this is a regular polygon, not arbitrary imported geometry.
- `CutExtrude`: preserve `ThroughAll`; add `Blind` with required positive `depthMm`. Reject irrelevant depth on ThroughAll. Direction remains the existing cut into a positive normal extrusion.

Angles are bounded to [-360,360]. Validate finite dimensions, exact types, unexpected fields and numerically degenerate generated geometry before any COM mutation. Shared geometry generation belongs in Core without SOLIDWORKS dependencies; native handlers convert mm to metres exactly once.

## Deliberate limits

Sketch creation still supports origin planes only. Blind cuts begin on that plane, usually opening on the underside of a positive-direction boss; arbitrary face selection and offset planes are not implemented. Do not silently translate top-face pocket intent into an underside cut. Ask for clarification or report this limitation. Finishing features and advanced surfaces remain unsupported.

Simulation may validate the new profiles but must reject their solid construction and blind cuts explicitly rather than claim geometric verification it cannot perform. Native acceptance provides the geometric evidence.

## Verification

Ordinary CI needs no SOLIDWORKS. Unit tests cover contract/bridge validation, closure, rotated geometry and simulator honesty. Opt-in native tests build new isolated parts only, verify volume, bounds, one solid body, rebuild, editable features and save/reopen. Never close all documents or modify/save the user's original open part. Preserve and restore its active document and dirty state.
