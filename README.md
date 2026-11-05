<div align="center">

<img src="assets/banner.png" alt="ImageRotater" width="720">

### Give each game more than one look.

<p>
  <a href="https://github.com/Mike-Aniki/ImageRotater/releases/latest">
    <img src="https://img.shields.io/github/v/release/Mike-Aniki/ImageRotater?style=for-the-badge&label=Latest%20Release&color=blue" alt="Latest release">
  </a>
  <a href="https://github.com/Mike-Aniki/ImageRotater/releases/latest">
    <img src="https://img.shields.io/github/downloads/Mike-Aniki/ImageRotater/latest/total?style=for-the-badge&label=Latest%20Release%20Downloads&color=blue" alt="Latest release downloads">
  </a>
  <a href="https://github.com/Mike-Aniki/ImageRotater/stargazers">
    <img src="https://img.shields.io/github/stars/Mike-Aniki/ImageRotater?style=for-the-badge&label=Stars&color=blue" alt="GitHub stars">
  </a>
</p>

<p>

ImageRotater is a Playnite extension that lets every game use a collection of covers, backgrounds and logos instead of being limited to a single image.

Add the artwork you like, choose how you want it to behave, and ImageRotater takes care of switching it inside Playnite.

The plugin works in both **Desktop** and **Fullscreen** and lets you manage covers, backgrounds and logos independently.

</p>

<p align="center">
  <img src="assets/artwork-manager.png" alt="ImageRotater Artwork Manager" width="47%">
  <img src="assets/online-search.png" alt="ImageRotater online artwork search" width="47%">
</p>

</div>

<h2 align="center">Project</h2>

<div align="center">

### **[Huddini](https://github.com/aHuddini) × [Mike-Aniki](https://github.com/Mike-Aniki)**

ImageRotater was originally created by **Huddini** and is now developed collaboratively with **Mike-Aniki**.

<br>

[Support Huddini on Ko-fi](https://ko-fi.com/huddini)
·
[Support Mike Aniki on Ko-fi](https://ko-fi.com/mikeaniki)

</div>

---

<h2 align="center">More Than One Artwork per Game</h2>

Once a game has several covers, backgrounds or logos, ImageRotater can decide which one Playnite should display.

You can keep one artwork for the whole Playnite session, choose a new one each time you focus the game, lock a specific artwork, or use slideshow rotation.

You can also group a cover, background and logo into a **synchronized artwork pack** and rotate the whole set together. See [Artwork Packs](docs/ARTWORK_PACKS.md) for setup, selection modes and theme support.

The detailed behavior and all available settings are explained in the User Guide.

<div align="center">

### **[Read the User Guide](https://github.com/Mike-Aniki/ImageRotater/wiki)**

</div>

---

<h2 align="center">Transitions Only</h2>

You do not have to add any artwork to use ImageRotater.

With artwork rotation turned off and transitions turned on, ImageRotater keeps Playnite's original covers and backgrounds and only replaces the animation played when you switch from one game to another. Crossfade, Mosaic, Pixelate, Reveal and the other transitions can be chosen separately for covers and backgrounds.

---

<h2 align="center">Works with Regular Playnite Themes</h2>

ImageRotater displays images **and videos** directly inside the theme's own artwork elements, in both **Desktop** and **Fullscreen**. Themes using Playnite's standard cover and background elements need no dedicated ImageRotater support, and there is no integration mode to enable.

Logos work through the **Extra Metadata Loader** logo control, or through themes that load its per-game `logo.png` directly.

Games without ImageRotater artwork keep their normal Playnite artwork. Themes with a custom structure may need small adjustments; see the [Theme Developer Guide](docs/THEME_INTEGRATION.md).

---

<h2 align="center">Coming from BackgroundChanger?</h2>

ImageRotater includes a migration tool for existing BackgroundChanger users.

Your current covers and backgrounds can be imported into ImageRotater's own artwork library without deleting the original BackgroundChanger files.

This makes it possible to move over without rebuilding your collection game by game.

Migration steps are documented in the User Guide.

---

<h2 align="center">For Users</h2>

The User Guide contains the complete documentation for using ImageRotater.

Installation, artwork management, rotation modes, artwork packs, online sources, optional tools, BackgroundChanger migration, troubleshooting, and advanced settings are all documented separately so the main project page stays easy to read.

<div align="center">

### **[Open the User Guide](https://github.com/Mike-Aniki/ImageRotater/wiki)**

</div>

---

<h2 align="center">For Theme Authors</h2>

ImageRotater injects its artwork into existing theme elements automatically.

The Theme Developer Guide covers the supported background and cover elements, cover bindings, Extra Metadata logos, and how to remove an older manual ImageRotater integration.

<div align="center">

### **[Open the Theme Developer Guide](docs/THEME_INTEGRATION.md)**

</div>

---

<h2 align="center">Links</h2>

<div align="center">

[Latest Release](https://github.com/Mike-Aniki/ImageRotater/releases/latest)
·
[User Guide](https://github.com/Mike-Aniki/ImageRotater/wiki)
·
[Artwork Packs](docs/ARTWORK_PACKS.md)
·
[Theme Developer Guide](docs/THEME_INTEGRATION.md)
·
[Changelog](CHANGELOG.md)
·
[Issues](https://github.com/Mike-Aniki/ImageRotater/issues)
·
[Playnite](https://playnite.link/)

</div>
