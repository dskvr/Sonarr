using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.EpisodeImport.Specifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport.Manual
{
    [TestFixture]
    public class SameEpisodesQualityTracksWorkflowFixture : CoreTest<SameEpisodesImportSpecification>
    {
        private LocalEpisode _local;
        private Episode _first;
        private Episode _second;

        [SetUp]
        public void Setup()
        {
            _first = new Episode { Id = 1, SeriesId = 1, EpisodeFileId = 50 };
            _second = new Episode { Id = 2, SeriesId = 1, EpisodeFileId = 50 };
            _local = new LocalEpisode
            {
                Series = new Series
                {
                    Id = 1,
                    QualityTracks = new List<SeriesQualityTrack> { new SeriesQualityTrack { Id = 10, IsPrimary = true, Enabled = true } }
                },
                Episodes = [_first],
                TargetQualityTrackIds = [10]
            };
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodesByFileId(50)).Returns(new List<Episode> { _first, _second });
        }

        [Test]
        public void single_profile_partial_replacement_should_keep_existing_rejection()
        {
            var result = Subject.IsSatisfiedBy(_local, null);

            result.Accepted.Should().BeFalse();
            result.Reason.Should().Be(ImportRejectionReason.ExistingFileHasMoreEpisodes);
            Subject.Type.Should().Be(RejectionType.Permanent);
        }

        [Test]
        public void single_profile_complete_replacement_should_remain_accepted()
        {
            _local.Episodes.Add(_second);

            Subject.IsSatisfiedBy(_local, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void enabled_additional_profile_should_allow_reference_aware_partial_replacement()
        {
            _local.Series.QualityTracks.Value.Add(new SeriesQualityTrack { Id = 20, Enabled = true });

            Subject.IsSatisfiedBy(_local, null).Accepted.Should().BeTrue();

            Mocker.GetMock<IEpisodeService>().Verify(s => s.GetEpisodesByFileId(50), Times.Never());
        }

        [Test]
        public void retained_version_should_allow_reference_aware_partial_replacement()
        {
            _local.Series.QualityTracks.Value.Add(new SeriesQualityTrack
            {
                Id = 20,
                Enabled = false,
                TrackFiles = new List<EpisodeTrackFile> { new EpisodeTrackFile { EpisodeId = 2, TrackId = 20, EpisodeFileId = 50 } }
            });

            Subject.IsSatisfiedBy(_local, null).Accepted.Should().BeTrue();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void disabled_profile_without_retained_files_should_restore_single_profile_rejection(bool loadedEmptyLinks)
        {
            var track = new SeriesQualityTrack { Id = 20, Enabled = false };
            if (loadedEmptyLinks)
            {
                track.TrackFiles = new List<EpisodeTrackFile>();
            }

            _local.Series.QualityTracks.Value.Add(track);

            Subject.IsSatisfiedBy(_local, null).Accepted.Should().BeFalse();
        }

        [TestCase(null)]
        [TestCase(new int[0])]
        public void absent_target_intent_should_never_bypass_existing_episode_safety(int[] targets)
        {
            _local.TargetQualityTrackIds = targets == null ? null : new List<int>(targets);
            _local.Series.QualityTracks.Value.Add(new SeriesQualityTrack { Id = 20, Enabled = true });

            Subject.IsSatisfiedBy(_local, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void unhydrated_series_tracks_should_preserve_legacy_safety()
        {
            _local.Series.QualityTracks = null;

            Subject.IsSatisfiedBy(_local, null).Accepted.Should().BeFalse();
        }
    }
}
