# Windows / SOLIDWORKS 2020 acceptance handoff

2026-10-30 finishing update: [bounded native fillet/chamfer ports and definition inspection](batched-finishing-ports-2026-10-30.md) supersede the historical excluded-handler status below. Native execution acceptance remains pending.

This branch was built without native interop on Linux. The native adapter code under
`SOLIDWORKS_INTEROP` has **not** been compiled or run in this session. Keep that boundary
visible until the installed 2020 build and fixtures pass.

## Additional acceptance for the licensed reuse batch

The new COM-free suite has140 cases locally. Run the full Windows unit suite to cover
tool registration, prior standard-plane behavior and OLE scope restoration as well.
The licence bundle test must pass and all copied notices must appear in the output ZIP:

```powershell
.\scripts\test-bundle-third-party-notices.ps1 -SourceZip .\artifacts\SolidWorksCadAgent-Windows-Test-Bundle.zip
```

The modified `PrismaticCapabilityTests` now inspect six faces/twelve edges per box,
hidden solids, signed top normals, four5mm Y-parallel edges per box, adjacent feature
ownership, exact-count failure and unchanged dirty/configuration/sketch/selection-count
state, both before and after save/reopen. Run `CheckpointedParametricEditNativeTests`
alongside the previous filters to verify feature inventory preservation and restoration:

```powershell
dotnet test tests\SolidWorksCadAgent.IntegrationTests\SolidWorksCadAgent.IntegrationTests.csproj --no-build --configuration Debug --filter 'FullyQualifiedName~PrismaticCapabilityTests|FullyQualifiedName~CheckpointedParametricEditNativeTests' --logger 'trx;LogFileName=licensed-reuse-native.trx'
```

InspectTopology is read-only and part-only. Its IDs are not persistent mutation handles;
face-box centers are approximate. The graph tool validates symbols and dimensions but
does not create/solve constraints. No new fillet/face-sketch planner capability is enabled.
The scoped OLE filter must additionally be exercised against an actual busy SOLIDWORKS2020
COM server: only retry-later refusals are retried, cancellation stops retries and previous
filters are restored. No accepted-call timeout or automatic mutation replay is promised.

## Reproduce the portable verification

Production targets remain net48. Only this selected, COM-free test project uses net8.
It links the exact production helper files plus the relevant MSTest test files.

```powershell
dotnet build tests/SolidWorksCadAgent.PortableTests/SolidWorksCadAgent.PortableTests.csproj -m:1
dotnet tests/SolidWorksCadAgent.PortableTests/bin/Debug/net8.0/SolidWorksCadAgent.PortableTests.dll --no-progress
```

Expected current result: 53 total, 53 succeeded, zero failed/skipped. This official
MSTest executable runner avoids the VSTest `ShouldRunInProcess` crash encountered
before test discovery in this session. Do not substitute portable success for the
Windows full-suite or native checks below.

## Build the installed native integration

Use a Developer PowerShell with the .NET Framework 4.8 developer tools. Confirm this
directory contains the assemblies from **SOLIDWORKS 2020**, not a newer installation.
Use a clean 2020 session and synthetic test parts. The modified prismatic fixtures
create isolated owned documents and preserve unrelated originals through their existing
capture/restore checks; checkpointed edit fixtures retain source hashes and use child copies.

```powershell
$cadApi2020 = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist'
msbuild SolidWorksCadAgent.sln /t:Restore /p:Configuration=Debug /p:SolidWorksApiPath="$cadApi2020"
msbuild SolidWorksCadAgent.sln /t:Rebuild /p:Configuration=Debug /p:Restore=false /p:SolidWorksApiPath="$cadApi2020"
msbuild tests\SolidWorksCadAgent.UnitTests\SolidWorksCadAgent.UnitTests.csproj /t:Restore /p:Configuration=Debug /p:SolidWorksApiPath="$cadApi2020"
msbuild tests\SolidWorksCadAgent.UnitTests\SolidWorksCadAgent.UnitTests.csproj /t:Rebuild /p:Configuration=Debug /p:Restore=false /p:SolidWorksApiPath="$cadApi2020"
dotnet test tests\SolidWorksCadAgent.UnitTests\SolidWorksCadAgent.UnitTests.csproj --no-build --configuration Debug --logger 'trx;LogFileName=open-source-units.trx'
```

Require all full-suite results to pass. The baseline documented 762 cases; this branch
adds 46 material/traversal data cases, so expected total is 808 if discovery matches that
baseline. This expected count is arithmetic, **not a claimed executed Windows result**.

```powershell
$env:SOLIDWORKS_RUN_PRISMATIC_TESTS = '1'
$env:SOLIDWORKS_RUN_REFERENCE_TESTS = '1'
dotnet test tests\SolidWorksCadAgent.IntegrationTests\SolidWorksCadAgent.IntegrationTests.csproj --no-build --configuration Debug --filter 'FullyQualifiedName~PrismaticCapabilityTests|FullyQualifiedName~ParametricPlateAcceptanceTests|FullyQualifiedName~ParametricHoleAcceptanceTests|FullyQualifiedName~CheckpointedParametricEditAcceptanceTests' --logger 'trx;LogFileName=open-source-native.trx'
```

Record actual SOLIDWORKS revision, native-enabled capability, test counts, TRX files,
created part paths and hashes. Check:

1. First boss succeeds with positive `materialVerification.volumeDeltaMm3`; each
   cut succeeds only with negative volume delta, correct returned feature type and clean rebuild.
2. Hidden sole and hidden additional solids contribute to `GetBodyCount.volumeMm3`
   before/after save/reopen (500 mm³ per solid in the modified native fixture).
3. Absorbed sketches appear once in feature inspection; subfeature rebuild errors
   cannot be omitted. Parent/depth are first-encounter display metadata, not trusted identity.
4. All existing plate/hole driving dimensions and native relation attachments survive;
   the extra rebuild must not disrupt capture, selection or save/reopen checks.
5. Checkpointed width/hole-position edits preserve immutable source bytes and all
   unedited parameters. Injected post-reopen evidence failures restore verified checkpoints
   and block artifact publication, as required by the existing acceptance tests.
6. On measurement/preflight failure, the native mutation was never called. On failure
   after attempted mutation, `documentMayBeModified=true` and `rollbackAttempted=false`;
   the Host must stop without publishing the failed creation as a verified part.

## First milestone that can be exercised with the existing bounded workflow

This demonstrates a flat mounting plate, not an angled L-bracket or multi-hole pattern.
The exact explicit request uses deterministic tool selection and does **not** call an AI:

```text
V2 parametric plate with hole: 120 x 60 x 10 mm; hole diameter 8 mm at x=20 mm, y=10 mm; save as acceptance/mounting-plate.sldprt
```

Review and approve the persisted current plan using the existing Desktop/Host workflow.
Expected: one native solid; three plate dimensions (`AgentWidth`, `AgentLength`,
`AgentThickness`) and three hole dimensions (`AgentHoleDiameter`, `AgentHoleX`,
`AgentHoleY`); fully defined native plate/hole sketches without Fix-all substitution;
through-all cut; clean rebuild; saved/reopened driving-role and geometry evidence.
Volume is `120*60*10 - pi*8^2/4*10 = 71497.3451754 mm³`.
Hole X/Y are coordinates from the **Top Plane sketch origin**, not from a plate corner.

After that job reaches ReadyForReview, substitute its real job UUID below:

```text
V2 parametric edit: job <source-job-UUID>; parameter width = 150 mm; save as acceptance/mounting-plate-wide.sldprt
```

Approve the current edit revision. Expected: a verified child model with width150,
length60, thickness10 and the same Ø8 hole at (20,10); source hash unchanged; original
feature structure retained through dimension editing; new volume `89497.3451754 mm³`.
The width is changed through an existing native driving dimension, not by replaying
NewPart/sketch/extrusion creation commands.

### Remaining gap to the user's complete AI milestone

General natural-language V2 planning already exists, but current explicit dimensioned
plate/hole/edit syntax deliberately bypasses the provider. This session has not
demonstrated AI interpretation → native execution → conversational AI verification.
Feature names still follow the existing native/default naming while driving dimensions
have meaningful managed names. The next bounded increment should add naming/readback
and an AI intent adapter over these same versioned recipes, with a fake-provider contract
test and an explicitly authorized live-provider acceptance. Keep the existing deterministic
path as an independent reference oracle. Do not broaden planner privileges or claim an
L-bracket until face placement/multiple boss/constraint capabilities are actually implemented.
