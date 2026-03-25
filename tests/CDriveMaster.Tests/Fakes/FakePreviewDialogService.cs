using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;
using CDriveMaster.UI.Services;

namespace CDriveMaster.Tests.Fakes;

public sealed class FakePreviewDialogService : IPreviewDialogService
{
    public bool UserConfirmed { get; set; } = true;

    public Func<IEnumerable<CleanupEntry>, IEnumerable<CleanupEntry>>? SelectionLogic { get; set; }

    public bool WasCalled { get; private set; }

    public Task<IEnumerable<CleanupEntry>> ShowPreviewAsync(string title, IEnumerable<CleanupEntry> entries)
    {
        WasCalled = true;

        if (!UserConfirmed)
        {
            return Task.FromResult(Enumerable.Empty<CleanupEntry>());
        }

        var source = entries.ToList();
        var selected = SelectionLogic is null ? source : SelectionLogic(source).ToList();
        return Task.FromResult(selected.AsEnumerable());
    }
}
