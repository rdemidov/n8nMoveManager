using Application.Models;

namespace Application.Contracts;

public interface ILogicalCredentialService
{
    Task<IReadOnlyList<LogicalCredentialDto>> ListAsync(CancellationToken cancellationToken);
    Task<LogicalCredentialDto> CreateAsync(LogicalCredentialRequest request, CancellationToken cancellationToken);
    Task<LogicalCredentialDto> UpdateAsync(Guid id, LogicalCredentialRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task<LogicalCredentialDto> SetMappingAsync(LogicalCredentialMappingRequest request, CancellationToken cancellationToken);
    Task<LogicalCredentialDto> SetPairMappingAsync(LogicalCredentialPairMappingRequest request, CancellationToken cancellationToken);
    Task DeleteMappingAsync(Guid mappingId, CancellationToken cancellationToken);
}
