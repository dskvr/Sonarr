using System;
using System.Linq.Expressions;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Housekeeping.Housekeepers
{
    [TestFixture]
    public class UpdateCleanTitleForSeriesFixture : CoreTest<UpdateCleanTitleForSeries>
    {
        [Test]
        public void should_update_clean_title()
        {
            var series = Builder<Series>.CreateNew()
                                        .With(s => s.Title = "Full Title")
                                        .With(s => s.CleanTitle = "unclean")
                                        .Build();

            Mocker.GetMock<ISeriesRepository>()
                 .Setup(s => s.All())
                 .Returns(new[] { series });
            Expression<Func<Series, object>>[] updatedFields = null;
            Mocker.GetMock<ISeriesRepository>()
                .Setup(s => s.SetFields(It.IsAny<Series>(), It.IsAny<Expression<Func<Series, object>>[]>()))
                .Callback<Series, Expression<Func<Series, object>>[]>((model, fields) => updatedFields = fields);

            Subject.Clean();

            Mocker.GetMock<ISeriesRepository>()
                .Verify(v => v.Update(It.IsAny<Series>()), Times.Never());
            Assert.That(updatedFields, Has.Length.EqualTo(1));
            Assert.That(updatedFields[0].Compile()(series), Is.EqualTo("fulltitle"));
        }

        [Test]
        public void should_not_update_unchanged_title()
        {
            var series = Builder<Series>.CreateNew()
                                        .With(s => s.Title = "Full Title")
                                        .With(s => s.CleanTitle = "fulltitle")
                                        .Build();

            Mocker.GetMock<ISeriesRepository>()
                 .Setup(s => s.All())
                 .Returns(new[] { series });

            Subject.Clean();

            Mocker.GetMock<ISeriesRepository>()
                .Verify(v => v.SetFields(It.IsAny<Series>(), It.IsAny<Expression<Func<Series, object>>[]>()), Times.Never());
        }
    }
}
