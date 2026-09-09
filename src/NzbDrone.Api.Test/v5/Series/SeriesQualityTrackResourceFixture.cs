using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Tv;
using Sonarr.Api.V5.Series;

namespace NzbDrone.Api.Test.v5.Series
{
    [TestFixture]
    public class SeriesQualityTrackResourceFixture
    {
        [Test]
        public void omitted_additional_profiles_should_preserve_existing_tracks()
        {
            var resource = STJson.Deserialize<SeriesResource>("{\"qualityProfileId\":3}");
            var series = new NzbDrone.Core.Tv.Series
            {
                QualityProfileId = 1,
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new SeriesQualityTrack { Id = 10, QualityProfileId = 1, IsPrimary = true, Enabled = true },
                    new SeriesQualityTrack { Id = 20, QualityProfileId = 2, Enabled = true }
                }
            };

            var model = resource.ToModel(series);

            model.QualityProfileId.Should().Be(3);
            model.AdditionalQualityProfileIds.Should().BeNull();
            model.QualityTracks.Value.Should().Contain(t => t.Id == 20 && t.Enabled);
        }

        [Test]
        public void explicit_empty_additional_profiles_should_request_removal()
        {
            var resource = STJson.Deserialize<SeriesResource>("{\"qualityProfileId\":1,\"additionalQualityProfileIds\":[]}");

            resource.ToModel(new NzbDrone.Core.Tv.Series()).AdditionalQualityProfileIds.Should().BeEmpty();
        }

        [Test]
        public void response_should_exclude_disabled_profiles_from_selection_but_keep_their_files_discoverable()
        {
            var retained = new SeriesQualityTrack
            {
                Id = 20,
                QualityProfileId = 2,
                Enabled = false,
                TrackFiles = new List<EpisodeTrackFile>
                {
                    new EpisodeTrackFile { EpisodeId = 1, EpisodeFileId = 100, TrackId = 20 },
                    new EpisodeTrackFile { EpisodeId = 2, EpisodeFileId = 100, TrackId = 20 },
                    new EpisodeTrackFile { EpisodeId = 3, EpisodeFileId = 101, TrackId = 20 }
                }
            };
            var series = new NzbDrone.Core.Tv.Series
            {
                QualityProfileId = 1,
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new SeriesQualityTrack { Id = 10, QualityProfileId = 1, IsPrimary = true, Enabled = true },
                    retained,
                    new SeriesQualityTrack { Id = 30, QualityProfileId = 3, Enabled = true }
                }
            };

            var resource = series.ToResource();

            resource.QualityProfileId.Should().Be(1);
            resource.AdditionalQualityProfileIds.Should().Equal(3);
            resource.QualityTracks.Should().ContainSingle(t => t.Id == 20 && !t.Enabled && t.EpisodeFileCount == 2);
        }

        [Test]
        public void response_fields_should_not_allow_writing_track_identity_or_file_ownership()
        {
            var resource = STJson.Deserialize<SeriesResource>("{\"qualityProfileId\":1,\"qualityTracks\":[{\"id\":999,\"qualityProfileId\":2,\"enabled\":true,\"episodeFileCount\":10}]}");

            var model = resource.ToModel();

            model.AdditionalQualityProfileIds.Should().BeNull();
            model.QualityTracks?.Value.Should().BeNullOrEmpty();
        }

        [Test]
        public void v3_resource_should_keep_scalar_shape_and_leave_extra_profiles_unchanged()
        {
            var resource = STJson.Deserialize<Sonarr.Api.V3.Series.SeriesResource>("{\"qualityProfileId\":2}");
            var model = Sonarr.Api.V3.Series.SeriesResourceMapper.ToModel(resource);

            model.QualityProfileId.Should().Be(2);
            model.AdditionalQualityProfileIds.Should().BeNull();
            STJson.ToJson(resource).Should().NotContain("additionalQualityProfileIds").And.NotContain("qualityTracks");
        }
    }
}
