using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public interface IRecycleBinService
{
    Task<RecycleBinInfo> QueryAsync(CancellationToken cancellationToken);
    Task<CleanupResult> EmptyAsync(bool showConfirmation, bool showProgressUi, bool playSound, CancellationToken cancellationToken);
}
