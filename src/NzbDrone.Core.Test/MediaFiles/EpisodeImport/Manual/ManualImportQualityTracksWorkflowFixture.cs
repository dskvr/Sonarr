using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.EpisodeImport.Aggregation;
using NzbDrone.Core.MediaFiles.EpisodeImport.Manual;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport.Manual
{
    [TestFixture]
    public class ManualImportQualityTracksWorkflowFixture : CoreTest<ManualImportService>
    {
        private Series _series;
        private List<Episode> _episodes;
        private List<SeriesQualityTrack> _tracks;
        private List<EpisodeTrackFile> _links;
        private List<EpisodeFile> _files;
        private List<IImportDecisionEngineSpecification> _specifications;
        private TrackedDownload _tracked;
        private LocalEpisode _lastLocal;
        private LocalEpisode _importedLocal;
        private string _downloadPath;

        [SetUp]
        public void Setup()
        {
            var profile = new QualityProfile { Id = 1, Name = "HD", Items = Qualities.QualityFixture.GetDefaultQualities(Quality.HDTV720p) };
            _series = new Series { Id = 1, Title = "Test Show", Path = @"C:\Library\Test Show".AsOsAgnostic(), QualityProfileId = 1, QualityProfile = profile };
            _episodes =
            [
                new Episode { Id = 100, SeriesId = 1, SeasonNumber = 1, EpisodeNumber = 1 },
                new Episode { Id = 101, SeriesId = 1, SeasonNumber = 1, EpisodeNumber = 2 }
            ];
            _tracks = [new SeriesQualityTrack { Id = 10, SeriesId = 1, QualityProfileId = 1, IsPrimary = true, Enabled = true, QualityProfile = profile }];
            _series.QualityTracks = _tracks;
            _links = [];
            _files = [];
            _specifications = [];
            _tracked = null;
            _lastLocal = null;
            _importedLocal = null;
            _downloadPath = @"C:\Downloads\Test.Show.S01E01.720p.HDTV-GROUP.mkv".AsOsAgnostic();

            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(_series);
            Mocker.GetMock<IParsingService>().Setup(s => s.GetSeries(It.IsAny<string>())).Returns(_series);
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>())).Returns<IEnumerable<int>>(ids => _episodes.Where(e => ids.Contains(e.Id)).ToList());
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodeBySeries(1)).Returns(_episodes);
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.GetEnabledTracks(1)).Returns(() => _tracks.Where(t => t.Enabled).ToList());
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.GetForSeries(1)).Returns(_links);
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.GetForFile(It.IsAny<int>())).Returns<int>(id => _links.Where(l => l.EpisodeFileId == id).ToList());
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(_files);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeason(1, It.IsAny<int>())).Returns<int, int>((_, season) => _files.Where(f => f.SeasonNumber == season).ToList());
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesWithRelativePath(1, It.IsAny<string>())).Returns<int, string>((_, path) => _files.Where(f => f.RelativePath == path).ToList());
            Mocker.GetMock<IMediaFileService>().Setup(s => s.FilterExistingFiles(It.IsAny<List<string>>(), _series)).Returns<List<string>, Series>((paths, _) => paths);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFileSize(It.IsAny<string>())).Returns(1234);
            Mocker.GetMock<IAppFolderInfo>().SetupGet(s => s.AppDataFolder).Returns(Path.Combine(TempFolder, "manual-appdata"));
            Mocker.GetMock<IDiskScanService>().Setup(s => s.GetVideoFiles(It.IsAny<string>(), It.IsAny<bool>())).Returns(Array.Empty<string>());
            Mocker.GetMock<IDiskScanService>().Setup(s => s.FilterPaths(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), true)).Returns<string, IEnumerable<string>, bool>((_, paths, _) => paths.ToList());
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(It.IsAny<string>())).Returns(() => _tracked);
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId(It.IsAny<string>())).Returns(new List<EpisodeHistory>());
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<EpisodeFile>(), _series)).Returns(new List<CustomFormat>());
            Mocker.GetMock<ILocalEpisodeCustomFormatCalculationService>().Setup(s => s.ParseEpisodeCustomFormats(It.IsAny<LocalEpisode>())).Returns(new List<CustomFormat>());
            Mocker.GetMock<IAggregationService>().Setup(s => s.Augment(It.IsAny<LocalEpisode>(), It.IsAny<DownloadClientItem>())).Returns<LocalEpisode, DownloadClientItem>((local, _) =>
            {
                local.Episodes = local.Episodes.Count == 0 ? [_episodes[0]] : local.Episodes;
                local.Quality ??= local.FileEpisodeInfo?.Quality ?? new QualityModel(Quality.HDTV720p);
                local.Languages = [Language.English];
                _lastLocal = local;
                return local;
            });
            Mocker.GetMock<IUpgradeMediaFiles>().Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>())).Returns<EpisodeFile, LocalEpisode, bool>((file, local, _) =>
            {
                file.Id = 900;
                file.RelativePath = Path.GetFileName(local.Path);
                _importedLocal = local;
                return new EpisodeFileMoveResult { EpisodeFile = file };
            });
            Mocker.SetConstant<IEnumerable<IImportDecisionEngineSpecification>>(_specifications);
            Mocker.SetConstant<IMakeImportDecision>(Mocker.Resolve<ImportDecisionMaker>());
            Mocker.SetConstant<IImportApprovedEpisodes>(Mocker.Resolve<ImportApprovedEpisodes>());
        }

        private ManualImportItem Reprocess(List<int> targets = null, string downloadId = null, Quality quality = null, bool resetTargets = false)
        {
            return Subject.ReprocessItem(_downloadPath, downloadId, 1, null, [100], "MANUAL", new QualityModel(quality ?? Quality.HDTV720p), [Language.English], 0, ReleaseType.SingleEpisode, targets, resetTargets);
        }

        private void GivenAdditionalTrack(bool enabled = true)
        {
            var profile = new QualityProfile { Id = 2, Name = "Full HD", Items = Qualities.QualityFixture.GetDefaultQualities(Quality.HDTV1080p) };
            _tracks.Add(new SeriesQualityTrack { Id = 20, SeriesId = 1, QualityProfileId = 2, Enabled = enabled, QualityProfile = profile });
        }

        private void GivenTrackedDownload(List<int> targets, Dictionary<int, string> signatures = null)
        {
            var item = new DownloadClientItem { DownloadId = "tracked", Title = "Test.Show.S01E01.720p.HDTV-GROUP", OutputPath = new OsPath(_downloadPath), CanMoveFiles = true };
            _tracked = new TrackedDownload
            {
                DownloadItem = item,
                ImportItem = item,
                RemoteEpisode = new RemoteEpisode { Series = _series, Episodes = [_episodes[0]], TargetQualityTrackIds = targets, TargetQualityTrackSignatures = signatures },
                State = TrackedDownloadState.ImportPending
            };
        }

        private ManualImportCommand Command(List<int> targets = null, string downloadId = null, string path = null)
        {
            return new ManualImportCommand
            {
                ImportMode = ImportMode.Copy,
                Files = [new ManualImportFile { Path = path ?? _downloadPath, SeriesId = 1, EpisodeIds = [100], Quality = new QualityModel(Quality.HDTV720p), Languages = [Language.English], TargetQualityTrackIds = targets, DownloadId = downloadId }]
            };
        }

        [TestCase(0, null)]
        [TestCase(1, 50)]
        [TestCase(2, null)]
        public void metadata_reprocess_should_preserve_only_unique_persisted_file_identity(int matchingFiles, int? expectedId)
        {
            _downloadPath = Path.Combine(_series.Path, "metadata.mkv");
            for (var index = 0; index < matchingFiles; index++)
            {
                _files.Add(new EpisodeFile { Id = 50 + index, SeriesId = 1, RelativePath = "metadata.mkv" });
            }

            var rejection = new Mock<IImportDecisionEngineSpecification>();
            rejection.Setup(s => s.IsSatisfiedBy(It.IsAny<LocalEpisode>(), It.IsAny<DownloadClientItem>()))
                .Returns(ImportSpecDecision.Reject(ImportRejectionReason.NoAudio, "No audio tracks detected"));
            _specifications.Add(rejection.Object);

            var item = Reprocess([10]);

            item.EpisodeFileId.Should().Be(expectedId);
            item.TargetQualityTrackIds.Should().Equal(10);
            item.Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.NoAudio);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(It.IsAny<EpisodeFile>()), Times.Never());
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void sole_profile_reprocess_should_select_target_and_capture_file_state_without_prompt()
        {
            _links.Add(new EpisodeTrackFile { EpisodeId = 100, TrackId = 10, EpisodeFileId = 50 });

            var item = Reprocess();

            item.TargetQualityTrackIds.Should().Equal(10);
            item.Rejections.Should().BeEmpty();
            _lastLocal.TargetQualityTrackSignatures[10].Should().Be(QualityTrackSnapshot.ProfileSignature(_tracks[0].QualityProfile.Value));
            _lastLocal.ExpectedTrackFiles.Should().ContainSingle(l => l.EpisodeId == 100 && l.TrackId == 10 && l.EpisodeFileId == 50);
        }

        [Test]
        public void sole_manual_reprocess_should_keep_existing_quality_override_behavior()
        {
            Reprocess(quality: Quality.HDTV1080p).Rejections.Should().BeEmpty();
            _lastLocal.TargetQualityTrackIds.Should().Equal(10);
        }

        [Test]
        public void disabled_additional_track_should_not_become_automatic_target()
        {
            GivenAdditionalTrack(false);

            Reprocess().TargetQualityTrackIds.Should().Equal(10);
        }

        [Test]
        public void explicit_additional_target_should_survive_reprocess_and_preserve_primary_profile()
        {
            GivenAdditionalTrack();

            var item = Reprocess([20], quality: Quality.HDTV1080p);

            item.TargetQualityTrackIds.Should().Equal(20);
            item.Rejections.Should().BeEmpty();
            item.Series.QualityProfileId.Should().Be(1);
            item.ReleaseGroup.Should().Be("MANUAL");
        }

        [TestCase(new int[0])]
        [TestCase(new[] { 999 })]
        public void unknown_or_empty_targets_should_reject_without_selecting_another_profile(int[] targets)
        {
            var item = Reprocess(targets.ToList());

            item.TargetQualityTrackIds.Should().BeEmpty();
            item.Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.DecisionError);
        }

        [Test]
        public void disabled_explicit_target_should_reject()
        {
            GivenAdditionalTrack(false);

            Reprocess([20]).Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.DecisionError);
        }

        [Test]
        public void reprocess_should_reject_episodes_from_another_series()
        {
            _episodes[0].SeriesId = 2;

            Reprocess([10]).Rejections.Should().NotBeEmpty();
        }

        [Test]
        public void persisted_download_target_should_win_over_new_quality_matching()
        {
            GivenAdditionalTrack();
            GivenTrackedDownload([10]);

            var item = Reprocess(downloadId: "tracked", quality: Quality.HDTV1080p);

            item.TargetQualityTrackIds.Should().Equal(10);
            item.Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.NotQualityUpgrade);
        }

        [Test]
        public void reprocess_should_keep_valid_queued_target_when_another_was_removed()
        {
            GivenAdditionalTrack(false);
            GivenTrackedDownload([10, 20], new Dictionary<int, string> { [10] = QualityTrackSnapshot.ProfileSignature(_tracks[0].QualityProfile.Value) });

            var item = Reprocess(downloadId: "tracked");

            item.TargetQualityTrackIds.Should().Equal(10);
            item.Rejections.Should().BeEmpty();
            _tracked.RemoteEpisode.TargetQualityTrackIds.Should().Equal(10, 20);
        }

        [Test]
        public void unchanged_persisted_sole_criteria_should_keep_previous_import_validation()
        {
            GivenTrackedDownload([10], new Dictionary<int, string> { [10] = QualityTrackSnapshot.ProfileSignature(_tracks[0].QualityProfile.Value) });

            Reprocess(downloadId: "tracked", quality: Quality.HDTV1080p).Rejections.Should().BeEmpty();
            _lastLocal.TargetQualityTrackIds.Should().Equal(10);
        }

        [Test]
        public void automatically_selected_sole_target_should_keep_cached_criteria_during_reprocess()
        {
            GivenTrackedDownload([10], new Dictionary<int, string> { [10] = QualityTrackSnapshot.ProfileSignature(_tracks[0].QualityProfile.Value) });

            var item = Reprocess([10], "tracked", Quality.HDTV1080p);

            item.Rejections.Should().BeEmpty();
            item.TargetQualityTrackIds.Should().Equal(10);
        }

        [Test]
        public void explicit_series_reselection_should_reset_stale_target_and_auto_select_sole_profile()
        {
            GivenAdditionalTrack(false);
            GivenTrackedDownload([20]);

            var item = Reprocess(downloadId: "tracked", resetTargets: true);

            item.TargetQualityTrackIds.Should().Equal(10);
            item.Rejections.Should().BeEmpty();
            _lastLocal.TargetQualityTrackSignatures[10].Should().Be(QualityTrackSnapshot.ProfileSignature(_tracks[0].QualityProfile.Value));
            _tracked.RemoteEpisode.TargetQualityTrackIds.Should().Equal(20);
        }

        [Test]
        public void series_reselection_should_recover_stale_target_while_reparsing_cleared_episode_selection()
        {
            GivenAdditionalTrack(false);
            GivenTrackedDownload([20]);

            var item = Subject.ReprocessItem(_downloadPath, "tracked", 1, null, [], null, new QualityModel(Quality.Unknown), [Language.Unknown], 0, ReleaseType.Unknown, null, true);

            item.Rejections.Should().BeEmpty();
            item.TargetQualityTrackIds.Should().Equal(10);
            item.Episodes.Should().ContainSingle(e => e.Id == 100);
            _tracked.RemoteEpisode.TargetQualityTrackIds.Should().Equal(20);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void reselecting_series_for_existing_file_should_keep_identity_with_cleared_episodes(bool parsedEpisodes)
        {
            _downloadPath = Path.Combine(_series.Path, "Test.Show.S01E01.720p.HDTV-GROUP.mkv");
            _files.Add(new EpisodeFile { Id = 50, SeriesId = 1, RelativePath = Path.GetFileName(_downloadPath) });
            _links.Add(new EpisodeTrackFile { EpisodeId = 100, TrackId = 10, EpisodeFileId = 50 });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.FilterExistingFiles(It.IsAny<List<string>>(), _series))
                .Returns<List<string>, Series>((paths, series) => MediaFileService.FilterExistingFiles(paths, _files, series));
            MediaFileService.FilterExistingFiles([_downloadPath], _files, _series).Should().BeEmpty();

            if (!parsedEpisodes)
            {
                Mocker.GetMock<IAggregationService>().Setup(s => s.Augment(It.IsAny<LocalEpisode>(), It.IsAny<DownloadClientItem>()))
                    .Returns<LocalEpisode, DownloadClientItem>((local, _) => local);
            }

            var item = Subject.ReprocessItem(_downloadPath, null, 1, null, [], null, new QualityModel(Quality.Unknown), [Language.Unknown], 0, ReleaseType.Unknown, null, true);

            item.Series.Should().BeSameAs(_series);
            item.EpisodeFileId.Should().Be(50);
            if (parsedEpisodes)
            {
                item.Episodes.Should().ContainSingle(e => e.Id == 100);
                item.TargetQualityTrackIds.Should().Equal(10);
                item.Rejections.Should().BeEmpty();
            }
            else
            {
                item.Episodes.Should().BeNullOrEmpty();
                item.TargetQualityTrackIds.Should().BeNullOrEmpty();
                item.Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.InvalidSeasonOrEpisode);
            }

            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(It.IsAny<EpisodeFile>()), Times.Never());
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()), Times.Never());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void retained_disabled_owner_should_be_reselected_only_after_explicit_reset(bool resetTargets)
        {
            GivenAdditionalTrack(false);
            _downloadPath = Path.Combine(_series.Path, "Test.Show.S01E01.2160p.HDTV-GROUP.mkv");
            _tracks[1].QualityProfile.Value.Items = Qualities.QualityFixture.GetDefaultQualities(Quality.HDTV2160p);
            _files.Add(new EpisodeFile { Id = 50, SeriesId = 1, RelativePath = Path.GetFileName(_downloadPath), Quality = new QualityModel(Quality.HDTV2160p) });
            _files.Add(new EpisodeFile { Id = 51, SeriesId = 1, RelativePath = "primary.mkv", Quality = new QualityModel(Quality.HDTV720p) });
            _links.Add(new EpisodeTrackFile { EpisodeId = 100, TrackId = 20, EpisodeFileId = 50 });
            _links.Add(new EpisodeTrackFile { EpisodeId = 100, TrackId = 10, EpisodeFileId = 51 });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.FilterExistingFiles(It.IsAny<List<string>>(), _series))
                .Returns<List<string>, Series>((paths, series) => MediaFileService.FilterExistingFiles(paths, _files, series));

            var item = Subject.ReprocessItem(_downloadPath, null, 1, null, [], null, new QualityModel(Quality.Unknown), [Language.Unknown], 0, ReleaseType.Unknown, null, resetTargets);

            item.Series.Should().BeSameAs(_series);
            item.EpisodeFileId.Should().Be(50);
            item.Episodes.Should().ContainSingle(e => e.Id == 100);
            if (resetTargets)
            {
                item.TargetQualityTrackIds.Should().Equal(10);
                item.Rejections.Should().BeEmpty();
            }
            else
            {
                item.TargetQualityTrackIds.Should().BeEmpty();
                item.Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.DecisionError);
            }

            _links.Should().HaveCount(2);
            _links.Should().ContainSingle(l => l.EpisodeId == 100 && l.TrackId == 20 && l.EpisodeFileId == 50);
            _links.Should().ContainSingle(l => l.EpisodeId == 100 && l.TrackId == 10 && l.EpisodeFileId == 51);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(It.IsAny<EpisodeFile>()), Times.Never());
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void omitted_reset_while_reparsing_should_preserve_download_intent()
        {
            GivenAdditionalTrack(false);
            GivenTrackedDownload([20]);

            var item = Subject.ReprocessItem(_downloadPath, "tracked", 1, null, [], null, new QualityModel(Quality.Unknown), [Language.Unknown], 0, ReleaseType.Unknown);

            item.TargetQualityTrackIds.Should().BeEmpty();
            item.Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.DecisionError);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void reparsing_missing_episode_selection_should_not_ignore_explicit_invalid_targets(bool resetTargets)
        {
            var item = Subject.ReprocessItem(_downloadPath, null, 1, null, [], null, new QualityModel(Quality.Unknown), [Language.Unknown], 0, ReleaseType.Unknown, [999], resetTargets);

            item.TargetQualityTrackIds.Should().BeEmpty();
            item.Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.DecisionError);
        }

        [Test]
        public void reparsing_missing_episode_selection_should_preserve_explicit_matching_profile()
        {
            GivenAdditionalTrack();
            _downloadPath = @"C:\Downloads\Test.Show.S01E01.1080p.HDTV-GROUP.mkv".AsOsAgnostic();

            var item = Subject.ReprocessItem(_downloadPath, null, 1, null, [], null, new QualityModel(Quality.Unknown), [Language.Unknown], 0, ReleaseType.Unknown, [20]);

            item.Rejections.Should().BeEmpty();
            item.TargetQualityTrackIds.Should().Equal(20);
            item.Episodes.Should().ContainSingle(e => e.Id == 100);
        }

        [Test]
        public void explicit_same_series_reselection_should_restore_normal_sole_manual_quality_handling()
        {
            GivenTrackedDownload([10], new Dictionary<int, string> { [10] = "old criteria" });

            var item = Reprocess(downloadId: "tracked", quality: Quality.HDTV1080p, resetTargets: true);

            item.Rejections.Should().BeEmpty();
            item.TargetQualityTrackIds.Should().Equal(10);
            _lastLocal.TargetQualityTrackSignatures[10].Should().Be(QualityTrackSnapshot.ProfileSignature(_tracks[0].QualityProfile.Value));
        }

        [Test]
        public void explicit_reset_should_match_current_multi_profile_criteria()
        {
            GivenAdditionalTrack();
            GivenTrackedDownload([10]);

            var item = Reprocess(downloadId: "tracked", quality: Quality.HDTV1080p, resetTargets: true);

            item.TargetQualityTrackIds.Should().Equal(20);
            item.Rejections.Should().BeEmpty();
        }

        [Test]
        public void explicit_reset_should_not_bypass_invalid_new_target_selection()
        {
            GivenTrackedDownload([10]);

            Reprocess([999], "tracked", resetTargets: true).Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.DecisionError);
        }

        [TestCase(null)]
        [TestCase("old profile criteria")]
        public void stale_persisted_sole_criteria_should_be_revalidated(string signature)
        {
            GivenTrackedDownload([10], signature == null ? null : new Dictionary<int, string> { [10] = signature });

            var item = Reprocess(downloadId: "tracked", quality: Quality.HDTV1080p);

            item.TargetQualityTrackIds.Should().Equal(10);
            item.Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.NotQualityUpgrade);
        }

        [Test]
        public void legacy_download_without_targets_should_keep_primary_manual_behavior()
        {
            GivenTrackedDownload(null);
            _tracked.RemoteEpisode.LegacyQualityTrackTarget = true;

            Reprocess(downloadId: "tracked", quality: Quality.HDTV1080p).TargetQualityTrackIds.Should().Equal(10);
            _lastLocal.LegacyQualityTrackTarget.Should().BeTrue();
        }

        [Test]
        public void unknown_download_without_targets_should_not_claim_legacy_provenance()
        {
            GivenTrackedDownload(null);

            Reprocess(downloadId: "tracked").TargetQualityTrackIds.Should().Equal(10);

            _lastLocal.LegacyQualityTrackTarget.Should().BeFalse();
        }

        [Test]
        public void rejected_metadata_reprocess_should_preserve_valid_file_targets_for_manual_edit()
        {
            _downloadPath = Path.Combine(_series.Path, "existing.mkv");
            _files.Add(new EpisodeFile { Id = 50, SeriesId = 1, RelativePath = "existing.mkv" });
            _links.Add(new EpisodeTrackFile { EpisodeId = 100, TrackId = 10, EpisodeFileId = 50 });
            var noAudio = new Mock<IImportDecisionEngineSpecification>();
            noAudio.Setup(s => s.IsSatisfiedBy(It.IsAny<LocalEpisode>(), It.IsAny<DownloadClientItem>()))
                .Returns(ImportSpecDecision.Reject(ImportRejectionReason.NoAudio, "Video file does not contain an audio stream"));
            _specifications.Add(noAudio.Object);

            var item = Reprocess([10]);

            item.TargetQualityTrackIds.Should().Equal(10);
            item.Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.NoAudio);
            item.ReleaseGroup.Should().Be("MANUAL");
            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.UpdateFile(It.IsAny<EpisodeFile>(), It.IsAny<List<EpisodeTrackFile>>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void file_scan_should_return_auto_selected_sole_target()
        {
            var item = Subject.GetMediaFiles(_downloadPath, null, 1, true).Single();

            item.TargetQualityTrackIds.Should().Equal(10);
            item.Episodes.Should().ContainSingle(e => e.Id == 100);
            item.RelativePath.Should().Be(Path.GetFileName(_downloadPath));
        }

        [Test]
        public void tracked_file_scan_should_restore_persisted_target()
        {
            GivenAdditionalTrack();
            GivenTrackedDownload([10], new Dictionary<int, string> { [10] = QualityTrackSnapshot.ProfileSignature(_tracks[0].QualityProfile.Value) });

            Subject.GetMediaFiles(null, "tracked", null, true).Single().TargetQualityTrackIds.Should().Equal(10);
        }

        [Test]
        public void missing_download_or_missing_path_should_return_empty_choices()
        {
            Subject.GetMediaFiles(null, "missing", null, true).Should().BeEmpty();
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(_downloadPath)).Returns(false);
            Subject.GetMediaFiles(_downloadPath, null, null, true).Should().BeEmpty();
        }

        [Test]
        public void folder_scan_should_map_track_aware_decisions()
        {
            var folder = Path.GetDirectoryName(_downloadPath);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(folder)).Returns(true);
            Mocker.GetMock<IDiskScanService>().Setup(s => s.GetVideoFiles(folder, true)).Returns(new[] { _downloadPath });

            var item = Subject.GetMediaFiles(folder, null, 1, false).Single();

            item.TargetQualityTrackIds.Should().Equal(10);
            item.FolderName.Should().Be(new DirectoryInfo(folder).Name);
        }

        [Test]
        public void retained_shared_file_should_remain_discoverable_through_all_ownership_links()
        {
            GivenAdditionalTrack(false);
            _episodes[0].EpisodeFileId = 99;
            var file = new EpisodeFile { Id = 50, SeriesId = 1, SeasonNumber = 1, RelativePath = "retained.mkv", Path = Path.Combine(_series.Path, "retained.mkv"), Quality = new QualityModel(Quality.HDTV1080p) };
            _links.AddRange(new[] { new EpisodeTrackFile { EpisodeId = 100, TrackId = 20, EpisodeFileId = 50 }, new EpisodeTrackFile { EpisodeId = 101, TrackId = 20, EpisodeFileId = 50 } });
            file.TrackFiles = _links;
            _files.Add(file);

            var item = Subject.GetMediaFiles(1, 1).Single();

            item.EpisodeFileId.Should().Be(50);
            item.Episodes.Select(e => e.Id).Should().Equal(100, 101);
            item.TargetQualityTrackIds.Should().Equal(20);
        }

        [Test]
        public void file_edit_without_loaded_links_should_preserve_legacy_episode_mapping()
        {
            _episodes[0].EpisodeFileId = 50;
            _files.Add(new EpisodeFile { Id = 50, SeriesId = 1, SeasonNumber = 1, RelativePath = "legacy.mkv", Path = Path.Combine(_series.Path, "legacy.mkv"), Quality = new QualityModel(Quality.HDTV720p) });

            var item = Subject.GetMediaFiles(1, 1).Single();

            item.Episodes.Should().ContainSingle(e => e.Id == 100);
            item.TargetQualityTrackIds.Should().BeNull();
        }

        [Test]
        public void library_scan_should_keep_unmapped_files_available_for_manual_matching()
        {
            var unmapped = Path.Combine(_series.Path, "unknown.mkv");
            Mocker.GetMock<IDiskScanService>().Setup(s => s.GetVideoFiles(_series.Path, true)).Returns(new[] { unmapped });

            var item = Subject.GetMediaFiles(1, null).Single();

            item.Path.Should().Be(unmapped);
            item.TargetQualityTrackIds.Should().BeNull();
            item.Episodes.Should().BeEmpty();
        }

        [Test]
        public void season_selection_without_episodes_should_remain_rejected()
        {
            var item = Subject.ReprocessItem(_downloadPath, null, 1, 1, [], null, new QualityModel(Quality.Unknown), [Language.Unknown], 0, ReleaseType.Unknown);

            item.Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.NoEpisodes);
            item.TargetQualityTrackIds.Should().BeNull();
        }

        [Test]
        public void reprocess_without_episode_selection_should_recover_file_mapping_and_sole_target()
        {
            var item = Subject.ReprocessItem(_downloadPath, null, 1, null, [], null, new QualityModel(Quality.Unknown), [Language.Unknown], 0, ReleaseType.Unknown);

            item.TargetQualityTrackIds.Should().Equal(10);
            item.Episodes.Should().ContainSingle(e => e.Id == 100);
        }

        [Test]
        public void manual_command_should_auto_select_primary_and_emit_completion_after_success()
        {
            Subject.Execute(Command());

            _importedLocal.ManualImport.Should().BeTrue();
            _importedLocal.TargetQualityTrackIds.Should().Equal(10);
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.Is<UntrackedDownloadCompletedEvent>(e => e.Series.Id == 1 && e.Episodes.Single().Id == 100)), Times.Once());
        }

        [Test]
        public void manual_command_should_preserve_explicit_target_instead_of_cached_download_target()
        {
            GivenAdditionalTrack();
            GivenTrackedDownload([10]);

            Subject.Execute(Command([20], "tracked"));

            _importedLocal.TargetQualityTrackIds.Should().Equal(20);
            _tracked.State.Should().Be(TrackedDownloadState.Imported);
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());
        }

        [Test]
        public void manual_command_without_selection_should_preserve_cached_targets()
        {
            GivenAdditionalTrack();
            GivenTrackedDownload([20]);

            Subject.Execute(Command(downloadId: "tracked"));

            _importedLocal.TargetQualityTrackIds.Should().Equal(20);
        }

        [Test]
        public void existing_shared_file_remapping_should_keep_all_existing_target_owners()
        {
            GivenAdditionalTrack(false);
            var path = Path.Combine(_series.Path, "retained.mkv");
            _files.Add(new EpisodeFile { Id = 50, SeriesId = 1, RelativePath = "retained.mkv" });
            _links.Add(new EpisodeTrackFile { EpisodeId = 101, TrackId = 20, EpisodeFileId = 50 });

            Subject.Execute(Command(path: path));

            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.UpdateFile(It.Is<EpisodeFile>(f => f.Id == 50), It.Is<List<EpisodeTrackFile>>(links => links.Count == 1 && links[0].EpisodeId == 100 && links[0].TrackId == 20), true), Times.Once());
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void disabled_command_target_should_fail_before_any_file_transfer()
        {
            GivenAdditionalTrack(false);
            var upgrader = Mocker.Resolve<UpgradeMediaFileService>();
            Exception targetError = null;
            Mocker.GetMock<IUpgradeMediaFiles>().Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()))
                .Returns<EpisodeFile, LocalEpisode, bool>((file, local, copyOnly) =>
                {
                    try
                    {
                        return upgrader.UpgradeEpisodeFile(file, local, copyOnly);
                    }
                    catch (Exception e)
                    {
                        targetError = e;
                        throw;
                    }
                });

            Subject.Execute(Command([20]));

            Mocker.GetMock<IDiskTransferService>().Verify(s => s.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<UntrackedDownloadCompletedEvent>()), Times.Never());
            targetError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("no longer enabled");
            ExceptionVerification.ExpectedWarns(1);
        }

        [TestCase(new int[0], false)]
        [TestCase(new[] { 999 }, false)]
        [TestCase(new[] { 100, 999 }, false)]
        [TestCase(new[] { 100 }, true)]
        public void invalid_command_episode_selection_should_fail_before_augmentation_or_import(int[] episodeIds, bool foreignSeries)
        {
            if (foreignSeries)
            {
                _episodes[0].SeriesId = 2;
            }

            var command = Command([10]);
            command.Files[0].EpisodeIds = episodeIds.ToList();

            Assert.Throws<InvalidOperationException>(() => Subject.Execute(command)).Message.Should().Be("Selected episodes must exist and belong to the selected series.");

            _lastLocal.Should().BeNull();
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<UntrackedDownloadCompletedEvent>()), Times.Never());
        }
    }
}
