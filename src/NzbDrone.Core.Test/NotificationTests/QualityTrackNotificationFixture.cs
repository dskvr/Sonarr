using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.NotificationTests
{
    [TestFixture]
    public class QualityTrackNotificationFixture : CoreTest<NotificationService>
    {
        [TestCase(true, false, false)]
        [TestCase(true, true, true)]
        [TestCase(false, false, true)]
        public void should_respect_upgrade_setting_when_existing_file_remains_shared(bool isUpgrade, bool notifyOnUpgrade, bool expectedNotification)
        {
            var notification = new Mock<INotification>();
            notification.SetupGet(n => n.Definition).Returns(new NotificationDefinition { Id = 1, OnUpgrade = notifyOnUpgrade });
            Mocker.GetMock<INotificationFactory>()
                .Setup(f => f.OnDownloadEnabled(true))
                .Returns(new List<INotification> { notification.Object });
            var localEpisode = new LocalEpisode
            {
                Series = new Series { Id = 1, Title = "Series" },
                Episodes = new List<Episode>(),
                Quality = new QualityModel(Quality.HDTV1080p),
                IsUpgrade = isUpgrade
            };

            Subject.Handle(new EpisodeImportedEvent(localEpisode, new EpisodeFile(), new List<DeletedEpisodeFile>(), true, null));

            notification.Verify(n => n.OnDownload(It.Is<DownloadMessage>(m => m.IsUpgrade == isUpgrade && m.OldFiles.Count == 0)), expectedNotification ? Times.Once() : Times.Never());
        }

        [Test]
        public void should_handle_notifications_without_episode_context()
        {
            Assert.That(new DownloadMessage().IsUpgrade, Is.False);
            var message = new DownloadMessage
            {
                OldFiles = new List<DeletedEpisodeFile> { new DeletedEpisodeFile(new EpisodeFile(), null) }
            };
            Assert.That(message.IsUpgrade, Is.True);
        }

        [Test]
        public void should_preserve_legacy_upgrade_classification_from_deleted_files()
        {
            var message = new DownloadMessage
            {
                EpisodeInfo = new LocalEpisode(),
                OldFiles = new List<DeletedEpisodeFile> { new DeletedEpisodeFile(new EpisodeFile(), null) }
            };

            Assert.That(message.IsUpgrade, Is.True);
        }
    }
}
