> Historical design document. Its rendering/integration instructions are superseded by the [current Theme Developer Guide](../../THEME_INTEGRATION.md).

# ImageRotater v0.1 — Background Rendering Engine

**Date:** 2026-07-30
**Status:** Approved, pending implementation plan
**Prior study:** [BACKGROUNDCHANGER_STUDY.md](../../dev_docs/BACKGROUNDCHANGER_STUDY.md)

## Purpose

Render per-game background images in Playnite, sized correctly for the display and cheap
enough to feel instant while browsing a Fullscreen theme.

This is a **clean-room** implementation. BackgroundChanger (MIT, Lacro59) was studied for
behavior and performance characteristics; no code is copied from it.

---

## Scope

The problem space contains four separable subsystems. v0.1 builds exactly one.

| Subsystem | Version |
|---|---|
| **Rendering** — resolve, decode, cache, display | **v0.1 (this spec)** |
| Timed rotation | v0.2 — accommodated, not built |
| Plugin-owned per-game image folders | v0.3 |
| Image acquisition (SteamGridDB, Google) + management UI | later |

v0.1 reads each game's existing `BackgroundImage` from Playnite's database, so the plugin
is useful immediately on install with no setup and no empty folders to populate.

### Explicitly out of scope for v0.1

Timed rotation, plugin-owned image folders, image downloading, management UI, cover
images, video backgrounds, crossfade animation.

---

## Behavior

**Trigger:** background changes when the selected game changes. There is no timer.

**Selection:** when a game has multiple images, one is chosen at random per selection, and
the choice avoids repeating that game's immediately-previous pick. Revisiting a game
therefore tends to show variety — this is what makes the plugin a "rotater" without a
timer.

**Consequence worth stating:** with no timer, the entire user-visible behavior is *how the
image is picked*. That logic is isolated in a pure, unit-tested component rather than
buried in the control.

**Not shipping in v0.1: crossfade.** BackgroundChanger's two-layer opacity crossfade is
well-built and cheap (opacity is GPU-composited). But with selection-only triggering,
changes happen at browsing speed, and a 0.5s fade may read as sluggish while arrowing
through a library. v0.1 swaps instantly; crossfade becomes an option once the instant
version has been felt in a real theme.

---

## Architecture

### The one seam

Everything added later plugs in here:

```csharp
public interface IBackgroundImageSource
{
    // All candidate image paths for this game. May be empty.
    IReadOnlyList<string> GetImagePaths(Game game);
}
```

v0.1 ships a single implementation reading Playnite's `BackgroundImage`. v0.3's folder
source and later acquisition sources become additional implementations behind this
interface, composed by a merging source — **with no changes to rendering**.

This is the only speculative abstraction built now. One interface, one method. Everything
else stays concrete until a second real case exists.

### Components

| Unit | Responsibility | Depends on |
|---|---|---|
| `IBackgroundImageSource` | "Which images does game X have?" | — |
| `PlayniteImageSource` | Reads `game.BackgroundImage` | Playnite API |
| `ImagePicker` | Chooses one path from a list. **Pure.** | — |
| `ImageCache` | `(path, widthBucket)` → frozen `BitmapSource`; byte-bounded LRU | — |
| `ImageLoader` | Decodes off-thread at a bucket width | `ImageCache` |
| `BackgroundImageControl` | WPF control: measure, request, display | all above |

Each unit is understandable and testable without reading the others' internals.

---

## The decode path

The central finding of the study: WPF's built-in imaging already decodes off-thread
correctly. What BackgroundChanger omits is the *size*.

```csharp
// executed off the UI thread
bitmap.BeginInit();
bitmap.DecodePixelWidth = bucket;                     // the omission being fixed
bitmap.CacheOption      = BitmapCacheOption.OnLoad;
bitmap.CreateOptions    = BitmapCreateOptions.IgnoreColorProfile;
bitmap.StreamSource     = stream;
bitmap.EndInit();
bitmap.Freeze();
```

All five properties are required:

- `DecodePixelWidth` — WIC's JPEG decoder does true DCT-domain scaled decoding (1/2, 1/4,
  1/8); PNG streams without materializing a full-size intermediate. This is decode-time
  downscaling, not decode-then-resize.
- `OnLoad` — releases the file stream promptly
- `IgnoreColorProfile` — skips colour-profile work
- `Freeze()` — makes the bitmap cross-thread-safe and cheaper for WPF to render
- Off-thread — keeps decode off the UI thread

**No imaging dependency is added.** SkiaSharp, Magick.NET, ImageSharp, GDI+ and direct WIC
interop were all evaluated (see study). The built-in path is already near-optimal for
JPEG/PNG; GDI+ would be actively worse, having no decode-time downscale at all.

### Width buckets

Render width depends on the theme's layout and the user's settings — the same control can
be a full-bleed 3840px background in one view and a 400px card backdrop in another, within
one theme. There is no single correct width.

`DecodePixelWidth` is applied before `EndInit()`, and a frozen `BitmapSource` cannot be
resized afterward, so changing size means decoding again. The design therefore optimizes
for *avoiding re-decodes* rather than for exact measurement.

**Measure the control's actual render width, round up to `480 / 960 / 1920 / 3840`.**

- Adapts to any theme automatically, which a fixed setting cannot
- Ordinary resizes stay within a bucket, so they are cache hits rather than re-decodes
- Bounds cache-key growth to four values per image instead of one per distinct pixel width
- Small panels get small bitmaps; full-bleed 4K backgrounds keep full detail

### Cache

Keyed `(absolute path, width bucket)`. Bounded by **total decoded bytes**, not item count,
evicting least-recently-used. The budget is fixed at 150 MB for v0.1 — a cache-size knob is
one almost no user can reason about, and the cache itself may not earn its place (see the
acknowledged risk below). It may become adjustable in a later version.

A count-based bound is not a bound: twenty thumbnails and twenty 4K images differ ~30x in
memory while reporting the same "limit". Decoded size is known at decode time, so a byte
budget holds regardless of library composition.

**Why this matters more than ordinary RAM politeness:** Playnite is a 32-bit process
(verified from the PE header of `Playnite.DesktopApp.exe`), sharing ~2 GB of address space
with CefSharp/Chromium. Decoded bitmaps are large *contiguous* allocations — a 4K frame
needs ~32 MB of unbroken address space. Long-running 32-bit WPF processes fragment their
heap, so `OutOfMemoryException` can occur with hundreds of MB nominally free. The byte
budget is a fragmentation control, not just a memory cap.

**Acknowledged risk:** the cache is the component most likely to be unnecessary in v0.1.
Without a timer, re-decodes occur only at human navigation speed. It is kept because
arrowing quickly through a library is genuinely rapid-fire and back-and-forth navigation
re-hits the same images. If it proves to add no measurable benefit, it is the first thing
to remove.

---

## Lifecycle

The study found BackgroundChanger's controls subscribe to application-lifetime events in
`Loaded` (and the constructor) with no matching unsubscribe, and that its base class does
no teardown whatsoever — 364 lines containing one `+=`, zero `-=`, and no `Unloaded` or
`Dispose` member. Handlers therefore accumulate as themes rebuild visual trees, pinning
dead control instances and their decoded images.

ImageRotater does not inherit that base class. The rule is structural:

- **Subscribe in `Loaded`, unsubscribe in `Unloaded`. Symmetric, no exceptions.**
- Nothing subscribes in a constructor.
- The control releases its bitmap references on `Unloaded`. The cache owns bitmap
  lifetime; the control only borrows.

---

## Error handling

Two distinct empty states, deliberately treated differently:

| State | Treatment |
|---|---|
| Game has **no** background image | Render nothing. The theme's own background shows through. Not logged. |
| Path exists but file is **missing or corrupt** | Show the placeholder. Log once per path. |

Conflating these would be a UX bug: a placeholder on every game lacking an image would
cover the theme's background across a large fraction of a typical library. The placeholder
means "something is wrong", never "nothing here".

**Placeholder rendering:** drawn in vector XAML, not shipped as a bitmap. It must hold up
from 480px to 3840px wide, so a vector scales cleanly where a PNG would need several
resolutions and still look soft at some bucket. Subdued neutral tones, so it reads as a
missing-image indicator rather than as decoration competing with the theme.

**Decode failures** are caught and logged once per path, and the control clears its image and
shows the placeholder. Hiding the failure behind the previously shown image would be quieter
but misleading; the placeholder is the one visual whose job is to signal that something is
wrong.

---

## Testing

| Unit | Approach |
|---|---|
| `ImagePicker` | Real unit tests. Pure function — verify a single-image list returns that image, that the previous pick is avoided, and that a 2-image list alternates rather than spinning. |
| `ImageCache` | Real unit tests. Eviction at the byte budget, LRU ordering, key distinguishes buckets for the same path. |
| `ImageLoader` | Tests against small fixture images: decoded width matches the requested bucket, result is frozen, a corrupt file surfaces as a handled failure rather than an exception escaping. |
| `PlayniteImageSource` | Thin; tested via a stubbed game object. |
| `BackgroundImageControl` | **Manual.** Needs a live WPF visual tree and a running Playnite. Verified in a real Fullscreen theme. |

The control's untestability is a real boundary, stated rather than worked around. The logic
worth testing has been deliberately moved out of it into `ImagePicker` and `ImageCache`.

---

## Success criteria

1. A 4K background on a 1080p display decodes to 1920 wide, not 3840 — verifiable by
   inspecting `PixelWidth` on the produced bitmap.
2. Memory does not grow unbounded while browsing a large library.
3. No handler accumulation: navigating repeatedly between views does not increase the
   subscriber count on application-level events.
4. A game with no background image renders nothing and logs nothing.
5. A game whose image file has been deleted shows the placeholder and logs once.
6. Browsing quickly through a library does not visibly stutter.

---

## Open items deliberately deferred

- **Crossfade** — revisit after feeling the instant swap in a real theme.
- **Cache necessity** — measure before assuming it earns its place.
- **Decode-width re-pick on resize** — v0.1 measures at layout and on bucket change only.
  Continuous re-decode while dragging a window edge is explicitly not handled, and is a
  Desktop-mode concern rather than a Fullscreen one.
