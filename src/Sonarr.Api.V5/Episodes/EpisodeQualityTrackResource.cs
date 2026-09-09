namespace Sonarr.Api.V5.Episodes;

public class EpisodeQualityTrackResource
{
    public int TrackId { get; set; }
    public int QualityProfileId { get; set; }
    public bool IsPrimary { get; set; }
    public bool Enabled { get; set; }
    public int EpisodeFileId { get; set; }
    public bool HasFile { get; set; }
    public bool QualityCutoffNotMet { get; set; }
    public bool CutoffNotMet { get; set; }
    public int CustomFormatScore { get; set; }
}
