using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Test.Qualities;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.HistoryTests
{
    public class HistoryServiceFixture : CoreTest<HistoryService>
    {
        private QualityProfile _profile;
        private QualityProfile _profileCustom;

        [SetUp]
        public void Setup()
        {
            _profile = new QualityProfile
                {
                    Cutoff = Quality.WEBDL720p.Id,
                    Items = QualityFixture.GetDefaultQualities(),
                };

            _profileCustom = new QualityProfile
                {
                    Cutoff = Quality.WEBDL720p.Id,
                    Items = QualityFixture.GetDefaultQualities(Quality.DVD),
                };
        }

        [Test]
        public void should_preserve_grab_targets_in_history_for_each_episode()
        {
            var remoteEpisode = new RemoteEpisode
            {
                Episodes = Builder<Episode>.CreateListOfSize(2).Build().ToList(),
                Release = new ReleaseInfo(),
                ParsedEpisodeInfo = new ParsedEpisodeInfo(),
                TargetQualityTrackIds = new List<int> { 3, 7 },
                TargetQualityTrackSignatures = new Dictionary<int, string> { [3] = "profile-3", [7] = "profile-7" }
            };
            var histories = new List<EpisodeHistory>();
            Mocker.GetMock<IHistoryRepository>()
                .Setup(s => s.Insert(It.IsAny<EpisodeHistory>()))
                .Callback<EpisodeHistory>(histories.Add);

            Subject.Handle(new EpisodeGrabbedEvent(remoteEpisode));

            Assert.That(histories.Select(h => h.EpisodeId), Is.EquivalentTo(remoteEpisode.Episodes.Select(e => e.Id)));
            Assert.That(histories.All(h => STJson.Deserialize<List<int>>(h.Data["qualityTrackIds"]).SequenceEqual(new[] { 3, 7 })), Is.True);
            Assert.That(histories.All(h => STJson.Deserialize<Dictionary<int, string>>(h.Data["qualityTrackSignatures"])[7] == "profile-7"), Is.True);
        }

        [Test]
        public void should_record_only_new_targets_when_reusing_partially_imported_download()
        {
            var remoteEpisode = new RemoteEpisode
            {
                Episodes = new List<Episode> { new Episode { Id = 1 } },
                Release = new ReleaseInfo(),
                ParsedEpisodeInfo = new ParsedEpisodeInfo(),
                TargetQualityTrackIds = new List<int> { 3, 7 },
                TargetQualityTrackSignatures = new Dictionary<int, string> { [3] = "profile-3", [7] = "profile-7" }
            };
            EpisodeHistory recorded = null;
            Mocker.GetMock<IHistoryRepository>()
                .Setup(s => s.Insert(It.IsAny<EpisodeHistory>()))
                .Callback<EpisodeHistory>(h => recorded = h);

            Subject.Handle(new EpisodeGrabbedEvent(remoteEpisode) { NewQualityTrackIds = new List<int> { 7 } });

            Assert.That(STJson.Deserialize<List<int>>(recorded.Data["qualityTrackIds"]), Is.EqualTo(new[] { 7 }));
            Assert.That(STJson.Deserialize<Dictionary<int, string>>(recorded.Data["qualityTrackSignatures"]).Keys, Is.EqualTo(new[] { 7 }));
        }

        [Test]
        public void should_leave_legacy_grab_target_unspecified()
        {
            var remoteEpisode = new RemoteEpisode
            {
                Episodes = Builder<Episode>.CreateListOfSize(1).Build().ToList(),
                Release = new ReleaseInfo(),
                ParsedEpisodeInfo = new ParsedEpisodeInfo()
            };

            Subject.Handle(new EpisodeGrabbedEvent(remoteEpisode));

            Mocker.GetMock<IHistoryRepository>()
                .Verify(s => s.Insert(It.Is<EpisodeHistory>(h => !h.Data.ContainsKey("qualityTrackIds"))), Times.Once());
        }

        [Test]
        public void should_preserve_import_target_and_source_metadata_together()
        {
            var series = new Series { Id = 1, Path = TempFolder };
            var local = new LocalEpisode
            {
                Series = series,
                Episodes = new List<Episode> { new Episode { Id = 2, SeriesId = 1 } },
                Path = Path.Combine(series.Path, "source.mkv"),
                TargetQualityTrackIds = new List<int> { 10, 20 }
            };
            var file = new EpisodeFile { Id = 3, SeriesId = 1, RelativePath = "imported.mkv", SceneName = "Release" };
            var client = new DownloadClientItem { DownloadId = "fixture" };
            EpisodeHistory recorded = null;
            Mocker.GetMock<IHistoryRepository>()
                .Setup(s => s.Insert(It.IsAny<EpisodeHistory>()))
                .Callback<EpisodeHistory>(h => recorded = h);

            Subject.Handle(new EpisodeImportedEvent(local, file, new List<DeletedEpisodeFile>(), true, client));

            Assert.That(recorded.SourceTitle, Is.EqualTo("Release"));
            Assert.That(recorded.Data["DroppedPath"], Is.EqualTo(local.Path));
            Assert.That(STJson.Deserialize<List<int>>(recorded.Data["qualityTrackIds"]), Is.EqualTo(new[] { 10, 20 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_record_snapshot_ownership_for_each_affected_episode(bool rename)
        {
            var series = new Series { Id = 1, Path = TempFolder };
            var file = new EpisodeFile
            {
                Id = 3,
                SeriesId = 1,
                Series = series,
                RelativePath = "episode.mkv",
                Episodes = new List<Episode> { new Episode { Id = 1 }, new Episode { Id = 2 } },
                TrackFiles = new List<EpisodeTrackFile>
                {
                    new EpisodeTrackFile { EpisodeId = 1, TrackId = 10, EpisodeFileId = 3 },
                    new EpisodeTrackFile { EpisodeId = 1, TrackId = 20, EpisodeFileId = 3 },
                    new EpisodeTrackFile { EpisodeId = 2, TrackId = 20, EpisodeFileId = 3 }
                }
            };
            var histories = new List<EpisodeHistory>();
            Mocker.GetMock<IHistoryRepository>()
                .Setup(s => s.Insert(It.IsAny<EpisodeHistory>()))
                .Callback<EpisodeHistory>(histories.Add);

            if (rename)
            {
                Subject.Handle(new EpisodeFileRenamedEvent(series, file, Path.Combine(series.Path, "old.mkv")));
            }
            else
            {
                Subject.Handle(new EpisodeFileDeletedEvent(file, DeleteMediaFileReason.Upgrade));
            }

            Assert.That(histories, Has.Count.EqualTo(2));
            Assert.That(STJson.Deserialize<List<int>>(histories.Single(h => h.EpisodeId == 1).Data["qualityTrackIds"]), Is.EquivalentTo(new[] { 10, 20 }));
            Assert.That(STJson.Deserialize<List<int>>(histories.Single(h => h.EpisodeId == 2).Data["qualityTrackIds"]), Is.EqualTo(new[] { 20 }));
        }

        [Test]
        public void should_prefer_live_targets_when_recording_download_failure()
        {
            EpisodeHistory recorded = null;
            Mocker.GetMock<IHistoryRepository>()
                .Setup(s => s.Insert(It.IsAny<EpisodeHistory>()))
                .Callback<EpisodeHistory>(h => recorded = h);
            var tracked = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadClientInfo = new DownloadClientItemClientInfo { Name = "Fixture" } },
                RemoteEpisode = new RemoteEpisode { TargetQualityTrackIds = new List<int> { 10, 20 } }
            };

            Subject.Handle(new DownloadFailedEvent
            {
                EpisodeIds = new List<int> { 1 },
                TrackedDownload = tracked,
                Data = new Dictionary<string, string> { ["qualityTrackIds"] = "[30]" }
            });

            Assert.That(STJson.Deserialize<List<int>>(recorded.Data["qualityTrackIds"]), Is.EqualTo(new[] { 10, 20 }));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void should_preserve_stored_failure_targets_without_fabricating_legacy_intent(bool hasTrackedItem, bool hasStoredTargets)
        {
            EpisodeHistory recorded = null;
            Mocker.GetMock<IHistoryRepository>()
                .Setup(s => s.Insert(It.IsAny<EpisodeHistory>()))
                .Callback<EpisodeHistory>(h => recorded = h);
            var data = new Dictionary<string, string>();
            if (hasStoredTargets)
            {
                data["qualityTrackIds"] = "[20]";
            }

            var tracked = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadClientInfo = new DownloadClientItemClientInfo { Name = "Fixture" } }
            };
            Subject.Handle(new DownloadFailedEvent
            {
                EpisodeIds = new List<int> { 1 },
                Data = data,
                TrackedDownload = hasTrackedItem ? tracked : null
            });

            Assert.That(recorded.Data.ContainsKey("qualityTrackIds"), Is.EqualTo(hasStoredTargets));
            if (hasStoredTargets)
            {
                Assert.That(STJson.Deserialize<List<int>>(recorded.Data["qualityTrackIds"]), Is.EqualTo(new[] { 20 }));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_record_ignored_download_targets_only_when_known(bool knownTargets)
        {
            List<EpisodeHistory> recorded = null;
            Mocker.GetMock<IHistoryRepository>()
                .Setup(s => s.InsertMany(It.IsAny<IList<EpisodeHistory>>()))
                .Callback<IList<EpisodeHistory>>(h => recorded = h.ToList());

            var tracked = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem(),
                RemoteEpisode = new RemoteEpisode { TargetQualityTrackIds = new List<int> { 10, 20 } }
            };
            Subject.Handle(new DownloadIgnoredEvent
            {
                EpisodeIds = new List<int> { 1, 2 },
                DownloadClientInfo = new DownloadClientItemClientInfo { Name = "Fixture" },
                TrackedDownload = knownTargets ? tracked : null
            });

            Assert.That(recorded, Has.Count.EqualTo(2));
            Assert.That(recorded.All(h => h.Data.ContainsKey("qualityTrackIds") == knownTargets), Is.True);
            if (knownTargets)
            {
                Assert.That(recorded.All(h => STJson.Deserialize<List<int>>(h.Data["qualityTrackIds"]).SequenceEqual(new[] { 10, 20 })), Is.True);
            }
        }

        [Test]
        public void should_use_file_name_for_source_title_if_scene_name_is_null()
        {
            var series = Builder<Series>.CreateNew().Build();
            var episodes = Builder<Episode>.CreateListOfSize(1).Build().ToList();
            var episodeFile = Builder<EpisodeFile>.CreateNew()
                                                  .With(f => f.SceneName = null)
                                                  .Build();

            var localEpisode = new LocalEpisode
                               {
                                   Series = series,
                                   Episodes = episodes,
                                   Path = @"C:\Test\Unsorted\Series.s01e01.mkv"
                               };

            var downloadClientItem = new DownloadClientItem
                                     {
                                         DownloadClientInfo = new DownloadClientItemClientInfo
                                         {
                                             Protocol = DownloadProtocol.Usenet,
                                             Id = 1,
                                             Name = "sab"
                                         },
                                         DownloadId = "abcd"
                                     };

            Subject.Handle(new EpisodeImportedEvent(localEpisode, episodeFile, new List<DeletedEpisodeFile>(), true, downloadClientItem));

            Mocker.GetMock<IHistoryRepository>()
                .Verify(v => v.Insert(It.Is<EpisodeHistory>(h => h.SourceTitle == Path.GetFileNameWithoutExtension(localEpisode.Path))));
        }
    }
}
