using HRMS.API.Middleware;
using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Common;
using HRMS.Infrastructure.Persistence;
using HRMS.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using MediatR;
using HRMS.Application.Features.Auth.Commands.Login;
using Microsoft.OpenApi;
using HRMS.Infrastructure.Persistence.SeedData;


namespace HRMS.API
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddControllers();

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

            builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(LoginCommandHandler).Assembly));
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
            });

            var app = builder.Build();

            if(app.Environment.IsDevelopment())
            {
                using var scope = app.Services.CreateScope();

                var dbContext = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();

                var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

                await DevelopmentDbSeeder.SeedAsync(dbContext, passwordHasher, builder.Configuration);
            }

            if(app.Environment.IsProduction())
            {
                using var scope = app.Services.CreateScope();

                var dbContext = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();

                var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

                await ProductionDbSeeder.SeedAsync(dbContext,passwordHasher, builder.Configuration);
            }

            if(app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }


            // we are going to remove OpenAPI and instead we are going to use Swashbuckle and configure swagger.

            // Configure the HTTP request pipeline.
           // if (app.Environment.IsDevelopment())
            //{
            //    app.MapOpenApi();
            //}

            

            

            app.UseAuthentication();

            app.UseMiddleware<TenantMiddleware>();

            app.UseAuthorization();


            app.MapControllers();

            app.Run();
        }
    }
}
