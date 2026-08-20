package ru.invokers.patcher;

import android.content.Context;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;

/**
 * Composing the Russian file, and the hand-off with a computer.
 *
 * Without root the app cannot reach the game's own directory, while a computer running adb can. The
 * computer therefore copies the game's two language files into this app's external directory — the
 * one place adb may write and the app may read with no permission at all — the app composes the
 * Russian file there, and the computer copies the result back. The composition itself is the same
 * code the root path runs, so both routes produce identical bytes.
 */
final class Bridge {
    static final String ENGLISH = "dl_en_US.bin";
    static final String TARGET = "dl_uk_UA.bin";
    static final String CATALOG = "ru_RU.jsonl";
    static final String STATUS = "status.txt";

    /** Result of composing one file: the bytes to install and how many strings became Russian. */
    static final class Built {
        final byte[] bytes;
        final int applied;

        Built(byte[] bytes, int applied) {
            this.bytes = bytes;
            this.applied = applied;
        }
    }

    static File root(Context context) {
        File external = context.getExternalFilesDir(null);
        if (external == null) return null;
        File bridge = new File(external, "bridge");
        new File(bridge, "in").mkdirs();
        new File(bridge, "out").mkdirs();
        return bridge;
    }

    static File in(Context context, String name) {
        File bridge = root(context);
        return bridge == null ? null : new File(new File(bridge, "in"), name);
    }

    static File out(Context context, String name) {
        File bridge = root(context);
        return bridge == null ? null : new File(new File(bridge, "out"), name);
    }

    /** True when a computer has already put both language files where the app can read them. */
    static boolean inputReady(Context context) {
        File english = in(context, ENGLISH);
        File target = in(context, TARGET);
        return english != null && english.length() > 0 && target != null && target.length() > 0;
    }

    static Built compose(byte[] englishRaw, byte[] targetRaw, Overlay overlay) throws Exception {
        Loc1 english = Loc1.parse(englishRaw);
        Loc1 target = Loc1.parse(targetRaw);
        if (!english.contentGuid.equals(target.contentGuid)) {
            throw new IOException("Английский и украинский пакеты из разных версий контента. "
                    + "Запустите игру, дождитесь докачки и повторите.");
        }
        int applied = overlay.apply(english, target);
        return new Built(target.build(), applied);
    }

    static byte[] read(File file) throws IOException {
        byte[] data = new byte[(int) file.length()];
        try (FileInputStream in = new FileInputStream(file)) {
            int read = 0;
            while (read < data.length) {
                int n = in.read(data, read, data.length - read);
                if (n < 0) break;
                read += n;
            }
        }
        return data;
    }

    static void write(File file, byte[] data) throws IOException {
        File parent = file.getParentFile();
        if (parent != null) parent.mkdirs();
        try (FileOutputStream out = new FileOutputStream(file)) {
            out.write(data);
            out.getFD().sync();
        }
    }

    /**
     * The single line the computer polls for. It is written last and only after the file itself, so
     * seeing OK there means the result is complete on disk.
     */
    static void status(Context context, String line) {
        try {
            File file = out(context, STATUS);
            if (file != null) write(file, (line + "\n").getBytes(StandardCharsets.UTF_8));
        } catch (Exception ignored) {
            // The computer falls back to a timeout, so a missing marker is not worth failing over.
        }
    }

    static void clearStatus(Context context) {
        File file = out(context, STATUS);
        if (file != null) file.delete();
    }

    static Overlay catalog(Context context) throws Exception {
        File local = in(context, CATALOG);
        if (local != null && local.length() > 0) {
            try (FileInputStream stream = new FileInputStream(local)) {
                return Overlay.read(stream);
            }
        }
        return Overlay.read(new ByteArrayInputStream(Net.download(Net.CATALOG_URL)));
    }
}
