# Tessel Resident Lifecycle Optimization Spec

> Status: planned optimization slice  
> Recorded: 2026-08-12  
> Applies after: M5-07 stability baseline complete

## Goal

Tessel is a resident Windows utility. The optimization target is therefore not one universal memory number, but a predictable relationship between four runtime states:

```text
Deep Resident
    |
    | tray / lightweight intent signal
    v
Prewarm
    |
    | open product
    v
Interactive
    |
    | explicit expensive feature intent
    v
Active Tool
    |
    | close to tray
    v
Warm Resident
    |
    | timeout / memory pressure
    v
Deep Resident
```

The goal is simple:

- while the user is interacting, prioritize responsiveness;
- while the product is sitting in the tray, retain only what is necessary to remain reliable;
- use user intent and idle time to hide hydration cost;
- never optimize Task Manager numbers by introducing visible latency or unsafe state reuse.

This slice begins only after the original M5-07 stability baseline is complete. Pre-optimization and post-optimization measurements must use the same sampler and comparable workflows.

## Non-goals

This slice does not:

- add new layout, AI or automation capability;
- change Writer / Recovery semantics;
- change the M5-01 automation authorization matrix;
- change Pending confirmation semantics;
- perform real desktop icon movement during performance validation;
- add forced Working Set trimming;
- add routine `GC.Collect()` calls simply to reduce reported memory;
- treat cached presentation data as execution evidence.

## Current known pressure points

The current product shell constructs several heavy objects eagerly.

`MainWindow` currently creates the Layout workbench and Overlay appearance settings view during window construction. `LayoutManagementView` in turn owns objects including:

- SQLite layout template store;
- SQLite rule store;
- product settings store;
- `StaThreadExecutor`;
- `WindowsDesktopObservationSource`;
- Shell icon / display readers;
- `DesktopZoneOverlayHost`;
- the complete Layout page UI tree.

This is convenient for implementation but means resident and Overview states can inherit resources that are only required for layout editing or fresh desktop observation.

The optimization should remove this lifetime coupling rather than merely attempt to reclaim memory after the fact.

## Runtime states

### Deep Resident

Deep Resident is the long-term tray state.

It may retain:

- application / single-instance coordination;
- tray icon host and tray callback;
- minimal settings required for product behavior;
- truly required lightweight background listeners;
- small cached product summary state where useful;
- cancellation / lifecycle coordination required for later hydration.

It should not retain only for convenience:

- full Layout page visual tree;
- `LayoutManagementView`;
- fresh desktop observation services;
- Shell COM / STA resources used only by active tools;
- Overlay HWNDs;
- ColorPicker / appearance preview resources;
- AI provider clients or network activity;
- large page-specific lists and preview models.

### Prewarm

Prewarm is speculative and short-lived.

It may begin when the user provides a strong low-risk intent signal, for example:

- opening the tray context menu;
- `PointerPressed` on a navigation target;
- deliberate navigation hover after a short dwell threshold;
- post-first-frame idle work after the product has already been shown.

Prewarm must be:

- asynchronous;
- non-blocking to the initiating interaction;
- side-effect free;
- cancellable where practical;
- safe to abandon;
- unable to grant Writer or automation authority.

The tray context menu itself must be shown immediately. Prewarm must never run synchronously before `TrackPopupMenuEx`.

Good tray-menu prewarm candidates:

- product shell skeleton;
- lightweight settings snapshot;
- Overview read model;
- layout metadata / names required for first paint.

Bad tray-menu prewarm candidates:

- fresh Shell observation;
- STA COM desktop reads;
- Overlay creation;
- AI network requests;
- anything that changes persistent state.

### Interactive

Interactive begins when the main Tessel window is visible and the user is navigating normal product pages.

Responsiveness has priority over minimizing every MiB.

Expected warm resources may include:

- NavigationView and common shell visual tree;
- Overview cards and lightweight read model;
- settings presentation state;
- pending list presentation state;
- layout list metadata;
- other inexpensive state useful for normal navigation.

Interactive does not automatically imply that Active Tool resources should exist.

### Active Tool

Active Tool begins only when an explicit operation needs expensive capability.

Examples:

- fresh desktop observation for impact preview;
- Shell COM desktop reading;
- Desktop Overlay native windows;
- future Desktop Zone Editor;
- AI request execution;
- other explicit expensive tools.

Active Tool resources should have an explicit owner and a clear release path.

Closing or completing the feature should not leave native windows, STA executors, page-specific services or large transient models alive indefinitely unless a deliberate warm-cache policy keeps them for a short bounded period.

### Warm Resident

Closing the main window to the tray should be instant from the user's point of view.

The product may retain Interactive resources briefly so a quick reopen does not become a cold launch.

Initial design target for the warm retention window is approximately 30-60 seconds. This is a product tuning value, not a frozen safety constant.

Warm Resident returns to Deep Resident when:

- the warm TTL expires;
- system memory pressure requests deeper release;
- explicit cleanup is appropriate;
- application exit begins.

The transition should be asynchronous and must not delay the close-to-tray action.

## Page lifetime direction

Pages should not all follow the same lifetime rule.

### Overview

Overview should use a dedicated lightweight read model.

Opening Overview must not require construction of the complete Layout workbench, Shell COM observation pipeline or Overlay host.

A last-known lightweight display snapshot may be rendered immediately and refreshed asynchronously. Cached display state is presentation only and is never valid execution evidence.

### Layout

The Layout page may use staged hydration:

```text
open Layout
    |
    +-- render page shell / list area
    |
    +-- load layout metadata
    |
    +-- construct editing services only when edit / preview requires them
    |
    +-- construct fresh observation / Overlay path only on explicit feature intent
```

The first visible page should not wait on services that are invisible until a later action.

### Pending

Pending list access may initialize its store when the page is first used. Repeated navigation should reuse lightweight state where safe, but close-to-tray deep hibernation may release large presentation collections if measurements show meaningful savings.

### Settings

Settings should remain inexpensive. Native ColorPicker or real desktop Overlay preview resources should exist only while those controls are in use.

## Intent-driven prewarm

### Tray context menu

Target flow:

```text
Deep Resident
    |
    | WM_RBUTTONUP / tray context-menu intent
    +-----------------------> BeginPrewarmAsync()
    |
    +-----------------------> show menu immediately

menu command selected
    |
    +-- Open / Layout / Settings -> continue hydration -> Interactive
    |
    +-- Exit -> cancel prewarm -> exit
    |
    +-- dismissed -> short warm TTL -> Deep Resident
```

The menu must remain responsive even if prewarm fails.

### Navigation pointer signals

`PointerPressed` may begin cancellable page preparation before `Click` / selection completes.

Hover may only be used after a short dwell, approximately 150-250 ms, to avoid hydrating pages when the pointer merely crosses the navigation rail.

No pointer prewarm may alter settings, persistent state, desktop state, automation state or Writer authority.

## First-frame priority

The user-visible shell should win over hidden setup work.

Preferred order:

```text
window becomes visible
    ↓
brand / title / navigation / card skeleton
    ↓
lightweight cached or persisted summary
    ↓
normal asynchronous data refresh
    ↓
optional idle prewarm
```

Avoid blocking the UI thread on:

- SQLite reads not required for first paint;
- COM reads;
- fresh desktop observation;
- AI provider initialization;
- Overlay construction.

## Cancellation and race rules

Hydration and hibernation may overlap with rapid user actions. Lifecycle code therefore needs explicit generation / cancellation semantics.

Examples:

- close to tray begins Warm Resident timeout, then immediate reopen cancels deep hibernation;
- tray menu opens, prewarm begins, menu is dismissed, then window opens before TTL expires;
- user starts opening Layout while memory pressure requests cache release;
- app exit occurs while hydration is still running.

Rules:

- stale async completion must not replace newer state;
- lifecycle cancellation must be expected, not treated as an error;
- hibernation must never dispose a resource currently owned by an Active Tool;
- exit has priority over prewarm / hydration;
- no speculative state becomes an execution authorization.

## Memory pressure

If a reliable Windows memory-pressure signal is available without introducing disproportionate complexity, it may shorten Warm Resident retention and release optional caches earlier.

Memory pressure is a cache-depth signal, not an excuse to destroy essential tray or lifecycle state.

The first implementation may ship with deterministic TTL-only behavior if that is easier to validate. Memory-pressure adaptation is optional until measured benefit justifies the complexity.

## Measurement plan

Use the existing M5-07 sampler for before / after resource comparisons.

Required resource states:

```text
Deep Resident
Warm Resident
Interactive Overview
Interactive Layout
Active Tool + Overlay
```

Record at least:

- Working Set MiB;
- Private Memory MiB;
- Handle Count;
- Thread Count;
- GDI Objects;
- USER Objects;
- idle CPU trend.

Latency measurements should additionally record:

- cold product open to first visible frame;
- Deep Resident -> Interactive restore latency;
- Warm Resident -> Interactive restore latency;
- first Layout open latency;
- warm Layout reopen latency;
- first explicit Preview latency;
- second Preview latency where caching legitimately applies.

Do not freeze arbitrary absolute SLOs before the first implementation. Compare the same machine and same workflow before and after optimization.

## Acceptance principles

The optimization passes only if all of the following remain true:

- tray reliability remains unchanged or better;
- single-instance behavior remains unchanged;
- close-to-tray remains immediate;
- quick reopen is not meaningfully worse;
- Deep Resident footprint is measurably lower or lifetime coupling is demonstrably reduced;
- Active Tool resources release when the feature ends or after an explicitly bounded warm policy;
- repeated hydration / hibernation does not create handle, thread, GDI or USER leaks;
- Explorer restart tray recovery still passes;
- all M5 automation and Writer safety gates remain unchanged;
- no cached presentation snapshot is reused as execution evidence.

## Implementation slicing

Suggested implementation order:

```text
Slice A
Overview lightweight read model

Slice B
lazy page creation / page lifetime host

Slice C
separate Layout metadata hydration from expensive observation services

Slice D
Warm Resident -> Deep Resident hibernation

Slice E
tray-menu asynchronous prewarm

Slice F
PointerPressed / optional hover prewarm

Slice G
before/after performance and repeated lifecycle soak
```

Each slice should remain independently testable. Do not combine lifecycle refactoring, new product capability and Desktop Zone Editing into one implementation PR.

## Engineering rule

The optimization philosophy is:

> 利用用户意图提前加载，利用用户离开延迟释放，利用空闲时间做准备，利用内存压力决定缓存深度。

The product result should feel simpler than the implementation:

> 托盘里很轻，点开很快，用起来不抠搜。
