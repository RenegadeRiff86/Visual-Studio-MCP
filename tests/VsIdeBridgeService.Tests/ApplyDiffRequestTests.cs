using System.Reflection;
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
        Assert.Contains("*** Update File: CodeMaidShared/Helpers/TypeFormatHelper.cs\n@@ simple-replace\n", request.Diff);
        Assert.Contains("-" + OldContent + "\n", request.Diff);
        Assert.Contains("+" + NewContent + "\n", request.Diff);
        Assert.DoesNotContain("*** Replace in File:", request.Diff);
    }

    [Fact]
    public void SimpleReplaceHelperReplacesInlineSubstring()
    {
        string updated = SimpleReplacePatch.ReplaceInline(
            "    p.add_argument('--output')",
            "    p.add_argument(",
            "    parser.add_argument(",
            replaceAll: false,
            out int replacementCount);

        Assert.Equal("    parser.add_argument('--output')", updated);
        Assert.Equal(1, replacementCount);
    }

    [Fact]
    public void SimpleReplaceHelperCanReplaceAllOccurrencesOnLine()
    {
        string updated = SimpleReplacePatch.ReplaceInline(
            "old old old",
            "old",
            "new",
            replaceAll: true,
            out int replacementCount);

        Assert.Equal("new new new", updated);
        Assert.Equal(3, replacementCount);
    }

    [Fact]
    public void SimpleReplaceCapturesReplaceAllMetadata()
    {
        JsonObject args = new()
        {
            ["file"] = "src/Foo.cs",
            ["old_content"] = "old",
            ["new_content"] = "new",
            ["replace_all"] = true,
        };

        ApplyDiffRequest request = ApplyDiffRequest.FromJsonObject(args);
        JsonObject metadata = request.ToJsonObject();

        Assert.True(request.ReplaceAll);
        Assert.True(metadata["replaceAll"]!.GetValue<bool>());
    }

    [Fact]
    public void BuildApplyDiffArgsForwardsReplaceAllSwitch()
    {
        JsonObject args = new()
        {
            ["file"] = "src/Foo.cs",
            ["old_content"] = "old",
            ["new_content"] = "new",
            ["replace_all"] = true,
        };
        ApplyDiffRequest request = ApplyDiffRequest.FromJsonObject(args);
        MethodInfo? buildMethod = typeof(ToolCatalog).GetMethod("BuildApplyDiffArgs", BindingFlags.Static | BindingFlags.NonPublic);

        string pipeArgs = Assert.IsType<string>(buildMethod!.Invoke(null, [request]));

        Assert.Contains("--replace-all true", pipeArgs);
    }

    [Fact]
    public void RejectsReplaceAllWithDiffShape()
    {
        JsonObject args = new()
        {
            ["diff"] = "*** Begin Patch\n*** Update File: src/Foo.cs\n@@\n context\n-old\n+new\n*** End Patch",
            ["replace_all"] = true,
        };

        ApplyDiffValidationException exception = Assert.Throws<ApplyDiffValidationException>(() => ApplyDiffRequest.FromJsonObject(args));

        Assert.Contains("replace_all", exception.Message);
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
