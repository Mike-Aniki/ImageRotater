# Changelog

All notable changes to ImageRotater. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Tools settings include download-page buttons for FFmpeg, yt-dlp and Deno, with FR/EN/ES labels.

- The SteamGridDB search has a Load more button that fetches the next page of results, so games
  with more than 50 grids, heroes or logos are no longer cut short. Already loaded results and your
  ticked filters stay as they are.

- Automatic native artwork injection is now the default. Rendering-mode and experimental toggles are removed from settings; per-kind rotation, transitions, media options and packs remain configurable. Old saved rendering-mode choices migrate automatically.
- Automatic logo source injection for Extra Metadata Loader controls and direct per-game logo PNG images, with native source restoration and FR/EN/ES settings.
- Native covers: adapts standard `PART_ImageCover` elements and direct `CoverImageObject` bindings without theme
  edits, respects selected-only/all-visible cover playback, restores native bindings on
  disable, and releases plugin media when tiles leave the viewport or are recycled.
- Native FadeImage background rendering: hosts ImageRotater's
  image/video renderer inside Playnite's existing background, restores native bindings
  when released, and avoids background metadata writes.
- Theme background display state: `IsBackgroundDisplayed` lets themes hide their
  native background while ImageRotater renders custom or native artwork.
- Synchronized artwork packs per game: group an existing cover, background and logo,
  preview their composition, and rotate the whole set with one shared selection and slideshow
  interval. Includes fixed packs, ordering, exclusions and original-artwork fallback for empty
  or missing slots. Open the dedicated Pack Manager from a game's ImageRotater
  context menu; its visual picker and composition preview focus on creating packs.
  Configure shared rotation and slideshow behavior in the plugin's Packs settings.
- Library > Migration page: import the covers and backgrounds you gave BackgroundChanger. Copies only;
  BackgroundChanger's own files are untouched, and running it again only adds what is new.
- The search dialog remembers how you left it, separately for covers and backgrounds: source tab,
  ticked shapes, sizes and styles, and the content toggles. Open it on the next game and your
  filters are already set.
- Aspect groups in the search dialog collapse; they start collapsed, show how many results they
  hold, and the group tick still selects every size underneath.
- A note wherever a game is left with a single image — after a download, after adding files, and
  on the Animation pages: rotation and transitions need two or more.

### Changed
- Returning to the native cover fades only the injected renderer, so themes that place both images in one wrapper keep the native cover visible underneath.
- Removed legacy startup publication, metadata rotation, native FadeImage retuning and manual-theme support probing from the active runtime. Old rendering switches can no longer select another mode.
- Native artwork discovery handles direct Image Loaded events, with weak subscriptions and a five-second safety scan for covers/logos. Disabled cover rotation does not keep a discovery timer running.

- Search result tiles show the whole image instead of cropping it to a banner. Cover searches get a
  square box, background searches keep the wide one.
- Every search filter applies as it is ticked; the Apply button is gone.
- Search dialog footer: Close sits at the far right, Download selected beside it in the theme's
  accent colour.
- Covers rotate once the selection has settled (a quarter of a second), and a departing game's
  background rotates only if the user had actually stopped on it. Scrolling quickly through the
  library no longer queues a file copy, a database write and a tile decode for every game passed;
  forty tiles scrolled produced one import where it used to produce forty.
- Switching away from a game with a video background dissolves the video's last frame into the
  next game's background, instead of cutting to the old game's still and then fading.
- Closing Playnite commits every restored game in one database write and tries each replaced
  file once, instead of one write per game and a 2.5 s retry per file still held by a tile.

### Fixed
- Fade injected covers back to native artwork when losing selection, with cancellation on quick reselection.
- Hold a bounded cached frame over video loop seeks; serialize optional poster extraction off the UI thread and release loop resources with playback.
- Leave native-only logo placements alone and restore style/binding-based visibility after suppressing a native reload.
- Add opt-in cover geometry traces for investigating theme-specific aspect ratio issues.

- Release logo adapters when their source changes to unrelated artwork, and refresh cover/logo discovery immediately when the main window becomes visible.
- Align DLL and build-script version metadata with the existing 1.4.2 manifest.

- Injected backgrounds honor the native stretch/alignment and incoming-media size.
  Themes without a standard background part can keep their existing ImageRotater host
  as a compatibility fallback. Video-open timings are logged.
- Native cover geometry follows each ready image/video even when playback remains
  active; outgoing transition layers no longer enlarge auto-width cover boxes.
- Native artwork updates are coalesced after binding propagation. Grid/Canvas covers
  retain their native template part and overlays no longer reparent it during selection.
- Native cover injection no longer reveals the original cover while loading the IR
  selection. Game-detail controls are reused for handoffs, and known covers update
  immediately when the selected game or its view model changes.
- Cover rotation and animation settings remain accessible with native injection;
  their existing options are unchanged.
- Browsing games without ImageRotater backgrounds no longer fills the plugin's
  bitmap cache with native artwork or preloads native-only neighbours. Minimizing
  Playnite clears the cache and releases hosted media; pending decodes cannot refill
  it while minimized or after restoring. Image decoding is limited to two concurrent
  operations to reduce transient memory spikes during fast browsing.
- Playnite starting one to two seconds slower with the plugin enabled. The startup pass over the
  library was reading every pair of same-length files in full to collapse duplicates - and a library
  brought over from BackgroundChanger holds a preserved original beside an identical copy for most
  games. It also republished every video a still had displaced and took a poster frame through
  ffmpeg for each. Both ran on the UI thread before the first frame, every launch. The startup pass
  no longer opens files, and the poster is only taken when the tile is still the placeholder. The
  pass now logs its duration so a slow start can be checked at a glance. Reported by Mike Aniki.
- Freezes when switching games, up to 2.6 seconds on a cover rotation. Removing the library copy a
  rotation had just replaced ran on the UI thread, and the theme's tile still had that file open -
  so Playnite retried the delete five times, half a second apart, and then failed anyway. The
  removal now runs on a worker thread. Measured in Fullscreen with Aniki ReMake: 30 game switches,
  none over 100 ms, where the same run had shown 2599, 151 and 122 ms.
- Levelling a background to the game's common width - a full decode, resize and PNG encode, 100 to
  300 ms on the UI thread - ran on every rotation and threw the result away. The levelled copy is
  now kept beside the source and reused, so each picture is levelled once; the target width per
  game is remembered too, instead of opening every candidate on every switch. Selection handling
  over 100 ms is logged with a per-phase breakdown.
- Search dialog: a group tick unticked itself after every search while its sizes stayed ticked;
  switching source tabs dropped every tick.

### Removed
- The "Work with themes built for BackgroundChanger" setting. The plugin now always answers to
  BackgroundChanger's element names; the two plugins could never run together, so the toggle only
  ever added a restart to the setup.

## [1.0.0] - 2026-09-12

First release built on Playnite 10.57.

### Added
- Transition setting for still artwork — crossfade, fade through black, fade through white, or
  cut — chosen separately for covers and backgrounds and applied identically in Desktop and
  Fullscreen.
- Steam tab in the search dialog: store artwork, trailers and store-page clips for any game
  Steam sells, no key needed.
- YouTube tab: search and download as MP4 through yt-dlp, with the video played in YouTube's own
  player for preview.
- In-dialog preview panel with a live format label on every result.
- Library page: convert all GIFs to MP4, convert all JPEGs to PNG, repair videos that render as
  black tiles, repair dangling artwork references, reset the library to the original art.
- Settings page redesigned: navigation rail, per-page strips, tool status with checkmarks.
- Background size levelling, so Playnite's blur stops jumping between backgrounds of different
  resolutions.

### Changed
- Fullscreen grid covers rotate through Playnite's own tile. The notification Playnite was missing
  shipped in 10.57 (submitted from this project); the plugin-side refresh workaround is gone.
- Playnite's background crossfade is eased so it no longer dips dark on every change.

### Fixed
- Cover transitions in Fullscreen finishing over an empty tile after the selection reload.
- Slideshow clock restarting on repeat selection events and waiting behind rendering.
- Search dialog tiles squashed by themed toggle-button styles; unreadable section headers.
- Fragmented (DASH) MP4 downloads rendering black — remuxed on download and repairable in bulk.

[1.0.0]: https://github.com/aHuddini/ImageRotater/releases/tag/v1.0.0
