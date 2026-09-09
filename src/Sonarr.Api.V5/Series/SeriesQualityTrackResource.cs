using NzbDrone.Core.Tv;

namespace Sonarr.Api.V5.Series;

public class SeriesQualityTrackResource
{
    public int Id { get; set; }
    public int QualityProfileId { get; set; }
    public bool IsPrimary { get; set; }
    public bool Enabled { get; set; }
    public int EpisodeFileCount { get; set; }
}

public static class SeriesQualityTrackResourceMapper
{
    public static SeriesQualityTrackResource ToResource(this SeriesQualityTrack track)
    {
        return new SeriesQualityTrackResource
        {
            Id = track.Id,
            QualityProfileId = track.QualityProfileId,
            IsPrimary = track.IsPrimary,
            Enabled = track.Enabled,
            EpisodeFileCount = track.EpisodeFileCount ?? track.TrackFiles?.Value?.Select(f => f.EpisodeFileId).Distinct().Count() ?? 0
        };
    }
}
