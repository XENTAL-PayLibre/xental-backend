namespace Xental.Application.Common.Exceptions;

/// <summary>Input failed validation (maps to 400).</summary>
public sealed class ValidationException(string message) : Exception(message);

/// <summary>Client credentials invalid / tenant not active (maps to 401).</summary>
public sealed class AuthenticationException(string message) : Exception(message);

/// <summary>A uniqueness/state conflict (maps to 409).</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>A required resource was not found (maps to 404).</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>A downstream Nomba call failed (maps to 502).</summary>
public sealed class NombaIntegrationException(string message, Exception? inner = null)
    : Exception(message, inner);
