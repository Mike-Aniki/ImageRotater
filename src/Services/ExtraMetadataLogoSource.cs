using System;
using System.IO;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Reads ExtraMetadata's existing per-game logo without copying or altering it.
    // This is artwork interoperability only; no ExtraMetadata settings are read.
    public sealed class ExtraMetadataLogoSource
    {
        // Separate state file: GameImageStore prunes exclusions for files not
        // physically present in its own folder. ExtraMetadata's logo lives
        // outside that folder, so the regular exclusion list cannot hold it.
        private const string ExclusionFileName = ".imagerotater-extrametadata-logo-excluded";

        private readonly string _configurationPath;

        public ExtraMetadataLogoSource(string configurationPath)
        {
            _configurationPath = configurationPath;
        }

        public string GetLogoPath(Guid gameId)
        {
            if (string.IsNullOrWhiteSpace(_configurationPath) || gameId == Guid.Empty)
            {
                return null;
            }

            string path = Path.Combine(_configurationPath, "ExtraMetadata", "games",
                gameId.ToString(), "Logo.png");
            return File.Exists(path) ? path : null;
        }

        public bool IsExcluded(GameImageStore store, Guid gameId)
        {
            return store != null && File.Exists(
                Path.Combine(store.GetGameFolder(gameId, ArtworkKind.Logo), ExclusionFileName));
        }

        public bool SetExcluded(GameImageStore store, Guid gameId, bool excluded)
        {
            if (store == null) return false;
            try
            {
                string folder = store.GetGameFolder(gameId, ArtworkKind.Logo);
                string marker = Path.Combine(folder, ExclusionFileName);
                if (excluded)
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(marker, "Excluded from ImageRotater logo rotation");
                }
                else if (File.Exists(marker))
                {
                    File.Delete(marker);
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // Avoid double-weighting the original when it has also been imported
        // into ImageRotater. Only read content if sizes match.
        public static bool SameContent(string first, string second)
        {
            if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            try
            {
                var a = new FileInfo(first);
                var b = new FileInfo(second);
                if (!a.Exists || !b.Exists || a.Length != b.Length) return false;
                const int chunkSize = 8192;
                var ab = new byte[chunkSize];
                var bb = new byte[chunkSize];
                using (var af = File.OpenRead(first))
                using (var bf = File.OpenRead(second))
                {
                    int count;
                    while ((count = af.Read(ab, 0, ab.Length)) > 0)
                    {
                        int done = 0;
                        while (done < count)
                        {
                            int n = bf.Read(bb, done, count - done);
                            if (n == 0) return false;
                            done += n;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            if (ab[i] != bb[i]) return false;
                        }
                    }
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
