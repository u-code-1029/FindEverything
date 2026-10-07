using FindEverything.Engine;
using Xunit;

namespace FindEverything.Engine.Tests;

public sealed class DirectoryDiscoveryTests
{
    [Fact]
    public async Task PrunedDirectoryIsReportedButItsDescendantsAreSkippedAndSiblingsContinue()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("terminal/deep/hidden.txt");
        workspace.WriteFile("sibling/leaf/keep.txt");
        var candidates = new List<DirectoryCandidate>();
        var progress = new List<DirectoryDiscoveryProgress>();

        var report = await new FileSystemMetadataScanner().DiscoverDirectoriesAsync(
            Request(workspace), candidate =>
            {
                candidates.Add(candidate);
                return candidate.Name == "terminal"
                    ? DirectoryTraversalDecision.SkipDescendants
                    : DirectoryTraversalDecision.Continue;
            }, new InlineProgress<DirectoryDiscoveryProgress>(progress.Add));

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Empty(report.Errors);
        Assert.Contains(candidates, candidate => candidate.Name == "terminal");
        Assert.DoesNotContain(candidates, candidate => candidate.Name == "deep");
        Assert.Contains(candidates, candidate => candidate.Name == "sibling");
        Assert.Contains(candidates, candidate => candidate.Name == "leaf");
        Assert.Equal((long)candidates.Count, report.Progress.Directories);
        Assert.Equal(1, report.Progress.PrunedDirectories);
        Assert.Equal(report.Progress, Assert.IsType<DirectoryDiscoveryProgress>(progress[^1]));
    }

    [Fact]
    public async Task DiscoveryScopeCanBeReportedAndPrunedBeforeItIsOpened()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("deep/child/file.txt");
        var candidates = new List<DirectoryCandidate>();

        var report = await new FileSystemMetadataScanner().DiscoverDirectoriesAsync(
            Request(workspace), candidate =>
            {
                candidates.Add(candidate);
                return DirectoryTraversalDecision.SkipDescendants;
            });

        var root = Assert.Single(candidates);
        Assert.Equal(PathRules.Normalize(workspace.SourcePath), root.FullPath);
        Assert.Equal(0, root.Depth);
        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Equal(0, report.Progress.Entries);
        Assert.Equal(1, report.Progress.Directories);
        Assert.Equal(1, report.Progress.PrunedDirectories);
    }

    [Fact]
    public async Task ExcludedDirectoriesArePrunedBeforeTheCallback()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("excluded/deep/hidden.txt");
        workspace.WriteFile("retained/keep.txt");
        var candidates = new List<DirectoryCandidate>();
        var request = Request(workspace) with
        {
            Options = TestWorkspace.FastOptions with { ExcludedDirectoryNames = ["excluded"] }
        };

        var report = await new FileSystemMetadataScanner().DiscoverDirectoriesAsync(
            request, candidate =>
            {
                candidates.Add(candidate);
                return DirectoryTraversalDecision.Continue;
            });

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.DoesNotContain(candidates, candidate => candidate.Name is "excluded" or "deep");
        Assert.Contains(candidates, candidate => candidate.Name == "retained");
        Assert.Equal(1, report.Progress.ExcludedEntries);
        Assert.Equal(1, report.Diagnostics.Exclusions.Directories);
    }

    [Fact]
    public async Task IndexDeferralRulesDoNotHideDirectoriesFromExplicitDiscovery()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("deferred/deep/file.txt");
        var candidates = new List<DirectoryCandidate>();
        var request = Request(workspace) with
        {
            Options = TestWorkspace.FastOptions with
            {
                Deferral = new DeferralPolicy { DirectoryNames = ["deferred"] }
            }
        };

        var report = await new FileSystemMetadataScanner().DiscoverDirectoriesAsync(
            request, candidate =>
            {
                candidates.Add(candidate);
                return DirectoryTraversalDecision.Continue;
            });

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Contains(candidates, candidate => candidate.Name == "deferred");
        Assert.Contains(candidates, candidate => candidate.Name == "deep");
    }

    [LinuxFact]
    public async Task DirectoryLinksAreSkippedBeforeTheCallback()
    {
        using var workspace = new TestWorkspace();
        var outside = Path.Combine(workspace.BasePath, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "deep"));
        Directory.CreateSymbolicLink(Path.Combine(workspace.SourcePath, "link"), outside);
        workspace.WriteFile("retained/keep.txt");
        var candidates = new List<DirectoryCandidate>();

        var report = await new FileSystemMetadataScanner().DiscoverDirectoriesAsync(
            Request(workspace), candidate =>
            {
                candidates.Add(candidate);
                return DirectoryTraversalDecision.Continue;
            });

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.DoesNotContain(candidates, candidate => candidate.Name is "link" or "deep");
        Assert.Contains(candidates, candidate => candidate.Name == "retained");
        Assert.Equal(1, report.Progress.SkippedLinks);
    }

    [Fact]
    public async Task CancellationRequestedByTheCallbackReturnsACancelledReport()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("child/file.txt");
        using var cancellation = new CancellationTokenSource();
        var candidates = new List<DirectoryCandidate>();

        var report = await new FileSystemMetadataScanner().DiscoverDirectoriesAsync(
            Request(workspace), candidate =>
            {
                candidates.Add(candidate);
                cancellation.Cancel();
                return DirectoryTraversalDecision.Continue;
            }, cancellationToken: cancellation.Token);

        Assert.Equal(ScanStatus.Cancelled, report.Status);
        Assert.Single(candidates);
        Assert.Equal(1, report.Progress.Directories);
        Assert.Equal(0, report.Progress.Entries);
    }

    [Fact]
    public async Task CallbackExceptionsPropagateWithoutBecomingFileSystemErrors()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteFile("child/file.txt");
        var expected = new InvalidOperationException("Profile evaluation failed.");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FileSystemMetadataScanner().DiscoverDirectoriesAsync(
                Request(workspace), _ => throw expected));

        Assert.Same(expected, actual);
    }

    private static DirectoryDiscoveryRequest Request(TestWorkspace workspace) =>
        new(workspace.SourcePath) { Options = TestWorkspace.FastOptions };
}
