# Nexus Daemon — исходники

`Nexus.Daemon/` — сервис `nexusd` (.NET 8, roll-forward на новые мажорные версии). Тильда в имени папки — Unity
её не импортирует. Собранный демон лежит в `Tools~/daemon/` и коммитится вместе с исходниками.

Сборка после изменений (версию поднять в `Nexus.Daemon.csproj` → `<Version>` и в `Tools~/daemon/nexusd.version`):

```
dotnet publish Daemon~/Nexus.Daemon -c Release -o Tools~/daemon
```

Запуск для отладки: `dotnet Tools~/daemon/nexusd.dll --echo` (журнал в консоль; `--port N` — свой порт).
