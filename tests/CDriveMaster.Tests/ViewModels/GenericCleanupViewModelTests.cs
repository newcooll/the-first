using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Fakes;
using CDriveMaster.UI.Services;
using CDriveMaster.UI.ViewModels;
using CDriveMaster.UI.ViewModels.Items;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests.ViewModels;

public sealed class GenericCleanupViewModelTests
{
    [Fact]
    public void ScanCommand_CanExecute_ShouldBeFalse_WhenIsBusyOrSelectedAppIsNull()
    {
        var dialog = new FakeDialogService();
        var pipeline = new FakeCleanupPipeline();
        var vm = CreateViewModel(dialog, pipeline);

        vm.SelectedApp = null;
        vm.ScanCommand.CanExecute(null).Should().BeFalse();

        vm.SelectedApp = new StubCleanupProvider("TestApp");
        vm.ScanCommand.CanExecute(null).Should().BeTrue();

        vm.IsBusy = true;
        vm.ScanCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ApplySafeAutoCommand_CanExecute_ShouldBeFalse_WhenNoSafeAutoItemsOrIsBusy()
    {
        var dialog = new FakeDialogService();
        var pipeline = new FakeCleanupPipeline();
        var vm = CreateViewModel(dialog, pipeline);

        vm.HasSafeAutoItems = false;
        vm.IsBusy = false;
        vm.ApplySafeAutoCommand.CanExecute(null).Should().BeFalse();

        vm.HasSafeAutoItems = true;
        vm.IsBusy = false;
        vm.ApplySafeAutoCommand.CanExecute(null).Should().BeTrue();

        vm.IsBusy = true;
        vm.ApplySafeAutoCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ScanAsync_ShouldUpdateHasSafeAutoItems_BasedOnScanResults()
    {
        var dialog = new FakeDialogService();
        var pipeline = new FakeCleanupPipeline
        {
            ResultsFactory = buckets => buckets
                .Select(x => CreateResult(x.BucketId, x.RiskLevel, ExecutionStatus.Skipped, x.EstimatedSizeBytes))
                .ToList()
        };

        var safeProvider = new StubCleanupProvider("TestApp", new[]
        {
            CreateBucket("safe", RiskLevel.SafeAuto)
        });

        var vm = CreateViewModel(dialog, pipeline, safeProvider);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.HasSafeAutoItems.Should().BeTrue();

        vm.SelectedApp = new StubCleanupProvider("TestApp", new[]
        {
            CreateBucket("preview", RiskLevel.SafeWithPreview)
        });
        await vm.ScanCommand.ExecuteAsync(null);
        vm.HasSafeAutoItems.Should().BeFalse();
    }

    [Fact]
    public async Task ApplySafeAutoAsync_WhenNoSafeAutoItems_ShouldShowInfoAndNotExecute()
    {
        var dialog = new FakeDialogService();
        var pipeline = new FakeCleanupPipeline();
        var vm = CreateViewModel(dialog, pipeline);

        vm.BucketItems = new ObservableCollection<BucketResultItemViewModel>
        {
            new(CreateResult("b1", RiskLevel.SafeWithPreview, ExecutionStatus.Skipped))
        };
        vm.HasSafeAutoItems = true;

        await vm.ApplySafeAutoCommand.ExecuteAsync(null);

        dialog.WasShowInfoCalled.Should().BeTrue();
        pipeline.WasExecuteCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ApplySafeAutoAsync_WhenUserCancels_ShouldNotExecute()
    {
        var dialog = new FakeDialogService { ConfirmResult = false };
        var pipeline = new FakeCleanupPipeline();
        var vm = CreateViewModel(dialog, pipeline);

        vm.BucketItems = new ObservableCollection<BucketResultItemViewModel>
        {
            new(CreateResult("b1", RiskLevel.SafeAuto, ExecutionStatus.Skipped))
        };
        vm.HasSafeAutoItems = true;

        await vm.ApplySafeAutoCommand.ExecuteAsync(null);

        dialog.WasConfirmCalled.Should().BeTrue();
        pipeline.WasExecuteCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ApplySafeAutoAsync_WhenConfirmed_ShouldExecuteOnlySafeAutoItems()
    {
        var dialog = new FakeDialogService { ConfirmResult = true };
        var pipeline = new FakeCleanupPipeline
        {
            ResultsFactory = buckets => buckets
                .Select(x => CreateResult(x.BucketId, x.RiskLevel, ExecutionStatus.Success, x.EstimatedSizeBytes))
                .ToList()
        };

        var vm = CreateViewModel(dialog, pipeline);
        vm.BucketItems = new ObservableCollection<BucketResultItemViewModel>
        {
            new(CreateResult("safe", RiskLevel.SafeAuto, ExecutionStatus.Skipped)),
            new(CreateResult("preview", RiskLevel.SafeWithPreview, ExecutionStatus.Skipped))
        };
        vm.HasSafeAutoItems = true;

        await vm.ApplySafeAutoCommand.ExecuteAsync(null);

        pipeline.WasExecuteCalled.Should().BeTrue();
        pipeline.LastApply.Should().BeTrue();
        pipeline.LastBuckets.Should().OnlyContain(x => x.RiskLevel == RiskLevel.SafeAuto);
        pipeline.LastBuckets.Should().HaveCount(1);
    }

    [Fact]
    public async Task ApplySafeAutoAsync_AfterExecution_ShouldUpdateItemResults()
    {
        var dialog = new FakeDialogService { ConfirmResult = true };
        var pipeline = new FakeCleanupPipeline
        {
            ResultsFactory = buckets => buckets
                .Select(x => CreateResult(x.BucketId, x.RiskLevel, ExecutionStatus.Success, x.EstimatedSizeBytes))
                .ToList()
        };

        var vm = CreateViewModel(dialog, pipeline);
        vm.BucketItems = new ObservableCollection<BucketResultItemViewModel>
        {
            new(CreateResult("safe", RiskLevel.SafeAuto, ExecutionStatus.Skipped))
        };
        vm.HasSafeAutoItems = true;

        await vm.ApplySafeAutoCommand.ExecuteAsync(null);

        vm.BucketItems.Should().HaveCount(1);
        vm.BucketItems[0].OriginalResult.FinalStatus.Should().Be(ExecutionStatus.Success);
        vm.BucketItems[0].StatusText.Should().Be("成功");
    }

    [Fact]
    public async Task ApplySafeAutoAsync_WhenPipelineThrows_ShouldShowErrorAndRestoreIsBusy()
    {
        var dialog = new FakeDialogService { ConfirmResult = true };
        var pipeline = new FakeCleanupPipeline { ThrowOnExecute = true };
        var vm = CreateViewModel(dialog, pipeline);

        vm.BucketItems = new ObservableCollection<BucketResultItemViewModel>
        {
            new(CreateResult("safe", RiskLevel.SafeAuto, ExecutionStatus.Skipped))
        };
        vm.HasSafeAutoItems = true;

        await vm.ApplySafeAutoCommand.ExecuteAsync(null);

        dialog.WasShowErrorCalled.Should().BeTrue();
        vm.IsBusy.Should().BeFalse();
    }

    private static GenericCleanupViewModel CreateViewModel(
        FakeDialogService dialog,
        FakeCleanupPipeline pipeline,
        ICleanupProvider? selectedApp = null)
    {
        var ruleCatalog = new RuleCatalog(Array.Empty<IAppDetector>(), new BucketBuilder());
        var vm = new GenericCleanupViewModel(ruleCatalog, pipeline, dialog, new FakePreviewDialogService())
        {
            SelectedApp = selectedApp ?? new StubCleanupProvider("TestApp")
        };

        return vm;
    }

    private static CleanupBucket CreateBucket(string bucketId, RiskLevel risk)
    {
        return new CleanupBucket(
            BucketId: bucketId,
            Category: "TestCategory",
            RootPath: @"C:\Sandbox\Path",
            AppName: "TestApp",
            RiskLevel: risk,
            SuggestedAction: CleanupAction.DeleteToRecycleBin,
            Description: "Test Bucket",
            EstimatedSizeBytes: 1024 * 1024,
            Entries: Array.Empty<CleanupEntry>());
    }

    private static BucketResult CreateResult(string bucketId, RiskLevel risk, ExecutionStatus status, long estimatedBytes = 1024 * 1024)
    {
        var bucket = new CleanupBucket(
            BucketId: bucketId,
            Category: "TestCategory",
            RootPath: @"C:\Sandbox\Path",
            AppName: "TestApp",
            RiskLevel: risk,
            SuggestedAction: CleanupAction.DeleteToRecycleBin,
            Description: "Test Bucket",
            EstimatedSizeBytes: estimatedBytes,
            Entries: Array.Empty<CleanupEntry>());

        return new BucketResult(
            Bucket: bucket,
            FinalStatus: status,
            ReclaimedSizeBytes: status == ExecutionStatus.Success ? estimatedBytes : 0,
            SuccessCount: status == ExecutionStatus.Success ? 1 : 0,
            FailedCount: status == ExecutionStatus.Failed ? 1 : 0,
            BlockedCount: status == ExecutionStatus.Blocked ? 1 : 0,
            Logs: Array.Empty<AuditLogItem>());
    }

    private sealed class StubCleanupProvider : ICleanupProvider
    {
        private readonly IReadOnlyList<CleanupBucket> buckets;

        public StubCleanupProvider(string appName, IReadOnlyList<CleanupBucket>? buckets = null)
        {
            AppName = appName;
            this.buckets = buckets ?? Array.Empty<CleanupBucket>();
        }

        public string AppName { get; }

        public IReadOnlyList<CleanupBucket> GetBuckets()
        {
            return buckets;
        }
    }

    private sealed class FakeCleanupPipeline : ICleanupPipeline
    {
        public bool WasExecuteCalled { get; private set; }

        public bool LastApply { get; private set; }

        public bool ThrowOnExecute { get; set; }

        public IReadOnlyList<CleanupBucket> LastBuckets { get; private set; } = Array.Empty<CleanupBucket>();

        public Func<IReadOnlyList<CleanupBucket>, IReadOnlyList<BucketResult>>? ResultsFactory { get; set; }

        public IReadOnlyList<BucketResult> Execute(IReadOnlyList<CleanupBucket> buckets, bool apply)
        {
            if (ThrowOnExecute)
            {
                throw new InvalidOperationException("pipeline failed");
            }

            WasExecuteCalled = true;
            LastApply = apply;
            LastBuckets = buckets.ToList();

            if (ResultsFactory is not null)
            {
                return ResultsFactory(buckets);
            }

            return buckets
                .Select(x => CreateResult(x.BucketId, x.RiskLevel, ExecutionStatus.Skipped, x.EstimatedSizeBytes))
                .ToList();
        }

        public BucketResult ExecuteEntries(CleanupBucket parentBucket, IEnumerable<CleanupEntry> entriesToApply, bool apply)
        {
            var selected = entriesToApply.ToList();
            var temp = new CleanupBucket(
                BucketId: parentBucket.BucketId,
                Category: parentBucket.Category,
                RootPath: parentBucket.RootPath,
                AppName: parentBucket.AppName,
                RiskLevel: parentBucket.RiskLevel,
                SuggestedAction: parentBucket.SuggestedAction,
                Description: parentBucket.Description,
                EstimatedSizeBytes: selected.Sum(x => x.SizeBytes),
                Entries: selected);

            return CreateResult(temp.BucketId, temp.RiskLevel, ExecutionStatus.Success, temp.EstimatedSizeBytes);
        }
    }

    private sealed class FakePreviewDialogService : IPreviewDialogService
    {
        public Task<IEnumerable<CleanupEntry>> ShowPreviewAsync(string title, IEnumerable<CleanupEntry> entries)
        {
            return Task.FromResult(entries.Take(0));
        }
    }
}
