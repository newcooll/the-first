using System.Collections.Generic;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.UI.Services;

public interface IPreviewDialogService
{
    Task<IEnumerable<CleanupEntry>> ShowPreviewAsync(string title, IEnumerable<CleanupEntry> entries, string? summary = null);
}
