# Theme Integration

ImageRotater supports two rendering paths for backgrounds and covers, plus a dedicated theme-only integration for logos.

## Overview

A theme can support these ImageRotater elements:

```xml
<ContentControl x:Name="ImageRotater_Background"/>
<ContentControl x:Name="ImageRotater_Cover"/>
<ContentControl x:Name="ImageRotater_Logo"/>
```

| Element | Theme integration | Compatibility mode | Animated artwork |
|---|:---:|:---:|:---:|
| Background | Yes | Yes | Yes, with theme integration |
| Cover | Yes | Yes | Yes, with theme integration |
| Logo | Yes | No | No |

Theme integration is the recommended path. It lets ImageRotater render directly from its own artwork library without repeatedly replacing Playnite's native cover/background metadata.

---

## Theme integration

When **Theme integration** is enabled, ImageRotater leaves Playnite's native `Game.BackgroundImage` and `Game.CoverImage` metadata untouched and renders through controls hosted by the active theme.

This is the preferred setup because it:

- avoids unnecessary Playnite database artwork updates;
- supports animated backgrounds and covers;
- supports ImageRotater logos;
- allows the theme to keep its normal artwork as a fallback.

The three supported controls are independent. A theme may support only some of them.

For example:

```xml
<ContentControl x:Name="ImageRotater_Background"/>
<ContentControl x:Name="ImageRotater_Cover"/>
```

supports backgrounds and covers but not logos.

Adding:

```xml
<ContentControl x:Name="ImageRotater_Logo"/>
```

adds logo support.

---

## Compatibility mode

Turn **Theme integration** off in **Settings > General** when the active theme does not include ImageRotater controls.

In Compatibility mode, ImageRotater can temporarily use Playnite's native artwork fields for static backgrounds and covers. This allows regular themes to work without special XAML integration.

Compatibility mode does **not** provide logo support because Playnite does not expose a native game-logo field equivalent to `Game.BackgroundImage` or `Game.CoverImage`.

For ImageRotater logos, the theme must include `ImageRotater_Logo`.

---

# Quick integration

A complete theme integration normally consists of these three controls:

```xml
<ContentControl x:Name="ImageRotater_Background"/>
<ContentControl x:Name="ImageRotater_Cover"/>
<ContentControl x:Name="ImageRotater_Logo"/>
```

Do not create a second image/video renderer in the theme. The background and cover controls already contain the rendering logic needed by ImageRotater.

---

# Background integration

Place `ImageRotater_Background` in the same container as the theme's normal background.

Example:

```xml
<Grid>
    <FadeImage x:Name="PART_ImageBackground"/>

    <ContentControl x:Name="ImageRotater_Background"
                    Panel.ZIndex="1"
                    HorizontalAlignment="Stretch"
                    VerticalAlignment="Stretch"
                    HorizontalContentAlignment="Stretch"
                    VerticalContentAlignment="Stretch"
                    Focusable="False"
                    IsTabStop="False"/>
</Grid>
```

The ImageRotater control renders the selected plugin background over the theme's normal background.

If ImageRotater has nothing to render for the current game, the normal theme background remains available underneath.

A theme can add its own visibility triggers if the background must follow theme-specific options such as "disable background image".

---

# Cover integration

Place `ImageRotater_Cover` as a sibling of the theme's normal cover.

For a Fullscreen game tile:

```xml
<Grid>
    <Image x:Name="PART_ImageCover"/>

    <ContentControl x:Name="ImageRotater_Cover"
                    HorizontalAlignment="Stretch"
                    VerticalAlignment="Stretch"
                    HorizontalContentAlignment="Stretch"
                    VerticalContentAlignment="Stretch"
                    Focusable="False"
                    IsTabStop="False"/>
</Grid>
```

The control supports still images and supported animated cover formats.

For grid tiles, it is normally best to leave the native cover underneath. `ImageRotater_Cover` stays empty when that game has no ImageRotater cover.

## Cover Stretch override

By default, ImageRotater follows Playnite's native cover stretch behavior.

A theme can override the rendering for a specific `ImageRotater_Cover` control through its standard `Tag` property:

```xml
<ContentControl x:Name="ImageRotater_Cover"
                Tag="ImageRotater:Stretch=Uniform;StretchDirection=Both"
                HorizontalAlignment="Stretch"
                VerticalAlignment="Stretch"/>
```

Supported values are normal WPF `Stretch` and `StretchDirection` values.

Example:

```xml
Tag="ImageRotater:Stretch=Uniform;StretchDirection=Both"
```

If the tag is omitted or invalid, ImageRotater falls back to Playnite's normal cover behavior.

This makes it possible to use different cover behavior in Grid View, Details View, or other theme layouts without changing the plugin settings globally.

---

# Logo integration

Logos are different from backgrounds and covers.

Playnite has no native game-logo artwork field, so ImageRotater logos are available only through theme integration.

Add:

```xml
<ContentControl x:Name="ImageRotater_Logo"/>
```

A typical logo control can be styled like this:

```xml
<ContentControl x:Name="ImageRotater_Logo"
                MaxWidth="600"
                MaxHeight="250"
                HorizontalAlignment="Center"
                VerticalAlignment="Center"
                Focusable="False"
                IsHitTestVisible="False"/>
```

ImageRotater logos currently support static image formats:

- PNG
- JPG / JPEG
- BMP

Transparent PNG is recommended for game logos.

## Logo layout overrides (optional)

For a theme whose logo host fills the whole background, set the **inner logo image**
layout via the host's standard `Tag` property. These values do not read or alter
ExtraMetadata preferences; each theme or view can choose its own layout:

```xml
<ContentControl x:Name="ImageRotater_Logo"
                HorizontalAlignment="Stretch"
                VerticalAlignment="Stretch"
                Tag="ImageRotater:Stretch=Uniform;MaxWidth=600;MaxHeight=200;HorizontalAlignment=Center;VerticalAlignment=Bottom"/>
```

Supported options: `Stretch` (`None`, `Fill`, `Uniform`, `UniformToFill`),
`StretchDirection` (`Both`, `UpOnly`, `DownOnly`), `MaxWidth`, `MaxHeight`,
`HorizontalAlignment` (`Left`, `Center`, `Right`, `Stretch`) and
`VerticalAlignment` (`Top`, `Center`, `Bottom`, `Stretch`). Width/height are
positive WPF device-independent pixels, using `.` for decimals.

All options are optional. Without a valid tag, the previous defaults remain:
`Stretch=Uniform` and no internal maximum size or alignment restriction.
This does not alter existing integrations such as Aniki ReMake.

## ExtraMetadata original in rotation

If ExtraMetadata's `ExtraMetadata/games/{gameId}/Logo.png` exists, ImageRotater
can also offer it as an **Original** candidate alongside user-added logos.
It never copies, replaces or deletes ExtraMetadata's file. Its inclusion can
be toggled independently in ImageRotater's Artwork Manager (right-click the
Original logo and choose Exclude/Include rotation). The original is read-only.

To avoid activating ImageRotater for every game that merely has an ExtraMetadata
logo, **at least one enabled ImageRotater logo** is still required before any
logo rotation is offered. With no ImageRotater logos, the theme's ordinary
ExtraMetadata fallback remains in control.

## No ImageRotater transition for logos

ImageRotater does **not** apply its background/cover transition engine to logos.

When the selected ImageRotater logo changes, the logo source is updated directly.

This is intentional: logo appearance and transition behavior remain under the control of Playnite/the theme.

If your theme already animates its normal or ExtraMetadata logo container, keep that theme-side behavior around the logo area. ImageRotater does not add another transition on top of it.

---

# Using ExtraMetadata as the logo fallback

Themes that already use ExtraMetadata should keep it as the normal fallback.

Recommended behavior:

```text
ImageRotater logo available
    -> show ImageRotater_Logo
    -> hide ExtraMetadata logo

No ImageRotater logo
    -> hide ImageRotater_Logo
    -> show ExtraMetadata logo normally
```

ImageRotater exposes `HasDataLogo` specifically for this purpose.

Example:

```xml
<Grid>
    <ContentControl x:Name="ExtraMetadataLoader_LogoLoaderControl">
        <ContentControl.Style>
            <Style TargetType="ContentControl">
                <Setter Property="Visibility" Value="Visible"/>
                <Style.Triggers>
                    <DataTrigger Binding="{PluginSettings Plugin=ImageRotater, Path=HasDataLogo, FallbackValue=False}"
                                 Value="True">
                        <Setter Property="Visibility" Value="Collapsed"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </ContentControl.Style>
    </ContentControl>

    <ContentControl x:Name="ImageRotater_Logo">
        <ContentControl.Style>
            <Style TargetType="ContentControl">
                <Setter Property="Visibility" Value="Collapsed"/>
                <Style.Triggers>
                    <DataTrigger Binding="{PluginSettings Plugin=ImageRotater, Path=HasDataLogo, FallbackValue=False}"
                                 Value="True">
                        <Setter Property="Visibility" Value="Visible"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </ContentControl.Style>
    </ContentControl>
</Grid>
```

This does not create a dependency between ImageRotater and ExtraMetadata. The theme simply chooses which logo control should be visible.

If ImageRotater is disabled, logo rotation is disabled, Theme integration is disabled, or the current game has no ImageRotater logo, `HasDataLogo` is false and the theme can continue showing its normal ExtraMetadata logo.

If your theme also has a text-title fallback, make sure that title does not appear at the same time as an ImageRotater logo.

---

# Settings available to themes

ImageRotater registers settings support with:

```text
SourceName = ImageRotater
```

Theme XAML can therefore use:

```xml
{PluginSettings Plugin=ImageRotater, Path=...}
```

Useful paths include:

| Path | Meaning |
|---|---|
| `EnableBackgroundImage` | Background rotation is currently enabled |
| `EnableCoverImage` | Cover rotation is currently enabled |
| `EnableLogoImage` | Logo rotation is enabled and Theme integration is active |
| `HasDataCover` | The currently selected game has ImageRotater cover artwork |
| `HasDataLogo` | The currently selected game has usable ImageRotater logo artwork |
| `CurrentCoverPath` | Full path of the currently selected ImageRotater cover |
| `CurrentCoverGameId` | Game ID associated with `CurrentCoverPath` |
| `ImagesRoot` | Root of ImageRotater's per-game artwork storage |

Example:

```xml
<DataTrigger Binding="{PluginSettings Plugin=ImageRotater, Path=HasDataLogo, FallbackValue=False}"
             Value="True">
    <Setter Property="Visibility" Value="Collapsed"/>
</DataTrigger>
```

`HasDataCover`, `HasDataLogo`, `CurrentCoverPath`, and `CurrentCoverGameId` describe the **currently selected game**. Do not use them as a per-tile state for an entire game grid.

The custom `ImageRotater_Cover` control receives the correct game context for each tile and is the recommended way to support grid covers.

---

# Active theme support detection

ImageRotater scans the active Desktop and Fullscreen theme XAML files and looks for the official element names:

```text
ImageRotater_Background
ImageRotater_Cover
ImageRotater_Logo
```

The **Active theme support** panel then reports which integrations were found.

Examples:

```text
Compatible (Background + Cover + Logo)
Compatible (Background + Cover)
Partial support (Background)
```

Use the exact element names above. Renaming them prevents ImageRotater from resolving the controls and prevents the support detector from recognizing the integration.

A theme is considered fully compatible with the normal background/cover integration when both `ImageRotater_Background` and `ImageRotater_Cover` are present. Logo support is reported additionally when `ImageRotater_Logo` is found.

---

# Fullscreen grid covers

ImageRotater requires Playnite **10.57 or newer**.

Playnite 10.57 fixed Fullscreen cover update notifications that were missing in older versions. Theme authors do not need to add the old manual cover-refresh workarounds.

For theme-integrated animated covers, use the plugin control directly inside the tile template:

```xml
<ContentControl x:Name="ImageRotater_Cover"/>
```

Do not add a second `MediaElement`, GIF renderer, or custom file watcher in each tile.

---

# Themes that also support BackgroundChanger

BackgroundChanger and ImageRotater use separate theme element names and can coexist in the same theme.

ImageRotater registers only:

```xml
<ContentControl x:Name="ImageRotater_Background"/>
<ContentControl x:Name="ImageRotater_Cover"/>
<ContentControl x:Name="ImageRotater_Logo"/>
```

It does not claim BackgroundChanger's controls.

Keep existing BackgroundChanger integration intact and add ImageRotater controls alongside it where required.

---

# What theme authors should not do

## Do not build another media renderer

Do not add your own `MediaElement` or GIF renderer specifically for ImageRotater artwork.

The plugin's Background and Cover controls already contain their renderers. Duplicating them can create multiple media pipelines, unnecessary memory use, and instability in Fullscreen.

## Do not copy animated content out of the plugin control

Reading only `Content.Source` from a plugin control can work for static bitmaps but does not preserve GIF/video playback.

Place the official ImageRotater control where the artwork must render instead.

## Do not bind a theme directly to a repeatedly overwritten artwork file unless you specifically need that behavior

WPF image caching and file lifetime behavior can make fixed-path artwork bindings unreliable for live rotation.

The custom controls are the supported integration path.

## Do not use selected-game settings to gate every grid tile

`HasDataCover` and `HasDataLogo` belong to the selected game.

For a game grid, allow each `ImageRotater_Cover` instance to receive its own tile game context.

---

# Recommended integration checklist

For complete ImageRotater support:

- [ ] Add `ImageRotater_Background` where the theme renders game backgrounds.
- [ ] Add `ImageRotater_Cover` where covers should support ImageRotater rendering.
- [ ] Add `ImageRotater_Logo` where the theme renders game logos.
- [ ] Keep the native cover/background underneath when a fallback is needed.
- [ ] Use `HasDataLogo` to fall back to ExtraMetadata or the theme's normal logo.
- [ ] Keep logo transitions/animations theme-side; ImageRotater does not animate logos.
- [ ] Use the Cover `Tag` override when a specific layout needs a different stretch mode.
- [ ] Do not duplicate ImageRotater's video/GIF renderer in theme XAML.
- [ ] Check **ImageRotater Settings > General > Active theme support** after installing the theme.

---

# Minimal complete example

```xml
<Grid>
    <FadeImage x:Name="PART_ImageBackground"/>
    <ContentControl x:Name="ImageRotater_Background"/>
</Grid>

<Grid>
    <Image x:Name="PART_ImageCover"/>
    <ContentControl x:Name="ImageRotater_Cover"
                    Tag="ImageRotater:Stretch=Uniform;StretchDirection=Both"/>
</Grid>

<Grid>
    <ContentControl x:Name="ExtraMetadataLoader_LogoLoaderControl">
        <ContentControl.Style>
            <Style TargetType="ContentControl">
                <Setter Property="Visibility" Value="Visible"/>
                <Style.Triggers>
                    <DataTrigger Binding="{PluginSettings Plugin=ImageRotater, Path=HasDataLogo, FallbackValue=False}"
                                 Value="True">
                        <Setter Property="Visibility" Value="Collapsed"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </ContentControl.Style>
    </ContentControl>

    <ContentControl x:Name="ImageRotater_Logo">
        <ContentControl.Style>
            <Style TargetType="ContentControl">
                <Setter Property="Visibility" Value="Collapsed"/>
                <Style.Triggers>
                    <DataTrigger Binding="{PluginSettings Plugin=ImageRotater, Path=HasDataLogo, FallbackValue=False}"
                                 Value="True">
                        <Setter Property="Visibility" Value="Visible"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </ContentControl.Style>
    </ContentControl>
</Grid>
```

That is enough for a theme to support ImageRotater backgrounds, covers, and logos while retaining its normal artwork as fallback.
