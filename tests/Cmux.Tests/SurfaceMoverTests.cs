using Cmux.Core.Models;
using Cmux.Core.Services;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class SurfaceMoverTests
{
    private static (Workspace ws, Surface s1, Surface s2) MakeWorkspace(string name, int surfaceCount = 2)
    {
        var ws = new Workspace { Name = name };
        var s1 = new Surface { Name = $"{name}/T1" };
        var s2 = new Surface { Name = $"{name}/T2" };
        ws.Surfaces.Add(s1);
        if (surfaceCount >= 2) ws.Surfaces.Add(s2);
        ws.SelectedSurface = s1;
        return (ws, s1, s2);
    }

    [Fact]
    public void Move_TransfersSurfaceToTarget()
    {
        var (a, a1, _) = MakeWorkspace("A");
        var (b, _, _) = MakeWorkspace("B");

        var result = SurfaceMover.Move(a, b, a1);

        result.Moved.Should().BeTrue();
        a.Surfaces.Should().NotContain(a1);
        b.Surfaces.Should().Contain(a1);
    }

    [Fact]
    public void Move_SetsTargetSelectedToMovedSurface()
    {
        var (a, a1, _) = MakeWorkspace("A");
        var (b, b1, _) = MakeWorkspace("B");

        SurfaceMover.Move(a, b, a1);

        b.SelectedSurface.Should().BeSameAs(a1, "target should focus the just-moved tab");
        b.SelectedSurface.Should().NotBeSameAs(b1);
    }

    [Fact]
    public void Move_WhenMovedSurfaceWasSourceSelected_PicksAnotherSelection()
    {
        var (a, a1, a2) = MakeWorkspace("A");
        var (b, _, _) = MakeWorkspace("B");
        a.SelectedSurface = a1;

        SurfaceMover.Move(a, b, a1);

        a.SelectedSurface.Should().BeSameAs(a2);
    }

    [Fact]
    public void Move_LastSurfaceFromSource_FlagsReplacementNeeded()
    {
        // Regression: the previous "refuse if last tab" guard made the menu
        // appear to do nothing. The right behavior is to allow the move and
        // signal that the source needs a replacement surface.
        var (a, a1, _) = MakeWorkspace("A", surfaceCount: 1);
        var (b, _, _) = MakeWorkspace("B");

        var result = SurfaceMover.Move(a, b, a1);

        result.Moved.Should().BeTrue();
        result.SourceNeedsReplacementSurface.Should().BeTrue();
        a.Surfaces.Should().BeEmpty();
        b.Surfaces.Should().Contain(a1);
    }

    [Fact]
    public void Move_WhenTargetIsSource_NoOp()
    {
        var (a, a1, _) = MakeWorkspace("A");

        var result = SurfaceMover.Move(a, a, a1);

        result.Moved.Should().BeFalse();
        a.Surfaces.Should().Contain(a1);
    }

    [Fact]
    public void Move_WhenSurfaceNotInSource_NoOp()
    {
        var (a, _, _) = MakeWorkspace("A");
        var (b, _, _) = MakeWorkspace("B");
        var stranger = new Surface { Name = "stranger" };

        var result = SurfaceMover.Move(a, b, stranger);

        result.Moved.Should().BeFalse();
        b.Surfaces.Should().NotContain(stranger);
    }

    [Fact]
    public void Move_DoesNotDuplicateSurfaceInTarget()
    {
        var (a, a1, _) = MakeWorkspace("A");
        var (b, _, _) = MakeWorkspace("B");

        SurfaceMover.Move(a, b, a1);

        b.Surfaces.Count(s => ReferenceEquals(s, a1)).Should().Be(1);
    }
}
