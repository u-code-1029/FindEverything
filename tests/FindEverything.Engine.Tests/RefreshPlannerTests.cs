using FindEverything.Engine;
using Xunit;

namespace FindEverything.Engine.Tests;

public sealed class RefreshPlannerTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "FindEverything-planner", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AncestorScopeCoversNestedInvalidationsButPreservesSiblings()
    {
        var root = NewRoot();
        var options = TestWorkspace.FastOptions;
        var scopes = RefreshPlanner.Plan(root,
        [
            Path.Combine(root, "projects", "alpha", "docs"),
            Path.Combine(root, "projects", "beta"),
            Path.Combine(root, "projects", "alpha"),
            Path.Combine(root, "archive")
        ], options);

        Assert.Equal(new[]
        {
            Path.Combine(root, "archive"),
            Path.Combine(root, "projects", "alpha"),
            Path.Combine(root, "projects", "beta")
        }, scopes.Select(request => request.ScopePath));
        Assert.All(scopes, request =>
        {
            Assert.Equal(root, request.RootPath);
            Assert.Same(options, request.Options);
        });
    }

    [Fact]
    public void SimilarPathPrefixesRemainIndependentScopes()
    {
        var root = NewRoot();
        var foo = Path.Combine(root, "foo");
        var foobar = Path.Combine(root, "foobar", "nested");

        var scopes = RefreshPlanner.Plan(root, [foobar, Path.Combine(foo, "child"), foo]);

        Assert.Equal(new[] { foo, foobar }, scopes.Select(request => request.ScopePath));
    }

    [Fact]
    public void NormalizedDuplicatesAndInputOrderDoNotChangePlan()
    {
        var root = NewRoot();
        var alpha = Path.Combine(root, "alpha");
        var zeta = Path.Combine(root, "zeta");
        string[] paths = [zeta, alpha + Path.DirectorySeparatorChar, Path.Combine(alpha, "child", ".."), alpha, zeta];

        var forward = RefreshPlanner.Plan(root, paths);
        var backward = RefreshPlanner.Plan(root, paths.Reverse());

        Assert.Equal(new[] { alpha, zeta }, forward.Select(request => request.ScopePath));
        Assert.Equal(forward.Select(request => request.ScopePath), backward.Select(request => request.ScopePath));
    }

    [Fact]
    public void ExclusionOfAnAncestorPrunesDeepScopesWithPathBoundaries()
    {
        var root = NewRoot();
        var options = TestWorkspace.FastOptions with
        {
            ExcludedDirectoryNames = ["Cache"],
            ExcludedPaths = [Path.Combine(root, "private")]
        };
        var kept = Path.Combine(root, "private-backup", "documents");

        var scopes = RefreshPlanner.Plan(root,
        [
            Path.Combine(root, "projects", "Cache", "nested", "deeper"),
            Path.Combine(root, "private", "nested", "deeper"),
            Path.Combine(root, "private"),
            kept
        ], options);

        Assert.Equal(kept, Assert.Single(scopes).ScopePath);
    }

    [Fact]
    public void RootInvalidationDominatesAllOtherScopes()
    {
        var root = NewRoot();

        var scopes = RefreshPlanner.Plan(root,
            [Path.Combine(root, "one", "nested"), root + Path.DirectorySeparatorChar, Path.Combine(root, "two")]);

        Assert.Equal(root, Assert.Single(scopes).ScopePath);
    }

    [Fact]
    public void ExcludedRootProducesNoRefreshes()
    {
        var root = Path.Combine(NewRoot(), "Cache");
        var options = new ScanOptions { ExcludedDirectoryNames = ["Cache"] };

        Assert.Empty(RefreshPlanner.Plan(root, [root, Path.Combine(root, "nested")], options));
        Assert.Empty(RefreshPlanner.Plan(root, [root], new ScanOptions { ExcludedPaths = [root] }));
    }

    [Fact]
    public void OutsideRootIsRejectedEvenWhenRootIsAlreadyInvalidated()
    {
        var root = NewRoot();

        Assert.Throws<ArgumentException>(() => RefreshPlanner.Plan(root, [root, root + "-other"]));
    }

    [Fact]
    public void MissingDirectoriesStillProducePlansWithoutCreatingFiles()
    {
        var root = NewRoot();
        var missing = Path.Combine(root, "not-created", "nested");
        Assert.False(Directory.Exists(root));

        var request = Assert.Single(RefreshPlanner.Plan(root, [missing]));

        Assert.Equal(missing, request.ScopePath);
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(root));
    }

    [Fact]
    public void EmptyInputReturnsEmptyAndStillValidatesOptions()
    {
        var root = NewRoot();

        Assert.Empty(RefreshPlanner.Plan(root, []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RefreshPlanner.Plan(root, [], new ScanOptions { MaxEntriesPerSecond = 0 }));
        Assert.Throws<ArgumentException>(() =>
            RefreshPlanner.Plan(root, [], new ScanOptions { ExcludedPaths = [root + "-outside"] }));
    }

    [Fact]
    public void DirectoryNameExclusionsUseHostPathCaseSemantics()
    {
        var root = NewRoot();
        var scopes = RefreshPlanner.Plan(root, [Path.Combine(root, "cache", "nested")],
            new ScanOptions { ExcludedDirectoryNames = ["Cache"] });

        if (OperatingSystem.IsWindows())
            Assert.Empty(scopes);
        else
            Assert.Single(scopes);
    }

    [Fact]
    public void CaseEquivalentDuplicatesUseDeterministicHostSemantics()
    {
        var root = NewRoot();
        var uppercase = Path.Combine(root, "Alpha");
        var lowercase = Path.Combine(root, "alpha");

        var forward = RefreshPlanner.Plan(root, [uppercase, lowercase]);
        var backward = RefreshPlanner.Plan(root, [lowercase, uppercase]);

        Assert.Equal(forward.Select(request => request.ScopePath), backward.Select(request => request.ScopePath));
        if (OperatingSystem.IsWindows())
            Assert.Equal(uppercase, Assert.Single(forward).ScopePath);
        else
            Assert.Equal(2, forward.Count);
    }
}
