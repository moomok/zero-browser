using ZeroBrowser.Core.Fingerprint;
using FluentAssertions;
using Xunit;

namespace ZeroBrowser.Tests;

public class FingerprintInjectorTests
{
    private readonly FingerprintInjector _injector = new();
    private readonly FingerprintGenerator _generator = new();

    [Fact]
    public void Placeholder_is_substituted_with_real_payload()
    {
        var fp = _generator.Generate("inject-substitution-seed");
        var script = _injector.BuildPatchScript(fp);

        script.Should().NotContain("__FP_PAYLOAD__");
        script.Should().Contain(fp.UserAgent);
        script.Should().Contain(fp.Timezone);
        script.Should().Contain(fp.WebGlRenderer);
    }

    [Fact]
    public void Patch_script_overrides_critical_navigator_props()
    {
        var fp = _generator.Generate("nav-seed");
        var script = _injector.BuildPatchScript(fp);

        script.Should().Contain("navigator");
        script.Should().Contain("'userAgent'");
        script.Should().Contain("'platform'");
        script.Should().Contain("'languages'");
        script.Should().Contain("'hardwareConcurrency'");
        script.Should().Contain("'webdriver'");
    }

    [Fact]
    public void Patch_script_overrides_canvas_and_webgl()
    {
        var fp = _generator.Generate("canvas-webgl-seed");
        var script = _injector.BuildPatchScript(fp);

        script.Should().Contain("HTMLCanvasElement.prototype.toDataURL");
        script.Should().Contain("CanvasRenderingContext2D.prototype.getImageData");
        script.Should().Contain("0x9245");  // UNMASKED_VENDOR_WEBGL
        script.Should().Contain("0x9246");  // UNMASKED_RENDERER_WEBGL
    }

    [Fact]
    public void Patch_script_handles_intl_timezone()
    {
        var fp = _generator.Generate("tz-seed");
        var script = _injector.BuildPatchScript(fp);

        script.Should().Contain("Intl.DateTimeFormat");
        script.Should().Contain("getTimezoneOffset");
    }

    [Fact]
    public void Patch_script_handles_geolocation()
    {
        var fp = _generator.Generate("geo-seed");
        var script = _injector.BuildPatchScript(fp);

        script.Should().Contain("getCurrentPosition");
        script.Should().Contain("watchPosition");
    }

    [Fact]
    public void Patch_script_preserves_native_toString_appearance()
    {
        var fp = _generator.Generate("native-tostring-seed");
        var script = _injector.BuildPatchScript(fp);

        script.Should().Contain("[native code]");
        script.Should().Contain("Function.prototype.toString");
    }
}
