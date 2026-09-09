using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles
{
    public class MediaFileServiceDeletionFixture : DbTest<MediaFileService, EpisodeFile>
    {
        private Series _series;
        private List<Episode> _episodes;
        private List<EpisodeFile> _files;
        private List<SeriesQualityTrack> _tracks;

        [SetUp]
        public void Setup()
        {
            Mocker.SetConstant<IMediaFileRepository>(Mocker.Resolve<MediaFileRepository>());
            Mocker.SetConstant<IEpisodeTrackFileRepository>(Mocker.Resolve<EpisodeTrackFileRepository>());
            Mocker.SetConstant<IEpisodeTrackFileService>(Mocker.Resolve<EpisodeTrackFileService>());
            _series = Db.Insert(Builder<Series>.CreateNew().With(s => s.Path = Path.Combine(TempFolder, "series")).BuildNew());
            _episodes = Builder<Episode>.CreateListOfSize(2).All().With(e => e.Id = 0).With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = 0).BuildList();
            Db.InsertMany(_episodes);
            _tracks = Builder<SeriesQualityTrack>.CreateListOfSize(2).All().With(t => t.Id = 0).With(t => t.SeriesId = _series.Id).With(t => t.Enabled = true)
                .With(t => t.IsPrimary = false).TheFirst(1).With(t => t.IsPrimary = true).BuildList();
            Db.InsertMany(_tracks);
            _files = Builder<EpisodeFile>.CreateListOfSize(2).All().With(f => f.Id = 0).With(f => f.SeriesId = _series.Id)
                .With(f => f.Quality = new QualityModel(Quality.HDTV720p)).With(f => f.Languages = new List<Language> { Language.English }).BuildList();
            Db.InsertMany(_files);
            Mocker.Resolve<IEpisodeTrackFileService>().ReplaceLinks(_series.Id,
            [
                new EpisodeTrackFile { EpisodeId = _episodes[0].Id, TrackId = _tracks[0].Id, EpisodeFileId = _files[0].Id },
                new EpisodeTrackFile { EpisodeId = _episodes[0].Id, TrackId = _tracks[1].Id, EpisodeFileId = _files[0].Id },
                new EpisodeTrackFile { EpisodeId = _episodes[1].Id, TrackId = _tracks[0].Id, EpisodeFileId = _files[0].Id },
                new EpisodeTrackFile { EpisodeId = _episodes[1].Id, TrackId = _tracks[1].Id, EpisodeFileId = _files[1].Id }
            ]);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void deletion_should_commit_all_references_before_event_and_preserve_old_ownership(bool omittedNavigation)
        {
            var file = Subject.Get(_files[0].Id);
            if (omittedNavigation)
            {
                file.TrackFiles = null;
            }

            EpisodeFileDeletedEvent deleted = null;
            Mocker.GetMock<IEventAggregator>().Setup(s => s.PublishEvent(It.IsAny<EpisodeFileDeletedEvent>())).Callback<EpisodeFileDeletedEvent>(message =>
            {
                deleted = message;
                Db.All<EpisodeTrackFile>().Should().NotContain(link => link.EpisodeFileId == file.Id);
                Db.All<EpisodeFile>().Should().NotContain(stored => stored.Id == file.Id);
            });

            Subject.Delete(file, DeleteMediaFileReason.Manual);

            deleted.Should().NotBeNull();
            deleted.EpisodeFile.Episodes.Value.Select(e => e.Id).Should().BeEquivalentTo(_episodes.Select(e => e.Id));
            deleted.EpisodeFile.TrackFiles.Value.Should().HaveCount(3);
            deleted.EpisodeFile.Path.Should().Be(Path.Combine(_series.Path, file.RelativePath));
            Db.All<Episode>().Single(e => e.Id == _episodes[0].Id).EpisodeFileId.Should().Be(0);
            Db.All<Episode>().Single(e => e.Id == _episodes[1].Id).EpisodeFileId.Should().Be(_files[1].Id);
            Db.All<EpisodeTrackFile>().Should().ContainSingle().Which.EpisodeFileId.Should().Be(_files[1].Id);
        }

        [Test]
        public void failed_file_delete_should_restore_links_and_projections_without_publishing_deletion()
        {
            var database = Mocker.Resolve<IMainDatabase>();
            using (var connection = database.OpenConnection())
            {
                if (database.DatabaseType == DatabaseType.PostgreSQL)
                {
                    connection.Execute("CREATE FUNCTION fail_media_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected delete failure'; END $$; CREATE TRIGGER fail_media_delete BEFORE DELETE ON \"EpisodeFiles\" FOR EACH ROW EXECUTE FUNCTION fail_media_delete();");
                }
                else
                {
                    connection.Execute("CREATE TRIGGER fail_media_delete BEFORE DELETE ON EpisodeFiles BEGIN SELECT RAISE(ABORT, 'injected delete failure'); END;");
                }
            }

            Action delete = () => Subject.Delete(Subject.Get(_files[0].Id), DeleteMediaFileReason.Manual);
            delete.Should().Throw<Exception>().WithMessage("*injected delete failure*");

            Db.All<EpisodeFile>().Should().HaveCount(2);
            Db.All<EpisodeTrackFile>().Should().HaveCount(4);
            Db.All<Episode>().Should().OnlyContain(e => e.EpisodeFileId == _files[0].Id);
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<EpisodeFileDeletedEvent>()), Times.Never());
        }
    }
}
