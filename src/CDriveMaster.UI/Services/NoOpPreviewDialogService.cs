using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.UI.Services;

public sealed class NoOpPreviewDialogService : IPreviewDialogService
{
    public Task<IEnumerable<CleanupEntry>> ShowPreviewAsync(string title, IEnumerable<CleanupEntry> entries, string? summary = null)
    {
        return Task.FromResult(Enumerable.Empty<CleanupEntry>());
    }
}
