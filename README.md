<p align="center">
  <img src=".github/melogold-icon.png" width="128" height="128" alt="Melogold">
</p>

<h1 align="center">Melogold для Windows</h1>

<p align="center">Клиент <a href="https://github.com/melogold-app/melogoldAndroid">Melogold</a> для Windows: музыка из YouTube Music и обычного YouTube с общими Избранным, плейлистами и сохранёнными альбомами на всех устройствах.</p>

## Что умеет

- Поиск по YouTube Music и YouTube, «Тренды» и «Новое», альбомы, исполнители, каналы, плейлисты.
- Библиотека: Избранное, свои плейлисты, История, сохранённые альбомы и исполнители.
- Синхронизация через [сервер Melogold](https://github.com/melogold-app/melogoldServer): вход, регистрация с кодом восстановления, устройства. Без аккаунта всё работает на этом компьютере.
- «Сейчас играет» с синхронным текстом (YouTube Music, LRCLIB, KuGou), поиск другого текста и импорт `.lrc`/`.ttml`.
- Очередь, таймер сна, мини-плеер, медиаклавиши, системная плашка и кнопки на миниатюре в панели задач.
- Русский и английский интерфейс, светлая и тёмная тема, управление с клавиатуры, экранный диктор.

Windows 10 2004 и новее, x64 и ARM64. Звук каждый клиент берёт с YouTube сам; сервер хранит только метаданные.

## Установка

Установщик — в [Releases](https://github.com/melogold-app/melogoldWindows/releases): `Melogold-<версия>-x64-setup.exe`
или `-arm64-`. Права администратора не нужны: программа ставится в `%LOCALAPPDATA%\Programs\Melogold`, данные лежат
в `%LOCALAPPDATA%\Melogold` и при удалении остаются, если не выбрать иное. Дальше Melogold обновляется сам.

Подписи кода пока нет: при первой установке SmartScreen предупредит — «Подробнее» → «Выполнить в любом случае».

## Сборка

Нужны .NET SDK 10 и Windows App SDK (подтягивается NuGet).

```powershell
dotnet build src/Melogold.App/Melogold.App.csproj -p:Platform=x64
dotnet test tests/Melogold.Tests -p:Platform=x64
```

Живые тесты (YouTube, воспроизведение, тексты, синхронизация на сервере) — с `MELOGOLD_LIVE=1`.

## Выпуск

Версия — одна, `MelogoldVersion` в `Directory.Build.props`; «Что нового» — `release-notes/<версия>.ru.md` и `.en.md`.

```powershell
powershell -File scripts/release.ps1            # обе архитектуры, установщики Inno Setup и update.json в dist/
powershell -File scripts/release.ps1 -Publish   # и релиз vX.Y.Z на GitHub
```

Приложение читает `releases/latest/download/update.json`, скачивает установщик своей архитектуры, проверяет размер
и SHA-256 и ставит его тихо.

## Лицензия

[GPL-3.0](./LICENSE).

## Остальные части Melogold

| Платформа | Репозиторий |
|---|---|
| Android | [melogoldAndroid](https://github.com/melogold-app/melogoldAndroid) |
| Сервер | [melogoldServer](https://github.com/melogold-app/melogoldServer) |
| Windows | [melogoldWindows](https://github.com/melogold-app/melogoldWindows) |
| Linux | [melogoldLinux](https://github.com/melogold-app/melogoldLinux) |
| iOS и macOS | [melogoldiOSmacOS](https://github.com/melogold-app/melogoldiOSmacOS) |
