package ru.invokers.patcher;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.os.Bundle;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.zip.GZIPInputStream;

public final class MainActivity extends Activity {
    private static final String PKG = "hitzone.anima.spirit.guardians";
    private static final String DIR = "/sdcard/Android/data/" + PKG + "/files/i18n";
    private static final String TARGET = DIR + "/dl_uk_UA.bin";
    private static final String ENGLISH = DIR + "/dl_en_US.bin";
    private static final String OVERLAY_URL =
            "https://raw.githubusercontent.com/Braintfy/ruslocal-invokers/main/translations/ru_RU.jsonl";

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler ui = new Handler(Looper.getMainLooper());
    private TextView log;
    private Button installButton;
    private Button restoreButton;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(20), dp(24), dp(20), dp(20));
        root.setBackgroundColor(Color.parseColor("#12141C"));

        TextView title = new TextView(this);
        title.setText("Русификатор Invokers");
        title.setTextColor(Color.parseColor("#E8C87A"));
        title.setTextSize(TypedValue.COMPLEX_UNIT_SP, 22);
        root.addView(title);

        TextView subtitle = new TextView(this);
        subtitle.setText("Любительский перевод. Не связан с HitZone Inc.");
        subtitle.setTextColor(Color.parseColor("#8A90A0"));
        subtitle.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
        subtitle.setPadding(0, dp(4), 0, dp(16));
        root.addView(subtitle);

        installButton = button("Установить перевод", "#2E7D32");
        installButton.setOnClickListener(v -> run(true));
        root.addView(installButton);

        restoreButton = button("Восстановить оригинал", "#37474F");
        restoreButton.setOnClickListener(v -> run(false));
        root.addView(restoreButton);

        Button devOptions = button("Открыть настройки телефона", "#3E4A5C");
        devOptions.setOnClickListener(v -> openDeveloperSettings());
        root.addView(devOptions);

        Button guide = button("Открыть инструкцию", "#1F3A5F");
        guide.setOnClickListener(v -> openGuide());
        root.addView(guide);

        ScrollView scroll = new ScrollView(this);
        log = new TextView(this);
        log.setTextColor(Color.parseColor("#C7CCD8"));
        log.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
        log.setPadding(0, dp(16), 0, 0);
        scroll.addView(log);
        root.addView(scroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));

        setContentView(root);
        worker.execute(this::describeEnvironment);
    }

    private Button button(String text, String color) {
        Button b = new Button(this);
        b.setText(text);
        b.setAllCaps(false);
        b.setTextColor(Color.WHITE);
        b.setBackgroundColor(Color.parseColor(color));
        b.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(52));
        lp.bottomMargin = dp(10);
        b.setLayoutParams(lp);
        return b;
    }

    private static final String GUIDE_URL = "https://github.com/Braintfy/ruslocal-invokers#readme";

    /** Opens developer options directly, falling back to device info where the section is still hidden. */
    private void openDeveloperSettings() {
        try {
            startActivity(new Intent(android.provider.Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        } catch (Exception first) {
            try {
                startActivity(new Intent(android.provider.Settings.ACTION_DEVICE_INFO_SETTINGS)
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
                say("Раздел разработчика ещё скрыт. Откройте «Сведения о ПО» и семь раз нажмите "
                        + "на «Номер сборки».");
            } catch (Exception second) {
                say("Не удалось открыть настройки: " + second.getMessage());
            }
        }
    }

    private void openGuide() {
        try {
            startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(GUIDE_URL))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        } catch (Exception e) {
            say("Не удалось открыть браузер. Адрес: " + GUIDE_URL);
        }
    }

    private int dp(int value) {
        return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value,
                getResources().getDisplayMetrics()));
    }

    private void say(String line) {
        ui.post(() -> log.append(line + "\n"));
    }

    private void busy(boolean value) {
        ui.post(() -> {
            installButton.setEnabled(!value);
            restoreButton.setEnabled(!value);
        });
    }

    private void describeEnvironment() {
        say("Проверяю устройство…");
        if (!Shell.hasRoot()) {
            say("");
            say("НУЖНЫ ПРАВА ROOT.");
            say("");
            say("Начиная с Android 11 система не разрешает приложению читать и изменять файлы другой "
                    + "программы. Обойти это на самом телефоне можно только с root.");
            say("");
            say("Без root приложение не может даже прочитать файлы игры, поэтому кнопки выше "
                    + "работать не будут.");
            say("");
            say("Установите перевод с компьютера по кабелю — это работает на любом телефоне "
                    + "и занимает пару минут:");
            say("");
            say("1. Включите режим разработчика. Раздела «Для разработчиков» в настройках "
                    + "сначала нет, он скрыт:");
            say("   Настройки → «Сведения о телефоне» → «Сведения о ПО» → семь раз подряд "
                    + "нажмите на «Номер сборки», затем введите пин-код.");
            say("   Появится сообщение «Режим разработчика включён».");
            say("");
            say("2. Настройки → «Параметры разработчика» → включите «Отладка по USB».");
            say("   Раздел лежит в самом низу списка настроек.");
            say("");
            say("3. Подключите телефон к компьютеру кабелем и разрешите отладку "
                    + "во всплывающем окне на телефоне.");
            say("");
            say("4. Запустите русификатор на компьютере и нажмите «Установить».");
            say("");
            say("Полная инструкция — по кнопке «Открыть инструкцию» выше.");
            return;
        }
        say("Root есть.");
        if (!Shell.exists(TARGET)) {
            say("");
            say("Украинский языковой файл ещё не загружен.");
            say("Откройте игру, выберите в настройках УКРАИНСКИЙ язык, дождитесь загрузки, "
                    + "полностью закройте игру и вернитесь сюда.");
            return;
        }
        try {
            say("Файл игры найден.");
            say("Текущая версия: " + Shell.run("cat '" + DIR + "/dl_uk_UA.bin.ver'").trim());
        } catch (Exception e) {
            say("Не удалось прочитать версию: " + e.getMessage());
        }
    }

    private void run(boolean install) {
        busy(true);
        worker.execute(() -> {
            try {
                if (install) doInstall(); else doRestore();
            } catch (Exception e) {
                say("");
                say("ОШИБКА: " + e.getMessage());
            } finally {
                busy(false);
            }
        });
    }

    private File backupFile() {
        return new File(getFilesDir(), "dl_uk_UA.bin.orig");
    }

    private void doInstall() throws Exception {
        File scratch = new File(getCacheDir(), "scratch.bin");
        boolean root = Shell.hasRoot();

        say("");
        say("Загружаю перевод…");
        byte[] overlayBytes = download(OVERLAY_URL);
        Overlay overlay = Overlay.read(new java.io.ByteArrayInputStream(overlayBytes));
        say("Записей в переводе: " + overlay.size());

        byte[] englishRaw;
        byte[] targetRaw;
        if (root) {
            if (!Shell.exists(TARGET)) throw new Exception("Украинский языковой файл ещё не загружен. "
                    + "Выберите украинский язык в игре и повторите.");
            say("Закрываю игру…");
            Shell.run("am force-stop " + PKG);
            say("Читаю файлы игры…");
            englishRaw = Shell.readFile(ENGLISH, scratch);
            targetRaw = Shell.readFile(TARGET, scratch);

            File backup = backupFile();
            if (!backup.exists()) {
                try (FileOutputStream out = new FileOutputStream(backup)) { out.write(targetRaw); }
                say("Оригинал сохранён внутри приложения.");
            }
        } else {
            throw new Exception("Без root приложение не может прочитать файлы игры. "
                    + "Используйте установку с компьютера по кабелю.");
        }

        say("Собираю русский файл…");
        Loc1 english = Loc1.parse(englishRaw);
        Loc1 target = Loc1.parse(targetRaw);
        if (!english.contentGuid.equals(target.contentGuid)) {
            throw new Exception("Английский и украинский пакеты из разных версий контента.");
        }
        Set<Long> ids = overlay.idsAppliedTo(english, target);
        Overlay.fillEnglishFallback(english, target, ids);
        int applied = overlay.apply(english, target);
        byte[] built = target.build();
        say("Переведено строк: " + applied);

        say("Устанавливаю…");
        Shell.writeFile(built, TARGET, scratch);
        String expected = Overlay.hex(MessageDigest.getInstance("SHA-256").digest(built));
        if (!Shell.sha256(TARGET).equalsIgnoreCase(expected)) {
            Shell.writeFile(readBackup(), TARGET, scratch);
            throw new Exception("Файл не прошёл проверку, оригинал возвращён.");
        }

        say("");
        say("ГОТОВО. Переведено строк: " + applied);
        say("");
        say("ВАЖНО: не открывайте выбор языка в настройках игры — клиент заново скачает файл "
                + "и сотрёт перевод. Язык должен остаться украинским.");
        say("После обновления игры перевод нужно установить заново.");
    }

    private byte[] readBackup() throws Exception {
        File backup = backupFile();
        byte[] data = new byte[(int) backup.length()];
        try (java.io.FileInputStream in = new java.io.FileInputStream(backup)) {
            int read = 0;
            while (read < data.length) {
                int n = in.read(data, read, data.length - read);
                if (n < 0) break;
                read += n;
            }
        }
        return data;
    }

    private void doRestore() throws Exception {
        if (!Shell.hasRoot()) throw new Exception("Для восстановления нужны права root.");
        File backup = backupFile();
        if (!backup.exists()) {
            say("Резервной копии нет.");
            say("Оригинал можно вернуть и без неё: переключите язык в настройках игры на другой "
                    + "и обратно на украинский — клиент скачает файл заново.");
            return;
        }
        say("Закрываю игру…");
        Shell.run("am force-stop " + PKG);
        Shell.writeFile(readBackup(), TARGET, new File(getCacheDir(), "scratch.bin"));
        say("Оригинальный украинский текст восстановлен.");
    }

    private byte[] download(String address) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL(address).openConnection();
        connection.setRequestProperty("Accept-Encoding", "gzip");
        connection.setConnectTimeout(20000);
        connection.setReadTimeout(120000);
        try {
            if (connection.getResponseCode() != 200) {
                throw new Exception("Сервер ответил " + connection.getResponseCode());
            }
            InputStream stream = connection.getInputStream();
            if ("gzip".equalsIgnoreCase(connection.getContentEncoding())) {
                stream = new GZIPInputStream(stream);
            }
            java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream(1 << 22);
            byte[] buffer = new byte[1 << 16];
            int n;
            while ((n = stream.read(buffer)) > 0) out.write(buffer, 0, n);
            return out.toByteArray();
        } finally {
            connection.disconnect();
        }
    }
}
