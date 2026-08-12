# Tessel Desktop Zone Editor Spec

> Status: product interaction specification  
> Recorded: 2026-08-12  
> Planned stage: M5-07.5 Desktop Zone Editing

## Purpose

Tessel should let users shape the desktop space directly, not only edit zones inside the Layout workbench.

Desktop Zone Editing is a product interaction surface for creating and adjusting layout zones on the real Explorer desktop. Editing zones does **not** move desktop icons, does not grant Writer authority, and does not change the existing automation safety model.

A user should be able to draw the space first, then decide whether to place icons manually or later use rules and an authorized execution flow.

## Product model

Two modes remain deliberately separate:

```text
Display Overlay
    -> click-through
    -> no activation
    -> shows saved zones only
    -> does not edit layout

Desktop Zone Editor
    -> explicit edit mode
    -> accepts pointer input
    -> moves / resizes / creates / removes zones
    -> Layout Editing = Automation BLOCK
    -> does not move desktop icons
```

Do not turn the existing display-only overlay into one window with many hidden responsibilities just to reuse code.

Shared geometry, appearance and rendering helpers are fine. Window lifecycle, hit testing and editing authority should stay explicit.

## Entry points

Preferred first entry point:

- Layout page action: `在桌面上编辑`.

Possible later entry points:

- tray menu under Layout;
- Overview quick action;
- shortcut from the normal desktop zone overlay.

Entering the editor is always an explicit user action.

## Entering edit mode

When the user chooses `在桌面上编辑`:

1. validate that a layout or edit draft exists;
2. enter Layout Editing state before the desktop editor becomes interactive;
3. suspend automation through the existing Layout Editing gate;
4. show the target display and current zones on the Explorer desktop;
5. optionally hide or de-emphasize the main Tessel window so the desktop remains the working surface.

Entering edit mode must not trigger fresh Writer planning or icon movement.

## V1 interactions

### Select zone

Clicking a zone selects it and shows a clear active outline and resize handles.

Selection by itself is passive. It may expose zone name, bounds and appearance controls, but it does not persist changes until the user completes the edit flow.

### Move zone

Drag the zone body or title area to move the selected zone.

The editor should:

- preserve normalized layout coordinates rather than storing transient physical screen pixels as the layout contract;
- keep the zone inside the target work area unless the product later adds an explicit overflow concept;
- update visual feedback continuously while dragging;
- avoid persisting every pointer move.

### Resize zone

Resize handles allow changing width and height.

V1 should support corner handles and may add edge handles if implementation remains simple.

Minimum zone size must prevent accidental collapse into an unusable sliver.

### Create zone

Provide one explicit create action.

A new zone may begin at a sensible default size near the center or use click-drag creation. The first implementation should choose whichever path produces more reliable pointer behaviour on real Windows without complicating hit testing.

New zones receive a normal product name such as `分区 3` until renamed.

### Delete zone

Deleting a zone is explicit and only changes the in-memory layout draft until the user completes the edit.

If rules refer to the zone, the editor must fail closed or require the relationship to be resolved before save. It must not silently leave persisted rules pointing at a removed zone.

### Rename and color

Name and per-zone color remain layout properties.

V1 may expose these through a compact editor panel or reuse the main Layout page after the geometry edit ends. Geometry editing should not become a floating settings dashboard if that would obscure the desktop.

## Snapping

Snapping is desirable because freehand zone alignment quickly becomes irritating.

V1 snapping candidates:

- target work-area edges;
- nearby zone edges;
- nearby zone centers where useful;
- small visual guide when snap is active.

Snapping should have a small pixel threshold and must not rewrite zone geometry after pointer release in surprising ways.

A modifier key to temporarily disable snapping can be added later if physical testing shows it is useful.

## Desktop interaction boundary

V1 uses a clear modal product state:

```text
Normal desktop
    -> overlay is click-through

Explicit Desktop Zone Edit Mode
    -> editor receives pointer input
    -> desktop icon interaction is secondary

Complete / Cancel
    -> editor closes
    -> normal click-through overlay may return
```

This is intentionally simpler than trying to make arbitrary parts of one transparent overlay interactive while every other pixel passes through to Explorer.

## Later enhancement: border-only interaction

A later version may support:

- zone borders / resize handles receive pointer input;
- zone interior remains click-through to Explorer;
- users can resize a zone and immediately drag desktop icons into its interior without leaving the surface.

This requires precise native hit testing and should only be added after V1 is stable on multiple displays, DPI settings and Explorer states.

It is an interaction polish item, not a prerequisite for M5-07.5 completion.

## Complete and Cancel

The editor must always expose clear `完成` and `取消` actions.

### Complete

`完成`:

1. validates the resulting draft;
2. returns the updated geometry to the normal Layout workflow;
3. persists only through the existing template + rules save coordinator;
4. exits Layout Editing only after the edit session has safely ended;
5. does not apply the layout to desktop icons.

Saving a layout and applying a layout remain separate actions.

### Cancel

`取消`:

- discards geometry changes from the desktop edit session;
- restores the previous draft / persisted layout view;
- exits Layout Editing;
- restores the normal display overlay only if it was previously requested by the user.

No partial geometry should leak into persistence after cancel.

## Safety and automation invariants

Desktop Zone Editing must preserve all existing v1.0 safety rules.

In particular:

- `Layout Editing = Automation BLOCK`;
- the editor never calls Shell Writer;
- moving a zone never moves an icon;
- completing an edit never counts as authorization to execute the layout;
- AI cannot silently change active desktop geometry through this surface;
- saved geometry is preference / layout data, not evidence that the desktop currently matches it;
- future execution still requires fresh observation, fresh planning, authorization and the normal Writer safety chain.

## Multi-display behaviour

The first implementation should edit one target display at a time.

The editor must respect:

- WorkArea rather than assuming full monitor bounds;
- per-display DPI;
- the layout target binding already stored by the draft;
- deterministic display identity rules already used by the product.

If the target display disappears during editing, fail closed and return control to the product instead of moving the editor to another display implicitly.

## Window behaviour

The desktop editor should feel attached to the desktop rather than like a normal foreground application window.

Desired behaviour:

- no unnecessary taskbar entry;
- no accidental topmost behaviour over unrelated applications;
- no focus stealing outside explicit edit interaction;
- reliable cleanup of HWND / GDI / USER resources on Complete, Cancel, display loss and product exit;
- no duplicate editor windows for the same edit session.

The exact ownership / Z-order implementation should be validated physically on Windows before being frozen.

## Performance boundary

M5-07.5 comes after the M5-07 stability baseline and resident lifecycle optimization work.

The editor should therefore be an Active Tool:

```text
Resident / Interactive Tessel
    -> user explicitly enters desktop editing
    -> Desktop Zone Editor resources hydrate
    -> edit session ends
    -> editor resources are released
```

Desktop Zone Editor HWNDs and rendering resources must not remain alive for hours after the user returns to the tray.

## Physical gate

M5-07.5 should not be considered complete until real Windows testing confirms at least:

1. enter and exit editor repeatedly without crash or duplicate windows;
2. move and resize zones correctly on the primary display;
3. create and delete zones;
4. Complete persists intended geometry;
5. Cancel restores the previous geometry;
6. automation remains blocked for the entire edit session;
7. no desktop icon moves during editing or save;
8. normal overlay returns to click-through behaviour after editing;
9. multi-display target selection stays on the intended display;
10. DPI / scaling changes do not produce obviously incorrect zone geometry;
11. Explorer restart or display loss fails safely;
12. repeated edit sessions do not show unbounded Handle / GDI / USER growth.

## Non-goals for first implementation

Do not require these for the first Desktop Zone Editing slice:

- free-form polygon zones;
- arbitrary rotation;
- overlapping-zone automation semantics;
- live AI editing gestures;
- simultaneous multi-display editing;
- border-only interactive / interior click-through hybrid mode;
- automatic desktop icon movement while dragging or saving a zone.

The first goal is simple: **make the desktop itself a reliable place to shape Tessel's rectangular zones, without changing the desktop behind the user's back.**
