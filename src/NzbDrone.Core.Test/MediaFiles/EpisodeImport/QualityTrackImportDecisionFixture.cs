using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    public class QualityTrackImportDecisionFixture : CoreTest<ImportDecisionMaker>
    {
        private LocalEpisode _episode;
        private List<SeriesQualityTrack> _tracks;
        private Mock<IImportDecisionEngineSpecification> _specification;

        [SetUp]
        public void Setup()
        {
            var primary = new QualityProfile { Id = 1, Name = "HD", Items = Qualities.QualityFixture.GetDefaultQualities(Quality.HDTV720p) };
            var secondary = new QualityProfile { Id = 2, Name = "Full HD", Items = Qualities.QualityFixture.GetDefaultQualities(Quality.HDTV1080p) };
            var series = new Series { Id = 1, QualityProfileId = 1, QualityProfile = primary };
            _episode = new LocalEpisode
            {
                Series = series,
                Episodes = [new Episode { Id = 10, SeriesId = 1, EpisodeFileId = 9 }],
                Quality = new QualityModel(Quality.HDTV1080p)
            };
            _tracks =
            [
                new SeriesQualityTrack { Id = 1, SeriesId = 1, QualityProfileId = 1, IsPrimary = true, Enabled = true, QualityProfile = primary },
                new SeriesQualityTrack { Id = 2, SeriesId = 1, QualityProfileId = 2, Enabled = true, QualityProfile = secondary }
            ];
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.GetEnabledTracks(1)).Returns(() => _tracks);
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.GetForSeries(1)).Returns(new List<EpisodeTrackFile>());
            _specification = new Mock<IImportDecisionEngineSpecification>();
            _specification.Setup(s => s.IsSatisfiedBy(It.IsAny<LocalEpisode>(), It.IsAny<DownloadClientItem>())).Returns(ImportSpecDecision.Accept());
            Mocker.SetConstant<IEnumerable<IImportDecisionEngineSpecification>>([_specification.Object]);
        }

        [Test]
        public void should_select_matching_profile_without_changing_shared_series_or_episode()
        {
            Subject.GetDecision(_episode, null).Approved.Should().BeTrue();
            _episode.TargetQualityTrackIds.Should().Equal(2);
            _episode.Series.QualityProfileId.Should().Be(1);
            _episode.Episodes[0].EpisodeFileId.Should().Be(9);
            _specification.Verify(s => s.IsSatisfiedBy(It.Is<LocalEpisode>(e => e.Series.QualityProfileId == 2 && e.Episodes[0].EpisodeFileId == 0), null), Times.Once());
        }

        [Test]
        public void should_accept_overlapping_profiles_once_with_both_targets()
        {
            _tracks[0].QualityProfile = _tracks[1].QualityProfile;
            Subject.GetDecision(_episode, null).Approved.Should().BeTrue();
            _episode.TargetQualityTrackIds.Should().Equal(1, 2);
        }

        [Test]
        public void should_preserve_sole_profile_import_validation()
        {
            _tracks.RemoveAt(1);
            Subject.GetDecision(_episode, null).Approved.Should().BeTrue();
            _episode.TargetQualityTrackIds.Should().Equal(1);
            _specification.Verify(s => s.IsSatisfiedBy(It.IsAny<LocalEpisode>(), null), Times.Once());
        }

        [Test]
        public void should_not_redirect_explicit_target_to_another_matching_profile()
        {
            _episode.TargetQualityTrackIds = [1];
            Subject.GetDecision(_episode, null).Approved.Should().BeFalse();
            _episode.TargetQualityTrackIds.Should().Equal(1);
        }

        [Test]
        public void should_reject_empty_persisted_targets()
        {
            _episode.TargetQualityTrackIds = [];
            Subject.GetDecision(_episode, null).Approved.Should().BeFalse();
            _specification.Verify(s => s.IsSatisfiedBy(It.IsAny<LocalEpisode>(), null), Times.Never());
        }

        [Test]
        public void should_keep_valid_target_when_another_was_removed()
        {
            _episode.TargetQualityTrackIds = [2, 99];
            Subject.GetDecision(_episode, null).Approved.Should().BeTrue();
            _episode.TargetQualityTrackIds.Should().Equal(2);
        }

        [Test]
        public void should_restore_download_target_instead_of_selecting_by_file_quality()
        {
            var download = new DownloadClientItem { DownloadId = "queued" };
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find("queued")).Returns(new TrackedDownload
            {
                RemoteEpisode = new RemoteEpisode { TargetQualityTrackIds = [1] }
            });
            Subject.GetDecision(_episode, download).Approved.Should().BeFalse();
            _episode.TargetQualityTrackIds.Should().Equal(1);
        }

        [Test]
        public void should_use_legacy_primary_target_for_download_without_saved_intent()
        {
            var download = new DownloadClientItem { DownloadId = "legacy" };
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find("legacy")).Returns(new TrackedDownload
            {
                RemoteEpisode = new RemoteEpisode { LegacyQualityTrackTarget = true }
            });
            _episode.Quality = new QualityModel(Quality.HDTV720p);
            Subject.GetDecision(_episode, download).Approved.Should().BeTrue();
            _episode.TargetQualityTrackIds.Should().Equal(1);
        }

        [Test]
        public void should_evaluate_existing_file_with_track_local_ownership()
        {
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.GetForSeries(1)).Returns(
            [
                new EpisodeTrackFile { EpisodeId = 10, TrackId = 1, EpisodeFileId = 9 },
                new EpisodeTrackFile { EpisodeId = 10, TrackId = 2, EpisodeFileId = 12 }
            ]);
            Subject.GetDecision(_episode, null).Approved.Should().BeTrue();
            _specification.Verify(s => s.IsSatisfiedBy(It.Is<LocalEpisode>(e => e.Episodes[0].EpisodeFileId == 12), null), Times.Once());
            _episode.Episodes[0].EpisodeFileId.Should().Be(9);
        }

        [Test]
        public void should_preserve_unchanged_sole_profile_download_validation()
        {
            _tracks.RemoveAt(1);
            var download = new DownloadClientItem { DownloadId = "unchanged" };
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(download.DownloadId)).Returns(new TrackedDownload
            {
                RemoteEpisode = new RemoteEpisode
                {
                    TargetQualityTrackIds = [1],
                    TargetQualityTrackSignatures = new Dictionary<int, string> { [1] = QualityTrackSnapshot.ProfileSignature(_tracks[0].QualityProfile.Value) }
                }
            });

            Subject.GetDecision(_episode, download).Approved.Should().BeTrue();
        }

        [TestCase("previous criteria")]
        [TestCase(null)]
        public void should_revalidate_changed_or_missing_saved_criteria_for_sole_profile(string signature)
        {
            _tracks.RemoveAt(1);
            var download = new DownloadClientItem { DownloadId = "changed" };
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(download.DownloadId)).Returns(new TrackedDownload
            {
                RemoteEpisode = new RemoteEpisode
                {
                    TargetQualityTrackIds = [1],
                    TargetQualityTrackSignatures = signature == null ? null : new Dictionary<int, string> { [1] = signature }
                }
            });

            Subject.GetDecision(_episode, download).Approved.Should().BeFalse();
            _episode.TargetQualityTrackIds.Should().Equal(1);
        }

        [Test]
        public void should_preserve_migrated_legacy_sole_download_validation()
        {
            _tracks.RemoveAt(1);
            var download = new DownloadClientItem { DownloadId = "legacy" };
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(download.DownloadId)).Returns(new TrackedDownload
            {
                RemoteEpisode = new RemoteEpisode { TargetQualityTrackIds = [1], LegacyQualityTrackTarget = true }
            });

            Subject.GetDecision(_episode, download).Approved.Should().BeTrue();
        }

        [Test]
        public void should_assign_unique_matching_profile_for_unknown_queue_item()
        {
            var download = new DownloadClientItem { DownloadId = "unknown" };

            Subject.GetDecision(_episode, download).Approved.Should().BeTrue();

            _episode.TargetQualityTrackIds.Should().Equal(2);
            _episode.LegacyQualityTrackTarget.Should().BeFalse();
        }

        [Test]
        public void should_require_target_choice_when_unknown_queue_item_matches_multiple_profiles()
        {
            _tracks[0].QualityProfile = _tracks[1].QualityProfile;
            var download = new DownloadClientItem { DownloadId = "unknown" };

            Subject.GetDecision(_episode, download).Approved.Should().BeFalse();

            _episode.TargetQualityTrackIds.Should().BeEmpty();
            _episode.LegacyQualityTrackTarget.Should().BeFalse();
        }

        [Test]
        public void should_honor_explicit_shared_targets_for_unknown_queue_item()
        {
            _tracks[0].QualityProfile = _tracks[1].QualityProfile;
            _episode.TargetQualityTrackIds = [1, 2];
            var download = new DownloadClientItem { DownloadId = "unknown" };

            Subject.GetDecision(_episode, download).Approved.Should().BeTrue();

            _episode.TargetQualityTrackIds.Should().Equal(1, 2);
        }

        [Test]
        public void should_preserve_sole_profile_validation_when_unknown_queue_target_roundtrips()
        {
            _tracks.RemoveAt(1);
            _episode.TargetQualityTrackIds = [1];
            var download = new DownloadClientItem { DownloadId = "unknown" };

            Subject.GetDecision(_episode, download).Approved.Should().BeTrue();

            _episode.TargetQualityTrackIds.Should().Equal(1);
            _episode.LegacyQualityTrackTarget.Should().BeFalse();
        }

        [Test]
        public void should_preserve_explicit_target_when_ordinary_spec_rejects_metadata_edit()
        {
            _episode.Quality = new QualityModel(Quality.HDTV720p);
            _episode.TargetQualityTrackIds = [1];
            _specification.Setup(s => s.IsSatisfiedBy(It.IsAny<LocalEpisode>(), null)).Returns(ImportSpecDecision.Reject(ImportRejectionReason.NoAudio, "No audio tracks detected"));

            var decision = Subject.GetDecision(_episode, null);

            decision.Approved.Should().BeFalse();
            decision.Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.NoAudio);
            _episode.TargetQualityTrackIds.Should().Equal(1);
        }

        [Test]
        public void should_keep_automatic_sole_target_even_when_file_is_rejected()
        {
            _tracks.RemoveAt(1);
            _specification.Setup(s => s.IsSatisfiedBy(It.IsAny<LocalEpisode>(), null)).Returns(ImportSpecDecision.Reject(ImportRejectionReason.NoAudio, "No audio tracks detected"));

            Subject.GetDecision(_episode, null).Approved.Should().BeFalse();

            _episode.TargetQualityTrackIds.Should().Equal(1);
        }

        [Test]
        public void should_keep_unique_matching_target_for_manual_override_of_rejected_unknown_file()
        {
            _specification.Setup(s => s.IsSatisfiedBy(It.IsAny<LocalEpisode>(), null)).Returns(ImportSpecDecision.Reject(ImportRejectionReason.NoAudio, "No audio tracks detected"));

            Subject.GetDecision(_episode, null).Approved.Should().BeFalse();

            _episode.TargetQualityTrackIds.Should().Equal(2);
        }
    }
}
