# Theme integration

ImageRotater now has two rendering paths.

## Theme integration (default / recommended)

This is the fast path, following the same principle as BackgroundChanger.
ImageRotater leaves Playnite's native `Game.BackgroundImage` and
`Game.CoverImage` metadata untouched and renders directly from its own artwork
folders through plugin controls hosted by the theme. This avoids the repeated
Playnite database updates that can wake other metadata plugins on every
rotation or slideshow tick.

A compatible theme hosts:

```xml
<ContentControl x:Name="ImageRotater_Background"/>
<ContentControl x:Name="ImageRotater_Cover"/>
```

The controls render still images, GIFs and video themselves. Backgrounds fall
back to the game's native Playnite background when the game has no
ImageRotater background. Cover controls remain transparent when the game has no
ImageRotater cover, so the theme's native cover can stay underneath.

## Compatibility mode

Turn **Theme integration** off in **Settings > General** when the active theme
does not support ImageRotater. In this mode the plugin keeps the historical
behaviour: the selected still/poster is temporarily written through Playnite's
native artwork fields, so ordinary themes work without any special markup.

This is the broadest-compatibility path, but each artwork change can trigger a
Playnite game/database update. Other extensions that react to those updates may
perform additional metadata or image work, so Slideshow and frequent rotation
can cost more on installations with such plugins. ImageRotater restores the
user's original Playnite artwork when it no longer needs the compatibility
write path.

Theme integration is enabled by default.

---

**What a theme author has to do, in full:**

1. Add `<ContentControl x:Name="ImageRotater_Cover" .../>` as a sibling
   *after* your own cover `Image`, wherever a cover is drawn that should be
   able to animate. Leave your `Image` exactly as it is: the control is
   transparent when it has nothing to draw, and draws the pick over it when
   it does - stills included, with its own crossfade or flash.
2. Add `<ContentControl x:Name="ImageRotater_Background" .../>` over your
   background element, the same way.
3. Nothing else. No `MediaElement`, no path bindings, no triggers, no
   converters, no plugin-settings conditions. Video and GIF play through the
   hosted control; stills keep coming through Playnite.

BackgroundChanger and ImageRotater use separate theme controls. A theme may
support both plugins at the same time, but ImageRotater requires its own
`ImageRotater_Background` / `ImageRotater_Cover` elements and never claims
BackgroundChanger's element names.

See [Animated covers](#animated-covers-place-the-plugins-control) for the
markup and [Worked example: Aniki ReMake](#worked-example-aniki-remake) for a
complete integration that is known to work.

**Do not build a renderer in the theme.** It can be made to work - that has been
demonstrated - but it duplicates one the plugin already has, and a second media
pipeline per tile is what crashed Playnite. The details are recorded under
[What theme authors should NOT do](#what-theme-authors-should-not-do) so nobody
repeats them.

---

## The Fullscreen grid cover problem (fixed in Playnite 10.57)

**Status: resolved upstream. ImageRotater 1.0.0 requires Playnite 10.57 or
newer for Fullscreen cover rotation. Theme authors need to do nothing.**

Kept here because it explains why older plugin versions carried a workaround,
and why this one does not.

Playnite's `GamesCollectionViewEntry` raises `PropertyChanged` for the Desktop
cover properties when `Game.CoverImage` changes. Up to 10.56 it did not raise
it for `FullscreenListItemCoverObject` - the property the Fullscreen grid's
`PART_ImageCover` binds (`Playnite.FullscreenApp/Controls/GameListItem.cs`) -
so a Fullscreen tile showed whatever cover was current when it was built and
never updated. Switching grid modes rebuilt the tiles and they picked up new
covers, which looked like partial success but was just reconstruction.

Earlier versions of this plugin worked around it by finding the game's tile in
the grid and calling `UpdateTarget()` on its cover binding by hand - the
re-read the missing notification should have caused. It worked, but was
unreliable during transitions and had to be suppressed to avoid decoding the
same image twice.

The actual fix was one line, submitted as a PR and merged as upstream commit
`c48f3562` ("Cover image data not reloading properly in Fullscreen mode"),
shipped in Playnite 10.57:

```csharp
// source/Playnite/GamesCollectionViewEntry.cs
if (propertyName == nameof(Game.CoverImage))
{
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImageObject)));
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImageObjectCached)));
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GridViewCoverObjectCached)));
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullscreenListItemCoverObject)));  // added
}
```

With that in place a Fullscreen tile updates from the database write alone,
exactly like Desktop, and the workaround is gone. The original write-up with
the reproduction is at [PLAYNITE_ISSUE_DRAFT.md](PLAYNITE_ISSUE_DRAFT.md).

## Animated covers: place the plugin's control

**One line. Nothing else.**

```xml
<ContentControl x:Name="ImageRotater_Cover"
                HorizontalAlignment="Stretch"
                VerticalAlignment="Stretch"/>
```

Put that in the game tile template and the plugin renders the cover itself —
stills, animated GIFs, and MP4/WebM video, whichever the rotation picked. No
`MediaElement`, no path bindings, no triggers, no converters.

This works because the control's lifecycle inside a Fullscreen tile is healthy,
which was worth confirming rather than assuming. Instrumented across 67
instances while scrolling a live grid: every one was constructed, received
`GameContextChanged` with the correct game, fired `Loaded`, ran its refresh,
picked artwork and rendered it at full tile size. The earlier belief that these
controls were built and never loaded came from a faulty measurement.

The control already contains every renderer needed — an `Image` for stills,
XamlAnimatedGif for GIFs, and a `MediaElement` for video — and switches between
them per pick, keeping exactly one active so two cannot fight over the same
tile.

**Hide your own cover element while the plugin has one**, or the two stack:

```xml
<Condition Binding="{PluginSettings Plugin=ImageRotater, Path=EnableCoverImage}" Value="True"/>
<Condition Binding="{PluginSettings Plugin=ImageRotater, Path=HasDataCover}" Value="True"/>
```

`HasDataCover` describes only the SELECTED game, so it cannot gate a whole grid
— see the warning under [Settings themes can bind](#settings-themes-can-bind).
The plugin's control renders nothing for a game with no artwork, so in a grid
the simplest correct answer is to leave your own element alone and let the
control draw over it only where it has something.

## Worked example: Aniki ReMake

Two edits, both pure additions — nothing in the theme is modified or removed.
This is the exact integration used to test the plugin, so the markup below is
known to work rather than inferred.

### 1. Animated covers — `DerivedStyles/ListGameItemTemplate.xaml`

Find `PART_ImageCover` inside the `ListGameItemTemplate` style (the Fullscreen
grid tile, around line 387) and add the control as a **sibling after it**, so it
draws on top:

```xml
<Image x:Name="PART_ImageCover" ... />

<!-- ImageRotater renders the cover: stills, animated GIF, MP4/WebM. -->
<ContentControl x:Name="ImageRotater_Cover"
                HorizontalAlignment="Stretch"
                VerticalAlignment="Stretch"
                HorizontalContentAlignment="Stretch"
                VerticalContentAlignment="Stretch"
                Focusable="False"
                IsTabStop="False"/>
```

`Focusable="False"` and `IsTabStop="False"` keep it out of controller
navigation — the tile itself is the focus target, not the artwork inside it.

No gating is needed. The control draws nothing for a game the plugin has no
artwork for, so `PART_ImageCover` shows through untouched underneath.

### 2. Animated backgrounds — `Views/Main.xaml`

Find `PART_ImageBackground` in the wallpaper grid (around line 9691) and add the
control **before it**, inside the same `Grid` so it inherits Aniki's zoom and
translate transforms:

```xml
<ContentControl x:Name="ImageRotater_Background"
                Panel.ZIndex="1"
                HorizontalAlignment="Stretch"
                VerticalAlignment="Stretch"
                HorizontalContentAlignment="Stretch"
                VerticalContentAlignment="Stretch"
                Focusable="False"
                IsTabStop="False">
    <ContentControl.Style>
        <Style TargetType="ContentControl">
            <Setter Property="Visibility" Value="Visible"/>
            <Style.Triggers>
                <!-- Defer to the theme's own switches, exactly as
                     PART_ImageBackground does, so turning the wallpaper off
                     does not leave this drawing over a deliberately bare
                     screen. -->
                <DataTrigger Binding="{Settings Fullscreen.EnableMainBackgroundImage}" Value="False">
                    <Setter Property="Visibility" Value="Collapsed"/>
                </DataTrigger>
                <DataTrigger Binding="{Binding Items.Count, ElementName=PART_ListGameItems}" Value="0">
                    <Setter Property="Visibility" Value="Collapsed"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </ContentControl.Style>
</ContentControl>

<FadeImage x:Name="PART_ImageBackground" ... />
```

`Panel.ZIndex="1"` puts it above the theme's own wallpaper. The two
`DataTrigger`s are optional but recommended: they make the plugin's background
respect the same switches Aniki already honours for its own.

### What you should see

Only the **selected** tile animates. Every other tile showing animated artwork
renders its still frame instead — a screenful of simultaneous decoders is what
takes a 32-bit process down, and one moving cover reads better than twenty.

### Aniki's BackgroundChanger blocks

Aniki already hosts `BackgroundChanger_PluginBackgroundImage` and
`BackgroundChanger_PluginCoverImage`. Leave them alone. They are gated on
`{PluginStatus Plugin=playnite-backgroundchanger-plugin}`, which this plugin
cannot satisfy, so they stay inert — and one of them carries a
`ContentControl.Resources` Image style that would throw if it ever resolved
against a real plugin control. See the `{Settings}`-in-`Setter.Value` trap
below.

## What theme authors should NOT do

Everything in this section was built and run against a live Fullscreen grid.
None of it is hypothetical, and none of it is recommended.

**Do not add your own `MediaElement` to a tile template.** It animates — that
much is real, and video plays smoothly. But it is solving a problem the plugin
already solves, and it brings its own:

- **It crashes Playnite when it duplicates the plugin's.** Each realised tile
  opens its own media file, and the plugin's control already has a
  `MediaElement` of its own. Two media pipelines per tile, in a 32-bit process
  sharing its address space with Chromium, runs out well before a library
  finishes scrolling. This is what actually took Playnite down - not the
  presence of a renderer, but two of them.
- **It hides the rotation it was meant to show.** A `MediaElement` drawn over
  `PART_ImageCover` covers it for as long as it has a source, so the still
  picks rotate underneath, invisibly. Gating it on "is the current pick a
  video" needs a published boolean, because a WPF `DataTrigger` compares a
  binding to a *literal* and "does this path end in .mp4" cannot be written in
  XAML.
- **It costs a file copy per rotation.** A theme binds a PATH, so the plugin has
  to publish the pick as a file - and to serve both a `MediaElement` and an
  image element, twice, under two names. On a 4.7 MB GIF, on the selection
  path, that is visible as stutter. A hosted control reads the store directly
  and needs neither copy.

- **GIFs still will not animate.** A `MediaElement` picks its decoder by file
  extension, so it needs `current.gif` rather than the extensionless published
  name - which is why the double publish existed in the first place. The
  plugin's control uses XamlAnimatedGif and has no such constraint.

**Do not reuse the BackgroundChanger cover pattern for animation.** Aniki hosts
`BackgroundChanger_PluginCoverImage` as a 1×1 collapsed control and pulls
`Content.Source` out of it into its own `FadeImage`. That is a good pattern and
it works — for *stills*. It yields a single `BitmapSource`, so it strips the
animation: GIF playback lives in an attached property on the control's own
`Image`, not in `Source`. Copying it gets clean cover rotation and specifically
not animated covers.

**Do not put a `Binding` inside a template trigger's `Storyboard`.** Template
triggers must be freezable and a binding is not, so the theme fails to load
with *"Cannot freeze this Storyboard timeline tree"* — which in Fullscreen is a
black window. A `MediaTimeline` with `RepeatBehavior="Forever"` carries no
binding and does seal.

**Do not write `<Trigger Property="IsSelected">` in a `ControlTemplate` whose
`TargetType` is `GameListItem`.** The property resolves against the target
type, and `GameListItem` has no `IsSelected` — the theme dies with *"Property
can not be null on Trigger"*. Bind the containing `ListBoxItem` instead:

```xml
<DataTrigger Binding="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=IsSelected}"
             Value="True">
```

**What to do instead:** place `ImageRotater_Cover` and stop there. Still-cover
rotation already works with no theme support at all; animation needs only that
one element, because the renderer lives in the plugin where a single instance
per tile is guaranteed.

Every problem in this section came from putting the renderer in the THEME. Once
it lives in the plugin, the crash (two media pipelines per tile), the stutter
(a published file copy per rotation) and the dead GIFs (a decoder chosen by file
extension) all stop being problems rather than being worked around.

### Why theme-side workarounds do not exist

Each of these was built and tested against a live Fullscreen grid. They are
recorded so nobody spends the evening rediscovering them.

**A plugin control per tile.** Registering `ImageRotater_Cover` via
`AddCustomElementSupport` and placing it in the tile template resolves cleanly
and does not crash, provided the control binds its image path with
`IsAsync=True`. An earlier version decoded synchronously in code-behind and
reliably killed Fullscreen: dozens of tiles decoding inside the layout pass, in
a 32-bit process.

**It works.** An earlier version of this document claimed the opposite, on the
strength of a measurement that turned out to be wrong: it reported 49 element
requests and zero refreshes, and concluded the controls were built and never
loaded.

Re-instrumented properly, across 67 instances while scrolling a live grid,
every one of them: constructed, received `GameContextChanged` with the correct
game, fired `Loaded` with that context already set, ran its refresh, picked
artwork, and rendered at full tile size (`234x234, visible=True`). The early
`0x0, visible=False` entries are the pre-layout pass, not a failure.

So a plugin control in a Fullscreen tile is a working integration point, and it
is the right one for animated covers - see
[Animated covers](#animated-covers-place-the-plugins-control).

**A theme-side converter.** WPF triggers compare a binding to a *literal*, and a
tile's own game id is a binding, so a tile cannot ask "is this published cover
mine?". Shipping an `IMultiValueConverter` in the plugin does not help either:
themes cannot reference a plugin assembly. A `clr-namespace` pointing at
`ImageRotater` makes the whole resource dictionary fail to load, and Playnite
falls back to the default theme.

**A file the theme binds by path.** The plugin publishes each game's current
artwork to a fixed path, but neither XAML binding form works:

| Binding form | Re-reads when the file changes | Holds the file open |
| --- | --- | --- |
| `Source="{Binding path}"` | only if the *path* changes | yes, for the bitmap's lifetime |
| Inline `BitmapImage` + `CacheOption=OnLoad` | never — frozen at `EndInit()` | no |

`CreateOptions=IgnoreImageCache` does not change this. It stops WPF serving a
previously cached bitmap *at load time*; it does not make a binding watch a file.

A generation counter in the filename (`current.1.tile`, `current.2.tile`, …) does
force the re-read, at the cost of one locked file per rotation until Playnite
restarts. Viable, but unbuilt, and it only papers over the missing notification.

**One trap worth naming.** Playnite's `{Settings}` markup extension is illegal in
a `Setter.Value`. A theme can carry such a setter indefinitely without symptom,
because a `Style` is only sealed when it is actually applied — so a
`ContentControl.Resources` block for a plugin that is not installed never throws.
Install the plugin, the element resolves, the style seals, and Fullscreen crashes
with `'Settings' is not valid for Setter.Value`. Style the plugin's own control
instead.

---

## Settings themes can bind

These work today. The plugin registers `AddSettingsSupport` with
`SourceName = "ImageRotater"`, so themes can read:

| Path | Meaning |
| --- | --- |
| `EnableCoverImage` | cover rotation is on *and* the theme-rendered path is enabled |
| `HasDataCover` | the **currently selected** game has plugin covers |
| `CurrentCoverPath` | full path to the cover the last rotation chose |
| `CurrentCoverGameId` | the game that path belongs to |
| `ImagesRoot` | root of the plugin's per-game image folders |

```xml
<Condition Binding="{PluginSettings Plugin=ImageRotater, Path=HasDataCover}" Value="True"/>
```

`PluginStatus` matches the **installed folder name**, which for this plugin uses a
dot rather than an underscore:

```xml
<Condition Binding="{PluginStatus Plugin=ImageRotater.72b7d457-0621-429b-8368-665bc53ff896, Status=Installed}" Value="True"/>
```

`HasDataCover`, `CurrentCoverPath` and `CurrentCoverGameId` describe only the
selected game. They cannot drive a grid on their own — every tile would show the
same cover.

## Per-game published artwork

Every game has a published file at:

```
{ImagesRoot}\{game id}\covers\current.tile
{ImagesRoot}\{game id}\backgrounds\current.tile
```

It is written on every rotation, and seeded at startup for the whole library so
the path always resolves — games with no plugin artwork get a 70-byte transparent
placeholder rather than a copy of their artwork. `.tile` is deliberately not a
recognised image extension, so the published copy is never offered back as a
rotation candidate; WPF decodes by content, not by name.

This exists for themes that want to draw artwork themselves. It does not solve
the Fullscreen grid issue above, for the binding reasons already listed.

## Elements

```xml
<ContentControl x:Name="ImageRotater_Background"/>
<ContentControl x:Name="ImageRotater_Cover"/>
```

In Theme integration mode these controls are required. Without them, use
Compatibility mode for static artwork on themes that do not integrate
ImageRotater.

## Themes that also support BackgroundChanger

BackgroundChanger and ImageRotater are intentionally independent. ImageRotater
only registers its own elements:

```xml
<ContentControl x:Name="ImageRotater_Background"/>
<ContentControl x:Name="ImageRotater_Cover"/>
```

It does **not** register or answer to
`BackgroundChanger_PluginBackgroundImage` or
`BackgroundChanger_PluginCoverImage`. A theme can therefore keep its existing
BackgroundChanger controls and add ImageRotater controls alongside them without
either plugin claiming the other's element names.

Theme integration works only where the ImageRotater controls are actually
present. If a theme has only BackgroundChanger integration, enable ImageRotater's
Compatibility mode instead or add the ImageRotater controls to that theme.
