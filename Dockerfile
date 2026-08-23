FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore ./H2HClientWpf.csproj
RUN dotnet publish ./H2HClientWpf.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
COPY --from=build /app/publish .
RUN mkdir -p /app/App_Data /app/App_Data/keys \
    && chown -R 1654:1654 /app
USER 1654
VOLUME ["/app/App_Data"]
ENTRYPOINT ["dotnet", "H2HClientWpf.dll"]
