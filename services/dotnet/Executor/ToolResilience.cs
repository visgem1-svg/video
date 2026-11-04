using System.Net;

namespace Executor;

public static class ToolResilience
{
    /// <summary>429 is engine back-pressure, not a broken engine: only exceptions/timeouts and 5xx trip the breaker.</summary>
    public static bool TripsBreaker(Exception? ex, HttpStatusCode? status) => ex is not null || (int?)status >= 500;
}
