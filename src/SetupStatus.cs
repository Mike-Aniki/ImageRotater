namespace ImageRotater
{
    // One line under a Setup field: a glyph, the message, and the colour both
    // are drawn in.
    //
    // A small object rather than three properties per field, because there are
    // three fields and nine loose strings on a view model is where they start
    // disagreeing with each other - a message updated without its glyph shows
    // a tick next to an error.
    //
    // Plain characters, not Segoe MDL2 icons. MDL2 is Windows 10 and later,
    // and a machine without the font renders an empty box where the answer
    // should be. UniPlaySong already puts a bare warning sign in its XAML, so
    // this follows what the other plugins do.
    public class SetupStatus
    {
        public string Glyph { get; private set; }
        public string Message { get; private set; }
        public string Brush { get; private set; }

        // Not set, and that is fine. Every one of these is optional - the
        // plugin works without any of them and simply does less.
        public static SetupStatus Neutral(string message)
        {
            return new SetupStatus
            {
                Glyph = string.Empty,
                Message = message,
                Brush = "Gray"
            };
        }

        public static SetupStatus Ok(string message)
        {
            return new SetupStatus
            {
                Glyph = "✓",
                Message = message,

                // Not the theme's accent: a tick has to read as "working"
                // against whatever palette the theme uses, and Playnite themes
                // put accents on everything from orange to purple.
                Brush = "#3A9E4B"
            };
        }

        public static SetupStatus Warning(string message)
        {
            return new SetupStatus
            {
                Glyph = "!",
                Message = message,
                Brush = "#D9A441"
            };
        }

        public static SetupStatus Problem(string message)
        {
            return new SetupStatus
            {
                Glyph = "✗",
                Message = message,
                Brush = "#E03A3A"
            };
        }
    }
}
