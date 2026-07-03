namespace Xental.Application.Common.Exceptions;

/// <summary>Input failed validation (maps to 400).</summary>
public sealed class ValidationException(string message) : Exception(message);

/// <summary>Client credentials invalid / tenant not active (maps to 401).</summary>
public sealed class AuthenticationException(string message) : Exception(message);

/// <summary>Credentials are valid but the account's email is not verified (maps to 403).</summary>
public sealed class EmailNotVerifiedException(string message) : Exception(message);

/// <summary>The action needs an approved onboarding the tenant doesn't have (maps to 403).</summary>
public sealed class OnboardingNotApprovedException(string message) : Exception(message);

/// <summary>The caller lacks permission for this action (maps to 403).</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>A uniqueness/state conflict (maps to 409).</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>A required resource was not found (maps to 404).</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>A downstream Nomba call failed (maps to 502).</summary>
public sealed class NombaIntegrationException(string message, Exception? inner = null)
    : Exception(message, inner);
