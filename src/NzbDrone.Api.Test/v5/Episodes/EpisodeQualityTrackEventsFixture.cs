using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;
using NzbDrone.SignalR;
using NzbDrone.Test.Common;
using Sonarr.Api.V5.Episodes;
using Sonarr.Http;

namespace NzbDrone.Api.Test.v5.Episodes
{
    [TestFixture]
    public class EpisodeQualityTrackEventsFixture : TestBase<EpisodeController>
    {
        private Episode _episode;
        private NzbDrone.Core.Tv.Series _series;
        private EpisodeFile _file;
        private List<SignalRMessage> _messages;

        [SetUp]
        public void Setup()
        {
            var profile = new QualityProfile { Id = 1 };
            _series = new NzbDrone.Core.Tv.Series
            {
                Id = 1,
                Path = "/series",
                QualityProfile = profile,
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new SeriesQualityTrack { Id = 10, QualityProfileId = 1, IsPrimary = true, Enabled = true, QualityProfile = profile },
                    new SeriesQualityTrack { Id = 20, QualityProfileId = 2, Enabled = false, QualityProfile = new QualityProfile { Id = 2 } }
                }
            };
            _file = new EpisodeFile { Id = 100, SeriesId = 1, RelativePath = "episode.mkv", Quality = new QualityModel(Quality.HDTV720p) };
            var links = new List<EpisodeTrackFile>
            {
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 10, EpisodeFileId = 100, EpisodeFile = _file },
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 20, EpisodeFileId = 100, EpisodeFile = _file }
            };
            _episode = new Episode { Id = 1, SeriesId = 1, EpisodeFileId = 100, EpisodeFile = _file, TrackFiles = links };
            _file.TrackFiles = links;
            _file.Episodes = new List<Episode> { _episode };
            _messages = new List<SignalRMessage>();
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisode(1)).Returns(_episode);
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(_series);
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<EpisodeFile>(), _series)).Returns(new List<CustomFormat>());
            Mocker.GetMock<IBroadcastSignalRMessage>().SetupGet(s => s.IsConnected).Returns(true);
            Mocker.GetMock<IBroadcastSignalRMessage>().Setup(s => s.BroadcastMessage(It.IsAny<SignalRMessage>()))
                .Callback<SignalRMessage>(_messages.Add).Returns(Task.CompletedTask);
        }

        private EpisodeResource Resource()
        {
            _messages.Should().ContainSingle();
            _messages[0].Version.Should().Be(5);
            return ((ResourceChangeMessage<EpisodeResource>)_messages[0].Body).Resource;
        }

        [Test]
        public void grab_should_broadcast_current_ownership_instead_of_download_decision_projection()
        {
            var remote = new RemoteEpisode { Episodes = new List<Episode> { new Episode { Id = 1, SeriesId = 1, EpisodeFileId = 0 } } };

            Subject.Handle(new EpisodeGrabbedEvent(remote));

            var resource = Resource();
            resource.Grabbed.Should().BeTrue();
            resource.EpisodeFileId.Should().Be(100);
            resource.HasFile.Should().BeTrue();
            resource.QualityTracks.Should().ContainSingle(t => t.TrackId == 20 && !t.Enabled && t.EpisodeFileId == 100);
            resource.EpisodeFiles.Should().ContainSingle(f => f.Id == 100);
            using var json = JsonDocument.Parse(STJson.ToJson(_messages[0]));
            json.RootElement.GetProperty("body").GetProperty("resource").GetProperty("qualityTracks").GetArrayLength().Should().Be(2);
        }

        [Test]
        public void search_time_model_event_should_keep_computed_version_collections()
        {
            _episode.LastSearchTime = DateTime.UtcNow;

            Subject.Handle(new ModelEvent<Episode>(new Episode { Id = 1, SeriesId = 1 }, ModelAction.Updated));

            var resource = Resource();
            resource.LastSearchTime.Should().Be(_episode.LastSearchTime);
            resource.QualityTracks.Should().HaveCount(2);
            resource.EpisodeFiles.Should().ContainSingle(f => f.Id == 100);
        }

        [Test]
        public void imported_event_should_refresh_all_file_owners()
        {
            Subject.Handle(new EpisodeImportedEvent(new LocalEpisode { Episodes = new List<Episode> { new Episode { Id = 1, SeriesId = 1 } } }, _file, new List<DeletedEpisodeFile>(), true, null));

            Resource().EpisodeFiles.Single().QualityTrackIds.Should().Equal(10, 20);
        }

        [Test]
        public void file_deleted_event_should_explicitly_clear_files_and_refresh_missing_versions()
        {
            _episode.EpisodeFileId = 0;
            _episode.TrackFiles = new List<EpisodeTrackFile>();

            Subject.Handle(new EpisodeFileDeletedEvent(_file, DeleteMediaFileReason.Manual));

            var resource = Resource();
            resource.EpisodeFiles.Should().BeEmpty();
            resource.QualityTracks.Should().OnlyContain(t => !t.HasFile && t.EpisodeFileId == 0);
            using var json = JsonDocument.Parse(STJson.ToJson(resource));
            json.RootElement.GetProperty("episodeFiles").GetArrayLength().Should().Be(0);
        }

        [Test]
        public void plain_projection_should_omit_uncomputed_collections()
        {
            var resource = new Episode { Id = 1, SeriesId = 1 }.ToResource();
            resource.Grabbed = true;

            using var json = JsonDocument.Parse(STJson.ToJson(resource));

            json.RootElement.TryGetProperty("episodeFiles", out _).Should().BeFalse();
            json.RootElement.TryGetProperty("qualityTracks", out _).Should().BeFalse();
        }

        [Test]
        public void full_get_should_keep_explicit_empty_collections_when_no_ownership_exists()
        {
            _episode.EpisodeFileId = 0;
            _episode.TrackFiles = new List<EpisodeTrackFile>();
            _series.QualityTracks = new List<SeriesQualityTrack>();

            var response = Subject.GetResourceByIdWithErrorHandler(1);
            var resource = ((Microsoft.AspNetCore.Http.HttpResults.Ok<EpisodeResource>)response.Result).Value;
            using var json = JsonDocument.Parse(STJson.ToJson(resource));

            json.RootElement.GetProperty("episodeFiles").GetArrayLength().Should().Be(0);
            json.RootElement.GetProperty("qualityTracks").GetArrayLength().Should().Be(0);
        }

        [Test]
        public void disconnected_grab_should_not_load_ownership_or_broadcast()
        {
            Mocker.GetMock<IBroadcastSignalRMessage>().SetupGet(s => s.IsConnected).Returns(false);

            Subject.Handle(new EpisodeGrabbedEvent(new RemoteEpisode { Episodes = new List<Episode> { new Episode { Id = 1 } } }));

            _messages.Should().BeEmpty();
            Mocker.GetMock<IEpisodeService>().Verify(s => s.GetEpisode(It.IsAny<int>()), Times.Never());
        }
    }
}
