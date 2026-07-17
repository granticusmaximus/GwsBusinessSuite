using System.Collections.Concurrent;
using Microsoft.AspNetCore.Routing;

namespace GwsBusinessSuite.Web.Services;

public sealed class PerformanceTelemetry
{
    private readonly ConcurrentDictionary<string, RouteMetric> _routes = new(StringComparer.OrdinalIgnoreCase);

    public void Record(HttpContext context, TimeSpan elapsed)
    {
        var endpoint = context.GetEndpoint() as RouteEndpoint;
        var route = endpoint?.RoutePattern.RawText ?? context.Request.Path.Value ?? "/";
        var key = $"{context.Request.Method} {route}";
        _routes.GetOrAdd(key, static _ => new RouteMetric()).Record(elapsed, context.Response.StatusCode);
    }

    public IReadOnlyList<RoutePerformanceSnapshot> Snapshot() => _routes
        .Select(pair => pair.Value.Snapshot(pair.Key))
        .OrderByDescending(metric => metric.AverageMilliseconds)
        .ThenByDescending(metric => metric.RequestCount)
        .ToList();

    private sealed class RouteMetric
    {
        private readonly object _gate = new();
        private long _count;
        private double _totalMilliseconds;
        private double _maximumMilliseconds;
        private int _lastStatusCode;
        private DateTimeOffset _lastObservedAt;

        public void Record(TimeSpan elapsed, int statusCode)
        {
            lock (_gate)
            {
                _count++;
                _totalMilliseconds += elapsed.TotalMilliseconds;
                _maximumMilliseconds = Math.Max(_maximumMilliseconds, elapsed.TotalMilliseconds);
                _lastStatusCode = statusCode;
                _lastObservedAt = DateTimeOffset.UtcNow;
            }
        }

        public RoutePerformanceSnapshot Snapshot(string route)
        {
            lock (_gate)
            {
                return new RoutePerformanceSnapshot(
                    route,
                    _count,
                    _count == 0 ? 0 : _totalMilliseconds / _count,
                    _maximumMilliseconds,
                    _lastStatusCode,
                    _lastObservedAt);
            }
        }
    }
}

public sealed record RoutePerformanceSnapshot(
    string Route,
    long RequestCount,
    double AverageMilliseconds,
    double MaximumMilliseconds,
    int LastStatusCode,
    DateTimeOffset LastObservedAt);
