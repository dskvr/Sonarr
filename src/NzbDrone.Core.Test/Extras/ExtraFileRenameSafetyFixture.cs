using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Consumers.Xbmc;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Extras
{
    [TestFixture]
    public class ExtraFileRenameSafetyFixture : CoreTest<ExtraService>
    {
        private Series _series;
        private EpisodeFile _file;
        private SubtitleFile _subtitle;
        private OtherExtraFile _other;
        private bool _failSubtitleMove;
        private bool _failSubtitleSave;

        [SetUp]
        public void Setup()
        {
            _failSubtitleMove = false;
            _failSubtitleSave = false;
            Directory.CreateDirectory(TempFolder);
            Mocker.GetMock<IAppFolderInfo>().SetupGet(f => f.AppDataFolder).Returns(Path.Combine(TempFolder, "appdata"));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.CreateFolder(It.IsAny<string>())).Callback<string>(path => Directory.CreateDirectory(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists(It.IsAny<string>())).Returns((string path) => Directory.Exists(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFileAttributes(It.IsAny<string>())).Returns((string path) => File.GetAttributes(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.OpenReadStream(It.IsAny<string>())).Returns((string path) => File.OpenRead(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>(File.WriteAllText);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.ReadAllText(It.IsAny<string>())).Returns((string path) => File.ReadAllText(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.DeleteFile(It.IsAny<string>())).Callback<string>(File.Delete);
            _series = new Series { Id = 1, Path = TempFolder };
            _file = new EpisodeFile { Id = 10, SeriesId = 1, RelativePath = "New.S01E01.mkv" };
            _subtitle = new SubtitleFile { Id = 1, SeriesId = 1, EpisodeFileId = 10, RelativePath = "Old.S01E01.en.srt", Extension = ".srt", Language = Language.English };
            _other = new OtherExtraFile { Id = 2, SeriesId = 1, EpisodeFileId = 10, RelativePath = "Old.S01E01.nfo", Extension = ".nfo" };
            File.WriteAllText(Path.Combine(TempFolder, _subtitle.RelativePath), "subtitle bytes");
            File.WriteAllText(Path.Combine(TempFolder, _other.RelativePath), "metadata bytes");
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(It.IsAny<string>())).Returns((string path) => File.Exists(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFiles(It.IsAny<string>(), false)).Returns((string path, bool _) => Directory.GetFiles(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetParentFolder(It.IsAny<string>())).Returns((string path) => Path.GetDirectoryName(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Callback<string, string, bool>((source, destination, overwrite) =>
                {
                    if (_failSubtitleMove && source.EndsWith(".srt"))
                    {
                        throw new IOException("Injected subtitle disk failure");
                    }

                    File.Move(source, destination, overwrite);
                });
            Mocker.GetMock<ISubtitleFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(() => new List<SubtitleFile>
            {
                new SubtitleFile { Id = _subtitle.Id, EpisodeFileId = _subtitle.EpisodeFileId, RelativePath = _subtitle.RelativePath, Extension = ".srt", Language = Language.English }
            });
            Mocker.GetMock<ISubtitleFileService>().Setup(s => s.GetFilesByEpisodeFile(10)).Returns(() => new List<SubtitleFile>
            {
                new SubtitleFile { Id = _subtitle.Id, EpisodeFileId = _subtitle.EpisodeFileId, RelativePath = _subtitle.RelativePath, Extension = ".srt", Language = Language.English }
            });
            Mocker.GetMock<ISubtitleFileService>().Setup(s => s.FindByPath(1, It.IsAny<string>())).Returns((int _, string path) => path == _subtitle.RelativePath ? _subtitle : null);
            Mocker.GetMock<ISubtitleFileService>().Setup(s => s.Upsert(It.IsAny<SubtitleFile>())).Callback<SubtitleFile>(file =>
            {
                if (_failSubtitleSave)
                {
                    throw new IOException("Injected subtitle persistence failure");
                }

                _subtitle = file;
            });
            Mocker.GetMock<ISubtitleFileService>().Setup(s => s.Upsert(It.IsAny<List<SubtitleFile>>())).Callback<List<SubtitleFile>>(files =>
            {
                if (files.Count > 0)
                {
                    _subtitle = files.Single();
                }
            });
            Mocker.GetMock<IOtherExtraFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(() => new List<OtherExtraFile>
            {
                new OtherExtraFile { Id = _other.Id, EpisodeFileId = _other.EpisodeFileId, RelativePath = _other.RelativePath, Extension = ".nfo" }
            });
            Mocker.GetMock<IOtherExtraFileService>().Setup(s => s.GetFilesByEpisodeFile(10)).Returns(() => new List<OtherExtraFile>
            {
                new OtherExtraFile { Id = _other.Id, EpisodeFileId = _other.EpisodeFileId, RelativePath = _other.RelativePath, Extension = ".nfo" }
            });
            Mocker.GetMock<IOtherExtraFileService>().Setup(s => s.FindByPath(1, It.IsAny<string>())).Returns((int _, string path) => path == _other.RelativePath ? _other : null);
            Mocker.GetMock<IOtherExtraFileService>().Setup(s => s.Upsert(It.IsAny<OtherExtraFile>())).Callback<OtherExtraFile>(file => _other = file);
            Mocker.GetMock<IOtherExtraFileService>().Setup(s => s.Upsert(It.IsAny<List<OtherExtraFile>>())).Callback<List<OtherExtraFile>>(files =>
            {
                if (files.Count > 0)
                {
                    _other = files.Single();
                }
            });
            Mocker.SetConstant<IEnumerable<IManageExtraFiles>>(new IManageExtraFiles[]
            {
                Mocker.Resolve<SubtitleService>(), Mocker.Resolve<OtherExtraService>()
            });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(new List<EpisodeFile> { _file });
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodeBySeries(1)).Returns(new List<Episode> { new Episode { Id = 1 } });
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.GetForSeries(1)).Returns(new List<EpisodeTrackFile>
            {
                new EpisodeTrackFile { EpisodeId = 1, EpisodeFileId = 10, TrackId = 1 }
            });
        }

        [Test]
        public void should_surface_disk_failure_after_real_event_swallowed_it_and_retry_remaining_sidecars()
        {
            Subject.EnsureRenamesCompleted(_series);
            _failSubtitleMove = true;
            Mocker.GetMock<IServiceFactory>().Setup(f => f.BuildAll<IHandle<SeriesRenamedEvent>>()).Returns(new List<IHandle<SeriesRenamedEvent>> { Subject });
            Mocker.GetMock<IServiceFactory>().Setup(f => f.BuildAll<IHandleAsync<SeriesRenamedEvent>>()).Returns(new List<IHandleAsync<SeriesRenamedEvent>>());
            Mocker.GetMock<IServiceFactory>().Setup(f => f.BuildAll<IHandleAsync<IEvent>>()).Returns(new List<IHandleAsync<IEvent>>());

            Mocker.Resolve<EventAggregator>().PublishEvent(new SeriesRenamedEvent(_series, new List<RenamedEpisodeFile>()));

            File.Exists(Path.Combine(TempFolder, "Old.S01E01.en.srt")).Should().BeTrue();
            File.ReadAllText(Path.Combine(TempFolder, "New.S01E01.nfo")).Should().Be("metadata bytes");
            Action strictRename = () => Subject.MoveFilesAfterRename(_series, _file, true);
            strictRename.Should().Throw<AggregateException>();
            Action moveGuard = () => Subject.EnsureRenamesCompleted(_series);
            moveGuard.Should().Throw<IOException>();
            _failSubtitleMove = false;

            strictRename.Should().NotThrow();
            moveGuard.Should().NotThrow();

            File.ReadAllText(Path.Combine(TempFolder, "New.S01E01.en.srt")).Should().Be("subtitle bytes");
            _subtitle.RelativePath.Should().Be("New.S01E01.en.srt");
            _other.RelativePath.Should().Be("New.S01E01.nfo");
            Mocker.Resolve<EventAggregator>().PublishEvent(new SeriesRenamedEvent(_series, new List<RenamedEpisodeFile>()));
            File.ReadAllText(Path.Combine(TempFolder, "New.S01E01.en.srt")).Should().Be("subtitle bytes");
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_recover_sidecar_ownership_when_persistence_fails_after_move()
        {
            _failSubtitleSave = true;
            Action rename = () => Subject.MoveFilesAfterRename(_series, _file, true);

            rename.Should().Throw<AggregateException>();

            File.ReadAllText(Path.Combine(TempFolder, "New.S01E01.en.srt")).Should().Be("subtitle bytes");
            File.Exists(Path.Combine(TempFolder, "Old.S01E01.en.srt")).Should().BeFalse();
            Directory.GetFiles(Path.Combine(TempFolder, "appdata", "MediaFileRecovery", "extras"), "*.json").Should().ContainSingle();
            _subtitle.RelativePath.Should().Be("Old.S01E01.en.srt");
            _failSubtitleSave = false;
            rename.Should().NotThrow();
            File.ReadAllText(Path.Combine(TempFolder, "New.S01E01.en.srt")).Should().Be("subtitle bytes");
        }

        [TestCase(false)]
        [TestCase(true)]
        [Platform("Linux")]
        public void should_preserve_unrelated_entry_when_source_parent_link_changes_during_ownership_save(bool symbolicLink)
        {
            _series.Path = Path.Combine(TempFolder, "library");
            var physicalSource = Path.Combine(TempFolder, "physical-source");
            var unrelatedFolder = Path.Combine(TempFolder, "unrelated");
            var sourceAlias = Path.Combine(_series.Path, "source");
            var destinationFolder = Path.Combine(_series.Path, "destination");
            Directory.CreateDirectory(_series.Path);
            Directory.CreateDirectory(physicalSource);
            Directory.CreateDirectory(unrelatedFolder);
            Directory.CreateDirectory(destinationFolder);
            Directory.CreateSymbolicLink(sourceAlias, physicalSource);
            _subtitle.RelativePath = Path.Combine("source", "Old.S01E01.en.srt");
            _file.RelativePath = Path.Combine("destination", "New.S01E01.mkv");
            var original = Path.Combine(physicalSource, "Old.S01E01.en.srt");
            var unrelated = Path.Combine(unrelatedFolder, "Old.S01E01.en.srt");
            var destination = Path.Combine(destinationFolder, "New.S01E01.en.srt");

            if (symbolicLink)
            {
                File.WriteAllText(Path.Combine(physicalSource, "target.srt"), "subtitle bytes");
                File.WriteAllText(Path.Combine(unrelatedFolder, "target.srt"), "unrelated subtitle");
                File.CreateSymbolicLink(original, "target.srt");
                File.CreateSymbolicLink(unrelated, "target.srt");
            }
            else
            {
                File.WriteAllText(original, "subtitle bytes");
                File.WriteAllText(unrelated, "unrelated subtitle");
                Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(It.IsAny<string>(), destination, false))
                    .Callback<string, string, bool>((source, target, _) => File.Copy(source, target));
            }

            var manager = Mocker.Resolve<SubtitleService>();
            Action rename = () => manager.MoveFilesAfterRename(_series, new List<EpisodeFile> { _file }, true);
            _failSubtitleSave = true;
            rename.Should().Throw<AggregateException>();
            File.ReadAllText(original).Should().Be("subtitle bytes");
            File.ReadAllText(destination).Should().Be("subtitle bytes");
            _failSubtitleSave = false;
            Mocker.GetMock<ISubtitleFileService>().Setup(s => s.Upsert(It.IsAny<SubtitleFile>())).Callback<SubtitleFile>(file =>
            {
                _subtitle = file;
                Directory.Delete(sourceAlias);
                Directory.CreateSymbolicLink(sourceAlias, unrelatedFolder);
            });

            rename.Should().Throw<AggregateException>();

            File.ReadAllText(unrelated).Should().Be("unrelated subtitle");
            File.ReadAllText(original).Should().Be("subtitle bytes");
            File.ReadAllText(destination).Should().Be("subtitle bytes");
            Directory.GetFiles(Path.Combine(TempFolder, "appdata", "MediaFileRecovery", "extras"), "*.json").Should().ContainSingle();
            if (symbolicLink)
            {
                new FileInfo(unrelated).LinkTarget.Should().Be("target.srt");
            }

            Directory.Delete(sourceAlias);
            Directory.CreateSymbolicLink(sourceAlias, physicalSource);
            Mocker.GetMock<ISubtitleFileService>().Setup(s => s.Upsert(It.IsAny<SubtitleFile>())).Callback<SubtitleFile>(file => _subtitle = file);

            rename.Should().NotThrow();

            File.Exists(original).Should().BeFalse();
            File.ReadAllText(unrelated).Should().Be("unrelated subtitle");
            File.ReadAllText(destination).Should().Be("subtitle bytes");
            Directory.GetFiles(Path.Combine(TempFolder, "appdata", "MediaFileRecovery", "extras"), "*.json").Should().BeEmpty();
        }

        [TestCase(false)]
        [TestCase(true)]
        [Platform("Linux")]
        public void should_preserve_unmanaged_destination_file_or_dangling_link(bool symbolicLink)
        {
            var destination = Path.Combine(TempFolder, "New.S01E01.en.srt");
            if (symbolicLink)
            {
                File.CreateSymbolicLink(destination, "missing-external-target.srt");
            }
            else
            {
                File.WriteAllText(destination, "unmanaged subtitle");
            }

            Action rename = () => Subject.MoveFilesAfterRename(_series, _file, true);
            rename.Should().Throw<AggregateException>();

            File.ReadAllText(Path.Combine(TempFolder, "Old.S01E01.en.srt")).Should().Be("subtitle bytes");
            _subtitle.RelativePath.Should().Be("Old.S01E01.en.srt");
            if (symbolicLink)
            {
                new FileInfo(destination).LinkTarget.Should().Be("missing-external-target.srt");
            }
            else
            {
                File.ReadAllText(destination).Should().Be("unmanaged subtitle");
            }
        }

        [Test]
        public void should_report_other_extra_failure_and_still_finish_subtitles()
        {
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(Path.Combine(TempFolder, "Old.S01E01.nfo"), Path.Combine(TempFolder, "New.S01E01.nfo"), false))
                .Throws(new IOException("Injected other-extra move failure"));
            Action rename = () => Subject.MoveFilesAfterRename(_series, _file, true);

            rename.Should().Throw<AggregateException>();

            File.ReadAllText(Path.Combine(TempFolder, "New.S01E01.en.srt")).Should().Be("subtitle bytes");
            File.ReadAllText(Path.Combine(TempFolder, "Old.S01E01.nfo")).Should().Be("metadata bytes");
            _subtitle.RelativePath.Should().Be("New.S01E01.en.srt");
            _other.RelativePath.Should().Be("Old.S01E01.nfo");
        }

        [Test]
        public void should_not_overwrite_another_versions_subtitle_when_media_extensions_differ()
        {
            var incomingFolder = Path.Combine(TempFolder, "incoming");
            Directory.CreateDirectory(incomingFolder);
            var source = Path.Combine(incomingFolder, "Release.S01E01.en.srt");
            File.WriteAllText(source, "different version subtitle");
            _file.RelativePath = "Episode.mkv";
            _subtitle.RelativePath = "Episode.en.srt";
            File.WriteAllText(Path.Combine(TempFolder, _subtitle.RelativePath), "retained version subtitle");
            var originalHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(TempFolder, _subtitle.RelativePath)));
            var incomingHash = SHA256.HashData(File.ReadAllBytes(source));
            var alternate = new EpisodeFile { Id = 20, SeriesId = 1, RelativePath = "Episode.mp4" };
            var local = new LocalEpisode
            {
                Series = _series,
                Path = Path.Combine(incomingFolder, "Release.S01E01.mkv"),
                FileEpisodeInfo = new ParsedEpisodeInfo { SeasonNumber = 1, EpisodeNumbers = new[] { 1 } }
            };

            var result = Mocker.Resolve<SubtitleService>().ImportFiles(local, alternate, new List<string> { source }, true);

            result.Should().BeEmpty();
            SHA256.HashData(File.ReadAllBytes(Path.Combine(TempFolder, _subtitle.RelativePath))).Should().Equal(originalHash);
            SHA256.HashData(File.ReadAllBytes(source)).Should().Equal(incomingHash);
            incomingHash.Should().NotEqual(originalHash);
            Mocker.GetMock<IDiskTransferService>().Verify(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), It.IsAny<bool>()), Times.Never());
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_retry_actual_metadata_consumer_sidecars_after_provider_failure()
        {
            var metadata = new List<MetadataFile>
            {
                new MetadataFile { Id = 1, EpisodeFileId = 10, Consumer = nameof(XbmcMetadata), RelativePath = "MetaOld.nfo", Type = MetadataType.EpisodeMetadata },
                new MetadataFile { Id = 2, EpisodeFileId = 10, Consumer = nameof(XbmcMetadata), RelativePath = "MetaOld-thumb.jpg", Type = MetadataType.EpisodeImage }
            };
            File.WriteAllText(Path.Combine(TempFolder, "MetaOld.nfo"), "nfo bytes");
            File.WriteAllText(Path.Combine(TempFolder, "MetaOld-thumb.jpg"), "image bytes");
            Mocker.GetMock<IMetadataFactory>().Setup(f => f.GetAvailableProviders()).Returns(new List<IMetadata> { Mocker.Resolve<XbmcMetadata>() });
            Mocker.GetMock<IMetadataFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(() => metadata.Select(m => new MetadataFile
            {
                Id = m.Id, EpisodeFileId = m.EpisodeFileId, Consumer = m.Consumer, RelativePath = m.RelativePath, Type = m.Type
            }).ToList());
            Mocker.GetMock<IMetadataFileService>().Setup(s => s.GetFilesByEpisodeFile(10)).Returns(() => metadata.Select(m => new MetadataFile
            {
                Id = m.Id, EpisodeFileId = m.EpisodeFileId, Consumer = m.Consumer, RelativePath = m.RelativePath, Type = m.Type
            }).ToList());
            Mocker.GetMock<IMetadataFileService>().Setup(s => s.FindByPath(1, It.IsAny<string>())).Returns((int _, string path) => metadata.SingleOrDefault(m => m.RelativePath == path));
            Mocker.GetMock<IMetadataFileService>().Setup(s => s.Upsert(It.IsAny<MetadataFile>())).Callback<MetadataFile>(file => metadata[metadata.FindIndex(m => m.Id == file.Id)] = file);
            var source = Path.Combine(TempFolder, "MetaOld-thumb.jpg");
            var destination = Path.Combine(TempFolder, "New.S01E01-thumb.jpg");
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(source, destination, false)).Throws(new IOException("Injected image move failure"));
            var manager = Mocker.Resolve<MetadataService>();
            Action rename = () => manager.MoveFilesAfterRename(_series, new List<EpisodeFile> { _file }, true);

            rename.Should().Throw<AggregateException>();

            File.ReadAllText(Path.Combine(TempFolder, "New.S01E01.nfo")).Should().Be("nfo bytes");
            File.ReadAllText(source).Should().Be("image bytes");
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(source, destination, false)).Callback(() => File.Move(source, destination));
            rename.Should().NotThrow();
            File.ReadAllText(destination).Should().Be("image bytes");
            metadata.Should().OnlyContain(m => m.RelativePath.StartsWith("New.S01E01"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_continue_other_metadata_when_one_provider_filename_fails(bool requireSuccess)
        {
            var consumer = new Mock<IMetadata>();
            var files = new List<EpisodeFile>
            {
                _file,
                new EpisodeFile { Id = 20, SeriesId = 1, RelativePath = "Other.New.mkv" }
            };
            var metadata = new List<MetadataFile>
            {
                new MetadataFile { Id = 1, EpisodeFileId = 10, RelativePath = "Bad.nfo", Consumer = consumer.Object.GetType().Name },
                new MetadataFile { Id = 2, EpisodeFileId = 20, RelativePath = "Good.nfo", Consumer = consumer.Object.GetType().Name }
            };
            File.WriteAllText(Path.Combine(TempFolder, "Bad.nfo"), "failed metadata preserved");
            File.WriteAllText(Path.Combine(TempFolder, "Good.nfo"), "other metadata completes");
            consumer.Setup(c => c.GetFilenameAfterMove(_series, It.IsAny<EpisodeFile>(), It.IsAny<MetadataFile>()))
                .Returns((Series series, EpisodeFile file, MetadataFile extra) => extra.Id == 1
                    ? throw new IOException("Injected provider filename failure")
                    : Path.ChangeExtension(Path.Combine(series.Path, file.RelativePath), ".nfo"));
            Mocker.GetMock<IMetadataFactory>().Setup(f => f.GetAvailableProviders()).Returns(new List<IMetadata> { consumer.Object });
            Mocker.GetMock<IMetadataFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(metadata);
            var manager = Mocker.Resolve<MetadataService>();
            Action rename = () => manager.MoveFilesAfterRename(_series, files, requireSuccess);

            if (requireSuccess)
            {
                rename.Should().Throw<AggregateException>();
            }
            else
            {
                rename.Should().NotThrow();
                ExceptionVerification.ExpectedWarns(1);
            }

            File.ReadAllText(Path.Combine(TempFolder, "Bad.nfo")).Should().Be("failed metadata preserved");
            File.ReadAllText(Path.Combine(TempFolder, "Other.New.nfo")).Should().Be("other metadata completes");
            metadata[1].RelativePath.Should().Be("Other.New.nfo");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_create_metadata_and_image_without_overwriting_other_version(bool existingOwnMetadata)
        {
            _other.EpisodeFileId = 20;
            var consumer = new Mock<IMetadata>();
            var imageSource = Path.Combine(TempFolder, "source-image.jpg");
            File.WriteAllText(imageSource, "image bytes");
            consumer.Setup(c => c.EpisodeMetadata(_series, _file)).Returns(new MetadataFileResult("New.S01E01.nfo", "generated metadata"));
            consumer.Setup(c => c.EpisodeImages(_series, _file)).Returns(new List<ImageFileResult>
            {
                new ImageFileResult("New.S01E01-thumb.jpg", imageSource)
            });
            Mocker.GetMock<IMetadataFactory>().Setup(f => f.Enabled()).Returns(new List<IMetadata> { consumer.Object });
            Mocker.GetMock<IDiskProvider>().Setup(d => d.CopyFile(It.IsAny<string>(), It.IsAny<string>(), false))
                .Callback<string, string, bool>((source, destination, overwrite) => File.Copy(source, destination, overwrite));
            if (existingOwnMetadata)
            {
                File.WriteAllText(Path.Combine(TempFolder, "New.S01E01.nfo"), "previous own metadata");
                Mocker.GetMock<IMetadataFileService>().Setup(s => s.FindByPath(1, "New.S01E01.nfo"))
                    .Returns(new MetadataFile { Id = 3, EpisodeFileId = 10, RelativePath = "New.S01E01.nfo" });
            }

            var result = Mocker.Resolve<MetadataService>().CreateAfterEpisodeImport(_series, _file).ToList();

            result.Should().HaveCount(2).And.OnlyContain(f => f.EpisodeFileId == 10);
            File.ReadAllText(Path.Combine(TempFolder, "New.S01E01.nfo")).Should().Be("generated metadata");
            File.ReadAllText(Path.Combine(TempFolder, "New.S01E01-thumb.jpg")).Should().Be("image bytes");
            File.ReadAllText(Path.Combine(TempFolder, "Old.S01E01.nfo")).Should().Be("metadata bytes");
        }

        [Test]
        public void should_not_overwrite_metadata_owned_by_another_version()
        {
            var destination = Path.Combine(TempFolder, "Episode.nfo");
            File.WriteAllText(destination, "retained version metadata");
            var consumer = new Mock<IMetadata>();
            consumer.Setup(c => c.EpisodeMetadata(_series, _file)).Returns(new MetadataFileResult("Episode.nfo", "new version metadata"));
            Mocker.GetMock<IMetadataFactory>().Setup(f => f.Enabled()).Returns(new List<IMetadata> { consumer.Object });
            Mocker.GetMock<IMetadataFileService>().Setup(s => s.FindByPath(1, "Episode.nfo"))
                .Returns(new MetadataFile { EpisodeFileId = 20, RelativePath = "Episode.nfo" });

            Action create = () => Mocker.Resolve<MetadataService>().CreateAfterEpisodeImport(_series, _file);

            create.Should().Throw<IOException>();
            File.ReadAllText(destination).Should().Be("retained version metadata");
        }
    }
}
