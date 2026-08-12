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

## Existing layout workbench

The current three-column layout workflow remains a good information architecture:

```text
My layouts | Desktop canvas | Properties
```

It should not be replaced simply to match the Overview page. Later refinement should make it visually consistent with Tessel through card hierarchy, spacing, typography, zone colors and a stronger desktop canvas.

The desktop canvas remains the visual center of the layout workflow.

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
