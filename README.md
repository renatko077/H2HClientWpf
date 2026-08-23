# H2HClientWpf — ASP.NET Core версия

Это единственный активный проект в репозитории `H2HClientWpf`: прежнее WPF-приложение заменено ASP.NET Core MVC сайтом. Проект рассчитан на Windows/IIS или Linux/Docker.

## Локальный запуск

```powershell
dotnet restore
dotnet run
```

В Development пароль по умолчанию — `admin`. Он задан только в `appsettings.Development.json`.

## Обязательные настройки сервера

Задайте переменные окружения:

```text
ASPNETCORE_ENVIRONMENT=Production
H2H_ADMIN_PASSWORD=<длинный уникальный пароль>
Payment__ProductionBaseUrl=https://адрес-платёжного-api
Webhooks__RequireValidSignature=true
```

Не размещайте сайт без HTTPS. Каталог `App_Data` должен быть постоянным и доступным на запись процессу приложения: там находятся SQLite, история и ключи Data Protection.

## GitHub

В репозитории должен находиться этот проект прямо в корне — рядом должны лежать `H2HClientWpf.csproj`, `Dockerfile` и `Program.cs`. Workflow `.github/workflows/build.yml` проверяет каждый push и создаёт готовый артефакт `H2HClientWpf-server` во вкладке GitHub Actions.

## Docker

```bash
docker build -t h2h-client-web .
docker run -d --name h2h-client-web \
  -p 8080:8080 \
  -e H2H_ADMIN_PASSWORD='replace-me' \
  -v h2h-data:/app/App_Data \
  h2h-client-web
```

Поставьте Nginx/Caddy/Cloudflare перед контейнером и проксируйте HTTPS на `http://127.0.0.1:8080`.

## IIS

1. Установите .NET 8 Hosting Bundle на сервер.
2. Выполните `dotnet publish -c Release`.
3. Создайте IIS Application Pool с `No Managed Code`.
4. Укажите физический путь на папку `bin/Release/net8.0/publish`.
5. Дайте identity пула права Modify на `App_Data`.
6. Добавьте `H2H_ADMIN_PASSWORD` в переменные окружения IIS и включите HTTPS binding.

## Адреса

- сайт: `/`;
- callback: `/api/webhooks/payment`;
- health-check: `/health`.

Подробное сравнение с WPF и Flask находится в `MIGRATION_NOTES.md`.
