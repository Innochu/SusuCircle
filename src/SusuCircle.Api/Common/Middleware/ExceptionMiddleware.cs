using System.Net;
using System.Text.Json;
using SusuCircle.Api.Common.Exceptions;

namespace SusuCircle.Api.Common.Middleware;

public class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (Exception ex) { await HandleAsync(ctx, ex); }
    }

    private async Task HandleAsync(HttpContext ctx, Exception ex)
    {
        logger.LogError(ex, "Unhandled exception: {Message}", ex.Message);

        var (status, message, errors) = ex switch
        {
            NotFoundException e    => (HttpStatusCode.NotFound, e.Message, (IEnumerable<string>?)null),
            ValidationException e  => (HttpStatusCode.BadRequest, e.Message, e.Errors),
            UnauthorizedException e => (HttpStatusCode.Unauthorized, e.Message, null),
            ConflictException e    => (HttpStatusCode.Conflict, e.Message, null),
            NombaApiException e    => (HttpStatusCode.BadGateway, e.Message, null),
            _                      => (HttpStatusCode.InternalServerError, "An unexpected error occurred.", null)
        };

        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode  = (int)status;

        var body = JsonSerializer.Serialize(new
        {
            success = false,
            message,
            errors
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        await ctx.Response.WriteAsync(body);
    }
}
