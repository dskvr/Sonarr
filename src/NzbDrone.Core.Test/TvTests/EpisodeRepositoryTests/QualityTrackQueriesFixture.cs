using System;
using System.Collections.Generic;
using FizzWare.NBuilder;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.TvTests.EpisodeRepositoryTests
{
    [TestFixture]
    public class QualityTrackQueriesFixture : DbTest<EpisodeRepository, Episode>
    {
        private Series _series;
        private Episode _episode;
        private SeriesQualityTrack _additional;
        private EpisodeFile _secondaryFile;

        [SetUp]
        public void Setup()
        {
            _series = Db.Insert(Builder<Series>.CreateNew()
                .With(s => s.QualityProfileId = 1)
                .With(s => s.Runtime = 30)
                .BuildNew());
            var primaryFile = Db.Insert(Builder<EpisodeFile>.CreateNew()
                .With(f => f.SeriesId = _series.Id)
                .With(f => f.Languages = new List<Language> { Language.English })
                .With(f => f.Quality = new QualityModel(Quality.Bluray1080p))
                .BuildNew());
            _secondaryFile = Db.Insert(Builder<EpisodeFile>.CreateNew()
                .With(f => f.SeriesId = _series.Id)
                .With(f => f.Languages = new List<Language> { Language.English })
                .With(f => f.Quality = new QualityModel(Quality.HDTV720p))
                .BuildNew());
            _episode = Db.Insert(Builder<Episode>.CreateNew()
                .With(e => e.SeriesId = _series.Id)
                .With(e => e.SeasonNumber = 1)
                .With(e => e.EpisodeNumber = 1)
                .With(e => e.Monitored = false)
                .With(e => e.AirDateUtc = DateTime.UtcNow.AddDays(-1))
                .With(e => e.EpisodeFileId = primaryFile.Id)
                .BuildNew());
            var primary = Db.Insert(new SeriesQualityTrack { SeriesId = _series.Id, QualityProfileId = 1, IsPrimary = true, Enabled = true });
            _additional = Db.Insert(new SeriesQualityTrack { SeriesId = _series.Id, QualityProfileId = 2, Enabled = true });
            Db.Insert(new EpisodeTrackFile { EpisodeId = _episode.Id, TrackId = primary.Id, EpisodeFileId = primaryFile.Id });
        }

        private static PagingSpec<Episode> Page()
        {
            return new PagingSpec<Episode> { Page = 1, PageSize = 10, SortKey = "AirDateUtc", SortDirection = SortDirection.Ascending };
        }

        private List<QualitiesBelowCutoff> BelowCutoff()
        {
            return new List<QualitiesBelowCutoff>
            {
                new QualitiesBelowCutoff(1, new[] { Quality.HDTV720p.Id }),
                new QualitiesBelowCutoff(2, new[] { Quality.HDTV720p.Id })
            };
        }

        [Test]
        public void should_find_missing_additional_version_when_primary_exists()
        {
            var result = Subject.EpisodesWithoutFiles(Page(), false);

            Assert.That(result.TotalRecords, Is.EqualTo(1));
            Assert.That(result.Records, Has.Count.EqualTo(1));
            Assert.That(result.Records[0].Id, Is.EqualTo(_episode.Id));
        }

        [Test]
        public void should_not_consider_disabled_track_missing()
        {
            _additional.Enabled = false;
            Db.Update(_additional);

            Assert.That(Subject.EpisodesWithoutFiles(Page(), false).TotalRecords, Is.Zero);
        }

        [Test]
        public void should_monitor_partially_present_episode_when_monitoring_missing()
        {
            Subject.SetMonitored(_series.Id, MonitorTypes.Missing, 0, 0);

            Assert.That(Subject.Get(_episode.Id).Monitored, Is.True);
        }

        [Test]
        public void should_find_secondary_cutoff_and_apply_quality_filter_to_same_track()
        {
            Db.Insert(new EpisodeTrackFile { EpisodeId = _episode.Id, TrackId = _additional.Id, EpisodeFileId = _secondaryFile.Id });

            var result = Subject.EpisodesWhereCutoffUnmet(Page(), BelowCutoff(), false);
            Assert.That(result.TotalRecords, Is.EqualTo(1));
            Assert.That(result.Records[0].Id, Is.EqualTo(_episode.Id));
            Assert.That(Subject.EpisodesWhereCutoffUnmet(Page(), BelowCutoff(), false, quality: new List<int> { Quality.HDTV720p.Id }).TotalRecords, Is.EqualTo(1));
            Assert.That(Subject.EpisodesWhereCutoffUnmet(Page(), BelowCutoff(), false, quality: new List<int> { Quality.Bluray1080p.Id }).TotalRecords, Is.Zero);
            Assert.That(Subject.EpisodesWithoutFiles(Page(), false).TotalRecords, Is.Zero);
        }

        [Test]
        public void should_exclude_retained_disabled_file_from_cutoff_search()
        {
            Db.Insert(new EpisodeTrackFile { EpisodeId = _episode.Id, TrackId = _additional.Id, EpisodeFileId = _secondaryFile.Id });
            _additional.Enabled = false;
            Db.Update(_additional);

            Assert.That(Subject.EpisodesWhereCutoffUnmet(Page(), BelowCutoff(), false).TotalRecords, Is.Zero);
        }
    }
}
