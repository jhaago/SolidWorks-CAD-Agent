# PC V2 first increment: 6 October 2026

Branch: feature/v2-expanded-cad
Base: 5ddf48ff62956803d8d265e9b0c52a840a11029e

## Delivered
- Desktop Request Changes submits clarification/change instructions to the persisted revision endpoint, then displays the revised plan for review.
- Desktop history loads persisted jobs, supports paginated older records and loads full snapshots on selection. A five-second refresh retries failed initial history reads and refreshes recovered running jobs.
- A running job blocks replacement submissions and history switching while keeping independent cancellation available.
- Native AddLine and AddArc commands use strict shared geometry validation, explicit millimetres and arc direction, the owned document and STA dispatcher. Coordinates bypass sketch snapping and preserve the previous AddToDB setting.
- Planner protocol exposes both commands. Simulation accepts sketch primitives but explicitly fails custom-profile solid creation rather than inventing geometry verification.

## Verified
- Unit suite: 235 passed, zero failed/skipped. TRX: TestResults/V2/v2-unit.trx.
- Live SOLIDWORKS 2020 integration: four passed, zero failed/skipped. TRX: TestResults/V2/v2-native-sketch.trx.
- New acceptance: closed semicircle made from one line and one arc, extruded 5 mm; one native solid, sorted extents 5/10/20 mm, clean rebuild and volume 785.3981633974483 mm3; saved and reopened with the same checks.
- Regression: original 100/60/10 plate with centered through-hole and save/reopen, 10/10/1 unit scale, and switched-active-document protection all passed.
- New tests were observed failing before their production implementations. Review findings received reproducing tests before fixes.

## Review record
Independent reviewer found three Important desktop issues: stable healthy connections did not retry missing history, recovered running jobs did not refresh terminal states, and new submission could redirect Cancel. All fixed; corresponding tests passed with the full 235-test suite.

Deferred minor: the semicircle fixture verifies volume/bounds but does not distinguish clockwise from counterclockwise orientation. A directional quarter-arc fixture is reserved for the next native sketch acceptance expansion. Arc direction mapping follows the official CreateArc documentation (+1 counterclockwise, -1 clockwise): https://help.solidworks.com/2013/English/Api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISketchManager~CreateArc.html

## Remaining boundaries
Manual visual acceptance of the desktop clarification dialog/history navigation remains pending. No new live OpenAI request was made. Native tests exercised the Bridge directly; planner/client contracts were verified locally. NuGet audit metadata was unavailable (NU1900 warnings); cached dependencies built successfully.

This is the first V2 increment. Stable entity references, dimensions/relations, fillets/chamfers, patterns/revolve, existing-model edits and advanced features remain future milestones in the V2 design; they are not claimed implemented.