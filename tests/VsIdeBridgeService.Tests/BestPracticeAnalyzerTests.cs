using System.Linq;
using VsIdeBridge.Diagnostics;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class BestPracticeAnalyzerTests
{
    [Theory]
    [InlineData("sample.cs", "/// version 2\n// version 2\n/* version 2\nversion 2 */\nint value = 7;\n")]
    [InlineData("sample.cpp", "/* version 2\nversion 2\nversion 2\nversion 2 */\nint value = 7;\n")]
    [InlineData("sample.py", "# version 2\n# version 2\n# version 2\n# version 2\nvalue = 7\n")]
    [InlineData("sample.ps1", "# version 2\n# version 2\n# version 2\n# version 2\n$value = 7\n")]
    [InlineData("sample.vb", "' version 2\n' version 2\n' version 2\n' version 2\nDim value = 7\n")]
    [InlineData("sample.fs", "(* version 2\nversion 2\nversion 2\nversion 2 *)\nlet value = 7\n")]
    [InlineData("sample.csproj", "<!-- version 2\nversion 2\nversion 2\nversion 2 -->\n<Project />\n")]
    [InlineData(".editorconfig", "; version 2\n; version 2\n; version 2\n; version 2\nroot = true\n")]
    public void FindMagicNumbersIgnoresCommentLiterals(string file, string content)
    {
        Assert.DoesNotContain(BestPracticeAnalyzer.FindMagicNumbers(file, content),
            row => row["code"]?.ToString() == "BP1002");
    }

    [Fact]
    public void FindMagicNumbersStillReportsRepeatedCodeLiterals()
    {
        string content =
            "int firstCount = 42;\n" +
            "int secondCount = 42;\n" +
            "int thirdCount = 42;\n" +
            "int fourthCount = 42;\n";

        Newtonsoft.Json.Linq.JObject finding = Assert.Single(BestPracticeAnalyzer.FindMagicNumbers("sample.cs", content));
        Assert.Equal("BP1002", finding["code"]?.ToString());
        Assert.Equal("42", finding["symbol"]?.ToString());
    }
}

