FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /app

COPY Blackbox.slnx ./
COPY src/Blackbox.Core/Blackbox.Core.csproj src/Blackbox.Core/
COPY src/Blackbox.Infrastructure/Blackbox.Infrastructure.csproj src/Blackbox.Infrastructure/
COPY src/Blackbox.Worker/Blackbox.Worker.csproj src/Blackbox.Worker/

RUN dotnet restore src/Blackbox.Worker/Blackbox.Worker.csproj

COPY src/Blackbox.Core/ src/Blackbox.Core/
COPY src/Blackbox.Infrastructure/ src/Blackbox.Infrastructure/
COPY src/Blackbox.Worker/ src/Blackbox.Worker/

WORKDIR /app/src/Blackbox.Worker
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID
ENTRYPOINT ["dotnet", "Blackbox.Worker.dll"]
