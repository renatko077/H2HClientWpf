FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY H2HClientWpf.csproj .
RUN dotnet restore H2HClientWpf.csproj
COPY . .
RUN dotnet publish H2HClientWpf.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
COPY --from=build --chown=app:app /app/publish .
RUN mkdir -p /app/App_Data && chown -R app:app /app/App_Data
USER app
VOLUME ["/app/App_Data"]
ENTRYPOINT ["dotnet", "H2HClientWpf.dll"]
