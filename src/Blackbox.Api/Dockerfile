FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /app

# Copy solution and project definitions
COPY Blackbox.slnx ./
COPY src/Blackbox.Core/Blackbox.Core.csproj src/Blackbox.Core/
COPY src/Blackbox.Infrastructure/Blackbox.Infrastructure.csproj src/Blackbox.Infrastructure/
COPY src/Blackbox.Api/Blackbox.Api.csproj src/Blackbox.Api/

# Restore dependencies
RUN dotnet restore src/Blackbox.Api/Blackbox.Api.csproj

# Copy source code and publish
COPY src/Blackbox.Core/ src/Blackbox.Core/
COPY src/Blackbox.Infrastructure/ src/Blackbox.Infrastructure/
COPY src/Blackbox.Api/ src/Blackbox.Api/

WORKDIR /app/src/Blackbox.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "Blackbox.Api.dll"]
