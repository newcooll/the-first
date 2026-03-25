using System;
using System.Collections.Generic;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class AuditAggregatorTests
{
    [Fact]
    public void Empty_logs_should_return_skipped()
    {
        var result = AuditAggregator.CalculateBucketStatus(Array.Empty<AuditLogItem>());
        result.Should().Be(ExecutionStatus.Skipped);
    }

    [Fact]
    public void All_success_should_return_success()
    {
        var logs = new[] { Log(ExecutionStatus.Success), Log(ExecutionStatus.Success) };
        var result = AuditAggregator.CalculateBucketStatus(logs);
        result.Should().Be(ExecutionStatus.Success);
    }

    [Fact]
    public void All_failed_should_return_failed()
    {
        var logs = new[] { Log(ExecutionStatus.Failed), Log(ExecutionStatus.Failed) };
        var result = AuditAggregator.CalculateBucketStatus(logs);
        result.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public void All_blocked_should_return_blocked()
    {
        var logs = new[] { Log(ExecutionStatus.Blocked), Log(ExecutionStatus.Blocked) };
        var result = AuditAggregator.CalculateBucketStatus(logs);
        result.Should().Be(ExecutionStatus.Blocked);
    }

    [Fact]
    public void All_skipped_should_return_skipped()
    {
        var logs = new[] { Log(ExecutionStatus.Skipped), Log(ExecutionStatus.Skipped) };
        var result = AuditAggregator.CalculateBucketStatus(logs);
        result.Should().Be(ExecutionStatus.Skipped);
    }

    [Fact]
    public void Success_and_failed_should_return_partial_success()
    {
        var logs = new[] { Log(ExecutionStatus.Success), Log(ExecutionStatus.Failed) };
        var result = AuditAggregator.CalculateBucketStatus(logs);
        result.Should().Be(ExecutionStatus.PartialSuccess);
    }

    [Fact]
    public void Success_and_blocked_should_return_partial_success()
    {
        var logs = new[] { Log(ExecutionStatus.Success), Log(ExecutionStatus.Blocked) };
        var result = AuditAggregator.CalculateBucketStatus(logs);
        result.Should().Be(ExecutionStatus.PartialSuccess);
    }

    [Fact]
    public void Success_and_skipped_should_return_success()
    {
        var logs = new[] { Log(ExecutionStatus.Success), Log(ExecutionStatus.Skipped) };
        var result = AuditAggregator.CalculateBucketStatus(logs);
        result.Should().Be(ExecutionStatus.Success);
    }

    [Fact]
    public void Failed_and_skipped_should_return_failed()
    {
        var logs = new[] { Log(ExecutionStatus.Failed), Log(ExecutionStatus.Skipped) };
        var result = AuditAggregator.CalculateBucketStatus(logs);
        result.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public void Blocked_and_skipped_should_return_blocked()
    {
        var logs = new[] { Log(ExecutionStatus.Blocked), Log(ExecutionStatus.Skipped) };
        var result = AuditAggregator.CalculateBucketStatus(logs);
        result.Should().Be(ExecutionStatus.Blocked);
    }

    private static AuditLogItem Log(ExecutionStatus status)
    {
        return new AuditLogItem(
            JobId: "job-1",
            BucketId: "bucket-1",
            TimestampUtc: DateTime.UtcNow,
            TargetPath: "C:/tmp/file.tmp",
            TargetSizeBytes: 10,
            Action: CleanupAction.DeleteToRecycleBin,
            Risk: RiskLevel.SafeAuto,
            AppName: "WeChat",
            Reason: "test",
            Status: status,
            ErrorMessage: status == ExecutionStatus.Failed ? "failed" : null);
    }
}
