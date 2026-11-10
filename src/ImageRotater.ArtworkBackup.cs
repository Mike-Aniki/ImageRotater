using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ImageRotater.Controls;
using ImageRotater.Models;
using ImageRotater.Services;
using Playnite.SDK;

namespace ImageRotater
{
    public partial class ImageRotater
    {
        private List<ArtworkBackup.GameEntry> BackupLibrary()
        {
            return PlayniteApi.Database.Games.Select(game => new ArtworkBackup.GameEntry
            {
                Id = game.Id,
                PluginId = game.PluginId,
                ExternalId = game.GameId,
                Name = game.Name,
                Pack = _packs.Configuration(game.Id)
            }).ToList();
        }

        private static string BackupZipFilter => Loc.Get("LOCImageRotaterBackupZip") + "|*.zip";

        internal void ExportArtworkBackup()
        {
            string path = PlayniteApi.Dialogs.SaveFile(BackupZipFilter);
            if (string.IsNullOrWhiteSpace(path)) return;
            var games = BackupLibrary();
            string result = null;
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                try
                {
                    int exported = ArtworkBackup.Export(path, _store.ImagesRoot, games,
                        (done, total, item) =>
                        {
                            progress.ProgressMaxValue = Math.Max(1, total);
                            progress.CurrentProgressValue = done;
                            progress.Text = Loc.Format("LOCImageRotaterProgressFile", done, total, item);
                        }, progress.CancelToken);
                    result = Loc.Format("LOCImageRotaterBackupExported", exported, path);
                }
                catch (OperationCanceledException) { result = Loc.Get("LOCImageRotaterBackupCancelled"); }
                catch (Exception ex)
                {
                    Logger.Error(ex, "ImageRotater: artwork backup export failed");
                    result = Loc.Format("LOCImageRotaterBackupError", ex.Message);
                }
            }, new GlobalProgressOptions(Loc.Get("LOCImageRotaterBackupExport"), true) { IsIndeterminate = false });

            if (result != null) PlayniteApi.Dialogs.ShowMessage(result, "ImageRotater");
        }

        internal void ImportArtworkBackup()
        {
            string path = PlayniteApi.Dialogs.SelectFile(BackupZipFilter);
            if (string.IsNullOrWhiteSpace(path)) return;
            var library = BackupLibrary();
            try
            {
                using (var stream = File.OpenRead(path))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var plan = ArtworkBackup.Plan(zip, library, _store.ImagesRoot);
                    var ready = plan.Where(item => item.Status == null).ToList();
                    string preview = Loc.Format("LOCImageRotaterBackupPreview", ready.Count, plan.Count - ready.Count);
                    if (ready.Count == 0)
                    {
                        ShowBackupReport(Loc.Format("LOCImageRotaterBackupResult", 0, plan.Count), plan);
                        return;
                    }

                    if (PlayniteApi.Dialogs.ShowMessage(preview, "ImageRotater", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                        return;

                    bool cancelled = false;
                    PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
                    {
                        progress.ProgressMaxValue = ready.Count;
                        progress.CurrentProgressValue = 0;
                        for (int i = 0; i < ready.Count; i++)
                        {
                            var item = ready[i];
                            try
                            {
                                progress.Text = Loc.Format("LOCImageRotaterProgressFile", i + 1, ready.Count, item.Game.Name);
                                ArtworkBackup.Restore(item, _store.ImagesRoot, file =>
                                    progress.Text = Loc.Format("LOCImageRotaterProgressFile", i + 1, ready.Count, item.Game.Name + " — " + file),
                                    progress.CancelToken);
                                item.Status = "Restored";
                            }
                            catch (OperationCanceledException)
                            {
                                cancelled = true;
                                break;
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex, $"ImageRotater: could not restore artwork for {item.Game.Id}");
                                item.Status = "Failed";
                            }
                            progress.CurrentProgressValue = i + 1;
                        }
                    }, new GlobalProgressOptions(Loc.Get("LOCImageRotaterBackupImport"), true) { IsIndeterminate = false });

                    var restored = ready.Where(item => item.Status == "Restored").ToList();
                    var packs = restored.Where(item => item.Game.Pack != null)
                        .ToDictionary(item => item.Target.Id, item => item.Game.Pack);
                    if (packs.Count > 0) _settingsViewModel.SaveImportedArtworkPacks(packs);
                    if (restored.Count > 0)
                    {
                        _rotationService?.ForgetAll();
                        foreach (var item in restored) _packs.Forget(item.Target.Id);
                        NotifyArtworkBackupRestored(restored.Select(item => item.Target.Id));
                    }
                    string summary = Loc.Format("LOCImageRotaterBackupResult", restored.Count, plan.Count - restored.Count);
                    if (cancelled) summary += Environment.NewLine + Loc.Get("LOCImageRotaterBackupCancelled");
                    ShowBackupReport(summary, plan);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: artwork backup import failed");
                PlayniteApi.Dialogs.ShowMessage(Loc.Format("LOCImageRotaterBackupError", ex.Message), "ImageRotater");
            }
        }

        private void NotifyArtworkBackupRestored(IEnumerable<Guid> games)
        {
            foreach (var id in games)
            {
                CoverImageControl.NotifyArtworkRotated(id);
                LogoImageControl.NotifyArtworkChanged(id);
            }

            if (_slideshowGame != null)
            {
                BackgroundImageControl.NotifyPlaybackPolicyChanged();
                SyncNativeFadeImageHost(_slideshowGame);
            }
        }

        private void ShowBackupReport(string summary, IEnumerable<ArtworkBackup.RestoreItem> plan)
        {
            var report = new TextBox
            {
                Text = summary + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine,
                    plan.Select(item => Loc.Format("LOCImageRotaterBackupLine",
                        Loc.Get("LOCImageRotaterBackup" + (item.Status ?? "NotApplied")),
                        item.Target != null && item.Game.Id != item.Target.Id
                            ? item.Game.Name + " → " + item.Target.Name : item.Game.Name))),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(16)
            };
            var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions { ShowCloseButton = true });
            window.Title = Loc.Get("LOCImageRotaterBackupReport");
            window.Width = 630;
            window.Height = 430;
            window.Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                ?? Application.Current.MainWindow;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Content = report;
            window.ShowDialog();
        }
    }
}
