using HRMS.API.Authorization;
using HRMS.API.Middleware;
using HRMS.Application.Features.Auth;
using HRMS.Application.Features.Employees;
using HRMS.Infrastructure.Audit;
using HRMS.Infrastructure.Employees;
using HRMS.Infrastructure.Authorization;
using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Common;
using HRMS.Infrastructure.Persistence;
using HRMS.Infrastructure.Persistence.Repositories;
using HRMS.Infrastructure.Outbox;
using HRMS.API.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using MediatR;
using HRMS.Application.Features.Auth.Commands.Login;
using Microsoft.OpenApi;
using HRMS.Infrastructure.Persistence.SeedData;
using HRMS.API.Extensions;
using HRMS.API.Services;
using HRMS.Application.Common.Behaviors;
using FluentValidation;


namespace HRMS.API
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddControllers(options =>
            {
                // Deny by default: every action must declare [AllowAnonymous], [RequirePermission] or [SelfService].
                options.Filters.Add<PermissionAuthorizationFilter>();
            });

            // Allowed browser origins come from configuration (Cors:AllowedOrigins); see CorsSetup.
            builder.Services.ConfigureFrontendCors();

            builder.Services.AddHttpContextAccessor();

            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen( options =>
            {
                options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = ParameterLocation.Header,
                    Description = "Enter your JWT Token."
                });

                options.AddSecurityRequirement(document =>
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", document)] = []
                });
            });

            builder.Services.AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssembly(typeof(LoginCommandHandler).Assembly);
                // Order matters: the first registered behavior is the outermost. Validation runs before a transaction is opened.
                cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
                cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
            });

            builder.Services.AddValidatorsFromAssembly(typeof(LoginCommandHandler).Assembly);
            // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
            // builder.Services.AddOpenApi();

            builder.Services.AddDbContext<HrmsDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

            builder.Services.AddScoped<ITenantContext, TenantContext>();
            builder.Services.AddScoped<ITenantResolver, TenantResolver>();
            builder.Services.AddScoped<IUserRepository, UserRepository>();
            builder.Services.AddScoped<ISessionRepository, SessionRepository>();
            builder.Services.AddScoped<IPasswordHasher, BCryptPasswordHasher>();
            builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
            builder.Services.AddScoped<ITokenHasher, TokenHasher>();
            builder.Services.AddScoped<IClientInfo, HttpClientInfo>();
            builder.Services.AddScoped<CurrentUserService>();
            builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserService>());
            builder.Services.AddScoped<ICurrentUserLoader>(sp => sp.GetRequiredService<CurrentUserService>());
            builder.Services.AddScoped<IDataScopeService, DataScopeService>();
            builder.Services.AddScoped<IReferenceValidator, ReferenceValidator>();
            builder.Services.AddScoped<AuditWriter>();
            builder.Services.AddScoped<IAuditWriter>(sp => sp.GetRequiredService<AuditWriter>());
            builder.Services.AddScoped<IAuditStagingState>(sp => sp.GetRequiredService<AuditWriter>());
            builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
            builder.Services.AddScoped<IEmployeeRepository, EmployeeRepository>();
            builder.Services.AddScoped<IEmployeeQueries, EmployeeQueries>();
            builder.Services.AddScoped<IEmployeeCodeGenerator, EmployeeCodeGenerator>();
            builder.Services.AddScoped<IDepartmentRepository, HRMS.Infrastructure.Departments.DepartmentRepository>();
            builder.Services.AddScoped<IDepartmentQueries, HRMS.Infrastructure.Departments.DepartmentQueries>();
            builder.Services.AddScoped<IRoleQueries, HRMS.Infrastructure.Roles.RoleQueries>();
            builder.Services.AddScoped<IRoleRepository, HRMS.Infrastructure.Roles.RoleRepository>();
            builder.Services.AddSingleton<ITemporaryPasswordGenerator, TemporaryPasswordGenerator>();

            // Onboarding: one-time password-setup invitations. No delivery channel (email provider) is bundled or assumed; replace
            // NotConfiguredInvitationSender with a real IAccountInvitationSender once one is chosen (decision D9).
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(new AccountSetupOptions
            {
                InvitationLifetime = TimeSpan.FromHours(builder.Configuration.GetValue<int?>("Onboarding:InvitationLifetimeHours") ?? AccountSetupOptions.DefaultInvitationLifetimeHours)
            });
            builder.Services.AddScoped<PostCommitActions>();
            builder.Services.AddScoped<IPostCommitActions>(sp => sp.GetRequiredService<PostCommitActions>());
            builder.Services.AddScoped<IAccountRepository, AccountRepository>();
            builder.Services.AddScoped<IAccountInvitationSender, NotConfiguredInvitationSender>();
            builder.Services.AddScoped<AccountInvitationIssuer>();

            // Transactional outbox for invitations: the delivery intent is saved with the account; the worker (and an immediate
            // attempt after the commit) deliver it outside any transaction, with bounded retries. See docs/PROJECT_STATUS.md.
            // Bound lazily from the BUILT host's configuration (not builder.Configuration), so every configuration source - including
            // the in-memory one the test host adds - is honoured.
            builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection(OutboxOptions.SectionName).Get<OutboxOptions>() ?? new OutboxOptions());
            builder.Services.AddSingleton<IInvitationDispatcher, InvitationDispatcher>();
            builder.Services.AddScoped<InvitationOutboxProcessor>();
            builder.Services.AddHostedService<OutboxWorker>();

            // Rejected security-relevant attempts (anonymous or not) that the business audit log cannot hold.
            builder.Services.AddScoped<ISecurityEventWriter, SecurityEventWriter>();

            // Rate limiting of the credential endpoints, and the (opt-in, explicit) trust in forwarded client addresses.
            builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new RateLimitingOptions());
            builder.Services.AddAuthRateLimiting();
            builder.Services.ConfigureForwardedHeaders();

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                var secret = builder.Configuration["Jwt:Secret"] ?? throw new InvalidOperationException("JWT Secret is not configured.");

                var issuer = builder.Configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("JWT Issuer is not configured.");

                var audience = builder.Configuration["Jwt:Audience"] ?? throw new InvalidOperationException("JWT Audience is not configured.");

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),

                    ValidateIssuer = true,
                    ValidIssuer = issuer,

                    ValidateAudience = true,
                    ValidAudience = audience,

                    ValidateLifetime = true,

                    ClockSkew = TimeSpan.Zero
                };

                options.Events = new JwtBearerEvents
                {
                    // The JWT is valid cryptographically; additionally require a live server-side session.
                    OnTokenValidated = async context =>
                    {
                        var token = context.Request.GetBearerToken();

                        if (token is null)
                        {
                            context.Fail("Missing bearer token.");
                            return;
                        }

                        var tokenHasher = context.HttpContext.RequestServices.GetRequiredService<ITokenHasher>();
                        var sessionRepository = context.HttpContext.RequestServices.GetRequiredService<ISessionRepository>();

                        var hasActiveSession = await sessionRepository.ExistsActiveAsync(
                            tokenHasher.Hash(token),
                            context.HttpContext.RequestAborted);

                        if (!hasActiveSession)
                        {
                            context.Fail("Session is revoked or expired.");
                        }
                    }
                };
            });

            var app = builder.Build();

            // An invalid or (in Production) missing CORS configuration stops the application here, before any migration or seeding.
            app.Services.ValidateFrontendCors();

            // First: everything after it (rate limiting, audit, sessions) sees the client address, not the proxy's. Does nothing unless
            // the deployment enabled it and named its proxies (ForwardedHeadersSetup).
            app.UseForwardedHeaders();

            app.UseMiddleware<ExceptionHandlingMiddleware>();

            if(app.Environment.IsDevelopment())
            {
                using var scope = app.Services.CreateScope();

                var dbContext = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();

                var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

                await DevelopmentDbSeeder.SeedAsync(dbContext, passwordHasher, builder.Configuration);

                // Off unless HRMS_SUPERADMIN_* is configured; see SuperAdminProvisioner.
                await SuperAdminProvisioner.ProvisionAsync(dbContext, passwordHasher, builder.Configuration, scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("HRMS.SuperAdminProvisioning"));
            }

            if(app.Environment.IsProduction())
            {
                using var scope = app.Services.CreateScope();

                var dbContext = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();

                var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

                await ProductionDbSeeder.SeedAsync(dbContext,passwordHasher, builder.Configuration);

                // Off unless HRMS_SUPERADMIN_* is configured; see SuperAdminProvisioner.
                await SuperAdminProvisioner.ProvisionAsync(dbContext, passwordHasher, builder.Configuration, scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("HRMS.SuperAdminProvisioning"));
            }

            app.UseSwagger();
            app.UseSwaggerUI();


            // we are going to remove OpenAPI and instead we are going to use Swashbuckle and configure swagger.

            // Configure the HTTP request pipeline.
            // if (app.Environment.IsDevelopment())
            //{
            //    app.MapOpenApi();
            //}



            app.UseCors(CorsSetup.PolicyName);

            app.UseAuthentication();

            // After authentication (per-user limits need the identity), before the tenant lookup and the controllers.
            app.UseRateLimiter();

            app.UseMiddleware<TenantMiddleware>();

            app.UseAuthorization();


            app.MapControllers();

            app.Run();
        }
    }
}
