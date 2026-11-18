using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CompanyManagement.API.Infrastructure;
using CompanyManagement.API.Mcp;
using CompanyManagement.Application;
using CompanyManagement.Infrastructure;
using CompanyManagement.Infrastructure.Persistence;
using CompanyManagement.Infrastructure.Realtime;
using CompanyManagement.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    // ---------------- Authentication (JWT) ----------------
    var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
    if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
        throw new InvalidOperationException(
            "Jwt:SigningKey must be configured (at least 32 characters). Use user-secrets or the Jwt__SigningKey environment variable.");

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = jwt.Issuer,
                ValidateAudience = true, ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = System.Security.Claims.ClaimTypes.Name,
                RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            };
            // SignalR sends the access token in the query string for WebSockets / SSE.
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    var token = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments(NotificationHub.Path))
                        ctx.Token = token;
                    return Task.CompletedTask;
                }
            };
        })
        // Claude (Claude Code / Claude Desktop) with a personal key — accepted only on /mcp.
        .AddScheme<AuthenticationSchemeOptions, PersonalKeyAuthenticationHandler>(PersonalKeyAuthenticationHandler.SchemeName, null);
    builder.Services.AddAuthorization(o =>
    {
        o.AddPermissionPolicies();
        o.AddPolicy(McpSetup.Policy, p => p.AddAuthenticationSchemes(PersonalKeyAuthenticationHandler.SchemeName).RequireAuthenticatedUser());
    });

    // ---------------- MCP: TaskVeta for Claude ----------------
    builder.Services.AddMcpServer(o =>
        {
            o.ServerInfo = new Implementation { Name = "taskveta", Title = "TaskVeta", Version = "1.0.0" };
            o.ServerInstructions = TaskVetaPrompts.Instructions;
        })
        .WithHttpTransport(o => o.Stateless = true)
        .WithTools<TaskVetaTools>()
        .WithPrompts<TaskVetaPrompts>();

    // ---------------- MVC / validation / errors ----------------
    builder.Services.AddControllers(o => o.Filters.Add<ValidationFilter>())
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        });
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

    // ---------------- CORS (only needed when the SPA is served from another origin) ----------------
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    {
        if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    }));

    // ---------------- Rate limiting ----------------
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        // Employees at HQ usually share one public/NAT IP, so IP-based limits must be generous.
        // Per-account brute force is stopped by Identity lockout (5 failures → 15 min).
        o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
            "login:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimits:LoginPerMinutePerIp", 60), Window = TimeSpan.FromMinutes(1) }));
        o.AddPolicy("refresh", ctx => RateLimitPartition.GetFixedWindowLimiter(
            "refresh:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimits:RefreshPerMinutePerIp", 300), Window = TimeSpan.FromMinutes(1) }));
        // The personal key is checked by the endpoint's policy (after this middleware), so partition by the key itself (hashed):
        // everyone at the office shares one IP and must not share one budget.
        o.AddPolicy(McpSetup.Policy, ctx => RateLimitPartition.GetFixedWindowLimiter(
            "mcp:" + McpSetup.PartitionKey(ctx),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimits:McpPerMinutePerUser", 120), Window = TimeSpan.FromMinutes(1) }));
        // Runs after authentication, so it is partitioned per user.
        o.AddPolicy("attendance", ctx => RateLimitPartition.GetFixedWindowLimiter(
            "att:" + (ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    });

    // ---------------- Reverse proxy support (needed for the company-network IP check) ----------------
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;
        foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
            if (IPAddress.TryParse(proxy, out var ip)) o.KnownProxies.Add(ip);
    });

    // ---------------- Swagger ----------------
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "Company Management API", Version = "v1", Description = "Attendance & task management" });
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header,
            Description = "Paste the access token returned by /api/auth/login"
        });
        c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = [] });
        c.CustomSchemaIds(t => t.FullName?.Replace("+", "."));
    });

    var app = builder.Build();

    app.UseForwardedHeaders();
    app.UseExceptionHandler();
    app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
        ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error : Serilog.Events.LogEventLevel.Information);

    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        await next();
    });

    if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Swagger:Enabled", false))
    {
        app.UseSwagger();
        app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Company Management API v1"));
    }
    else
    {
        app.UseHsts();
    }
    if (app.Configuration.GetValue("Https:Redirect", false)) app.UseHttpsRedirection();

    // Angular SPA built into wwwroot (single-origin deployment). Hashed bundles are cached for a year;
    // index.html, translations and other unhashed files are revalidated so a new deployment shows up immediately.
    var spaFiles = new StaticFileOptions { OnPrepareResponse = ctx => SpaCaching.Apply(ctx.Context, ctx.File.Name) };
    app.UseDefaultFiles();
    app.UseStaticFiles(spaFiles);

    app.UseCors();
    app.UseAuthentication();
    app.UseAccountGate(); // temporary passwords and client accounts are limited to their own endpoints
    app.UseRateLimiter(); // after authentication so policies can partition by user
    app.UseAuthorization();

    app.MapControllers();
    app.MapHub<NotificationHub>(NotificationHub.Path);
    app.MapMcp(McpSetup.Path).RequireAuthorization(McpSetup.Policy).RequireRateLimiting(McpSetup.Policy);
    app.MapHealthChecks("/health").AllowAnonymous();
    app.MapFallback("/api/{**path}", () => Results.NotFound()).AllowAnonymous(); // unknown API routes are 404, not the SPA
    app.MapFallbackToFile("index.html", spaFiles).AllowAnonymous();

    // One-time bootstrap for an empty database: dotnet run -- create-admin
    if (args.Contains("create-admin", StringComparer.OrdinalIgnoreCase))
    {
        Environment.ExitCode = await DatabaseSeeder.CreateAdminInteractiveAsync(app.Services);
        return;
    }

    await DatabaseSeeder.RunAsync(app.Services);
    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException && ex.GetType().Name != "StopTheHostException")
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program;

