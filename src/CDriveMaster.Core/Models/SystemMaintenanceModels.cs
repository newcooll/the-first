using System;

namespace CDriveMaster.Core.Models;

public sealed record SystemMaintenanceReport(
    string OperationId,
    string Name,
    RiskLevel Risk,
    long ActualSizeBytes,
    long BackupsAndDisabledFeaturesBytes,
    long CacheAndTemporaryDataBytes,
    long EstimatedReclaimableBytes,
    int ReclaimablePackageCount,
    bool CleanupRecommended,
    bool RequiresAdmin,
    string RawOutput
);

public sealed record SystemMaintenanceResult(
    string OperationId,
    ExecutionStatus Status,
    long EstimatedReclaimableBytes,
    int ExitCode,
    TimeSpan Duration,
    string StdOut,
    string StdErr
);

public sealed record SystemAnalysisResult(
    ExecutionStatus Status,
    SystemMaintenanceReport? Report,
    string StdOut,
    string StdErr,
    int ExitCode,
    string Reason
);

public sealed record SystemAnalysisViewModel(
    string Title,
    ExecutionStatus Status,
    long ActualSizeBytes,
    long EstimatedReclaimableBytes,
    bool CleanupRecommended,
    string Message
);

public sealed record SystemCleanupViewModel(
    string Title,
    ExecutionStatus Status,
    TimeSpan Duration,
    int ExitCode,
    string Message
);
