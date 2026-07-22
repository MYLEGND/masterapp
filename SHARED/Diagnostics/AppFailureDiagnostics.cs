using System.Text;

namespace Shared.Diagnostics;

public sealed class AppFailureDiagnostics
{
    public string AppName { get; init; } = string.Empty;
    public string FailureKind { get; init; } = string.Empty;
    public int StatusCode { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public DateTime OccurredUtc { get; init; }
    public string RequestMethod { get; init; } = string.Empty;
    public string CurrentPath { get; init; } = string.Empty;
    public string CurrentQueryString { get; init; } = string.Empty;
    public string FailingPath { get; init; } = string.Empty;
    public string FailingQueryString { get; init; } = string.Empty;
    public string? EndpointDisplayName { get; init; }
    public string? RouteValuesSummary { get; init; }
    public bool IsAuthenticated { get; init; }
    public string? UserDisplay { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? InnerExceptionType { get; init; }
    public string? InnerExceptionMessage { get; init; }
    public string? FailingPoint { get; init; }
    public string? StackTrace { get; init; }
    public int ReturnUrlDepth { get; init; }
    public string? DiagnosticHint { get; init; }
    public string? ResponseLocation { get; init; }

    public string CurrentRequestTarget => CombinePathAndQuery(CurrentPath, CurrentQueryString);

    public string FailingRequestTarget => CombinePathAndQuery(FailingPath, FailingQueryString);

    public string ReportText
    {
        get
        {
            var lines = new List<string>
            {
                $"App: {AppName}",
                $"Failure Kind: {FailureKind}",
                $"Status Code: {StatusCode}",
                $"Summary: {Summary}",
                $"Request ID: {RequestId}",
                $"Occurred UTC: {OccurredUtc:O}",
                $"Request: {RequestMethod} {FailingRequestTarget}",
                $"Current Handler Path: {CurrentRequestTarget}",
                $"Authenticated: {IsAuthenticated}"
            };

            if (!string.IsNullOrWhiteSpace(UserDisplay))
                lines.Add($"User: {UserDisplay}");

            if (!string.IsNullOrWhiteSpace(EndpointDisplayName))
                lines.Add($"Endpoint: {EndpointDisplayName}");

            if (!string.IsNullOrWhiteSpace(RouteValuesSummary))
                lines.Add($"Route Values: {RouteValuesSummary}");

            if (!string.IsNullOrWhiteSpace(ExceptionType))
                lines.Add($"Exception Type: {ExceptionType}");

            if (!string.IsNullOrWhiteSpace(ExceptionMessage))
                lines.Add($"Exception Message: {ExceptionMessage}");

            if (!string.IsNullOrWhiteSpace(InnerExceptionType))
                lines.Add($"Inner Exception Type: {InnerExceptionType}");

            if (!string.IsNullOrWhiteSpace(InnerExceptionMessage))
                lines.Add($"Inner Exception Message: {InnerExceptionMessage}");

            if (!string.IsNullOrWhiteSpace(FailingPoint))
                lines.Add($"Failing Point: {FailingPoint}");

            if (ReturnUrlDepth > 0)
                lines.Add($"returnUrl Depth: {ReturnUrlDepth}");

            if (!string.IsNullOrWhiteSpace(ResponseLocation))
                lines.Add($"Response Location: {ResponseLocation}");

            if (!string.IsNullOrWhiteSpace(DiagnosticHint))
                lines.Add($"Hint: {DiagnosticHint}");

            if (!string.IsNullOrWhiteSpace(StackTrace))
            {
                lines.Add("Stack Trace:");
                lines.Add(StackTrace);
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    private static string CombinePathAndQuery(string? path, string? queryString)
    {
        var pathValue = string.IsNullOrWhiteSpace(path) ? "/" : path;
        var queryValue = string.IsNullOrWhiteSpace(queryString) ? string.Empty : queryString;

        return string.Concat(pathValue, queryValue);
    }
}
