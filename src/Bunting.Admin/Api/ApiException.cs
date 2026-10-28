using System.Net;

namespace Bunting.Admin.Api;

/// <summary>A problem response (RFC 9457) from the Management API.</summary>
public sealed class ApiException : Exception
{
    public ApiException(HttpStatusCode status, string title, string? detail, long? currentVersion = null, IReadOnlyDictionary<string, string[]>? errors = null)
        : base(detail ?? title)
    {
        Status = status;
        Title = title;
        CurrentVersion = currentVersion;
        Errors = errors ?? new Dictionary<string, string[]>();
    }

    public HttpStatusCode Status { get; }

    public string Title { get; }

    /// <summary>Set on 409 version conflicts: the version the server holds now.</summary>
    public long? CurrentVersion { get; }

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public bool IsConflict => Status == HttpStatusCode.Conflict;

    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;

    public override string Message =>
        Errors.Count > 0 ? string.Join(" ", Errors.SelectMany(e => e.Value)) : base.Message;
}
