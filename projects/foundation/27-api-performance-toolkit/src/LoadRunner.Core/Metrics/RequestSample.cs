namespace LoadRunner.Core.Metrics;

public enum ErrorKind
{
    None,
    Connection,
    Timeout,
    Http4xx,
    Http5xx,
    AssertionFailure,
    Other
}

public sealed record RequestSample(
    string StepName,
    long IntendedStartUnixMs,
    long ActualStartUnixMs,
    long CompletedUnixMs,
    long ServiceLatencyNs,
    long IntendedLatencyNs,
    int StatusCode,
    ErrorKind Error,
    int VuId,
    long BytesReceived);
