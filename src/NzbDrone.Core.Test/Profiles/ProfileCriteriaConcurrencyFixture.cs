using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Profiles
{
    [TestFixture]
    public class ProfileCriteriaConcurrencyFixture : CoreTest
    {
        [SetUp]
        public void Setup()
        {
            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());
            Mocker.GetMock<IQualityProfileRepository>().Setup(r => r.All()).Returns(new List<QualityProfile>());
            Mocker.GetMock<IQualityProfileRepository>().Setup(r => r.Insert(Moq.It.IsAny<QualityProfile>())).Returns<QualityProfile>(p => p);
            Mocker.GetMock<ICustomFormatRepository>().Setup(r => r.Insert(Moq.It.IsAny<CustomFormat>())).Returns<CustomFormat>(f => f);
            Mocker.GetMock<ICustomFormatRepository>().Setup(r => r.Get(1)).Returns(new CustomFormat { Id = 1 });
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetAllSeries()).Returns(new List<Series>());
            Mocker.GetMock<IImportListFactory>().Setup(f => f.All()).Returns(new List<ImportListDefinition>());
        }

        [TestCase("profile-update")]
        [TestCase("profile-add")]
        [TestCase("profile-delete")]
        [TestCase("size-limits")]
        [TestCase("format-update")]
        [TestCase("formats-update")]
        [TestCase("format-insert")]
        [TestCase("format-delete")]
        [TestCase("formats-delete")]
        [TestCase("naming-update")]
        public void should_wait_for_active_file_operation_before_changing_criteria(string operation)
        {
            var profileService = Mocker.Resolve<QualityProfileService>();
            var formatService = Mocker.Resolve<CustomFormatService>();
            var namingService = Mocker.Resolve<NamingConfigService>();
            var profile = new QualityProfile { Id = 1 };
            var format = new CustomFormat { Id = 1 };
            Action mutation = operation switch
            {
                "profile-update" => () => profileService.Update(profile),
                "profile-add" => () => profileService.Add(profile),
                "profile-delete" => () => profileService.Delete(1),
                "size-limits" => () => profileService.UpdateAllSizeLimits(),
                "format-update" => () => formatService.Update(format),
                "formats-update" => () => formatService.Update(new List<CustomFormat> { format }),
                "format-insert" => () => formatService.Insert(format),
                "format-delete" => () => formatService.Delete(1),
                "formats-delete" => () => formatService.Delete(new List<int> { 1 }),
                "naming-update" => () => namingService.Save(NamingConfig.Default),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            using var started = new ManualResetEventSlim();
            Task worker;

            lock (MediaFileOperationLock.ForSeries(47))
            {
                worker = Task.Run(() =>
                {
                    started.Set();
                    mutation();
                });

                Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(worker.Wait(TimeSpan.FromMilliseconds(100)), Is.False);
            }

            Assert.That(worker.Wait(TimeSpan.FromSeconds(5)), Is.True);
            worker.GetAwaiter().GetResult();
        }
    }
}
