@echo off
setlocal
set "ASPNETCORE_ENVIRONMENT=Development"
set "ASPNETCORE_URLS=http://localhost:5036"
dotnet run --project "%~dp0H2HClientWpf.csproj" --no-launch-profile
endlocal
