using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public interface ICleanupPipeline
{
    IReadOnlyList<BucketResult> Execute(IReadOnlyList<CleanupBucket> buckets, bool apply);

    Task<IReadOnlyList<BucketResult>> ExecuteAsync(
        IReadOnlyList<CleanupBucket> buckets,
        bool apply,
        CancellationToken cancellationToken = default);

    BucketResult ExecuteEntries(
        CleanupBucket parentBucket,
        IEnumerable<CleanupEntry> entriesToApply,
        bool apply,
        bool allowTrustedExactFileFastPath = false);
}
