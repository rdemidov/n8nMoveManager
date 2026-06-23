namespace Application.Models;

public sealed record LoginRequest(string UserName, string Password);
public sealed record LoginResult(string AccessToken, DateTimeOffset ExpiresAt, string UserName, string Role);
public sealed record LocalUserRequest(string UserName, string Password, string Role);
public sealed record LocalUserDto(Guid Id, string UserName, string Role, bool IsEnabled, DateTimeOffset CreatedAt);
