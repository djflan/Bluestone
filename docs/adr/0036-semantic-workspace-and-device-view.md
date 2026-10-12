# 0036. A semantic workspace, Device Views as view instances, and local layout state

- Status: Accepted
- Date: 2026-10-11

## Context

Bluestone is becoming a device-oriented DAW in the manner of Bitwig: a track's device chain is a central place to
work, not a list in an inspector. The device chain was edited in an inspector section (`DeviceStripViewModel`):
one instance, bound to the selected track, with an *Add a device…* combo box and up/down buttons. Window layout was
not saved at all, and colours, sizes, and margins were written inline across the views.

The owner asked for a device-centric slice: a dark design system, a workspace with regions, a dockable Browser, a
first-class Device View that can follow or be locked, several Device Views at once, and local persistence of all of
it, without a general docking framework.

## Decision

### Semantic regions, not free docking

The main window has fixed regions with strong default placement:

- **Global chrome**: transport and status (unchanged).
- **Primary**: the arranger.
- **Detail**: below the arranger, a tab strip of Device Views beside the existing piano roll, event list, MIDI
  monitor, and messages. Height is resized by its top edge.
- **Left and right side regions**: the inspector (left) and the Browser (right), one docked tool per side,
  resized by their inner edge.
- **Border rails**: thin strips at the far left and right holding an icon for each side tool and for any view
  that is not docked. A docked tool's icon is marked open; clicking it collapses the tool; clicking a collapsed
  icon docks it again, pushing and resizing the arranger. A tool stays open until it is collapsed or closed.

Each view (`WorkspaceView`) has a `Kind`, a home `Region`, and a `Placement`: **Docked**, **Collapsed** (an icon on
its region's rail), or **Detached** (its own window). Views share this one hosting model and are moved only between
their home region, the rail, and a window. There is no drag-to-dock, no splitting, and no tabbing a view into
another region. `WorkspaceViewModel` owns the views, the rails, the detail tabs, and the region sizes; the desktop
layer only reflects it (`MainWindow.AttachWorkspace`, `ViewWindow`).

No docking dependency was added. The regions are a handful of grid columns and rows, and the behaviour needed (one
tool per side, tabs in the detail region, detach to a window) is small and fully testable in the view model.

### Device View instances and model ownership

A Device View is a workspace view, not part of the track UI. `DeviceViewViewModel` holds only **presentation**:

- which chain it shows: unlocked (follows the selected track) or locked to a track or a rack by stable ID;
- which devices are collapsed, which is selected, and the horizontal scroll offset.

The chain, its devices, their parameters, bypass, and plugin state belong to the **project** and are changed only by
project commands. Each view builds lightweight panel view models from the project on every sync; device instances
are never duplicated because two views show them, so a bypass or parameter change in one view shows in the other,
while collapsing or selecting a device in one view leaves the other alone. Removing a locked view's track or rack
shows the view as unavailable rather than silently retargeting it.

Several Device Views may exist; the first is opened by default and **+** opens more. Each can be docked as a detail
tab, collapsed to the rail, or detached. A detached view gets a new control bound to the same view model.
Creating a rack locks the active Device View to it (**Back to the selected track** unlocks it).

### The Browser is a palette; the Device View is the canvas

`BrowserViewModel` lists device definitions (built-in and scanned plugins), project racks, and hardware profiles, with
search and category filters. Recommendations are hints for the target chain (an instrument for a chain with none,
then effects); nothing is hidden or refused for being unusual. Insertion is by drag and drop onto a Device View,
which decides the chain and the exact gap (`DeviceViewViewModel.GapAt`, shown by an insertion marker). Double-click
or Return inserts into the **most recently active** Device View (`WorkspaceViewModel.InsertionTarget`), after its
selected device, if that view is open and shows a chain; otherwise nothing is inserted and a message says why.
Bluestone never guesses a destination.

### Design tokens

`Theme/Tokens.axaml` defines the one dark theme as semantic tokens: surfaces, lines, text levels, states (hover,
pressed, selected, focus, warning, error, offline), signal accents (note, instrument, audio, other), typography,
spacing, radii, control heights, device-panel dimensions (fixed height; collapsed, small, medium, and large widths),
icon sizes, and motion timings. Views and styles use tokens, not raw values. `Palette` mirrors the colours that
custom-drawn controls need, and a UI test keeps the two in step. Small Bluestone controls (`Knob`, `ResizeHandle`)
are custom where the stock control would look generic; ordinary layout and text stay stock.

### Local layout state

View state is separate from the project and never marks it edited. `WorkspaceState` is a small versioned record,
serialized as JSON by `WorkspaceStore` in the user's settings folder:

- **User defaults** (`workspace-defaults.json`): the last layout, with project bindings removed (`AsDefaults`),
  used for a project opened here for the first time.
- **Per project** (`workspaces/<hash of the project path>.json`): the full state, including Device View locks,
  collapsed devices, selection, and scroll offsets, all by stable ID.

Window bounds are stored per display configuration (`DisplayConfiguration.Signature`, the set of attached display
areas). On restore, `LayoutRecovery` uses the bounds saved for the current configuration, otherwise any saved bounds
still reachable on an attached display, fitted to it. A window is never placed off-screen. A detached view whose
display is missing is shown as a rail icon (`IsAwaitingDisplay`) but is still saved as detached with its original
bounds, so working on one display for a while does not destroy the multi-monitor layout; when the displays change and
its saved window lands on one again, it is detached again. Positions, sizes, and display areas are all kept in the
platform's desktop units (physical pixels on Windows and Linux, points on macOS), so they compare and fit in one
coordinate system; version 1 files, which kept sizes in device-independent pixels, are still read and converted when
applied. An unknown, unreadable, or malformed state file (a missing or null view, ID, bounds, or Browser state) is
ignored.

## Consequences

- The inspector no longer edits devices; its Devices section and `DeviceStripViewModel` were removed.
- New views join by adding a `WorkspaceViewKind` with a home region; they get docking, rails, detaching, and
  persistence from the same model.
- Region placement is deliberately restricted. A request to dock a view somewhere unusual is a new decision.
- Device panels are presentation over public definition data (parameter descriptors, categories, signal kinds), so a
  third-party device gets the same compact panel as a built-in one. Declarative third-party layouts, custom Avalonia
  editors, child-chain slots, and modulation can be added to the panel model without changing ownership.
- Detached windows depend on the platform honouring requested positions; recovery logic is tested, but behaviour
  with real monitors being attached and removed has not been verified by hand.
