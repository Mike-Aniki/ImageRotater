using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// So the parser can be tested against real yt-dlp output rather than through a
// process. Twice now a field has arrived empty from a shape assumption that was
// never checked against a captured line.
[assembly: InternalsVisibleTo("ImageRotater.Tests")]

[assembly: ComVisible(false)]
[assembly: Guid("72b7d457-0621-429b-8368-665bc53ff896")]

// Version information - updated automatically by scripts from version.txt
[assembly: AssemblyVersion("1.6.0")]
[assembly: AssemblyFileVersion("1.6.0")]
[assembly: AssemblyInformationalVersion("1.6.0")]
