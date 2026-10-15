using System.Linq;
using AIMonitor.Application.Providers;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Agy;
using AIMonitor.TestSupport;
using Xunit;

namespace AIMonitor.Infrastructure.Tests.Providers.Agy;

public class AgyQuotaTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeAgyUsageRunner : IAgyUsageRunner
    {
        private readonly Func<CancellationToken, Task<string>> _handler;

        public FakeAgyUsageRunner(string responseJson)
            : this(_ => Task.FromResult(responseJson))
        {
        }

        public FakeAgyUsageRunner(Func<CancellationToken, Task<string>> handler)
        {
            _handler = handler;
        }

        public Task<string> ReadUsageAsync(CancellationToken cancellationToken) => _handler(cancellationToken);
    }

    [Fact]
    public void MapUsage_TwoGroupsFourWindows_PreservesGroupOrderAndMapsAllMeters()
    {
        const string json = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "name": "Gemini 2.5 Pro",
                  "buckets": [
                    {
                      "id": "b_gem_5h",
                      "window": "5h",
                      "remaining_fraction": 0.88,
                      "reset_time": "2026-10-15T17:00:00Z"
                    },
                    {
                      "id": "b_gem_weekly",
                      "window": "weekly",
                      "remaining_fraction": 0.50,
                      "reset_time": "2026-10-22T00:00:00Z"
                    }
                  ]
                },
                {
                  "name": "Claude 3.7 Sonnet",
                  "buckets": [
                    {
                      "id": "b_cld_5h",
                      "window": "5h",
                      "remaining_fraction": 0.75,
                      "reset_time": "2026-10-15T16:30:00Z"
                    },
                    {
                      "id": "b_cld_weekly",
                      "window": "weekly",
                      "remaining_fraction": 0.10,
                      "reset_time": "2026-10-21T00:00:00Z"
                    }
                  ]
                }
              ]
            }
          }
        }
        """;

        var meters = AgyUsageMapper.MapUsage(json);

        Assert.Equal(4, meters.Count);

        // Group 0 - Gemini
        Assert.Equal("session", meters[0].Kind);
        Assert.Equal("agy_0_b_gem_5h", meters[0].Key);
        Assert.Equal("Gemini 2.5 Pro · Session", meters[0].Title);
        Assert.Equal("5-hour window", meters[0].Subtitle);
        Assert.Equal(12.0, meters[0].Percent);

        Assert.Equal("weekly", meters[1].Kind);
        Assert.Equal("agy_0_b_gem_weekly", meters[1].Key);
        Assert.Equal("Gemini 2.5 Pro · Weekly", meters[1].Title);
        Assert.Equal("Weekly window", meters[1].Subtitle);
        Assert.Equal(50.0, meters[1].Percent);

        // Group 1 - Claude
        Assert.Equal("session", meters[2].Kind);
        Assert.Equal("agy_1_b_cld_5h", meters[2].Key);
        Assert.Equal("Claude 3.7 Sonnet · Session", meters[2].Title);
        Assert.Equal("5-hour window", meters[2].Subtitle);
        Assert.Equal(25.0, meters[2].Percent);

        Assert.Equal("weekly", meters[3].Kind);
        Assert.Equal("agy_1_b_cld_weekly", meters[3].Key);
        Assert.Equal("Claude 3.7 Sonnet · Weekly", meters[3].Title);
        Assert.Equal("Weekly window", meters[3].Subtitle);
        Assert.Equal(90.0, meters[3].Percent);
    }

    [Fact]
    public void MapUsage_WeeklyFirstInput_Yields5hFirst()
    {
        const string json = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "quota",
            "data": {
              "groups": [
                {
                  "name": "Pool Alpha",
                  "buckets": [
                    {
                      "id": "w_bucket",
                      "window": "weekly",
                      "remaining_fraction": 0.40,
                      "reset_time": "2026-10-22T00:00:00Z"
                    },
                    {
                      "id": "s_bucket",
                      "window": "5h",
                      "remaining_fraction": 0.90,
                      "reset_time": "2026-10-15T17:00:00Z"
                    }
                  ]
                }
              ]
            }
          }
        }
        """;

        var meters = AgyUsageMapper.MapUsage(json);

        Assert.Equal(2, meters.Count);
        Assert.Equal("session", meters[0].Kind);
        Assert.Equal("agy_0_s_bucket", meters[0].Key);
        Assert.Equal("5-hour window", meters[0].Subtitle);

        Assert.Equal("weekly", meters[1].Kind);
        Assert.Equal("agy_0_w_bucket", meters[1].Key);
        Assert.Equal("Weekly window", meters[1].Subtitle);
    }

    [Fact]
    public void MapUsage_Fractions_ComputesAccurateUsedPercentage()
    {
        const string json = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "name": "Fraction Test",
                  "buckets": [
                    { "id": "f_zero", "window": "5h", "remaining_fraction": 0.0 },
                    { "id": "f_one", "window": "weekly", "remaining_fraction": 1.0 },
                    { "id": "f_eightyeight", "window": "5h", "remaining_fraction": 0.88 }
                  ]
                }
              ]
            }
          }
        }
        """;

        var meters = AgyUsageMapper.MapUsage(json);

        var mZero = meters.First(m => m.Key == "agy_0_f_zero");
        var mOne = meters.First(m => m.Key == "agy_0_f_one");
        var m88 = meters.First(m => m.Key == "agy_0_f_eightyeight");

        Assert.Equal(100.0, mZero.Percent);
        Assert.Equal(0.0, mOne.Percent);
        Assert.Equal(12.0, m88.Percent);
    }

    [Fact]
    public void MapUsage_InvalidFractions_OmittedWithoutFabricatingZero()
    {
        const string mixedJson = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "name": "Mixed",
                  "buckets": [
                    { "id": "valid", "window": "5h", "remaining_fraction": 0.5 },
                    { "id": "null_frac", "window": "5h", "remaining_fraction": null },
                    { "id": "str_frac", "window": "5h", "remaining_fraction": "0.5" },
                    { "id": "negative", "window": "weekly", "remaining_fraction": -0.1 },
                    { "id": "exceeds_one", "window": "weekly", "remaining_fraction": 1.2 }
                  ]
                }
              ]
            }
          }
        }
        """;

        var meters = AgyUsageMapper.MapUsage(mixedJson);

        Assert.Single(meters);
        Assert.Equal("agy_0_valid", meters[0].Key);
        Assert.Equal(50.0, meters[0].Percent);

        const string allInvalidJson = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "name": "All Invalid",
                  "buckets": [
                    { "id": "neg", "window": "5h", "remaining_fraction": -0.5 },
                    { "id": "too_big", "window": "weekly", "remaining_fraction": 2.0 }
                  ]
                }
              ]
            }
          }
        }
        """;

        Assert.Throws<AgyUsageException>(() => AgyUsageMapper.MapUsage(allInvalidJson));
    }

    [Fact]
    public void MapUsage_EmptyOrErrorEnvelope_ThrowsSafeException()
    {
        const string errorJson = """
        {
          "status": "ERROR",
          "error": "internal token crash"
        }
        """;

        var exError = Assert.Throws<AgyUsageException>(() => AgyUsageMapper.MapUsage(errorJson));
        Assert.DoesNotContain("internal token crash", exError.Message);

        const string emptyGroupsJson = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": []
            }
          }
        }
        """;

        Assert.Throws<AgyUsageException>(() => AgyUsageMapper.MapUsage(emptyGroupsJson));

        Assert.Throws<AgyUsageException>(() => AgyUsageMapper.MapUsage("{}"));
        Assert.Throws<AgyUsageException>(() => AgyUsageMapper.MapUsage("not json"));
        Assert.Throws<AgyUsageException>(() => AgyUsageMapper.MapUsage(""));
    }

    [Fact]
    public void MapUsage_ResetTime_InvalidNoOffsetOrDateOnly_ResultsInNull()
    {
        const string json = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "name": "Resets",
                  "buckets": [
                    { "id": "date_only", "window": "5h", "remaining_fraction": 0.5, "reset_time": "2026-10-15" },
                    { "id": "no_offset", "window": "5h", "remaining_fraction": 0.5, "reset_time": "2026-10-15T12:00:00" },
                    { "id": "invalid_str", "window": "5h", "remaining_fraction": 0.5, "reset_time": "in 2 hours" },
                    { "id": "valid_utc", "window": "weekly", "remaining_fraction": 0.5, "reset_time": "2026-10-15T18:00:00Z" },
                    { "id": "valid_offset", "window": "weekly", "remaining_fraction": 0.5, "reset_time": "2026-10-15T18:00:00+07:00" }
                  ]
                }
              ]
            }
          }
        }
        """;

        var meters = AgyUsageMapper.MapUsage(json);

        var dateOnly = meters.First(m => m.Key == "agy_0_date_only");
        var noOffset = meters.First(m => m.Key == "agy_0_no_offset");
        var invalidStr = meters.First(m => m.Key == "agy_0_invalid_str");
        var validUtc = meters.First(m => m.Key == "agy_0_valid_utc");
        var validOffset = meters.First(m => m.Key == "agy_0_valid_offset");

        Assert.Null(dateOnly.ResetsAt);
        Assert.Null(noOffset.ResetsAt);
        Assert.Null(invalidStr.ResetsAt);

        Assert.NotNull(validUtc.ResetsAt);
        Assert.Equal(TimeSpan.Zero, validUtc.ResetsAt.Value.Offset);

        Assert.NotNull(validOffset.ResetsAt);
        Assert.Equal(TimeSpan.FromHours(7), validOffset.ResetsAt.Value.Offset);
    }

    [Fact]
    public void MapUsage_DuplicateBucketIdsInGroup_FailsDeterministically()
    {
        const string json = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "name": "Duplicates",
                  "buckets": [
                    { "id": "dup_id", "window": "5h", "remaining_fraction": 0.5 },
                    { "id": "dup_id", "window": "weekly", "remaining_fraction": 0.5 }
                  ]
                }
              ]
            }
          }
        }
        """;

        Assert.Throws<AgyUsageException>(() => AgyUsageMapper.MapUsage(json));
    }

    [Fact]
    public async Task GetSnapshotAsync_FakeRunnerErrors_SanitizedWithoutEchoingSecretDetails()
    {
        var runner = new FakeAgyUsageRunner(_ => throw new InvalidOperationException("secret_key_abcdef12345 in stderr"));
        var clock = new FakeClock(TestNow);
        var client = new AgyQuotaClient(runner, clock);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.False(snapshot.Configured);
        Assert.NotNull(snapshot.Error);
        Assert.DoesNotContain("secret_key_abcdef12345", snapshot.Error);
        Assert.Equal(AgyQuotaClient.ActionableError, snapshot.Error);
        Assert.Equal(AgyQuotaClient.SetupHint, snapshot.SetupHint);

        Assert.NotNull(snapshot.Detection);
        Assert.Equal(DetectionState.NotConnected, snapshot.Detection.State);
        Assert.Equal("agy_cli", snapshot.Detection.SourceId);
        Assert.Equal("AGY CLI", snapshot.Detection.SourceLabel);
    }

    [Fact]
    public async Task GetSnapshotAsync_Cancellation_PreAndDuringCall_RethrowsOperationCanceledException()
    {
        var runner = new FakeAgyUsageRunner(async ct =>
        {
            await Task.Delay(50, ct);
            return "{}";
        });
        var clock = new FakeClock(TestNow);
        var client = new AgyQuotaClient(runner, clock);

        // Pre-cancelled
        using var preCancelledCts = new CancellationTokenSource();
        preCancelledCts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.GetSnapshotAsync(ProviderSnapshotRequest.Default, preCancelledCts.Token));

        // Cancelled during call
        using var duringCallCts = new CancellationTokenSource();
        var slowRunner = new FakeAgyUsageRunner(async ct =>
        {
            await Task.Delay(1000, ct);
            return "{}";
        });
        var slowClient = new AgyQuotaClient(slowRunner, clock);
        var task = slowClient.GetSnapshotAsync(ProviderSnapshotRequest.Default, duringCallCts.Token);
        duringCallCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task GetSnapshotAsync_Success_ProducesCompleteSnapshot()
    {
        const string json = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "name": "Pro Pool",
                  "buckets": [
                    { "id": "p5h", "window": "5h", "remaining_fraction": 0.80 }
                  ]
                }
              ]
            }
          }
        }
        """;

        var runner = new FakeAgyUsageRunner(json);
        var clock = new FakeClock(TestNow);
        var client = new AgyQuotaClient(runner, clock);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.True(snapshot.Configured);
        Assert.Equal("agy", snapshot.ProviderId);
        Assert.Null(snapshot.Error);
        Assert.Equal(TestNow, snapshot.FetchedAt);
        Assert.Equal(AgyQuotaClient.ValueNote, snapshot.ValueNote);

        Assert.NotNull(snapshot.Detection);
        Assert.Equal(DetectionState.Connected, snapshot.Detection.State);
        Assert.Equal("agy_cli", snapshot.Detection.SourceId);
        Assert.Equal("AGY CLI", snapshot.Detection.SourceLabel);

        Assert.Null(snapshot.History);
        Assert.Empty(snapshot.Stats);
        Assert.Single(snapshot.Meters);
        Assert.Equal(20.0, snapshot.Meters[0].Percent);
    }
}
