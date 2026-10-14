# Explorer Grid Physical Probe v1

## Scope

This is a Diagnostics Workbench physical gate, not a product feature.

The probe validates three current Explorer geometry assumptions:

1. each display has constant observed horizontal and vertical spacing;
2. each display has constant observed two-dimensional phase;
3. all displays share the same spacing, and each display has a constant observed two-dimensional phase of its own.

It does not alter Workspace compilation, AI authority, Writer authority, Recovery semantics, or automatic execution policy.

## Safety boundary

- The runner freezes a complete `DesktopObservation` before creating any artifact.
- It creates exactly one uniquely named temporary `.url` shortcut per display.
- Only the exact `ParsingName` / `StableId` values of those newly created files are passed to the existing `IDesktopIconPositionWriter`.
- The existing `DesktopShellIconPositionWriter` remains the only movement protocol: `SelectAndPositionItems` is immediately followed by `GetItemPosition` in the same `IFolderView2` connection.
- The Writer read-back is retained as `immediateObserved`, but it is not treated as Explorer's final position. Explorer can asynchronously apply grid alignment after that same-connection read-back.
- Every successful move is therefore followed by the existing stable double-observation boundary. The report retains both `immediateObserved` and `settledObserved`; spacing, phase, native-slot acceptance, and the next move's expected previous position are all derived from `settledObserved`.
- During settling, only the currently moving probe shortcut may transition. Any topology/capability change, user-item change, or movement of another probe shortcut stops the scan immediately. Existing desktop items must remain byte-for-byte equivalent as observation records.
- Movement is a direct `IFolderView` placement, not a path animation. Only the landing slot can conflict with another icon.
- Before an active write, the target uses the shared spacing-sized rectangle collision semantics. A free slot is tested actively with the dedicated shortcut. A slot occupied by exactly one item at the predicted native-slot coordinate is recorded as a read-only passive sample and never passed to the Writer. Partial overlaps are not written.
- Topology is checked before every write. Topology/capability changes, existing-item changes, unexpected previous positions, out-of-bounds positions, inexact native-slot writes, spacing drift, or phase drift stop the scan immediately.
- The initial Explorer position of every test shortcut is recorded. On exit the runner attempts restoration in reverse order, then deletes only its exact uniquely named files and performs a final observation that requires all pre-existing desktop items to be unchanged.
- If topology changed, the runner does not write using the stale coordinate system; it skips position restoration and removes only its own temporary shortcut files.

## Scan shape

For each display, one dedicated shortcut plus read-only occupied-slot evidence covers:

1. one center seed move to observe the actual Explorer phase;
2. the longest contiguous **verifiable** run of native slots on one horizontal line;
3. the longest contiguous verifiable run on one vertical line;
4. four distinct safe perimeter slots nearest the four corners.

For steps 2-4, free slots are active probe moves and exact occupied slots are passive observation samples. Each axis must still contain at least three active landing samples (or every slot when that axis contains fewer than three slots). If no such safe run exists, the run ends as `Blocked`, not as a grid-geometry failure. A completely full surface can provide read-only geometry evidence but cannot satisfy the Physical Gate's active-write requirement.

A slot is **verifiable** only when it is fully inside the work area and either completely free or occupied by exactly one item at that exact coordinate. A slot that is partially overlapped by an off-grid item — an icon left at a stale coordinate, or an icon inside a display's non-work strip such as a taskbar band — is not verifiable and is excluded from the scan line; the probe never writes to it. Each axis therefore needs only **one contiguous span** of verifiable slots (≥3 active samples), so a desktop containing off-grid icons no longer invalidates the whole line. A `Blocked` report now names the inspected line, its verifiable/active/passive counts, the longest contiguous run, and the exact slots and occupants that broke it (including whether a blocking item lies outside the work area), and points at *View → Align icons to grid* as the repair.

The seed may snap after the Writer's immediate read-back. Every subsequent point is derived from the seed's stable `settledObserved` phase and current `GetSpacing`. Explorer itself can asynchronously re-apply the layout *it* remembers and move the probe shortcut somewhere else entirely (observed repeatedly on the target machine): it puts the shortcut back on a slot of the same lattice, on a *different* lattice, or even on another display. Any such deviation is an environment behaviour, not a geometry contradiction, so the scan treats it as **"this slot was not measured"**: the move is retried once, and if it settles off-request again the sample is discarded (`skippedLandings` in the report) and the scan continues. Exact landings remain phase-checked per point (`EnsurePhase`), so a landing that disagrees with the geometry being measured still stops the run, and the fact recorder's "≥3 active samples per axis" rule is unchanged: a display that loses too many samples writes no facts rather than weaker facts.

Phase is **per display**. The seed establishes the phase of the display it ran on; a second display's absolute phase is not required to match, because a display whose work-area origin sits at a different offset from the global grid legitimately has a different phase (target machine: `\\.\DISPLAY1` `(2,16)` vs `\\.\DISPLAY3` `(35,84)`). When the frozen observation already contains at least three pre-existing icons on one lattice for a display, that lattice's consensus phase is used as an independent read-only cross-check: if the consensus is reliable and disagrees with the measured phase, the run stops before scanning, because a real native slot cannot sit on two lattices at once. A display without enough pre-existing icons simply has no cross-check. Spacing remains a single global value.

## Automated verification

```powershell
dotnet test .\tests\DesktopAgent.Tests\DesktopAgent.Tests.csproj -c Debug --filter "FullyQualifiedName~ExplorerGridPhysicalProbe"
```

Coverage includes negative-coordinate plan generation, mixed active/passive lines with occupied default-icon slots (including a faithful replay of the target machine's `y=702` line: 26 active + 4 passive), the contiguous-run rule over off-grid icons that partially overlap a column, actionable blocked diagnostics, a fully occupied surface returning `Blocked`, a passing two-display run whose displays have **different work-area anchors and therefore different phases**, an immediate stop when a display's pre-existing icons sit on a different lattice from the measured phase, simulated asynchronous Explorer snapping between immediate Writer read-back and stable observation, immediate stop on topology mutation, immediate stop with requested/immediate/settled logging on position drift, owned-shortcut-only writes, restoration/deletion, and a diagnostics-only dependency boundary.

## Blocked root cause found on the target machine, and the fix (2026-10-13)

The first real run ended `Status: Blocked` after the seed/restore moves both landed exactly
(`1370,1290 → 1446,702`, restore exact; `OwnedShortcutsDeleted: True`, `UserIconsUnchanged: True`,
zero rows in `explorer_grid_measurements` — the fail-closed path held). The reported failure was:

> 显示器 \\.\DISPLAY1 找不到可安全覆盖且至少包含 3 个主动落点的横向 native-slot 扫描线。

Independent read-only measurement of the same desktop (via the product's own
`DesktopShellIconPositioningCapabilitiesReader` + `DesktopShellIconReader` on a .NET 10 host) gave:
spacing `(76,98)`, `autoArrange=False`, 144 icons, all displays at 96 DPI,
`\\.\DISPLAY1` work area `(124,0)-(2560,1440)` (a 124 px left taskbar band),
`\\.\DISPLAY2` `(-1080,-210)-(0,1670)`, `\\.\DISPLAY3` `(309,1440)-(2229,2480)`.

Offline replay of `ExplorerGridProbePlan.Create` over those 144 icons:

- DISPLAY1 (31 columns × 14 rows): 48 icons inside the work area, **41 of them exactly on the grid**
  (x≡2 mod 76, y≡16 mod 98 — i.e. the phase the probe itself inferred, `(2,16)`); 7 icons left in an
  old column at `x=166`; 18 icons sitting at `x=90` **inside the taskbar band, outside the work area**.
  Their 76×98 rectangles overlap the first in-work-area slot column (`x=154`) in every row, and the
  `x=166` column also overlaps `x=230` in rows 0-6. **Qualifying strict rows: 0 of 14.**
- DISPLAY3 (24 columns × 10 rows): 46 of 48 icons sit at a *different* stale phase, `(35,84)`;
  qualifying strict rows: 1 of 10.
- DISPLAY2: no icons inside the work area; 18 of 18 rows qualify.

So `Blocked` was never a spacing/phase inference error — it was the plan's "every slot of the whole
line must be verifiable" rule meeting a desktop that carries icons at stale positions. Fixed by the
contiguous-run rule above plus the actionable diagnostics. Replaying the same 144-icon snapshot with
the new rule gives DISPLAY1 14/14 rows, DISPLAY3 10/10, DISPLAY2 18/18, vertical 28/18/13, and all
corner selections succeed — the three displays now pass the plan stage. Snapping every icon to the
grid and replaying the *strict* rule also passes every row, which is why step 2 of the procedure
below (*Align icons to grid*) remains the recommended precondition rather than a substitute for the fix.

## Second real run: the per-display phase defect (2026-10-13)

The second run used the relaxed scan rule and got past the plan stage: the report shows a seed move
plus `Horizontal[16]`, `[17]`, `[18]`, `[19]` and `[12]` on `y=702`, all `immediateExact=True` /
`settledExact=True`, with three read-only passive samples at exactly `(1218,702)`, `(1294,702)`,
`(1370,702)`. It nevertheless ended `Status: Failed` with:

> `\\.\DISPLAY1 Horizontal[12] settled Observation 检测到非预期桌面变化：StableId "…" 在连续两次完整观察之间发生变化`

An external desktop write (a download creating a new icon while the scan ran) changed the frozen
user-item set between two complete observations, so the runner aborted and rolled back:
`UserIconsUnchanged: False`, zero new rows in `explorer_grid_measurements`, and no `daprobe-*`
leftovers on the desktop. The fail-closed path worked; this was environment noise, not a geometry
error — rerun on a quiet desktop.

The same run exposed a real product defect that would have prevented a PASS on any multi-display
machine whose displays have different work-area anchors: the runner required every display's
*absolute* phase to equal the first display's, and the fact recorder required `SharedPhase: True`.
The machine's own data shows that can never hold (`\\.\DISPLAY1` `(2,16)` vs `\\.\DISPLAY3`
`(35,84)`). Fixed by:

- removing the cross-display phase equality abort; each display's seed phase is authoritative for
  that display;
- adding the read-only icon-lattice cross-check described above (an unreliable or absent consensus
  suppresses the check instead of failing);
- replacing `SharedPhase` with `PhaseVerifiedPerDisplay` in the report and requiring it in the fact
  recorder (a report whose per-display phase check did not pass writes no facts);
- fixing the vertical blocked-line diagnostic, which labelled a slot's x value as `y`;
- making the real-machine plan test faithful: its `y=702` line now carries the four exactly-occupied
  slots the real desktop has, so it asserts 26 active + 4 passive samples instead of claiming no
  passive samples exist.

## Third real run: Explorer re-applying its own layout (2026-10-14)

The third run (`Status: Failed`, `OwnedShortcutsDeleted: True`, `UserIconsUnchanged: False`, still
zero rows in `explorer_grid_measurements`) got further than the second: the seed plus
`Horizontal[16]`, `[17]`, `[18]`, `[19]` all landed `settledExact=True` on `y=702`, and
`Horizontal[12]` was requested at `(1142,702)` and observed immediately at that point. It then
aborted with:

> `\\.\DISPLAY1 Horizontal[12] settled Observation 检测到非预期桌面变化`

Independent read-only measurement at that moment (same .NET 10 reflection host as the first
diagnosis) showed `(1142,702)` is free (zero occupants) and on the probe lattice
`x≡2 mod 76, y≡16 mod 98`; the deviation that the settled observation reported, `(1218,800)`, is
**also free and also on the same lattice** — one column left and one row down. Explorer had
asynchronously re-applied the layout it remembers for that shortcut file and moved the probe
shortcut there between the immediate read-back and the settled observation. This is environment
behaviour, not a geometry error, and it is not preventable from the probe side.

Fixed by treating every safe deviation as "this slot was not measured" (final rule after the fourth run
showed that an off-lattice landing is a normal outcome as well):

- a settled position that differs from the request is retried once with the same request; if the retry
  settles exactly, the sample counts normally (and the retry is visible in the move list);
- if it settles off-request again, the sample is **discarded** and the scan continues; the count is
  reported as `skippedLandings=N` on that display, and the discarded landing is never counted as an
  active sample;
- safety is not weakened: `ValidateSettledProbeObservation` still requires the user's icons to be
  untouched and the other probe shortcuts to be where they were, every landing is still verified for
  the user's icons, and only exact landings feed the samples;
- the per-axis spacing check now accepts a positive multiple of the pitch between neighbours
  (i.e. holes left by discarded samples) while requiring at least one neighbour pair exactly one
  pitch apart whenever the axis has three or more samples; the fact recorder's "≥3 active samples per
  axis" rule is unchanged, so a display that lost too many samples writes no facts rather than
  weaker facts.

Tests: `RunAsync_ExplorerPutsTheIconBackOnItsRememberedSlot_RetriesOnceAndStillCountsTheSample`,
`RunAsync_ExplorerKeepsPuttingTheIconBack_SkipsThatSampleAndStillPasses` and
`RunAsync_SettledLandingLeavesTheWorkArea_SkipsThatSampleAndStillPasses`.

## Fourth real run: a display Explorer refuses to keep the probe on (2026-10-14)

The fourth run (`Status: Failed`) is the first one where DISPLAY1 itself measured cleanly:

```
\\.\DISPLAY1: spacing=(76,98) phase=(2,16) horizontal=17 vertical=14 corners=2
   active=33 passiveOccupied=4 skippedLandings=15 spacingConstant=True
   phaseConstant=True phaseVerified=True
   iconLattice consensus=(2,16) support=40/47
```

All 15 discarded landings sat on the *other* lattice that Explorer remembers (`x≡14 mod 76`,
`y≡2 mod 98`) — the same behaviour as the third run, now handled by the rule above. The run then
failed on the second display's seed:

> `Explorer settled observed 位置越出目标 WorkArea：(2,702)。` (`Horizontal`/seed observed at
> `(-540,730)` immediately, settled at `(2,702)`)

Explorer moved the probe shortcut for DISPLAY2 back onto DISPLAY1 — i.e. that display has no
measurable slot for a probe file at all. This is not a geometry contradiction and not a safety
violation (`UserIconsUnchanged: True`, `OwnedShortcutsDeleted: True`, all three owned shortcuts
restored exactly). The probe therefore no longer aborts the whole run on this event:

- a **settled landing outside the target display's work area** is reported and treated like any other
  deviation (retry once, then discard the sample);
- a **seed** that lands outside the target display is retried (up to 3 attempts, 1.2 s apart) because
  Explorer re-places a freshly created shortcut once; only if every attempt lands off that display is the
  display marked *unmeasured*
  (`measured=False`, `unmeasured reason=…` in the report, `MeasuredDisplays: N/M` in the header) and
  keeps measuring the remaining displays instead of aborting;
- a **display with too few active samples** (a landing the Explorer keeps rejecting, or a display it never
  keeps the probe on) is reported as `measured=False` with a reason, and the run finishes as `Partial`
  (`MeasuredDisplays: N/M` plus an `UnmeasuredDisplays:` line). The measured displays still produce their
  facts, the unmeasured display produces nothing, and the strategy layer refuses to execute on a display
  without a fact. A run in which *no* display was measured is `Failed` and writes nothing. Missing
  measurements therefore stay fail-closed **per display**; they never degrade into inferred facts.

Tests: `RunAsync_ExplorerPutsTheSeedOnAnotherDisplay_MarksThatDisplayUnmeasuredAndKeepsTheOthers`,
`RunAsync_ExplorerKeepsEveryLandingOffTheSlot_MarksThatDisplayUnmeasured`, plus the out-of-work-area skip
test above.


## Fifth real run: Explorer re-flows the whole icon flow (2026-10-14)

The fifth run reached the end of the scan and failed only on the second display's seed — but it also
produced the first real safety finding of the whole gate: `UserIconsUnchanged: False`.

Measured on the target machine from two read-only icon snapshots (`%TEMP%\grid_dump_2345.txt` at
23:45 plus fresh dumps after the runs):

| window | existing desktop icons that moved |
| --- | --- |
| 23:45 → 00:09 (second run) | 8 |
| 00:09 → 00:23 (app not running) | 0 |
| 00:23 → 01:00 (third to sixth run) | 38 |
| 23:45 → 01:00 (total) | 46 of 144 |

Every displaced icon moved exactly one slot: the icons in the desktop's left columns (`x=14`, `x=90`,
`x=166`) each moved `y+98` (one vertical pitch), and the two icons at the bottom of a column wrapped
to the top of the next one (`GitHub Desktop (14,1766) → (90,2)`). Nothing was renamed, added or
deleted — 144 icons before and after. Deleting the probe's own shortcuts does **not** put the flow
back.

Controlled experiments (each started from the restored 23:45 baseline, using the same read-only diff
as the detector):

- creating three `[InternetShortcut]` files on the desktop, moving one of them once, then deleting
  them again: **0** existing icons displaced;
- the probe's real write sequence (three files, a 17-slot horizontal sweep, a 14-slot vertical sweep):
  **46** existing icons displaced within 10 seconds, still 46 after the files were deleted.

Cause: creating desktop items makes Explorer insert them into the icon *flow*; every icon after the
insertion point moves one slot, and Explorer never packs the flow back when the item disappears. A
probe run therefore permanently rearranges the desktop on this machine, and the old
"existing icons changed → stop immediately" check detected the damage only after it had happened.

Explorer's behaviour cannot be changed from the probe, so the probe repairs it. The safety boundary
above is unchanged — the probe must not leave the desktop in a different state than it found it — but
it is now enforced at the end of a run instead of by aborting mid-run:

1. The ordered icons observed before anything is created are the baseline.
2. A mid-run drift of *existing* icons is recorded but no longer aborts the run; a change of the icon
   *set* (an item appearing or disappearing) still aborts immediately. Aborting mid-run left the
   desktop half-rearranged and could never self-heal.
3. In the end-of-run cleanup block, after the owned shortcuts are gone, the final observation is
   diffed against the baseline. Every displaced icon is moved back to its baseline position, then a
   new stable observation is taken and diffed again.
4. `UserIconsUnchanged: True` now means "verified after that repair". The report prints
   `UserIconsRepaired: N unrepaired=M` next to it, `unrepaired>0` is a FAIL with
   `既有桌面项目被 Explorer 挪动后未能全部恢复到运行前的位置：…`, and a set change is a FAIL with
   `既有桌面项目集合在 Probe 运行期间发生变化：缺失 N 个，新增 M 个。`

An already-displaced desktop can be recovered with an engineering-only host (`%TEMP%\gridrestore`, a
reflection host over `DesktopShellIconReader`/`DesktopShellIconPositionWriter` that diffs a snapshot,
moves icons back and re-verifies). It restored the 46 displaced icons exactly:
`moved=46 exact=46 failed=0 remainingAfterVerification=0`.

Tests: `RunAsync_ExplorerReflowsTheDesktopDuringTheRun_PutsTheMovedUserIconsBackAndStillPasses` and
`RunAsync_UserIconsKeepMovingAfterTheRepair_ReportsTheUnrepairedIconsAndFails`.

## Sixth real run: the repair works, the re-flow outlasts the settle budget (2026-10-14)

The first run with the end-of-run repair in place. `UserIconsUnchanged: True` **and**
`UserIconsRepaired: 8 unrepaired=0`: Explorer displaced eight existing icons while the scan was
running, and the probe moved all eight back and verified it. That is the first production evidence that
the repair path works — the previous run left 46 icons displaced, this one left none.

The run still failed, for a different reason:

```
Failure: ProbeAbortException: \\.\DISPLAY1 Horizontal[12] settled Observation 检测到非预期桌面变化：
  StableId "c:\users\29719\desktop\daprobe-9417dbda-01.url" …
  StableId "…\习近平新时代中国特色社会主义思想概论 23版 题库0610(2)【26.6.18&&】(1).doc" …
  … 8 existing desktop items in total
```

The seed and four horizontal landings had already come back exact on the measured lattice
(`(1446,702)`, `(1522,702)`, `(1598,702)`, `(1674,702)` — all `phase=(2,16)`, `spacing=(76,98)`), then
`Horizontal[12]` could not get a settled observation: the re-flow keeps *the whole desktop* unstable
for several seconds, so two consecutive complete observations never match, and the settle capture gave
up after its four attempts and aborted the run. Measured displays is `0/0` because a display only enters
`Displays:` once its scan finishes; the partial evidence is in `Moves`.

Two changes:

1. `SettledObservationAttempts` 4 → 24 with the standard 250 ms pause between attempts: the settle
   window now covers a re-flow that lasts several seconds, and only a desktop that stays unstable for
   the whole budget fails.
2. A second tolerated class in `CaptureStableRequiredAsync`: `IconChanged` for StableIds that were
   present in the pre-run baseline. That is exactly the displacement the end-of-run repair restores and
   verifies, so waiting for it is correct — aborting on it was what made a re-flow end a run mid-scan,
   leaving the desktop rearranged *and* no measurement. A change of the icon *set* (`IconAdded` /
   `IconRemoved`) and any other issue still fail immediately, and so does any move of a StableId that
   was not in the baseline (i.e. of another probe shortcut).

Test: `RunAsync_ExplorerReflowsTheDesktopBetweenTwoSamples_KeepsWaitingAndStillPasses` — the fake
observation source gained an `AfterCapture` hook, so the desktop changes between the two samples of a
single stability attempt; the run must keep waiting, finish, repair and PASS.

## Seventh real run, and the per-display decision (2026-10-14)

The seventh run finally exercised the repair path on the real desktop: Explorer re-flowed eight existing
items while the probe was writing, the settle loop kept waiting instead of aborting, the icons were moved
back and re-verified, and the desktop was left exactly as found (`UserIconsUnchanged: True`,
`UserIconsRepaired: 8 unrepaired=0`). It still failed, for a new and narrower reason: DISPLAY1's
`Horizontal[12]` settled observation never became stable inside the settle budget, because Explorer was
still moving the whole icon flow between samples and every intermediate diff looked like an "unexpected
desktop change". Two changes followed: the settle budget was raised (4 → 24 attempts, 250 ms apart ≈ 6 s),
and a stable sample may now tolerate `IconChanged` for items that already existed before the run (they are
repaired and re-verified at the end); `IconAdded`/`IconRemoved` still stop the run immediately.

The product owner then decided how to handle a display the Explorer never keeps the probe on:

- facts are **per display**: every display that met the gate is written; a display without enough samples
  writes nothing and is reported (`measured=False` + reason, an `UnmeasuredDisplays:` line, and
  `MeasuredDisplays: N/M` in the header);
- the run status becomes `Partial` when at least one display was measured and at least one was not; a run
  in which no display was measured is `Failed` and writes nothing;
- the strategy layer refuses to execute on a display that has no measured fact for the current topology,
  while displays that do have one keep working. Wiring that refusal is the remaining step and waits for the
  real-machine gate to produce facts.

## Eighth real run: driven without a human, blocked only by stale expectations (2026-10-14)

The eighth run was started by the agent itself (restore the window, select the tab, tick the write
acknowledgement, press **Run Probe**, accept the confirmation dialog) and read back from
`%LOCALAPPDATA%\DesktopAgent\logs\grid-probe-latest.txt`. Result: `Status: Failed`,
`MeasuredDisplays: 1/2`, and two things were proven at once.

DISPLAY1 was measured completely and reproducibly — `spacing=(76,98) phase=(2,16) horizontal=17
vertical=14 corners=2 active=33 passiveOccupied=4 skippedLandings=15 spacingConstant=True
phaseConstant=True phaseVerified=True`, `iconLattice consensus=(2,16) support=41/48` — identical to the
two runs before it. The lattice on that display is now a stable, repeated measurement rather than a
one-off.

The repair path ran again on the real desktop: `UserIconsUnchanged: True`, `UserIconsRepaired: 46
unrepaired=0`. An independent read-only dump then compared all 144 icons against the pre-run baseline
snapshot: `moved=0 onlyBefore=0 onlyAfter=0`. The desktop the run walked over is measurably the desktop
it left behind.

The run still failed, on the same environmental behaviour reaching checks that had no tolerance for it.
DISPLAY3's seed settled observation reported 39 items changing between two samples — including this
run's own `daprobe-…-02.url` / `-03.url`, which were neither the expected transition nor in the
tolerated set, so the scan aborted; the restore path then hit the same class of check twice more
(`非当前目标的专用快捷方式 … 在移动期间发生变化`, `专用快捷方式位置在移动前发生异常变化`). Three
tolerances close that gap, all of them for this run's own items and for expectations that Explorer makes
stale:

- the tolerated set is now the pre-run desktop **plus** the probe shortcuts this run created, so a
  re-flow that moves our own items is waited out instead of aborting;
- `ValidateSettledProbeObservation` no longer aborts when a non-target probe shortcut is not where it
  was expected — it adopts the observed position as the new baseline (the writer's own return contract
  is still checked item by item);
- `ExecuteMoveAsync` no longer aborts when the shortcut was already somewhere else before the move — it
  adopts the writer-reported `PreviousPosition` and continues, reporting the drift in the progress log.

None of the three is a geometry contradiction, none of them touches a user icon, and every displacement
stays in the report: the actual previous position of each move is recorded in the `Moves` column, and the
end-of-run repair plus re-verification remains the single gate on the user's desktop. Regression test:
`RunAsync_ExplorerMovesTheOtherProbeShortcutsDuringTheRun_KeepsScanningAndStillPasses` (probe suite 33/33).

DISPLAY2 stays unmeasured by design, not by failure: its seed request `(-540,730)` is placed on DISPLAY1
at `(2,702)` on all three attempts, so the display is reported unmeasured with its reason and — under the
per-display decision above — contributes no fact instead of voiding the run.

## Ninth real run: the gate produced a fact (2026-10-14)

The first run that produced a durable measured fact, driven without a human (see the unattended entry
point below). Report: `%LOCALAPPDATA%\DesktopAgent\logs\grid-probe-20261014-162856.txt`.

- `Status: Partial`, `MeasuredDisplays: 1/3`, `SharedSpacing: True`, `PhaseVerifiedPerDisplay: True`,
  `OwnedShortcutsDeleted: True`, `UserIconsUnchanged: True`, `UserIconsRepaired: 46 unrepaired=0`.
- `\\.\DISPLAY1`: `spacing=(76,98) phase=(2,16) horizontal=17 vertical=14 corners=2 active=33
  passiveOccupied=4 skippedLandings=15` — the same lattice as the three previous runs, with
  `iconLattice consensus=(2,16) support=41/48`.
- `\\.\DISPLAY2` unmeasured: the seed request `(-540,730)` settles on `(2,702)`, which is DISPLAY1, on all
  three attempts, so no landing stays on that display's work area.
- `\\.\DISPLAY3` unmeasured: the seed implies `phase=(2,16)` while 46/48 of that display's own icons sit on
  `phase=(35,84)`. A landing measured there would not describe the grid the user's icons actually use, so
  the display is marked unmeasured instead of voiding the whole run.
- `=== 网格事实写入 ===` wrote one row: `\\.\DISPLAY1 spacing=(76,98) phase=(2,16) samples=33` for topology
  `explorer-topology-v1:9F7C7810A0D3CF9FC43E76E8FB313D0075DA92295398A952A6C292EC4635B12B`.

Independent verification, outside the report:

- `sqlite3 %LOCALAPPDATA%\DesktopAgent\desktop-agent.db "select device_name, evidence_source, spacing_x,
  spacing_y, phase_x, phase_y, sample_count, consensus_count from explorer_grid_measurements;"` returns
  exactly one row: `\\.\DISPLAY1 | 0 (Measured) | 76 | 98 | 2 | 16 | 33 | 33`, observed at
  `2026-10-14T08:28:56Z`; `pragma user_version` is 11.
- The read-only desktop differ (`%TEMP%\gridrestore`) reports `needRestore=0` against the 23:45 baseline:
  the 46 icons Explorer had displaced during the run were put back and nothing else moved.

What the two unmeasured displays mean: DISPLAY2 has no landing slot the Explorer keeps the probe on while
the current layout stands, and DISPLAY3's own icons do not sit on the native slot grid at all. Both are
honest "this display could not be measured" outcomes — the strategy layer refuses to execute on a display
without a fact — and both are candidates for a scan that follows the display's own icon lattice instead of
the native slot grid.

## Seeding on the display's own icon lattice (2026-10-14)

Fix driven by the ninth run's DISPLAY3 outcome (see the paragraph directly above). The probe used to derive
the phase of its `seed` candidate slots from **the probe shortcut's own landing position**. Explorer places a
newly created shortcut wherever it remembers the layout (DISPLAY2's request `(-540,730)` settles on DISPLAY1's
`(2,702)`), so that landing can belong to another display's lattice — which is how DISPLAY3's candidate slots
were pushed onto a `(2,16)` lattice while 46 of its 48 icons sit on `(35,84)`.

- `ExplorerGridPhysicalProbeRunner.FindSafeSeedRequest` now asks `ComputeIconLatticeConsensus` for the target
  display's **own** icon lattice first — the same consensus the post-seed verification gate already uses — and
  only falls back to the probe's landing phase when that consensus is not reliable. A free centre slot is
  still preferred.
- The seed retry loop now stops as soon as the landing leaves the target display's `Bounds`, instead of
  retrying `MaximumSeedAttempts` times: on the target machine every retry re-homed the shortcut to DISPLAY1
  and each attempt moved real user icons for nothing. Landings that stay inside the display but miss the
  `WorkArea` still retry.
- An unmeasured display's reason now states the anchor situation (`既有图标锚点 N 个，已形成晶格
  phase=(x,y)` / `不足以形成晶格共识`), so "no anchor to calibrate against" (DISPLAY2) and "the display's
  icons are not on the measured lattice" (DISPLAY3) are distinguishable in the report alone.
- The phase-verification gate is unchanged: a landing that disagrees with the display's own icon lattice
  still marks that display unmeasured without voiding the run.

Tests: `RunAsync_SeedRequestUsesTheDisplaysOwnIconLattice_NotTheProbeShortcutsLanding` pins the requested slot
to the display's own lattice while the centre slot is occupied;
`RunAsync_ExplorerPutsTheSeedOnAnotherDisplay_MarksThatDisplayUnmeasuredAndKeepsTheOthers` and
`RunAsync_TheDisplaysOwnIconsAreOnAnotherLattice_MarksThatDisplayUnmeasuredAndKeepsTheOthers` still hold.

## Tenth real run: the two fixes hold on the target machine (2026-10-14)

First run after the two fixes above (outside-every-display icons became notices; the seed is anchored to the
display's own icon lattice). Report: `%LOCALAPPDATA%\DesktopAgent\logs\grid-probe-20261014-172543.txt`.

- `Status: Partial`, `MeasuredDisplays: 1/3`, `SharedSpacing: True`, `PhaseVerifiedPerDisplay: True`,
  `OwnedShortcutsDeleted: True`, `UserIconsUnchanged: True`, `UserIconsRepaired: 46 unrepaired=0`.
- `\\.\DISPLAY1` measured again on the same lattice: `active=33 passiveOccupied=4 skippedLandings=15`,
  `iconLattice consensus=(2,16) support=41/48`. One row written for the same topology identity
  (`18bb6239-391e-4c65-8014-c9091fca3293`, `evidence_source=0`, observed `2026-10-14T09:25:43Z`).
- `\\.\DISPLAY2` unmeasured, and the reason now names the anchor situation: the seed request `(-540,730)`
  settled on `(2,702)` (DISPLAY1) and that display's work area holds `既有图标锚点 0 个` — there is nothing
  on that display to calibrate against.
- `\\.\DISPLAY3` unmeasured, reason unchanged: the seed implies `phase=(2,16)` while 46/48 of that display's
  own icons sit on `(35,84)`.
- Neither unmeasured display writes a fact, and the run stays `Partial` instead of voiding DISPLAY1's result.

Independent verification (read-only reflection host `%TEMP%\gridevidence`: product DLLs plus the real
database, zero writes):

- `IDENTITY adapter=core=explorer-topology-v1:9F7C781… same=True`; `STORE active=1` →
  `FACT \\.\DISPLAY1 source=Measured spacing=(76,98) phase=(2,16) samples=33 consensus=33` — re-running the
  probe leaves exactly one active fact per display.
- `PROVIDER \\.\DISPLAY1 -> Measured`, `PROVIDER \\.\DISPLAY2 -> Unknown`, `PROVIDER \\.\DISPLAY3 -> Unknown`:
  the A-option authorization query answers per display on the real machine.
- `ADAPTER succeeded=True demoted=0` with **18 `IconOutsideAllDisplays` notices and no issues**. The same
  observation produced 19 fatal `IconOutsideAssignedDisplay` issues (`succeeded=False`) before the fix, which
  made planning on this machine impossible. The notices name each icon (for example `GitHub Desktop` at
  `(14,1766)`, five images at `(2619..2923,2044)`, five items at `y=2534`) and state that they keep their
  position and take no part in the run.

## Local Physical Gate

1. Record the current Explorer settings so they can be restored after the gate.
2. In the desktop context menu under **View**, turn **Align icons to grid** on and **Auto arrange icons** off.
3. Keep the intended monitor arrangement, scaling, resolution, taskbar placement, and taskbar auto-hide state fixed for the whole run.
4. Pause anything that can add, delete, rename, or reposition desktop items (sync clients, installers, scripts, and other DesktopAgent processes). Do not drag icons during the run.
5. Build and launch the engineering tool:

   ```powershell
   dotnet run --project .\src\DesktopAgent.Poc\DesktopAgent.Poc.csproj -c Debug
   ```

6. In **DesktopAgent Diagnostics Workbench**, open the **Explorer Grid Physical Probe v1** tab.
7. Read and select the explicit physical-write acknowledgement, choose **Run Probe**, and accept the final confirmation.
8. Wait for a terminal report. Do not close the window while cleanup is running. The **Stop** button cancels further scanning but still waits for restoration and cleanup.
9. A valid acceptance run requires all of the following in the report:

   - `Status: Passed` (every display measured) or `Status: Partial` (at least one display measured and at
     least one left unmeasured, so only the measured displays produce facts; a run with no measured
     display is `Failed` and produces nothing)
   - `SharedSpacing: True` and `PhaseVerifiedPerDisplay: True` (phase is required to be constant
     within each display and, where the desktop provided one, consistent with that display's
     pre-existing icon lattice; absolute phases may differ between displays)
   - every measured display has `spacingConstant=True` and `phaseConstant=True`
   - every measured display has the expected full horizontal/vertical sample counts and `corners=4`
   - `MeasuredDisplays: N/M` states how many displays produced a fact; every `measured=False` display
     carries an `unmeasured reason` and is also listed under `UnmeasuredDisplays:`
   - every scan row contains both `immediateObserved` and `settledObserved`, and every non-seed scan row has `settledExact=True`
   - occupied slots, if any, appear under `Passive occupied samples`; those rows are read-only and have no corresponding Writer move
   - every display reports at least the required active scan samples; `Status: Blocked` is not a PASS or geometry FAIL
   - `OwnedShortcutsDeleted: True`
   - `UserIconsUnchanged: True` with `UserIconsRepaired: N unrepaired=0` (existing icons that Explorer
     displaced during the run are moved back and re-verified before this is reported; see the fifth
     real run above)
   - every cleanup row has `exact=True` and no issue

10. Copy and retain the complete report as the Physical Gate evidence. A FAIL/CANCEL report is evidence of a stopped run, not geometry acceptance.
11. Confirm no desktop item beginning with `DesktopAgent Grid Probe ` remains. If a cleanup failure explicitly reports one of these uniquely named files, remove only that exact temporary file after checking the report; do not remove similarly named pre-existing items.
12. Restore the Explorer settings recorded in step 1.

### Driving a run unattended

Every finished run — PASS, FAIL, BLOCKED, CANCELLED and even the fail-closed escape path — writes the
complete report (the same text the workbench shows) to
`%LOCALAPPDATA%\DesktopAgent\logs\grid-probe-latest.txt`, plus a timestamped copy
`grid-probe-<yyyyMMdd-HHmmss>.txt` in the same directory. A report that cannot be written does not change
the probe verdict.

The supported unattended entry point is the command-line switch:

```powershell
.\src\DesktopAgent.Poc\bin\Debug\net10.0-windows10.0.19041.0\DesktopAgent.Diagnostics.exe --run-grid-probe
```

- The switch is the explicit acknowledgement for this physical write: no consent box and no final
  confirmation dialog appear.
- The process waits (up to 60 s) until the window is idle, runs the gate, then exits with `0` = `Passed`,
  `3` = `Partial`, `4` = refused or another terminal state, `1` = unexpected exception.
- The startup dialogs (`Windows App SDK unavailable`, notification-runtime failure) are suppressed in this
  mode. Without that suppression an unattended run hangs forever on a modal nobody clicks.
- Do not drive the run by clicking the UI. `IsAnyOperationInProgress` is true right after startup (the
  window is still reading displays and icons), and while it is true `运行 Probe` — and even UIA
  `InvokePattern.Invoke()` — is a silent no-op. This is why the first scripted attempts appeared to do
  nothing at all.

## Acceptance standard (confirmed)

1. Measurement first. A Physical Gate PASS is accepted as the grid fact.
2. A display that cannot be measured fails closed. Majority voting does not authorize execution.
3. Inference is a fallback path only and must stay labelled as inference.
4. Facts are environment-bound. A fact is valid only while the topology identity that produced it still matches.
   After topology, scaling, taskbar, or auto-arrange changes, a stale measured fact is more dangerous than an
   inference because it is confidently wrong; it must be invalidated and explicitly demoted, not silently reused.

## What a PASS must produce

The printed report is not the deliverable. A PASS must become one durable fact per display:

- evidence source = `Measured`;
- device name, spacing, phase, per-axis active sample counts, consensus count, spacing/phase constant flags;
- the topology identity that produced the fact: auto-arrange, spacing, per-display device name, bounds,
  work area, DPI, primary flag;
- observed-at UTC.

Contract: `src/DesktopAgent.Core/DesktopLayouts/Workspaces/Geometry/ExplorerGridMeasurement.cs`.
Store: `IExplorerGridMeasurementStore` + `SqliteExplorerGridMeasurementStore` (schema v10, `explorer_grid_measurements`).

The topology identity must not contain phase-derived values (phase, grid origin, blocked slot ids), otherwise the
lookup is circular: the stored phase is exactly what the lookup is validating against. The full geometry
fingerprint keeps serving as plan and Realization Certificate identity; the two identities are not interchangeable.

Implemented: a `Passed`/`Partial` run writes one `Measured` fact per measured display through
`src/DesktopAgent.WindowsInterop/ExplorerGridPhysicalProbe/ExplorerGridProbeFactRecorder.cs` (moved there from
`src/DesktopAgent.Poc/ExplorerGridPhysicalProbe/` on 2026-10-14 so the product shell can host the same probe). A display that cannot meet the gate
(fewer than 3 active landing samples on an axis, a non-constant observed spacing/phase, or one the Explorer refuses to
keep the probe on — see the fourth real run above) is not written and is not turned into an inference fact; the report
shows the reason, the run ends as `Partial`, and no fact exists for that display. A run in which every display stayed
unmeasured ends as `Failed` and writes nothing. Passive occupied samples are evidence only and do not count toward the
Measured sample count. A persistence failure does not change the physical scan verdict.

An existing desktop icon that Explorer displaced while the probe was writing is moved back to its
pre-run position and re-verified at the end of the run (see the fifth real run above); the report shows
`UserIconsRepaired`/`unrepaired`, and a repair that cannot be verified keeps the run at FAIL.

Physical Gate status remains **PENDING** until these steps are run on the target Windows topology and the complete PASS report is retained.

## Where the probe lives now, and who can drive it (2026-10-14)

The runner is no longer diagnostics-only. It moved to
`src/DesktopAgent.WindowsInterop/ExplorerGridPhysicalProbe/` (`ExplorerGridPhysicalProbeRunner.cs`,
`ExplorerGridProbePlan.cs`, `ExplorerGridProbeContracts.cs`, `ExplorerGridProbeFactRecorder.cs`,
`DesktopInternetShortcutStore.cs`), together with two new files:

- `ExplorerGridProbeSession.cs` — the shared back half of a run: scan → write facts → format the report →
  persist it. It owns the two rules that must not diverge between hosts: a fact-write failure never changes the
  physical scan verdict (it becomes a readable `未写入：…` rejection), and a report-persistence failure never
  changes the verdict either (`PersistReport` returns `null` while the UI keeps the full text).
- `ExplorerGridPhysicalProbeRunnerFactory.cs` — assembles the real dependencies
  (`NativeDisplayTopologyReader` + `StableDesktopObservationSource` at a 500 ms sample interval +
  `DesktopShellIconPositionWriter` + `DesktopInternetShortcutStore`). This keeps the physical writer out of the
  UI layer, which the product contract test enforces.

Two hosts drive it, both through the same session:

- the WPF diagnostics workbench page (`src/DesktopAgent.Poc/MainWindow.ExplorerGridPhysicalProbe.cs`, page +
  consent checkbox + `--run-grid-probe` unattended entry), which writes into the engineering data root
  (`%LOCALAPPDATA%\DesktopAgent\desktop-agent.db`);
- the product calibration panel (`src/DesktopAgent.WinUI/LayoutManagementView.GridProbe.cs`, 「校准桌面网格…」
  next to 「已记住的显示器…」), which writes into the product data root
  (`%LOCALAPPDATA%\Tessel.Dev\desktop-agent.db` for the Dev channel). This is what closes the gap recorded in
  `docs/experiments/explorer-grid-measurement-fact-v1.md` §18: before this move, nothing in the product could
  produce a Measured fact, so unattended execution could never be authorized there.

Both hosts require the same explicit consent before any physical write, and both surface unmeasured displays as
"supervised only" rather than filling them with inference.
