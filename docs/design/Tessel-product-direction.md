# Tessel Product Direction

> Status: product direction note  
> Recorded: 2026-08-12

## Name

The product name is **Tessel**.

`DesktopAgent` remains the internal engineering / repository naming for now. Existing assemblies and namespaces such as `DesktopAgent.Core`, `DesktopAgent.WinUI`, `DesktopAgent.Persistence` and `DesktopAgent.WindowsInterop` do not need to be renamed just for branding.

The external product identity can move to Tessel first: window title, tray tooltip, About page, installer, app icon and public-facing documentation. Internal renaming is a separate decision and should only happen if it has real engineering value.

## Overall UI direction

Tessel should feel like a quiet, structured Windows utility rather than an AI-first product or an engineering workbench.

The UI is not tied to light or dark mode. It should follow the Windows system theme by default and use WinUI theme resources wherever practical so both light and dark modes feel native rather than being two separately designed skins.

Visual language:

- stable left-side navigation;
- card-based information hierarchy;
- desktop / layout visualization as the main visual element;
- restrained Fluent-style surfaces and spacing;
- geometry, tiles and zones as recurring brand motifs;
- color used mainly for brand identity, status and zone differentiation;
- avoid turning the product surface into a multi-color dashboard simply because the Tessel logo may use multiple colors.

## Overview page

The Overview page should become the main product-facing page and the first place where Tessel feels like a finished application.

Preferred structure:

```text
Left navigation
    |
    +-- Overview
    +-- AI assistant
    +-- Pending
    +-- Layouts
    +-- Settings

Overview content
    |
    +-- current desktop / layout visualization (primary card)
    +-- current layout
    +-- desktop item count
    +-- automation state
    +-- pending count
    +-- recent activity
    +-- quick actions
```

The main card should answer the most important question visually: **what does Tessel think the desktop looks like right now?**

The surrounding cards should stay small and useful rather than becoming a generic analytics dashboard. Good candidates are current layout, item count, automation mode and pending actions.

Recent Activity should translate internal history into normal product language. Quick Actions can expose common explicit actions such as opening a layout, showing desktop zones or starting a safe organization workflow when that execution path is available.

The Overview page should use a lightweight read model. Showing a desktop summary must not require constructing the complete Layout workbench, fresh Shell observation path or other expensive editing/runtime services.

## Existing layout workbench

The current three-column layout workflow remains a good information architecture:

```text
My layouts | Desktop canvas | Properties
```

It should not be replaced simply to match the Overview page. Later refinement should make it visually consistent with Tessel through card hierarchy, spacing, typography, zone colors and a stronger desktop canvas.

The desktop canvas remains the visual center of the layout workflow.

## Runtime lifecycle and footprint direction

Tessel is a resident utility, so the resource target depends on whether the user is actively using the product or it is only sitting in the notification area.

The goal is not to make every page maximally lazy. The goal is to keep the resident state small without moving all startup cost into the user's first click.

Preferred lifecycle:

```text
Deep Resident
    |
    | tray interaction
    v
Prewarm
    |
    | open product
    v
Interactive
    |
    | explicit feature intent
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

### Deep Resident

Keep only what is required for the product to remain a reliable Windows resident utility:

- single-instance / lifecycle coordination;
- tray icon and commands;
- minimum settings / automation state;
- genuinely required background listeners;
- lightweight state needed to reopen the product.

Heavy page trees, editing surfaces and expensive native services should not remain alive only because the product is in the tray.

### Interactive

When the user opens Tessel, responsiveness takes priority over minimizing every MiB. The Shell, Overview and normal product navigation may be hydrated directly. Frequently used lightweight metadata can be kept warm while the panel is open.

### Active Tool

Expensive capabilities should still follow real user intent. Examples include fresh Shell observation, STA / COM readers, Overlay native windows and AI provider/network work. Entering the Layout page alone should not automatically wake every expensive capability.

### Warm Resident

Closing to tray should hide the product immediately, but it does not need to destroy all interactive state immediately. A short warm interval can preserve the UI shell for quick reopen. If the user does not return, or the system is under memory pressure, Tessel can continue shrinking toward Deep Resident.

This avoids both bad extremes:

- keeping the complete UI/workbench resident for hours;
- destroying everything immediately and making every quick reopen a cold start.

## Intent-driven prewarm

Tessel can use low-risk interaction signals to hide part of the hydration cost before the final command is clicked.

Good candidates:

- opening the tray context menu begins a non-blocking Interactive prewarm;
- `PointerPressed` on navigation can begin cancellable page-specific preparation before `Click` completes;
- deliberate navigation hover may prewarm after a short dwell threshold, not on every pointer pass;
- after the first frame is presented, idle time can be used to warm lightweight common data.

Tray-menu prewarm is especially useful because opening the menu is already a strong signal that the user is likely to interact with Tessel. Prewarm must never delay `TrackPopupMenuEx` or make the menu itself feel slower.

If the menu is dismissed without opening Tessel, the warmed state may remain for a short TTL before returning to Deep Resident. Choosing Exit should cancel unnecessary prewarm work immediately.

All speculative work must be:

- side-effect free;
- cancellable where practical;
- safe to abandon;
- incapable of granting Writer or automation authority.

## Loading behaviour

The first visible frame should win over hidden background preparation.

A page may show its shell, card structure or previous lightweight snapshot immediately and fill fresh data asynchronously. This is preferable to blocking the UI thread until every store, COM reader and observation source has completed initialization.

Where a last-known snapshot is used, it must remain presentation data rather than evidence for execution. Any action that can move desktop icons or change persistent rules still requires the same fresh observation / planning / authority path as before.

Rapid refresh signals should be coalesced where appropriate rather than rebuilding the same UI or Shell state repeatedly.

## Performance validation principle

Footprint optimization should compare distinct runtime states rather than quote one memory number for the whole application.

Useful baselines include:

```text
Deep Resident footprint
Warm Resident footprint
Interactive footprint
Resident -> Interactive latency
first Layout open latency
warm Layout reopen latency
```

The optimization target is a balance between resident footprint, startup/reopen speed and first-use latency. Do not force `GC.Collect()` or Working Set trimming merely to make Task Manager numbers look smaller.

M5-07 establishes the unoptimized stability baseline first. Resident lifecycle optimization should be measured against that baseline only after leak/stability gates are complete.

## Product copy

Public-facing UI should explain what will happen, whether the desktop will be changed, and what the user needs to decide.

Internal terms such as `LayoutDraft`, `Pending Store`, `AI-only`, `RuleSet`, `Observation`, `ZoneOverride`, version numbers and storage implementation details should normally stay out of primary product copy. They can remain in Diagnostics, engineering documents and optional technical detail views.

## Brand boundary

Tessel is the product name, not a reason to rewrite stable internals.

Brand migration should therefore be incremental:

1. product identity and design language;
2. Overview page;
3. existing Layout page visual refinement;
4. Pending / Settings / AI surfaces;
5. installer and release identity during packaging work.

A full `DesktopAgent.*` namespace or source-path rename is explicitly not required by this branding decision.
