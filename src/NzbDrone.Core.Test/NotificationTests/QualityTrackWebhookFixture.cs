using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Webhook;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.NotificationTests
{
    [TestFixture]
    public class QualityTrackWebhookFixture : CoreTest<Webhook>
    {
        [TestCase(true)]
        [TestCase(false)]
        public void should_report_logical_upgrade_without_claiming_shared_file_was_deleted(bool isUpgrade)
        {
            var series = new Series { Id = 1, Path = TempFolder, Title = "Series" };
            var episode = new Episode { Id = 2, SeriesId = 1, SeasonNumber = 1, EpisodeNumber = 1 };
            var file = new EpisodeFile
            {
                Id = 3,
                SeriesId = 1,
                Series = series,
                Episodes = new List<Episode> { episode },
                RelativePath = "episode.mkv",
                Quality = new QualityModel(Quality.HDTV1080p),
                Languages = new List<Language> { Language.English }
            };
            WebhookImportPayload payload = null;
            Mocker.GetMock<ITagRepository>().Setup(r => r.GetTags(It.IsAny<HashSet<int>>())).Returns(new List<Tag>());
            Mocker.GetMock<IWebhookProxy>()
                .Setup(p => p.SendWebhook(It.IsAny<WebhookPayload>(), It.IsAny<WebhookSettings>()))
                .Callback<WebhookPayload, WebhookSettings>((body, settings) => payload = (WebhookImportPayload)body);
            Subject.Definition = new NotificationDefinition { Settings = new WebhookSettings() };

            Subject.OnDownload(new DownloadMessage
            {
                Series = series,
                EpisodeFile = file,
                EpisodeInfo = new LocalEpisode { IsUpgrade = isUpgrade, CustomFormats = new List<CustomFormat>() },
                OldFiles = new List<DeletedEpisodeFile>()
            });

            Assert.That(payload.IsUpgrade, Is.EqualTo(isUpgrade));
            Assert.That(payload.EpisodeFile.Id, Is.EqualTo(file.Id));
            Assert.That(payload.DeletedFiles, Is.Null.Or.Empty);
        }
    }
}
