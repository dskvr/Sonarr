using NzbDrone.Core.SeriesStats;

namespace Sonarr.Api.V5.Series;

public class QualityTrackStatisticsResource
{
    public int TrackId { get; set; }
    public int QualityProfileId { get; set; }
    public int EpisodeCount { get; set; }
    public int EpisodeFileCount { get; set; }
    public int CutoffUnmetCount { get; set; }
    public int MissingCount { get; set; }
}

public static class QualityTrackStatisticsResourceMapper
{
    public static QualityTrackStatisticsResource ToResource(this QualityTrackStatistics model)
    {
        return new QualityTrackStatisticsResource
        {
            TrackId = model.TrackId,
            QualityProfileId = model.QualityProfileId,
            EpisodeCount = model.EpisodeCount,
            EpisodeFileCount = model.EpisodeFileCount,
            CutoffUnmetCount = model.CutoffUnmetCount,
            MissingCount = model.MissingCount
        };
    }
}
