namespace SusuCircle.Api.Common.Exceptions;

public class NotFoundException(string entity, object key)
    : Exception($"{entity} with key '{key}' was not found.");

public class ValidationException(IEnumerable<string> errors)
    : Exception("One or more validation errors occurred.")
{
    public IEnumerable<string> Errors { get; } = errors;
}

public class UnauthorizedException(string message = "Unauthorized.")
    : Exception(message);

public class ConflictException(string message)
    : Exception(message);

public class NombaApiException(string message, int? statusCode = null)
    : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
