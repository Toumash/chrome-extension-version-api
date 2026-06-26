var tests = new (string Name, Action Run)[]
{
    ("extracts update service version", ExtractsUpdateServiceVersion),
    ("ignores update service unknown application", IgnoresUnknownApplication),
    ("does not extract version from consent page", DoesNotExtractFromConsentPage),
    ("ignores update service response for another extension", IgnoresAnotherExtension)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void ExtractsUpdateServiceVersion()
{
    const string extensionId = "bgkpehhnbobnalcmakjmllahilkcfelp";
    const string response = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gupdate xmlns="http://www.google.com/update2/response" protocol="2.0" server="prod">
          <app appid="bgkpehhnbobnalcmakjmllahilkcfelp" status="ok">
            <updatecheck status="ok" version="4.180.1" />
          </app>
        </gupdate>
        """;

    AssertEqual("4.180.1", VersionExtractor.Extract(response, extensionId));
}

static void IgnoresUnknownApplication()
{
    const string extensionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string response = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gupdate xmlns="http://www.google.com/update2/response" protocol="2.0" server="prod">
          <app appid="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" status="error-unknownApplication" />
        </gupdate>
        """;

    AssertNull(VersionExtractor.Extract(response, extensionId));
}

static void DoesNotExtractFromConsentPage()
{
    const string extensionId = "bgkpehhnbobnalcmakjmllahilkcfelp";
    const string response = """
        <!doctype html>
        <html>
          <head><base href="https://consent.google.com/"><title>Before you continue</title></head>
          <body><input type="hidden" name="continue" value="https://chromewebstore.google.com/detail/_/bgkpehhnbobnalcmakjmllahilkcfelp"></body>
        </html>
        """;

    AssertNull(VersionExtractor.Extract(response, extensionId));
}

static void IgnoresAnotherExtension()
{
    const string extensionId = "bgkpehhnbobnalcmakjmllahilkcfelp";
    const string response = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gupdate xmlns="http://www.google.com/update2/response" protocol="2.0" server="prod">
          <app appid="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" status="ok">
            <updatecheck status="ok" version="9.9.9" />
          </app>
        </gupdate>
        """;

    AssertNull(VersionExtractor.Extract(response, extensionId));
}

static void AssertEqual(string expected, string? actual)
{
    if (actual != expected)
        throw new InvalidOperationException($"Expected '{expected}', got '{actual ?? "<null>"}'.");
}

static void AssertNull(string? actual)
{
    if (actual is not null)
        throw new InvalidOperationException($"Expected '<null>', got '{actual}'.");
}
