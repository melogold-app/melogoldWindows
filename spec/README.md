# Векторы правил

Копии общих файлов правил: те же случаи прогоняют сервер, Android и этот клиент (`tests/Melogold.Tests/SpecVectorTests.cs`). Случаи не правятся здесь — только копируются заново из источника.

| Файл | Правило | Источник |
|---|---|---|
| `hwid.vectors.json` | идентификатор устройства, API §1.6 | `melogoldServer/spec`, коммит `c2a42a3` |
| `pow.vectors.json` | доказательство работы при регистрации, API §4.3 | `melogoldServer/spec`, коммит `c2a42a3` |
| `server-address.vectors.json` | адрес сервера, API §7.1 | `melogoldAndroid/docs/spec`, коммит `4ae2183` |
| `youtube-links.vectors.json` | ссылки YouTube, REWRITE §4.9 | `melogoldAndroid/docs/spec`, коммит `4ae2183` |
| `title-cleaner.vectors.json` | очистка названий, REWRITE §4.10.8 | `melogoldAndroid/docs/spec`, коммит `4ae2183` |
| `lyrics.vectors.json`, `lyrics.md` | модель текстов, LRC и TTML | `melogoldAndroid/docs/spec`, коммит `4ae2183` |

Обновить: скопировать файлы из источника, поправить коммит в таблице, прогнать `dotnet test`.
