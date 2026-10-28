using Microsoft.AspNetCore.Http;

namespace Bunting.Server.Infrastructure;

/// <summary>Thrown inside a write scope to abort it (rolling back) with an RFC 9457 problem response.</summary>
public sealed class ApiException(IResult result) : Exception("API problem")
{
    public IResult Result { get; } = result;
}

/// <summary>Factory for RFC 9457 ProblemDetails responses.</summary>
public static class Problems
{
    public static IResult NotFound(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");

    public static IResult Conflict(string detail, IDictionary<string, object?>? extensions = null) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict", extensions: extensions);

    public static IResult Validation(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, title: "One or more validation errors occurred.");

    public static IResult Validation(string field, params string[] messages) =>
        Validation(new Dictionary<string, string[]> { [field] = messages });

    public static IResult PreconditionRequired() =>
        Results.Problem(
            detail: "This operation requires an If-Match header containing the current ETag.",
            statusCode: StatusCodes.Status428PreconditionRequired,
            title: "Precondition required");

    public static IResult VersionConflict(long currentVersion) =>
        Results.Problem(
            detail: "The resource was modified by someone else. Fetch the latest version and retry.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Version conflict",
            extensions: new Dictionary<string, object?> { ["currentVersion"] = currentVersion });

    public static ApiException Throw(IResult result) => new(result);
}

public static class ETags
{
    public static string Format(long version) => $"\"{version}\"";

    /// <summary>Reads <c>If-Match</c> (strong or weak, quoted or bare). Throws a 428/400 problem when absent/invalid.</summary>
    public static long RequireIfMatch(HttpRequest request)
    {
        var header = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            throw Problems.Throw(Problems.PreconditionRequired());
        }

        var text = header.Trim();
        if (text.StartsWith("W/", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        if (long.TryParse(text.Trim('"'), out var version))
        {
            return version;
        }

        throw Problems.Throw(Problems.Validation("If-Match", "The If-Match header must contain a version number."));
    }

    /// <summary>Throws 409 with the current version when <paramref name="expected"/> differs.</summary>
    public static void Check(long expected, long current)
    {
        if (expected != current)
        {
            throw Problems.Throw(Problems.VersionConflict(current));
        }
    }
}
