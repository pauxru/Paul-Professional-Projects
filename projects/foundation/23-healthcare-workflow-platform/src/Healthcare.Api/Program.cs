using System.Text;
using System.Threading.RateLimiting;
using Healthcare.Api.Auth;
using Healthcare.Api.Endpoints;
using Healthcare.Api.Middleware;
using Healthcare.Api.Options;
using Healthcare.Application.Abstractions;
using Healthcare.Application.Access;
using Healthcare.Application.Appointments;
using Healthcare.Application.Availability;
using Healthcare.Application.Encounters;
using Healthcare.Application.Patients;
using Healthcare.Application.Referrals;
using Healthcare.Application.Reminders;
using Healthcare.Application.Reports;
using Healthcare.Application.Waitlist;
using Healthcare.Domain.Common;
using Healthcare.Infrastructure.Adapters;
using Healthcare.Infrastructure.Persistence;
using Healthcare.Infrastructure.Seeding;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ---- Options ----
builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<DatabaseOptions>().Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ReminderOptions>().Bind(builder.Configuration.GetSection(ReminderOptions.SectionName));

// ---- Database ----
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    var cfg = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    options.UseSqlite(cfg.ConnectionString);
});
builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

// ---- Application services ----
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();
// Reminder channels
builder.Services.AddSingleton<InMemorySmsReminderChannel>();
builder.Services.AddSingleton<InMemoryEmailReminderChannel>();
builder.Services.AddSingleton<IReminderChannel>(sp => sp.GetRequiredService<InMemorySmsReminderChannel>());
builder.Services.AddSingleton<IReminderChannel>(sp => sp.GetRequiredService<InMemoryEmailReminderChannel>());

builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IClinicalAccessGuard, ClinicalAccessGuard>();
builder.Services.AddScoped<PatientRegistrationService>();
builder.Services.AddScoped<AppointmentBookingService>();
builder.Services.AddScoped<SlotEngine>();
builder.Services.AddScoped<EncounterService>();
builder.Services.AddScoped<ReferralService>();
builder.Services.AddScoped<WaitlistService>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<ReportsService>();

// ---- Auth ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddSingleton<TokenIssuer>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = System.Security.Claims.ClaimTypes.Name,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.CanBookAppointment, p => p.RequireAuthenticatedUser()
        .RequireRole(Roles.Receptionist, Roles.Nurse, Roles.Clinician, Roles.ClinicalLead, Roles.Administrator));
    options.AddPolicy(Policies.CanReadClinicalRecord, p => p.RequireAuthenticatedUser()
        .RequireRole(Roles.Nurse, Roles.Clinician, Roles.ClinicalLead, Roles.Administrator, Roles.Auditor));
    options.AddPolicy(Policies.CanWriteClinicalNote, p => p.RequireAuthenticatedUser()
        .RequireRole(Roles.Nurse, Roles.Clinician, Roles.ClinicalLead));
    options.AddPolicy(Policies.CanManageWorkflow, p => p.RequireAuthenticatedUser()
        .RequireRole(Roles.Receptionist, Roles.Nurse, Roles.Clinician, Roles.ClinicalLead, Roles.Administrator));
    options.AddPolicy(Policies.CanViewAudit, p => p.RequireAuthenticatedUser()
        .RequireRole(Roles.Auditor, Roles.ClinicalLead, Roles.Administrator));
    options.AddPolicy(Policies.CanManageFacility, p => p.RequireAuthenticatedUser()
        .RequireRole(Roles.Administrator, Roles.ClinicalLead));
});

// ---- Problem details, correlation, rate limiting, OpenAPI ----
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("default", context =>
        RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.Name ?? "anon", _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
builder.Services.AddOpenApi();

// ---- OpenTelemetry ----
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddSource("Healthcare.*").AddConsoleExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMeter("Healthcare.*").AddConsoleExporter());

// ---- Static files for dashboard ----
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:3023").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Refuse to boot in Production with default signing key
if (app.Environment.IsProduction())
{
    var jwt = app.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
    if (jwt.SigningKey.StartsWith("dev-only-not-a-real-secret"))
        throw new InvalidOperationException("Refusing to start in Production with the default signing key.");
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi();
}

app.MapAuthEndpoints();
app.MapFacilityEndpoints();
app.MapClinicianEndpoints();
app.MapPatientEndpoints();
app.MapAvailabilityEndpoints();
app.MapAppointmentEndpoints();
app.MapEncounterEndpoints();
app.MapReferralEndpoints();
app.MapWaitlistEndpoints();
app.MapReminderEndpoints();
app.MapReportsEndpoints();
app.MapAuditEndpoints();

// Ensure DB is created + seeded (skip in Testing environment; the test fixture manages that)
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var clock = scope.ServiceProvider.GetRequiredService<IClock>();
    await db.Database.EnsureCreatedAsync();
    if (app.Environment.IsDevelopment())
        await DataSeeder.SeedAsync(db, clock, CancellationToken.None);
}

app.Run();

public partial class Program;
