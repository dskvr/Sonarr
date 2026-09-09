namespace Sonarr.Api.V5.Release;

public class ReleaseQualityTrackDecisionResource
{
    public int TrackId { get; set; }
    public int QualityProfileId { get; set; }
    public ReleaseDecisionResource? Decision { get; set; }
    public int CustomFormatScore { get; set; }
}
