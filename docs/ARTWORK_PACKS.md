# Synchronized artwork packs

Right-click a game and choose **ImageRotater > Pack manager**. This opens a
separate window dedicated to that game's packs.

1. Choose **New pack** and give it a name.
2. Select the cover, background or logo card to open the visual artwork picker.
   Choose an existing illustration or use **Add files** to import local artwork.
   The Original tile keeps the game's own artwork for that slot.
3. Inspect the composition preview. Animated artwork uses a still frame; video
   previews require ffmpeg. Logos use the existing supported static formats.
4. Add more packs if desired. Move them up/down to set their order, clear
   **Include this pack** to keep one out of rotation, or mark a pack for Fixed mode.
5. Choose **Save packs**. Draft edits do not affect rotation. Closing the window
   with unsaved changes offers to save, discard, or continue editing.

Configure rotation in **ImageRotater options > Packs**, separately from creation.
The settings apply to every game with packs. The Display page links to the
existing cover, background and logo settings, which packs share.

Packs reference existing files by name inside the game's artwork folders. They
do not duplicate artwork already in the library. **Add files** imports a copy
from your computer into the game’s artwork library. The same artwork can belong
to several packs. Removing a pack
does not delete its artwork.

## Rotation

With synchronized packs enabled in the plugin options, a pack replaces independent cover/background/logo rotation for that
game, even when an individual kind's global rotation switch is off. Other games
keep their existing settings. The plugin's master rotation switch still applies.

- **Session:** keep the same pack during the Playnite session.
- **Every selection:** choose a pack on navigation back to the game. Duplicate
  notifications for an already selected game do not advance it.
- **Daily:** keep the day's pack, including across clean Playnite restarts.
- **Fixed:** use the pack marked for Fixed mode in the game’s pack manager.
  If no pack is marked, the first included pack is used.
- **Slideshow:** advance the entire pack using one interval. Existing pauses
  while Playnite is unfocused or a game is running also apply.

Random, sequential and shuffle order are available. The up/down buttons set
sequential order. Excluded packs stay in the editor but are skipped by rotation.
A game needs at least one included pack to use pack rotation. One included pack
stays still without a slideshow timer. Games without included packs keep their
independent artwork behavior.

## Originals, missing files and themes

An empty slot uses the game's original artwork. A missing referenced file also
falls back to the original, and the editor displays a warning. If no original
exists, the plugin has no artwork to render for that slot.

The delete-artwork confirmation lists packs that reference the selected files.
Deleting the files does not delete those packs; select replacement files in the
editor or leave their slots on Original.

Packs use the same automatic artwork injection as independent rotation. Supported
native background and cover elements can display images and videos without adding
IR-specific theme controls. Logos use supported Extra Metadata Loader controls or
direct per-game logo bindings. See the [Theme Developer Guide](THEME_INTEGRATION.md)
for supported structures and custom-theme limitations. ffmpeg is used for preview
stills; native injection plays the supported video itself.

Selection is shared across the three slots. Decoding and the existing
transitions still run in their respective controls; multiple video players are
not synchronized frame by frame.

Disable synchronized packs in the plugin options and save to return to
independent rotation. To stop using packs for just one game, remove or exclude
its packs and save.
Pack definitions are saved with ImageRotater's plugin settings. A successful
full library reset also clears pack definitions.
