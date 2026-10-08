using FindEverything.Engine;
using FindEverything.Server;
using FindEverything.Server.Models;
using Xunit;

namespace FindEverything.Server.Tests;

public sealed class ServerPathsTests
{
    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("/absolute")]
    [InlineData("C:\\Data")]
    [InlineData("\\\\server\\share")]
    [InlineData("folder:stream")]
    public void ProfileAndOnDemandScopeCannotEscapeRegisteredSource(string relativePath)
    {
        using var workspace = new ServerWorkspace();
        var paths = new ServerPaths(workspace.Options);
        var validator = new ProfileValidator(paths);
        var definition = workspace.ProfileDefinition();
        Assert.Throws<ArgumentException>(() => validator.Validate(definition with { RelativeRoot = relativePath }));
        var profile = new ScanProfile(Guid.NewGuid(), 1, definition, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => validator.CreateSnapshot(profile, relativePath, onDemand: true));
    }

    [Fact]
    public void DataDirectoryCannotBeInsideOrContainASource()
    {
        using var workspace = new ServerWorkspace();
        workspace.Options.DataDirectory = Path.Combine(workspace.SourcePath, "index");
        Assert.Throws<ArgumentException>(() => new ServerPaths(workspace.Options));
        workspace.Options.DataDirectory = workspace.BasePath;
        Assert.Throws<ArgumentException>(() => new ServerPaths(workspace.Options));
        Assert.False(Directory.Exists(Path.Combine(workspace.SourcePath, "index")));
    }

    [Fact]
    public void ProfileResolvesPortableFiltersInSnapshotWithoutMutatingDefinition()
    {
        using var workspace = new ServerWorkspace();
        var paths = new ServerPaths(workspace.Options);
        var validator = new ProfileValidator(paths);
        var definition = workspace.ProfileDefinition() with
        {
            Options = ServerWorkspace.FastOptions with
            {
                ExcludedPaths = ["Cache/subfolder"],
                Deferral = new DeferralPolicy { Paths = ["Archive"] }
            }
        };
        var profile = new ScanProfile(Guid.NewGuid(), 1, validator.Validate(definition), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var snapshot = validator.CreateSnapshot(profile, "Work", onDemand: true);
        Assert.Equal(Path.Combine(workspace.SourcePath, "Work"), snapshot.ScopePath);
        Assert.Equal(Path.Combine(workspace.SourcePath, "Cache", "subfolder"), Assert.Single(snapshot.Profile.Definition.Options.ExcludedPaths));
        Assert.Equal(Path.Combine(workspace.SourcePath, "Archive"), Assert.Single(snapshot.Profile.Definition.Options.Deferral.Paths));
        Assert.Equal("Cache/subfolder", Assert.Single(profile.Definition.Options.ExcludedPaths));
        Assert.True(snapshot.OnDemand);
    }

    [Fact]
    public void InvalidRegexIsRejectedBeforeAnyDataDirectoryIsCreated()
    {
        using var workspace = new ServerWorkspace();
        var validator = new ProfileValidator(new ServerPaths(workspace.Options));
        var definition = workspace.ProfileDefinition() with
        {
            Options = ServerWorkspace.FastOptions with
            {
                ExcludedDirectoryNameRegexes = [new DirectoryNameRegex { Pattern = "[", MatchMode = RegexMatchMode.Full }]
            }
        };
        Assert.ThrowsAny<ArgumentException>(() => validator.Validate(definition));
        Assert.False(Directory.Exists(workspace.DataPath));
    }

    [Fact]
    public async Task OnlyOneServerInstanceCanOwnDataDirectory()
    {
        using var workspace = new ServerWorkspace();
        var paths = new ServerPaths(workspace.Options);
        await using (await paths.AcquireInstanceLeaseAsync())
            await Assert.ThrowsAsync<IOException>(() => paths.AcquireInstanceLeaseAsync());
        await using var acquiredAfterRelease = await paths.AcquireInstanceLeaseAsync();
    }
}
