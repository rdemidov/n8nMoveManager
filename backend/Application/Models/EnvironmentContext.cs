using Domain;

namespace Application.Models;

public sealed record EnvironmentContext(
    Workspace Workspace,
    EnvironmentDefinition Environment);
