# Omnissiah Breakwall

Переносимый лаунчер для Windows 10/11 x64 с профилями zapret для Warhammer 40,000: Darktide, Discord и YouTube и автоматическим выбором клиента Cloudflare WARP. Пошаговый запуск описан в Инструкция.txt.

## Стратегии

Если уже запущен другой zapret, завершите его через его собственный интерфейс перед включением этой сборки. Для проверки входа в Darktide выберите **Darktide UDP · основной**, оставьте WARP выключенным и попробуйте войти в «Моурнингстар». Если ошибка 2004, 2014, 3001 или 3013 повторится, выключите фильтр, выберите **Darktide UDP · усиленный + TLS** и повторите вход. Усиленный профиль посылает больше пробных пакетов в начале игровой сессии и использует альтернативную обработку TLS.

Выберите стратегии Darktide, Discord и YouTube в трёх списках и нажмите **Включить**. По умолчанию у каждого сервиса выбран основной вариант. Для смены любого варианта выключите фильтр, выберите нужный пункт и снова включите его.

| Вариант | Discord | YouTube |
| --- | --- | --- |
| Основной | Правила прежней сборки. Обработка TLS зависит от выбранного профиля Darktide | FAKE TLS AUTO ALT2 |
| TLS со смещением | FAKE TLS AUTO ALT2 | Доступен как основной |
| TLS с метками времени | ALT10 | ALT10 |
| Подмена имени хоста | ALT3 | ALT3 |
| TLS с перекрытием | ALT11 | ALT11 |
| Простой TLS | SIMPLE FAKE ALT2 | SIMPLE FAKE ALT2 |
| Разбиение по SNI | ALT7 | ALT7 |

## Cloudflare WARP

Встроенный WARP использует существующую регистрацию Omnissiah Darktide. Путь к локальным данным сохранён для совместимости.

Поставьте галочку **Подключить WARP** и нажмите **Включить**. Лаунчер запустит выбранные стратегии zapret и автоматически выберет клиент WARP. Оранжевая обводка показывает, что галочка выбрана. Результат подключения отображается под настройками.

- Если установлен официальный клиент Cloudflare в стандартном каталоге Program Files, лаунчер использует уже активное подключение или запрашивает новое. Ошибка клиента доступна в «Подробностях».
- При отсутствии официального клиента запускается встроенный WARP. Для регистрации и подключения нужен интернет. При первой регистрации подтвердите условия Cloudflare. Туннель пробует MASQUE по QUIC, затем по TCP, и пропускает игровой UDP. Встроенный клиент использует [usque 4.2.1](https://github.com/Diniboy1123/usque/releases/tag/v4.2.1) и [Wintun 0.14.1](https://www.wintun.net/).

При обнаружении другого активного VPN, включая AmneziaVPN, лаунчер пропускает запуск нового туннеля. Кнопка **Выключить** и обычное закрытие окна останавливают фильтр и WARP, если его запустил лаунчер. Уже активное подключение официального клиента продолжает работать.

Регистрация встроенного WARP хранится в `%LOCALAPPDATA%\Omnissiah Darktide Local\warp-masque.dpapi`. Windows шифрует приватный ключ, токен, ID регистрации и назначенные туннелю IP для текущей учётной записи. При запуске usque временный JSON создаётся в подпапке `warp-runtime` и удаляется после попытки подключения. Остаток после аварийного завершения очищается при следующем запуске встроенного WARP. Если локального файла ещё нет, прежняя регистрация из «Документов» или старого каталога AppData переносится при следующем подключении. Официальный клиент хранит регистрацию в собственном каталоге.

При регистрации usque использует условную модель PC и сгенерированный идентификатор. Cloudflare видит исходный IP сетевого соединения. Папку программы можно передавать отдельно от данных регистрации.

## Профили

При ручном редактировании списков указывайте по одному домену или IP-адресу/подсети в формате CIDR на строку в соответствующем файле. Доменные записи охватывают поддомены. Чтобы применить изменения, перезапустите фильтр.

- Выбранная стратегия YouTube применяет отдельные правила TLS и QUIC к сайту, видеосерверам и CDN превью из `profiles/youtube-core.txt`. Список `profiles/youtube.txt` включает также региональные домены Google и домены сервисов из списка v2fly. Все записи из `list-google.txt` Flowseal покрыты, включая `stable.dl2.discordapp.net`, который обрабатывается профилем Discord. [Список Flowseal](https://github.com/Flowseal/zapret-discord-youtube/blob/2401b7a13837139b0265632464ba563dddbf80d8/lists/list-google.txt) · [Список Google и YouTube](https://github.com/v2fly/domain-list-community/blob/777d056afe68b2643c1ef15f80e7bde3dfa149b1/data/google)
- `profiles/darktide.txt` содержит доменные зоны игровых сервисов Atoma, официальные сайты Fatshark и адрес Vivox из журналов игры. Поддомены учитываются автоматически. Профиль обрабатывает HTTP, TLS и QUIC к этим доменам.
- `profiles/base.args` содержит отдельное правило для начала сессии Darktide на UDP-портах `35500–35650`. Нижняя граница включает порт `35500`, который игра выбрала при входе в лобби. Правило применяется к первым пакетам на этих портах при смене IP игрового сервера. Если провайдер блокирует сам IP или весь UDP-маршрут, может понадобиться WARP. [Руководство Steam](https://steamcommunity.com/sharedfiles/filedetails/?id=3759171917)
- `profiles/discord.txt` охватывает домены Discord из Flowseal, включая клиент, CDN и вложения, а также Klipy для GIF. Из v2fly добавлены `discord.tools` и точный адрес поддержки `hammerandchisel.ssl.zendesk.com`. Голосовой UDP обрабатывается по диапазонам портов и сигнатурам Discord/STUN из Flowseal. [Flowseal](https://github.com/Flowseal/zapret-discord-youtube/blob/2401b7a13837139b0265632464ba563dddbf80d8/lists/list-general.txt) · [v2fly](https://github.com/v2fly/domain-list-community/blob/777d056afe68b2643c1ef15f80e7bde3dfa149b1/data/discord)
- `profiles/warp.txt` содержит домены служб клиента, Cloudflare Access, Gateway, DNS 1.1.1.1 и сайта WARP. `profiles/warp-api.txt` содержит точные адреса API регистрации для TLS. Списки `warp-masque.txt` и `warp-wireguard.txt` задают подсети для туннеля. Эти правила загружаются с галочкой WARP. [Cloudflare](https://developers.cloudflare.com/cloudflare-one/team-and-resources/devices/cloudflare-one-client/deployment/firewall/) · [Flowseal](https://github.com/Flowseal/zapret-discord-youtube/blob/main/lists/list-general.txt) · [Руководство Steam](https://steamcommunity.com/sharedfiles/filedetails/?id=3695068003)

## Компоненты и исходники

Лаунчер написан на C# и Windows Forms. `src/build.ps1` собирает EXE штатным компилятором .NET Framework в Windows x64. Фильтр — winws v72.9 с WinDivert и Cygwin из [релиза Flowseal 1.10.4](https://github.com/Flowseal/zapret-discord-youtube/releases/tag/1.10.4). SHA-256 использованного архива `291af673bd0238506f9548e0e94ca0abf244000f4c9a06960544fb01c5f97b34`.

- [Исходники winws](https://github.com/bol-van/zapret/tree/c849e55ef0f1c244206f5a05ff7b1ab41a3824ee)
- [Исходники и лицензия WinDivert](https://github.com/basil00/WinDivert/tree/v2.2.2)
- [Исходники и лицензирование Cygwin](https://cygwin.com/licensing.html)
- [Информация Fatshark о блокировках](https://support.fatshark.se/hc/en-us/articles/28867379991069-Playing-From-Russia-and-Experiencing-Errors-on-Loading-in-to-the-Mourningstar)
