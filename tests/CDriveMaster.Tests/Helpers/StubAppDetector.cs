using CDriveMaster.Core.Interfaces;

namespace CDriveMaster.Tests.Helpers;

public sealed class StubAppDetector : IAppDetector
{
    private readonly DetectionResult result;

    public StubAppDetector(string basePath)
    {
        result = new DetectionResult(
            Found: true,
            BasePath: basePath,
            Source: "TestStub",
            Reason: "Injected by test.");
    }

    public StubAppDetector(DetectionResult result)
    {
        this.result = result;
    }

    public string AppName => "WeChat";

    public DetectionResult Detect() => result;
}
