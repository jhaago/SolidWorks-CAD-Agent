# Exact face boundaries and SOLIDWORKS 2020 compilation

2026-10-30 finishing update: [bounded native fillet/chamfer ports and definition inspection](batched-finishing-ports-2026-10-30.md) supersede the historical excluded-handler status below. Native execution acceptance remains pending.

This increment implements a selection prerequisite shared by face sketches and finishing. It adds a read-only tool and reusable native resolver. The excluded sketch-on-face, fillet and chamfer mutation handlers remain unavailable.

```json
{"command":"InspectFaceBoundary","parameters":{"normalAxis":"Y","side":"Max"}}
```

The bridge requires its existing bound part document and one solid body, including hidden solids. It reads exact body extrema and planar-face points/outward normals. Exactly one face must coincide with the requested extreme. Pocket floors, tilted faces and split coplanar faces cannot be substituted by a largest-area/first-match guess; failure is `FACE_BOUNDARY_QUERY_FAILED`.

Every loop is inspected; only the unique outer loop's edges are returned. Hole edges remain excluded even if the first loop is a hole. Singular, incomplete, duplicate and ambiguous evidence fails closed. Budgets: 20000 faces, 4096 loops, 20000 loop-edge visits, 256 outer edges. Identification tolerances: 0.00001 mm coordinate difference and 0.00000001 normal cross-product magnitude; these are not engineering acceptance tolerances.

Output includes face/edge hashes, outer-edge and inner-loop counts, point/body-axis bounds and `inspectionOnly:true`, `selectionAuthority:false`, `nativeCreated:false`. Hashes are evidence, not public mutation handles. The closest point establishes a plane coordinate, not a sketch origin or face centre. No selection, rebuild or geometry mutation occurs. The handler rejects observed changes in document binding, dirty flag, configuration, active sketch identity or selected-object count. The tool remains outside the modelling planner allowlist.

## Version-2020 API compilation

The public [Visiativ.SOLIDWORKS.Interop 28.1.0 package](https://www.nuget.org/packages/Visiativ.SOLIDWORKS.Interop/28.1.0) is labelled SOLIDWORKS 2020 SP1. Its two extracted reference DLLs both have metadata version **28.1.0.74**. Reading metadata and compiling against them does not execute SOLIDWORKS.

| Evidence | Value |
|---|---|
| Package SHA256 | `828d3639aec02ca8219db1ac24e5857d0c7a485282fd0a682e4193f76ca547d6` |
| sldworks DLL SHA256 | `8709b4e6a8e637f2b3c8160c9e955362022a811f1fae995aa137e799bd066fc6` |
| swconst DLL SHA256 | `2e32c43682f556c5093afe371db130343f76c95d9904d099eb485beee8894ce2` |
| sldworks metadata public-key token | `7c4797c3e4eeac03` |
| swconst metadata public-key token | `19f43e188e4269d8` |

Only those two explicitly named net48 DLLs were extracted. The package's install script was not executed. Its metadata supplies no explicit licence grant: build-only reference use here does not authorize redistribution. No binary, package dependency or installation hook is added to the repository or shipped bundle. Workstation builds continue using the installed 2020 SDK/redist; no newer API is substituted.

`build/SolidWorks2020Interop.targets` rejects explicit missing references, majors other than 28 and differing reference versions. Bridge and native integration-test projects import it. Empty API paths retain native-disabled compilation.

CI runs two checks after constructing/uploading the existing native-disabled bundle:

1. `test-solidworks-api-version-guard.ps1` tests synthetic metadata: matching 28, newer 32, older 27, mixed 28 service packs and missing DLLs. This proves the guard only.
2. `test-solidworks-2020-reference-compile.ps1` downloads the hash-pinned package, extracts only the two references, verifies 28.1.0.74 and compiles the full solution with native interop enabled. Output stays in temporary storage outside distributed artifacts; no native runtime test executes.

The adapter uses APIs predating 2020. [GetClosestPointOn](https://help.solidworks.com/2017/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IFace2~GetClosestPointOn.html) returns five XYZUV doubles and dates to 2001Plus. [GetLoops](https://help.solidworks.com/2013/english/api/sldworksapi/solidworks.interop.sldworks~solidworks.interop.sldworks.iface2~getloops.html) dates to 2009; loop IsOuter/GetEdges date to 2001Plus. Existing extrema and persistent-reference APIs are retained.

## Evidence and remaining gates

- Pure tests cover six sides, pocket floors, outward/tilted normals, tolerances, split faces, malformed XYZUV, duplicate IDs, hole/outer order and budgets.
- **213 portable tests passed**, zero failures/skips. This includes explicit nonfinite-value tests replacing two JSON literals whose parsing differed between net48 and net8.
- Full solution compiled with native interop disabled. Separately, full native-enabled solution and acceptance-test sources compiled against actual version-28.1.0.74 references, zero warnings/errors. Compiler logs confirm `SOLIDWORKS_INTEROP` in Bridge and integration tests.
- Locally, the guard accepts actual version-28 references and rejects a missing directory. CI runs the full synthetic matrix, Windows unit/facade tests, bundle/licence checks and native-enabled compile. Final evidence is recorded on PR #7.
- Opt-in `PrismaticCapabilityTests` inspect hidden solids, reject multiple solids and inspect both sides of a through-hole plate before/after save/reopen, checking four outer edges, excluded hole loops, coordinates and unchanged document state.
- Actual SOLIDWORKS execution, geometry, solver and recovery acceptance remain pending. Compilation is not native geometric verification.

The next mutation increment must add native definition/health checks and fork/checkpoint behavior before admitting face sketches or finishing into approved AI plans. Existing feature handlers and interfaces are preserved. The selector is independently implemented from documented APIs; no additional third-party source was imported.

Independent review: fixed expected-negative-probe exit-code leakage in the Windows guard script; also added independent native face-count completeness readback. Two new pure regressions reject partial/count-mismatched arrays before uniqueness is inferred. Windows script execution is verified by CI, not claimed from this Linux session.
