package ru.invokers.patcher;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.io.File;
import java.io.FileOutputStream;
import java.security.MessageDigest;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * The installer's single screen.
 *
 * It is deliberately built as a wizard rather than a control panel: the phone decides which of the
 * three routes it is actually capable of, and shows that one as a single obvious button. Everything
 * else is one tap away but never in the way.
 */
public final class MainActivity extends Activity {
    static final String GAME = "hitzone.anima.spirit.guardians";
    private static final String GAME_DIR = "/sdcard/Android/data/" + GAME + "/files/i18n";
    private static final String GAME_TARGET = GAME_DIR + "/dl_uk_UA.bin";
    private static final String GAME_ENGLISH = GAME_DIR + "/dl_en_US.bin";

    /** The action a computer uses to make the app compose the file and quit. */
    static final String ACTION_BRIDGE = "ru.invokers.patcher.BRIDGE";

    private static final int BG = 0xFF12141C;
    private static final int CARD = 0xFF1B1F2B;
    private static final int GOLD = 0xFFE8C87A;
    private static final int TEXT = 0xFFC7CCD8;
    private static final int MUTED = 0xFF8A90A0;
    private static final int GREEN = 0xFF2E7D32;
    private static final int AMBER = 0xFFC98A16;
    private static final int BLUE = 0xFF2B4C7E;
    private static final int SLATE = 0xFF3A4354;
    private static final int RED = 0xFFB3382F;

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler ui = new Handler(Looper.getMainLooper());

    private TextView log;
    private TextView statusTitle;
    private TextView statusBody;
    private LinearLayout actions;
    private View statusCard;
    private boolean onHome = true;
    private volatile boolean working;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        // A computer may kick this off while the phone is face down on the table, so the screen is
        // woken and held on: the work happens in the activity, and a sleeping phone would stall it.
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        if (android.os.Build.VERSION.SDK_INT >= 27) {
            setShowWhenLocked(true);
            setTurnScreenOn(true);
        }
        showHome();
        if (ACTION_BRIDGE.equals(getIntent() == null ? null : getIntent().getAction())) {
            say("Компьютер прислал файлы игры. Собираю перевод…");
            worker.execute(this::bridgeBuild);
        } else {
            worker.execute(this::refresh);
        }
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        if (ACTION_BRIDGE.equals(intent.getAction())) {
            if (!onHome) showHome();
            say("Компьютер прислал файлы игры. Собираю перевод…");
            worker.execute(this::bridgeBuild);
        }
    }

    @Override
    public void onBackPressed() {
        if (onHome) super.onBackPressed(); else showHome();
    }

    // ---------- home ----------

    private void showHome() {
        onHome = true;
        LinearLayout page = column();

        page.addView(heading("Русификатор Invokers"));
        page.addView(caption("Любительский перевод. Проект не связан с HitZone Inc."));

        statusCard = null;
        LinearLayout card = card(SLATE);
        statusTitle = new TextView(this);
        statusTitle.setTextColor(Color.WHITE);
        statusTitle.setTextSize(TypedValue.COMPLEX_UNIT_SP, 17);
        statusTitle.setText("Проверяю телефон…");
        card.addView(statusTitle);
        statusBody = new TextView(this);
        statusBody.setTextColor(TEXT);
        statusBody.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        statusBody.setPadding(0, dp(6), 0, 0);
        statusBody.setText("Секунду.");
        card.addView(statusBody);
        statusCard = card;
        page.addView(card);

        actions = new LinearLayout(this);
        actions.setOrientation(LinearLayout.VERTICAL);
        page.addView(actions);

        page.addView(warning("Одно правило\n\nНе открывайте выбор языка в настройках игры. При "
                + "выборе любого языка игра заново скачивает текст с сервера и стирает перевод. "
                + "Язык должен остаться украинским."));

        page.addView(flat("Инструкция на сайте проекта", () -> open(Guide.MANUAL_URL)));
        page.addView(flat("Проверить ещё раз", () -> worker.execute(this::refresh)));

        log = new TextView(this);
        log.setTextColor(MUTED);
        log.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
        log.setPadding(0, dp(14), 0, 0);
        page.addView(log);

        setContentView(scroll(page));
    }

    /** Works out what this phone can actually do and turns that into one recommended action. */
    private void refresh() {
        String game = gameVersion();
        boolean root = Shell.hasRoot();
        boolean fromComputer = Bridge.inputReady(this);

        if (fromComputer) {
            state(BLUE, "Файлы от компьютера получены",
                    "Компьютер уже передал файлы игры. Нажмите кнопку — перевод соберётся здесь, "
                    + "а компьютер поставит его в игру.",
                    action("Собрать перевод", GREEN, () -> run(this::bridgeBuild)));
            return;
        }

        if (root) {
            String extra = game == null
                    ? "Игра на этом телефоне не найдена. Установите её и вернитесь сюда."
                    : "Игра найдена, версия " + game + ". Выберите в игре украинский язык, "
                      + "закройте её и нажмите кнопку.";
            state(GREEN, "Есть root — всё сделаем прямо здесь", extra,
                    action("Установить перевод", GREEN, () -> run(this::rootInstall)),
                    action("Восстановить оригинал", SLATE, () -> run(this::rootRestore)));
            return;
        }

        state(AMBER, "Нужен компьютер — это минут пять",
                (game == null ? "Игра на этом телефоне не найдена. " : "Игра найдена, версия " + game + ". ")
                + "Начиная с Android 11 приложение не может само менять файлы другой программы — "
                + "система это запрещает, и обойти запрет на самом телефоне можно только с root. "
                + "Поэтому перевод ставится с компьютера: телефон при этом собирает файл сам, "
                + "компьютер только передаёт его игре.",
                action("Как это сделать без проводов", BLUE, () -> showRoute("Без проводов, по Wi-Fi", Guide.WIFI)),
                action("Как это сделать по кабелю", SLATE, () -> showRoute("По кабелю", Guide.CABLE)),
                action("У меня есть root", SLATE, () -> showRoute("Только телефон, с root", Guide.ROOT)));
    }

    private void state(int accent, String title, String body, View... buttons) {
        ui.post(() -> {
            if (!onHome) return;
            statusCard.setBackground(rounded(CARD, accent));
            statusTitle.setText(title);
            statusTitle.setTextColor(accent == AMBER ? GOLD : Color.WHITE);
            statusBody.setText(body);
            actions.removeAllViews();
            for (View b : buttons) actions.addView(b);
        });
    }

    private View action(String text, int color, Runnable onClick) {
        return button(text, color, onClick);
    }

    // ---------- route screens ----------

    private void showRoute(String title, Guide.Step[] steps) {
        onHome = false;
        LinearLayout page = column();
        page.addView(heading(title));
        page.addView(caption("Делайте по порядку. Пропущенный шаг — самая частая причина, "
                + "почему ничего не получается."));

        int number = 1;
        for (Guide.Step step : steps) {
            LinearLayout card = card(SLATE);

            TextView index = new TextView(this);
            index.setText("Шаг " + number++);
            index.setTextColor(GOLD);
            index.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
            card.addView(index);

            TextView body = new TextView(this);
            body.setText(step.text);
            body.setTextColor(TEXT);
            body.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
            body.setPadding(0, dp(6), 0, 0);
            body.setLineSpacing(dp(3), 1f);
            card.addView(body);

            if (step.button != null) {
                Button b = (Button) button(step.button, BLUE, () -> perform(step.action));
                ((LinearLayout.LayoutParams) b.getLayoutParams()).topMargin = dp(12);
                ((LinearLayout.LayoutParams) b.getLayoutParams()).bottomMargin = 0;
                card.addView(b);
            }
            page.addView(card);
        }

        page.addView(flat("Скопировать ссылку на программу для компьютера",
                () -> perform(Guide.Action.COPY_LINK)));
        page.addView(button("Назад", SLATE, this::showHome));
        setContentView(scroll(page));
    }

    private void perform(Guide.Action action) {
        switch (action) {
            case DEVELOPER_SETTINGS: openDeveloperSettings(); break;
            case DOWNLOAD_PAGE: open(Guide.DOWNLOAD_URL); break;
            case COPY_LINK: copy(Guide.DOWNLOAD_URL); break;
            default: break;
        }
    }

    /** Developer options, falling back to the screen where the section is unlocked. */
    private void openDeveloperSettings() {
        try {
            startActivity(new Intent(android.provider.Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        } catch (Exception first) {
            try {
                startActivity(new Intent(android.provider.Settings.ACTION_DEVICE_INFO_SETTINGS)
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
                toast("Раздел разработчика ещё закрыт: семь раз нажмите «Номер сборки».");
            } catch (Exception second) {
                toast("Не удалось открыть настройки, откройте их вручную.");
            }
        }
    }

    private void open(String address) {
        try {
            startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(address))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        } catch (Exception e) {
            copy(address);
        }
    }

    private void copy(String value) {
        ClipboardManager clipboard = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
        if (clipboard != null) clipboard.setPrimaryClip(ClipData.newPlainText("InvokersRu", value));
        toast("Ссылка скопирована: " + value);
    }

    // ---------- work ----------

    private void run(Runnable job) {
        if (working) return;
        worker.execute(() -> {
            working = true;
            try {
                job.run();
            } finally {
                working = false;
            }
        });
    }

    /** Composes the file from what a computer handed over, and leaves the result for it to pick up. */
    private void bridgeBuild() {
        Bridge.clearStatus(this);
        try {
            if (!Bridge.inputReady(this)) {
                throw new IllegalStateException("Компьютер не передал файлы игры.");
            }
            say("Читаю файлы игры…");
            byte[] english = Bridge.read(Bridge.in(this, Bridge.ENGLISH));
            byte[] target = Bridge.read(Bridge.in(this, Bridge.TARGET));

            say("Открываю перевод…");
            Overlay overlay = Bridge.catalog(this);
            say("Записей в переводе: " + overlay.size());

            say("Собираю русский файл…");
            Bridge.Built built = Bridge.compose(english, target, overlay);
            Bridge.write(Bridge.out(this, Bridge.TARGET), built.bytes);
            Bridge.status(this, "OK " + built.applied);

            Bridge.in(this, Bridge.ENGLISH).delete();
            Bridge.in(this, Bridge.TARGET).delete();

            say("");
            say("Готово: переведено строк " + built.applied + ".");
            say("Компьютер сейчас поставит файл в игру — смотрите в его окно.");
            ui.post(() -> state(GREEN, "Файл собран",
                    "Переведено строк: " + built.applied + ". Дальше работает компьютер — "
                    + "закончите установку в его окне.",
                    action("Инструкция", SLATE, () -> open(Guide.MANUAL_URL))));
        } catch (Exception e) {
            String message = e.getMessage() == null ? e.toString() : e.getMessage();
            Bridge.status(this, "ERR " + message.replace('\n', ' '));
            say("");
            say("ОШИБКА: " + message);
            ui.post(() -> state(RED, "Не получилось", message,
                    action("Попробовать ещё раз", SLATE, () -> run(this::bridgeBuild))));
        }
    }

    private File backupFile() {
        return new File(getFilesDir(), "dl_uk_UA.bin.orig");
    }

    private void rootInstall() {
        try {
            if (!Shell.hasRoot()) throw new IllegalStateException("Root-доступ не выдан.");
            if (!Shell.exists(GAME_TARGET)) {
                throw new IllegalStateException("Украинский языковой файл ещё не загружен. "
                        + "Выберите в игре украинский язык, дождитесь загрузки и закройте игру.");
            }
            File scratch = new File(getCacheDir(), "scratch.bin");

            say("");
            say("Открываю перевод…");
            Overlay overlay = Bridge.catalog(this);
            say("Записей в переводе: " + overlay.size());

            say("Закрываю игру…");
            Shell.run("am force-stop " + GAME);
            say("Читаю файлы игры…");
            byte[] english = Shell.readFile(GAME_ENGLISH, scratch);
            byte[] target = Shell.readFile(GAME_TARGET, scratch);

            File backup = backupFile();
            if (!backup.exists()) {
                try (FileOutputStream out = new FileOutputStream(backup)) { out.write(target); }
                say("Оригинал сохранён внутри приложения.");
            }

            say("Собираю русский файл…");
            Bridge.Built built = Bridge.compose(english, target, overlay);

            say("Устанавливаю…");
            Shell.writeFile(built.bytes, GAME_TARGET, scratch);
            String expected = Overlay.hex(MessageDigest.getInstance("SHA-256").digest(built.bytes));
            if (!Shell.sha256(GAME_TARGET).equalsIgnoreCase(expected)) {
                Shell.writeFile(Bridge.read(backup), GAME_TARGET, scratch);
                throw new IllegalStateException("Файл не прошёл проверку, оригинал возвращён.");
            }

            say("");
            say("Готово: переведено строк " + built.applied + ".");
            ui.post(() -> state(GREEN, "Перевод установлен",
                    "Переведено строк: " + built.applied + ". Запускайте игру.\n\n"
                    + "Не открывайте выбор языка в настройках игры — перевод сотрётся.",
                    action("Восстановить оригинал", SLATE, () -> run(this::rootRestore))));
        } catch (Exception e) {
            fail(e);
        }
    }

    private void rootRestore() {
        try {
            if (!Shell.hasRoot()) throw new IllegalStateException("Для восстановления нужен root.");
            File backup = backupFile();
            if (!backup.exists()) {
                say("Резервной копии нет.");
                say("Оригинал можно вернуть и без неё: переключите язык в настройках игры на другой "
                        + "и обратно на украинский — игра скачает текст заново.");
                return;
            }
            say("Закрываю игру…");
            Shell.run("am force-stop " + GAME);
            Shell.writeFile(Bridge.read(backup), GAME_TARGET, new File(getCacheDir(), "scratch.bin"));
            say("Оригинальный украинский текст восстановлен.");
            worker.execute(this::refresh);
        } catch (Exception e) {
            fail(e);
        }
    }

    private void fail(Exception e) {
        String message = e.getMessage() == null ? e.toString() : e.getMessage();
        say("");
        say("ОШИБКА: " + message);
        ui.post(() -> state(RED, "Не получилось", message,
                action("Проверить ещё раз", SLATE, () -> worker.execute(this::refresh))));
    }

    private String gameVersion() {
        try {
            PackageInfo info = getPackageManager().getPackageInfo(GAME, 0);
            return info.versionName;
        } catch (PackageManager.NameNotFoundException e) {
            return null;
        }
    }

    // ---------- small view helpers ----------

    private LinearLayout column() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(18), dp(22), dp(18), dp(28));
        page.setBackgroundColor(BG);
        return page;
    }

    private ScrollView scroll(View content) {
        ScrollView view = new ScrollView(this);
        view.setBackgroundColor(BG);
        // Android 15 draws apps edge to edge, so without this the title sits under the clock.
        view.setFitsSystemWindows(true);
        view.setClipToPadding(false);
        view.addView(content, new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        return view;
    }

    private TextView heading(String text) {
        TextView view = new TextView(this);
        view.setText(text);
        view.setTextColor(GOLD);
        view.setTextSize(TypedValue.COMPLEX_UNIT_SP, 22);
        return view;
    }

    private TextView caption(String text) {
        TextView view = new TextView(this);
        view.setText(text);
        view.setTextColor(MUTED);
        view.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
        view.setPadding(0, dp(4), 0, dp(16));
        return view;
    }

    private LinearLayout card(int accent) {
        LinearLayout view = new LinearLayout(this);
        view.setOrientation(LinearLayout.VERTICAL);
        view.setBackground(rounded(CARD, accent));
        view.setPadding(dp(16), dp(14), dp(16), dp(14));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = dp(12);
        view.setLayoutParams(lp);
        return view;
    }

    private TextView warning(String text) {
        TextView view = new TextView(this);
        view.setText(text);
        view.setTextColor(0xFFFFD9A0);
        view.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        view.setBackground(rounded(0xFF2A1E14, 0xFFB3382F));
        view.setPadding(dp(16), dp(14), dp(16), dp(14));
        view.setLineSpacing(dp(3), 1f);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.topMargin = dp(6);
        lp.bottomMargin = dp(14);
        view.setLayoutParams(lp);
        return view;
    }

    private GradientDrawable rounded(int fill, int border) {
        GradientDrawable shape = new GradientDrawable();
        shape.setColor(fill);
        shape.setCornerRadius(dp(12));
        shape.setStroke(dp(2), border);
        return shape;
    }

    private View button(String text, int color, Runnable onClick) {
        Button view = new Button(this);
        view.setText(text);
        view.setAllCaps(false);
        view.setTextColor(Color.WHITE);
        view.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16);
        view.setBackground(rounded(color, color));
        view.setGravity(Gravity.CENTER);
        view.setOnClickListener(v -> onClick.run());
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(54));
        lp.bottomMargin = dp(10);
        view.setLayoutParams(lp);
        return view;
    }

    private View flat(String text, Runnable onClick) {
        TextView view = new TextView(this);
        view.setText(text);
        view.setTextColor(0xFF9FB6DA);
        view.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
        view.setPadding(dp(2), dp(10), dp(2), dp(10));
        view.setOnClickListener(v -> onClick.run());
        return view;
    }

    private int dp(int value) {
        return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value,
                getResources().getDisplayMetrics()));
    }

    private void say(String line) {
        ui.post(() -> { if (log != null) log.append(line + "\n"); });
    }

    private void toast(String message) {
        ui.post(() -> Toast.makeText(this, message, Toast.LENGTH_LONG).show());
    }
}
