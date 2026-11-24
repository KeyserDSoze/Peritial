using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class ObjectRecoveryArchiveEntryCountTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(2, 2, true)]
    [InlineData(3, 2, false)]
    [InlineData(2, 3, false)]
    public void Directory_count_must_exactly_match_manifest(int actual, int expected, bool admitted)
    {
        var entries = Enumerable.Range(0, actual).Select(i => i.ToString());
        Assert.Equal(admitted, ObjectRecoveryArchive.HasExactEntryCount(entries, expected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(100000)]
    public void Excess_entries_stop_enumeration_after_first_surplus(int expected)
    {
        var visited = 0;
        IEnumerable<string> Entries()
        {
            for (var i = 0; i <= expected; i++)
            {
                visited++;
                yield return "entry";
            }
            throw new InvalidOperationException("Enumeration continued beyond the surplus entry.");
        }

        Assert.False(ObjectRecoveryArchive.HasExactEntryCount(Entries(), expected));
        Assert.Equal(expected + 1, visited);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100001)]
    public void Impossible_manifest_count_is_rejected_before_enumeration(int expected)
    {
        IEnumerable<string> Entries()
        {
            yield return "entry";
            throw new InvalidOperationException("Unexpected enumeration.");
        }

        var error = Assert.Throws<InvalidOperationException>(() =>
            ObjectRecoveryArchive.HasExactEntryCount(Entries(), expected));
        Assert.Contains("count", error.Message);
    }
}
