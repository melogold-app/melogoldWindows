# Трек закрыт в стране: понятная ошибка со страной YouTube

Статус: сделано

Те же задания: `melogoldAndroid/tasks/0008-geo-blocked-tracks.md` (сделано, Android 0.1.8),
`melogoldiOSmacOS/tasks/0010-geo-blocked-tracks.md`, `melogoldLinux/tasks/0001-geo-blocked-tracks.md`.

## 1. Что нужно пользователю

Пользователь в России: Saba «Photosynthesis» не играет. С VPN через Хельсинки тоже: Google считает этот адрес
российским (Gemini там тоже не работает). Правообладатель открыл трек в 122 странах, России среди них нет, а клиент
показывал общую ошибку потока.

Пользователь: «нужно сделать логирование этой ошибки, чтобы человек понимал, что к чему, причём сразу на всех
платформах».

## 2. Решение (одинаково на всех клиентах)

Подробно, с проверенными ответами YouTube — `melogoldAndroid/tasks/0008-geo-blocked-tracks.md` (Android сделал это в 0.1.8).

Когда поток не получен, клиент **один раз** спрашивает YouTube, почему:

1. **Запрос:** `POST https://youtubei.googleapis.com/youtubei/v1/player`, клиент `WEB`
   (`clientName: WEB`, свежая `clientVersion` вида `2.2026…`), тело `{context, videoId}`, ответ не проверяется на
   пригодность. Таймаут 8 с. Клиенты потока (IOS, ANDROID_VR…) для этого не годятся: только `WEB`/`WEB_REMIX`
   присылают список стран, даже когда трек не играет.
2. **Из ответа:**
   - `playabilityStatus.status` и `.reason`;
   - `microformat.playerMicroformatRenderer.availableCountries` (у `WEB_REMIX` — `microformatDataRenderer`) — страны,
     где трек открыт;
   - `responseContext.visitorData` — страна, в которой YouTube видит устройство: base64 url-safe (`%3D` → `=`,
     `-` → `+`, `_` → `/`), это protobuf; поле 6 — вложенное сообщение, в его поле 1 — код страны из двух букв.
     Образцы: `CgtRTWpHWl9XellHZyjBhN7VBjIoCgJOTBIiEh4SHAsMDg8QERITFBUWFxgZGhscHR4fICEiIyQlJicgYw%3D%3D` → `NL`;
     `Cgs4bmZBZU9NZ2hGVSiLhd7VBjIOCgJERRII…` → `DE` (полная строка — в `VisitorDataTest.kt` Android). Разбор — простой
     обход полей protobuf (varint-ключ, тип 0/1/2/5), без библиотек.
3. **Классификация** (по порядку):
   - страна известна, список не пуст, страны в списке нет → **закрыт в стране** (страна + число стран);
   - в сообщении клиента потока или `reason` есть `available in your country`, `not made this video available in
     your country`, `blocked it in your country` → **закрыт в стране** (без числа);
   - `confirm your age`, `age-restricted`, `inappropriate for some users` → **возраст**;
   - `Private video`, `has been removed`, `account associated with this video has been terminated`,
     `no longer available` → **удалено или закрыто**;
   - иначе — прежняя общая ошибка.
4. **Текст в карточке ошибки** (рядом «Повторить · Пропустить · Другие версии»):

   | Случай | Русский | English |
   |---|---|---|
   | страна и число | Недоступно в стране «Россия»: YouTube считает, что вы там, а правообладатель открыл трек в 122 других странах. С VPN выберите сервер другой страны: некоторые серверы YouTube тоже относит к стране «Россия». | Unavailable in Russia: YouTube places you there, and the rights holder opened this track in 122 other countries. With a VPN, pick a server in another country: YouTube counts some VPN servers as Russia too. |
   | только страна | Недоступно в стране «Россия»: YouTube считает, что вы там, а правообладатель закрыл трек для этой страны. С VPN выберите сервер другой страны: некоторые серверы YouTube тоже относит к стране «Россия». | Unavailable in Russia: YouTube places you there, and the rights holder closed this track for it. With a VPN, pick a server in another country: YouTube counts some VPN servers as Russia too. |
   | без страны | Недоступно в вашей стране | Unavailable in your country |

   - Страна — полное название по коду на языке интерфейса («Россия», «Russia»); неизвестный код — сам код.
   - Число — с формами множественного числа: «в 121 другой стране», «в 122 других странах».
5. **Журнал:** одна строка на отказ — id трека, итог, статус и причина YouTube, страна, число стран, последнее
   сообщение клиента потока. Она попадает в журнал и в отчёт «Диагностики».


## 3. Windows

- `Melogold.InnerTube/Player.cs` уже читает `playabilityStatus.status` и `.reason`. Добавь отдельный запрос-диагноз
  клиентом `WEB` (как `YouTubeWeb` в поиске) и разбор `visitorData` → страна, `availableCountries` → список.
- `Melogold.Playback/StreamResolver.cs`: `StreamErrorKind.Geo` уже есть. Когда IOS и ANDROID_VR не дали поток,
  спроси диагноз и неси в `StreamException` страну и число стран; классификация — по п. 2.3.
- Текст — в ресурсах ru/en (`.resw`) с формами множественного числа; журнал — в `%LOCALAPPDATA%\Melogold\logs`.

## 4. Проверка

- **Юнит-тесты:**
  - страна из двух образцов `visitorData` (NL, DE) и `null` для мусора;
  - классификация: закрыт в стране (RU вне списка из 122) → страна и число; открыт → общая ошибка; фразы о стране,
    возрасте, удалении;
  - тексты на двух языках, формы 121/122.
- **Вручную:** Saba «Photosynthesis» (`cYKAr38pZcY`) с российского адреса (или через VPN, который YouTube считает
  российским) показывает «Недоступно в стране «Россия»… в 122 других странах…». Из другой страны трек играет как
  обычно.

## 5. Как сделано (Windows 0.1.10)

- `Melogold.InnerTube/Playability.cs`: запрос `player` клиентом `WEB` на `youtubei.googleapis.com` без нашего
  `visitorData` и по-английски (`hl=en`: причину сверяют с английскими фразами), таймаут 8 с; разбор `visitorData` →
  страна, `availableCountries`.
- `StreamResolver.ExplainAsync` / `Diagnose`: когда поток не получен (кроме сети и таймаута), один запрос-диагноз и
  классификация по п. 2.3; строка в журнал на каждый отказ.
- Текст — `PlayerViewModel.ErrorText(PlayerError)`; название страны — ICU Windows на языке интерфейса Melogold
  (`CountryNames`: `RegionInfo.DisplayName` всегда на языке Windows). Длинный текст в панели плеера — целиком в подсказке.
- Проверено: юнит-тесты `GeoBlockTests` (NL, DE, мусор; RU вне 122 → страна и число; открыт → прежняя ошибка; фразы;
  тексты ru/en, 121/122; названия стран) и живые `PlayabilityTellsCountryAndOpenCountries`,
  `ClosedTrackFailsWithCountry` (`MELOGOLD_LIVE=1`). Отсюда YouTube видит Нидерланды: трек открыт и играет. Российский
  адрес проверен только юнит-тестом; снимок карточки не делался — пользователь в это время играл.
