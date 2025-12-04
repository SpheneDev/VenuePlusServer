using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;

namespace VenuePlus.Server;

public static class HttpPipeline
{
    public static void AddCommonServices(WebApplicationBuilder builder)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("PublicJson", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter("Microsoft", LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Error);
        builder.Services.AddRouting();
        builder.Services.Configure<JsonOptions>(opts => { opts.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase; });
    }

    public static void UseCommonMiddleware(WebApplication app)
    {
        app.UseForwardedHeaders();
        app.Use(async (ctx, next) =>
        {
            var pathVal = ctx.Request.Path.Value;
            if (!string.IsNullOrEmpty(pathVal))
            {
                var normalized = pathVal;
                while (normalized.EndsWith('/') || normalized.EndsWith('.')) normalized = normalized.Substring(0, normalized.Length - 1);
                if (!string.Equals(normalized, pathVal, StringComparison.Ordinal)) ctx.Request.Path = new PathString(normalized);
            }
            await next();
        });
        app.Use(async (ctx, next) =>
        {
            try
            {
                app.Logger.LogDebug($"HTTP {ctx.Request.Method} {ctx.Request.Path}");
                await next();
                app.Logger.LogDebug($"HTTP done {ctx.Response.StatusCode} {ctx.Request.Path}");
            }
            catch (Exception ex)
            {
                app.Logger.LogDebug($"HTTP error {ctx.Request.Path}: {ex.Message}");
                throw;
            }
        });
        app.UseCors("PublicJson");
    }
}
