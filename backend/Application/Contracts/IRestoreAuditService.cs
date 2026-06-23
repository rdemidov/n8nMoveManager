using Application.Models;

namespace Application.Contracts;

public interface IRestoreAuditService
{
    Task RecordAsync(RestoreAuditCreate audit, CancellationToken cancellationToken);
}
