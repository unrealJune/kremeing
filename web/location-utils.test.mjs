import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const Location = require('./location-utils.js');

function fakeGeolocation() {
  let success;
  let failure;
  const cleared = [];
  return {
    api: {
      watchPosition(onSuccess, onError) {
        success = onSuccess;
        failure = onError;
        return 42;
      },
      clearWatch(id) {
        cleared.push(id);
      },
    },
    succeed(position) { success(position); },
    fail(error) { failure(error); },
    cleared,
  };
}

test('requestCurrentPosition resolves the first position and clears its watch', () => {
  const geo = fakeGeolocation();
  const positions = [];
  const errors = [];
  Location.requestCurrentPosition(geo.api, {
    onSuccess: position => positions.push(position),
    onError: error => errors.push(error),
  }, { watchdogMs: 100 });

  const position = { coords: { latitude: 47.6, longitude: -122.3 } };
  geo.succeed(position);
  geo.fail({ code: 2, message: 'late failure' });

  assert.deepEqual(positions, [position]);
  assert.deepEqual(errors, []);
  assert.deepEqual(geo.cleared, [42]);
});

test('requestCurrentPosition normalizes browser errors', () => {
  const geo = fakeGeolocation();
  const errors = [];
  Location.requestCurrentPosition(geo.api, {
    onSuccess() {},
    onError: error => errors.push(error),
  }, { watchdogMs: 100 });

  geo.fail({ code: 1, message: 'User denied Geolocation' });

  assert.deepEqual(errors, [{
    code: 1,
    name: 'permission-denied',
    message: 'User denied Geolocation',
    appTimeout: false,
  }]);
  assert.deepEqual(geo.cleared, [42]);
});

test('requestCurrentPosition cancels a request that Safari leaves pending', async () => {
  const geo = fakeGeolocation();
  const errors = [];
  Location.requestCurrentPosition(geo.api, {
    onSuccess() {},
    onError: error => errors.push(error),
  }, { watchdogMs: 5 });

  await new Promise(resolve => setTimeout(resolve, 20));

  assert.equal(errors.length, 1);
  assert.equal(errors[0].name, 'app-timeout');
  assert.equal(errors[0].appTimeout, true);
  assert.deepEqual(geo.cleared, [42]);
});

test('cancel clears the watch and ignores later callbacks', () => {
  const geo = fakeGeolocation();
  const positions = [];
  const errors = [];
  const request = Location.requestCurrentPosition(geo.api, {
    onSuccess: position => positions.push(position),
    onError: error => errors.push(error),
  }, { watchdogMs: 100 });

  request.cancel();
  geo.succeed({ coords: { latitude: 1, longitude: 2 } });

  assert.deepEqual(positions, []);
  assert.deepEqual(errors, []);
  assert.deepEqual(geo.cleared, [42]);
});

test('requestCurrentPosition reports a synchronous browser exception', () => {
  const errors = [];
  const geolocation = {
    watchPosition() {
      throw new Error('Geolocation is blocked in this context');
    },
    clearWatch() {},
  };

  Location.requestCurrentPosition(geolocation, {
    onSuccess() {},
    onError: error => errors.push(error),
  }, { watchdogMs: 100 });

  assert.equal(errors.length, 1);
  assert.equal(errors[0].name, 'unknown');
  assert.equal(errors[0].message, 'Geolocation is blocked in this context');
});
