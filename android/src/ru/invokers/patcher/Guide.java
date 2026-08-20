package ru.invokers.patcher;

/**
 * The three installation routes, written out as numbered steps.
 *
 * They live here as plain data so the wording — the part players actually depend on — can be read
 * and corrected without touching any of the screen-building code.
 */
final class Guide {
    /** What the button under a step does, if it has one. */
    enum Action { NONE, DEVELOPER_SETTINGS, DOWNLOAD_PAGE, COPY_LINK, MANUAL }

    static final class Step {
        final String text;
        final String button;
        final Action action;

        Step(String text) { this(text, null, Action.NONE); }

        Step(String text, String button, Action action) {
            this.text = text;
            this.button = button;
            this.action = action;
        }
    }

    static final String DOWNLOAD_URL = "https://github.com/Braintfy/ruslocal-invokers/releases/latest";
    static final String MANUAL_URL = "https://github.com/Braintfy/ruslocal-invokers#readme";

    static final String DEVELOPER_MODE =
            "Откройте «Настройки» → «Сведения о телефоне» → «Сведения о ПО» и семь раз подряд "
            + "нажмите на строку «Номер сборки». Телефон попросит пин-код и напишет "
            + "«Режим разработчика включён». Раньше этого пункта в настройках просто нет — он скрытый.";

    static final Step[] ROOT = {
        new Step("Этот способ работает, только если на телефоне есть root (Magisk, KernelSU). "
                + "Если вы не знаете, что это такое — значит root у вас нет: возвращайтесь и "
                + "выбирайте способ с компьютером."),
        new Step("Откройте игру, зайдите в настройки и выберите УКРАИНСКИЙ язык. Дождитесь, пока "
                + "игра докачает текст, и полностью закройте её.\n\n"
                + "Русский текст подставляется в украинскую ячейку — она единственная кириллическая."),
        new Step("Вернитесь сюда и нажмите «Установить перевод». Телефон спросит разрешение "
                + "суперпользователя — разрешите. Дальше всё произойдёт само."),
    };

    static final Step[] WIFI = {
        new Step("Скачайте на компьютер архив «Rusifikator-Invokers-PC.zip» — это маленькая "
                + "программа-помощник. Больше ничего искать и скачивать не придётся: всё "
                + "остальное она возьмёт сама.",
                "Открыть страницу загрузки", Action.DOWNLOAD_PAGE),
        new Step("Распакуйте архив в любую папку и запустите файл:\n\n"
                + "  •  Windows — «Русификатор-Android.cmd»\n"
                + "  •  macOS — «Русификатор-Android.command»\n\n"
                + "Откроется чёрное окно с текстом. Это нормально, закрывать его не нужно."),
        new Step("Теперь включите на телефоне режим разработчика.\n\n" + DEVELOPER_MODE,
                "Открыть настройки телефона", Action.DEVELOPER_SETTINGS),
        new Step("«Настройки» → «Параметры разработчика» (пункт в самом низу списка) → включите "
                + "«Отладка по Wi-Fi».\n\n"
                + "Телефон и компьютер должны быть в одной сети Wi-Fi.",
                "Открыть параметры разработчика", Action.DEVELOPER_SETTINGS),
        new Step("В программе на компьютере выберите пункт 2 — «по Wi-Fi». Она попросит адрес "
                + "и код."),
        new Step("На телефоне зайдите в «Отладка по Wi-Fi» → «Подключение устройства с помощью "
                + "кода сопряжения». Появятся шестизначный код и адрес вида 192.168.1.5:37105 — "
                + "перепишите их в программу на компьютере.\n\n"
                + "Держите этот экран открытым: код живёт около минуты."),
        new Step("Откройте игру, выберите УКРАИНСКИЙ язык, дождитесь загрузки текста и полностью "
                + "закройте игру."),
        new Step("Нажмите в программе «Установить перевод». Она сама всё сделает: экран телефона "
                + "на секунду моргнёт этим приложением — так и должно быть."),
    };

    static final Step[] CABLE = {
        new Step("Скачайте на компьютер архив «Rusifikator-Invokers-PC.zip». Это всё, что нужно "
                + "скачивать: программа сама возьмёт с сайта Google утилиту для связи с телефоном.",
                "Открыть страницу загрузки", Action.DOWNLOAD_PAGE),
        new Step("Распакуйте архив в любую папку и запустите файл:\n\n"
                + "  •  Windows — «Русификатор-Android.cmd»\n"
                + "  •  macOS — «Русификатор-Android.command»"),
        new Step("Включите на телефоне режим разработчика.\n\n" + DEVELOPER_MODE,
                "Открыть настройки телефона", Action.DEVELOPER_SETTINGS),
        new Step("«Настройки» → «Параметры разработчика» → включите «Отладка по USB».",
                "Открыть параметры разработчика", Action.DEVELOPER_SETTINGS),
        new Step("Подключите телефон к компьютеру кабелем. На телефоне появится окно "
                + "«Разрешить отладку по USB?» — поставьте галочку «Всегда разрешать» и нажмите "
                + "«Разрешить».\n\n"
                + "Окна нет? Отключите и снова подключите кабель. Кабель должен быть для передачи "
                + "данных, а не только для зарядки."),
        new Step("Samsung: сначала выключите «Auto Blocker» в «Настройки» → «Безопасность и "
                + "конфиденциальность» — он по умолчанию не даёт подключиться.\n\n"
                + "Xiaomi и Redmi: в параметрах разработчика включите ещё и «Отладка по USB "
                + "(настройки безопасности)»."),
        new Step("Откройте игру, выберите УКРАИНСКИЙ язык, дождитесь загрузки текста и полностью "
                + "закройте игру."),
        new Step("В программе на компьютере выберите пункт 1 — «по кабелю» — и нажмите "
                + "«Установить перевод». Дальше всё само."),
    };
}
