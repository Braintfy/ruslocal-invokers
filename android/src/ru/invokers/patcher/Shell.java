package ru.invokers.patcher;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * Privileged file access to the game's data directory.
 *
 * Since Android 11 an ordinary app cannot reach another package's Android/data at all: the Storage
 * Access Framework refuses that path and MANAGE_EXTERNAL_STORAGE does not cover it. Root is the only
 * way an app on the phone itself can get in, so everything here goes through su. Without it the app
 * can still compose the file and hand it to the user, but not install it.
 */
final class Shell {
    private static Boolean rootAvailable;

    static synchronized boolean hasRoot() {
        if (rootAvailable != null) return rootAvailable;
        rootAvailable = Boolean.FALSE;
        try {
            Process process = Runtime.getRuntime().exec(new String[]{"su", "-c", "id"});
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            copy(process.getInputStream(), out);
            process.waitFor();
            rootAvailable = process.exitValue() == 0 && out.toString("UTF-8").contains("uid=0");
        } catch (Exception ignored) {
            // No su binary, or the user denied the prompt: both mean the same thing here.
        }
        return rootAvailable;
    }

    static String run(String command) throws IOException, InterruptedException {
        Process process = Runtime.getRuntime().exec(new String[]{"su", "-c", command});
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        copy(process.getInputStream(), out);
        ByteArrayOutputStream err = new ByteArrayOutputStream();
        copy(process.getErrorStream(), err);
        int code = process.waitFor();
        if (code != 0) {
            throw new IOException("Команда завершилась с кодом " + code + ": " + err.toString("UTF-8").trim());
        }
        return out.toString("UTF-8");
    }

    /** Copies a file out of the protected directory into our own storage. */
    static byte[] readFile(String remotePath, File scratch) throws IOException, InterruptedException {
        run("cat '" + remotePath + "' > '" + scratch.getAbsolutePath() + "' && chmod 666 '"
                + scratch.getAbsolutePath() + "'");
        byte[] data = new byte[(int) scratch.length()];
        try (java.io.FileInputStream in = new java.io.FileInputStream(scratch)) {
            int read = 0;
            while (read < data.length) {
                int n = in.read(data, read, data.length - read);
                if (n < 0) break;
                read += n;
            }
        }
        return data;
    }

    /**
     * Writes through a staging copy and then restores the owner and mode of the file being replaced,
     * so the game keeps full control of its own file afterwards.
     */
    static void writeFile(byte[] content, String remotePath, File scratch) throws IOException, InterruptedException {
        try (FileOutputStream out = new FileOutputStream(scratch)) {
            out.write(content);
            out.getFD().sync();
        }
        String owner = run("stat -c '%U:%G' '" + remotePath + "'").trim();
        run("cat '" + scratch.getAbsolutePath() + "' > '" + remotePath + "'");
        if (!owner.isEmpty() && !owner.contains("?")) {
            run("chown " + owner + " '" + remotePath + "' || true");
        }
        run("chmod 660 '" + remotePath + "' || true");
    }

    static String sha256(String remotePath) throws IOException, InterruptedException {
        String output = run("sha256sum '" + remotePath + "'").trim();
        int space = output.indexOf(' ');
        return (space > 0 ? output.substring(0, space) : output).toUpperCase(java.util.Locale.ROOT);
    }

    static boolean exists(String remotePath) {
        try {
            return run("[ -f '" + remotePath + "' ] && echo yes || echo no").trim().equals("yes");
        } catch (Exception e) {
            return false;
        }
    }

    private static void copy(InputStream in, OutputStream out) throws IOException {
        byte[] buffer = new byte[8192];
        int n;
        while ((n = in.read(buffer)) > 0) out.write(buffer, 0, n);
    }
}
