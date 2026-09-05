# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Project files for Docker layer caching
COPY ["HRMS.API/HRMS.API.csproj", "HRMS.API/"]
COPY ["HRMS.Application/HRMS.Application.csproj", "HRMS.Application/"]
COPY ["HRMS.Domain/HRMS.Domain.csproj", "HRMS.Domain/"]
COPY ["HRMS.Infrastructure/HRMS.Infrastructure.csproj", "HRMS.Infrastructure/"]
COPY ["HRMS.Shared/HRMS.Shared.csproj", "HRMS.Shared/"]

# Restore dependencies
RUN dotnet restore "HRMS.API/HRMS.API.csproj"

# Copy the remaining source code
COPY . .

# Publish the API
WORKDIR "/src/HRMS.API"
RUN dotnet publish "HRMS.API.csproj" -c Release -o /app/publish /p:UseAppHost=false


# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

WORKDIR /app

# ASP.NET Core container listens on port 8080
ENV ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "HRMS.API.dll"]