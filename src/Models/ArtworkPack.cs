using System;
using System.Collections.Generic;

namespace ImageRotater.Models
{
    public sealed class ArtworkPack
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        // File names relative to each kind's game folder. Null means Original.
        public string Cover { get; set; }
        public string Background { get; set; }
        public string Logo { get; set; }
        public bool Excluded { get; set; }
        public override string ToString() => Name;
    }

    public sealed class ArtworkPackConfiguration
    {
        // Retained for deserializing early pack configurations. Runtime behavior
        // now comes from ImageRotaterSettings.Pack* for every configured game.
        public bool Enabled { get; set; }
        public SelectionMode Mode { get; set; } = SelectionMode.Session;
        public SelectionOrder Order { get; set; } = SelectionOrder.Random;
        public int SlideshowSeconds { get; set; } = 15;
        public string FixedPackId { get; set; }
        public List<ArtworkPack> Packs { get; set; } = new List<ArtworkPack>();
    }
}
