using ScadaDarbox.Core.Model;
using Xunit;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// The verdict a declared range gives a reading (ADR-0030).
/// </summary>
/// <remarks>
/// The rule is deliberately total and its ends are inclusive: a range of 0–100 that called 100 out of
/// range would be a range nobody could write down. What these pin is the boundary, because the boundary
/// is the whole of the arithmetic and the part a later reader would "tidy".
/// </remarks>
public sealed class TagRangeTests
{
    private static readonly TagRange Percent = new(0, 100);

    [Theory]
    [InlineData(-0.01, RangeVerdict.BelowRange)]
    [InlineData(0, RangeVerdict.InRange)]
    [InlineData(4.79, RangeVerdict.InRange)]
    [InlineData(100, RangeVerdict.InRange)]
    [InlineData(100.01, RangeVerdict.AboveRange)]
    [InlineData(464, RangeVerdict.AboveRange)]
    public void A_reading_is_placed_against_the_range_and_its_ends_count_as_inside_it(double value, RangeVerdict expected)
    {
        // 464 is the reading that started this: a level of 464 % was drawn exactly like 4.79 bar.
        Assert.Equal(expected, Percent.VerdictFor(value));
    }

    [Fact]
    public void A_range_records_the_ends_it_was_given_and_invents_nothing_else()
    {
        var range = new TagRange(-5.5, 12.25);

        Assert.Equal(-5.5, range.Low);
        Assert.Equal(12.25, range.High);

        // Nothing here clamps, substitutes or rounds: the verdict is the only thing a range produces, and
        // the reading it was asked about is not part of what it returns (ADR-0030 §2). A type that carried
        // a "corrected" value would be the fabrication ADR-0003 refuses.
        Assert.Equal(RangeVerdict.AboveRange, range.VerdictFor(1_000_000));
    }

    [Fact]
    public void A_tag_with_no_range_produces_no_verdict_at_all()
    {
        // Null is not "in range" (ADR-0030 §3): a deployment that declared nothing must not be shown a
        // verdict nobody made, which is the same distinction a null on-delay carries (ADR-0025). The
        // absence lives in Tag.Range being null, so what there is to test here is that the domain lets it
        // be null rather than defaulting to a span.
        var tag = new Tag
        {
            Id = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            Name = "Level",
            ValueKind = TagValueKind.Numeric,
            SourceAddress = "40001",
            IsWritable = false,
        };

        Assert.Null(tag.Range);

        tag.Range = Percent;
        Assert.Equal(100, tag.Range.High);
    }
}
