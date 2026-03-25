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
