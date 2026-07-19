using Jellyfin.Plugin.LiteTv.Web;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class ScriptInjectionTests
{
    private const string Html = "<html><head><title>x</title></head><body></body></html>";

    [Fact]
    public void AddScriptTag_InsertsBeforeHeadEnd()
    {
        var result = ScriptInjector.AddScriptTag(Html);

        Assert.NotNull(result);
        Assert.Contains(ScriptInjector.ScriptTag + "</head>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AddScriptTag_IsIdempotent()
    {
        var once = ScriptInjector.AddScriptTag(Html);
        Assert.NotNull(once);
        Assert.Null(ScriptInjector.AddScriptTag(once!));
    }

    [Fact]
    public void AddScriptTag_HandlesUppercaseHead()
    {
        var result = ScriptInjector.AddScriptTag("<HTML><HEAD></HEAD><BODY></BODY></HTML>");
        Assert.NotNull(result);
        Assert.Contains(ScriptInjector.ScriptTag, result, StringComparison.Ordinal);
    }

    [Fact]
    public void AddScriptTag_NoHead_ReturnsNull()
    {
        Assert.Null(ScriptInjector.AddScriptTag("<html><body></body></html>"));
    }

    [Fact]
    public void TransformIndexHtml_InjectsTag()
    {
        var result = ScriptInjector.TransformIndexHtml(new TransformationPayload { Contents = Html });
        Assert.Contains(ScriptInjector.ScriptTag, result, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformIndexHtml_UnchangedWhenAlreadyPresent()
    {
        var injected = ScriptInjector.TransformIndexHtml(new TransformationPayload { Contents = Html });
        Assert.Equal(injected, ScriptInjector.TransformIndexHtml(new TransformationPayload { Contents = injected }));
    }

    [Fact]
    public void TransformIndexHtml_EmptyContents_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ScriptInjector.TransformIndexHtml(new TransformationPayload { Contents = string.Empty }));
    }
}
