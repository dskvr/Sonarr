using System.Text.Json.Serialization;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Tv;
using Sonarr.Api.V5.EpisodeFiles;
using Sonarr.Api.V5.Series;
using Sonarr.Http.REST;
using Swashbuckle.AspNetCore.Annotations;

namespace Sonarr.Api.V5.Episodes
{
    public class EpisodeResource : RestResource
    {
        public int SeriesId { get; set; }
        public int TvdbId { get; set; }
        public int EpisodeFileId { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string? Title { get; set; }
        public string? AirDate { get; set; }
        public DateTime? AirDateUtc { get; set; }
        public DateTime? LastSearchTime { get; set; }
        public int Runtime { get; set; }
        public string? FinaleType { get; set; }
        public string? Overview { get; set; }
        public EpisodeFileResource? EpisodeFile { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<EpisodeFileResource>? EpisodeFiles { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<EpisodeQualityTrackResource>? QualityTracks { get; set; }
        public bool HasFile { get; set; }
        public bool Monitored { get; set; }
        public int? AbsoluteEpisodeNumber { get; set; }
        public int? SceneAbsoluteEpisodeNumber { get; set; }
        public int? SceneEpisodeNumber { get; set; }
        public int? SceneSeasonNumber { get; set; }
        public bool UnverifiedSceneNumbering { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime? GrabDate { get; set; }
        public SeriesResource? Series { get; set; }
        public List<MediaCover>? Images { get; set; }

        // Hiding this so people don't think its usable (only used to set the initial state)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        [SwaggerIgnore]
        public bool Grabbed { get; set; }
    }

    public static class EpisodeResourceMapper
    {
        public static EpisodeResource ToResource(this Episode model)
        {
            return new EpisodeResource
            {
                Id = model.Id,

                SeriesId = model.SeriesId,
                TvdbId = model.TvdbId,
                EpisodeFileId = model.EpisodeFileId,
                SeasonNumber = model.SeasonNumber,
                EpisodeNumber = model.EpisodeNumber,
                Title = model.Title,
                AirDate = model.AirDate,
                AirDateUtc = model.AirDateUtc,
                Runtime = model.Runtime,
                FinaleType = model.FinaleType,
                Overview = model.Overview,
                LastSearchTime = model.LastSearchTime,

                // EpisodeFile

                HasFile = model.HasFile,
                Monitored = model.Monitored,
                AbsoluteEpisodeNumber = model.AbsoluteEpisodeNumber,
                SceneAbsoluteEpisodeNumber = model.SceneAbsoluteEpisodeNumber,
                SceneEpisodeNumber = model.SceneEpisodeNumber,
                SceneSeasonNumber = model.SceneSeasonNumber,
                UnverifiedSceneNumbering = model.UnverifiedSceneNumbering,

                // Series = model.Series.MapToResource(),
            };
        }

        public static List<EpisodeResource> ToResource(this IEnumerable<Episode> models)
        {
            return models.Select(ToResource).ToList();
        }

        public static void MapQualityTracks(this EpisodeResource resource, Episode episode, NzbDrone.Core.Tv.Series series, bool includeEpisodeFiles, IUpgradableSpecification upgradableSpecification, ICustomFormatCalculationService formatCalculator)
        {
            var links = episode.TrackFiles?.Value ?? [];
            var files = links.Select(f => f.EpisodeFile.Value).Where(f => f != null).DistinctBy(f => f.Id).ToDictionary(f => f.Id);
            var formats = files.ToDictionary(f => f.Key, f => formatCalculator.ParseCustomFormat(f.Value, series));

            resource.QualityTracks = (series.QualityTracks?.Value ?? []).Select(track =>
            {
                var link = links.SingleOrDefault(f => f.TrackId == track.Id);
                var file = link == null ? null : files.GetValueOrDefault(link.EpisodeFileId);
                var profile = track.QualityProfile.Value;

                return new EpisodeQualityTrackResource
                {
                    TrackId = track.Id,
                    QualityProfileId = track.QualityProfileId,
                    IsPrimary = track.IsPrimary,
                    Enabled = track.Enabled,
                    EpisodeFileId = file?.Id ?? 0,
                    HasFile = file != null,
                    QualityCutoffNotMet = file != null && upgradableSpecification.QualityCutoffNotMet(profile, file.Quality),
                    CutoffNotMet = file != null && upgradableSpecification.CutoffNotMet(profile, file.Quality, formats[file.Id]),
                    CustomFormatScore = file == null ? 0 : profile.CalculateCustomFormatScore(formats[file.Id])
                };
            }).ToList();

            if (includeEpisodeFiles)
            {
                resource.EpisodeFiles = files.Values.Select(f => f.ToResource(series, upgradableSpecification, formatCalculator)).ToList();
            }
        }
    }
}
