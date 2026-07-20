// Cancellable, browser-independent wrapper around the Geolocation API.
// Safari can leave a geolocation request pending without invoking either
// callback, so callers need their own watchdog rather than relying only on
// PositionOptions.timeout.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.KREMEING_LOCATION = factory();
  }
}(typeof self !== 'undefined' ? self : this, function () {
  const ERROR_NAMES = {
    1: 'permission-denied',
    2: 'position-unavailable',
    3: 'timeout',
  };

  function normalizeError(error) {
    const code = Number.isFinite(error?.code) ? error.code : 0;
    return {
      code,
      name: error?.appTimeout ? 'app-timeout' : (ERROR_NAMES[code] || 'unknown'),
      message: error?.message || 'Location request failed',
      appTimeout: error?.appTimeout === true,
    };
  }

  function requestCurrentPosition(geolocation, callbacks, options) {
    if (!geolocation) {
      throw new TypeError('A geolocation implementation is required');
    }
    if (typeof callbacks?.onSuccess !== 'function'
        || typeof callbacks?.onError !== 'function') {
      throw new TypeError('onSuccess and onError callbacks are required');
    }

    const config = options || {};
    const watchdogMs = config.watchdogMs ?? 12_000;
    const positionOptions = config.positionOptions || {
      timeout: 10_000,
      maximumAge: 60_000,
    };
    let settled = false;
    let watchId = null;
    let watchdogId = null;

    const cleanup = () => {
      if (watchdogId !== null) {
        clearTimeout(watchdogId);
        watchdogId = null;
      }
      if (watchId !== null) {
        geolocation.clearWatch(watchId);
        watchId = null;
      }
    };

    const succeed = (position) => {
      if (settled) return;
      settled = true;
      cleanup();
      callbacks.onSuccess(position);
    };

    const fail = (error) => {
      if (settled) return;
      settled = true;
      cleanup();
      callbacks.onError(normalizeError(error));
    };

    watchdogId = setTimeout(() => {
      fail({
        code: 3,
        message: 'Safari did not complete the location request',
        appTimeout: true,
      });
    }, watchdogMs);

    try {
      // watchPosition gives us a request ID that can be cancelled if WebKit
      // never resolves its permission UI. We clear it on the first result,
      // preserving getCurrentPosition semantics.
      watchId = geolocation.watchPosition(succeed, fail, positionOptions);
      if (settled && watchId !== null) {
        geolocation.clearWatch(watchId);
        watchId = null;
      }
    } catch (error) {
      fail(error);
    }

    return {
      cancel() {
        if (settled) return;
        settled = true;
        cleanup();
      },
    };
  }

  return { normalizeError, requestCurrentPosition };
}));
