namespace ImageRotater.Models
{
    // What an image is for. Backgrounds and covers need different selection
    // rules - a background wants the most pixels it can get, while a cover is
    // drawn into a fixed-aspect card where shape matters more than resolution -
    // so they cannot share a pool.
    public enum ArtworkKind
    {
        Background,
        Cover,
        Logo
    }
}
