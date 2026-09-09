import React, {
  createContext,
  PropsWithChildren,
  useContext,
  useMemo,
} from 'react';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import Queue from 'typings/Queue';
import getSeriesQueueDetails from './getSeriesQueueDetails';

interface EpisodeDetails {
  episodeIds: number[];
}

interface SeriesDetails {
  seriesId: number;
}

interface AllDetails {
  all: boolean;
}

type QueueDetailsFilter = AllDetails | EpisodeDetails | SeriesDetails;

const QueueDetailsContext = createContext<ReadonlyArray<Queue> | undefined>(
  undefined
);

export default function QueueDetailsProvider({
  children,
  ...filter
}: PropsWithChildren<QueueDetailsFilter>) {
  const { data } = useApiQuery<Queue[]>({
    path: '/queue/details',
    queryParams: { ...filter, includeSubresources: ['episodes'] },
    queryOptions: {
      enabled: Object.keys(filter).length > 0,
    },
  });

  return (
    <QueueDetailsContext.Provider value={data}>
      {children}
    </QueueDetailsContext.Provider>
  );
}

export function useQueueItemForEpisode(episodeId: number) {
  const queue = useContext(QueueDetailsContext);

  return useMemo(() => {
    return queue?.find((item) => item.episodeIds?.includes(episodeId));
  }, [episodeId, queue]);
}

export function useIsDownloadingEpisodes(episodeIds: number[]) {
  const queue = useContext(QueueDetailsContext);

  return useMemo(() => {
    if (!queue) {
      return false;
    }

    return queue.some((item) =>
      item.episodeIds?.some((e) => episodeIds.includes(e))
    );
  }, [episodeIds, queue]);
}

export interface SeriesQueueDetails {
  count: number;
  episodesWithFiles: number;
  queuedVersionsCount: number;
}

export function useQueueDetailsForSeries(
  seriesId: number,
  seasonNumber?: number
) {
  const queue = useContext(QueueDetailsContext);

  return useMemo<SeriesQueueDetails>(
    () => getSeriesQueueDetails(queue, seriesId, seasonNumber),
    [seriesId, seasonNumber, queue]
  );
}

export const useQueueDetails = () => {
  return useContext(QueueDetailsContext) ?? [];
};
