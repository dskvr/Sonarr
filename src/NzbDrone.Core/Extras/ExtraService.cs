using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Extras
{
    public interface IExtraService
    {
        void MoveFilesAfterRename(Series series, EpisodeFile episodeFile, bool requireSuccess = false);
        void ImportEpisode(LocalEpisode localEpisode, EpisodeFile episodeFile, bool isReadOnly);
        void EnsureRenamesCompleted(Series series);
    }

    public class ExtraService : IExtraService,
                                IHandle<MediaCoversUpdatedEvent>,
                                IHandle<EpisodeFolderCreatedEvent>,
                                IHandle<SeriesScannedEvent>,
                                IHandle<SeriesRenamedEvent>,
                                IHandle<DownloadsProcessedEvent>
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly IEpisodeService _episodeService;
        private readonly IEpisodeTrackFileService _trackFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IConfigService _configService;
        private readonly List<IManageExtraFiles> _extraFileManagers;
        private readonly Dictionary<int, Series> _seriesWithImportedFiles;

        public ExtraService(IMediaFileService mediaFileService,
                            IEpisodeService episodeService,
                            IEpisodeTrackFileService trackFileService,
                            IDiskProvider diskProvider,
                            IAppFolderInfo appFolderInfo,
                            IConfigService configService,
                            IEnumerable<IManageExtraFiles> extraFileManagers,
                            Logger logger)
        {
            _mediaFileService = mediaFileService;
            _episodeService = episodeService;
            _trackFileService = trackFileService;
            _diskProvider = diskProvider;
            _appFolderInfo = appFolderInfo;
            _configService = configService;
            _extraFileManagers = extraFileManagers.OrderBy(e => e.Order).ToList();
            _seriesWithImportedFiles = new Dictionary<int, Series>();
        }

        public void EnsureRenamesCompleted(Series series)
        {
            var root = MediaFileRecoveryPaths.GetJournalRoot(_appFolderInfo, _diskProvider);
            var directory = MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, root, Path.Combine(root, "extras"));
            if (!_diskProvider.FolderExists(directory))
            {
                return;
            }

            var prefixes = new[] { nameof(SubtitleFile), nameof(OtherExtraFile), nameof(MetadataFile) }
                .Select(type => $"{type}-{series.Id}-").ToList();
            foreach (var receipt in _diskProvider.GetFiles(directory, false))
            {
                var filename = Path.GetFileName(receipt);
                if (filename.EndsWith(".json", StringComparison.Ordinal) && prefixes.Any(prefix => filename.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, directory, receipt);
                    throw new IOException("Finish pending sidecar renames before moving this series. Its files and recovery receipt are preserved.");
                }
            }
        }

        public void ImportEpisode(LocalEpisode localEpisode, EpisodeFile episodeFile, bool isReadOnly)
        {
            ImportExtraFiles(localEpisode, episodeFile, isReadOnly);

            CreateAfterEpisodeImport(localEpisode.Series, episodeFile);
        }

        private void ImportExtraFiles(LocalEpisode localEpisode, EpisodeFile episodeFile, bool isReadOnly)
        {
            if (!_configService.ImportExtraFiles)
            {
                return;
            }

            var folderSearchOption = localEpisode.FolderEpisodeInfo != null;

            var wantedExtensions = _configService.ExtraFileExtensions.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                                     .Select(e => e.Trim(' ', '.')
                                                                     .Insert(0, "."))
                                                                     .ToList();

            var sourceFolder = _diskProvider.GetParentFolder(localEpisode.Path);
            var files = _diskProvider.GetFiles(sourceFolder, folderSearchOption);
            var managedFiles = _extraFileManagers.Select((i) => new List<string>()).ToArray();

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                var matchingExtension = wantedExtensions.FirstOrDefault(e => e.Equals(extension));

                if (matchingExtension == null)
                {
                    continue;
                }

                for (var i = 0; i < _extraFileManagers.Count; i++)
                {
                    if (_extraFileManagers[i].CanImportFile(localEpisode, episodeFile, file, extension, isReadOnly))
                    {
                        managedFiles[i].Add(file);
                        break;
                    }
                }
            }

            for (var i = 0; i < _extraFileManagers.Count; i++)
            {
                _extraFileManagers[i].ImportFiles(localEpisode, episodeFile, managedFiles[i], isReadOnly);
            }
        }

        private void CreateAfterEpisodeImport(Series series, EpisodeFile episodeFile)
        {
            lock (_seriesWithImportedFiles)
            {
                _seriesWithImportedFiles.TryAdd(series.Id, series);
            }

            foreach (var extraFileManager in _extraFileManagers)
            {
                extraFileManager.CreateAfterEpisodeImport(series, episodeFile);
            }
        }

        public void Handle(MediaCoversUpdatedEvent message)
        {
            if (message.Updated)
            {
                var series = message.Series;

                foreach (var extraFileManager in _extraFileManagers)
                {
                    extraFileManager.CreateAfterMediaCoverUpdate(series);
                }
            }
        }

        public void Handle(SeriesScannedEvent message)
        {
            var series = message.Series;
            var episodeFiles = GetEpisodeFiles(series.Id);

            foreach (var extraFileManager in _extraFileManagers)
            {
                extraFileManager.CreateAfterSeriesScan(series, episodeFiles);
            }
        }

        public void Handle(EpisodeFolderCreatedEvent message)
        {
            var series = message.Series;

            foreach (var extraFileManager in _extraFileManagers)
            {
                extraFileManager.CreateAfterEpisodeFolder(series, message.SeriesFolder, message.SeasonFolder);
            }
        }

        public void MoveFilesAfterRename(Series series, EpisodeFile episodeFile, bool requireSuccess = false)
        {
            var episodeFiles = new List<EpisodeFile> { episodeFile };

            var failures = new List<Exception>();
            foreach (var extraFileManager in _extraFileManagers)
            {
                try
                {
                    extraFileManager.MoveFilesAfterRename(series, episodeFiles, requireSuccess).ToList();
                }
                catch (Exception ex) when (requireSuccess)
                {
                    failures.Add(ex);
                }
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("Extra files could not be moved after rename.", failures);
            }
        }

        public void Handle(SeriesRenamedEvent message)
        {
            var series = message.Series;
            var episodeFiles = GetEpisodeFiles(series.Id);

            foreach (var extraFileManager in _extraFileManagers)
            {
                extraFileManager.MoveFilesAfterRename(series, episodeFiles);
            }
        }

        public void Handle(DownloadsProcessedEvent message)
        {
            var allSeries = new List<Series>();

            lock (_seriesWithImportedFiles)
            {
                allSeries.AddRange(_seriesWithImportedFiles.Values);

                _seriesWithImportedFiles.Clear();
            }

            foreach (var series in allSeries)
            {
                foreach (var extraFileManager in _extraFileManagers)
                {
                    extraFileManager.CreateAfterEpisodesImported(series);
                }
            }
        }

        private List<EpisodeFile> GetEpisodeFiles(int seriesId)
        {
            var episodeFiles = _mediaFileService.GetFilesBySeries(seriesId);
            var episodes = _episodeService.GetEpisodeBySeries(seriesId).ToDictionary(e => e.Id);
            var links = _trackFileService.GetForSeries(seriesId).ToLookup(l => l.EpisodeFileId);

            foreach (var episodeFile in episodeFiles)
            {
                episodeFile.Episodes = links[episodeFile.Id].Select(l => l.EpisodeId)
                    .Distinct().Where(episodes.ContainsKey).Select(id => episodes[id]).ToList();
            }

            return episodeFiles;
        }
    }
}
