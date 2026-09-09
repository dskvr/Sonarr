using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.SeriesStats
{
    public interface ISeriesStatisticsService
    {
        List<SeriesStatistics> SeriesStatistics();
        SeriesStatistics SeriesStatistics(int seriesId, int qualityProfileId);
    }

    public class SeriesStatisticsService : ISeriesStatisticsService
    {
        private readonly ISeriesStatisticsRepository _seriesStatisticsRepository;
        private readonly ISeriesService _seriesService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly ISeriesQualityTrackService _qualityTrackService;
        private readonly IEpisodeTrackFileService _trackFileService;
        private readonly IEpisodeService _episodeService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IUpgradableSpecification _upgradableSpecification;

        public SeriesStatisticsService(ISeriesStatisticsRepository seriesStatisticsRepository,
                                       ISeriesService seriesService,
                                       IQualityProfileService qualityProfileService,
                                       ISeriesQualityTrackService qualityTrackService,
                                       IEpisodeTrackFileService trackFileService,
                                       IEpisodeService episodeService,
                                       IMediaFileService mediaFileService,
                                       ICustomFormatCalculationService formatCalculator,
                                       IUpgradableSpecification upgradableSpecification)
        {
            _seriesStatisticsRepository = seriesStatisticsRepository;
            _seriesService = seriesService;
            _qualityProfileService = qualityProfileService;
            _qualityTrackService = qualityTrackService;
            _trackFileService = trackFileService;
            _episodeService = episodeService;
            _mediaFileService = mediaFileService;
            _formatCalculator = formatCalculator;
            _upgradableSpecification = upgradableSpecification;
        }

        public List<SeriesStatistics> SeriesStatistics()
        {
            var seasonStatistics = _seriesStatisticsRepository.SeriesStatistics();
            var seriesProfiles = _seriesService.GetAllSeriesQualityProfiles();
            var profiles = _qualityProfileService.All().ToDictionary(p => p.Id);
            var tracks = _qualityTrackService.GetAllTracks().Where(t => t.Enabled).ToLookup(t => t.SeriesId);

            return seasonStatistics
                .GroupBy(s => s.SeriesId)
                .Select(s =>
                {
                    var profileId = seriesProfiles.GetValueOrDefault(s.Key);
                    profiles.TryGetValue(profileId, out var profile);
                    var statistics = MapSeriesStatistics(s.ToList(), profile);
                    AddQualityTrackStatistics(statistics, tracks[s.Key].ToList(), profiles);
                    return statistics;
                })
                .ToList();
        }

        public SeriesStatistics SeriesStatistics(int seriesId, int qualityProfileId)
        {
            var stats = _seriesStatisticsRepository.SeriesStatistics(seriesId);

            if (stats == null || stats.Count == 0)
            {
                return new SeriesStatistics();
            }

            var profile = _qualityProfileService.Get(qualityProfileId);

            var statistics = MapSeriesStatistics(stats, profile);
            var tracks = _qualityTrackService.GetEnabledTracks(seriesId);

            if (tracks.Count > 1)
            {
                AddQualityTrackStatistics(statistics, tracks, _qualityProfileService.All().ToDictionary(p => p.Id));
            }

            return statistics;
        }

        private void AddQualityTrackStatistics(SeriesStatistics statistics, List<SeriesQualityTrack> tracks, Dictionary<int, QualityProfile> profiles)
        {
            if (tracks.Count < 2)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var episodesBySeason = _episodeService.GetEpisodeBySeries(statistics.SeriesId).ToLookup(e => e.SeasonNumber);
            var files = _mediaFileService.GetFilesBySeries(statistics.SeriesId).ToDictionary(f => f.Id);
            var links = _trackFileService.GetForSeries(statistics.SeriesId).ToLookup(l => l.TrackId);
            var formats = new Dictionary<int, List<CustomFormat>>();

            foreach (var track in tracks)
            {
                if (!profiles.TryGetValue(track.QualityProfileId, out var profile))
                {
                    // The profile may have been removed after its track was disabled during this read.
                    continue;
                }

                var trackFiles = links[track.Id]
                    .Where(l => files.ContainsKey(l.EpisodeFileId))
                    .ToDictionary(l => l.EpisodeId, l => files[l.EpisodeFileId]);
                var trackStatistics = new QualityTrackStatistics { TrackId = track.Id, QualityProfileId = track.QualityProfileId };

                foreach (var season in statistics.SeasonStatistics)
                {
                    var seasonStatistics = new QualityTrackStatistics { TrackId = track.Id, QualityProfileId = track.QualityProfileId };

                    foreach (var episode in episodesBySeason[season.SeasonNumber])
                    {
                        var hasFile = trackFiles.TryGetValue(episode.Id, out var file);

                        if (hasFile || (episode.Monitored && episode.AirDateUtc <= now))
                        {
                            seasonStatistics.EpisodeCount++;
                        }

                        if (!hasFile)
                        {
                            continue;
                        }

                        seasonStatistics.EpisodeFileCount++;

                        if (episode.Monitored)
                        {
                            if (!formats.TryGetValue(file.Id, out var customFormats))
                            {
                                customFormats = _formatCalculator.ParseCustomFormat(file);
                                formats.Add(file.Id, customFormats);
                            }

                            if (_upgradableSpecification.CutoffNotMet(profile, file.Quality, customFormats))
                            {
                                seasonStatistics.CutoffUnmetCount++;
                            }
                        }
                    }

                    season.QualityTracks.Add(seasonStatistics);
                    trackStatistics.EpisodeCount += seasonStatistics.EpisodeCount;
                    trackStatistics.EpisodeFileCount += seasonStatistics.EpisodeFileCount;
                    trackStatistics.CutoffUnmetCount += seasonStatistics.CutoffUnmetCount;
                }

                statistics.QualityTracks.Add(trackStatistics);
            }
        }

        private SeriesStatistics MapSeriesStatistics(List<SeasonStatistics> seasonStatistics, QualityProfile profile)
        {
            var seriesStatistics = new SeriesStatistics
            {
                SeasonStatistics = seasonStatistics,
                SeriesId = seasonStatistics.First().SeriesId,
                EpisodeFileCount = seasonStatistics.Sum(s => s.EpisodeFileCount),
                EpisodeCount = seasonStatistics.Sum(s => s.EpisodeCount),
                TotalEpisodeCount = seasonStatistics.Sum(s => s.TotalEpisodeCount),
                MonitoredEpisodeCount = seasonStatistics.Sum(s => s.MonitoredEpisodeCount),
                SizeOnDisk = seasonStatistics.Sum(s => s.SizeOnDisk),
                ReleaseGroups = seasonStatistics.SelectMany(s => s.ReleaseGroups).Distinct().ToList(),
                ReleaseTypes = seasonStatistics.SelectMany(s => s.ReleaseTypes).Distinct().OrderBy(s => s).ToList(),
                EpisodeFileQualities = SortQualities(seasonStatistics.SelectMany(s => s.EpisodeFileQualities).Distinct().ToList(), profile)
            };

            var nextAiring = seasonStatistics.Where(s => s.NextAiring != null).MinBy(s => s.NextAiring);
            var previousAiring = seasonStatistics.Where(s => s.PreviousAiring != null).MaxBy(s => s.PreviousAiring);
            var lastAired = seasonStatistics.Where(s => s.SeasonNumber > 0 && s.LastAired != null).MaxBy(s => s.LastAired);

            seriesStatistics.NextAiring = nextAiring?.NextAiring;
            seriesStatistics.PreviousAiring = previousAiring?.PreviousAiring;
            seriesStatistics.LastAired = lastAired?.LastAired;

            return seriesStatistics;
        }

        private static List<Quality> SortQualities(List<Quality> qualities, QualityProfile profile)
        {
            if (profile == null)
            {
                return qualities;
            }

            return qualities.OrderBy(q => profile.GetIndex(q.Id).Index).ToList();
        }
    }
}
