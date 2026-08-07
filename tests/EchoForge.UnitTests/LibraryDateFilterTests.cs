using EchoForge.Contracts.Library;
using EchoForge.Infrastructure.Library;

namespace EchoForge.UnitTests;

/// <summary>
/// Narrowing the library to a range of days.
///
/// <para>
/// The whole difficulty here is one sentence: <b>meetings are stored as instants and remembered as
/// days.</b> A meeting at nine in the evening in Berlin happened on one date to the person who was
/// in it and on the next date in UTC, and a filter that compares a local date against a UTC instant
/// gets that wrong for a couple of hours every day. So the conversion happens once, both bounds are
/// whole local days, and both ends are included.
/// </para>
/// </summary>
public sealed class LibraryDateFilterTests : IDisposable
{
    private readonly LibraryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>A zone that is not UTC and does not observe daylight saving, so the sums are plain.</summary>
    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("test-plus-two", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    private static readonly TimeZoneInfo MinusSeven =
        TimeZoneInfo.CreateCustomTimeZone("test-minus-seven", TimeSpan.FromHours(-7), "UTC-7", "UTC-7");

    private async Task<SqliteLibraryIndex> IndexedAsync()
    {
        SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();
        return index;
    }

    private static DateOnly Day(int year, int month, int day) => new(year, month, day);

    private static string[] Ids(IEnumerable<LibraryEntry> entries) => [.. entries.Select(e => e.SessionId)];

    private void Given()
    {
        // Three meetings on three consecutive UTC days, at midday so nothing straddles anything.
        _fixture.AddSession("m-01", "First", createdUtc: new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
        _fixture.AddSession("m-02", "Second", createdUtc: new DateTimeOffset(2026, 3, 11, 12, 0, 0, TimeSpan.Zero));
        _fixture.AddSession("m-03", "Third", createdUtc: new DateTimeOffset(2026, 3, 12, 12, 0, 0, TimeSpan.Zero));
    }

    // -- the ranges --------------------------------------------------------------------------------

    [Fact]
    public async Task NoRangeShowsEverything()
    {
        Given();
        using SqliteLibraryIndex index = await IndexedAsync();

        Assert.Equal(3, index.Meetings().Count);
        Assert.Equal(3, index.Meetings(LibraryFilter.None).Count);
    }

    [Fact]
    public async Task AStartDateOnlyKeepsThatDayAndEverythingAfterIt()
    {
        Given();
        using SqliteLibraryIndex index = await IndexedAsync();

        LibraryFilter filter = LibraryFilter.ForLocalDates(Day(2026, 3, 11), null, TimeZoneInfo.Utc);

        Assert.Equal(["m-03", "m-02"], Ids(index.Meetings(filter)));
    }

    [Fact]
    public async Task AnEndDateOnlyKeepsThatDayAndEverythingBeforeIt()
    {
        Given();
        using SqliteLibraryIndex index = await IndexedAsync();

        LibraryFilter filter = LibraryFilter.ForLocalDates(null, Day(2026, 3, 11), TimeZoneInfo.Utc);

        Assert.Equal(["m-02", "m-01"], Ids(index.Meetings(filter)));
    }

    [Fact]
    public async Task BothEndsAreInclusive()
    {
        Given();
        using SqliteLibraryIndex index = await IndexedAsync();

        LibraryFilter filter = LibraryFilter.ForLocalDates(Day(2026, 3, 10), Day(2026, 3, 12), TimeZoneInfo.Utc);

        Assert.Equal(3, index.Meetings(filter).Count);
    }

    [Fact]
    public async Task ASingleDayIsAValidRange()
    {
        Given();
        using SqliteLibraryIndex index = await IndexedAsync();

        LibraryFilter filter = LibraryFilter.ForLocalDates(Day(2026, 3, 11), Day(2026, 3, 11), TimeZoneInfo.Utc);

        Assert.Equal(["m-02"], Ids(index.Meetings(filter)));
    }

    [Fact]
    public async Task AMeetingExactlyOnABoundaryIsIncluded()
    {
        // The first and last instants of a local day, which is where an exclusive comparison or a
        // fencepost error shows up.
        _fixture.AddSession("edge-start", "Midnight", createdUtc: new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero));
        _fixture.AddSession("edge-end", "One tick to midnight",
            createdUtc: new DateTimeOffset(2026, 3, 11, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999));

        using SqliteLibraryIndex index = await IndexedAsync();

        LibraryFilter filter = LibraryFilter.ForLocalDates(Day(2026, 3, 11), Day(2026, 3, 11), TimeZoneInfo.Utc);

        Assert.Equal(2, index.Meetings(filter).Count);
    }

    [Fact]
    public async Task AReversedRangeMatchesNothingAndSaysSo()
    {
        Given();
        using SqliteLibraryIndex index = await IndexedAsync();

        LibraryFilter filter = LibraryFilter.ForLocalDates(Day(2026, 3, 12), Day(2026, 3, 10), TimeZoneInfo.Utc);

        Assert.True(filter.IsReversed);
        Assert.Empty(index.Meetings(filter));
    }

    [Fact]
    public async Task ClearingTheRangeBringsEverythingBack()
    {
        Given();
        using SqliteLibraryIndex index = await IndexedAsync();

        LibraryFilter narrowed = LibraryFilter.ForLocalDates(Day(2026, 3, 12), Day(2026, 3, 12), TimeZoneInfo.Utc);
        Assert.Single(index.Meetings(narrowed));

        // Clearing is a different query, not a rebuild: the index was never discarded.
        Assert.Equal(3, index.Meetings(LibraryFilter.None).Count);
    }

    // -- local days versus UTC instants -------------------------------------------------------------

    [Fact]
    public void AnEveningMeetingIsFiledUnderTheDayItHappenedOn()
    {
        // 23:00 on the 11th in UTC+2 is 21:00 UTC on the 11th; but 23:00 UTC on the 11th is
        // 01:00 on the 12th for the person who was in the meeting.
        DateTimeOffset lateForThem = new(2026, 3, 11, 23, 0, 0, TimeSpan.Zero);

        LibraryFilter theEleventh = LibraryFilter.ForLocalDates(Day(2026, 3, 11), Day(2026, 3, 11), PlusTwo);
        LibraryFilter theTwelfth = LibraryFilter.ForLocalDates(Day(2026, 3, 12), Day(2026, 3, 12), PlusTwo);

        Assert.True(lateForThem > theEleventh.Until);
        Assert.True(lateForThem >= theTwelfth.Since && lateForThem <= theTwelfth.Until);
    }

    [Fact]
    public async Task AMeetingIsFoundUnderItsLocalDateAndNotTheUtcOne()
    {
        // Midnight UTC on the 12th is five in the afternoon on the 11th in UTC-7.
        _fixture.AddSession("late", "Late afternoon",
            createdUtc: new DateTimeOffset(2026, 3, 12, 0, 30, 0, TimeSpan.Zero));

        using SqliteLibraryIndex index = await IndexedAsync();

        Assert.Single(index.Meetings(LibraryFilter.ForLocalDates(Day(2026, 3, 11), Day(2026, 3, 11), MinusSeven)));
        Assert.Empty(index.Meetings(LibraryFilter.ForLocalDates(Day(2026, 3, 12), Day(2026, 3, 12), MinusSeven)));
    }

    [Fact]
    public void ARangeCoversTheWholeOfBothEndDays()
    {
        LibraryFilter filter = LibraryFilter.ForLocalDates(Day(2026, 3, 10), Day(2026, 3, 12), PlusTwo);

        Assert.Equal(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.FromHours(2)), filter.Since);

        // The last instant of the 12th locally, not the first instant of the 13th.
        Assert.Equal(new DateTimeOffset(2026, 3, 13, 0, 0, 0, TimeSpan.FromHours(2)).AddTicks(-1), filter.Until);
    }

    // -- with a search -----------------------------------------------------------------------------

    [Fact]
    public async Task SearchAndTheDateRangeApplyTogether()
    {
        _fixture.AddSession("s-early", "Early", createdUtc: new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
        _fixture.AddTranscript("s-early", ("microphone", "We should migrate the scheduler."));

        _fixture.AddSession("s-late", "Late", createdUtc: new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero));
        _fixture.AddTranscript("s-late", ("microphone", "We should migrate the scheduler."));

        using SqliteLibraryIndex index = await IndexedAsync();

        Assert.Equal(2, index.Search(new SearchQuery { Text = "migrate scheduler" }).Hits.Count);

        LibraryFilter filter = LibraryFilter.ForLocalDates(Day(2026, 3, 18), null, TimeZoneInfo.Utc);

        SearchResults narrowed = index.Search(new SearchQuery
        {
            Text = "migrate scheduler",
            Since = filter.Since,
            Until = filter.Until,
        });

        Assert.Single(narrowed.Hits);
        Assert.Equal("s-late", narrowed.Hits[0].SessionId);
    }

    [Fact]
    public async Task ARangeWithNoMeetingsInItIsEmptyRatherThanUnavailable()
    {
        Given();
        using SqliteLibraryIndex index = await IndexedAsync();

        LibraryFilter filter = LibraryFilter.ForLocalDates(Day(2020, 1, 1), Day(2020, 1, 2), TimeZoneInfo.Utc);

        Assert.Empty(index.Meetings(filter));

        SearchResults results = index.Search(new SearchQuery
        {
            Text = "anything",
            Since = filter.Since,
            Until = filter.Until,
        });

        Assert.Empty(results.Hits);
        Assert.False(results.IndexUnavailable);
    }
}
