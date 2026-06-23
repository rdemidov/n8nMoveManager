using Application.Models;

namespace Application.Contracts;

public interface ILocalUserService
{
    Task EnsureBootstrapAdminAsync(string userName, string password, CancellationToken cancellationToken);
    Task<LocalUserDto?> ValidateAsync(string userName, string password, CancellationToken cancellationToken);
    Task<IReadOnlyList<LocalUserDto>> ListAsync(CancellationToken cancellationToken);
    Task<LocalUserDto> CreateAsync(LocalUserRequest request, CancellationToken cancellationToken);
}
