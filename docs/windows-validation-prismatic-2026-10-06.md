# Windows V2 prismatic capability acceptance — 2026-10-06

Development branch: `feature/v2-cad-profiles-and-pockets`, based on `19f1dbd` from the V2 image-design intake line. Main is unchanged.

## Automated evidence

- Baseline ordinary tests: 308 passed.
- Expanded ordinary tests, native-interoperable build: 362 passed, zero failed.
- Complete solution compiled with `SolidWorksInteropAvailable=false`, including integration tests, with zero errors. Ordinary CI does not need SOLIDWORKS.
- The same 362 ordinary tests also passed against that build with native interop disabled.
- Native-enabled Release solution/test bundle build passed. Bundle validator passed; capability `NativeSolidWorksInterop`.
- Shared profile/cut validation and native registration were developed from observed failing tests. Independent review identified a positive cut depth that underflowed to zero metres; a regression test and validation fix were added.

NuGet audit warnings (`NU1900`) report the unavailable vulnerability feed. Build/test success does not establish a completed dependency vulnerability audit.

## SOLIDWORKS 2020 SP0.0 physical checks

Two opt-in acceptance scenarios passed native volume, one solid body, precise extents, successful rebuild, native editable feature/consumed-sketch inspection, new-file save and reopen:

1. A centred 100 × 60 × 10 mm plate. Obround Through All cut at sketch (-20,0), total length 30 mm, width 10 mm, axis 30 degrees. Hexagonal blind pocket at (20,0), circumcircle diameter 16 mm, first vertex 0 degrees, depth 3 mm from the original base plane into the boss. Expected remaining volume **56,715.771204 mm³**, within 0.05 mm³. Native slot has two lines of 20 mm and two arcs of radius 5 mm; rotated arc centres and hexagon vertices matched before and after reopen.
2. Regular hexagonal boss with circumcircle diameter 16 mm, first vertex 15 degrees and extrusion 5 mm. Expected volume **831.384388 mm³**, within 0.05 mm³; native vertices, bounds and reopen verification passed.

Retained successful artifacts:

- `C:\SolidWorks-CAD-Agent\Workspace\capability-tests\a31dd8aad7da4c20836f52851a13b10c\slot-and-hex-pocket.sldprt`
- `C:\SolidWorks-CAD-Agent\Workspace\capability-tests\64a20b3b6fed423c83fa3ed7a01f3027\hexagon-boss.sldprt`

The first native runs exposed acceptance-inspection issues, not mismatched volume: Instant3D reports cut feature type `ICE`, requiring the documented `GetTypeName()` fallback to `Cut`; a centre rectangle includes two construction diagonals, which must be excluded from solid-profile edge counts. Tests now inspect actual sketch features and non-construction edges while retaining strict native feature/volume assertions. See [SOLIDWORKS GetTypeName2 documentation](https://help.solidworks.com/2017/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IFeature~GetTypeName2.html).

No `CloseAllDocuments` call was used. Each test created an owned part in a unique workspace directory, closed only its own part, and asserted/restored the original document title, path and dirty flag without rebuilding it. `Plate-001.SLDPRT` remained open and active. Its existing file remained 82,105 bytes, last written 2026-10-06 01:17:14 UTC.

Run only these isolated tests on a SOLIDWORKS PC:

```powershell
$env:SOLIDWORKS_RUN_PRISMATIC_TESTS = '1'
dotnet test tests/SolidWorksCadAgent.IntegrationTests/SolidWorksCadAgent.IntegrationTests.csproj --filter FullyQualifiedName~PrismaticCapabilityTests -m:1 /nr:false
```

## Live planning and installed runtime

The existing credential-backed OpenAI planner (`gpt-5.6-sol`, `store=false`) returned the expected 15-command plan including `AddSlot`, `AddRegularPolygon` and `CutExtrude {endCondition:Blind,depthMm:3}`. The request explicitly specified the underside opening. Job `375ec898-f0ba-4a07-8db5-2dadd26e64f9` is **AwaitingApproval**, validated, no unresolved ambiguities, **zero executed commands**, no output file. It was deliberately left unapproved as evidence of the execution boundary.

Running bundle: `artifacts/V2-Prismatic-2026-10-06/SolidWorksCadAgent-Windows-Test-Bundle`. Host healthy, Real mode, Auto Mode false, attached to SOLIDWORKS 2020 with original `Plate-001.SLDPRT` active. Previous phone job remains ReadyForReview. Existing paired-device credentials and private Tailscale connection were preserved. No new Android binary was built in this increment.

## Limits

Blind pockets begin on an origin sketch plane. Arbitrary face/offset-plane placement is not implemented; the planner must not silently replace a requested top-face pocket with an underside opening. Profiles are editable native geometry but not dimension-constrained sketches. Simulation refuses geometry verification for the new profiles and blind pockets. Fillets, chamfers, revolves, patterns, lofts and surfaces remain future capabilities.
