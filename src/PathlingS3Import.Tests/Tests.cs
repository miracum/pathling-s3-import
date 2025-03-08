using FluentAssertions;

namespace PathlingS3Import.Tests;

public class Tests()
{
    [Fact]
    public void Sort_ShouldSortObjectsInExpectedOrder()
    {
        var objects =
            @"
        1736445718918-0-0.ndjson
        1736445723943-0-2.ndjson
        1736445728954-0-4.ndjson
        1736445728954-3-5.ndjson
        1736445733962-0-6.ndjson
        1736445738973-0-8.ndjson
        1736445746202-0-10.ndjson
        1736445718918-1-0.ndjson
        1736445751212-0-12.ndjson
        1736445756219-0-14.ndjson
        1736445761231-0-16.ndjson
        1736445766242-0-18.ndjson
        1736445766242-0-19.ndjson
        1736445771250-0-20.ndjson
        1736445778604-0-22.ndjson
        1736445783611-0-24.ndjson
        1736445788620-0-26.ndjson
        1736445718918-0-1.ndjson
        1736445718918-2-3.ndjson
        1736445718918-1-1.ndjson
        1736445718918-1-4.ndjson
        1736445723943-0-3.ndjson
        1736445733962-0-7.ndjson
        1736445738973-0-9.ndjson
        1736445746202-0-11.ndjson";

        var expected =
            @"
        1736445718918-0-0.ndjson
        1736445718918-0-1.ndjson
        1736445718918-1-0.ndjson
        1736445718918-1-1.ndjson
        1736445718918-1-4.ndjson
        1736445718918-2-3.ndjson
        1736445723943-0-2.ndjson
        1736445723943-0-3.ndjson
        1736445728954-0-4.ndjson
        1736445728954-3-5.ndjson
        1736445733962-0-6.ndjson
        1736445733962-0-7.ndjson
        1736445738973-0-8.ndjson
        1736445738973-0-9.ndjson
        1736445746202-0-10.ndjson
        1736445746202-0-11.ndjson
        1736445751212-0-12.ndjson
        1736445756219-0-14.ndjson
        1736445761231-0-16.ndjson
        1736445766242-0-18.ndjson
        1736445766242-0-19.ndjson
        1736445771250-0-20.ndjson
        1736445778604-0-22.ndjson
        1736445783611-0-24.ndjson
        1736445788620-0-26.ndjson";

        var sorted = string.Join("\n", objects.Split('\n').OrderBy(x => x));

        sorted.Should().Be(expected);
    }
}
