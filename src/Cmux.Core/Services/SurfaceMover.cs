using Cmux.Core.Models;

namespace Cmux.Core.Services;

/// <summary>
/// Pure logic for moving a <see cref="Surface"/> (terminal tab) between two
/// <see cref="Workspace"/>s. Extracted from the WPF view-model layer so the
/// invariants can be tested without standing up the full app.
/// </summary>
public static class SurfaceMover
{
    public sealed record Result(bool Moved, bool SourceNeedsReplacementSurface);

    /// <summary>
    /// Moves <paramref name="surface"/> from <paramref name="source"/> to
    /// <paramref name="target"/>. Returns whether the move happened and whether
    /// the source workspace was emptied (caller should add a replacement
    /// surface so workspaces never become empty).
    /// </summary>
    public static Result Move(Workspace source, Workspace target, Surface surface)
    {
        if (source == null || target == null || surface == null)
            return new Result(false, false);

        if (ReferenceEquals(source, target))
            return new Result(false, false);

        if (!source.Surfaces.Contains(surface))
            return new Result(false, false);

        source.Surfaces.Remove(surface);
        target.Surfaces.Add(surface);

        // If the moved surface was the source's selected one, pick a new selection.
        if (ReferenceEquals(source.SelectedSurface, surface))
            source.SelectedSurface = source.Surfaces.Count > 0 ? source.Surfaces[^1] : null;

        target.SelectedSurface = surface;

        return new Result(true, source.Surfaces.Count == 0);
    }
}
