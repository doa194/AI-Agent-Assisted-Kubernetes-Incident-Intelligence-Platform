using Microsoft.AspNetCore.Http;

namespace KubeSage.Workload.Shared.Logging;

// Reads the correlation identifier from the incoming request, or creates one
// if this is the first hop, and makes it available for the rest of the
// request. It is also echoed back on the response so a caller (including the
// traffic generator) can record which identifier its request was given.
public sealed class CorrelationMiddleware
{
    private readonly RequestDelegate _next;

    public CorrelationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[CorrelationContext.HeaderName].FirstOrDefault();
        var correlationId = CorrelationContext.Sanitise(incoming);

        CorrelationContext.CurrentId = correlationId;
        context.Response.Headers[CorrelationContext.HeaderName] = correlationId;

        try
        {
            await _next(context);
        }
        finally
        {
            // Cleared so a pooled thread cannot leak this request's identifier
            // into an unrelated background operation.
            CorrelationContext.CurrentId = null;
        }
    }
}

// Adds the current correlation identifier to every outgoing HTTP call, which
// is what actually propagates it between services.
public sealed class CorrelationPropagationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var correlationId = CorrelationContext.CurrentId;

        if (!string.IsNullOrEmpty(correlationId) &&
            !request.Headers.Contains(CorrelationContext.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(CorrelationContext.HeaderName, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
