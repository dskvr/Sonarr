using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.AutoTagging;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Tv
{
    public interface ISeriesService
    {
        Series GetSeries(int seriesId);
        List<Series> GetSeries(IEnumerable<int> seriesIds);
        Series AddSeries(Series newSeries);
        List<Series> AddSeries(List<Series> newSeries);
        Series FindByTvdbId(int tvdbId);
        Series FindByTvRageId(int tvRageId);
        Series FindByImdbId(string imdbId);
        Series FindByTitle(string title);
        Series FindByTitle(string title, int year);
        Series FindByTitleInexact(string title);
        Series FindByPath(string path);
        void DeleteSeries(List<int> seriesIds, bool deleteFiles, bool addImportListExclusion);
        List<Series> GetAllSeries();
        Dictionary<int, int> AllSeriesTvdbIds();
        Dictionary<int, string> GetAllSeriesPaths();
        Dictionary<int, List<int>> GetAllSeriesTags();
        List<Series> AllForTag(int tagId);
        Dictionary<int, int> GetAllSeriesQualityProfiles();
        Series UpdateSeries(Series series, bool updateEpisodesToMatchSeason = true, bool publishUpdatedEvent = true);
        List<Series> UpdateSeries(List<Series> series, bool useExistingRelativeFolder);
        Series UpdateSeriesMetadata(Series series, bool updateEpisodesToMatchSeason = true, bool publishUpdatedEvent = true);
        List<Series> UpdateSeriesMetadata(List<Series> series, bool useExistingRelativeFolder);
        bool SeriesPathExists(string folder);
        void RemoveAddOptions(Series series);
        bool UpdateAutoTaggingTags(Series series);
        void UpdateTags(List<Series> series);
    }

    public class SeriesService : ISeriesService
    {
        private readonly ISeriesRepository _seriesRepository;
        private readonly IEventAggregator _eventAggregator;
        private readonly IEpisodeService _episodeService;
        private readonly IBuildSeriesPaths _seriesPathBuilder;
        private readonly IAutoTaggingService _autoTaggingService;
        private readonly ISeriesQualityTrackService _qualityTrackService;
        private readonly ISeriesFolderMoveService _folderMoveService;
        private readonly Logger _logger;

        public SeriesService(ISeriesRepository seriesRepository,
                             IEventAggregator eventAggregator,
                             IEpisodeService episodeService,
                             IBuildSeriesPaths seriesPathBuilder,
                             IAutoTaggingService autoTaggingService,
                             ISeriesQualityTrackService qualityTrackService,
                             ISeriesFolderMoveService folderMoveService,
                             Logger logger)
        {
            _seriesRepository = seriesRepository;
            _eventAggregator = eventAggregator;
            _episodeService = episodeService;
            _seriesPathBuilder = seriesPathBuilder;
            _autoTaggingService = autoTaggingService;
            _qualityTrackService = qualityTrackService;
            _folderMoveService = folderMoveService;
            _logger = logger;
        }

        public Series GetSeries(int seriesId)
        {
            return _seriesRepository.Get(seriesId);
        }

        public List<Series> GetSeries(IEnumerable<int> seriesIds)
        {
            return _seriesRepository.Get(seriesIds).ToList();
        }

        public Series AddSeries(Series newSeries)
        {
            using var operationLock = MediaFileOperationLock.AcquireAll();
            _folderMoveService.RecoverPending(Array.Empty<int>(), new[] { newSeries.Path });
            _seriesRepository.Insert(newSeries);
            _eventAggregator.PublishEvent(new SeriesAddedEvent(GetSeries(newSeries.Id)));

            return newSeries;
        }

        public List<Series> AddSeries(List<Series> newSeries)
        {
            using var operationLock = MediaFileOperationLock.AcquireAll();
            _folderMoveService.RecoverPending(Array.Empty<int>(), newSeries.Select(s => s.Path));
            _seriesRepository.InsertMany(newSeries);
            _eventAggregator.PublishEvent(new SeriesImportedEvent(newSeries.Select(s => s.Id).ToList()));

            return newSeries;
        }

        public Series FindByTvdbId(int tvRageId)
        {
            return _seriesRepository.FindByTvdbId(tvRageId);
        }

        public Series FindByTvRageId(int tvRageId)
        {
            return _seriesRepository.FindByTvRageId(tvRageId);
        }

        public Series FindByImdbId(string imdbId)
        {
            return _seriesRepository.FindByImdbId(imdbId);
        }

        public Series FindByTitle(string title)
        {
            return _seriesRepository.FindByTitle(title.CleanSeriesTitle());
        }

        public Series FindByTitleInexact(string title)
        {
            // find any series clean title within the provided release title
            var cleanTitle = title.CleanSeriesTitle();
            var list = _seriesRepository.FindByTitleInexact(cleanTitle);
            if (!list.Any())
            {
                // no series matched
                return null;
            }

            if (list.Count == 1)
            {
                // return the first series if there is only one
                return list.Single();
            }

            // build ordered list of series by position in the search string
            var query =
                list.Select(series => new
                {
                    position = cleanTitle.IndexOf(series.CleanTitle),
                    length = series.CleanTitle.Length,
                    series = series
                })
                    .Where(s => (s.position >= 0))
                    .ToList()
                    .OrderBy(s => s.position)
                    .ThenByDescending(s => s.length)
                    .ToList();

            // get the leftmost series that is the longest
            // series are usually the first thing in release title, so we select the leftmost and longest match
            var match = query.First().series;

            _logger.Debug("Multiple series matched {0} from title {1}", match.Title, title);
            foreach (var entry in list)
            {
                _logger.Debug("Multiple series match candidate: {0} cleantitle: {1}", entry.Title, entry.CleanTitle);
            }

            return match;
        }

        public Series FindByPath(string path)
        {
            return _seriesRepository.FindByPath(path);
        }

        public Series FindByTitle(string title, int year)
        {
            return _seriesRepository.FindByTitle(title.CleanSeriesTitle(), year);
        }

        public void DeleteSeries(List<int> seriesIds, bool deleteFiles, bool addImportListExclusion)
        {
            using var operationLock = MediaFileOperationLock.AcquireAll();
            _folderMoveService.RecoverPending(seriesIds, Array.Empty<string>());
            var series = _seriesRepository.Get(seriesIds).ToList();
            _seriesRepository.DeleteMany(seriesIds);
            _eventAggregator.PublishEvent(new SeriesDeletedEvent(series, deleteFiles, addImportListExclusion));
        }

        public List<Series> GetAllSeries()
        {
            return _seriesRepository.All().ToList();
        }

        public Dictionary<int, int> AllSeriesTvdbIds()
        {
            return _seriesRepository.AllSeriesTvdbIds();
        }

        public Dictionary<int, string> GetAllSeriesPaths()
        {
            return _seriesRepository.AllSeriesPaths();
        }

        public Dictionary<int, List<int>> GetAllSeriesTags()
        {
            return _seriesRepository.AllSeriesTags();
        }

        public Dictionary<int, int> GetAllSeriesQualityProfiles()
        {
            return _seriesRepository.AllSeriesQualityProfiles();
        }

        public List<Series> AllForTag(int tagId)
        {
            return GetAllSeries().Where(s => s.Tags.Contains(tagId))
                                 .ToList();
        }

        // updateEpisodesToMatchSeason is an override for EpisodeMonitoredService to use so a change via Season pass doesn't get nuked by the seasons loop.
        // TODO: Remove when seasons are split from series (or we come up with a better way to address this)
        public Series UpdateSeries(Series series, bool updateEpisodesToMatchSeason = true, bool publishUpdatedEvent = true)
        {
            using var operationLock = AcquireUpdateLock(new[] { series });
            _qualityTrackService.ValidateProfiles(series.Id, series.QualityProfileId, series.AdditionalQualityProfileIds);
            var storedSeries = GetSeries(series.Id);
            if (!series.Path.PathEquals(storedSeries.Path) && _seriesRepository.HasPathConflict(series.Id, series.Path))
            {
                throw new ValidationException(new[] { new ValidationFailure("Path", "Another series already uses this folder or an overlapping folder.") });
            }

            var episodeMonitoredChanged = false;

            if (updateEpisodesToMatchSeason)
            {
                foreach (var season in series.Seasons)
                {
                    var storedSeason = storedSeries.Seasons.SingleOrDefault(s => s.SeasonNumber == season.SeasonNumber);

                    if (storedSeason != null && season.Monitored != storedSeason.Monitored)
                    {
                        _episodeService.SetEpisodeMonitoredBySeason(series.Id, season.SeasonNumber, season.Monitored);
                        episodeMonitoredChanged = true;
                    }
                }
            }

            // Never update AddOptions when updating a series, keep it the same as the existing stored series.
            series.AddOptions = storedSeries.AddOptions;
            UpdateAutoTaggingTags(series);

            var updatedSeries = _seriesRepository.Update(series);
            if (publishUpdatedEvent)
            {
                _eventAggregator.PublishEvent(new SeriesEditedEvent(updatedSeries, storedSeries, episodeMonitoredChanged));
            }

            return updatedSeries;
        }

        public List<Series> UpdateSeries(List<Series> series, bool useExistingRelativeFolder)
        {
            _logger.Debug("Updating {0} series", series.Count);

            foreach (var s in series)
            {
                _logger.Trace("Updating: {0}", s.Title);

                if (!s.RootFolderPath.IsNullOrWhiteSpace())
                {
                    s.Path = _seriesPathBuilder.BuildPath(s, useExistingRelativeFolder);

                    _logger.Trace("Changing path for {0} to {1}", s.Title, s.Path);
                }
                else
                {
                    _logger.Trace("Not changing path for: {0}", s.Title);
                }

                UpdateAutoTaggingTags(s);
            }

            using var operationLock = AcquireUpdateLock(series);
            _seriesRepository.UpdateMany(series);
            _logger.Debug("{0} series updated", series.Count);
            _eventAggregator.PublishEvent(new SeriesBulkEditedEvent(series));

            return series;
        }

        private IDisposable AcquireUpdateLock(IList<Series> models)
        {
            while (true)
            {
                var pathChanged = models.Any(s => !s.Path.PathEquals(_seriesRepository.Get(s.Id).Path));
                var scope = pathChanged ? MediaFileOperationLock.AcquireAll() : MediaFileOperationLock.Acquire(models.Select(s => s.Id));
                try
                {
                    if (pathChanged)
                    {
                        _folderMoveService.RecoverPending(models.Select(s => s.Id), models.Select(s => s.Path));
                        return scope;
                    }

                    if (models.All(s => s.Path.PathEquals(_seriesRepository.Get(s.Id).Path)))
                    {
                        return scope;
                    }
                }
                catch
                {
                    scope.Dispose();
                    throw;
                }

                scope.Dispose();
            }
        }

        public Series UpdateSeriesMetadata(Series series, bool updateEpisodesToMatchSeason = true, bool publishUpdatedEvent = true)
        {
            using var operationLock = MediaFileOperationLock.Acquire(new[] { series.Id });
            PreserveCurrentConfiguration(series);
            return UpdateSeries(series, updateEpisodesToMatchSeason, publishUpdatedEvent);
        }

        public List<Series> UpdateSeriesMetadata(List<Series> series, bool useExistingRelativeFolder)
        {
            using var operationLock = MediaFileOperationLock.Acquire(series.Select(s => s.Id));
            foreach (var model in series)
            {
                PreserveCurrentConfiguration(model);
            }

            return UpdateSeries(series, useExistingRelativeFolder);
        }

        private void PreserveCurrentConfiguration(Series series)
        {
            var current = _seriesRepository.Get(series.Id);

            // Metadata refresh, monitoring and list cleanup do not edit paths or quality selections.
            series.Path = current.Path;
            series.RootFolderPath = null;
            series.QualityProfileId = current.QualityProfileId;
            series.QualityProfile = current.QualityProfile;
            series.QualityTracks = current.QualityTracks;
            series.AdditionalQualityProfileIds = null;
        }

        public bool SeriesPathExists(string folder)
        {
            return _seriesRepository.SeriesPathExists(folder);
        }

        public void RemoveAddOptions(Series series)
        {
            _seriesRepository.SetFields(series, s => s.AddOptions);
        }

        public bool UpdateAutoTaggingTags(Series series)
        {
            _logger.Trace("Updating tags for {0}", series);

            var tagsAdded = new HashSet<int>();
            var tagsRemoved = new HashSet<int>();
            var changes = _autoTaggingService.GetTagChanges(series);

            foreach (var tag in changes.TagsToRemove)
            {
                if (series.Tags.Contains(tag))
                {
                    series.Tags.Remove(tag);
                    tagsRemoved.Add(tag);
                }
            }

            foreach (var tag in changes.TagsToAdd)
            {
                if (!series.Tags.Contains(tag))
                {
                    series.Tags.Add(tag);
                    tagsAdded.Add(tag);
                }
            }

            if (tagsAdded.Any() || tagsRemoved.Any())
            {
                _logger.Debug("Updated tags for '{0}'. Added: {1}, Removed: {2}", series.Title, tagsAdded.Count, tagsRemoved.Count);

                return true;
            }

            _logger.Debug("Tags not updated for '{0}'", series.Title);

            return false;
        }

        public void UpdateTags(List<Series> series)
        {
            if (series.Count == 0)
            {
                return;
            }

            _seriesRepository.SetFields(series, s => s.Tags);
            _eventAggregator.PublishEvent(new SeriesBulkEditedEvent(series));
        }
    }
}
