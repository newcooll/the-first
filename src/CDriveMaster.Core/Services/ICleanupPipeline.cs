using System.Collections.Generic;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public interface ICleanupPipeline
{
    IReadOnlyList<BucketResult> Execute(IReadOnlyList<CleanupBucket> buckets, bool apply);
}