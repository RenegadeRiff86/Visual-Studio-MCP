using System.Text;
using System.Text.Json.Nodes;
using VsIdeBridge.Tooling.Patches;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class ApplyDiffRequestTests
{
    [Fact]
    public void ValidatesEditorPatchAndExposesMetadata()
    {
        const string Patch = "*** Begin Patch\n" +
            "*** Update File: src/Foo.cs\n" +
            "@@\n" +
            " public sealed class Foo\n" +
            "-old\n" +
            "+new\n" +
            "*** End Patch";

        ApplyDiffRequest request = ApplyDiffRequest.FromPatchText(Patch);

        Assert.Equal(1, request.FileCount);
        Assert.Equal(2, request.MutationLineCount);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(Patch)), request.EncodedDiff);
        Assert.Equal("src/Foo.cs", request.Operations.Single().Path);
    }

    [Fact]
    public void AcceptsMoveWithoutHunks()
    {
        const string Patch = "*** Begin Patch\n" +
            "*** Update File: src/Old.cs\n" +
            "*** Move to: src/New.cs\n" +
            "*** End Patch";

        ApplyDiffRequest request = ApplyDiffRequest.FromPatchText(Patch);

        Assert.Equal("src/New.cs", request.Operations.Single().MoveTo);
    }

    [Fact]
    public void SimpleReplaceBuildsSupportedEditorPatch()
    {
        const string OldContent = @"var typedValue = Regex.Replace(input, @""[^ \t,<>\[\]]*\."", string.Empty);";
        const string NewContent = @"string typedValue = Regex.Replace(input, @""[^ \t,<>\[\]]*\."", string.Empty);";
        JsonObject args = new()
        {
            ["file"] = "CodeMaidShared/Helpers/TypeFormatHelper.cs",
            ["old_content"] = OldContent,
            ["new_content"] = NewContent,
        };

        ApplyDiffRequest request = ApplyDiffRequest.FromJsonObject(args);

        Assert.Equal(2, request.MutationLineCount);
        Assert.Contains("*** Update File: CodeMaidShared/Helpers/TypeFormatHelper.cs\n@@\n", request.Diff);
        Assert.Contains("-" + OldContent + "\n", request.Diff);
        Assert.Contains("+" + NewContent + "\n", request.Diff);
        Assert.DoesNotContain("*** Replace in File:", request.Diff);
    }

    [Fact]
    public void RejectsMixedTargetedAndDiffShapes()
    {
        JsonObject args = new()
        {
            ["file"] = "src/Foo.cs",
            ["old_content"] = "old",
            ["new_content"] = "new",
            ["diff"] = "*** Begin Patch\n*** Update File: src/Foo.cs\n@@\n-old\n+new\n*** End Patch",
        };

        ApplyDiffValidationException exception = Assert.Throws<ApplyDiffValidationException>(() => ApplyDiffRequest.FromJsonObject(args));

        Assert.Contains("Use exactly one shape", exception.Message);
    }

    [Fact]
    public void RejectsSimpleSingleFileDiffToPreferTargetedForm()
    {
        const string Patch = "*** Begin Patch\n" +
            "*** Update File: src/Foo.cs\n" +
            "@@\n" +
            "-old\n" +
            "+new\n" +
            "*** End Patch";

        ApplyDiffValidationException exception = Assert.Throws<ApplyDiffValidationException>(() => ApplyDiffRequest.FromPatchText(Patch));

        Assert.Contains("file' + 'old_content' + 'new_content", exception.Message);
    }

    [Fact]
    public void RejectsFullFileWriteShape()
    {
        JsonObject args = new()
        {
            ["file"] = "src/Foo.cs",
            ["content"] = "public sealed class Foo { }",
        };

        ApplyDiffValidationException exception = Assert.Throws<ApplyDiffValidationException>(() => ApplyDiffRequest.FromJsonObject(args));

        Assert.Contains("write_file", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--- a/src/Foo.cs\n+++ b/src/Foo.cs")]
    [InlineData("*** Begin Patch\n*** Update File: src/Foo.cs\n@@\n context only\n*** End Patch")]
    [InlineData("*** Begin Patch\n*** Add File: src/Foo.cs\nnot-added\n*** End Patch")]
    [InlineData("*** Begin Patch\n*** Update File: \n@@\n-old\n+new\n*** End Patch")]
    public void RejectsInvalidPatchBeforeBridgeWrite(string patch)
    {
        Assert.Throws<ApplyDiffValidationException>(() => ApplyDiffRequest.FromPatchText(patch));
    }
}
