namespace Reorder.Services;

/// <summary>
/// Renames a set of files in two phases (source -> temp -> target) so that
/// targets may freely overlap with existing source names. Rolls back on failure.
/// </summary>
public static class BatchRenamer
{
    public static void Rename(IReadOnlyList<(string Source, string Target)> plan)
    {
        var sources = new HashSet<string>(plan.Select(p => p.Source), StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, target) in plan)
        {
            if (!targets.Add(target))
                throw new InvalidOperationException(Loc.Format("Error_DuplicateTarget", Path.GetFileName(target)));
            if (File.Exists(target) && !sources.Contains(target))
                throw new IOException(Loc.Format("Error_TargetExists", Path.GetFileName(target)));
        }

        // Ordinal comparison so that case-only changes are still applied.
        var steps = plan.Where(p => !string.Equals(p.Source, p.Target, StringComparison.Ordinal)).ToList();
        var done = new List<(string From, string To)>(steps.Count * 2);
        var temps = new string[steps.Count];

        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                string dir = Path.GetDirectoryName(steps[i].Source)!;
                temps[i] = Path.Combine(dir, $".reorder-{Guid.NewGuid():N}{Path.GetExtension(steps[i].Source)}");
                File.Move(steps[i].Source, temps[i]);
                done.Add((steps[i].Source, temps[i]));
            }

            for (int i = 0; i < steps.Count; i++)
            {
                File.Move(temps[i], steps[i].Target);
                done.Add((temps[i], steps[i].Target));
            }
        }
        catch
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                try { File.Move(done[i].To, done[i].From); }
                catch { /* best effort rollback */ }
            }
            throw;
        }
    }
}
