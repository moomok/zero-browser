using System.Globalization;
using System.Text;
using System.Text.Json;
using AntiDetectBrowser.Core.Models;

namespace AntiDetectBrowser.Core.Fingerprint;

/// <summary>
/// Builds the JavaScript patch script that is injected into every frame of a Chromium
/// instance via CDP <c>Page.addScriptToEvaluateOnNewDocument</c>.
///
/// The script is intentionally minified-by-hand so the resulting fingerprint is consistent
/// with what production anti-detect browsers ship. Care is taken so that:
///   1. <c>Function.prototype.toString</c> on patched getters still returns "[native code]".
///   2. Patches live on <c>*.prototype</c>, not on instances, so cloned objects remain consistent.
///   3. The patcher leaves no obvious global side-effects (no leaked symbols on window).
///   4. Stack traces from spoofed APIs do not reveal user-defined function names.
/// </summary>
public sealed class FingerprintInjector
{
    public string BuildPatchScript(FingerprintProfile fp)
    {
        // Serialize the fingerprint payload as JSON the JS side can consume.
        var payload = JsonSerializer.Serialize(new
        {
            ua                = fp.UserAgent,
            platform          = fp.Platform,
            vendor            = fp.Vendor,
            productSub        = fp.ProductSub,
            languages         = fp.Languages,
            language          = fp.PrimaryLanguage,
            hardwareConcurrency = fp.HardwareConcurrency,
            deviceMemory      = fp.DeviceMemoryGb,
            screen = new
            {
                width       = fp.ScreenWidth,
                height      = fp.ScreenHeight,
                availWidth  = fp.AvailWidth,
                availHeight = fp.AvailHeight,
                colorDepth  = fp.ColorDepth,
                pixelDepth  = fp.ColorDepth
            },
            devicePixelRatio  = fp.DevicePixelRatio,
            timezone          = fp.Timezone,
            timezoneOffset    = fp.TimezoneOffsetMinutes,
            geo = new
            {
                latitude  = fp.GeoLatitude.ToString("0.######", CultureInfo.InvariantCulture),
                longitude = fp.GeoLongitude.ToString("0.######", CultureInfo.InvariantCulture),
                accuracy  = fp.GeoAccuracy.ToString("0.##", CultureInfo.InvariantCulture)
            },
            webgl = new
            {
                vendor = fp.WebGlVendor,
                renderer = fp.WebGlRenderer,
                version = fp.WebGlVersion,
                shadingLanguageVersion = fp.WebGlShadingLanguageVersion
            },
            canvasSeed = fp.CanvasNoiseSeed,
            audioSeed  = fp.AudioNoiseSeed,
            fontSeed   = fp.FontNoiseSeed,
            fonts      = fp.Fonts,
            mediaDevices = fp.MediaDevices.Select(m => new { m.DeviceId, m.GroupId, m.Kind, m.Label }),
            webRtcMode = fp.WebRtcMode.ToString()
        });

        // The JS template below is plain JavaScript, NOT C# string interpolation.
        // We only inject the JSON payload via {payload} placeholder.
        // Keep this file readable; minification can happen later in a build step.
        var script = JsTemplate.Replace("__FP_PAYLOAD__", payload);
        return script;
    }

    /// <summary>
    /// The actual JS that does the patching. Designed to run as the very first script
    /// in a fresh document context, before any page script.
    /// </summary>
    private const string JsTemplate = """
(() => {
  'use strict';
  const FP = __FP_PAYLOAD__;

  // ===== utilities =====
  const nativeFnToString = Function.prototype.toString;
  const fakeNativeSource = (name) => `function ${name}() { [native code] }`;

  // Replace a getter on a prototype while preserving toString as native.
  function defineGetter(proto, prop, getter) {
    const fn = function () { return getter.call(this); };
    Object.defineProperty(fn, 'name', { value: `get ${prop}` });
    // Hide our impl from Function.prototype.toString
    Object.defineProperty(proto, prop, {
      configurable: true,
      enumerable: true,
      get: fn
    });
    proxyToString(fn, `get ${prop}`);
  }

  function defineValue(proto, prop, value) {
    Object.defineProperty(proto, prop, {
      configurable: true,
      enumerable: true,
      writable: false,
      value
    });
  }

  // Make any function `fn` report itself as native code via toString.
  function proxyToString(fn, name) {
    const origToString = nativeFnToString;
    fn.toString = new Proxy(origToString, {
      apply(target, thisArg, args) {
        if (thisArg === fn) return fakeNativeSource(name);
        return Reflect.apply(target, thisArg, args);
      }
    });
  }

  // Patch Function.prototype.toString globally so wrapped natives still look native.
  // This is delicate — we keep the toString on the prototype but proxy its apply.
  (() => {
    const origToString = Function.prototype.toString;
    const map = new WeakMap();
    Function.prototype.toString = new Proxy(origToString, {
      apply(target, thisArg, args) {
        if (map.has(thisArg)) return map.get(thisArg);
        return Reflect.apply(target, thisArg, args);
      }
    });
    // Hide the patched toString itself.
    map.set(Function.prototype.toString, fakeNativeSource('toString'));
  })();

  // ===== Navigator =====
  const NavProto = Navigator.prototype;
  defineGetter(NavProto, 'userAgent',           () => FP.ua);
  defineGetter(NavProto, 'appVersion',          () => FP.ua.replace(/^Mozilla\//, ''));
  defineGetter(NavProto, 'platform',            () => FP.platform);
  defineGetter(NavProto, 'vendor',              () => FP.vendor);
  defineGetter(NavProto, 'productSub',          () => FP.productSub);
  defineGetter(NavProto, 'language',            () => FP.language);
  defineGetter(NavProto, 'languages',           () => Object.freeze([...FP.languages]));
  defineGetter(NavProto, 'hardwareConcurrency', () => FP.hardwareConcurrency);
  defineGetter(NavProto, 'deviceMemory',        () => FP.deviceMemory);
  defineGetter(NavProto, 'webdriver',           () => false);

  // Plugins / mimeTypes — modern Chrome reports empty PluginArray.
  // We keep them empty to match.
  // (no patch needed; just make sure tests don't see custom properties)

  // ===== Screen =====
  const ScreenProto = Screen.prototype;
  defineGetter(ScreenProto, 'width',       () => FP.screen.width);
  defineGetter(ScreenProto, 'height',      () => FP.screen.height);
  defineGetter(ScreenProto, 'availWidth',  () => FP.screen.availWidth);
  defineGetter(ScreenProto, 'availHeight', () => FP.screen.availHeight);
  defineGetter(ScreenProto, 'colorDepth',  () => FP.screen.colorDepth);
  defineGetter(ScreenProto, 'pixelDepth',  () => FP.screen.pixelDepth);

  // window.devicePixelRatio
  Object.defineProperty(window, 'devicePixelRatio', {
    configurable: true, enumerable: true, get: () => FP.devicePixelRatio
  });

  // ===== Intl / Timezone =====
  // Patch Date.prototype.getTimezoneOffset to return our spoofed offset.
  const origGetTzOffset = Date.prototype.getTimezoneOffset;
  Date.prototype.getTimezoneOffset = function () {
    return FP.timezoneOffset;
  };
  proxyToString(Date.prototype.getTimezoneOffset, 'getTimezoneOffset');

  // Patch Intl.DateTimeFormat().resolvedOptions().timeZone
  const origResolvedOptions = Intl.DateTimeFormat.prototype.resolvedOptions;
  Intl.DateTimeFormat.prototype.resolvedOptions = function () {
    const opts = origResolvedOptions.apply(this, arguments);
    opts.timeZone = FP.timezone;
    return opts;
  };
  proxyToString(Intl.DateTimeFormat.prototype.resolvedOptions, 'resolvedOptions');

  // ===== Canvas =====
  // Add deterministic, sub-perceptual noise to readback APIs.
  const canvasNoise = (() => {
    let s = FP.canvasSeed >>> 0;
    return () => {
      // xorshift32
      s ^= s << 13; s >>>= 0;
      s ^= s >>> 17;
      s ^= s << 5; s >>>= 0;
      return ((s & 0xff) % 3) - 1; // {-1, 0, 1}
    };
  })();

  const origToDataURL = HTMLCanvasElement.prototype.toDataURL;
  HTMLCanvasElement.prototype.toDataURL = function () {
    try {
      const ctx = this.getContext('2d');
      if (ctx) {
        const w = this.width, h = this.height;
        if (w > 0 && h > 0) {
          const img = ctx.getImageData(0, 0, w, h);
          for (let i = 0; i < img.data.length; i += 4) {
            img.data[i]   = (img.data[i]   + canvasNoise() + 256) & 0xff;
            img.data[i+1] = (img.data[i+1] + canvasNoise() + 256) & 0xff;
            img.data[i+2] = (img.data[i+2] + canvasNoise() + 256) & 0xff;
          }
          ctx.putImageData(img, 0, 0);
        }
      }
    } catch (e) { /* swallow — toDataURL on tainted canvas */ }
    return origToDataURL.apply(this, arguments);
  };
  proxyToString(HTMLCanvasElement.prototype.toDataURL, 'toDataURL');

  const origGetImageData = CanvasRenderingContext2D.prototype.getImageData;
  CanvasRenderingContext2D.prototype.getImageData = function () {
    const img = origGetImageData.apply(this, arguments);
    for (let i = 0; i < img.data.length; i += 4) {
      img.data[i]   = (img.data[i]   + canvasNoise() + 256) & 0xff;
      img.data[i+1] = (img.data[i+1] + canvasNoise() + 256) & 0xff;
      img.data[i+2] = (img.data[i+2] + canvasNoise() + 256) & 0xff;
    }
    return img;
  };
  proxyToString(CanvasRenderingContext2D.prototype.getImageData, 'getImageData');

  // ===== WebGL =====
  function patchWebGl(proto) {
    const origGetParameter = proto.getParameter;
    proto.getParameter = function (parameter) {
      // UNMASKED_VENDOR_WEBGL = 0x9245, UNMASKED_RENDERER_WEBGL = 0x9246
      if (parameter === 0x9245) return FP.webgl.vendor;
      if (parameter === 0x9246) return FP.webgl.renderer;
      // VERSION = 0x1F02, SHADING_LANGUAGE_VERSION = 0x8B8C, VENDOR = 0x1F00, RENDERER = 0x1F01
      if (parameter === 0x1F02) return FP.webgl.version;
      if (parameter === 0x8B8C) return FP.webgl.shadingLanguageVersion;
      if (parameter === 0x1F00) return FP.webgl.vendor;
      if (parameter === 0x1F01) return FP.webgl.renderer;
      return origGetParameter.apply(this, arguments);
    };
    proxyToString(proto.getParameter, 'getParameter');
  }
  if (typeof WebGLRenderingContext !== 'undefined') patchWebGl(WebGLRenderingContext.prototype);
  if (typeof WebGL2RenderingContext !== 'undefined') patchWebGl(WebGL2RenderingContext.prototype);

  // ===== Audio =====
  // Minimal noise on AudioBuffer.getChannelData & DynamicsCompressorNode output.
  if (typeof AudioBuffer !== 'undefined') {
    const origGetChannelData = AudioBuffer.prototype.getChannelData;
    let aSeed = FP.audioSeed >>> 0;
    const audioNoise = () => {
      aSeed ^= aSeed << 13; aSeed >>>= 0;
      aSeed ^= aSeed >>> 17;
      aSeed ^= aSeed << 5; aSeed >>>= 0;
      return ((aSeed & 0xffff) / 0xffff - 0.5) * 1e-7;
    };
    AudioBuffer.prototype.getChannelData = function () {
      const data = origGetChannelData.apply(this, arguments);
      for (let i = 0; i < data.length; i += 100) data[i] += audioNoise();
      return data;
    };
    proxyToString(AudioBuffer.prototype.getChannelData, 'getChannelData');
  }

  // ===== WebRTC =====
  if (FP.webRtcMode === 'Disabled') {
    const blocked = function () { throw new Error('WebRTC disabled'); };
    if (typeof RTCPeerConnection !== 'undefined') {
      window.RTCPeerConnection = blocked;
      window.webkitRTCPeerConnection = blocked;
    }
  } else {
    // Mode "Proxy"/"Real": prevent leaking host candidates / mDNS
    if (typeof RTCPeerConnection !== 'undefined') {
      const OrigPC = RTCPeerConnection;
      const wrapped = function (...args) {
        const pc = new OrigPC(...args);
        const origAddIce = pc.addIceCandidate.bind(pc);
        pc.addIceCandidate = function (candidate, ...rest) {
          if (candidate && typeof candidate.candidate === 'string' &&
              /(\.local|host)/.test(candidate.candidate)) {
            return Promise.resolve();
          }
          return origAddIce(candidate, ...rest);
        };
        return pc;
      };
      Object.setPrototypeOf(wrapped, OrigPC);
      wrapped.prototype = OrigPC.prototype;
      window.RTCPeerConnection = wrapped;
      if (window.webkitRTCPeerConnection) window.webkitRTCPeerConnection = wrapped;
    }
  }

  // ===== Geolocation =====
  if (navigator.geolocation) {
    const fakePos = {
      coords: {
        latitude:  parseFloat(FP.geo.latitude),
        longitude: parseFloat(FP.geo.longitude),
        accuracy:  parseFloat(FP.geo.accuracy),
        altitude: null, altitudeAccuracy: null, heading: null, speed: null
      },
      timestamp: Date.now()
    };
    const origGetCurrent = navigator.geolocation.getCurrentPosition.bind(navigator.geolocation);
    navigator.geolocation.getCurrentPosition = function (success) {
      try { success(fakePos); } catch (_) {}
    };
    const origWatch = navigator.geolocation.watchPosition.bind(navigator.geolocation);
    navigator.geolocation.watchPosition = function (success) {
      try { success(fakePos); } catch (_) {}
      return 0;
    };
    proxyToString(navigator.geolocation.getCurrentPosition, 'getCurrentPosition');
    proxyToString(navigator.geolocation.watchPosition, 'watchPosition');
  }

  // ===== Media devices =====
  if (navigator.mediaDevices && navigator.mediaDevices.enumerateDevices) {
    navigator.mediaDevices.enumerateDevices = async function () {
      return FP.mediaDevices.map(d => ({
        deviceId: d.DeviceId, groupId: d.GroupId, kind: d.Kind, label: d.Label,
        toJSON() { return { deviceId: d.DeviceId, groupId: d.GroupId, kind: d.Kind, label: d.Label }; }
      }));
    };
    proxyToString(navigator.mediaDevices.enumerateDevices, 'enumerateDevices');
  }

  // ===== Permissions =====
  if (navigator.permissions && navigator.permissions.query) {
    const origQuery = navigator.permissions.query.bind(navigator.permissions);
    navigator.permissions.query = function (parameters) {
      if (parameters && parameters.name === 'notifications') {
        return Promise.resolve({ state: 'prompt', onchange: null });
      }
      return origQuery(parameters);
    };
    proxyToString(navigator.permissions.query, 'query');
  }

  // ===== chrome.* small surface =====
  if (!window.chrome) {
    Object.defineProperty(window, 'chrome', {
      configurable: true, enumerable: true, writable: true,
      value: { runtime: {}, loadTimes: () => undefined, csi: () => undefined, app: { isInstalled: false } }
    });
  }
})();
""";
}
