# Tooling

The runtime tooling the game and the in-engine harness share: the `--dump-*` probes and their wrappers, the `--screenshot`/`--shots` capture, the golden-image hash, and the glTF export. The game and `CSVM.Testing` both depend on this namespace; the one call from here into the harness is `--run-tests`' dispatch in `ProbeRunner.cs`.

One `## src/...` entry per module, body at most 8 lines, 12 for the highest-traffic modules.

Traps do not live here; the rule is in `docs/architecture.md`.

## src/Tooling/Probes.cs
The assertion cores behind the `--dump-markers` / `--dump-weapons` / `--dump-loadout` /
`--dump-flight` / `--dump-mips` / `--damage-test` / `--effects-test` reports. Each
probe does the work once and returns both halves: the report text a flag prints and writes, and the
structured verdict a `--run-tests` suite asserts on, so a dump and the suite reading it cannot
disagree. The envelope, effects, damage and debris-shading sweeps each carry their thresholds, row
order and sources at their own members. The reachability half of a flight row is
`EnvelopeMargins.cs`, the suites asserting on these verdicts are the `Testing/*Suites.cs` modules,
and the branch vocabulary is [../org/flightModel.md](../org/flightModel.md).

## src/Tooling/EnvelopeMargins.cs
The reachability half of the flight-envelope report. `Sample` reads one completed `FlightModel`
step's public state and keeps, per scenario, the distance to each term that could have bounded it
(the G clamp, the C_L ceiling, the AOA window, the stall speed, the altitude band, the dive cap);
`Take` formats that as the row's `margins:` line, and across an airframe it also records which of
`Branches` the scenarios reached. Nothing here feeds a force. Read `Probes.cs` for the report it
lands in; the branch names and which instrument drives each unreached one are in
[../org/flightModel.md](../org/flightModel.md), "Parity ledger".

## src/Tooling/GoldenShot.cs
The engine half of the golden-image tripwire: `PixelHash(Image)` (md5, lower-case hex) and
`Adapter()` (`"<gpu> / <api>"`). Called at the `--screenshot` save site, which prints
`[core] shot pixmd5=… size=… gpu=…` on every capture; `RunTests.ps1`'s `goldens` stage parses that
line and compares against `analysis/goldens/manifest.json`. The Launcher's `[perf] gpu=` line
reads `Adapter()` too.

## src/Tooling/ProbeRunner.cs
The `--dump-markers`/`--dump-weapons`/`--dump-flight`/`--dump-loadout`/`--dump-mips`/`--run-tests`/
`--effects-test`/`--damage-test`/`--destroy=` probe wrappers, constructed once in `Launcher._Ready`
after the base paths settle: the Launcher dispatches the early quits itself and hands the runner to
each session node. Each method reads a `SessionSpec` passed per call rather than storing one. It
also holds `TriggerDestroy` (`--destroy=`) and `ForceObjective` (`--debug-objective=`), the two
scripted world forces belonging to no session. `RunTestSuites` is the one call into `CSVM.Testing`
from outside it. Read `Probes.cs` for the work each wrapper calls into.

## src/Tooling/CaptureDirector.cs
The `--screenshot=`/`--shots=`/`--frames=` capture state machine plus F11/F12's placement print and
ad-hoc save, built in `Launcher._Ready` from the launch spec and `Tick()`ed from the Launcher's
`_Process`, so `--menu --screenshot` captures the launchscreen with no session node alive; it takes
its camera/orbit/rigs/clock as parameters. That capture reads synchronously, since the process
exits on its file. `SaveScreenshot`, the F12 save every screen shares, asks `PaneReadback` for the
frame and writes the PNG into `ShotDir()` (`Screenshots/`, git-ignored) on the worker it lands on,
logging "screenshot saved" there; it returns the path the file will take, not yet written. Read
`GoldenShot.cs` for what the save site prints.

## src/Tooling/GltfExporter.cs
Exports any `Node3D` subtree to glTF: mesh, live material state, no animation and no emitters.
`Export(node, path)` works on a throwaway `node.Duplicate()`, frees hidden `Node3D`s (the panel/flare `Visible` toggles
are how damage is baked) and the point-sprite `"lights"` instances, and converts every shader skin to a
`StandardMaterial3D` over geometry with its winding reversed (`docs/formats/gotchas.md`: unreversed,
this data exports inside out). Format is extension-driven (`.glb` default). `ExportSet` writes several
subtrees under one root at their world transforms, dropping any an ancestor in the list carries.
`ExportToExports`/`ExportSetToExports` name a timestamped `Exports/` GLB. The viewer's `--export-gltf=`
one-shot and F10, and NodeLab's selection and export set actions, share that writer.
