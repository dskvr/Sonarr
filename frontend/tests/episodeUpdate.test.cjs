const assert = require('node:assert/strict');
const { test } = require('node:test');
const mergeEpisodeUpdate =
  require('../src/Episode/mergeEpisodeUpdate.ts').default;

test('search and grab updates preserve uncomputed version collections only', () => {
  const current = {
    id: 1,
    title: 'Old',
    overview: 'Old overview',
    episodeFileId: 1,
    episodeFiles: [{ id: 1 }, { id: 3 }],
    qualityTracks: [{ trackId: 1 }, { trackId: 3 }]
  };
  const updated = { id: 1, title: 'New', episodeFileId: 1, grabbed: true };
  const result = mergeEpisodeUpdate(current, updated);
  assert.deepEqual(result.episodeFiles, current.episodeFiles);
  assert.deepEqual(result.qualityTracks, current.qualityTracks);
  assert.equal(result.title, 'New');
  assert.equal(result.grabbed, true);
  assert.equal(result.overview, undefined);
});

test('authoritative empty collections clear deleted versions', () => {
  const result = mergeEpisodeUpdate(
    {
      id: 1,
      episodeFileId: 1,
      hasFile: true,
      episodeFiles: [{ id: 1 }],
      qualityTracks: [{ trackId: 1 }]
    },
    {
      id: 1,
      episodeFileId: 0,
      hasFile: false,
      episodeFiles: [],
      qualityTracks: []
    }
  );
  assert.deepEqual(result.episodeFiles, []);
  assert.deepEqual(result.qualityTracks, []);
  assert.equal(result.episodeFileId, 0);
  assert.equal(result.hasFile, false);
});

test('new collections replace stale associations and null means uncomputed', () => {
  const current = {
    id: 1,
    episodeFiles: [{ id: 1 }],
    qualityTracks: [{ trackId: 1 }]
  };
  assert.deepEqual(
    mergeEpisodeUpdate(current, {
      id: 1,
      episodeFiles: [{ id: 2 }],
      qualityTracks: [{ trackId: 3 }]
    }).qualityTracks,
    [{ trackId: 3 }]
  );
  assert.deepEqual(
    mergeEpisodeUpdate(current, {
      id: 1,
      episodeFiles: null,
      qualityTracks: null
    }).episodeFiles,
    [{ id: 1 }]
  );
});
