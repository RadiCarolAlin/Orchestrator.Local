# ========== build ==========
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app /p:UseAppHost=false

# ========== runtime ==========
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .

# Cloud Run expune portul 8080; suprascriem orice setare din appsettings
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

# dacă DLL-ul tău se numește altfel, schimbă numele de mai jos
ENTRYPOINT ["dotnet", "Orchestrator.Local.dll"]
