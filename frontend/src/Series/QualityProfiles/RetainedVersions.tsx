import React, { useCallback, useState } from 'react';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import useEpisodeFiles from 'EpisodeFile/useEpisodeFiles';
import Series from 'Series/Series';
import { useQualityProfilesData } from 'Settings/Profiles/Quality/useQualityProfiles';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import styles from './AdditionalQualityProfiles.css';

interface RetainedVersionsProps {
  series: Series;
}

interface RetainedVersionsModalContentProps extends RetainedVersionsProps {
  onModalClose: () => void;
}

function RetainedVersionsModalContent({
  series,
  onModalClose,
}: RetainedVersionsModalContentProps) {
  const {
    data: files,
    isFetching,
    error,
  } = useEpisodeFiles({ seriesId: series.id });
  const profiles = useQualityProfilesData();
  const tracks = series.qualityTracks ?? [];
  const retainedTrackIds = tracks
    .filter((track) => !track.enabled)
    .map((track) => track.id);
  const retainedFiles = files.filter((file) =>
    file.qualityTrackIds?.some((id) => retainedTrackIds.includes(id))
  );

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>{translate('RetainedVersions')}</ModalHeader>

      <ModalBody>
        <p>{translate('RetainedVersionsHelpText')}</p>

        {isFetching ? <LoadingIndicator /> : null}

        {error ? (
          <Alert kind="danger">
            {translate('UnableToLoadRetainedVersions')}
          </Alert>
        ) : null}

        {retainedFiles.map((file) => (
          <div key={file.id} className={styles.file}>
            <div>{file.path}</div>
            <div>
              {file.quality.quality.name} · {formatBytes(file.size)}
            </div>
            <div>
              {tracks
                .filter((track) => file.qualityTrackIds?.includes(track.id))
                .map((track) => (
                  <Label
                    key={track.id}
                    kind={track.enabled ? 'info' : 'default'}
                  >
                    {profiles.find(
                      (profile) => profile.id === track.qualityProfileId
                    )?.name ?? track.qualityProfileId}
                    {track.enabled ? '' : ` (${translate('Retained')})`}
                  </Label>
                ))}
            </div>
          </div>
        ))}

        {!isFetching && !error && !retainedFiles.length ? (
          <p>{translate('NoRetainedVersions')}</p>
        ) : null}
      </ModalBody>

      <ModalFooter>
        <Button onPress={onModalClose}>{translate('Close')}</Button>
      </ModalFooter>
    </ModalContent>
  );
}

function RetainedVersions({ series }: RetainedVersionsProps) {
  const [isOpen, setIsOpen] = useState(false);
  const handleOpen = useCallback(() => setIsOpen(true), []);
  const handleClose = useCallback(() => setIsOpen(false), []);

  if (
    !series.qualityTracks?.some(
      (track) => !track.enabled && track.episodeFileCount > 0
    )
  ) {
    return null;
  }

  return (
    <div className={styles.retained}>
      <Link onPress={handleOpen}>{translate('RetainedVersions')}</Link>

      <Modal isOpen={isOpen} onModalClose={handleClose}>
        <RetainedVersionsModalContent
          series={series}
          onModalClose={handleClose}
        />
      </Modal>
    </div>
  );
}

export default RetainedVersions;
