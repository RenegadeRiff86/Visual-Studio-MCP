using System.Text;
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
