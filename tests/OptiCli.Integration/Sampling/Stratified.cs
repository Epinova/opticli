using OptiCli.Core.Cms;

namespace OptiCli.Integration.Sampling;

/// <summary>
/// Picks a sample that covers the site rather than its most common type: round-robin over kinds (pages,
/// blocks, media, folders, other), within a kind over content types, within a type over languages (starting
/// at a different language for each type), and within those in a seeded random order. Taking the first N
/// therefore spreads N as widely as possible.
/// </summary>
internal static class Stratified
{
    public static IReadOnlyList<SampleItem> Take(IEnumerable<SampleItem> candidates, int count, int seed)
    {
        var random = new Random(seed);
        var byKind = candidates
            .OrderBy(c => c.ContentId).ThenBy(c => c.LanguageId)
            .Select(c => (Item: c, Order: random.Next()))
            .GroupBy(c => c.Item.Kind)
            .OrderBy(g => g.Key)
            .Select(kind => Interleave(kind
                .GroupBy(c => c.Item.TypeId)
                .OrderBy(g => g.Key)
                .Select(type => Interleave(type
                    .GroupBy(c => c.Item.LanguageId)
                    .OrderBy(g => Mix(g.Key, type.Key, seed))
                    .Select(language => language.OrderBy(c => c.Order).Select(c => c.Item))))));
        return Interleave(byKind).Take(count).ToList();
    }

    /// <summary>A stable pseudo-random order key (<see cref="HashCode"/> differs between processes).</summary>
    private static uint Mix(int a, int b, int seed) => unchecked((((uint)a * 2654435761u) ^ ((uint)b * 40503u) ^ (uint)seed) * 2246822519u);

    /// <summary>One element from each sequence in turn until all are exhausted.</summary>
    public static IEnumerable<T> Interleave<T>(IEnumerable<IEnumerable<T>> sequences)
    {
        var enumerators = sequences.Select(s => s.GetEnumerator()).ToList();
        try
        {
            while (enumerators.Count > 0)
            {
                for (var i = 0; i < enumerators.Count;)
                {
                    if (enumerators[i].MoveNext())
                    {
                        yield return enumerators[i].Current;
                        i++;
                    }
                    else
                    {
                        enumerators[i].Dispose();
                        enumerators.RemoveAt(i);
                    }
                }
            }
        }
        finally
        {
            enumerators.ForEach(e => e.Dispose());
        }
    }
}
