const assert = require('node:assert/strict');
const { test } = require('node:test');
const {
  applyAdditionalQualityProfiles,
  getAdditionalQualityProfileIds,
  hasOverlappingQualities
} = require('../src/Series/QualityProfiles/qualityProfileSelection.ts');

test('ordinary bulk edits preserve each series additional profiles', () => {
  for (const current of [[], [2], [3, 4]]) {
    assert.deepEqual(
      applyAdditionalQualityProfiles(current, [5], 'noChange'),
      current
    );
  }
});

test('bulk add combines mixed selections without duplicate profiles or mutations', () => {
  const current = Object.freeze([2, 3]);
  const selected = Object.freeze([3, 4, 4]);
  assert.deepEqual(
    applyAdditionalQualityProfiles(current, selected, 'add'),
    [2, 3, 4]
  );
  assert.deepEqual(applyAdditionalQualityProfiles([], selected, 'add'), [3, 4]);
  assert.deepEqual(current, [2, 3]);
  assert.deepEqual(selected, [3, 4, 4]);
});

test('bulk remove affects only selected additional profiles in each series', () => {
  assert.deepEqual(applyAdditionalQualityProfiles([2, 3], [2, 4], 'remove'), [
    3
  ]);
  assert.deepEqual(applyAdditionalQualityProfiles([5], [2, 4], 'remove'), [5]);
});

test('bulk replace can clear all additional profiles explicitly', () => {
  assert.deepEqual(applyAdditionalQualityProfiles([2, 3], [], 'replace'), []);
  assert.deepEqual(applyAdditionalQualityProfiles([2, 3], [4, 4], 'replace'), [
    4
  ]);
});

test('legacy series without track fields remains single profile', () => {
  assert.deepEqual(getAdditionalQualityProfileIds({ qualityProfileId: 1 }), []);
});

test('configuration ignores primary and disabled tracks without erasing retained metadata', () => {
  const series = {
    qualityProfileId: 1,
    qualityTracks: [
      { id: 10, qualityProfileId: 1, isPrimary: true, enabled: true },
      { id: 11, qualityProfileId: 2, isPrimary: false, enabled: true },
      {
        id: 12,
        qualityProfileId: 3,
        isPrimary: false,
        enabled: false,
        episodeFileCount: 5
      }
    ]
  };
  assert.deepEqual(getAdditionalQualityProfileIds(series), [2]);
  assert.equal(series.qualityTracks[2].episodeFileCount, 5);
});

test('explicit empty additional selection overrides old track projection', () => {
  assert.deepEqual(
    getAdditionalQualityProfileIds({
      additionalQualityProfileIds: [],
      qualityTracks: [{ qualityProfileId: 2, isPrimary: false, enabled: true }]
    }),
    []
  );
});

test('single profile never shows overlap notice even with duplicate quality members', () => {
  assert.equal(hasOverlappingQualities([]), false);
  assert.equal(
    hasOverlappingQualities([
      {
        items: [
          { quality: { id: 1 }, allowed: true },
          { quality: { id: 1 }, allowed: true }
        ]
      }
    ]),
    false
  );
});

test('overlap includes allowed grouped qualities and excludes disabled qualities and groups', () => {
  const first = { items: [{ quality: { id: 1 }, allowed: true }] };
  assert.equal(
    hasOverlappingQualities([
      first,
      { items: [{ id: 1000, allowed: true, items: [{ quality: { id: 1 } }] }] }
    ]),
    true
  );
  assert.equal(
    hasOverlappingQualities([
      first,
      {
        items: [
          { quality: { id: 1 }, allowed: false },
          { id: 1000, allowed: false, items: [{ quality: { id: 1 } }] },
          { quality: { id: 2 }, allowed: true }
        ]
      }
    ]),
    false
  );
});
