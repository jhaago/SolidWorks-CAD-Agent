# V2 face sketches and finishing

Base `a1aa0ae` on `feature/v2-cad-profiles-and-pockets`; work branch `feature/v2-face-sketches-and-finishing`. User requested fixing pocket placement and continuing to finishing features, and previously authorized autonomous V2 development. Main remains unchanged.

## Purpose and boundaries

Create intentional pockets on a selected outer planar face, then native constant-radius edge fillets and 45-degree equal-leg chamfers. Use the existing command whitelist and approval system. Never modify an unrelated active document or treat discussion as execution.

## Face references

`CreateSketchOnFace {normalAxis:'X'|'Y'|'Z',side:'Min'|'Max'}` resolves the unique axis-aligned planar face on the minimum/maximum global coordinate of a single solid body. Reject absent, multiple or ambiguous targets before mutation. Re-resolve geometry at each feature command; do not persist raw COM handles or guess stale topology indices. This first selector covers prismatic exterior faces, not arbitrary curved/interior faces or multi-body models.

Face sketch coordinates use a deterministic frame projected from the model origin: X-normal uses global Y/Z as sketch X/Y; Y-normal uses global X/Z; Z-normal uses global X/Y. The bridge maps this frame to the actual native sketch transform for every supported primitive. Legacy origin-plane coordinates remain unchanged. This prevents face orientation changes from mirroring or relocating intended geometry.

`CutExtrude` gains optional `direction:'IntoBody'|'Positive'|'Negative'`. Defaults remain positive sketch-normal for origin-plane sketches and become IntoBody for face sketches. IntoBody is available only for a face-bound profile and is derived from the resolved face side and actual native sketch normal. Explicit positive/negative are relative to native sketch normal. Store the completed sketch context per owned document/execution, clear it on document bind/clear and replacement sketch, and preserve it through ExitSketch until consumed by the feature.

## Finishing

`FilletEdges {normalAxis,side,radiusMm}` applies a uniform circular fillet to all edges of the selected face's outer loop only.

`ChamferEdges {normalAxis,side,distanceMm}` applies equal-leg 45-degree chamfers to that same bounded edge set. No tangent propagation, hole-loop selection, arbitrary macros or best-effort face selection. Report selected edge count and resulting native feature. Native failure remains fail-stop; do not substitute another geometry.

## Verification

Strict provider-neutral validation precedes COM. CI requires no SOLIDWORKS. Simulation must reject geometry verification it cannot provide. Native isolated tests verify offset top/bottom pockets physically, intended coordinates and blind depths, finishing feature types, unchanged hole edges, one solid body, rebuild, save/reopen and preservation of the user's existing open part. New test files/artifacts remain separate from production approval flows.

## Future stages

Arbitrary stable face/edge references, offset planes, named sketch entities and dimensions/relations, feature patterns and revolutions remain subsequent increments. This stage establishes predictable face placement and safe perimeter finishing first.
