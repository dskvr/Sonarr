const assert = require('node:assert/strict');
const { test } = require('node:test');
const getSeriesQueueDetails =
  require('../src/Activity/Queue/Details/getSeriesQueueDetails.ts').default;

test('episode progress and queued version slots stay separate for shared releases', () => {
  const queue = [
    {
      seriesId: 1,
      targetQualityTrackIds: [1],
      episodes: [{ id: 2, hasFile: false, seasonNumber: 1 }]
    },
    {
      seriesId: 1,
      targetQualityTrackIds: [3],
      episodes: [{ id: 2, hasFile: false, seasonNumber: 1 }]
    },
    {
      seriesId: 1,
      targetQualityTrackIds: [1, 6],
      episodes: [{ id: 3, hasFile: false, seasonNumber: 1 }]
    }
  ];
  assert.deepEqual(getSeriesQueueDetails(queue, 1), {
    count: 2,
    episodesWithFiles: 0,
    queuedVersionsCount: 4
  });
  assert.deepEqual(getSeriesQueueDetails([...queue, queue[0]], 1), {
    count: 2,
    episodesWithFiles: 0,
    queuedVersionsCount: 4
  });
});

test('multiple quality downloads count each episode once', () => {
  const queue = [
    { seriesId: 1, episodes: [{ id: 2, hasFile: false, seasonNumber: 1 }] },
    { seriesId: 1, episodes: [{ id: 2, hasFile: false, seasonNumber: 1 }] },
    { seriesId: 1, episodes: [{ id: 3, hasFile: false, seasonNumber: 1 }] }
  ];
  assert.deepEqual(getSeriesQueueDetails(queue, 1), {
    count: 2,
    episodesWithFiles: 0,
    queuedVersionsCount: 0
  });
});

test('overlapping episode packs count existing files once', () => {
  const queue = [
    {
      seriesId: 1,
      episodes: [
        { id: 1, hasFile: true, seasonNumber: 1 },
        { id: 2, hasFile: false, seasonNumber: 1 }
      ]
    },
    {
      seriesId: 1,
      episodes: [
        { id: 1, hasFile: true, seasonNumber: 1 },
        { id: 3, hasFile: false, seasonNumber: 1 }
      ]
    }
  ];
  assert.deepEqual(getSeriesQueueDetails(queue, 1), {
    count: 3,
    episodesWithFiles: 1,
    queuedVersionsCount: 0
  });
});

test('season progress excludes other seasons, series, and imported jobs', () => {
  const queue = [
    {
      seriesId: 1,
      episodes: [
        { id: 1, hasFile: true, seasonNumber: 1 },
        { id: 2, hasFile: false, seasonNumber: 2 }
      ]
    },
    { seriesId: 2, episodes: [{ id: 3, hasFile: false, seasonNumber: 1 }] },
    {
      seriesId: 1,
      trackedDownloadState: 'imported',
      episodes: [{ id: 4, hasFile: false, seasonNumber: 1 }]
    }
  ];
  assert.deepEqual(getSeriesQueueDetails(queue, 1, 1), {
    count: 1,
    episodesWithFiles: 1,
    queuedVersionsCount: 0
  });
  assert.deepEqual(getSeriesQueueDetails(queue, 1, 2), {
    count: 1,
    episodesWithFiles: 0,
    queuedVersionsCount: 0
  });
  assert.deepEqual(getSeriesQueueDetails(undefined, 1), {
    count: 0,
    episodesWithFiles: 0,
    queuedVersionsCount: 0
  });
});
