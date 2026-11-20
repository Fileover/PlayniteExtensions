using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using MetadataImageOptimizer.Addons;
using MetadataImageOptimizer.Addons.BackgroundChanger;
using MetadataImageOptimizer.Helpers;
using MetadataImageOptimizer.Models;
using MetadataImageOptimizer.Settings;
using MetadataImageOptimizer.Views;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace MetadataImageOptimizer
{
    public class MetadataImageOptimizer : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly IPlayniteAPI api;
        private readonly List<SupportedAddonBase> supportedAddons;

        private MetadataImageOptimizerSettingsViewModel settingsVm { get; set; }

        /// <summary>
        /// Games that are currently being optimized. The database update the optimizer performs on a game raises
        /// <see cref="OnGameUpdated"/> again, which would otherwise start a second optimization (and a second
        /// progress dialog) for the same game.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, byte> gamesInProcess = new ConcurrentDictionary<Guid, byte>();

        public override Guid Id { get; } = Guid.Parse("17b571ff-6ffe-4bea-ad25-32e52b54f9d3");

        public MetadataImageOptimizer(IPlayniteAPI api) : base(api)
        {
            this.api = api;
            settingsVm = new MetadataImageOptimizerSettingsViewModel(this);
            supportedAddons = new List<SupportedAddonBase> { new BackgroundChangerOptimizer(api, settingsVm) };
            Properties = new GenericPluginProperties { HasSettings = true };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            yield return new GameMenuItem
            {
                Description = "Optimize images",
                MenuSection = "MetadataImageOptimizer",
                Action = actionArgs =>
                {
                    OptimizeGames(actionArgs.Games, settingsVm.Settings);
                }
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            // SOME kind of UI to be shown in the main menu if we're optimizing in the background
            // Gives users a way to kill it
            if (settingsVm.Settings.RunInBackground &&
                backgroundOptimizeQueue.Count > 0)
            {
                yield return new MainMenuItem()
                {
                    MenuSection = $"@MetadataImageOptimizer|Optimizing {backgroundOptimizeQueue.Count} games",
                    Description = "Cancel",
                    Action = (a) =>
                    {
                        ClearBackgroundOptimizeQueue();
                    }
                };
            }
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settingsVm;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new MetadataImageOptimizerSettingsView();
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);

            OptimizationCacheHelper.Load(api, GetPluginUserDataPath());

            if (File.Exists(BackgroundOptimizeQueueFilePath))
            {
                if (settingsVm.Settings.RunInBackground)
                {
                    LoadBackgroundOptimizeQueueFromFile();
                }
                else
                {
                    File.Delete(BackgroundOptimizeQueueFilePath);
                }
            }

            api.Database.Games.ItemUpdated += OnGameUpdated;
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            base.OnApplicationStopped(args);

            api.Database.Games.ItemUpdated -= OnGameUpdated;
            OptimizationCacheHelper.Save();
        }

        private void OnGameUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            var optimizerSettings = settingsVm.Settings;

            // Every image type can be switched off, in which case there is nothing to do at all.
            // Without this check the progress dialog still showed up on every game edit.
            if (!optimizerSettings.Background.Optimize
                && !optimizerSettings.Cover.Optimize
                && !optimizerSettings.Icon.Optimize)
            {
                return;
            }

            var gamesToUpdate = e.UpdatedItems
                // Ignore games where the playing/installing status was updated
                .Where(
                    change => change.OldData.IsInstalled == change.NewData.IsInstalled
                        && change.OldData.IsInstalling == change.NewData.IsInstalling
                        && change.OldData.IsLaunching == change.NewData.IsLaunching
                        && change.OldData.IsRunning == change.NewData.IsRunning
                        && change.OldData.IsUninstalling == change.NewData.IsUninstalling)
                // Ignore games we are currently updating
                .Where(change => !gamesInProcess.ContainsKey(change.OldData.Id))
                // Only keep games where an enabled image type actually changed
                .Where(change => ShouldOptimize(change, optimizerSettings))
                .ToList();

            if (gamesToUpdate.Count == 0)
            {
                return;
            }

            if (optimizerSettings.RunInBackground)
            {
                foreach (var change in gamesToUpdate)
                {
                    OptimizeGame(change, optimizerSettings);
                }
            }
            else
            {
                api.Dialogs.ActivateGlobalProgress(
                    globalProgress =>
                    {
                        globalProgress.ProgressMaxValue = gamesToUpdate.Count;

                        foreach (var change in gamesToUpdate)
                        {
                            OptimizeGame(change, optimizerSettings);

                            globalProgress.CurrentProgressValue += 1;
                        }
                    }
                    , new GlobalProgressOptions("Optimizing game images...", false) { IsIndeterminate = gamesToUpdate.Count == 1 });
            }
        }

        /// <summary>
        /// Determines whether any image of the updated game needs to be optimized, taking both the per image type
        /// 'Optimize' setting and the 'Always optimize images when editing a game' setting into account.
        /// </summary>
        private static bool ShouldOptimize(ItemUpdateEvent<Game> change, MetadataImageOptimizerSettings optimizerSettings)
        {
            return ShouldOptimize(optimizerSettings.Background, change.OldData.BackgroundImage, change.NewData.BackgroundImage, optimizerSettings.AlwaysOptimizeOnSave)
                || ShouldOptimize(optimizerSettings.Cover, change.OldData.CoverImage, change.NewData.CoverImage, optimizerSettings.AlwaysOptimizeOnSave)
                || ShouldOptimize(optimizerSettings.Icon, change.OldData.Icon, change.NewData.Icon, optimizerSettings.AlwaysOptimizeOnSave);
        }

        private static bool ShouldOptimize(ImageTypeSettings typeSettings, string oldPath, string newPath, bool alwaysOptimize)
        {
            if (typeSettings == null || !typeSettings.Optimize)
            {
                return false;
            }

            return alwaysOptimize || !string.Equals(oldPath, newPath, StringComparison.Ordinal);
        }

        private void OptimizeGames(List<Game> games, MetadataImageOptimizerSettings optimizerSettings)
        {
            if (games.Count == 0)
            {
                return;
            }

            if (!optimizerSettings.Background.Optimize
                && !optimizerSettings.Cover.Optimize
                && !optimizerSettings.Icon.Optimize)
            {
                api.Dialogs.ShowMessage(
                    "No image types are enabled for optimization. Enable at least one image type in the add-on settings."
                    , "MetadataImageOptimizer - Nothing to optimize");
                return;
            }

            if (optimizerSettings.RunInBackground)
            {
                foreach (var game in games)
                {
                    QueueOptimizeGame(
                        game.Id
                        , optimizerSettings.Background.Optimize
                        , optimizerSettings.Cover.Optimize
                        , optimizerSettings.Icon.Optimize
                        , true);
                }

                logger.Info($"Added {games.Count} games to queue.");
                api.Dialogs.ShowMessage($"Queued up {games.Count} games to optimize. Optimizing in the background.", "Queued up optimization");
            }
            else
            {
                api.Dialogs.ActivateGlobalProgress(
                    globalProgress =>
                    {
                        globalProgress.ProgressMaxValue = games.Count;

                        foreach (var game in games)
                        {
                            OptimizeGame(
                                game.Id
                                , optimizerSettings.Background.Optimize
                                , optimizerSettings.Cover.Optimize
                                , optimizerSettings.Icon.Optimize
                                , true);

                            globalProgress.CurrentProgressValue += 1;
                        }
                    }
                    , new GlobalProgressOptions("Optimizing game images...", false) { IsIndeterminate = games.Count == 1 });
            }
        }

        private void OptimizeGame(ItemUpdateEvent<Game> change, MetadataImageOptimizerSettings optimizerSettings)
        {
            var game = api.Database.Games[change.NewData.Id];

            var optimizeBackground = ShouldOptimize(optimizerSettings.Background, change.OldData.BackgroundImage, change.NewData.BackgroundImage, optimizerSettings.AlwaysOptimizeOnSave);
            var optimizeCover = ShouldOptimize(optimizerSettings.Cover, change.OldData.CoverImage, change.NewData.CoverImage, optimizerSettings.AlwaysOptimizeOnSave);
            var optimizeIcon = ShouldOptimize(optimizerSettings.Icon, change.OldData.Icon, change.NewData.Icon, optimizerSettings.AlwaysOptimizeOnSave);

            if (!optimizeBackground && !optimizeCover && !optimizeIcon)
            {
                return;
            }

            if (optimizerSettings.RunInBackground)
            {
                QueueOptimizeGame(game.Id, optimizeBackground, optimizeCover, optimizeIcon);
            }
            else
            {
                OptimizeGame(game.Id, optimizeBackground, optimizeCover, optimizeIcon);
            }
        }

        private void OptimizeGame(Guid gameId, bool optimizeBackground, bool optimizeCover, bool optimizeIcon, bool force = false)
        {
            // Register the game before touching the database, so the ItemUpdated event raised by our own update
            // is ignored instead of starting a second optimization (and a second progress dialog) for it.
            if (!gamesInProcess.TryAdd(gameId, 0))
            {
                logger.Debug($"Skipping optimization of '{gameId}'. It is already being processed.");
                return;
            }

            try
            {
                OptimizeGameCore(gameId, optimizeBackground, optimizeCover, optimizeIcon, force);
            }
            finally
            {
                gamesInProcess.TryRemove(gameId, out _);
            }
        }

        private void OptimizeGameCore(Guid gameId, bool optimizeBackground, bool optimizeCover, bool optimizeIcon, bool force)
        {
            var pathsChanged = false;
            var optimizerSettings = settingsVm.Settings;

            // Ensure we have the latest copy of {game}
            var game = api.Database.Games.Get(gameId);
            if (game == null)
            {
                return;
            }

            // An explicit "Optimize images" action always runs, otherwise unchanged images are skipped
            if (!force && !OptimizationCacheHelper.IsCheckNecessary(game))
            {
                logger.Debug($"Skipping '{game.Name}'. No change from previously cached information.");
                return;
            }

            string newBackgroundPath = null;
            string newCoverPath = null;
            string newIconPath = null;
            var backgroundFailed = false;
            var coverFailed = false;
            var iconFailed = false;
            var failures = new ConcurrentQueue<string>();

            logger.Debug($"Processing '{game.Name}'. Background: {optimizeBackground} | Cover: {optimizeCover} | Icon: {optimizeIcon}");

            var work = new List<ImageOptimizationWork>();
            if (optimizeBackground && !string.IsNullOrWhiteSpace(game.BackgroundImage))
            {
                work.Add(new ImageOptimizationWork(
                    "background"
                    , api.Database.GetFullFilePath(game.BackgroundImage)
                    , optimizerSettings.Background
                    , path => newBackgroundPath = path
                    , () => backgroundFailed = true));
            }

            if (optimizeCover && !string.IsNullOrWhiteSpace(game.CoverImage))
            {
                work.Add(new ImageOptimizationWork(
                    "cover"
                    , api.Database.GetFullFilePath(game.CoverImage)
                    , optimizerSettings.Cover
                    , path => newCoverPath = path
                    , () => coverFailed = true));
            }

            if (optimizeIcon && !string.IsNullOrWhiteSpace(game.Icon))
            {
                work.Add(new ImageOptimizationWork(
                    "icon"
                    , api.Database.GetFullFilePath(game.Icon)
                    , optimizerSettings.Icon
                    , path => newIconPath = path
                    , () => iconFailed = true));
            }

            // Only file work is parallel; database changes below stay on the calling thread.
            Parallel.ForEach(
                work
                , new ParallelOptions { MaxDegreeOfParallelism = 2 }
                , item =>
                {
                    try
                    {
                        item.SetResult(ImageOptimizer.Optimize(item.ImagePath, item.Settings, optimizerSettings.Quality));
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Error while optimizing {item.Name} image for '{game.Name}'.");
                        item.SetFailed();
                        failures.Enqueue(ex.Message);
                    }
                });

            if (!failures.IsEmpty)
            {
                api.Notifications.Add(
                    "metadataimageoptimizer-optimize-failed"
                    , $"Failed to optimize images of '{game.Name}': {string.Join("; ", failures)}"
                    , NotificationType.Error);
            }

            // Ensure we have the latest copy of {game} again in case it changed during the optimization
            game = api.Database.Games.Get(gameId) ?? game;

            if (newBackgroundPath != null && !string.Equals(newBackgroundPath, api.Database.GetFullFilePath(game.BackgroundImage), StringComparison.OrdinalIgnoreCase))
            {
                PlayniteApi.Database.RemoveFile(game.BackgroundImage);
                game.BackgroundImage = api.Database.AddFile(newBackgroundPath, game.Id);
                File.Delete(newBackgroundPath);
                pathsChanged = true;
            }

            if (newCoverPath != null && !string.Equals(newCoverPath, api.Database.GetFullFilePath(game.CoverImage), StringComparison.OrdinalIgnoreCase))
            {
                PlayniteApi.Database.RemoveFile(game.CoverImage);
                game.CoverImage = api.Database.AddFile(newCoverPath, game.Id);
                File.Delete(newCoverPath);
                pathsChanged = true;
            }

            if (newIconPath != null && !string.Equals(newIconPath, api.Database.GetFullFilePath(game.Icon), StringComparison.OrdinalIgnoreCase))
            {
                PlayniteApi.Database.RemoveFile(game.Icon);
                game.Icon = api.Database.AddFile(newIconPath, game.Id);
                File.Delete(newIconPath);
                pathsChanged = true;
            }

            // Process images from supported addons
            supportedAddons.Where(x => x.IsInstalled).ForEach(addon => addon.OptimizeImages(game.Id));

            if (pathsChanged)
            {
                api.Database.Games.Update(game);
            }

            OptimizationCacheHelper.Add(game, backgroundFailed, coverFailed, iconFailed);
        }

        private sealed class ImageOptimizationWork
        {
            public ImageOptimizationWork(
                string name
                , string imagePath
                , ImageTypeSettings settings
                , Action<string> setResult
                , Action setFailed)
            {
                Name = name;
                ImagePath = imagePath;
                Settings = settings;
                SetResult = setResult;
                SetFailed = setFailed;
            }

            public string Name { get; }
            public string ImagePath { get; }
            public ImageTypeSettings Settings { get; }
            public Action<string> SetResult { get; }
            public Action SetFailed { get; }
        }

        #region Background processing

        private readonly ConcurrentQueue<OptimizeQueueItem> backgroundOptimizeQueue = new ConcurrentQueue<OptimizeQueueItem>();
        private Thread backgroundOptimizeThread;
        private string BackgroundOptimizeQueueFilePath => Path.Combine(GetPluginUserDataPath(), "optimize-queue");

        private void QueueOptimizeGame(Guid gameId, bool optimizeBackground, bool optimizeCover, bool optimizeIcon, bool force = false)
        {
            var existingItem = backgroundOptimizeQueue.FirstOrDefault(x => x.GameId == gameId);
            if (existingItem != null)
            {
                existingItem.OptimizeBackground |= optimizeBackground;
                existingItem.OptimizeCover |= optimizeCover;
                existingItem.OptimizeIcon |= optimizeIcon;
                existingItem.Force |= force;
            }
            else
            {
                var queueItem = new OptimizeQueueItem(gameId, optimizeBackground, optimizeCover, optimizeIcon, force);
                backgroundOptimizeQueue.Enqueue(queueItem);
            }

            EnsureBackgroundThreadRunning();
        }

        private void EnsureBackgroundThreadRunning(bool force = false)
        {
            if (backgroundOptimizeThread != null && backgroundOptimizeThread.IsAlive)
            {
                if (force)
                {
                    try
                    {
                        backgroundOptimizeThread.Abort();
                    }
                    catch { }
                }
                else
                {
                    return;
                }
            }

            backgroundOptimizeThread = new Thread(BackgroundOptimizeThread_Run)
            { IsBackground = true, Name = nameof(MetadataImageOptimizer) };

            backgroundOptimizeThread.Start();
        }

        private void BackgroundOptimizeThread_Run()
        {
            var handled = 0u;

            if (backgroundOptimizeQueue.IsEmpty)
            {
                return;
            }

            while (!Environment.HasShutdownStarted && backgroundOptimizeQueue.TryDequeue(out var item))
            {
                OptimizeGame(item.GameId, item.OptimizeBackground, item.OptimizeCover, item.OptimizeIcon, item.Force);

                if (++handled % 100u == 0u)
                {
                    // Every {X} items we update the queue file to remove old items
                    // We don't do it EVERY file because then we'd be potentially hammering the disk
                    SaveBackgroundOptimizeQueueToFile();
                }

                if (!settingsVm.Settings.RunInBackground)
                {
                    // Clear remaining queue if background processing is turned off
                    logger.Info($"Background processing was turned off. Cleared queue of {backgroundOptimizeQueue.Count} items.");
                    api.Dialogs.ShowMessage(
                        $"Background processing was turned off. Cleared queue of {backgroundOptimizeQueue.Count} items."
                        , "MetadataImageOptimizer - Stopped background processing");
                    ClearBackgroundOptimizeQueue();
                    return;
                }
            }

            SaveBackgroundOptimizeQueueToFile();
        }

        private void ClearBackgroundOptimizeQueue()
        {
            lock (backgroundOptimizeQueue)
            {
                while (backgroundOptimizeQueue.Count > 0)
                {
                    backgroundOptimizeQueue.TryDequeue(out _);
                }
            }

            SaveBackgroundOptimizeQueueToFile();
        }

        private void LoadBackgroundOptimizeQueueFromFile()
        {
            var lines = File.ReadAllLines(BackgroundOptimizeQueueFilePath);
            logger.Info($"Loading queue from disk with {lines.Length} items...");

            foreach (var line in lines)
            {
                var parts = line.Split(',');

                // The force flag was added later, so queue files written by older versions only have four fields
                var force = false;

                if ((parts.Length == 4 || parts.Length == 5) &&
                    Guid.TryParse(parts[0], out var gameId) &&
                    bool.TryParse(parts[1], out var optimizeBackground) &&
                    bool.TryParse(parts[2], out var optimizeCover) &&
                    bool.TryParse(parts[3], out var optimizeIcon) &&
                    (parts.Length == 4 || bool.TryParse(parts[4], out force)))
                {
                    backgroundOptimizeQueue.Enqueue(new OptimizeQueueItem(gameId, optimizeBackground, optimizeCover, optimizeIcon, force));
                }
                else
                {
                    logger.Error($"Failed to parse optimize queue line: {line}");
                }
            }

            EnsureBackgroundThreadRunning();
        }

        private void SaveBackgroundOptimizeQueueToFile()
        {
            lock (backgroundOptimizeQueue)
            {
                File.WriteAllLines(
                    BackgroundOptimizeQueueFilePath
                    , backgroundOptimizeQueue.Select(
                        queueItem => $"{queueItem.GameId},{queueItem.OptimizeBackground},{queueItem.OptimizeCover},{queueItem.OptimizeIcon},{queueItem.Force}"));
            }
        }
        #endregion
    }
}
