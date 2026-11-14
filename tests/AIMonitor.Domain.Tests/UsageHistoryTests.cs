using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class UsageHistoryTests
{
    [Fact]
    public void Defaults_AreEmptyNotNull()
    {
        var history = new UsageHistory();

        Assert.Empty(history.Buckets);
        Assert.Empty(history.Series);
        Assert.Empty(history.ByModel);
        Assert.Empty(history.ByProject);
        Assert.Equal(14, history.Days);
        Assert.Equal("Total tokens", history.Metric);
        Assert.Equal("By project", history.ProjectLabel);
    }

    [Fact]
    public void Buckets_CarryDayAndPerModelTotals()
    {
        var day = new DateOnly(2026, 9, 18);
        var bucket = new UsageHistoryBucket(day, new Dictionary<string, double> { ["opus"] = 3.0, ["fable"] = 2.0 });

        Assert.Equal(day, bucket.Day);
        Assert.Equal(5.0, bucket.Total);
    }

    [Fact]
    public void Buckets_MutatingTheSourceListAfterConstruction_DoesNotAlterTheHistory()
    {
        var buckets = new List<UsageHistoryBucket> { new(new DateOnly(2026, 9, 18)) };
        var history = new UsageHistory(buckets: buckets);

        buckets.Clear();

        Assert.Single(history.Buckets);
    }

    [Fact]
    public void Bucket_PerModel_MutatingTheSourceDictionaryAfterConstruction_DoesNotAlterTheBucket()
    {
        var perModel = new Dictionary<string, double> { ["opus"] = 3.0 };
        var bucket = new UsageHistoryBucket(new DateOnly(2026, 9, 18), perModel);

        perModel["opus"] = 999.0;
        perModel["fable"] = 1.0;

        Assert.Equal(3.0, bucket.PerModel["opus"]);
        Assert.False(bucket.PerModel.ContainsKey("fable"));
    }

    [Fact]
    public void ByModelAndByProject_MutatingTheSourceListAfterConstruction_DoesNotAlterTheHistory()
    {
        var byModel = new List<UsageHistoryBreakdown> { new("opus", 5.0) };
        var byProject = new List<UsageHistoryBreakdown> { new("default", 5.0) };
        var history = new UsageHistory(byModel: byModel, byProject: byProject);

        byModel.Clear();
        byProject.Clear();

        Assert.Single(history.ByModel);
        Assert.Single(history.ByProject);
    }

    [Fact]
    public void Series_MutatingTheSourceListAfterConstruction_DoesNotAlterTheHistory()
    {
        var series = new List<string> { "Total" };
        var history = new UsageHistory(series: series);

        series.Clear();

        Assert.Single(history.Series);
    }

    [Fact]
    public void Buckets_ExposedPropertyIsGenuinelyReadOnly()
    {
        var history = new UsageHistory(buckets: [new UsageHistoryBucket(new DateOnly(2026, 9, 18))]);

        var asList = Assert.IsAssignableFrom<IList<UsageHistoryBucket>>(history.Buckets);

        Assert.True(asList.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asList.Add(new UsageHistoryBucket(new DateOnly(2026, 9, 19))));
        Assert.Single(history.Buckets);
    }

    [Fact]
    public void Series_ExposedPropertyIsGenuinelyReadOnly()
    {
        var history = new UsageHistory(series: ["Total"]);

        var asList = Assert.IsAssignableFrom<IList<string>>(history.Series);

        Assert.True(asList.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asList.Add("Other"));
        Assert.Single(history.Series);
    }

    [Fact]
    public void ByModelAndByProject_ExposedPropertiesAreGenuinelyReadOnly()
    {
        var history = new UsageHistory(byModel: [new("opus", 5.0)], byProject: [new("default", 5.0)]);

        var byModel = Assert.IsAssignableFrom<IList<UsageHistoryBreakdown>>(history.ByModel);
        var byProject = Assert.IsAssignableFrom<IList<UsageHistoryBreakdown>>(history.ByProject);

        Assert.True(byModel.IsReadOnly);
        Assert.True(byProject.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => byModel.Add(new("fable", 1.0)));
        Assert.Throws<NotSupportedException>(() => byProject.Add(new("other", 1.0)));
    }

    [Fact]
    public void Bucket_PerModel_ExposedPropertyIsGenuinelyReadOnly()
    {
        var bucket = new UsageHistoryBucket(new DateOnly(2026, 9, 18), new Dictionary<string, double> { ["opus"] = 3.0 });

        var asDictionary = Assert.IsAssignableFrom<IDictionary<string, double>>(bucket.PerModel);

        Assert.True(asDictionary.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asDictionary["opus"] = 999.0);
        Assert.Equal(3.0, bucket.PerModel["opus"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Bucket_PerModel_RejectsBlankModelLabel(string label)
    {
        Assert.Throws<ArgumentException>(
            () => new UsageHistoryBucket(new DateOnly(2026, 9, 18), new Dictionary<string, double> { [label] = 1.0 }));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    public void Bucket_PerModel_RejectsNonFiniteOrNegativeValues(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new UsageHistoryBucket(new DateOnly(2026, 9, 18), new Dictionary<string, double> { ["opus"] = value }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Breakdown_RejectsBlankLabel(string label)
    {
        Assert.Throws<ArgumentException>(() => new UsageHistoryBreakdown(label, 1.0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    public void Breakdown_RejectsNonFiniteOrNegativeValue(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageHistoryBreakdown("opus", value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankSeriesLabel(string label)
    {
        Assert.Throws<ArgumentException>(() => new UsageHistory(series: [label]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveDays(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageHistory(days: days));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankMetric(string metric)
    {
        Assert.Throws<ArgumentException>(() => new UsageHistory(metric: metric));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankProjectLabel(string projectLabel)
    {
        Assert.Throws<ArgumentException>(() => new UsageHistory(projectLabel: projectLabel));
    }
}
