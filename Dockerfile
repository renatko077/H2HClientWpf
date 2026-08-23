FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore ./H2HClientWpf.csproj
RUN dotnet publish ./H2HClientWpf.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["sh", "-c", "dotnet H2HClientWpf.dll --urls http://0.0.0.0:${PORT:-8080}"]
