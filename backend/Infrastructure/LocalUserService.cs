using System.Security.Cryptography;
using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public sealed class LocalUserService(AppDbContext dbContext) : ILocalUserService
{
    private static readonly HashSet<string> Roles = ["Viewer", "Editor", "Approver", "Admin"];

    public async Task EnsureBootstrapAdminAsync(string userName, string password, CancellationToken cancellationToken)
    {
        if (await dbContext.LocalUsers.AnyAsync(cancellationToken)) return;
        await CreateAsync(new LocalUserRequest(userName, password, "Admin"), cancellationToken);
    }

    public async Task<LocalUserDto?> ValidateAsync(string userName, string password, CancellationToken cancellationToken)
    {
        var user = await dbContext.LocalUsers.SingleOrDefaultAsync(x => x.UserName == NormalizeUserName(userName), cancellationToken);
        return user is not null && user.IsEnabled && Verify(password, user.PasswordHash) ? ToDto(user) : null;
    }

    public async Task<IReadOnlyList<LocalUserDto>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.LocalUsers.AsNoTracking().OrderBy(x => x.UserName).Select(x => new LocalUserDto(x.Id, x.UserName, x.Role, x.IsEnabled, x.CreatedAt)).ToArrayAsync(cancellationToken);

    public async Task<LocalUserDto> CreateAsync(LocalUserRequest request, CancellationToken cancellationToken)
    {
        var name = NormalizeUserName(request.UserName);
        if (name.Length < 3 || request.Password.Length < 12) throw new WorkflowImportException("User names require 3 characters and passwords require at least 12 characters.");
        var role = Roles.Contains(request.Role, StringComparer.OrdinalIgnoreCase) ? Roles.Single(x => x.Equals(request.Role, StringComparison.OrdinalIgnoreCase)) : throw new WorkflowImportException("Role must be Viewer, Editor, Approver, or Admin.");
        if (await dbContext.LocalUsers.AnyAsync(x => x.UserName == name, cancellationToken)) throw new WorkflowImportException("That user name already exists.");
        var now = DateTimeOffset.UtcNow;
        var user = new LocalUser { Id = Guid.NewGuid(), UserName = name, PasswordHash = Hash(request.Password), Role = role, CreatedAt = now, UpdatedAt = now };
        dbContext.LocalUsers.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(user);
    }

    private static string NormalizeUserName(string userName) => userName.Trim().ToLowerInvariant();
    private static LocalUserDto ToDto(LocalUser user) => new(user.Id, user.UserName, user.Role, user.IsEnabled, user.CreatedAt);
    private static string Hash(string password) { var salt = RandomNumberGenerator.GetBytes(16); var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA512, 32); return $"v1.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}"; }
    private static bool Verify(string password, string stored) { var parts = stored.Split('.'); if (parts.Length != 3 || parts[0] != "v1") return false; var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[1]), 210000, HashAlgorithmName.SHA512, 32); return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(parts[2])); }
}
