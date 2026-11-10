using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ImageRotater.Models;
using Newtonsoft.Json;

namespace ImageRotater.Services
{
    internal static class ArtworkBackup
    {
        internal sealed class GameEntry
        {
            public Guid Id { get; set; }
            public Guid PluginId { get; set; }
            public string ExternalId { get; set; }
            public string Name { get; set; }
            public ArtworkPackConfiguration Pack { get; set; }
        }

        internal sealed class Manifest
        {
            public int Version { get; set; }
            public List<GameEntry> Games { get; set; }
        }

        internal sealed class RestoreItem
        {
            internal GameEntry Game;
            internal GameEntry Target;
            internal string Status;
            internal readonly List<ZipArchiveEntry> Files = new List<ZipArchiveEntry>();
        }

        private const string ManifestName = "manifest.json";
        private static readonly HashSet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
            { "covers", "backgrounds", "logos" };
        private static readonly HashSet<string> Markers = new HashSet<string>(StringComparer.Ordinal)
        {
            ".imagerotater-fixed", ".imagerotater-order", ".imagerotater-excluded",
            ".imagerotater-native-excluded", ".imagerotater-extrametadata-logo-excluded"
        };

        private static bool SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.EndsWith(".") || name.EndsWith(" ") ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("/") || name.Contains("\\"))
                return false;
            string stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
            return stem != "CON" && stem != "PRN" && stem != "AUX" && stem != "NUL" &&
                !((stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                  stem.Length == 4 && "123456789¹²³".Contains(stem[3]));
        }

        private static bool BackupFile(string name) => SafeFileName(name) &&
            (Markers.Contains(name) || (GameImageStore.IsSupported(name) && !GameImageStore.IsPreservedOriginal(name)));

        private static bool RealDirectory(string path) => Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

        private static JsonSerializer Serializer() => JsonSerializer.Create(new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 24
        });

        internal static int Export(string destination, string root, IReadOnlyList<GameEntry> games,
            Action<int, int, string> report, System.Threading.CancellationToken cancel)
        {
            string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var manifest = new Manifest { Version = 1, Games = new List<GameEntry>() };
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    for (int i = 0; i < games.Count; i++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var game = games[i];
                        string directory = Path.Combine(root, game.Id.ToString("D"));
                        var files = new List<string>();
                        if (RealDirectory(root) && RealDirectory(directory))
                        {
                            foreach (var kind in Kinds)
                            {
                                string folder = Path.Combine(directory, kind);
                                if (!RealDirectory(folder)) continue;
                                files.AddRange(Directory.EnumerateFiles(folder).Where(file =>
                                    BackupFile(Path.GetFileName(file)) &&
                                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0));
                            }
                        }

                        foreach (string file in files)
                        {
                            report(i, games.Count, game.Name + " — " + Path.GetFileName(file));
                            string entry = $"Images/{game.Id:D}/{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)}";
                            using (var input = File.OpenRead(file))
                            using (var target = zip.CreateEntry(entry, CompressionLevel.NoCompression).Open())
                                Copy(input, target, cancel);
                        }
                        if (files.Count > 0 || game.Pack != null) manifest.Games.Add(game);
                        report(i + 1, games.Count, game.Name);
                    }

                    using (var writer = new StreamWriter(zip.CreateEntry(ManifestName).Open()))
                    using (var json = new JsonTextWriter(writer))
                        Serializer().Serialize(json, manifest);
                }
                cancel.ThrowIfCancellationRequested();
                if (File.Exists(destination)) File.Replace(temp, destination, null);
                else File.Move(temp, destination);
                return manifest.Games.Count;
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        internal static List<RestoreItem> Plan(ZipArchive zip, IReadOnlyList<GameEntry> library, string root)
        {
            var header = zip.GetEntry(ManifestName);
            if (header == null || header.Length > 32 * 1024 * 1024) throw new InvalidDataException();
            Manifest manifest;
            using (var reader = new StreamReader(header.Open()))
            using (var json = new JsonTextReader(reader))
                manifest = Serializer().Deserialize<Manifest>(json);
            if (manifest?.Version != 1 || manifest.Games == null || manifest.Games.Any(game => game == null ||
                game.Id == Guid.Empty || !ValidPack(game.Pack)) ||
                manifest.Games.Select(game => game.Id).Distinct().Count() != manifest.Games.Count)
                throw new InvalidDataException();

            var byId = library.ToDictionary(game => game.Id);
            var byExternal = library.Where(HasExternalId).ToLookup(game => Tuple.Create(game.PluginId, game.ExternalId));
            var items = new Dictionary<Guid, RestoreItem>();
            foreach (var game in manifest.Games)
            {
                var item = new RestoreItem { Game = game };
                items.Add(game.Id, item);
                if (!byId.TryGetValue(game.Id, out item.Target) && HasExternalId(game))
                {
                    var candidates = byExternal[Tuple.Create(game.PluginId, game.ExternalId)].Take(2).ToList();
                    if (candidates.Count == 1) item.Target = candidates[0];
                    if (candidates.Count > 1) item.Status = "Ambiguous";
                }
                if (item.Target == null && item.Status == null) item.Status = "Unmatched";
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (!paths.Add(entry.FullName)) throw new InvalidDataException();
                if (entry.FullName == ManifestName) continue;
                var parts = entry.FullName.Split('/');
                if (parts.Length != 4 || parts[0] != "Images" ||
                    !Guid.TryParseExact(parts[1], "D", out Guid id) || !items.TryGetValue(id, out var item) ||
                    !Kinds.Contains(parts[2]) || !BackupFile(parts[3]) ||
                    !paths.Add($"canonical:{id:D}/{parts[2]}/{parts[3]}"))
                    throw new InvalidDataException();
                if (Markers.Contains(parts[3]) && entry.Length > 1024 * 1024)
                    throw new InvalidDataException();
                item.Files.Add(entry);
            }

            foreach (var group in items.Values.Where(item => item.Target != null).GroupBy(item => item.Target.Id))
            {
                if (group.Count() > 1)
                {
                    // An exact match takes precedence over a remap to the same game.
                    foreach (var item in group.Where(item => item.Game.Id != item.Target.Id))
                        item.Status = "Ambiguous";
                }

                foreach (var item in group.Where(item => item.Status == null))
                {
                    string directory = Path.Combine(root, item.Target.Id.ToString("D"));
                    if (item.Target.Pack != null || File.Exists(directory) ||
                        (Directory.Exists(directory) && (!RealDirectory(directory) ||
                         Directory.EnumerateFileSystemEntries(directory).Any())))
                        item.Status = "Existing";
                }
            }

            return manifest.Games.Select(game => items[game.Id]).ToList();
        }

        private static bool HasExternalId(GameEntry game) =>
            game.PluginId != Guid.Empty && !string.IsNullOrWhiteSpace(game.ExternalId);

        private static bool ValidPack(ArtworkPackConfiguration pack) => pack == null ||
            (pack.Packs != null && pack.Packs.All(item => item != null &&
                new[] { item.Cover, item.Background, item.Logo }.All(name =>
                    string.IsNullOrEmpty(name) || (SafeFileName(name) && GameImageStore.IsSupported(name)))));

        internal static void Restore(RestoreItem item, string root, Action<string> report,
            System.Threading.CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (item.Files.Count == 0) return;
            Directory.CreateDirectory(root);
            if (!RealDirectory(root)) throw new IOException();
            string destination = Path.Combine(root, item.Target.Id.ToString("D"));
            // The staging directory is owned by this operation and contains only
            // regular files created below from validated, flat archive paths.
            string staging = Path.Combine(root, ".restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var entry in item.Files)
                {
                    var parts = entry.FullName.Split('/');
                    string folder = Path.Combine(staging, parts[2]);
                    Directory.CreateDirectory(folder);
                    report(parts[3]);
                    string file = Path.Combine(folder, parts[3]);
                    using (var input = entry.Open())
                    using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
                    {
                        Copy(input, output, cancel);
                        if (output.Length != entry.Length) throw new InvalidDataException();
                    }
                    if (parts[3] == ".imagerotater-fixed")
                    {
                        string selected = File.ReadAllText(file).Trim();
                        if (selected.Length != 0 && (!SafeFileName(selected) || !GameImageStore.IsSupported(selected)))
                            throw new InvalidDataException();
                    }
                }
                cancel.ThrowIfCancellationRequested();
                if (RealDirectory(destination) && !Directory.EnumerateFileSystemEntries(destination).Any())
                    Directory.Delete(destination);
                Directory.Move(staging, destination);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }

        private static void Copy(Stream source, Stream destination, System.Threading.CancellationToken cancel)
        {
            var buffer = new byte[128 * 1024];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, count);
            }
        }
    }
}
