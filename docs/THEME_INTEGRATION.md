# Theme developer guide

ImageRotater renders inside the artwork elements already present in a Playnite
theme. Static images, animated backgrounds and covers, and logo rotation use this
automatic injection path in Desktop and Fullscreen. Users do not need to select
a rendering mode or enable theme integration.

A theme using the elements below does not need dedicated ImageRotater controls.
Custom image names alone are not enough to identify artwork.

## Backgrounds

Use Playnite's standard background FadeImage, registered as
`PART_ImageBackground`, with your usual native background binding:

```xml
<FadeImage x:Name="PART_ImageBackground"
           Source="{Binding SelectedGame.DisplayBackgroundImageObject}"
           Stretch="UniformToFill" />
```

This example uses a Fullscreen selected-game context. Keep the source binding
appropriate to your view; the registered part name is what identifies it.

IR mounts its image/video renderer in FadeImage's `ImageHolder`, suppresses the
native image layers, and uses its own transitions. Keep the normal FadeImage
structure, including `ImageHolder`, `Image1` and `Image2`. FadeImage may call itself
`ControlRoot` internally; registration in the outer template's name scope still
identifies `PART_ImageBackground` correctly.

A plain `Image` registered as `PART_ImageBackground` is also supported when its
parent is a `Grid`. Put its native artwork binding on that image as usual.

Place the part where the background should appear. If several standard background
parts exist, IR prefers a game-overview part over the library-wide background.
An unrelated image called `TopBackground` is not automatically targeted.

Keep theme overlays, gradients, masks and layout around the existing background.
Do not add a second copy of the native background behind it or visibility triggers
that hide the entire injected host when IR is displaying artwork.

## Covers

The standard target is an `Image` registered as `PART_ImageCover`:

```xml
<Image x:Name="PART_ImageCover"
       Source="{Binding CoverImageObject}"
       Stretch="Uniform" />
```

IR also recognizes a direct `CoverImageObject` binding on an image with any name:

```xml
<Image x:Name="CoverImg"
       Source="{Binding CoverImageObject}"
       Stretch="Uniform" />
```

Bindings supplied by a style are supported too:

```xml
<Style x:Key="GameCoverStyle" TargetType="Image">
    <Setter Property="Source" Value="{Binding CoverImageObject}" />
    <Setter Property="Stretch" Value="Uniform" />
</Style>
```

For this additional detection, `CoverImageObject` must belong to the image's own
`DataContext`. IR must also resolve that context to a game, through a Game object,
a nested `Game` property, or a game `Id`. It never assumes an unidentified cover
belongs to the currently selected game.

The binding-based detection does not guess explicit `Source`, `ElementName`,
`RelativeSource`, nested paths such as `Game.CoverImageObject`, or
`OneWayToSource` bindings. For a custom binding scheme, use the standard
`PART_ImageCover` name and a game context the adapter can resolve.

Keep the original image and its normal source binding. The adapter preserves its
layout and restores the binding when it releases the cover. Grid and Canvas hosts
use a sibling rendering layer, so existing references to the native image remain
valid. Viewboxes, surrounding clipping and theme overlays can stay in place.

The user's selected-only/all-visible cover setting determines playback scope.
Unconfigured games retain their native cover. Off-screen and recycled cover
controls release IR playback resources.

## Logos

Playnite does not have a standard logo template part. IR recognizes two existing
Extra Metadata Loader display patterns.

### Extra Metadata Loader control

Keep the usual Extra Metadata Loader host:

```xml
<ContentControl x:Name="ExtraMetadataLoader_LogoLoaderControl" />
```

IR adapts the image inside `LogoLoaderControl`. Extra Metadata remains responsible
for its normal integration and files. IR remembers native source updates and
restores the latest source when it releases the image.

### Direct logo file binding

Images loading a per-game Extra Metadata logo can also be detected:

```text
<Playnite configuration directory>/ExtraMetadata/games/<game GUID>/logo.png
```

Detection supports a BitmapImage URI source, or an `ElementName` binding to a
TextBlock containing that full path. For example, in a view whose context is a game:

```xml
<TextBlock x:Name="GameLogoPath"
           Tag="{Api Paths.ConfigurationPath}"
           Visibility="Collapsed">
    <TextBlock.Text>
        <MultiBinding StringFormat="{}{0}\ExtraMetadata\games\{1}\logo.png">
            <Binding RelativeSource="{RelativeSource Self}" Path="Tag" />
            <Binding Path="Id" />
        </MultiBinding>
    </TextBlock.Text>
</TextBlock>

<Image Source="{Binding ElementName=GameLogoPath, Path=Text,
                        Converter={StaticResource ImageStringToImageConverter}}"
       Stretch="Uniform" />
```

Use the appropriate game-ID path for the view, such as `SelectedGame.Id` on a
selected-game context. Keep the TextBlock and Image in the same name scope.
Different logo placements can use different image names; the recognized file path
identifies the game. Other custom converter/binding schemes are not automatically
recognized.

IR supplies the displayed bitmap to the original image, preserving the theme's
geometry, effects and source binding. It does not change Extra Metadata's PNG.
Logo support retains IR's existing still-image formats; this is not video or
animated-logo support.

Outer theme visibility rules still apply. If a whole logo area is hidden using
Extra Metadata's `IsLogoAvailable`, a game with only an IR logo may still have that
area hidden. Prefer native image-source checks for direct-image title fallbacks,
and test games with an original logo, only an IR logo, and neither.

## Removing an older manual integration

1. Keep the theme's native artwork elements and source bindings. Check the standard
   background/cover part names or the supported bindings above.
2. Remove dedicated `ImageRotater_Background`, `ImageRotater_Cover` and
   `ImageRotater_Logo` ContentControls.
3. Remove the IR-specific visibility triggers that switched between those controls
   and native artwork. Retain unrelated theme preferences and title fallbacks.
4. Test with IR enabled, media rotation disabled, and both rotation and transitions
   disabled. Native artwork should remain available.

There is no need to add `HasDataCover`, `HasDataLogo` or `IsBackgroundDisplayed`
visibility bindings for automatic injection. Existing integrations have compatibility
handling, but they are not the approach to use for new theme development.

## User settings and validation

Background, cover, logo and pack choices remain plugin settings. Rotation and
transitions have separate switches. With media rotation off, enabled background
transitions use native artwork without selecting custom IR files. With both off,
the native background is restored.

Test rapid game changes during a transition, stills and videos, games without IR
artwork, both cover playback scopes, opening/closing details, and returning after
minimizing. Packs share the existing display controls; their video players are not
synchronized frame by frame.

The injection path avoids publishing selections through `Game.BackgroundImage` or
`Game.CoverImage`. It does not guarantee compatibility with every custom theme
structure or prove lower CPU/RAM usage. For unusual layouts, verify the actual
bindings, game context, parent structure and visibility rules first.
