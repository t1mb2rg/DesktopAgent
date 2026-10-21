# M3-B07-D Safe Failure / Fuse Acceptance

## Goal

Verify the real M3 automatic failure-feedback, cooldown, retry, and fuse chain on Windows without allowing any desktop Writer call.

Expected chain:

```text
Active Suggestion
→ Automatic
→ 05C preflight
→ B01 fresh observation / fresh candidate
→ B02 automatic safety
→ B03 Coordinator + commit-point gate
→ injected safety-snapshot creation failure
→ Coordinator = FailedBeforeWrite
→ B04 RecordExecutionFailure
→ 30s cooldown
→ automatic retry after cooldown
→ third consecutive failure
→ Fuse
```

## Why inject at safety snapshot creation

`TargetDisplayLayoutExecutionCoordinator` creates the safety snapshot only after it has freshly reread the desktop, rebuilt the latest execution plan, and passed the B03 scope gate. The snapshot factory is still before:

- initial recovery checkpoint;
- first `IDesktopIconPositionWriter.MoveDesktopIcon` call;
- any real desktop position side effect.

The coordinator already catches a snapshot factory exception and converts it into a structured `FailedBeforeWrite` result. Therefore B04 receives a real Coordinator failure and applies the normal failure/cooldown/fuse policy, while Writer remains unreachable.

## Arming

The normal B07 acceptance variables remain mandatory. Add exactly:

```powershell
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_FAILURE_MODE = 'FailBeforeWriterAtSafetySnapshot'
```

Recommended full B07-D configuration:

```powershell
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MODE = 'ValidatedOpen'
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_ARM = 'I_UNDERSTAND_THIS_ALLOWS_UNATTENDED_REAL_DESKTOP_WRITES'
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MAX_MOVES = '1'
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_MAX_STEPS = '1'
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_ALLOW_STAGING = 'false'
$env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_FAILURE_MODE = 'FailBeforeWriterAtSafetySnapshot'
```

The failure variable alone cannot arm acceptance. Any spelling/case mismatch fails closed.

## Physical acceptance sequence

1. Start the Debug PoC and explicitly confirm the M3 acceptance warning.
2. Bind `M2 Acceptance [SelectedTarget]` to one display and start monitoring.
3. Move one ordinary test icon so an `Active` suggestion appears with `Moves=1`, `Steps=1`, `Staging=False`.
4. Choose tray `Automatic` once.
5. The icon must not move.
6. First structured failure should produce `Requested Automatic / Effective Suggestion | Cooldown`.
7. Leave the desktop untouched. After cooldown expires, the same still-current suggestion may be retried automatically.
8. After the third consecutive structured failure, tray must show `Fuse` and no further automatic attempt may occur.
9. `Reset Fuse` must be explicit; do not use it during the primary acceptance run.

## Pass criteria

- Writer-visible desktop movement: **0**.
- Coordinator failures are structured `FailedBeforeWrite`, not escaped `FaultedAndPaused` exceptions.
- Each failure enters the normal 30s cooldown.
- Consecutive failure counter reaches the configured threshold of 3.
- Fuse trips on the third failure.
- Once fused, Effective mode is no longer Automatic and no fourth attempt occurs.
- No unresolved recovery session is created by the injected failure.

## Disarm

After acceptance:

```powershell
Remove-Item Env:DESKTOPAGENT_M3_AUTO_ACCEPTANCE_FAILURE_MODE -ErrorAction SilentlyContinue
```

Then clear the other B07 acceptance variables as usual.

## Scope

This seam is Debug acceptance composition only. Normal Release behavior, Writer implementation, M2 Coordinator semantics, recovery semantics, and ordinary M3 automatic flow are unchanged when the failure variable is absent.
